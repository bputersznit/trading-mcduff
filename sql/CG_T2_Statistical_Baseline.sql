-- =====================================================================
-- T2 ClanMarshal Statistical Baseline - ClickHouse October 2025
-- =====================================================================
-- Purpose: Extract statistical profile from validated CH backtest
--          to compare with NT8 playback runs (Mar-Apr 2026)
--
-- Method: Statistical parity, not exact signal matching
-- =====================================================================

-- =====================================================================
-- BASELINE METRICS - Overall Performance
-- =====================================================================

SELECT
    '=== T2 CLANMARSHAL BASELINE (Oct 2025 - ClickHouse) ===' as section,
    '' as metric,
    '' as value;

SELECT
    'Performance' as section,
    'Total Trades' as metric,
    toString(COUNT(*)) as value
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Performance', 'Total PnL', CONCAT('$', toString(ROUND(SUM(net_pnl_usd), 2)))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Performance', 'Expectancy', CONCAT('$', toString(ROUND(AVG(net_pnl_usd), 2)))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Performance', 'Win Rate', CONCAT(toString(ROUND(SUM(CASE WHEN net_pnl_usd > 0 THEN 1 ELSE 0 END) * 100.0 / COUNT(*), 2)), '%')
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Performance', 'Profit Factor', toString(ROUND(SUM(CASE WHEN net_pnl_usd > 0 THEN net_pnl_usd ELSE 0 END) / -SUM(CASE WHEN net_pnl_usd < 0 THEN net_pnl_usd ELSE 0 END), 3))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Performance', 'Max Drawdown', CONCAT('$', toString(ROUND(MIN(v5_drawdown_from_peak), 2)))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Performance', 'Avg Winner', CONCAT('$', toString(ROUND(AVG(net_pnl_usd), 2)))
FROM CG_mnq_hybrid_v5_clanmarshal
WHERE net_pnl_usd > 0
UNION ALL
SELECT 'Performance', 'Avg Loser', CONCAT('$', toString(ROUND(AVG(net_pnl_usd), 2)))
FROM CG_mnq_hybrid_v5_clanmarshal
WHERE net_pnl_usd < 0
UNION ALL
SELECT 'Performance', 'Trading Days', toString(COUNT(DISTINCT trade_date))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Performance', 'Avg Trades/Day', toString(ROUND(COUNT(*) * 1.0 / COUNT(DISTINCT trade_date), 1))
FROM CG_mnq_hybrid_v5_clanmarshal;


-- =====================================================================
-- TRADE DISTRIBUTION - Side and Execution
-- =====================================================================

SELECT
    '=== TRADE DISTRIBUTION ===' as section,
    '' as metric,
    '' as value;

-- Long vs Short
SELECT
    'Side Distribution' as section,
    side as metric,
    CONCAT(
        toString(COUNT(*)), ' trades (',
        toString(ROUND(COUNT(*) * 100.0 / (SELECT COUNT(*) FROM CG_mnq_hybrid_v5_clanmarshal), 1)),
        '%)'
    ) as value
FROM CG_mnq_hybrid_v5_clanmarshal
GROUP BY side
ORDER BY side;

-- Execution type
SELECT
    'Execution Type' as section,
    execution_type as metric,
    CONCAT(
        toString(COUNT(*)), ' (',
        toString(ROUND(COUNT(*) * 100.0 / (SELECT COUNT(*) FROM CG_mnq_hybrid_v5_clanmarshal), 1)),
        '%)'
    ) as value
FROM CG_mnq_hybrid_v5_clanmarshal
GROUP BY execution_type
ORDER BY execution_type;

-- Outcome distribution
SELECT
    'Outcome' as section,
    outcome as metric,
    CONCAT(
        toString(COUNT(*)), ' (',
        toString(ROUND(COUNT(*) * 100.0 / (SELECT COUNT(*) FROM CG_mnq_hybrid_v5_clanmarshal), 1)),
        '%)'
    ) as value
FROM CG_mnq_hybrid_v5_clanmarshal
GROUP BY outcome
ORDER BY COUNT(*) DESC;


-- =====================================================================
-- SESSION REGIME DISTRIBUTION
-- =====================================================================

SELECT
    '=== SESSION REGIME DISTRIBUTION ===' as section,
    '' as metric,
    '' as value;

SELECT
    'Regime' as section,
    time_zone as metric,
    CONCAT(
        toString(COUNT(*)), ' trades (',
        toString(ROUND(COUNT(*) * 100.0 / (SELECT COUNT(*) FROM CG_mnq_hybrid_v5_clanmarshal), 1)),
        '%) | Win Rate: ',
        toString(ROUND(SUM(CASE WHEN net_pnl_usd > 0 THEN 1 ELSE 0 END) * 100.0 / COUNT(*), 1)),
        '% | Avg PnL: $',
        toString(ROUND(AVG(net_pnl_usd), 2))
    ) as value
FROM CG_mnq_hybrid_v5_clanmarshal
GROUP BY time_zone
ORDER BY COUNT(*) DESC;


-- =====================================================================
-- SIGNAL FEATURE STATISTICS
-- =====================================================================

SELECT
    '=== SIGNAL FEATURE DISTRIBUTIONS ===' as section,
    '' as metric,
    '' as value;

-- total_event_size statistics
SELECT
    'Total Event Size' as section,
    'Min' as metric,
    toString(ROUND(MIN(total_event_size), 2)) as value
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Total Event Size', 'P25', toString(ROUND(quantile(0.25)(total_event_size), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Total Event Size', 'Median', toString(ROUND(quantile(0.50)(total_event_size), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Total Event Size', 'P75', toString(ROUND(quantile(0.75)(total_event_size), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Total Event Size', 'P90', toString(ROUND(quantile(0.90)(total_event_size), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Total Event Size', 'Max', toString(ROUND(MAX(total_event_size), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Total Event Size', 'Mean', toString(ROUND(AVG(total_event_size), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Total Event Size', 'StdDev', toString(ROUND(stddevPop(total_event_size), 2))
FROM CG_mnq_hybrid_v5_clanmarshal;

-- event_count_delta statistics
SELECT
    'Event Count Delta' as section,
    'Min' as metric,
    toString(ROUND(MIN(event_count_delta), 2)) as value
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Event Count Delta', 'P25', toString(ROUND(quantile(0.25)(event_count_delta), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Event Count Delta', 'Median', toString(ROUND(quantile(0.50)(event_count_delta), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Event Count Delta', 'P75', toString(ROUND(quantile(0.75)(event_count_delta), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Event Count Delta', 'P90', toString(ROUND(quantile(0.90)(event_count_delta), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Event Count Delta', 'Max', toString(ROUND(MAX(event_count_delta), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Event Count Delta', 'Mean', toString(ROUND(AVG(event_count_delta), 2))
FROM CG_mnq_hybrid_v5_clanmarshal
UNION ALL
SELECT 'Event Count Delta', 'StdDev', toString(ROUND(stddevPop(event_count_delta), 2))
FROM CG_mnq_hybrid_v5_clanmarshal;


-- =====================================================================
-- DAILY STATISTICS
-- =====================================================================

SELECT
    '=== DAILY STATISTICS ===' as section,
    '' as metric,
    '' as value;

WITH daily_stats AS (
    SELECT
        trade_date,
        COUNT(*) as trades,
        SUM(net_pnl_usd) as daily_pnl,
        SUM(CASE WHEN net_pnl_usd > 0 THEN 1 ELSE 0 END) * 100.0 / COUNT(*) as win_rate
    FROM CG_mnq_hybrid_v5_clanmarshal
    GROUP BY trade_date
)
SELECT
    'Trades Per Day' as section,
    'Min' as metric,
    toString(MIN(trades)) as value
FROM daily_stats
UNION ALL
SELECT 'Trades Per Day', 'Median', toString(ROUND(quantile(0.50)(trades), 1))
FROM daily_stats
UNION ALL
SELECT 'Trades Per Day', 'Max', toString(MAX(trades))
FROM daily_stats
UNION ALL
SELECT 'Trades Per Day', 'Mean', toString(ROUND(AVG(trades), 1))
FROM daily_stats
UNION ALL
SELECT 'Daily PnL', 'Best Day', CONCAT('$', toString(ROUND(MAX(daily_pnl), 2)))
FROM daily_stats
UNION ALL
SELECT 'Daily PnL', 'Worst Day', CONCAT('$', toString(ROUND(MIN(daily_pnl), 2)))
FROM daily_stats
UNION ALL
SELECT 'Daily PnL', 'Median', CONCAT('$', toString(ROUND(quantile(0.50)(daily_pnl), 2)))
FROM daily_stats
UNION ALL
SELECT 'Daily Win Rate', 'Min', CONCAT(toString(ROUND(MIN(win_rate), 1)), '%')
FROM daily_stats
UNION ALL
SELECT 'Daily Win Rate', 'Median', CONCAT(toString(ROUND(quantile(0.50)(win_rate), 1)), '%')
FROM daily_stats
UNION ALL
SELECT 'Daily Win Rate', 'Max', CONCAT(toString(ROUND(MAX(win_rate), 1)), '%')
FROM daily_stats;


-- =====================================================================
-- HOURLY DISTRIBUTION
-- =====================================================================

SELECT
    '=== HOURLY DISTRIBUTION (ET) ===' as section,
    '' as metric,
    '' as value;

SELECT
    'Hour of Day' as section,
    CONCAT(toString(toHour(entry_et)), ':00') as metric,
    CONCAT(
        toString(COUNT(*)), ' trades (',
        toString(ROUND(COUNT(*) * 100.0 / (SELECT COUNT(*) FROM CG_mnq_hybrid_v5_clanmarshal), 1)),
        '%)'
    ) as value
FROM CG_mnq_hybrid_v5_clanmarshal
GROUP BY toHour(entry_et)
ORDER BY toHour(entry_et);


-- =====================================================================
-- EXPORT BASELINE TO CSV
-- =====================================================================

-- Detailed trade data for distribution analysis
SELECT
    trade_date,
    toString(entry_time) as entry_time_utc,
    side,
    execution_type,
    total_event_size,
    event_count_delta,
    slippage_ticks_rt,
    time_zone as regime,
    outcome,
    net_pnl_usd,
    toHour(entry_et) as hour_et
FROM CG_mnq_hybrid_v5_clanmarshal
ORDER BY entry_time
INTO OUTFILE '/tmp/ch_baseline_oct2025.csv'
FORMAT CSVWithNames;

SELECT 'Exported baseline to: /tmp/ch_baseline_oct2025.csv' as status;
