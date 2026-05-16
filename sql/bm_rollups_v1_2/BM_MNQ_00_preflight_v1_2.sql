-- BM_MNQ_00_preflight_v1_2.sql
-- Generated: 2026-05-10 13:05:00 America/New_York
--
-- Purpose:
--   Validate that the Bookmap emulation 100ms base tables exist before running
--   the multi-scale rollup build.
--
-- Run:
--   clickhouse-client --multiquery < BM_MNQ_00_preflight_v1_2.sql

SELECT
    'SOURCE_TABLE_CHECK' AS check_name,
    name,
    engine,
    total_rows,
    total_bytes
FROM system.tables
WHERE database = currentDatabase()
  AND name IN
  (
      'BM_MNQ_AGGRESSION_EXECUTIONS_100MS',
      'BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS',
      'BM_MNQ_HEATMAP_DAILY_MAX_RTH'
  )
ORDER BY name;

SELECT
    'MISSING_REQUIRED_SOURCE_TABLES' AS check_name,
    required_name
FROM
(
    SELECT arrayJoin
    (
        [
            'BM_MNQ_AGGRESSION_EXECUTIONS_100MS',
            'BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS',
            'BM_MNQ_HEATMAP_DAILY_MAX_RTH'
        ]
    ) AS required_name
) AS req
LEFT JOIN
(
    SELECT name
    FROM system.tables
    WHERE database = currentDatabase()
) AS tbl
ON req.required_name = tbl.name
WHERE tbl.name = '';

SELECT
    'BASE_AGGRESSION_100MS' AS check_name,
    count() AS rows,
    min(trade_date) AS min_trade_date,
    max(trade_date) AS max_trade_date,
    min(ts_et) AS min_ts_et,
    max(ts_et) AS max_ts_et,
    sum(total_exec_size) AS total_exec_size,
    sum(buy_exec_size) AS buy_exec_size,
    sum(sell_exec_size) AS sell_exec_size,
    if(sum(total_exec_size) = 0, 0, (sum(buy_exec_size) - sum(sell_exec_size)) / sum(total_exec_size)) AS net_imbalance
FROM BM_MNQ_AGGRESSION_EXECUTIONS_100MS;

SELECT
    'BASE_HEATMAP_100MS' AS check_name,
    count() AS rows,
    min(trade_date) AS min_trade_date,
    max(trade_date) AS max_trade_date,
    min(ts_et) AS min_ts_et,
    max(ts_et) AS max_ts_et,
    sum(total_liquidity_event_size) AS total_liquidity_event_size,
    max(heatmap_proxy_value) AS max_heatmap_proxy_value
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS;

SELECT
    'BASE_DAILY_MAX_RTH' AS check_name,
    count() AS rows,
    min(trade_date) AS min_trade_date,
    max(trade_date) AS max_trade_date,
    max(daily_max_heatmap_proxy_value) AS max_daily_max_heatmap_proxy_value,
    max(daily_p999_heatmap_proxy_value) AS max_daily_p999_heatmap_proxy_value
FROM BM_MNQ_HEATMAP_DAILY_MAX_RTH;
