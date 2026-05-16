-- BM_MNQ_01_rollup_scales_v1_2.sql
-- Generated: 2026-05-10 13:05:00 America/New_York
--
-- Project: Bookmap Emulation / MNQ
--
-- Purpose:
--   Build the Bookmap emulation multi-scale ladder from the validated 100ms
--   base layer.
--
-- Canonical namespace:
--   BM_MNQ_*
--
-- Source tables:
--   BM_MNQ_AGGRESSION_EXECUTIONS_100MS
--   BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS
--   BM_MNQ_HEATMAP_DAILY_MAX_RTH
--
-- Output tables:
--   BM_MNQ_AGGRESSION_EXECUTIONS_1S
--   BM_MNQ_AGGRESSION_EXECUTIONS_5S
--   BM_MNQ_AGGRESSION_EXECUTIONS_30S
--   BM_MNQ_AGGRESSION_EXECUTIONS_1M
--   BM_MNQ_AGGRESSION_EXECUTIONS_5M
--
--   BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
--   BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
--   BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S
--   BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M
--   BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M
--
--   BM_MNQ_FRAME_SOURCE_1S
--   BM_MNQ_FRAME_SOURCE_5S
--   BM_MNQ_FRAME_SOURCE_30S
--   BM_MNQ_FRAME_SOURCE_1M
--   BM_MNQ_FRAME_SOURCE_5M
--
-- Critical ClickHouse 26.3 compatibility rule:
--   Do not project a direct aggregate to a public source-column name inside the
--   same grouped SELECT. This script uses a grouped CTE with agg_* aliases and
--   then an outer SELECT to project public names.
--
-- Run:
--   clickhouse-client --multiquery < BM_MNQ_01_rollup_scales_v1_2.sql

SET max_threads = 16;
SET max_memory_usage = 0;
SET max_bytes_before_external_group_by = 20000000000;
SET max_bytes_before_external_sort = 20000000000;
SET optimize_move_to_prewhere = 1;


--------------------------------------------------------------------------------
-- 1S AGGRESSION ROLLUP
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_AGGRESSION_EXECUTIONS_1S;

CREATE TABLE BM_MNQ_AGGRESSION_EXECUTIONS_1S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH grouped AS
(
    SELECT
        src.trade_date AS trade_date,
        toStartOfInterval(src.ts_bucket, toIntervalSecond(1)) AS roll_ts_bucket,
        toStartOfInterval(src.ts_et, toIntervalSecond(1)) AS roll_ts_et,
        src.symbol AS symbol,
        src.price AS price,

        sum(src.buy_exec_size) AS agg_buy_exec_size,
        sum(src.sell_exec_size) AS agg_sell_exec_size,
        sum(src.total_exec_size) AS agg_total_exec_size,
        sum(src.buy_trade_count) AS agg_buy_trade_count,
        sum(src.sell_trade_count) AS agg_sell_trade_count,
        sum(src.total_trade_count) AS agg_total_trade_count,
        max(src.rth_flag) AS agg_rth_flag
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_100MS AS src
    GROUP BY
        src.trade_date,
        roll_ts_bucket,
        roll_ts_et,
        src.symbol,
        src.price
)
SELECT
    trade_date AS trade_date,
    roll_ts_bucket AS ts_bucket,
    roll_ts_et AS ts_et,
    symbol AS symbol,
    price AS price,

    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size - agg_sell_exec_size AS exec_delta,
    if
    (
        agg_total_exec_size = 0,
        0,
        (agg_buy_exec_size - agg_sell_exec_size) / agg_total_exec_size
    ) AS exec_imbalance,

    agg_buy_trade_count AS buy_trade_count,
    agg_sell_trade_count AS sell_trade_count,
    agg_total_trade_count AS total_trade_count,

    agg_total_exec_size AS bubble_total_size,
    if(agg_total_exec_size = 0, 0, agg_buy_exec_size / agg_total_exec_size) AS bubble_buy_share,
    if(agg_total_exec_size = 0, 0, agg_sell_exec_size / agg_total_exec_size) AS bubble_sell_share,
    multiIf
    (
        agg_total_exec_size = 0, 'NONE',
        agg_buy_exec_size > agg_sell_exec_size, 'BUY',
        agg_sell_exec_size > agg_buy_exec_size, 'SELL',
        'MIXED'
    ) AS bubble_side,

    agg_rth_flag AS rth_flag
FROM grouped;

--------------------------------------------------------------------------------
-- 5S AGGRESSION ROLLUP
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_AGGRESSION_EXECUTIONS_5S;

CREATE TABLE BM_MNQ_AGGRESSION_EXECUTIONS_5S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH grouped AS
(
    SELECT
        src.trade_date AS trade_date,
        toStartOfInterval(src.ts_bucket, toIntervalSecond(5)) AS roll_ts_bucket,
        toStartOfInterval(src.ts_et, toIntervalSecond(5)) AS roll_ts_et,
        src.symbol AS symbol,
        src.price AS price,

        sum(src.buy_exec_size) AS agg_buy_exec_size,
        sum(src.sell_exec_size) AS agg_sell_exec_size,
        sum(src.total_exec_size) AS agg_total_exec_size,
        sum(src.buy_trade_count) AS agg_buy_trade_count,
        sum(src.sell_trade_count) AS agg_sell_trade_count,
        sum(src.total_trade_count) AS agg_total_trade_count,
        max(src.rth_flag) AS agg_rth_flag
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_100MS AS src
    GROUP BY
        src.trade_date,
        roll_ts_bucket,
        roll_ts_et,
        src.symbol,
        src.price
)
SELECT
    trade_date AS trade_date,
    roll_ts_bucket AS ts_bucket,
    roll_ts_et AS ts_et,
    symbol AS symbol,
    price AS price,

    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size - agg_sell_exec_size AS exec_delta,
    if
    (
        agg_total_exec_size = 0,
        0,
        (agg_buy_exec_size - agg_sell_exec_size) / agg_total_exec_size
    ) AS exec_imbalance,

    agg_buy_trade_count AS buy_trade_count,
    agg_sell_trade_count AS sell_trade_count,
    agg_total_trade_count AS total_trade_count,

    agg_total_exec_size AS bubble_total_size,
    if(agg_total_exec_size = 0, 0, agg_buy_exec_size / agg_total_exec_size) AS bubble_buy_share,
    if(agg_total_exec_size = 0, 0, agg_sell_exec_size / agg_total_exec_size) AS bubble_sell_share,
    multiIf
    (
        agg_total_exec_size = 0, 'NONE',
        agg_buy_exec_size > agg_sell_exec_size, 'BUY',
        agg_sell_exec_size > agg_buy_exec_size, 'SELL',
        'MIXED'
    ) AS bubble_side,

    agg_rth_flag AS rth_flag
FROM grouped;

--------------------------------------------------------------------------------
-- 30S AGGRESSION ROLLUP
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_AGGRESSION_EXECUTIONS_30S;

CREATE TABLE BM_MNQ_AGGRESSION_EXECUTIONS_30S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH grouped AS
(
    SELECT
        src.trade_date AS trade_date,
        toStartOfInterval(src.ts_bucket, toIntervalSecond(30)) AS roll_ts_bucket,
        toStartOfInterval(src.ts_et, toIntervalSecond(30)) AS roll_ts_et,
        src.symbol AS symbol,
        src.price AS price,

        sum(src.buy_exec_size) AS agg_buy_exec_size,
        sum(src.sell_exec_size) AS agg_sell_exec_size,
        sum(src.total_exec_size) AS agg_total_exec_size,
        sum(src.buy_trade_count) AS agg_buy_trade_count,
        sum(src.sell_trade_count) AS agg_sell_trade_count,
        sum(src.total_trade_count) AS agg_total_trade_count,
        max(src.rth_flag) AS agg_rth_flag
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_100MS AS src
    GROUP BY
        src.trade_date,
        roll_ts_bucket,
        roll_ts_et,
        src.symbol,
        src.price
)
SELECT
    trade_date AS trade_date,
    roll_ts_bucket AS ts_bucket,
    roll_ts_et AS ts_et,
    symbol AS symbol,
    price AS price,

    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size - agg_sell_exec_size AS exec_delta,
    if
    (
        agg_total_exec_size = 0,
        0,
        (agg_buy_exec_size - agg_sell_exec_size) / agg_total_exec_size
    ) AS exec_imbalance,

    agg_buy_trade_count AS buy_trade_count,
    agg_sell_trade_count AS sell_trade_count,
    agg_total_trade_count AS total_trade_count,

    agg_total_exec_size AS bubble_total_size,
    if(agg_total_exec_size = 0, 0, agg_buy_exec_size / agg_total_exec_size) AS bubble_buy_share,
    if(agg_total_exec_size = 0, 0, agg_sell_exec_size / agg_total_exec_size) AS bubble_sell_share,
    multiIf
    (
        agg_total_exec_size = 0, 'NONE',
        agg_buy_exec_size > agg_sell_exec_size, 'BUY',
        agg_sell_exec_size > agg_buy_exec_size, 'SELL',
        'MIXED'
    ) AS bubble_side,

    agg_rth_flag AS rth_flag
FROM grouped;

--------------------------------------------------------------------------------
-- 1M AGGRESSION ROLLUP
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_AGGRESSION_EXECUTIONS_1M;

CREATE TABLE BM_MNQ_AGGRESSION_EXECUTIONS_1M
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH grouped AS
(
    SELECT
        src.trade_date AS trade_date,
        toStartOfInterval(src.ts_bucket, toIntervalMinute(1)) AS roll_ts_bucket,
        toStartOfInterval(src.ts_et, toIntervalMinute(1)) AS roll_ts_et,
        src.symbol AS symbol,
        src.price AS price,

        sum(src.buy_exec_size) AS agg_buy_exec_size,
        sum(src.sell_exec_size) AS agg_sell_exec_size,
        sum(src.total_exec_size) AS agg_total_exec_size,
        sum(src.buy_trade_count) AS agg_buy_trade_count,
        sum(src.sell_trade_count) AS agg_sell_trade_count,
        sum(src.total_trade_count) AS agg_total_trade_count,
        max(src.rth_flag) AS agg_rth_flag
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_100MS AS src
    GROUP BY
        src.trade_date,
        roll_ts_bucket,
        roll_ts_et,
        src.symbol,
        src.price
)
SELECT
    trade_date AS trade_date,
    roll_ts_bucket AS ts_bucket,
    roll_ts_et AS ts_et,
    symbol AS symbol,
    price AS price,

    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size - agg_sell_exec_size AS exec_delta,
    if
    (
        agg_total_exec_size = 0,
        0,
        (agg_buy_exec_size - agg_sell_exec_size) / agg_total_exec_size
    ) AS exec_imbalance,

    agg_buy_trade_count AS buy_trade_count,
    agg_sell_trade_count AS sell_trade_count,
    agg_total_trade_count AS total_trade_count,

    agg_total_exec_size AS bubble_total_size,
    if(agg_total_exec_size = 0, 0, agg_buy_exec_size / agg_total_exec_size) AS bubble_buy_share,
    if(agg_total_exec_size = 0, 0, agg_sell_exec_size / agg_total_exec_size) AS bubble_sell_share,
    multiIf
    (
        agg_total_exec_size = 0, 'NONE',
        agg_buy_exec_size > agg_sell_exec_size, 'BUY',
        agg_sell_exec_size > agg_buy_exec_size, 'SELL',
        'MIXED'
    ) AS bubble_side,

    agg_rth_flag AS rth_flag
FROM grouped;

--------------------------------------------------------------------------------
-- 5M AGGRESSION ROLLUP
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_AGGRESSION_EXECUTIONS_5M;

CREATE TABLE BM_MNQ_AGGRESSION_EXECUTIONS_5M
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH grouped AS
(
    SELECT
        src.trade_date AS trade_date,
        toStartOfInterval(src.ts_bucket, toIntervalMinute(5)) AS roll_ts_bucket,
        toStartOfInterval(src.ts_et, toIntervalMinute(5)) AS roll_ts_et,
        src.symbol AS symbol,
        src.price AS price,

        sum(src.buy_exec_size) AS agg_buy_exec_size,
        sum(src.sell_exec_size) AS agg_sell_exec_size,
        sum(src.total_exec_size) AS agg_total_exec_size,
        sum(src.buy_trade_count) AS agg_buy_trade_count,
        sum(src.sell_trade_count) AS agg_sell_trade_count,
        sum(src.total_trade_count) AS agg_total_trade_count,
        max(src.rth_flag) AS agg_rth_flag
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_100MS AS src
    GROUP BY
        src.trade_date,
        roll_ts_bucket,
        roll_ts_et,
        src.symbol,
        src.price
)
SELECT
    trade_date AS trade_date,
    roll_ts_bucket AS ts_bucket,
    roll_ts_et AS ts_et,
    symbol AS symbol,
    price AS price,

    agg_buy_exec_size AS buy_exec_size,
    agg_sell_exec_size AS sell_exec_size,
    agg_total_exec_size AS total_exec_size,
    agg_buy_exec_size - agg_sell_exec_size AS exec_delta,
    if
    (
        agg_total_exec_size = 0,
        0,
        (agg_buy_exec_size - agg_sell_exec_size) / agg_total_exec_size
    ) AS exec_imbalance,

    agg_buy_trade_count AS buy_trade_count,
    agg_sell_trade_count AS sell_trade_count,
    agg_total_trade_count AS total_trade_count,

    agg_total_exec_size AS bubble_total_size,
    if(agg_total_exec_size = 0, 0, agg_buy_exec_size / agg_total_exec_size) AS bubble_buy_share,
    if(agg_total_exec_size = 0, 0, agg_sell_exec_size / agg_total_exec_size) AS bubble_sell_share,
    multiIf
    (
        agg_total_exec_size = 0, 'NONE',
        agg_buy_exec_size > agg_sell_exec_size, 'BUY',
        agg_sell_exec_size > agg_buy_exec_size, 'SELL',
        'MIXED'
    ) AS bubble_side,

    agg_rth_flag AS rth_flag
FROM grouped;

--------------------------------------------------------------------------------
-- 1S HEATMAP / LIQUIDITY-EVENT-PROXY ROLLUP
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S;

CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH grouped AS
(
    SELECT
        src.trade_date AS trade_date,
        toStartOfInterval(src.ts_bucket, toIntervalSecond(1)) AS roll_ts_bucket,
        toStartOfInterval(src.ts_et, toIntervalSecond(1)) AS roll_ts_et,
        src.symbol AS symbol,
        src.price AS price,

        sum(src.bid_add_size) AS agg_bid_add_size,
        sum(src.ask_add_size) AS agg_ask_add_size,
        sum(src.bid_cancel_size) AS agg_bid_cancel_size,
        sum(src.ask_cancel_size) AS agg_ask_cancel_size,
        sum(src.bid_modify_size) AS agg_bid_modify_size,
        sum(src.ask_modify_size) AS agg_ask_modify_size,
        sum(src.bid_trade_size) AS agg_bid_trade_size,
        sum(src.ask_trade_size) AS agg_ask_trade_size,

        sum(src.bid_event_count) AS agg_bid_event_count,
        sum(src.ask_event_count) AS agg_ask_event_count,
        sum(src.total_event_count) AS agg_total_event_count,

        sum(src.bid_liquidity_event_size) AS agg_bid_liquidity_event_size,
        sum(src.ask_liquidity_event_size) AS agg_ask_liquidity_event_size,
        sum(src.total_liquidity_event_size) AS agg_total_liquidity_event_size,
        sum(src.net_liquidity_event_delta) AS agg_net_liquidity_event_delta,

        max(src.heatmap_proxy_value) AS agg_heatmap_proxy_value,
        max(src.heatmap_proxy_value) AS agg_max_heatmap_proxy_value,
        avg(src.heatmap_proxy_value) AS agg_avg_heatmap_proxy_value,
        count() AS agg_persistence_bucket_count,
        max(src.rth_flag) AS agg_rth_flag
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS AS src
    GROUP BY
        src.trade_date,
        roll_ts_bucket,
        roll_ts_et,
        src.symbol,
        src.price
)
SELECT
    trade_date AS trade_date,
    roll_ts_bucket AS ts_bucket,
    roll_ts_et AS ts_et,
    symbol AS symbol,
    price AS price,

    agg_bid_add_size AS bid_add_size,
    agg_ask_add_size AS ask_add_size,
    agg_bid_cancel_size AS bid_cancel_size,
    agg_ask_cancel_size AS ask_cancel_size,
    agg_bid_modify_size AS bid_modify_size,
    agg_ask_modify_size AS ask_modify_size,
    agg_bid_trade_size AS bid_trade_size,
    agg_ask_trade_size AS ask_trade_size,

    agg_bid_event_count AS bid_event_count,
    agg_ask_event_count AS ask_event_count,
    agg_total_event_count AS total_event_count,

    agg_bid_liquidity_event_size AS bid_liquidity_event_size,
    agg_ask_liquidity_event_size AS ask_liquidity_event_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_net_liquidity_event_delta AS net_liquidity_event_delta,

    agg_heatmap_proxy_value AS heatmap_proxy_value,
    agg_max_heatmap_proxy_value AS max_heatmap_proxy_value,
    agg_avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
    agg_persistence_bucket_count AS persistence_bucket_count,
    agg_rth_flag AS rth_flag
FROM grouped;

--------------------------------------------------------------------------------
-- 5S HEATMAP / LIQUIDITY-EVENT-PROXY ROLLUP
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S;

CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH grouped AS
(
    SELECT
        src.trade_date AS trade_date,
        toStartOfInterval(src.ts_bucket, toIntervalSecond(5)) AS roll_ts_bucket,
        toStartOfInterval(src.ts_et, toIntervalSecond(5)) AS roll_ts_et,
        src.symbol AS symbol,
        src.price AS price,

        sum(src.bid_add_size) AS agg_bid_add_size,
        sum(src.ask_add_size) AS agg_ask_add_size,
        sum(src.bid_cancel_size) AS agg_bid_cancel_size,
        sum(src.ask_cancel_size) AS agg_ask_cancel_size,
        sum(src.bid_modify_size) AS agg_bid_modify_size,
        sum(src.ask_modify_size) AS agg_ask_modify_size,
        sum(src.bid_trade_size) AS agg_bid_trade_size,
        sum(src.ask_trade_size) AS agg_ask_trade_size,

        sum(src.bid_event_count) AS agg_bid_event_count,
        sum(src.ask_event_count) AS agg_ask_event_count,
        sum(src.total_event_count) AS agg_total_event_count,

        sum(src.bid_liquidity_event_size) AS agg_bid_liquidity_event_size,
        sum(src.ask_liquidity_event_size) AS agg_ask_liquidity_event_size,
        sum(src.total_liquidity_event_size) AS agg_total_liquidity_event_size,
        sum(src.net_liquidity_event_delta) AS agg_net_liquidity_event_delta,

        max(src.heatmap_proxy_value) AS agg_heatmap_proxy_value,
        max(src.heatmap_proxy_value) AS agg_max_heatmap_proxy_value,
        avg(src.heatmap_proxy_value) AS agg_avg_heatmap_proxy_value,
        count() AS agg_persistence_bucket_count,
        max(src.rth_flag) AS agg_rth_flag
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS AS src
    GROUP BY
        src.trade_date,
        roll_ts_bucket,
        roll_ts_et,
        src.symbol,
        src.price
)
SELECT
    trade_date AS trade_date,
    roll_ts_bucket AS ts_bucket,
    roll_ts_et AS ts_et,
    symbol AS symbol,
    price AS price,

    agg_bid_add_size AS bid_add_size,
    agg_ask_add_size AS ask_add_size,
    agg_bid_cancel_size AS bid_cancel_size,
    agg_ask_cancel_size AS ask_cancel_size,
    agg_bid_modify_size AS bid_modify_size,
    agg_ask_modify_size AS ask_modify_size,
    agg_bid_trade_size AS bid_trade_size,
    agg_ask_trade_size AS ask_trade_size,

    agg_bid_event_count AS bid_event_count,
    agg_ask_event_count AS ask_event_count,
    agg_total_event_count AS total_event_count,

    agg_bid_liquidity_event_size AS bid_liquidity_event_size,
    agg_ask_liquidity_event_size AS ask_liquidity_event_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_net_liquidity_event_delta AS net_liquidity_event_delta,

    agg_heatmap_proxy_value AS heatmap_proxy_value,
    agg_max_heatmap_proxy_value AS max_heatmap_proxy_value,
    agg_avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
    agg_persistence_bucket_count AS persistence_bucket_count,
    agg_rth_flag AS rth_flag
FROM grouped;

--------------------------------------------------------------------------------
-- 30S HEATMAP / LIQUIDITY-EVENT-PROXY ROLLUP
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S;

CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH grouped AS
(
    SELECT
        src.trade_date AS trade_date,
        toStartOfInterval(src.ts_bucket, toIntervalSecond(30)) AS roll_ts_bucket,
        toStartOfInterval(src.ts_et, toIntervalSecond(30)) AS roll_ts_et,
        src.symbol AS symbol,
        src.price AS price,

        sum(src.bid_add_size) AS agg_bid_add_size,
        sum(src.ask_add_size) AS agg_ask_add_size,
        sum(src.bid_cancel_size) AS agg_bid_cancel_size,
        sum(src.ask_cancel_size) AS agg_ask_cancel_size,
        sum(src.bid_modify_size) AS agg_bid_modify_size,
        sum(src.ask_modify_size) AS agg_ask_modify_size,
        sum(src.bid_trade_size) AS agg_bid_trade_size,
        sum(src.ask_trade_size) AS agg_ask_trade_size,

        sum(src.bid_event_count) AS agg_bid_event_count,
        sum(src.ask_event_count) AS agg_ask_event_count,
        sum(src.total_event_count) AS agg_total_event_count,

        sum(src.bid_liquidity_event_size) AS agg_bid_liquidity_event_size,
        sum(src.ask_liquidity_event_size) AS agg_ask_liquidity_event_size,
        sum(src.total_liquidity_event_size) AS agg_total_liquidity_event_size,
        sum(src.net_liquidity_event_delta) AS agg_net_liquidity_event_delta,

        max(src.heatmap_proxy_value) AS agg_heatmap_proxy_value,
        max(src.heatmap_proxy_value) AS agg_max_heatmap_proxy_value,
        avg(src.heatmap_proxy_value) AS agg_avg_heatmap_proxy_value,
        count() AS agg_persistence_bucket_count,
        max(src.rth_flag) AS agg_rth_flag
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS AS src
    GROUP BY
        src.trade_date,
        roll_ts_bucket,
        roll_ts_et,
        src.symbol,
        src.price
)
SELECT
    trade_date AS trade_date,
    roll_ts_bucket AS ts_bucket,
    roll_ts_et AS ts_et,
    symbol AS symbol,
    price AS price,

    agg_bid_add_size AS bid_add_size,
    agg_ask_add_size AS ask_add_size,
    agg_bid_cancel_size AS bid_cancel_size,
    agg_ask_cancel_size AS ask_cancel_size,
    agg_bid_modify_size AS bid_modify_size,
    agg_ask_modify_size AS ask_modify_size,
    agg_bid_trade_size AS bid_trade_size,
    agg_ask_trade_size AS ask_trade_size,

    agg_bid_event_count AS bid_event_count,
    agg_ask_event_count AS ask_event_count,
    agg_total_event_count AS total_event_count,

    agg_bid_liquidity_event_size AS bid_liquidity_event_size,
    agg_ask_liquidity_event_size AS ask_liquidity_event_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_net_liquidity_event_delta AS net_liquidity_event_delta,

    agg_heatmap_proxy_value AS heatmap_proxy_value,
    agg_max_heatmap_proxy_value AS max_heatmap_proxy_value,
    agg_avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
    agg_persistence_bucket_count AS persistence_bucket_count,
    agg_rth_flag AS rth_flag
FROM grouped;

--------------------------------------------------------------------------------
-- 1M HEATMAP / LIQUIDITY-EVENT-PROXY ROLLUP
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M;

CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH grouped AS
(
    SELECT
        src.trade_date AS trade_date,
        toStartOfInterval(src.ts_bucket, toIntervalMinute(1)) AS roll_ts_bucket,
        toStartOfInterval(src.ts_et, toIntervalMinute(1)) AS roll_ts_et,
        src.symbol AS symbol,
        src.price AS price,

        sum(src.bid_add_size) AS agg_bid_add_size,
        sum(src.ask_add_size) AS agg_ask_add_size,
        sum(src.bid_cancel_size) AS agg_bid_cancel_size,
        sum(src.ask_cancel_size) AS agg_ask_cancel_size,
        sum(src.bid_modify_size) AS agg_bid_modify_size,
        sum(src.ask_modify_size) AS agg_ask_modify_size,
        sum(src.bid_trade_size) AS agg_bid_trade_size,
        sum(src.ask_trade_size) AS agg_ask_trade_size,

        sum(src.bid_event_count) AS agg_bid_event_count,
        sum(src.ask_event_count) AS agg_ask_event_count,
        sum(src.total_event_count) AS agg_total_event_count,

        sum(src.bid_liquidity_event_size) AS agg_bid_liquidity_event_size,
        sum(src.ask_liquidity_event_size) AS agg_ask_liquidity_event_size,
        sum(src.total_liquidity_event_size) AS agg_total_liquidity_event_size,
        sum(src.net_liquidity_event_delta) AS agg_net_liquidity_event_delta,

        max(src.heatmap_proxy_value) AS agg_heatmap_proxy_value,
        max(src.heatmap_proxy_value) AS agg_max_heatmap_proxy_value,
        avg(src.heatmap_proxy_value) AS agg_avg_heatmap_proxy_value,
        count() AS agg_persistence_bucket_count,
        max(src.rth_flag) AS agg_rth_flag
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS AS src
    GROUP BY
        src.trade_date,
        roll_ts_bucket,
        roll_ts_et,
        src.symbol,
        src.price
)
SELECT
    trade_date AS trade_date,
    roll_ts_bucket AS ts_bucket,
    roll_ts_et AS ts_et,
    symbol AS symbol,
    price AS price,

    agg_bid_add_size AS bid_add_size,
    agg_ask_add_size AS ask_add_size,
    agg_bid_cancel_size AS bid_cancel_size,
    agg_ask_cancel_size AS ask_cancel_size,
    agg_bid_modify_size AS bid_modify_size,
    agg_ask_modify_size AS ask_modify_size,
    agg_bid_trade_size AS bid_trade_size,
    agg_ask_trade_size AS ask_trade_size,

    agg_bid_event_count AS bid_event_count,
    agg_ask_event_count AS ask_event_count,
    agg_total_event_count AS total_event_count,

    agg_bid_liquidity_event_size AS bid_liquidity_event_size,
    agg_ask_liquidity_event_size AS ask_liquidity_event_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_net_liquidity_event_delta AS net_liquidity_event_delta,

    agg_heatmap_proxy_value AS heatmap_proxy_value,
    agg_max_heatmap_proxy_value AS max_heatmap_proxy_value,
    agg_avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
    agg_persistence_bucket_count AS persistence_bucket_count,
    agg_rth_flag AS rth_flag
FROM grouped;

--------------------------------------------------------------------------------
-- 5M HEATMAP / LIQUIDITY-EVENT-PROXY ROLLUP
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M;

CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH grouped AS
(
    SELECT
        src.trade_date AS trade_date,
        toStartOfInterval(src.ts_bucket, toIntervalMinute(5)) AS roll_ts_bucket,
        toStartOfInterval(src.ts_et, toIntervalMinute(5)) AS roll_ts_et,
        src.symbol AS symbol,
        src.price AS price,

        sum(src.bid_add_size) AS agg_bid_add_size,
        sum(src.ask_add_size) AS agg_ask_add_size,
        sum(src.bid_cancel_size) AS agg_bid_cancel_size,
        sum(src.ask_cancel_size) AS agg_ask_cancel_size,
        sum(src.bid_modify_size) AS agg_bid_modify_size,
        sum(src.ask_modify_size) AS agg_ask_modify_size,
        sum(src.bid_trade_size) AS agg_bid_trade_size,
        sum(src.ask_trade_size) AS agg_ask_trade_size,

        sum(src.bid_event_count) AS agg_bid_event_count,
        sum(src.ask_event_count) AS agg_ask_event_count,
        sum(src.total_event_count) AS agg_total_event_count,

        sum(src.bid_liquidity_event_size) AS agg_bid_liquidity_event_size,
        sum(src.ask_liquidity_event_size) AS agg_ask_liquidity_event_size,
        sum(src.total_liquidity_event_size) AS agg_total_liquidity_event_size,
        sum(src.net_liquidity_event_delta) AS agg_net_liquidity_event_delta,

        max(src.heatmap_proxy_value) AS agg_heatmap_proxy_value,
        max(src.heatmap_proxy_value) AS agg_max_heatmap_proxy_value,
        avg(src.heatmap_proxy_value) AS agg_avg_heatmap_proxy_value,
        count() AS agg_persistence_bucket_count,
        max(src.rth_flag) AS agg_rth_flag
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS AS src
    GROUP BY
        src.trade_date,
        roll_ts_bucket,
        roll_ts_et,
        src.symbol,
        src.price
)
SELECT
    trade_date AS trade_date,
    roll_ts_bucket AS ts_bucket,
    roll_ts_et AS ts_et,
    symbol AS symbol,
    price AS price,

    agg_bid_add_size AS bid_add_size,
    agg_ask_add_size AS ask_add_size,
    agg_bid_cancel_size AS bid_cancel_size,
    agg_ask_cancel_size AS ask_cancel_size,
    agg_bid_modify_size AS bid_modify_size,
    agg_ask_modify_size AS ask_modify_size,
    agg_bid_trade_size AS bid_trade_size,
    agg_ask_trade_size AS ask_trade_size,

    agg_bid_event_count AS bid_event_count,
    agg_ask_event_count AS ask_event_count,
    agg_total_event_count AS total_event_count,

    agg_bid_liquidity_event_size AS bid_liquidity_event_size,
    agg_ask_liquidity_event_size AS ask_liquidity_event_size,
    agg_total_liquidity_event_size AS total_liquidity_event_size,
    agg_net_liquidity_event_delta AS net_liquidity_event_delta,

    agg_heatmap_proxy_value AS heatmap_proxy_value,
    agg_max_heatmap_proxy_value AS max_heatmap_proxy_value,
    agg_avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
    agg_persistence_bucket_count AS persistence_bucket_count,
    agg_rth_flag AS rth_flag
FROM grouped;

--------------------------------------------------------------------------------
-- 1S FRAME SOURCE
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_FRAME_SOURCE_1S;

CREATE TABLE BM_MNQ_FRAME_SOURCE_1S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
SELECT
    h.trade_date AS trade_date,
    h.ts_bucket AS ts_bucket,
    h.ts_et AS ts_et,
    h.symbol AS symbol,
    h.price AS price,

    h.bid_add_size AS bid_add_size,
    h.ask_add_size AS ask_add_size,
    h.bid_cancel_size AS bid_cancel_size,
    h.ask_cancel_size AS ask_cancel_size,
    h.bid_modify_size AS bid_modify_size,
    h.ask_modify_size AS ask_modify_size,
    h.bid_trade_size AS bid_trade_size,
    h.ask_trade_size AS ask_trade_size,

    h.bid_event_count AS bid_event_count,
    h.ask_event_count AS ask_event_count,
    h.total_event_count AS total_event_count,

    h.bid_liquidity_event_size AS bid_liquidity_event_size,
    h.ask_liquidity_event_size AS ask_liquidity_event_size,
    h.total_liquidity_event_size AS total_liquidity_event_size,
    h.net_liquidity_event_delta AS net_liquidity_event_delta,

    h.heatmap_proxy_value AS heatmap_proxy_value,
    h.max_heatmap_proxy_value AS max_heatmap_proxy_value,
    h.avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
    h.persistence_bucket_count AS persistence_bucket_count,

    d.daily_max_heatmap_proxy_value AS daily_max_heatmap_proxy_value,
    d.daily_p999_heatmap_proxy_value AS daily_p999_heatmap_proxy_value,

    if
    (
        d.daily_max_heatmap_proxy_value <= 0,
        0,
        log(1 + h.heatmap_proxy_value) / log(1 + d.daily_max_heatmap_proxy_value)
    ) AS heatmap_intensity,

    if
    (
        h.bid_liquidity_event_size >= d.daily_p999_heatmap_proxy_value
        AND h.bid_liquidity_event_size > h.ask_liquidity_event_size,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        h.ask_liquidity_event_size >= d.daily_p999_heatmap_proxy_value
        AND h.ask_liquidity_event_size > h.bid_liquidity_event_size,
        1,
        0
    ) AS is_ask_liquidity_event_wall,

    ifNull(a.buy_exec_size, 0) AS buy_exec_size,
    ifNull(a.sell_exec_size, 0) AS sell_exec_size,
    ifNull(a.total_exec_size, 0) AS total_exec_size,
    ifNull(a.exec_delta, 0) AS exec_delta,
    ifNull(a.exec_imbalance, 0) AS exec_imbalance,
    ifNull(a.buy_trade_count, 0) AS buy_trade_count,
    ifNull(a.sell_trade_count, 0) AS sell_trade_count,
    ifNull(a.total_trade_count, 0) AS total_trade_count,
    ifNull(a.bubble_total_size, 0) AS bubble_total_size,
    ifNull(a.bubble_buy_share, 0) AS bubble_buy_share,
    ifNull(a.bubble_sell_share, 0) AS bubble_sell_share,
    ifNull(a.bubble_side, 'NONE') AS bubble_side,

    greatest(h.rth_flag, ifNull(a.rth_flag, 0)) AS rth_flag
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S AS h
LEFT JOIN BM_MNQ_AGGRESSION_EXECUTIONS_1S AS a
    ON  h.trade_date = a.trade_date
    AND h.ts_bucket  = a.ts_bucket
    AND h.symbol     = a.symbol
    AND h.price      = a.price
LEFT JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON h.trade_date = d.trade_date;

--------------------------------------------------------------------------------
-- 5S FRAME SOURCE
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_FRAME_SOURCE_5S;

CREATE TABLE BM_MNQ_FRAME_SOURCE_5S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
SELECT
    h.trade_date AS trade_date,
    h.ts_bucket AS ts_bucket,
    h.ts_et AS ts_et,
    h.symbol AS symbol,
    h.price AS price,

    h.bid_add_size AS bid_add_size,
    h.ask_add_size AS ask_add_size,
    h.bid_cancel_size AS bid_cancel_size,
    h.ask_cancel_size AS ask_cancel_size,
    h.bid_modify_size AS bid_modify_size,
    h.ask_modify_size AS ask_modify_size,
    h.bid_trade_size AS bid_trade_size,
    h.ask_trade_size AS ask_trade_size,

    h.bid_event_count AS bid_event_count,
    h.ask_event_count AS ask_event_count,
    h.total_event_count AS total_event_count,

    h.bid_liquidity_event_size AS bid_liquidity_event_size,
    h.ask_liquidity_event_size AS ask_liquidity_event_size,
    h.total_liquidity_event_size AS total_liquidity_event_size,
    h.net_liquidity_event_delta AS net_liquidity_event_delta,

    h.heatmap_proxy_value AS heatmap_proxy_value,
    h.max_heatmap_proxy_value AS max_heatmap_proxy_value,
    h.avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
    h.persistence_bucket_count AS persistence_bucket_count,

    d.daily_max_heatmap_proxy_value AS daily_max_heatmap_proxy_value,
    d.daily_p999_heatmap_proxy_value AS daily_p999_heatmap_proxy_value,

    if
    (
        d.daily_max_heatmap_proxy_value <= 0,
        0,
        log(1 + h.heatmap_proxy_value) / log(1 + d.daily_max_heatmap_proxy_value)
    ) AS heatmap_intensity,

    if
    (
        h.bid_liquidity_event_size >= d.daily_p999_heatmap_proxy_value
        AND h.bid_liquidity_event_size > h.ask_liquidity_event_size,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        h.ask_liquidity_event_size >= d.daily_p999_heatmap_proxy_value
        AND h.ask_liquidity_event_size > h.bid_liquidity_event_size,
        1,
        0
    ) AS is_ask_liquidity_event_wall,

    ifNull(a.buy_exec_size, 0) AS buy_exec_size,
    ifNull(a.sell_exec_size, 0) AS sell_exec_size,
    ifNull(a.total_exec_size, 0) AS total_exec_size,
    ifNull(a.exec_delta, 0) AS exec_delta,
    ifNull(a.exec_imbalance, 0) AS exec_imbalance,
    ifNull(a.buy_trade_count, 0) AS buy_trade_count,
    ifNull(a.sell_trade_count, 0) AS sell_trade_count,
    ifNull(a.total_trade_count, 0) AS total_trade_count,
    ifNull(a.bubble_total_size, 0) AS bubble_total_size,
    ifNull(a.bubble_buy_share, 0) AS bubble_buy_share,
    ifNull(a.bubble_sell_share, 0) AS bubble_sell_share,
    ifNull(a.bubble_side, 'NONE') AS bubble_side,

    greatest(h.rth_flag, ifNull(a.rth_flag, 0)) AS rth_flag
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S AS h
LEFT JOIN BM_MNQ_AGGRESSION_EXECUTIONS_5S AS a
    ON  h.trade_date = a.trade_date
    AND h.ts_bucket  = a.ts_bucket
    AND h.symbol     = a.symbol
    AND h.price      = a.price
LEFT JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON h.trade_date = d.trade_date;

--------------------------------------------------------------------------------
-- 30S FRAME SOURCE
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_FRAME_SOURCE_30S;

CREATE TABLE BM_MNQ_FRAME_SOURCE_30S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
SELECT
    h.trade_date AS trade_date,
    h.ts_bucket AS ts_bucket,
    h.ts_et AS ts_et,
    h.symbol AS symbol,
    h.price AS price,

    h.bid_add_size AS bid_add_size,
    h.ask_add_size AS ask_add_size,
    h.bid_cancel_size AS bid_cancel_size,
    h.ask_cancel_size AS ask_cancel_size,
    h.bid_modify_size AS bid_modify_size,
    h.ask_modify_size AS ask_modify_size,
    h.bid_trade_size AS bid_trade_size,
    h.ask_trade_size AS ask_trade_size,

    h.bid_event_count AS bid_event_count,
    h.ask_event_count AS ask_event_count,
    h.total_event_count AS total_event_count,

    h.bid_liquidity_event_size AS bid_liquidity_event_size,
    h.ask_liquidity_event_size AS ask_liquidity_event_size,
    h.total_liquidity_event_size AS total_liquidity_event_size,
    h.net_liquidity_event_delta AS net_liquidity_event_delta,

    h.heatmap_proxy_value AS heatmap_proxy_value,
    h.max_heatmap_proxy_value AS max_heatmap_proxy_value,
    h.avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
    h.persistence_bucket_count AS persistence_bucket_count,

    d.daily_max_heatmap_proxy_value AS daily_max_heatmap_proxy_value,
    d.daily_p999_heatmap_proxy_value AS daily_p999_heatmap_proxy_value,

    if
    (
        d.daily_max_heatmap_proxy_value <= 0,
        0,
        log(1 + h.heatmap_proxy_value) / log(1 + d.daily_max_heatmap_proxy_value)
    ) AS heatmap_intensity,

    if
    (
        h.bid_liquidity_event_size >= d.daily_p999_heatmap_proxy_value
        AND h.bid_liquidity_event_size > h.ask_liquidity_event_size,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        h.ask_liquidity_event_size >= d.daily_p999_heatmap_proxy_value
        AND h.ask_liquidity_event_size > h.bid_liquidity_event_size,
        1,
        0
    ) AS is_ask_liquidity_event_wall,

    ifNull(a.buy_exec_size, 0) AS buy_exec_size,
    ifNull(a.sell_exec_size, 0) AS sell_exec_size,
    ifNull(a.total_exec_size, 0) AS total_exec_size,
    ifNull(a.exec_delta, 0) AS exec_delta,
    ifNull(a.exec_imbalance, 0) AS exec_imbalance,
    ifNull(a.buy_trade_count, 0) AS buy_trade_count,
    ifNull(a.sell_trade_count, 0) AS sell_trade_count,
    ifNull(a.total_trade_count, 0) AS total_trade_count,
    ifNull(a.bubble_total_size, 0) AS bubble_total_size,
    ifNull(a.bubble_buy_share, 0) AS bubble_buy_share,
    ifNull(a.bubble_sell_share, 0) AS bubble_sell_share,
    ifNull(a.bubble_side, 'NONE') AS bubble_side,

    greatest(h.rth_flag, ifNull(a.rth_flag, 0)) AS rth_flag
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S AS h
LEFT JOIN BM_MNQ_AGGRESSION_EXECUTIONS_30S AS a
    ON  h.trade_date = a.trade_date
    AND h.ts_bucket  = a.ts_bucket
    AND h.symbol     = a.symbol
    AND h.price      = a.price
LEFT JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON h.trade_date = d.trade_date;

--------------------------------------------------------------------------------
-- 1M FRAME SOURCE
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_FRAME_SOURCE_1M;

CREATE TABLE BM_MNQ_FRAME_SOURCE_1M
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
SELECT
    h.trade_date AS trade_date,
    h.ts_bucket AS ts_bucket,
    h.ts_et AS ts_et,
    h.symbol AS symbol,
    h.price AS price,

    h.bid_add_size AS bid_add_size,
    h.ask_add_size AS ask_add_size,
    h.bid_cancel_size AS bid_cancel_size,
    h.ask_cancel_size AS ask_cancel_size,
    h.bid_modify_size AS bid_modify_size,
    h.ask_modify_size AS ask_modify_size,
    h.bid_trade_size AS bid_trade_size,
    h.ask_trade_size AS ask_trade_size,

    h.bid_event_count AS bid_event_count,
    h.ask_event_count AS ask_event_count,
    h.total_event_count AS total_event_count,

    h.bid_liquidity_event_size AS bid_liquidity_event_size,
    h.ask_liquidity_event_size AS ask_liquidity_event_size,
    h.total_liquidity_event_size AS total_liquidity_event_size,
    h.net_liquidity_event_delta AS net_liquidity_event_delta,

    h.heatmap_proxy_value AS heatmap_proxy_value,
    h.max_heatmap_proxy_value AS max_heatmap_proxy_value,
    h.avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
    h.persistence_bucket_count AS persistence_bucket_count,

    d.daily_max_heatmap_proxy_value AS daily_max_heatmap_proxy_value,
    d.daily_p999_heatmap_proxy_value AS daily_p999_heatmap_proxy_value,

    if
    (
        d.daily_max_heatmap_proxy_value <= 0,
        0,
        log(1 + h.heatmap_proxy_value) / log(1 + d.daily_max_heatmap_proxy_value)
    ) AS heatmap_intensity,

    if
    (
        h.bid_liquidity_event_size >= d.daily_p999_heatmap_proxy_value
        AND h.bid_liquidity_event_size > h.ask_liquidity_event_size,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        h.ask_liquidity_event_size >= d.daily_p999_heatmap_proxy_value
        AND h.ask_liquidity_event_size > h.bid_liquidity_event_size,
        1,
        0
    ) AS is_ask_liquidity_event_wall,

    ifNull(a.buy_exec_size, 0) AS buy_exec_size,
    ifNull(a.sell_exec_size, 0) AS sell_exec_size,
    ifNull(a.total_exec_size, 0) AS total_exec_size,
    ifNull(a.exec_delta, 0) AS exec_delta,
    ifNull(a.exec_imbalance, 0) AS exec_imbalance,
    ifNull(a.buy_trade_count, 0) AS buy_trade_count,
    ifNull(a.sell_trade_count, 0) AS sell_trade_count,
    ifNull(a.total_trade_count, 0) AS total_trade_count,
    ifNull(a.bubble_total_size, 0) AS bubble_total_size,
    ifNull(a.bubble_buy_share, 0) AS bubble_buy_share,
    ifNull(a.bubble_sell_share, 0) AS bubble_sell_share,
    ifNull(a.bubble_side, 'NONE') AS bubble_side,

    greatest(h.rth_flag, ifNull(a.rth_flag, 0)) AS rth_flag
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M AS h
LEFT JOIN BM_MNQ_AGGRESSION_EXECUTIONS_1M AS a
    ON  h.trade_date = a.trade_date
    AND h.ts_bucket  = a.ts_bucket
    AND h.symbol     = a.symbol
    AND h.price      = a.price
LEFT JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON h.trade_date = d.trade_date;

--------------------------------------------------------------------------------
-- 5M FRAME SOURCE
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_FRAME_SOURCE_5M;

CREATE TABLE BM_MNQ_FRAME_SOURCE_5M
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
SELECT
    h.trade_date AS trade_date,
    h.ts_bucket AS ts_bucket,
    h.ts_et AS ts_et,
    h.symbol AS symbol,
    h.price AS price,

    h.bid_add_size AS bid_add_size,
    h.ask_add_size AS ask_add_size,
    h.bid_cancel_size AS bid_cancel_size,
    h.ask_cancel_size AS ask_cancel_size,
    h.bid_modify_size AS bid_modify_size,
    h.ask_modify_size AS ask_modify_size,
    h.bid_trade_size AS bid_trade_size,
    h.ask_trade_size AS ask_trade_size,

    h.bid_event_count AS bid_event_count,
    h.ask_event_count AS ask_event_count,
    h.total_event_count AS total_event_count,

    h.bid_liquidity_event_size AS bid_liquidity_event_size,
    h.ask_liquidity_event_size AS ask_liquidity_event_size,
    h.total_liquidity_event_size AS total_liquidity_event_size,
    h.net_liquidity_event_delta AS net_liquidity_event_delta,

    h.heatmap_proxy_value AS heatmap_proxy_value,
    h.max_heatmap_proxy_value AS max_heatmap_proxy_value,
    h.avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
    h.persistence_bucket_count AS persistence_bucket_count,

    d.daily_max_heatmap_proxy_value AS daily_max_heatmap_proxy_value,
    d.daily_p999_heatmap_proxy_value AS daily_p999_heatmap_proxy_value,

    if
    (
        d.daily_max_heatmap_proxy_value <= 0,
        0,
        log(1 + h.heatmap_proxy_value) / log(1 + d.daily_max_heatmap_proxy_value)
    ) AS heatmap_intensity,

    if
    (
        h.bid_liquidity_event_size >= d.daily_p999_heatmap_proxy_value
        AND h.bid_liquidity_event_size > h.ask_liquidity_event_size,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        h.ask_liquidity_event_size >= d.daily_p999_heatmap_proxy_value
        AND h.ask_liquidity_event_size > h.bid_liquidity_event_size,
        1,
        0
    ) AS is_ask_liquidity_event_wall,

    ifNull(a.buy_exec_size, 0) AS buy_exec_size,
    ifNull(a.sell_exec_size, 0) AS sell_exec_size,
    ifNull(a.total_exec_size, 0) AS total_exec_size,
    ifNull(a.exec_delta, 0) AS exec_delta,
    ifNull(a.exec_imbalance, 0) AS exec_imbalance,
    ifNull(a.buy_trade_count, 0) AS buy_trade_count,
    ifNull(a.sell_trade_count, 0) AS sell_trade_count,
    ifNull(a.total_trade_count, 0) AS total_trade_count,
    ifNull(a.bubble_total_size, 0) AS bubble_total_size,
    ifNull(a.bubble_buy_share, 0) AS bubble_buy_share,
    ifNull(a.bubble_sell_share, 0) AS bubble_sell_share,
    ifNull(a.bubble_side, 'NONE') AS bubble_side,

    greatest(h.rth_flag, ifNull(a.rth_flag, 0)) AS rth_flag
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M AS h
LEFT JOIN BM_MNQ_AGGRESSION_EXECUTIONS_5M AS a
    ON  h.trade_date = a.trade_date
    AND h.ts_bucket  = a.ts_bucket
    AND h.symbol     = a.symbol
    AND h.price      = a.price
LEFT JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON h.trade_date = d.trade_date;
