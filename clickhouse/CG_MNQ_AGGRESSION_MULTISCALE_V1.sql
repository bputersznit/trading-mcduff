-- ============================================================
-- CG_mnq_aggression_multiscale_v1
-- Raw all-timeframe aggression table
--
-- Purpose:
--   Create one wide row per 100ms bucket with aggression features at:
--   100ms, 500ms, 1s, 3s, 5s, 15s, 30s, 1m, 5m
--
-- Guardrails:
--   - Event/state logic separated
--   - No nested aggregates
--   - No aggregate-derived columns in ORDER BY
--   - No premature proximity-style inflation
--
-- Next step:
--   CG_mnq_structure_lifecycle_aggression_v1 will join to
--   structure events and classify alignment
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_aggression_multiscale_v1;

CREATE TABLE CG_mnq_aggression_multiscale_v1
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY
(
    trade_date,
    ts_100ms
)
AS
WITH
    0.25 AS tick_size
SELECT
    base.trade_date AS trade_date,
    base.ts_100ms AS ts_100ms,

    /* =========================
       100ms AGGRESSION
       ========================= */
    base.buy_exec_size AS buy_exec_size_100ms,
    base.sell_exec_size AS sell_exec_size_100ms,
    base.exec_size AS exec_size_100ms,
    base.exec_delta AS exec_delta_100ms,
    base.exec_imbalance AS exec_imbalance_100ms,
    base.exec_count AS exec_count_100ms,
    base.buy_exec_count AS buy_exec_count_100ms,
    base.sell_exec_count AS sell_exec_count_100ms,

    /* =========================
       500ms AGGRESSION
       ========================= */
    a500.buy_exec_size AS buy_exec_size_500ms,
    a500.sell_exec_size AS sell_exec_size_500ms,
    a500.exec_size AS exec_size_500ms,
    a500.exec_delta AS exec_delta_500ms,
    a500.exec_imbalance AS exec_imbalance_500ms,
    a500.exec_count AS exec_count_500ms,
    a500.buy_exec_count AS buy_exec_count_500ms,
    a500.sell_exec_count AS sell_exec_count_500ms,

    /* =========================
       1s AGGRESSION
       ========================= */
    a1s.buy_exec_size AS buy_exec_size_1s,
    a1s.sell_exec_size AS sell_exec_size_1s,
    a1s.exec_size AS exec_size_1s,
    a1s.exec_delta AS exec_delta_1s,
    a1s.exec_imbalance AS exec_imbalance_1s,
    a1s.exec_count AS exec_count_1s,
    a1s.buy_exec_count AS buy_exec_count_1s,
    a1s.sell_exec_count AS sell_exec_count_1s,

    /* =========================
       3s AGGRESSION
       ========================= */
    a3s.buy_exec_size AS buy_exec_size_3s,
    a3s.sell_exec_size AS sell_exec_size_3s,
    a3s.exec_size AS exec_size_3s,
    a3s.exec_delta AS exec_delta_3s,
    a3s.exec_imbalance AS exec_imbalance_3s,
    a3s.exec_count AS exec_count_3s,
    a3s.buy_exec_count AS buy_exec_count_3s,
    a3s.sell_exec_count AS sell_exec_count_3s,

    /* =========================
       5s AGGRESSION
       ========================= */
    a5s.buy_exec_size AS buy_exec_size_5s,
    a5s.sell_exec_size AS sell_exec_size_5s,
    a5s.exec_size AS exec_size_5s,
    a5s.exec_delta AS exec_delta_5s,
    a5s.exec_imbalance AS exec_imbalance_5s,
    a5s.exec_count AS exec_count_5s,
    a5s.buy_exec_count AS buy_exec_count_5s,
    a5s.sell_exec_count AS sell_exec_count_5s,

    /* =========================
       15s AGGRESSION
       ========================= */
    a15s.buy_exec_size AS buy_exec_size_15s,
    a15s.sell_exec_size AS sell_exec_size_15s,
    a15s.exec_size AS exec_size_15s,
    a15s.exec_delta AS exec_delta_15s,
    a15s.exec_imbalance AS exec_imbalance_15s,
    a15s.exec_count AS exec_count_15s,
    a15s.buy_exec_count AS buy_exec_count_15s,
    a15s.sell_exec_count AS sell_exec_count_15s,

    /* =========================
       30s AGGRESSION
       ========================= */
    a30s.buy_exec_size AS buy_exec_size_30s,
    a30s.sell_exec_size AS sell_exec_size_30s,
    a30s.exec_size AS exec_size_30s,
    a30s.exec_delta AS exec_delta_30s,
    a30s.exec_imbalance AS exec_imbalance_30s,
    a30s.exec_count AS exec_count_30s,
    a30s.buy_exec_count AS buy_exec_count_30s,
    a30s.sell_exec_count AS sell_exec_count_30s,

    /* =========================
       1m AGGRESSION
       ========================= */
    a1m.buy_exec_size AS buy_exec_size_1m,
    a1m.sell_exec_size AS sell_exec_size_1m,
    a1m.exec_size AS exec_size_1m,
    a1m.exec_delta AS exec_delta_1m,
    a1m.exec_imbalance AS exec_imbalance_1m,
    a1m.exec_count AS exec_count_1m,
    a1m.buy_exec_count AS buy_exec_count_1m,
    a1m.sell_exec_count AS sell_exec_count_1m,

    /* =========================
       5m AGGRESSION
       ========================= */
    a5m.buy_exec_size AS buy_exec_size_5m,
    a5m.sell_exec_size AS sell_exec_size_5m,
    a5m.exec_size AS exec_size_5m,
    a5m.exec_delta AS exec_delta_5m,
    a5m.exec_imbalance AS exec_imbalance_5m,
    a5m.exec_count AS exec_count_5m,
    a5m.buy_exec_count AS buy_exec_count_5m,
    a5m.sell_exec_count AS sell_exec_count_5m

FROM
(
    SELECT
        toDate(toTimeZone(ts_100ms, 'America/New_York')) AS trade_date,
        ts_100ms,

        sumIf(size, aggressor_side = 'BUY') AS buy_exec_size,
        sumIf(size, aggressor_side = 'SELL') AS sell_exec_size,
        sum(size) AS exec_size,
        sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL') AS exec_delta,
        (
            sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL')
        ) / nullIf(sum(size), 0) AS exec_imbalance,

        count() AS exec_count,
        countIf(aggressor_side = 'BUY') AS buy_exec_count,
        countIf(aggressor_side = 'SELL') AS sell_exec_count

    FROM
    (
        SELECT
            toStartOfInterval(ts_event, toIntervalMillisecond(100)) AS ts_100ms,
            size,
            multiIf(
                side = 'A', 'BUY',
                side = 'B', 'SELL',
                'UNKNOWN'
            ) AS aggressor_side
        FROM mnq_mbo
        WHERE action = 'T'
          AND side IN ('A', 'B')
          AND price > 0
          AND size > 0
          AND (
              toHour(toTimeZone(ts_event, 'America/New_York')) * 60
              + toMinute(toTimeZone(ts_event, 'America/New_York'))
          ) BETWEEN 570 AND 960
    ) AS raw_100ms
    GROUP BY ts_100ms
) AS base

LEFT JOIN
(
    SELECT
        ts_500ms,
        sumIf(size, aggressor_side = 'BUY') AS buy_exec_size,
        sumIf(size, aggressor_side = 'SELL') AS sell_exec_size,
        sum(size) AS exec_size,
        sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL') AS exec_delta,
        (
            sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL')
        ) / nullIf(sum(size), 0) AS exec_imbalance,
        count() AS exec_count,
        countIf(aggressor_side = 'BUY') AS buy_exec_count,
        countIf(aggressor_side = 'SELL') AS sell_exec_count
    FROM
    (
        SELECT
            toStartOfInterval(ts_event, toIntervalMillisecond(500)) AS ts_500ms,
            size,
            multiIf(side = 'A', 'BUY', side = 'B', 'SELL', 'UNKNOWN') AS aggressor_side
        FROM mnq_mbo
        WHERE action = 'T'
          AND side IN ('A', 'B')
          AND price > 0
          AND size > 0
          AND (
              toHour(toTimeZone(ts_event, 'America/New_York')) * 60
              + toMinute(toTimeZone(ts_event, 'America/New_York'))
          ) BETWEEN 570 AND 960
    )
    GROUP BY ts_500ms
) AS a500
    ON toStartOfInterval(base.ts_100ms, toIntervalMillisecond(500)) = a500.ts_500ms

LEFT JOIN
(
    SELECT
        ts_1s,
        sumIf(size, aggressor_side = 'BUY') AS buy_exec_size,
        sumIf(size, aggressor_side = 'SELL') AS sell_exec_size,
        sum(size) AS exec_size,
        sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL') AS exec_delta,
        (
            sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL')
        ) / nullIf(sum(size), 0) AS exec_imbalance,
        count() AS exec_count,
        countIf(aggressor_side = 'BUY') AS buy_exec_count,
        countIf(aggressor_side = 'SELL') AS sell_exec_count
    FROM
    (
        SELECT
            toStartOfInterval(ts_event, toIntervalSecond(1)) AS ts_1s,
            size,
            multiIf(side = 'A', 'BUY', side = 'B', 'SELL', 'UNKNOWN') AS aggressor_side
        FROM mnq_mbo
        WHERE action = 'T'
          AND side IN ('A', 'B')
          AND price > 0
          AND size > 0
          AND (
              toHour(toTimeZone(ts_event, 'America/New_York')) * 60
              + toMinute(toTimeZone(ts_event, 'America/New_York'))
          ) BETWEEN 570 AND 960
    )
    GROUP BY ts_1s
) AS a1s
    ON toStartOfInterval(base.ts_100ms, toIntervalSecond(1)) = a1s.ts_1s

LEFT JOIN
(
    SELECT
        ts_3s,
        sumIf(size, aggressor_side = 'BUY') AS buy_exec_size,
        sumIf(size, aggressor_side = 'SELL') AS sell_exec_size,
        sum(size) AS exec_size,
        sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL') AS exec_delta,
        (
            sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL')
        ) / nullIf(sum(size), 0) AS exec_imbalance,
        count() AS exec_count,
        countIf(aggressor_side = 'BUY') AS buy_exec_count,
        countIf(aggressor_side = 'SELL') AS sell_exec_count
    FROM
    (
        SELECT
            toStartOfInterval(ts_event, toIntervalSecond(3)) AS ts_3s,
            size,
            multiIf(side = 'A', 'BUY', side = 'B', 'SELL', 'UNKNOWN') AS aggressor_side
        FROM mnq_mbo
        WHERE action = 'T'
          AND side IN ('A', 'B')
          AND price > 0
          AND size > 0
          AND (
              toHour(toTimeZone(ts_event, 'America/New_York')) * 60
              + toMinute(toTimeZone(ts_event, 'America/New_York'))
          ) BETWEEN 570 AND 960
    )
    GROUP BY ts_3s
) AS a3s
    ON toStartOfInterval(base.ts_100ms, toIntervalSecond(3)) = a3s.ts_3s

LEFT JOIN
(
    SELECT
        ts_5s,
        sumIf(size, aggressor_side = 'BUY') AS buy_exec_size,
        sumIf(size, aggressor_side = 'SELL') AS sell_exec_size,
        sum(size) AS exec_size,
        sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL') AS exec_delta,
        (
            sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL')
        ) / nullIf(sum(size), 0) AS exec_imbalance,
        count() AS exec_count,
        countIf(aggressor_side = 'BUY') AS buy_exec_count,
        countIf(aggressor_side = 'SELL') AS sell_exec_count
    FROM
    (
        SELECT
            toStartOfInterval(ts_event, toIntervalSecond(5)) AS ts_5s,
            size,
            multiIf(side = 'A', 'BUY', side = 'B', 'SELL', 'UNKNOWN') AS aggressor_side
        FROM mnq_mbo
        WHERE action = 'T'
          AND side IN ('A', 'B')
          AND price > 0
          AND size > 0
          AND (
              toHour(toTimeZone(ts_event, 'America/New_York')) * 60
              + toMinute(toTimeZone(ts_event, 'America/New_York'))
          ) BETWEEN 570 AND 960
    )
    GROUP BY ts_5s
) AS a5s
    ON toStartOfInterval(base.ts_100ms, toIntervalSecond(5)) = a5s.ts_5s

LEFT JOIN
(
    SELECT
        ts_15s,
        sumIf(size, aggressor_side = 'BUY') AS buy_exec_size,
        sumIf(size, aggressor_side = 'SELL') AS sell_exec_size,
        sum(size) AS exec_size,
        sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL') AS exec_delta,
        (
            sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL')
        ) / nullIf(sum(size), 0) AS exec_imbalance,
        count() AS exec_count,
        countIf(aggressor_side = 'BUY') AS buy_exec_count,
        countIf(aggressor_side = 'SELL') AS sell_exec_count
    FROM
    (
        SELECT
            toStartOfInterval(ts_event, toIntervalSecond(15)) AS ts_15s,
            size,
            multiIf(side = 'A', 'BUY', side = 'B', 'SELL', 'UNKNOWN') AS aggressor_side
        FROM mnq_mbo
        WHERE action = 'T'
          AND side IN ('A', 'B')
          AND price > 0
          AND size > 0
          AND (
              toHour(toTimeZone(ts_event, 'America/New_York')) * 60
              + toMinute(toTimeZone(ts_event, 'America/New_York'))
          ) BETWEEN 570 AND 960
    )
    GROUP BY ts_15s
) AS a15s
    ON toStartOfInterval(base.ts_100ms, toIntervalSecond(15)) = a15s.ts_15s

LEFT JOIN
(
    SELECT
        ts_30s,
        sumIf(size, aggressor_side = 'BUY') AS buy_exec_size,
        sumIf(size, aggressor_side = 'SELL') AS sell_exec_size,
        sum(size) AS exec_size,
        sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL') AS exec_delta,
        (
            sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL')
        ) / nullIf(sum(size), 0) AS exec_imbalance,
        count() AS exec_count,
        countIf(aggressor_side = 'BUY') AS buy_exec_count,
        countIf(aggressor_side = 'SELL') AS sell_exec_count
    FROM
    (
        SELECT
            toStartOfInterval(ts_event, toIntervalSecond(30)) AS ts_30s,
            size,
            multiIf(side = 'A', 'BUY', side = 'B', 'SELL', 'UNKNOWN') AS aggressor_side
        FROM mnq_mbo
        WHERE action = 'T'
          AND side IN ('A', 'B')
          AND price > 0
          AND size > 0
          AND (
              toHour(toTimeZone(ts_event, 'America/New_York')) * 60
              + toMinute(toTimeZone(ts_event, 'America/New_York'))
          ) BETWEEN 570 AND 960
    )
    GROUP BY ts_30s
) AS a30s
    ON toStartOfInterval(base.ts_100ms, toIntervalSecond(30)) = a30s.ts_30s

LEFT JOIN
(
    SELECT
        ts_1m,
        sumIf(size, aggressor_side = 'BUY') AS buy_exec_size,
        sumIf(size, aggressor_side = 'SELL') AS sell_exec_size,
        sum(size) AS exec_size,
        sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL') AS exec_delta,
        (
            sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL')
        ) / nullIf(sum(size), 0) AS exec_imbalance,
        count() AS exec_count,
        countIf(aggressor_side = 'BUY') AS buy_exec_count,
        countIf(aggressor_side = 'SELL') AS sell_exec_count
    FROM
    (
        SELECT
            toStartOfInterval(ts_event, toIntervalMinute(1)) AS ts_1m,
            size,
            multiIf(side = 'A', 'BUY', side = 'B', 'SELL', 'UNKNOWN') AS aggressor_side
        FROM mnq_mbo
        WHERE action = 'T'
          AND side IN ('A', 'B')
          AND price > 0
          AND size > 0
          AND (
              toHour(toTimeZone(ts_event, 'America/New_York')) * 60
              + toMinute(toTimeZone(ts_event, 'America/New_York'))
          ) BETWEEN 570 AND 960
    )
    GROUP BY ts_1m
) AS a1m
    ON toStartOfInterval(base.ts_100ms, toIntervalMinute(1)) = a1m.ts_1m

LEFT JOIN
(
    SELECT
        ts_5m,
        sumIf(size, aggressor_side = 'BUY') AS buy_exec_size,
        sumIf(size, aggressor_side = 'SELL') AS sell_exec_size,
        sum(size) AS exec_size,
        sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL') AS exec_delta,
        (
            sumIf(size, aggressor_side = 'BUY')
            - sumIf(size, aggressor_side = 'SELL')
        ) / nullIf(sum(size), 0) AS exec_imbalance,
        count() AS exec_count,
        countIf(aggressor_side = 'BUY') AS buy_exec_count,
        countIf(aggressor_side = 'SELL') AS sell_exec_count
    FROM
    (
        SELECT
            toStartOfInterval(ts_event, toIntervalMinute(5)) AS ts_5m,
            size,
            multiIf(side = 'A', 'BUY', side = 'B', 'SELL', 'UNKNOWN') AS aggressor_side
        FROM mnq_mbo
        WHERE action = 'T'
          AND side IN ('A', 'B')
          AND price > 0
          AND size > 0
          AND (
              toHour(toTimeZone(ts_event, 'America/New_York')) * 60
              + toMinute(toTimeZone(ts_event, 'America/New_York'))
          ) BETWEEN 570 AND 960
    )
    GROUP BY ts_5m
) AS a5m
    ON toStartOfInterval(base.ts_100ms, toIntervalMinute(5)) = a5m.ts_5m;
