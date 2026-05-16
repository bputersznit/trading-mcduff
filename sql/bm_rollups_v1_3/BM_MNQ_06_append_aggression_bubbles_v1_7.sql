-- BM_MNQ_06_append_aggression_bubbles_v1_7.sql
-- Generated: 2026-05-10 14:28:00 America/New_York
--
-- Project: Bookmap Emulation / MNQ
--
-- Purpose:
--   Repair BM_MNQ_FRAME_SOURCE_* after v1.6 by appending aggression bubble rows.
--
-- Why this is needed:
--   v1.6 rebuilt the frame tables successfully and normalized heatmap_intensity
--   to <= 1, but QA showed:
--
--       total_exec_size = 0
--       aggression_rows = 0
--       joined_rows = 0
--
--   This means the frame tables are currently heatmap-only.
--
-- Root cause:
--   The heatmap-led exact joins did not match aggression rows, and the
--   aggression-only anti-join did not insert rows. A likely reason is
--   ClickHouse's default join_use_nulls=0 behavior: unmatched right-side columns
--   are materialized with default values, so h.trade_date IS NULL is not a
--   reliable anti-join test unless join_use_nulls=1 is set.
--
-- Repair strategy:
--   Since v1.6 QA showed joined_rows = 0 for all scales, the safest repair is
--   to append every aggression rollup row as an explicit aggression-bubble row
--   with zero heatmap fields. This preserves:
--
--       heatmap rows already present from v1.6
--       all aggression bubble rows from BM_MNQ_AGGRESSION_EXECUTIONS_<scale>
--
-- Run:
--   clickhouse-client --multiquery < BM_MNQ_06_append_aggression_bubbles_v1_7.sql
--
-- Then:
--   clickhouse-client --multiquery < BM_MNQ_07_frame_repair_qa_v1_7.sql

SET max_threads = 8;
SET max_memory_usage = 0;
SET max_bytes_before_external_group_by = 20000000000;
SET max_bytes_before_external_sort = 20000000000;


--------------------------------------------------------------------------------
-- APPEND AGGRESSION BUBBLES 1S
--------------------------------------------------------------------------------

INSERT INTO BM_MNQ_FRAME_SOURCE_1S
SELECT
    a.trade_date AS trade_date,
    a.ts_bucket AS ts_bucket,
    a.ts_et AS ts_et,
    a.symbol AS symbol,
    a.price AS price,

    0 AS bid_add_size,
    0 AS ask_add_size,
    0 AS bid_cancel_size,
    0 AS ask_cancel_size,
    0 AS bid_modify_size,
    0 AS ask_modify_size,
    0 AS bid_trade_size,
    0 AS ask_trade_size,

    0 AS bid_event_count,
    0 AS ask_event_count,
    0 AS total_event_count,

    0 AS bid_liquidity_event_size,
    0 AS ask_liquidity_event_size,
    0 AS total_liquidity_event_size,
    0 AS net_liquidity_event_delta,

    0 AS heatmap_proxy_value,
    0 AS max_heatmap_proxy_value,
    0 AS avg_heatmap_proxy_value,
    0 AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    0 AS heatmap_intensity,
    0 AS is_bid_liquidity_event_wall,
    0 AS is_ask_liquidity_event_wall,

    a.buy_exec_size AS buy_exec_size,
    a.sell_exec_size AS sell_exec_size,
    a.total_exec_size AS total_exec_size,
    a.exec_delta AS exec_delta,
    a.exec_imbalance AS exec_imbalance,
    a.buy_trade_count AS buy_trade_count,
    a.sell_trade_count AS sell_trade_count,
    a.total_trade_count AS total_trade_count,
    a.bubble_total_size AS bubble_total_size,
    a.bubble_buy_share AS bubble_buy_share,
    a.bubble_sell_share AS bubble_sell_share,
    a.bubble_side AS bubble_side,

    a.rth_flag AS rth_flag
FROM BM_MNQ_AGGRESSION_EXECUTIONS_1S AS a
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON a.trade_date = d.trade_date;

OPTIMIZE TABLE BM_MNQ_FRAME_SOURCE_1S FINAL;

--------------------------------------------------------------------------------
-- APPEND AGGRESSION BUBBLES 5S
--------------------------------------------------------------------------------

INSERT INTO BM_MNQ_FRAME_SOURCE_5S
SELECT
    a.trade_date AS trade_date,
    a.ts_bucket AS ts_bucket,
    a.ts_et AS ts_et,
    a.symbol AS symbol,
    a.price AS price,

    0 AS bid_add_size,
    0 AS ask_add_size,
    0 AS bid_cancel_size,
    0 AS ask_cancel_size,
    0 AS bid_modify_size,
    0 AS ask_modify_size,
    0 AS bid_trade_size,
    0 AS ask_trade_size,

    0 AS bid_event_count,
    0 AS ask_event_count,
    0 AS total_event_count,

    0 AS bid_liquidity_event_size,
    0 AS ask_liquidity_event_size,
    0 AS total_liquidity_event_size,
    0 AS net_liquidity_event_delta,

    0 AS heatmap_proxy_value,
    0 AS max_heatmap_proxy_value,
    0 AS avg_heatmap_proxy_value,
    0 AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    0 AS heatmap_intensity,
    0 AS is_bid_liquidity_event_wall,
    0 AS is_ask_liquidity_event_wall,

    a.buy_exec_size AS buy_exec_size,
    a.sell_exec_size AS sell_exec_size,
    a.total_exec_size AS total_exec_size,
    a.exec_delta AS exec_delta,
    a.exec_imbalance AS exec_imbalance,
    a.buy_trade_count AS buy_trade_count,
    a.sell_trade_count AS sell_trade_count,
    a.total_trade_count AS total_trade_count,
    a.bubble_total_size AS bubble_total_size,
    a.bubble_buy_share AS bubble_buy_share,
    a.bubble_sell_share AS bubble_sell_share,
    a.bubble_side AS bubble_side,

    a.rth_flag AS rth_flag
FROM BM_MNQ_AGGRESSION_EXECUTIONS_5S AS a
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON a.trade_date = d.trade_date;

OPTIMIZE TABLE BM_MNQ_FRAME_SOURCE_5S FINAL;

--------------------------------------------------------------------------------
-- APPEND AGGRESSION BUBBLES 30S
--------------------------------------------------------------------------------

INSERT INTO BM_MNQ_FRAME_SOURCE_30S
SELECT
    a.trade_date AS trade_date,
    a.ts_bucket AS ts_bucket,
    a.ts_et AS ts_et,
    a.symbol AS symbol,
    a.price AS price,

    0 AS bid_add_size,
    0 AS ask_add_size,
    0 AS bid_cancel_size,
    0 AS ask_cancel_size,
    0 AS bid_modify_size,
    0 AS ask_modify_size,
    0 AS bid_trade_size,
    0 AS ask_trade_size,

    0 AS bid_event_count,
    0 AS ask_event_count,
    0 AS total_event_count,

    0 AS bid_liquidity_event_size,
    0 AS ask_liquidity_event_size,
    0 AS total_liquidity_event_size,
    0 AS net_liquidity_event_delta,

    0 AS heatmap_proxy_value,
    0 AS max_heatmap_proxy_value,
    0 AS avg_heatmap_proxy_value,
    0 AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    0 AS heatmap_intensity,
    0 AS is_bid_liquidity_event_wall,
    0 AS is_ask_liquidity_event_wall,

    a.buy_exec_size AS buy_exec_size,
    a.sell_exec_size AS sell_exec_size,
    a.total_exec_size AS total_exec_size,
    a.exec_delta AS exec_delta,
    a.exec_imbalance AS exec_imbalance,
    a.buy_trade_count AS buy_trade_count,
    a.sell_trade_count AS sell_trade_count,
    a.total_trade_count AS total_trade_count,
    a.bubble_total_size AS bubble_total_size,
    a.bubble_buy_share AS bubble_buy_share,
    a.bubble_sell_share AS bubble_sell_share,
    a.bubble_side AS bubble_side,

    a.rth_flag AS rth_flag
FROM BM_MNQ_AGGRESSION_EXECUTIONS_30S AS a
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON a.trade_date = d.trade_date;

OPTIMIZE TABLE BM_MNQ_FRAME_SOURCE_30S FINAL;

--------------------------------------------------------------------------------
-- APPEND AGGRESSION BUBBLES 1M
--------------------------------------------------------------------------------

INSERT INTO BM_MNQ_FRAME_SOURCE_1M
SELECT
    a.trade_date AS trade_date,
    a.ts_bucket AS ts_bucket,
    a.ts_et AS ts_et,
    a.symbol AS symbol,
    a.price AS price,

    0 AS bid_add_size,
    0 AS ask_add_size,
    0 AS bid_cancel_size,
    0 AS ask_cancel_size,
    0 AS bid_modify_size,
    0 AS ask_modify_size,
    0 AS bid_trade_size,
    0 AS ask_trade_size,

    0 AS bid_event_count,
    0 AS ask_event_count,
    0 AS total_event_count,

    0 AS bid_liquidity_event_size,
    0 AS ask_liquidity_event_size,
    0 AS total_liquidity_event_size,
    0 AS net_liquidity_event_delta,

    0 AS heatmap_proxy_value,
    0 AS max_heatmap_proxy_value,
    0 AS avg_heatmap_proxy_value,
    0 AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    0 AS heatmap_intensity,
    0 AS is_bid_liquidity_event_wall,
    0 AS is_ask_liquidity_event_wall,

    a.buy_exec_size AS buy_exec_size,
    a.sell_exec_size AS sell_exec_size,
    a.total_exec_size AS total_exec_size,
    a.exec_delta AS exec_delta,
    a.exec_imbalance AS exec_imbalance,
    a.buy_trade_count AS buy_trade_count,
    a.sell_trade_count AS sell_trade_count,
    a.total_trade_count AS total_trade_count,
    a.bubble_total_size AS bubble_total_size,
    a.bubble_buy_share AS bubble_buy_share,
    a.bubble_sell_share AS bubble_sell_share,
    a.bubble_side AS bubble_side,

    a.rth_flag AS rth_flag
FROM BM_MNQ_AGGRESSION_EXECUTIONS_1M AS a
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON a.trade_date = d.trade_date;

OPTIMIZE TABLE BM_MNQ_FRAME_SOURCE_1M FINAL;

--------------------------------------------------------------------------------
-- APPEND AGGRESSION BUBBLES 5M
--------------------------------------------------------------------------------

INSERT INTO BM_MNQ_FRAME_SOURCE_5M
SELECT
    a.trade_date AS trade_date,
    a.ts_bucket AS ts_bucket,
    a.ts_et AS ts_et,
    a.symbol AS symbol,
    a.price AS price,

    0 AS bid_add_size,
    0 AS ask_add_size,
    0 AS bid_cancel_size,
    0 AS ask_cancel_size,
    0 AS bid_modify_size,
    0 AS ask_modify_size,
    0 AS bid_trade_size,
    0 AS ask_trade_size,

    0 AS bid_event_count,
    0 AS ask_event_count,
    0 AS total_event_count,

    0 AS bid_liquidity_event_size,
    0 AS ask_liquidity_event_size,
    0 AS total_liquidity_event_size,
    0 AS net_liquidity_event_delta,

    0 AS heatmap_proxy_value,
    0 AS max_heatmap_proxy_value,
    0 AS avg_heatmap_proxy_value,
    0 AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    0 AS heatmap_intensity,
    0 AS is_bid_liquidity_event_wall,
    0 AS is_ask_liquidity_event_wall,

    a.buy_exec_size AS buy_exec_size,
    a.sell_exec_size AS sell_exec_size,
    a.total_exec_size AS total_exec_size,
    a.exec_delta AS exec_delta,
    a.exec_imbalance AS exec_imbalance,
    a.buy_trade_count AS buy_trade_count,
    a.sell_trade_count AS sell_trade_count,
    a.total_trade_count AS total_trade_count,
    a.bubble_total_size AS bubble_total_size,
    a.bubble_buy_share AS bubble_buy_share,
    a.bubble_sell_share AS bubble_sell_share,
    a.bubble_side AS bubble_side,

    a.rth_flag AS rth_flag
FROM BM_MNQ_AGGRESSION_EXECUTIONS_5M AS a
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON a.trade_date = d.trade_date;

OPTIMIZE TABLE BM_MNQ_FRAME_SOURCE_5M FINAL;
