-- ============================================================
-- PHASE 4 REGIME LAYER
-- McDuff Package 04
--
-- Source:
--   CG_mnq_wall_outcomes_enriched_v2
--
-- Output:
--   CG_mnq_wall_outcomes_regime_v1
--
-- Purpose:
--   Add contextual regime conditioning:
--     - ORB position
--     - VWAP relation
--     - session time bucket
--     - session high/low distance
--     - rolling volatility regime
--     - tradeable edge classification
--
-- Notes:
--   ClickHouse-safe:
--     - no r.*
--     - no same-SELECT alias reuse
--     - derived expressions isolated in subqueries
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_wall_outcomes_regime_v1;

CREATE TABLE CG_mnq_wall_outcomes_regime_v1
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY
(
    trade_date,
    wall_time,
    wall_side,
    wall_price,
    delta_flip_pattern,
    orb_position,
    vwap_relation,
    time_bucket
)
AS
WITH
-- ------------------------------------------------------------
-- 1. RTH trades with ET timestamps
-- ------------------------------------------------------------
rth_trades AS
(
    SELECT
        toDate(toTimeZone(ts_event, 'America/New_York')) AS trade_date,
        ts_event AS ts_event,
        toTimeZone(ts_event, 'America/New_York') AS ts_et,
        price AS price,
        size AS size
    FROM mnq_trades
    WHERE (toHour(toTimeZone(ts_event, 'America/New_York')) * 60 + toMinute(toTimeZone(ts_event, 'America/New_York'))) >= (9 * 60 + 30)
      AND (toHour(toTimeZone(ts_event, 'America/New_York')) * 60 + toMinute(toTimeZone(ts_event, 'America/New_York'))) < (16 * 60)
),

-- ------------------------------------------------------------
-- 2. Opening range: 09:30-09:45 ET
-- ------------------------------------------------------------
orb AS
(
    SELECT
        trade_date,
        min(price) AS orb_low,
        max(price) AS orb_high,
        max(price) - min(price) AS orb_range
    FROM rth_trades
    WHERE (toHour(ts_et) * 60 + toMinute(ts_et)) >= (9 * 60 + 30)
      AND (toHour(ts_et) * 60 + toMinute(ts_et)) < (9 * 60 + 45)
    GROUP BY trade_date
),

-- ------------------------------------------------------------
-- 3. Intraday cumulative VWAP by 1-second bucket
-- ------------------------------------------------------------
trade_1s AS
(
    SELECT
        trade_date,
        toStartOfSecond(ts_event) AS bucket_time,
        sum(price * size) AS pv_1s,
        sum(size) AS vol_1s,
        min(price) AS low_1s,
        max(price) AS high_1s,
        argMax(price, ts_event) AS close_1s
    FROM rth_trades
    GROUP BY
        trade_date,
        bucket_time
),

vwap_1s AS
(
    SELECT
        trade_date,
        bucket_time,
        sum(pv_1s) OVER
        (
            PARTITION BY trade_date
            ORDER BY bucket_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        )
        /
        nullIf
        (
            sum(vol_1s) OVER
            (
                PARTITION BY trade_date
                ORDER BY bucket_time
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
            ),
            0
        ) AS session_vwap,

        min(low_1s) OVER
        (
            PARTITION BY trade_date
            ORDER BY bucket_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS session_low_so_far,

        max(high_1s) OVER
        (
            PARTITION BY trade_date
            ORDER BY bucket_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS session_high_so_far,

        close_1s AS close_1s
    FROM trade_1s
),

-- ------------------------------------------------------------
-- 4. 1-minute volatility proxy from trades
-- ------------------------------------------------------------
bars_1m AS
(
    SELECT
        trade_date,
        toStartOfMinute(ts_event) AS bar_time,
        min(price) AS low_px,
        max(price) AS high_px,
        argMax(price, ts_event) AS close_px
    FROM rth_trades
    GROUP BY
        trade_date,
        bar_time
),

range_1m AS
(
    SELECT
        trade_date,
        bar_time,
        high_px - low_px AS range_pts
    FROM bars_1m
),

vol_regime_1m AS
(
    SELECT
        trade_date,
        bar_time,
        range_pts,
        avg(range_pts) OVER
        (
            PARTITION BY trade_date
            ORDER BY bar_time
            ROWS BETWEEN 19 PRECEDING AND CURRENT ROW
        ) AS avg_range_20m
    FROM range_1m
),

vol_regime_classified AS
(
    SELECT
        trade_date,
        bar_time,
        range_pts,
        avg_range_20m,
        multiIf
        (
            avg_range_20m <= 4.0,  'LOW_VOL',
            avg_range_20m <= 9.0,  'NORMAL_VOL',
                                'HIGH_VOL'
        ) AS atr_regime
    FROM vol_regime_1m
),

-- ------------------------------------------------------------
-- 5. Base enriched wall outcomes with explicit projection
-- ------------------------------------------------------------
base AS
(
    SELECT
        trade_date,
        first_touch_time AS wall_time,
        toTimeZone(first_touch_time, 'America/New_York') AS wall_et,
        wall_side,
        wall_price,
        wall_score,

        outcome_label_30s,
        reject_ticks_30s + break_ticks_30s AS outcome_move_ticks_30s,

        wall_behavior,
        delta_flip_pattern,
        wall_aggression_pattern,

        buy_volume_pre_5s AS pre_buy_vol,
        sell_volume_pre_5s AS pre_sell_vol,
        buy_volume_touch_5s AS touch_buy_vol,
        sell_volume_touch_5s AS touch_sell_vol,
        buy_volume_post_5s AS post_buy_vol,
        sell_volume_post_5s AS post_sell_vol,

        delta_pre_5s AS pre_delta,
        delta_touch_5s AS touch_delta,
        delta_post_5s AS post_delta,

        net_aggression_into_wall_5s AS net_aggression_into_wall,
        total_volume_touch_5s AS total_aggression_near_wall
    FROM CG_mnq_wall_outcomes_enriched_v2
),

-- ------------------------------------------------------------
-- 6. Join context features
-- ------------------------------------------------------------
joined_context AS
(
    SELECT
        b.trade_date AS trade_date,
        b.wall_time AS wall_time,
        b.wall_et AS wall_et,
        b.wall_side AS wall_side,
        b.wall_price AS wall_price,
        b.wall_score AS wall_score,

        b.outcome_label_30s AS outcome_label_30s,
        b.outcome_move_ticks_30s AS outcome_move_ticks_30s,

        b.wall_behavior AS wall_behavior,
        b.delta_flip_pattern AS delta_flip_pattern,
        b.wall_aggression_pattern AS wall_aggression_pattern,

        b.pre_buy_vol AS pre_buy_vol,
        b.pre_sell_vol AS pre_sell_vol,
        b.touch_buy_vol AS touch_buy_vol,
        b.touch_sell_vol AS touch_sell_vol,
        b.post_buy_vol AS post_buy_vol,
        b.post_sell_vol AS post_sell_vol,

        b.pre_delta AS pre_delta,
        b.touch_delta AS touch_delta,
        b.post_delta AS post_delta,

        b.net_aggression_into_wall AS net_aggression_into_wall,
        b.total_aggression_near_wall AS total_aggression_near_wall,

        o.orb_low AS orb_low,
        o.orb_high AS orb_high,
        o.orb_range AS orb_range,

        v.session_vwap AS session_vwap,
        v.session_low_so_far AS session_low_so_far,
        v.session_high_so_far AS session_high_so_far,
        v.close_1s AS close_1s,

        vr.range_pts AS range_1m_pts,
        vr.avg_range_20m AS avg_range_20m,
        vr.atr_regime AS atr_regime,

        toHour(b.wall_et) * 60 + toMinute(b.wall_et) AS wall_clock_minutes
    FROM base AS b
    LEFT JOIN orb AS o
        ON b.trade_date = o.trade_date
    LEFT JOIN vwap_1s AS v
        ON b.trade_date = v.trade_date
       AND toStartOfSecond(b.wall_time) = v.bucket_time
    LEFT JOIN vol_regime_classified AS vr
        ON b.trade_date = vr.trade_date
       AND toStartOfMinute(b.wall_time) = vr.bar_time
),

-- ------------------------------------------------------------
-- 7. Classify regime fields
-- ------------------------------------------------------------
classified AS
(
    SELECT
        trade_date,
        wall_time,
        wall_et,
        wall_side,
        wall_price,
        wall_score,

        outcome_label_30s,
        outcome_move_ticks_30s,

        wall_behavior,
        delta_flip_pattern,
        wall_aggression_pattern,

        pre_buy_vol,
        pre_sell_vol,
        touch_buy_vol,
        touch_sell_vol,
        post_buy_vol,
        post_sell_vol,

        pre_delta,
        touch_delta,
        post_delta,

        net_aggression_into_wall,
        total_aggression_near_wall,

        orb_low,
        orb_high,
        orb_range,
        session_vwap,
        session_low_so_far,
        session_high_so_far,
        close_1s,
        range_1m_pts,
        avg_range_20m,
        atr_regime,

        wall_price - orb_high AS distance_from_orb_high,
        wall_price - orb_low AS distance_from_orb_low,
        session_high_so_far - wall_price AS distance_from_session_high,
        wall_price - session_low_so_far AS distance_from_session_low,
        wall_price - session_vwap AS distance_from_vwap,

        multiIf
        (
            wall_price > orb_high, 'ABOVE_OR_HIGH',
            wall_price < orb_low,  'BELOW_OR_LOW',
                                'INSIDE_OR'
        ) AS orb_position,

        multiIf
        (
            wall_price > session_vwap + 2.0, 'ABOVE_VWAP',
            wall_price < session_vwap - 2.0, 'BELOW_VWAP',
                                         'AT_VWAP'
        ) AS vwap_relation,

        multiIf
        (
            wall_clock_minutes >= (9 * 60 + 30) AND wall_clock_minutes < (10 * 60 + 15), 'RTH_OPEN',
            wall_clock_minutes >= (10 * 60 + 15) AND wall_clock_minutes < (13 * 60 + 30), 'MIDDAY',
            wall_clock_minutes >= (13 * 60 + 30) AND wall_clock_minutes < (15 * 60 + 30), 'PM_DRIFT',
            wall_clock_minutes >= (15 * 60 + 30) AND wall_clock_minutes < (16 * 60), 'CLOSE',
                                                                                      'OUTSIDE_RTH'
        ) AS time_bucket,

        multiIf
        (
            abs(session_high_so_far - wall_price) <= 8.0, 'NEAR_SESSION_HIGH',
            abs(wall_price - session_low_so_far) <= 8.0,  'NEAR_SESSION_LOW',
                                                        'MID_SESSION_RANGE'
        ) AS session_extreme_location,

        multiIf
        (
            wall_side = 'ASK'
            AND delta_flip_pattern IN ('CONTINUED_SELL', 'BUY_TO_SELL_FLIP'),
            'REJECTION_SHORT_CANDIDATE',

            wall_side = 'BID'
            AND delta_flip_pattern IN ('CONTINUED_BUY', 'SELL_TO_BUY_FLIP'),
            'REJECTION_LONG_CANDIDATE',

            wall_side = 'ASK'
            AND delta_flip_pattern IN ('CONTINUED_BUY', 'SELL_TO_BUY_FLIP'),
            'BREAK_LONG_CANDIDATE',

            wall_side = 'BID'
            AND delta_flip_pattern IN ('CONTINUED_SELL', 'BUY_TO_SELL_FLIP'),
            'BREAK_SHORT_CANDIDATE',

            'NO_CLEAR_TRADE_CANDIDATE'
        ) AS trade_candidate_class
    FROM joined_context
)

SELECT
    trade_date,
    wall_time,
    wall_et,
    wall_side,
    wall_price,
    wall_score,

    outcome_label_30s,
    outcome_move_ticks_30s,

    wall_behavior,
    delta_flip_pattern,
    wall_aggression_pattern,

    pre_buy_vol,
    pre_sell_vol,
    touch_buy_vol,
    touch_sell_vol,
    post_buy_vol,
    post_sell_vol,

    pre_delta,
    touch_delta,
    post_delta,

    net_aggression_into_wall,
    total_aggression_near_wall,

    orb_low,
    orb_high,
    orb_range,
    orb_position,
    distance_from_orb_high,
    distance_from_orb_low,

    session_vwap,
    vwap_relation,
    distance_from_vwap,

    session_low_so_far,
    session_high_so_far,
    session_extreme_location,
    distance_from_session_high,
    distance_from_session_low,

    range_1m_pts,
    avg_range_20m,
    atr_regime,

    time_bucket,
    trade_candidate_class
FROM classified
WHERE time_bucket != 'OUTSIDE_RTH';
