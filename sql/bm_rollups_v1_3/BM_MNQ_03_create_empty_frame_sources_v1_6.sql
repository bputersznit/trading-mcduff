-- BM_MNQ_03_create_empty_frame_sources_v1_6.sql
-- Generated: 2026-05-10 14:12:00 America/New_York
--
-- Purpose:
--   Drop and recreate empty BM_MNQ_FRAME_SOURCE_* tables with the corrected
--   frame-source schema.
--
-- v1.6 fix:
--   Removed unsupported ClickHouse setting:
--       the unsupported external-join memory setting
--
-- Run:
--   clickhouse-client --multiquery < BM_MNQ_03_create_empty_frame_sources_v1_6.sql

SET max_threads = 6;
SET max_memory_usage = 0;
SET max_bytes_before_external_group_by = 20000000000;
SET max_bytes_before_external_sort = 20000000000;


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

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    least
    (
        1,
        if
        (
            ifNull(d.daily_max_heatmap_proxy_value, 0) <= 0,
            0,
            log(1 + h.heatmap_proxy_value) / log(1 + d.daily_max_heatmap_proxy_value)
        )
    ) AS heatmap_intensity,

    if
    (
        h.rth_flag = 1
        AND h.bid_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.bid_liquidity_event_size > h.ask_liquidity_event_size
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        h.rth_flag = 1
        AND h.ask_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.ask_liquidity_event_size > h.bid_liquidity_event_size
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

    greatest(h.rth_flag, ifNull(a.rth_flag, 0)) AS rth_flag
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S AS h
LEFT ANY JOIN BM_MNQ_AGGRESSION_EXECUTIONS_1S AS a
    ON  h.trade_date = a.trade_date
    AND h.ts_bucket  = a.ts_bucket
    AND h.symbol     = a.symbol
    AND h.price      = a.price
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON h.trade_date = d.trade_date
WHERE 0;

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

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    least
    (
        1,
        if
        (
            ifNull(d.daily_max_heatmap_proxy_value, 0) <= 0,
            0,
            log(1 + h.heatmap_proxy_value) / log(1 + d.daily_max_heatmap_proxy_value)
        )
    ) AS heatmap_intensity,

    if
    (
        h.rth_flag = 1
        AND h.bid_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.bid_liquidity_event_size > h.ask_liquidity_event_size
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        h.rth_flag = 1
        AND h.ask_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.ask_liquidity_event_size > h.bid_liquidity_event_size
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

    greatest(h.rth_flag, ifNull(a.rth_flag, 0)) AS rth_flag
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S AS h
LEFT ANY JOIN BM_MNQ_AGGRESSION_EXECUTIONS_5S AS a
    ON  h.trade_date = a.trade_date
    AND h.ts_bucket  = a.ts_bucket
    AND h.symbol     = a.symbol
    AND h.price      = a.price
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON h.trade_date = d.trade_date
WHERE 0;

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

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    least
    (
        1,
        if
        (
            ifNull(d.daily_max_heatmap_proxy_value, 0) <= 0,
            0,
            log(1 + h.heatmap_proxy_value) / log(1 + d.daily_max_heatmap_proxy_value)
        )
    ) AS heatmap_intensity,

    if
    (
        h.rth_flag = 1
        AND h.bid_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.bid_liquidity_event_size > h.ask_liquidity_event_size
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        h.rth_flag = 1
        AND h.ask_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.ask_liquidity_event_size > h.bid_liquidity_event_size
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

    greatest(h.rth_flag, ifNull(a.rth_flag, 0)) AS rth_flag
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S AS h
LEFT ANY JOIN BM_MNQ_AGGRESSION_EXECUTIONS_30S AS a
    ON  h.trade_date = a.trade_date
    AND h.ts_bucket  = a.ts_bucket
    AND h.symbol     = a.symbol
    AND h.price      = a.price
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON h.trade_date = d.trade_date
WHERE 0;

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

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    least
    (
        1,
        if
        (
            ifNull(d.daily_max_heatmap_proxy_value, 0) <= 0,
            0,
            log(1 + h.heatmap_proxy_value) / log(1 + d.daily_max_heatmap_proxy_value)
        )
    ) AS heatmap_intensity,

    if
    (
        h.rth_flag = 1
        AND h.bid_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.bid_liquidity_event_size > h.ask_liquidity_event_size
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        h.rth_flag = 1
        AND h.ask_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.ask_liquidity_event_size > h.bid_liquidity_event_size
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

    greatest(h.rth_flag, ifNull(a.rth_flag, 0)) AS rth_flag
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M AS h
LEFT ANY JOIN BM_MNQ_AGGRESSION_EXECUTIONS_1M AS a
    ON  h.trade_date = a.trade_date
    AND h.ts_bucket  = a.ts_bucket
    AND h.symbol     = a.symbol
    AND h.price      = a.price
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON h.trade_date = d.trade_date
WHERE 0;

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

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    least
    (
        1,
        if
        (
            ifNull(d.daily_max_heatmap_proxy_value, 0) <= 0,
            0,
            log(1 + h.heatmap_proxy_value) / log(1 + d.daily_max_heatmap_proxy_value)
        )
    ) AS heatmap_intensity,

    if
    (
        h.rth_flag = 1
        AND h.bid_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.bid_liquidity_event_size > h.ask_liquidity_event_size
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        h.rth_flag = 1
        AND h.ask_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.ask_liquidity_event_size > h.bid_liquidity_event_size
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

    greatest(h.rth_flag, ifNull(a.rth_flag, 0)) AS rth_flag
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M AS h
LEFT ANY JOIN BM_MNQ_AGGRESSION_EXECUTIONS_5M AS a
    ON  h.trade_date = a.trade_date
    AND h.ts_bucket  = a.ts_bucket
    AND h.symbol     = a.symbol
    AND h.price      = a.price
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON h.trade_date = d.trade_date
WHERE 0;
