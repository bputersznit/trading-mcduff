-- BM_MNQ_02_postrun_qa_v1_3.sql
-- Generated: 2026-05-10 13:18:00 America/New_York
--
-- Purpose:
--   QA the BM_MNQ multi-scale rollup ladder after BM_MNQ_01_rollup_scales_v1_3.sql.
--
-- ClickHouse 26.3 compatibility:
--   QA aggregates use inner CTEs with agg_* aliases and outer projections to
--   avoid ILLEGAL_AGGREGATION alias-resolution traps.
--
-- Run:
--   clickhouse-client --multiquery < BM_MNQ_02_postrun_qa_v1_3.sql

DROP TABLE IF EXISTS BM_MNQ_QA_SCALE_SUMMARY;

CREATE TABLE BM_MNQ_QA_SCALE_SUMMARY
ENGINE = MergeTree
ORDER BY (domain, scale_order, scale, table_name)
AS
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
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_1S
)
SELECT
    'AGGRESSION' AS domain,
    1 AS scale_order,
    '1S' AS scale,
    'BM_MNQ_AGGRESSION_EXECUTIONS_1S' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    toFloat64(0) AS total_liquidity_event_size,
    toFloat64(0) AS max_heatmap_intensity,
    toUInt64(0) AS bid_wall_rows,
    toUInt64(0) AS ask_wall_rows
FROM agg
UNION ALL
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
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_5S
)
SELECT
    'AGGRESSION' AS domain,
    2 AS scale_order,
    '5S' AS scale,
    'BM_MNQ_AGGRESSION_EXECUTIONS_5S' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    toFloat64(0) AS total_liquidity_event_size,
    toFloat64(0) AS max_heatmap_intensity,
    toUInt64(0) AS bid_wall_rows,
    toUInt64(0) AS ask_wall_rows
FROM agg
UNION ALL
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
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_30S
)
SELECT
    'AGGRESSION' AS domain,
    3 AS scale_order,
    '30S' AS scale,
    'BM_MNQ_AGGRESSION_EXECUTIONS_30S' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    toFloat64(0) AS total_liquidity_event_size,
    toFloat64(0) AS max_heatmap_intensity,
    toUInt64(0) AS bid_wall_rows,
    toUInt64(0) AS ask_wall_rows
FROM agg
UNION ALL
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
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_1M
)
SELECT
    'AGGRESSION' AS domain,
    4 AS scale_order,
    '1M' AS scale,
    'BM_MNQ_AGGRESSION_EXECUTIONS_1M' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    toFloat64(0) AS total_liquidity_event_size,
    toFloat64(0) AS max_heatmap_intensity,
    toUInt64(0) AS bid_wall_rows,
    toUInt64(0) AS ask_wall_rows
FROM agg
UNION ALL
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
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_5M
)
SELECT
    'AGGRESSION' AS domain,
    5 AS scale_order,
    '5M' AS scale,
    'BM_MNQ_AGGRESSION_EXECUTIONS_5M' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    toFloat64(0) AS total_liquidity_event_size,
    toFloat64(0) AS max_heatmap_intensity,
    toUInt64(0) AS bid_wall_rows,
    toUInt64(0) AS ask_wall_rows
FROM agg
UNION ALL
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
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
)
SELECT
    'HEATMAP' AS domain,
    1 AS scale_order,
    '1S' AS scale,
    'BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    toFloat64(0) AS total_exec_size,
    toFloat64(0) AS buy_exec_size,
    toFloat64(0) AS sell_exec_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_max_heatmap_proxy_value AS max_heatmap_intensity,
    toUInt64(0) AS bid_wall_rows,
    toUInt64(0) AS ask_wall_rows
FROM agg
UNION ALL
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
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
)
SELECT
    'HEATMAP' AS domain,
    2 AS scale_order,
    '5S' AS scale,
    'BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    toFloat64(0) AS total_exec_size,
    toFloat64(0) AS buy_exec_size,
    toFloat64(0) AS sell_exec_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_max_heatmap_proxy_value AS max_heatmap_intensity,
    toUInt64(0) AS bid_wall_rows,
    toUInt64(0) AS ask_wall_rows
FROM agg
UNION ALL
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
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S
)
SELECT
    'HEATMAP' AS domain,
    3 AS scale_order,
    '30S' AS scale,
    'BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    toFloat64(0) AS total_exec_size,
    toFloat64(0) AS buy_exec_size,
    toFloat64(0) AS sell_exec_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_max_heatmap_proxy_value AS max_heatmap_intensity,
    toUInt64(0) AS bid_wall_rows,
    toUInt64(0) AS ask_wall_rows
FROM agg
UNION ALL
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
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M
)
SELECT
    'HEATMAP' AS domain,
    4 AS scale_order,
    '1M' AS scale,
    'BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    toFloat64(0) AS total_exec_size,
    toFloat64(0) AS buy_exec_size,
    toFloat64(0) AS sell_exec_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_max_heatmap_proxy_value AS max_heatmap_intensity,
    toUInt64(0) AS bid_wall_rows,
    toUInt64(0) AS ask_wall_rows
FROM agg
UNION ALL
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
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M
)
SELECT
    'HEATMAP' AS domain,
    5 AS scale_order,
    '5M' AS scale,
    'BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    toFloat64(0) AS total_exec_size,
    toFloat64(0) AS buy_exec_size,
    toFloat64(0) AS sell_exec_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_max_heatmap_proxy_value AS max_heatmap_intensity,
    toUInt64(0) AS bid_wall_rows,
    toUInt64(0) AS ask_wall_rows
FROM agg
UNION ALL
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
        sum(sell_exec_size) AS agg_sell_exec_size,
        sum(total_liquidity_event_size) AS agg_total_liquidity_event_size,
        max(heatmap_intensity) AS agg_max_heatmap_intensity,
        toUInt64(sum(is_bid_liquidity_event_wall)) AS agg_bid_wall_rows,
        toUInt64(sum(is_ask_liquidity_event_wall)) AS agg_ask_wall_rows
    FROM BM_MNQ_FRAME_SOURCE_1S
)
SELECT
    'FRAME' AS domain,
    1 AS scale_order,
    '1S' AS scale,
    'BM_MNQ_FRAME_SOURCE_1S' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_max_heatmap_intensity AS max_heatmap_intensity,
    agg_bid_wall_rows AS bid_wall_rows,
    agg_ask_wall_rows AS ask_wall_rows
FROM agg
UNION ALL
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
        sum(sell_exec_size) AS agg_sell_exec_size,
        sum(total_liquidity_event_size) AS agg_total_liquidity_event_size,
        max(heatmap_intensity) AS agg_max_heatmap_intensity,
        toUInt64(sum(is_bid_liquidity_event_wall)) AS agg_bid_wall_rows,
        toUInt64(sum(is_ask_liquidity_event_wall)) AS agg_ask_wall_rows
    FROM BM_MNQ_FRAME_SOURCE_5S
)
SELECT
    'FRAME' AS domain,
    2 AS scale_order,
    '5S' AS scale,
    'BM_MNQ_FRAME_SOURCE_5S' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_max_heatmap_intensity AS max_heatmap_intensity,
    agg_bid_wall_rows AS bid_wall_rows,
    agg_ask_wall_rows AS ask_wall_rows
FROM agg
UNION ALL
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
        sum(sell_exec_size) AS agg_sell_exec_size,
        sum(total_liquidity_event_size) AS agg_total_liquidity_event_size,
        max(heatmap_intensity) AS agg_max_heatmap_intensity,
        toUInt64(sum(is_bid_liquidity_event_wall)) AS agg_bid_wall_rows,
        toUInt64(sum(is_ask_liquidity_event_wall)) AS agg_ask_wall_rows
    FROM BM_MNQ_FRAME_SOURCE_30S
)
SELECT
    'FRAME' AS domain,
    3 AS scale_order,
    '30S' AS scale,
    'BM_MNQ_FRAME_SOURCE_30S' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_max_heatmap_intensity AS max_heatmap_intensity,
    agg_bid_wall_rows AS bid_wall_rows,
    agg_ask_wall_rows AS ask_wall_rows
FROM agg
UNION ALL
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
        sum(sell_exec_size) AS agg_sell_exec_size,
        sum(total_liquidity_event_size) AS agg_total_liquidity_event_size,
        max(heatmap_intensity) AS agg_max_heatmap_intensity,
        toUInt64(sum(is_bid_liquidity_event_wall)) AS agg_bid_wall_rows,
        toUInt64(sum(is_ask_liquidity_event_wall)) AS agg_ask_wall_rows
    FROM BM_MNQ_FRAME_SOURCE_1M
)
SELECT
    'FRAME' AS domain,
    4 AS scale_order,
    '1M' AS scale,
    'BM_MNQ_FRAME_SOURCE_1M' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_max_heatmap_intensity AS max_heatmap_intensity,
    agg_bid_wall_rows AS bid_wall_rows,
    agg_ask_wall_rows AS ask_wall_rows
FROM agg
UNION ALL
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
        sum(sell_exec_size) AS agg_sell_exec_size,
        sum(total_liquidity_event_size) AS agg_total_liquidity_event_size,
        max(heatmap_intensity) AS agg_max_heatmap_intensity,
        toUInt64(sum(is_bid_liquidity_event_wall)) AS agg_bid_wall_rows,
        toUInt64(sum(is_ask_liquidity_event_wall)) AS agg_ask_wall_rows
    FROM BM_MNQ_FRAME_SOURCE_5M
)
SELECT
    'FRAME' AS domain,
    5 AS scale_order,
    '5M' AS scale,
    'BM_MNQ_FRAME_SOURCE_5M' AS table_name,
    agg_rows AS rows,
    agg_min_trade_date AS min_trade_date,
    agg_max_trade_date AS max_trade_date,
    agg_min_ts_et AS min_ts_et,
    agg_max_ts_et AS max_ts_et,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_max_heatmap_intensity AS max_heatmap_intensity,
    agg_bid_wall_rows AS bid_wall_rows,
    agg_ask_wall_rows AS ask_wall_rows
FROM agg
;

SELECT
    domain,
    scale,
    table_name,
    rows,
    min_trade_date,
    max_trade_date,
    min_ts_et,
    max_ts_et,
    total_exec_size,
    buy_exec_size,
    sell_exec_size,
    total_liquidity_event_size,
    max_heatmap_intensity,
    bid_wall_rows,
    ask_wall_rows
FROM BM_MNQ_QA_SCALE_SUMMARY
ORDER BY scale_order, domain;

WITH agg AS
(
    SELECT
        scale_order AS scale_order,
        scale AS scale,
        table_name AS table_name,
        total_exec_size AS agg_total_exec_size,
        buy_exec_size AS agg_buy_exec_size,
        sell_exec_size AS agg_sell_exec_size
    FROM BM_MNQ_QA_SCALE_SUMMARY
    WHERE domain = 'AGGRESSION'
)
SELECT
    'AGGRESSION_BALANCE_CHECK' AS check_name,
    scale,
    table_name,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    if
    (
        agg_total_exec_size = 0,
        0,
        (agg_buy_exec_size - agg_sell_exec_size) / agg_total_exec_size
    ) AS net_exec_imbalance
FROM agg
ORDER BY scale_order;

SELECT
    'FRAME_WALL_AND_INTENSITY_CHECK' AS check_name,
    scale,
    table_name,
    rows,
    max_heatmap_intensity,
    bid_wall_rows,
    ask_wall_rows
FROM BM_MNQ_QA_SCALE_SUMMARY
WHERE domain = 'FRAME'
ORDER BY scale_order;

SELECT
    'TABLE_EXISTENCE_CHECK' AS check_name,
    name,
    engine,
    total_rows,
    total_bytes
FROM system.tables
WHERE database = currentDatabase()
  AND name LIKE 'BM_MNQ_%'
  AND
  (
      name LIKE 'BM_MNQ_AGGRESSION_EXECUTIONS_%'
      OR name LIKE 'BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_%'
      OR name LIKE 'BM_MNQ_FRAME_SOURCE_%'
      OR name = 'BM_MNQ_QA_SCALE_SUMMARY'
  )
ORDER BY name;
