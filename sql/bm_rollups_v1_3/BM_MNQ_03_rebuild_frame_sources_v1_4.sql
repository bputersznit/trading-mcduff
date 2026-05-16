-- BM_MNQ_03_rebuild_frame_sources_v1_4.sql
-- Generated: 2026-05-10 13:40:00 America/New_York
--
-- Project: Bookmap Emulation / MNQ
--
-- Purpose:
--   Repair the BM_MNQ_FRAME_SOURCE_* tables after the v1.3 rollup build.
--
-- Why this repair is needed:
--   v1.3 successfully built the aggression and heatmap rollup ladders, but the
--   frame-source QA showed total_exec_size = 0 in all frame tables.
--
-- Root cause:
--   The frame source used the heatmap table as the left side and joined
--   aggression by exact (trade_date, ts_bucket, symbol, price). When a trade
--   occurred at a price/time that did not have a matching heatmap proxy row, the
--   aggression bubble row was lost.
--
-- Repair:
--   Build a unified keyset from:
--       BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_<scale>
--       UNION DISTINCT
--       BM_MNQ_AGGRESSION_EXECUTIONS_<scale>
--
--   Then left join both domains into that keyset.
--
-- Also repaired:
--   heatmap_intensity is clamped to [0, 1] with least(1, ...), because the daily
--   max table is RTH-based while the frame source may include non-RTH rows.
--
-- Run after successful v1.3 rollups:
--   clickhouse-client --multiquery < BM_MNQ_03_rebuild_frame_sources_v1_4.sql

SET max_threads = 16;
SET max_memory_usage = 0;
SET max_bytes_before_external_group_by = 20000000000;
SET max_bytes_before_external_sort = 20000000000;
SET optimize_move_to_prewhere = 1;


--------------------------------------------------------------------------------
-- REBUILD FRAME SOURCE 1S
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_FRAME_SOURCE_1S;

CREATE TABLE BM_MNQ_FRAME_SOURCE_1S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH keys AS
(
    SELECT
        trade_date AS trade_date,
        ts_bucket AS ts_bucket,
        ts_et AS ts_et,
        symbol AS symbol,
        price AS price
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S

    UNION DISTINCT

    SELECT
        trade_date AS trade_date,
        ts_bucket AS ts_bucket,
        ts_et AS ts_et,
        symbol AS symbol,
        price AS price
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_1S
)
SELECT
    k.trade_date AS trade_date,
    k.ts_bucket AS ts_bucket,
    k.ts_et AS ts_et,
    k.symbol AS symbol,
    k.price AS price,

    ifNull(h.bid_add_size, 0) AS bid_add_size,
    ifNull(h.ask_add_size, 0) AS ask_add_size,
    ifNull(h.bid_cancel_size, 0) AS bid_cancel_size,
    ifNull(h.ask_cancel_size, 0) AS ask_cancel_size,
    ifNull(h.bid_modify_size, 0) AS bid_modify_size,
    ifNull(h.ask_modify_size, 0) AS ask_modify_size,
    ifNull(h.bid_trade_size, 0) AS bid_trade_size,
    ifNull(h.ask_trade_size, 0) AS ask_trade_size,

    ifNull(h.bid_event_count, 0) AS bid_event_count,
    ifNull(h.ask_event_count, 0) AS ask_event_count,
    ifNull(h.total_event_count, 0) AS total_event_count,

    ifNull(h.bid_liquidity_event_size, 0) AS bid_liquidity_event_size,
    ifNull(h.ask_liquidity_event_size, 0) AS ask_liquidity_event_size,
    ifNull(h.total_liquidity_event_size, 0) AS total_liquidity_event_size,
    ifNull(h.net_liquidity_event_delta, 0) AS net_liquidity_event_delta,

    ifNull(h.heatmap_proxy_value, 0) AS heatmap_proxy_value,
    ifNull(h.max_heatmap_proxy_value, 0) AS max_heatmap_proxy_value,
    ifNull(h.avg_heatmap_proxy_value, 0) AS avg_heatmap_proxy_value,
    ifNull(h.persistence_bucket_count, 0) AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    least
    (
        1,
        if
        (
            ifNull(d.daily_max_heatmap_proxy_value, 0) <= 0,
            0,
            log(1 + ifNull(h.heatmap_proxy_value, 0)) / log(1 + d.daily_max_heatmap_proxy_value)
        )
    ) AS heatmap_intensity,

    if
    (
        ifNull(h.rth_flag, 0) = 1
        AND ifNull(h.bid_liquidity_event_size, 0) >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND ifNull(h.bid_liquidity_event_size, 0) > ifNull(h.ask_liquidity_event_size, 0)
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        ifNull(h.rth_flag, 0) = 1
        AND ifNull(h.ask_liquidity_event_size, 0) >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND ifNull(h.ask_liquidity_event_size, 0) > ifNull(h.bid_liquidity_event_size, 0)
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
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

    greatest(ifNull(h.rth_flag, 0), ifNull(a.rth_flag, 0)) AS rth_flag
FROM keys AS k
LEFT JOIN BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S AS h
    ON  k.trade_date = h.trade_date
    AND k.ts_bucket  = h.ts_bucket
    AND k.symbol     = h.symbol
    AND k.price      = h.price
LEFT JOIN BM_MNQ_AGGRESSION_EXECUTIONS_1S AS a
    ON  k.trade_date = a.trade_date
    AND k.ts_bucket  = a.ts_bucket
    AND k.symbol     = a.symbol
    AND k.price      = a.price
LEFT JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON k.trade_date = d.trade_date;

--------------------------------------------------------------------------------
-- REBUILD FRAME SOURCE 5S
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_FRAME_SOURCE_5S;

CREATE TABLE BM_MNQ_FRAME_SOURCE_5S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH keys AS
(
    SELECT
        trade_date AS trade_date,
        ts_bucket AS ts_bucket,
        ts_et AS ts_et,
        symbol AS symbol,
        price AS price
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S

    UNION DISTINCT

    SELECT
        trade_date AS trade_date,
        ts_bucket AS ts_bucket,
        ts_et AS ts_et,
        symbol AS symbol,
        price AS price
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_5S
)
SELECT
    k.trade_date AS trade_date,
    k.ts_bucket AS ts_bucket,
    k.ts_et AS ts_et,
    k.symbol AS symbol,
    k.price AS price,

    ifNull(h.bid_add_size, 0) AS bid_add_size,
    ifNull(h.ask_add_size, 0) AS ask_add_size,
    ifNull(h.bid_cancel_size, 0) AS bid_cancel_size,
    ifNull(h.ask_cancel_size, 0) AS ask_cancel_size,
    ifNull(h.bid_modify_size, 0) AS bid_modify_size,
    ifNull(h.ask_modify_size, 0) AS ask_modify_size,
    ifNull(h.bid_trade_size, 0) AS bid_trade_size,
    ifNull(h.ask_trade_size, 0) AS ask_trade_size,

    ifNull(h.bid_event_count, 0) AS bid_event_count,
    ifNull(h.ask_event_count, 0) AS ask_event_count,
    ifNull(h.total_event_count, 0) AS total_event_count,

    ifNull(h.bid_liquidity_event_size, 0) AS bid_liquidity_event_size,
    ifNull(h.ask_liquidity_event_size, 0) AS ask_liquidity_event_size,
    ifNull(h.total_liquidity_event_size, 0) AS total_liquidity_event_size,
    ifNull(h.net_liquidity_event_delta, 0) AS net_liquidity_event_delta,

    ifNull(h.heatmap_proxy_value, 0) AS heatmap_proxy_value,
    ifNull(h.max_heatmap_proxy_value, 0) AS max_heatmap_proxy_value,
    ifNull(h.avg_heatmap_proxy_value, 0) AS avg_heatmap_proxy_value,
    ifNull(h.persistence_bucket_count, 0) AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    least
    (
        1,
        if
        (
            ifNull(d.daily_max_heatmap_proxy_value, 0) <= 0,
            0,
            log(1 + ifNull(h.heatmap_proxy_value, 0)) / log(1 + d.daily_max_heatmap_proxy_value)
        )
    ) AS heatmap_intensity,

    if
    (
        ifNull(h.rth_flag, 0) = 1
        AND ifNull(h.bid_liquidity_event_size, 0) >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND ifNull(h.bid_liquidity_event_size, 0) > ifNull(h.ask_liquidity_event_size, 0)
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        ifNull(h.rth_flag, 0) = 1
        AND ifNull(h.ask_liquidity_event_size, 0) >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND ifNull(h.ask_liquidity_event_size, 0) > ifNull(h.bid_liquidity_event_size, 0)
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
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

    greatest(ifNull(h.rth_flag, 0), ifNull(a.rth_flag, 0)) AS rth_flag
FROM keys AS k
LEFT JOIN BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S AS h
    ON  k.trade_date = h.trade_date
    AND k.ts_bucket  = h.ts_bucket
    AND k.symbol     = h.symbol
    AND k.price      = h.price
LEFT JOIN BM_MNQ_AGGRESSION_EXECUTIONS_5S AS a
    ON  k.trade_date = a.trade_date
    AND k.ts_bucket  = a.ts_bucket
    AND k.symbol     = a.symbol
    AND k.price      = a.price
LEFT JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON k.trade_date = d.trade_date;

--------------------------------------------------------------------------------
-- REBUILD FRAME SOURCE 30S
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_FRAME_SOURCE_30S;

CREATE TABLE BM_MNQ_FRAME_SOURCE_30S
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH keys AS
(
    SELECT
        trade_date AS trade_date,
        ts_bucket AS ts_bucket,
        ts_et AS ts_et,
        symbol AS symbol,
        price AS price
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S

    UNION DISTINCT

    SELECT
        trade_date AS trade_date,
        ts_bucket AS ts_bucket,
        ts_et AS ts_et,
        symbol AS symbol,
        price AS price
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_30S
)
SELECT
    k.trade_date AS trade_date,
    k.ts_bucket AS ts_bucket,
    k.ts_et AS ts_et,
    k.symbol AS symbol,
    k.price AS price,

    ifNull(h.bid_add_size, 0) AS bid_add_size,
    ifNull(h.ask_add_size, 0) AS ask_add_size,
    ifNull(h.bid_cancel_size, 0) AS bid_cancel_size,
    ifNull(h.ask_cancel_size, 0) AS ask_cancel_size,
    ifNull(h.bid_modify_size, 0) AS bid_modify_size,
    ifNull(h.ask_modify_size, 0) AS ask_modify_size,
    ifNull(h.bid_trade_size, 0) AS bid_trade_size,
    ifNull(h.ask_trade_size, 0) AS ask_trade_size,

    ifNull(h.bid_event_count, 0) AS bid_event_count,
    ifNull(h.ask_event_count, 0) AS ask_event_count,
    ifNull(h.total_event_count, 0) AS total_event_count,

    ifNull(h.bid_liquidity_event_size, 0) AS bid_liquidity_event_size,
    ifNull(h.ask_liquidity_event_size, 0) AS ask_liquidity_event_size,
    ifNull(h.total_liquidity_event_size, 0) AS total_liquidity_event_size,
    ifNull(h.net_liquidity_event_delta, 0) AS net_liquidity_event_delta,

    ifNull(h.heatmap_proxy_value, 0) AS heatmap_proxy_value,
    ifNull(h.max_heatmap_proxy_value, 0) AS max_heatmap_proxy_value,
    ifNull(h.avg_heatmap_proxy_value, 0) AS avg_heatmap_proxy_value,
    ifNull(h.persistence_bucket_count, 0) AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    least
    (
        1,
        if
        (
            ifNull(d.daily_max_heatmap_proxy_value, 0) <= 0,
            0,
            log(1 + ifNull(h.heatmap_proxy_value, 0)) / log(1 + d.daily_max_heatmap_proxy_value)
        )
    ) AS heatmap_intensity,

    if
    (
        ifNull(h.rth_flag, 0) = 1
        AND ifNull(h.bid_liquidity_event_size, 0) >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND ifNull(h.bid_liquidity_event_size, 0) > ifNull(h.ask_liquidity_event_size, 0)
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        ifNull(h.rth_flag, 0) = 1
        AND ifNull(h.ask_liquidity_event_size, 0) >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND ifNull(h.ask_liquidity_event_size, 0) > ifNull(h.bid_liquidity_event_size, 0)
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
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

    greatest(ifNull(h.rth_flag, 0), ifNull(a.rth_flag, 0)) AS rth_flag
FROM keys AS k
LEFT JOIN BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S AS h
    ON  k.trade_date = h.trade_date
    AND k.ts_bucket  = h.ts_bucket
    AND k.symbol     = h.symbol
    AND k.price      = h.price
LEFT JOIN BM_MNQ_AGGRESSION_EXECUTIONS_30S AS a
    ON  k.trade_date = a.trade_date
    AND k.ts_bucket  = a.ts_bucket
    AND k.symbol     = a.symbol
    AND k.price      = a.price
LEFT JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON k.trade_date = d.trade_date;

--------------------------------------------------------------------------------
-- REBUILD FRAME SOURCE 1M
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_FRAME_SOURCE_1M;

CREATE TABLE BM_MNQ_FRAME_SOURCE_1M
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH keys AS
(
    SELECT
        trade_date AS trade_date,
        ts_bucket AS ts_bucket,
        ts_et AS ts_et,
        symbol AS symbol,
        price AS price
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M

    UNION DISTINCT

    SELECT
        trade_date AS trade_date,
        ts_bucket AS ts_bucket,
        ts_et AS ts_et,
        symbol AS symbol,
        price AS price
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_1M
)
SELECT
    k.trade_date AS trade_date,
    k.ts_bucket AS ts_bucket,
    k.ts_et AS ts_et,
    k.symbol AS symbol,
    k.price AS price,

    ifNull(h.bid_add_size, 0) AS bid_add_size,
    ifNull(h.ask_add_size, 0) AS ask_add_size,
    ifNull(h.bid_cancel_size, 0) AS bid_cancel_size,
    ifNull(h.ask_cancel_size, 0) AS ask_cancel_size,
    ifNull(h.bid_modify_size, 0) AS bid_modify_size,
    ifNull(h.ask_modify_size, 0) AS ask_modify_size,
    ifNull(h.bid_trade_size, 0) AS bid_trade_size,
    ifNull(h.ask_trade_size, 0) AS ask_trade_size,

    ifNull(h.bid_event_count, 0) AS bid_event_count,
    ifNull(h.ask_event_count, 0) AS ask_event_count,
    ifNull(h.total_event_count, 0) AS total_event_count,

    ifNull(h.bid_liquidity_event_size, 0) AS bid_liquidity_event_size,
    ifNull(h.ask_liquidity_event_size, 0) AS ask_liquidity_event_size,
    ifNull(h.total_liquidity_event_size, 0) AS total_liquidity_event_size,
    ifNull(h.net_liquidity_event_delta, 0) AS net_liquidity_event_delta,

    ifNull(h.heatmap_proxy_value, 0) AS heatmap_proxy_value,
    ifNull(h.max_heatmap_proxy_value, 0) AS max_heatmap_proxy_value,
    ifNull(h.avg_heatmap_proxy_value, 0) AS avg_heatmap_proxy_value,
    ifNull(h.persistence_bucket_count, 0) AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    least
    (
        1,
        if
        (
            ifNull(d.daily_max_heatmap_proxy_value, 0) <= 0,
            0,
            log(1 + ifNull(h.heatmap_proxy_value, 0)) / log(1 + d.daily_max_heatmap_proxy_value)
        )
    ) AS heatmap_intensity,

    if
    (
        ifNull(h.rth_flag, 0) = 1
        AND ifNull(h.bid_liquidity_event_size, 0) >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND ifNull(h.bid_liquidity_event_size, 0) > ifNull(h.ask_liquidity_event_size, 0)
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        ifNull(h.rth_flag, 0) = 1
        AND ifNull(h.ask_liquidity_event_size, 0) >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND ifNull(h.ask_liquidity_event_size, 0) > ifNull(h.bid_liquidity_event_size, 0)
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
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

    greatest(ifNull(h.rth_flag, 0), ifNull(a.rth_flag, 0)) AS rth_flag
FROM keys AS k
LEFT JOIN BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M AS h
    ON  k.trade_date = h.trade_date
    AND k.ts_bucket  = h.ts_bucket
    AND k.symbol     = h.symbol
    AND k.price      = h.price
LEFT JOIN BM_MNQ_AGGRESSION_EXECUTIONS_1M AS a
    ON  k.trade_date = a.trade_date
    AND k.ts_bucket  = a.ts_bucket
    AND k.symbol     = a.symbol
    AND k.price      = a.price
LEFT JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON k.trade_date = d.trade_date;

--------------------------------------------------------------------------------
-- REBUILD FRAME SOURCE 5M
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_FRAME_SOURCE_5M;

CREATE TABLE BM_MNQ_FRAME_SOURCE_5M
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH keys AS
(
    SELECT
        trade_date AS trade_date,
        ts_bucket AS ts_bucket,
        ts_et AS ts_et,
        symbol AS symbol,
        price AS price
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M

    UNION DISTINCT

    SELECT
        trade_date AS trade_date,
        ts_bucket AS ts_bucket,
        ts_et AS ts_et,
        symbol AS symbol,
        price AS price
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_5M
)
SELECT
    k.trade_date AS trade_date,
    k.ts_bucket AS ts_bucket,
    k.ts_et AS ts_et,
    k.symbol AS symbol,
    k.price AS price,

    ifNull(h.bid_add_size, 0) AS bid_add_size,
    ifNull(h.ask_add_size, 0) AS ask_add_size,
    ifNull(h.bid_cancel_size, 0) AS bid_cancel_size,
    ifNull(h.ask_cancel_size, 0) AS ask_cancel_size,
    ifNull(h.bid_modify_size, 0) AS bid_modify_size,
    ifNull(h.ask_modify_size, 0) AS ask_modify_size,
    ifNull(h.bid_trade_size, 0) AS bid_trade_size,
    ifNull(h.ask_trade_size, 0) AS ask_trade_size,

    ifNull(h.bid_event_count, 0) AS bid_event_count,
    ifNull(h.ask_event_count, 0) AS ask_event_count,
    ifNull(h.total_event_count, 0) AS total_event_count,

    ifNull(h.bid_liquidity_event_size, 0) AS bid_liquidity_event_size,
    ifNull(h.ask_liquidity_event_size, 0) AS ask_liquidity_event_size,
    ifNull(h.total_liquidity_event_size, 0) AS total_liquidity_event_size,
    ifNull(h.net_liquidity_event_delta, 0) AS net_liquidity_event_delta,

    ifNull(h.heatmap_proxy_value, 0) AS heatmap_proxy_value,
    ifNull(h.max_heatmap_proxy_value, 0) AS max_heatmap_proxy_value,
    ifNull(h.avg_heatmap_proxy_value, 0) AS avg_heatmap_proxy_value,
    ifNull(h.persistence_bucket_count, 0) AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    least
    (
        1,
        if
        (
            ifNull(d.daily_max_heatmap_proxy_value, 0) <= 0,
            0,
            log(1 + ifNull(h.heatmap_proxy_value, 0)) / log(1 + d.daily_max_heatmap_proxy_value)
        )
    ) AS heatmap_intensity,

    if
    (
        ifNull(h.rth_flag, 0) = 1
        AND ifNull(h.bid_liquidity_event_size, 0) >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND ifNull(h.bid_liquidity_event_size, 0) > ifNull(h.ask_liquidity_event_size, 0)
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        ifNull(h.rth_flag, 0) = 1
        AND ifNull(h.ask_liquidity_event_size, 0) >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND ifNull(h.ask_liquidity_event_size, 0) > ifNull(h.bid_liquidity_event_size, 0)
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
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

    greatest(ifNull(h.rth_flag, 0), ifNull(a.rth_flag, 0)) AS rth_flag
FROM keys AS k
LEFT JOIN BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M AS h
    ON  k.trade_date = h.trade_date
    AND k.ts_bucket  = h.ts_bucket
    AND k.symbol     = h.symbol
    AND k.price      = h.price
LEFT JOIN BM_MNQ_AGGRESSION_EXECUTIONS_5M AS a
    ON  k.trade_date = a.trade_date
    AND k.ts_bucket  = a.ts_bucket
    AND k.symbol     = a.symbol
    AND k.price      = a.price
LEFT JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON k.trade_date = d.trade_date;
