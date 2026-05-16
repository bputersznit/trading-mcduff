-- ============================================================
-- CG_mnq_session_regime_v2
-- 5-second bars with regime context (ORB, VWAP, time, bias)
--
-- Purpose:
--   - Lightweight session context table
--   - ORB high/low/range per day
--   - Running VWAP
--   - ORB state, VWAP relation, time bucket, trend bias
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_session_regime_v2;

CREATE TABLE CG_mnq_session_regime_v2
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_5s)
AS
WITH
    570 AS rth_start_minute,
    585 AS orb_end_minute,
    960 AS rth_end_minute
SELECT
    base.trade_date AS trade_date,
    base.ts_5s AS ts_5s,
    base.minute_et AS minute_et,
    base.open_price AS open_price,
    base.high_price AS high_price,
    base.low_price AS low_price,
    base.close_price AS close_price,
    orb.orb_high AS orb_high,
    orb.orb_low AS orb_low,
    orb.orb_range_pts AS orb_range_pts,

    vwap.running_vwap AS vwap,

    multiIf(
        base.minute_et < orb_end_minute, 'BUILDING',
        base.close_price > orb.orb_high, 'ABOVE_OR',
        base.close_price < orb.orb_low, 'BELOW_OR',
        'INSIDE_OR'
    ) AS orb_state,

    multiIf(
        base.close_price > vwap.running_vwap + 1.0, 'ABOVE',
        base.close_price < vwap.running_vwap - 1.0, 'BELOW',
        'AT'
    ) AS vwap_relation,

    multiIf(
        base.minute_et < 585, 'OPENING_RANGE',
        base.minute_et < 660, 'OPEN',
        base.minute_et < 780, 'MIDDAY',
        base.minute_et < 900, 'PM',
        'CLOSE'
    ) AS time_bucket,

    multiIf(
        base.close_price > orb.orb_high AND base.close_price > vwap.running_vwap, 'LONG_BIAS',
        base.close_price < orb.orb_low AND base.close_price < vwap.running_vwap, 'SHORT_BIAS',
        'NEUTRAL'
    ) AS trend_bias

FROM
(
    SELECT
        toDate(toTimeZone(t, 'America/New_York')) AS trade_date,
        t AS ts_5s,
        open_px AS open_price,
        hi_px AS high_price,
        lo_px AS low_price,
        close_px AS close_price,
        (
            toHour(toTimeZone(t, 'America/New_York')) * 60
            + toMinute(toTimeZone(t, 'America/New_York'))
        ) AS minute_et
    FROM mnq_ohlc_5s
    WHERE
        (
            toHour(toTimeZone(t, 'America/New_York')) * 60
            + toMinute(toTimeZone(t, 'America/New_York'))
        ) BETWEEN rth_start_minute AND rth_end_minute
) AS base

INNER JOIN
(
    SELECT
        trade_date,
        max(high_price) AS orb_high,
        min(low_price) AS orb_low,
        max(high_price) - min(low_price) AS orb_range_pts
    FROM
    (
        SELECT
            toDate(toTimeZone(t, 'America/New_York')) AS trade_date,
            hi_px AS high_price,
            lo_px AS low_price,
            (
                toHour(toTimeZone(t, 'America/New_York')) * 60
                + toMinute(toTimeZone(t, 'America/New_York'))
            ) AS minute_et
        FROM mnq_ohlc_5s
        WHERE
            (
                toHour(toTimeZone(t, 'America/New_York')) * 60
                + toMinute(toTimeZone(t, 'America/New_York'))
            ) BETWEEN rth_start_minute AND orb_end_minute
    ) AS orb_base
    GROUP BY trade_date
) AS orb
    ON base.trade_date = orb.trade_date

INNER JOIN
(
    SELECT
        trade_date,
        ts_5s,
        sum(price_volume) OVER
        (
            PARTITION BY trade_date
            ORDER BY ts_5s
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        )
        /
        nullIf(
            sum(total_volume) OVER
            (
                PARTITION BY trade_date
                ORDER BY ts_5s
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
            ),
            0
        ) AS running_vwap
    FROM
    (
        SELECT
            toDate(toTimeZone(ts_event, 'America/New_York')) AS trade_date,
            toStartOfInterval(ts_event, toIntervalSecond(5)) AS ts_5s,
            sum(price * size) AS price_volume,
            sum(size) AS total_volume
        FROM mnq_mbo
        WHERE
            action = 'T'
            AND
            (
                toHour(toTimeZone(ts_event, 'America/New_York')) * 60
                + toMinute(toTimeZone(ts_event, 'America/New_York'))
            ) BETWEEN rth_start_minute AND rth_end_minute
        GROUP BY trade_date, ts_5s
    ) AS vwap_base
) AS vwap
    ON base.trade_date = vwap.trade_date
   AND base.ts_5s = vwap.ts_5s;
