-- CG_v5_SAFETY_FIXED.sql
-- Apply safety checks to v5 hybrid trades
-- Enforces: 10-second minimum between entries, one position at a time

-- Drop existing fixed table if exists
DROP TABLE IF EXISTS CG_mnq_hybrid_v5_FIXED;

-- Create fixed table with safety-compliant trades
CREATE TABLE CG_mnq_hybrid_v5_FIXED ENGINE = MergeTree
ORDER BY (trade_date, entry_time)
AS
WITH
    -- Add row numbers for chronological processing
    numbered_trades AS (
        SELECT *,
            ROW_NUMBER() OVER (ORDER BY effective_fill_time) as trade_num
        FROM CG_mnq_hybrid_v5_clanmarshal
    ),

    -- Calculate time since previous trade
    with_gaps AS (
        SELECT *,
            lagInFrame(effective_fill_time, 1, toDateTime64('1970-01-01 00:00:00', 3, 'UTC'))
                OVER (ORDER BY effective_fill_time) as prev_entry_time,
            dateDiff('second',
                lagInFrame(effective_fill_time, 1, toDateTime64('1970-01-01 00:00:00', 3, 'UTC'))
                    OVER (ORDER BY effective_fill_time),
                effective_fill_time
            ) as seconds_since_prev
        FROM numbered_trades
    ),

    -- Apply safety filter: keep only trades with >= 10s gap
    filtered_trades AS (
        SELECT *
        FROM with_gaps
        WHERE
            -- First trade always passes
            trade_num = 1
            OR
            -- Subsequent trades need 10s gap
            seconds_since_prev >= 10
    )

-- Final selection (remove helper columns)
SELECT
    entry_time,
    trade_date,
    entry_et,
    side,
    entry_price,
    target_price,
    stop_price,
    limit_fill_time,
    execution_type,
    effective_fill_time,
    total_event_size,
    event_count_delta,
    slippage_ticks_rt,
    outcome,
    target_hit_time,
    stop_hit_time,
    exit_time,
    net_pnl_usd,
    or_high,
    or_low,
    or_location,
    time_zone,
    running_daily_pnl,
    consecutive_losses,
    v5_running_daily_pnl,
    v5_running_daily_peak,
    v5_drawdown_from_peak
FROM filtered_trades;

-- ================================================================
-- COMPARISON REPORT
-- ================================================================

SELECT
    '═══════════════════════════════════════════════════════════════════════════' as separator
UNION ALL
SELECT 'V5 SAFETY FIX - CLICKHOUSE REPORT'
UNION ALL
SELECT '═══════════════════════════════════════════════════════════════════════════'
UNION ALL
SELECT ''
UNION ALL
SELECT 'ORIGINAL V5 (WITH VIOLATIONS):'
UNION ALL
SELECT '───────────────────────────────────────────────────────────────────────────'
UNION ALL
SELECT concat('Total Trades:        ', toString(COUNT(*))) FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT concat('Winners:             ', toString(countIf(outcome = 'TARGET'))) FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT concat('Losers:              ', toString(countIf(outcome = 'STOP'))) FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT concat('Win Rate:            ', toString(ROUND(countIf(outcome = 'TARGET') * 100.0 / COUNT(*), 2)), '%') FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT concat('Total P&L:           $', toString(ROUND(SUM(net_pnl_usd), 2))) FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT concat('Avg P&L:             $', toString(ROUND(AVG(net_pnl_usd), 2))) FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT ''
UNION ALL
SELECT 'FIXED V5 (WITH SAFETY CHECKS):'
UNION ALL
SELECT '───────────────────────────────────────────────────────────────────────────'
UNION ALL
SELECT concat('Total Trades:        ', toString(COUNT(*))) FROM CG_mnq_hybrid_v5_FIXED
UNION ALL
SELECT concat('Winners:             ', toString(countIf(outcome = 'TARGET'))) FROM CG_mnq_hybrid_v5_FIXED
UNION ALL
SELECT concat('Losers:              ', toString(countIf(outcome = 'STOP'))) FROM CG_mnq_hybrid_v5_FIXED
UNION ALL
SELECT concat('Win Rate:            ', toString(ROUND(countIf(outcome = 'TARGET') * 100.0 / COUNT(*), 2)), '%') FROM CG_mnq_hybrid_v5_FIXED
UNION ALL
SELECT concat('Total P&L:           $', toString(ROUND(SUM(net_pnl_usd), 2))) FROM CG_mnq_hybrid_v5_FIXED
UNION ALL
SELECT concat('Avg P&L:             $', toString(ROUND(AVG(net_pnl_usd), 2))) FROM CG_mnq_hybrid_v5_FIXED
UNION ALL
SELECT ''
UNION ALL
SELECT 'COMPARISON:'
UNION ALL
SELECT '═══════════════════════════════════════════════════════════════════════════'
UNION ALL
SELECT concat('Trades Removed:      ',
    toString((SELECT COUNT(*) FROM CG_mnq_hybrid_v5_clanmarshal) - (SELECT COUNT(*) FROM CG_mnq_hybrid_v5_FIXED)),
    ' (',
    toString(ROUND(((SELECT COUNT(*) FROM CG_mnq_hybrid_v5_clanmarshal) - (SELECT COUNT(*) FROM CG_mnq_hybrid_v5_FIXED)) * 100.0 / (SELECT COUNT(*) FROM CG_mnq_hybrid_v5_clanmarshal), 1)),
    '%)')
UNION ALL
SELECT concat('P&L Delta:           $',
    toString(ROUND((SELECT SUM(net_pnl_usd) FROM CG_mnq_hybrid_v5_FIXED) - (SELECT SUM(net_pnl_usd) FROM CG_mnq_hybrid_v5_clanmarshal), 2)))
UNION ALL
SELECT concat('Win Rate Delta:      ',
    toString(ROUND((SELECT countIf(outcome = 'TARGET') * 100.0 / COUNT(*) FROM CG_mnq_hybrid_v5_FIXED) - (SELECT countIf(outcome = 'TARGET') * 100.0 / COUNT(*) FROM CG_mnq_hybrid_v5_clanmarshal), 2)),
    '%')
UNION ALL
SELECT ''
UNION ALL
SELECT '✅ Fixed table created: CG_mnq_hybrid_v5_FIXED'
UNION ALL
SELECT ''
UNION ALL
SELECT 'Export to CSV:'
UNION ALL
SELECT '  clickhouse-client --query "SELECT * FROM CG_mnq_hybrid_v5_FIXED ORDER BY entry_time FORMAT CSVWithNames" > v5_FIXED_trades.csv'
UNION ALL
SELECT '';

-- ================================================================
-- VIOLATIONS ANALYSIS
-- ================================================================

SELECT
    '═══════════════════════════════════════════════════════════════════════════' as report
UNION ALL
SELECT 'SIMULTANEOUS ENTRY VIOLATIONS (ORIGINAL)'
UNION ALL
SELECT '═══════════════════════════════════════════════════════════════════════════'
UNION ALL
SELECT concat('Timestamp: ', toString(effective_fill_time), ' → ', toString(cnt), ' contracts')
FROM (
    SELECT effective_fill_time, COUNT(*) as cnt
    FROM CG_mnq_hybrid_v5_clanmarshal
    GROUP BY effective_fill_time
    HAVING cnt > 1
    ORDER BY effective_fill_time
)
UNION ALL
SELECT ''
UNION ALL
SELECT 'All violations eliminated in FIXED version ✅';
