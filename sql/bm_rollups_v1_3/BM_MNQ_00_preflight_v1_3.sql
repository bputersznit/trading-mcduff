-- BM_MNQ_00_preflight_v1_3.sql
-- Generated: 2026-05-10 13:18:00 America/New_York
--
-- Purpose:
--   Validate that the Bookmap emulation 100ms base tables exist before running
--   the multi-scale rollup build.
--
-- ClickHouse 26.3 compatibility:
--   All aggregate QA calculations use inner CTEs with agg_* aliases and outer
--   projections. This avoids ILLEGAL_AGGREGATION alias-resolution traps.
--
-- Run:
--   clickhouse-client --multiquery < BM_MNQ_00_preflight_v1_3.sql

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

WITH agg AS
(
    SELECT
        count() AS agg_rows,
        min(trade_date) AS agg_min_trade_date,
        max(trade_date) AS agg_max_trade_date,
        min(ts_et) AS agg_min_ts_et,
        max(ts_et) AS agg_max_ts_et,
        sum(total_exec_size) AS agg_total_exec_size,
        sum(buy_exec_size) AS agg_buy_exec_size,
        sum(sell_exec_size) AS agg_sell_exec_size
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_100MS
)
SELECT
    'BASE_AGGRESSION_100MS' AS check_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    if
    (
        agg_total_exec_size = 0,
        0,
        (agg_buy_exec_size - agg_sell_exec_size) / agg_total_exec_size
    ) AS net_imbalance
FROM agg;

WITH agg AS
(
    SELECT
        count() AS agg_rows,
        min(trade_date) AS agg_min_trade_date,
        max(trade_date) AS agg_max_trade_date,
        min(ts_et) AS agg_min_ts_et,
        max(ts_et) AS agg_max_ts_et,
        sum(total_liquidity_event_size) AS agg_total_liquidity_event_size,
        max(heatmap_proxy_value) AS agg_max_heatmap_proxy_value
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS
)
SELECT
    'BASE_HEATMAP_100MS' AS check_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_max_heatmap_proxy_value AS max_heatmap_proxy_value
FROM agg;

WITH agg AS
(
    SELECT
        count() AS agg_rows,
        min(trade_date) AS agg_min_trade_date,
        max(trade_date) AS agg_max_trade_date,
        max(daily_max_heatmap_proxy_value) AS agg_max_daily_max_heatmap_proxy_value,
        max(daily_p999_heatmap_proxy_value) AS agg_max_daily_p999_heatmap_proxy_value
    FROM BM_MNQ_HEATMAP_DAILY_MAX_RTH
)
SELECT
    'BASE_DAILY_MAX_RTH' AS check_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_max_daily_max_heatmap_proxy_value AS max_daily_max_heatmap_proxy_value,
    agg_max_daily_p999_heatmap_proxy_value AS max_daily_p999_heatmap_proxy_value
FROM agg;
