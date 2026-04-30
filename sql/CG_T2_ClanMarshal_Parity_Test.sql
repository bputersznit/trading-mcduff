-- =====================================================================
-- T2 ClanMarshal Parity Test - Pure ClickHouse SQL
-- =====================================================================
-- Purpose: Validate T2 signal logic by regenerating signals from features
--          and comparing with existing backtest results
--
-- Expected: 908 trades, $71,429.40 PnL, 64.43% win rate, 2.93 PF
-- Source: CG_mnq_hybrid_v5_clanmarshal (Sep 24 - Oct 22, 2025)
-- =====================================================================

-- =====================================================================
-- STEP 1: Inspect existing backtest results
-- =====================================================================

-- Summary metrics (should match restart doc v8.2)
SELECT
    COUNT(*) as trades,
    ROUND(SUM(net_pnl_usd), 2) as total_pnl_usd,
    ROUND(AVG(net_pnl_usd), 2) as expectancy,
    ROUND(SUM(CASE WHEN net_pnl_usd > 0 THEN net_pnl_usd ELSE 0 END) /
          -SUM(CASE WHEN net_pnl_usd < 0 THEN net_pnl_usd ELSE 0 END), 3) as profit_factor,
    ROUND(SUM(CASE WHEN net_pnl_usd > 0 THEN 1 ELSE 0 END) * 100.0 / COUNT(*), 2) as win_rate_pct,
    ROUND(MIN(v5_drawdown_from_peak), 2) as max_dd_usd,
    MIN(trade_date) as first_date,
    MAX(trade_date) as last_date,
    COUNT(DISTINCT trade_date) as trading_days
FROM CG_mnq_hybrid_v5_clanmarshal;

-- Distribution by side and execution type
SELECT
    side,
    execution_type,
    COUNT(*) as trades,
    ROUND(SUM(net_pnl_usd), 2) as pnl,
    ROUND(AVG(net_pnl_usd), 2) as avg_pnl
FROM CG_mnq_hybrid_v5_clanmarshal
GROUP BY side, execution_type
ORDER BY side, execution_type;

-- Distribution by session regime
SELECT
    time_zone as regime,
    COUNT(*) as trades,
    ROUND(SUM(net_pnl_usd), 2) as pnl,
    ROUND(AVG(net_pnl_usd), 2) as avg_pnl,
    ROUND(SUM(CASE WHEN net_pnl_usd > 0 THEN 1 ELSE 0 END) * 100.0 / COUNT(*), 2) as win_rate_pct
FROM CG_mnq_hybrid_v5_clanmarshal
GROUP BY regime
ORDER BY trades DESC;


-- =====================================================================
-- STEP 2: Check available feature data
-- =====================================================================

-- Verify features table exists and has data for backtest period
SELECT
    MIN(ts_bucket) as first_timestamp,
    MAX(ts_bucket) as last_timestamp,
    COUNT(*) as total_rows,
    COUNT(DISTINCT toDate(ts_bucket)) as days
FROM CG_mnq_features_100ms_clean_v2
WHERE toDate(ts_bucket) BETWEEN '2025-09-24' AND '2025-10-22';

-- Sample feature data
SELECT *
FROM CG_mnq_features_100ms_clean_v2
WHERE toDate(ts_bucket) = '2025-09-24'
ORDER BY ts_bucket
LIMIT 10;


-- =====================================================================
-- STEP 3: Recreate T2 signals from features
-- =====================================================================

-- Drop existing test table if it exists
DROP TABLE IF EXISTS CG_T2_signals_parity_test;

-- Create signals table with T2 logic
CREATE TABLE CG_T2_signals_parity_test
ENGINE = MergeTree
ORDER BY (trade_date, signal_time)
AS
SELECT
    toDate(ts_bucket) as trade_date,
    ts_bucket as signal_time,

    -- Determine side based on event_count_delta and short_momentum
    multiIf(
        event_count_delta > 0 AND short_momentum > 0, 'LONG',
        event_count_delta < 0 AND short_momentum < 0, 'SHORT',
        'NEUTRAL'
    ) as side,

    -- Signal features (matching NT8 CG_T2_ClanMarshal_LiveSignal_v1_1.cs)
    total_event_size,
    event_count_delta,
    short_momentum,
    spread,

    -- Session regime (matching NT8 GetSessionRegime logic)
    multiIf(
        toHour(ts_bucket) < 10, 'OPENING_DRIVE',
        toHour(ts_bucket) < 11 OR (toHour(ts_bucket) = 11 AND toMinute(ts_bucket) < 30), 'MID_MORNING',
        toHour(ts_bucket) < 13 OR (toHour(ts_bucket) = 13 AND toMinute(ts_bucket) < 30), 'LUNCH',
        toHour(ts_bucket) < 15 OR (toHour(ts_bucket) = 15 AND toMinute(ts_bucket) < 30), 'POWER_HOUR',
        'CLOSE'
    ) as regime,

    -- RTH filter (9:30 AM - 3:59 PM ET)
    toHour(ts_bucket) >= 9 AND toHour(ts_bucket) < 16 as is_rth,

    -- Signal strength metrics
    abs(event_count_delta) as abs_delta,
    abs(short_momentum) as abs_momentum,

    best_bid,
    best_ask,
    (best_bid + best_ask) / 2.0 as mid_price

FROM CG_mnq_features_100ms_clean_v2

WHERE
    -- Date range matching backtest
    toDate(ts_bucket) BETWEEN '2025-09-24' AND '2025-10-22'

    -- RTH only (9:30 AM - 3:59 PM ET)
    AND toHour(ts_bucket) >= 9
    AND toHour(ts_bucket) < 16

    -- T2 signal thresholds (from NT8 defaults)
    AND total_event_size >= 5.0           -- MinWallScoreProxy
    AND abs(event_count_delta) >= 10.0    -- MinDeltaAbs
    AND abs(short_momentum) >= 1.0        -- MomentumConfirmTicks
    AND spread <= 8.0                      -- MaxSpreadTicks

    -- Directional confirmation (long or short, not neutral)
    AND (
        (event_count_delta > 0 AND short_momentum > 0)  -- LONG signal
        OR (event_count_delta < 0 AND short_momentum < 0)  -- SHORT signal
    );

-- Check signal count
SELECT COUNT(*) as generated_signals FROM CG_T2_signals_parity_test;


-- =====================================================================
-- STEP 4: Compare with existing backtest results
-- =====================================================================

-- Match rate - how many CH backtest trades have matching signals?
SELECT
    COUNT(DISTINCT c.entry_time) as ch_trades,
    COUNT(DISTINCT t.signal_time) as matched_signals,
    ROUND(COUNT(DISTINCT t.signal_time) * 100.0 / COUNT(DISTINCT c.entry_time), 2) as match_pct
FROM CG_mnq_hybrid_v5_clanmarshal c
LEFT JOIN CG_T2_signals_parity_test t
    ON toDateTime64(c.entry_time, 3, 'UTC') = t.signal_time
    AND c.side = t.side;

-- Find CH trades with NO matching signal (missing signals)
SELECT
    c.trade_date,
    c.entry_time,
    c.side,
    c.total_event_size as ch_event_size,
    c.event_count_delta as ch_event_delta,
    c.net_pnl_usd,
    c.execution_type,
    c.time_zone as regime
FROM CG_mnq_hybrid_v5_clanmarshal c
LEFT JOIN CG_T2_signals_parity_test t
    ON toDateTime64(c.entry_time, 3, 'UTC') = t.signal_time
    AND c.side = t.side
WHERE t.signal_time IS NULL
ORDER BY c.entry_time
LIMIT 50;

-- Find generated signals with NO matching CH trade (extra signals)
SELECT
    t.trade_date,
    t.signal_time,
    t.side,
    t.total_event_size,
    t.event_count_delta,
    t.short_momentum,
    t.regime
FROM CG_T2_signals_parity_test t
LEFT JOIN CG_mnq_hybrid_v5_clanmarshal c
    ON t.signal_time = toDateTime64(c.entry_time, 3, 'UTC')
    AND t.side = c.side
WHERE c.entry_time IS NULL
ORDER BY t.signal_time
LIMIT 50;


-- =====================================================================
-- STEP 5: Analyze feature distributions
-- =====================================================================

-- Distribution of signal features (for matched trades)
SELECT
    side,
    ROUND(quantile(0.25)(total_event_size), 2) as event_size_p25,
    ROUND(quantile(0.50)(total_event_size), 2) as event_size_p50,
    ROUND(quantile(0.75)(total_event_size), 2) as event_size_p75,
    ROUND(quantile(0.25)(abs_delta), 2) as delta_p25,
    ROUND(quantile(0.50)(abs_delta), 2) as delta_p50,
    ROUND(quantile(0.75)(abs_delta), 2) as delta_p75,
    ROUND(quantile(0.25)(abs_momentum), 2) as momentum_p25,
    ROUND(quantile(0.50)(abs_momentum), 2) as momentum_p50,
    ROUND(quantile(0.75)(abs_momentum), 2) as momentum_p75
FROM CG_T2_signals_parity_test
GROUP BY side;

-- Signal count by day
SELECT
    trade_date,
    COUNT(*) as signals,
    SUM(CASE WHEN side = 'LONG' THEN 1 ELSE 0 END) as long_signals,
    SUM(CASE WHEN side = 'SHORT' THEN 1 ELSE 0 END) as short_signals
FROM CG_T2_signals_parity_test
GROUP BY trade_date
ORDER BY trade_date;


-- =====================================================================
-- STEP 6: Parameter sensitivity analysis
-- =====================================================================

-- Test different threshold combinations
-- This shows how signal count changes with parameter variations

WITH param_sweep AS (
    SELECT
        wall_threshold,
        delta_threshold,
        momentum_threshold,
        COUNT(*) as signal_count
    FROM (
        SELECT
            arrayJoin([3.0, 5.0, 7.0, 10.0]) as wall_threshold
    ) CROSS JOIN (
        SELECT arrayJoin([5.0, 10.0, 15.0, 20.0]) as delta_threshold
    ) CROSS JOIN (
        SELECT arrayJoin([0.5, 1.0, 1.5, 2.0]) as momentum_threshold
    ) CROSS JOIN (
        SELECT *
        FROM CG_mnq_features_100ms_clean_v2
        WHERE toDate(ts_bucket) BETWEEN '2025-09-24' AND '2025-10-22'
    ) f
    WHERE
        f.total_event_size >= wall_threshold
        AND abs(f.event_count_delta) >= delta_threshold
        AND abs(f.short_momentum) >= momentum_threshold
        AND toHour(f.ts_bucket) >= 9
        AND toHour(f.ts_bucket) < 16
    GROUP BY wall_threshold, delta_threshold, momentum_threshold
)
SELECT
    wall_threshold,
    delta_threshold,
    momentum_threshold,
    signal_count,
    ROUND(signal_count * 100.0 / 908, 2) as pct_of_baseline
FROM param_sweep
ORDER BY signal_count DESC;


-- =====================================================================
-- STEP 7: Export signals for NT8 comparison
-- =====================================================================

-- Export all signals to CSV for NT8 telemetry comparison
-- Run this to create a file for comparing with NT8 output

SELECT
    trade_date,
    toString(signal_time) as signal_time_utc,
    side,
    total_event_size,
    event_count_delta,
    short_momentum,
    spread,
    regime,
    ROUND(mid_price, 2) as entry_price_estimate
FROM CG_T2_signals_parity_test
ORDER BY signal_time
INTO OUTFILE '/tmp/CG_T2_signals_for_nt8_comparison.csv'
FORMAT CSVWithNames;

-- Also export the existing backtest trades for reference
SELECT
    trade_date,
    toString(entry_time) as entry_time_utc,
    toString(entry_et) as entry_time_et,
    side,
    entry_price,
    total_event_size,
    event_count_delta,
    execution_type,
    CASE
        WHEN limit_fill_time IS NOT NULL
        THEN dateDiff('millisecond', entry_time, limit_fill_time) / 1000.0
        ELSE NULL
    END as limit_wait_seconds,
    time_zone as regime,
    outcome,
    net_pnl_usd
FROM CG_mnq_hybrid_v5_clanmarshal
ORDER BY entry_time
INTO OUTFILE '/tmp/CG_T2_backtest_trades_reference.csv'
FORMAT CSVWithNames;


-- =====================================================================
-- STEP 8: Summary report
-- =====================================================================

SELECT
    '=== T2 CLANMARSHAL PARITY TEST SUMMARY ===' as report;

SELECT
    'Baseline (from CG_mnq_hybrid_v5_clanmarshal)' as metric,
    CONCAT(toString(COUNT(*)), ' trades') as value
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT
    'Generated signals (from features)',
    CONCAT(toString(COUNT(*)), ' signals')
FROM CG_T2_signals_parity_test
UNION ALL
SELECT
    'Match rate',
    CONCAT(toString(ROUND(COUNT(DISTINCT t.signal_time) * 100.0 / 908, 2)), '%')
FROM CG_mnq_hybrid_v5_clanmarshal c
LEFT JOIN CG_T2_signals_parity_test t
    ON toDateTime64(c.entry_time, 3, 'UTC') = t.signal_time
    AND c.side = t.side
UNION ALL
SELECT
    'Missing signals (in CH, not generated)',
    toString(COUNT(*))
FROM CG_mnq_hybrid_v5_clanmarshal c
LEFT JOIN CG_T2_signals_parity_test t
    ON toDateTime64(c.entry_time, 3, 'UTC') = t.signal_time
    AND c.side = t.side
WHERE t.signal_time IS NULL
UNION ALL
SELECT
    'Extra signals (generated, not in CH)',
    toString(COUNT(*))
FROM CG_T2_signals_parity_test t
LEFT JOIN CG_mnq_hybrid_v5_clanmarshal c
    ON t.signal_time = toDateTime64(c.entry_time, 3, 'UTC')
    AND t.side = c.side
WHERE c.entry_time IS NULL;

-- =====================================================================
-- NOTES:
-- =====================================================================
--
-- 1. If match rate is < 95%, investigate:
--    - Timing precision (100ms bucket vs exact entry_time)
--    - Session regime filter differences
--    - Threshold calibration
--
-- 2. Missing signals may indicate:
--    - Additional filters in original backtest not documented
--    - Opening range filter not implemented here
--    - Loss governor state not replicated
--
-- 3. Extra signals may indicate:
--    - Cooldown period not enforced
--    - Single-position enforcement not modeled
--    - Daily governors not applied
--
-- 4. For NT8 comparison:
--    - Load /tmp/CG_T2_signals_for_nt8_comparison.csv
--    - Load NT8 telemetry CSV
--    - Compare signal_time and side columns
--    - Calculate match rate
--
-- =====================================================================
