-- BM_MNQ_05_frame_repair_qa_v1_6.sql
-- Generated: 2026-05-10 14:12:00 America/New_York
--
-- Purpose:
--   QA batched repaired frame sources after BM_MNQ_run_frame_repair_batched_v1_6.sh.
--
-- Expected:
--   total_exec_size should match aggression ladder total:
--       36,614,148
--   max_heatmap_intensity should be <= 1.

DROP TABLE IF EXISTS BM_MNQ_QA_FRAME_REPAIR_V1_6;

CREATE TABLE BM_MNQ_QA_FRAME_REPAIR_V1_6
ENGINE = MergeTree
ORDER BY (scale_order, scale, table_name)
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
        sum(sell_exec_size) AS agg_sell_exec_size,
        sum(total_liquidity_event_size) AS agg_total_liquidity_event_size,
        max(heatmap_intensity) AS agg_max_heatmap_intensity,
        toUInt64(sum(is_bid_liquidity_event_wall)) AS agg_bid_wall_rows,
        toUInt64(sum(is_ask_liquidity_event_wall)) AS agg_ask_wall_rows,
        toUInt64(countIf(total_exec_size > 0)) AS agg_aggression_rows,
        toUInt64(countIf(total_liquidity_event_size > 0)) AS agg_heatmap_rows,
        toUInt64(countIf(total_exec_size > 0 AND total_liquidity_event_size = 0)) AS agg_aggression_only_rows,
        toUInt64(countIf(total_exec_size = 0 AND total_liquidity_event_size > 0)) AS agg_heatmap_only_rows,
        toUInt64(countIf(total_exec_size > 0 AND total_liquidity_event_size > 0)) AS agg_joined_rows
    FROM BM_MNQ_FRAME_SOURCE_1S
)
SELECT
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
    agg_ask_wall_rows AS ask_wall_rows,
    agg_aggression_rows AS aggression_rows,
    agg_heatmap_rows AS heatmap_rows,
    agg_aggression_only_rows AS aggression_only_rows,
    agg_heatmap_only_rows AS heatmap_only_rows,
    agg_joined_rows AS joined_rows
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
        toUInt64(sum(is_ask_liquidity_event_wall)) AS agg_ask_wall_rows,
        toUInt64(countIf(total_exec_size > 0)) AS agg_aggression_rows,
        toUInt64(countIf(total_liquidity_event_size > 0)) AS agg_heatmap_rows,
        toUInt64(countIf(total_exec_size > 0 AND total_liquidity_event_size = 0)) AS agg_aggression_only_rows,
        toUInt64(countIf(total_exec_size = 0 AND total_liquidity_event_size > 0)) AS agg_heatmap_only_rows,
        toUInt64(countIf(total_exec_size > 0 AND total_liquidity_event_size > 0)) AS agg_joined_rows
    FROM BM_MNQ_FRAME_SOURCE_5S
)
SELECT
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
    agg_ask_wall_rows AS ask_wall_rows,
    agg_aggression_rows AS aggression_rows,
    agg_heatmap_rows AS heatmap_rows,
    agg_aggression_only_rows AS aggression_only_rows,
    agg_heatmap_only_rows AS heatmap_only_rows,
    agg_joined_rows AS joined_rows
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
        toUInt64(sum(is_ask_liquidity_event_wall)) AS agg_ask_wall_rows,
        toUInt64(countIf(total_exec_size > 0)) AS agg_aggression_rows,
        toUInt64(countIf(total_liquidity_event_size > 0)) AS agg_heatmap_rows,
        toUInt64(countIf(total_exec_size > 0 AND total_liquidity_event_size = 0)) AS agg_aggression_only_rows,
        toUInt64(countIf(total_exec_size = 0 AND total_liquidity_event_size > 0)) AS agg_heatmap_only_rows,
        toUInt64(countIf(total_exec_size > 0 AND total_liquidity_event_size > 0)) AS agg_joined_rows
    FROM BM_MNQ_FRAME_SOURCE_30S
)
SELECT
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
    agg_ask_wall_rows AS ask_wall_rows,
    agg_aggression_rows AS aggression_rows,
    agg_heatmap_rows AS heatmap_rows,
    agg_aggression_only_rows AS aggression_only_rows,
    agg_heatmap_only_rows AS heatmap_only_rows,
    agg_joined_rows AS joined_rows
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
        toUInt64(sum(is_ask_liquidity_event_wall)) AS agg_ask_wall_rows,
        toUInt64(countIf(total_exec_size > 0)) AS agg_aggression_rows,
        toUInt64(countIf(total_liquidity_event_size > 0)) AS agg_heatmap_rows,
        toUInt64(countIf(total_exec_size > 0 AND total_liquidity_event_size = 0)) AS agg_aggression_only_rows,
        toUInt64(countIf(total_exec_size = 0 AND total_liquidity_event_size > 0)) AS agg_heatmap_only_rows,
        toUInt64(countIf(total_exec_size > 0 AND total_liquidity_event_size > 0)) AS agg_joined_rows
    FROM BM_MNQ_FRAME_SOURCE_1M
)
SELECT
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
    agg_ask_wall_rows AS ask_wall_rows,
    agg_aggression_rows AS aggression_rows,
    agg_heatmap_rows AS heatmap_rows,
    agg_aggression_only_rows AS aggression_only_rows,
    agg_heatmap_only_rows AS heatmap_only_rows,
    agg_joined_rows AS joined_rows
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
        toUInt64(sum(is_ask_liquidity_event_wall)) AS agg_ask_wall_rows,
        toUInt64(countIf(total_exec_size > 0)) AS agg_aggression_rows,
        toUInt64(countIf(total_liquidity_event_size > 0)) AS agg_heatmap_rows,
        toUInt64(countIf(total_exec_size > 0 AND total_liquidity_event_size = 0)) AS agg_aggression_only_rows,
        toUInt64(countIf(total_exec_size = 0 AND total_liquidity_event_size > 0)) AS agg_heatmap_only_rows,
        toUInt64(countIf(total_exec_size > 0 AND total_liquidity_event_size > 0)) AS agg_joined_rows
    FROM BM_MNQ_FRAME_SOURCE_5M
)
SELECT
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
    agg_ask_wall_rows AS ask_wall_rows,
    agg_aggression_rows AS aggression_rows,
    agg_heatmap_rows AS heatmap_rows,
    agg_aggression_only_rows AS aggression_only_rows,
    agg_heatmap_only_rows AS heatmap_only_rows,
    agg_joined_rows AS joined_rows
FROM agg
;

SELECT
    scale,
    table_name,
    rows,
    total_exec_size,
    buy_exec_size,
    sell_exec_size,
    total_liquidity_event_size,
    max_heatmap_intensity,
    bid_wall_rows,
    ask_wall_rows,
    aggression_rows,
    heatmap_rows,
    aggression_only_rows,
    heatmap_only_rows,
    joined_rows
FROM BM_MNQ_QA_FRAME_REPAIR_V1_6
ORDER BY scale_order;

SELECT
    'FRAME_REPAIR_PASS_FAIL' AS check_name,
    scale,
    total_exec_size,
    max_heatmap_intensity,
    if(total_exec_size = 36614148, 'PASS', 'CHECK_EXEC_TOTAL') AS exec_total_check,
    if(max_heatmap_intensity <= 1, 'PASS', 'CHECK_INTENSITY') AS intensity_check
FROM BM_MNQ_QA_FRAME_REPAIR_V1_6
ORDER BY scale_order;
