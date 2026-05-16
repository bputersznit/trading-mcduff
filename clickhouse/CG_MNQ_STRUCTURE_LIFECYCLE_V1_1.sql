-- ============================================================
-- CG_mnq_structure_lifecycle_v1_1
-- Price-led structure lifecycle tracking - REBUILT
--
-- Key fixes:
--   - ORB touch = actual level interaction
--   - VWAP touch = event-gated approach/cross (not "near forever")
--   - RTH filter uses minutes since midnight
--   - No nested aggregate alias traps
--   - No nullable fields in ORDER BY
--   - CG_ naming preserved
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_structure_lifecycle_v1_1;

CREATE TABLE CG_mnq_structure_lifecycle_v1_1
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY
(
    trade_date,
    level_type,
    structure_side
)
AS
WITH
    0.25 AS tick_size,
    4.0  AS orb_touch_band_pts,
    0.75 AS vwap_event_band_pts,
    3.0  AS vwap_far_band_pts,
    90   AS max_gap_seconds
SELECT
    grouped.trade_date AS trade_date,

    cityHash64(
        grouped.trade_date,
        grouped.level_type,
        grouped.structure_side,
        grouped.level_price,
        grouped.structure_group
    ) AS structure_id,

    grouped.level_type AS level_type,
    grouped.structure_side AS structure_side,
    grouped.level_price AS level_price,

    min(grouped.event_time) AS structure_start_time,
    max(grouped.event_time) AS structure_end_time,
    dateDiff('second', min(grouped.event_time), max(grouped.event_time)) AS time_in_structure_secs,

    count() AS touch_count,

    min(grouped.low_price) AS structure_low,
    max(grouped.high_price) AS structure_high,
    max(grouped.high_price) - min(grouped.low_price) AS range_width_pts,

    sum(grouped.break_attempt) AS break_attempts,
    sum(grouped.failed_break) AS failed_breaks,
    sum(grouped.successful_break) AS successful_breaks,

    avg(grouped.impulse_pts) AS avg_impulse_pts,
    max(grouped.impulse_pts) AS max_impulse_pts,

    avg(grouped.rejection_efficiency) AS avg_rejection_efficiency,
    max(grouped.rejection_efficiency) AS max_rejection_efficiency,

    multiIf(
        count() >= 3
        AND dateDiff('second', min(grouped.event_time), max(grouped.event_time)) >= 30
        AND sum(grouped.failed_break) >= 1,
        'MATURE',

        count() >= 2
        AND dateDiff('second', min(grouped.event_time), max(grouped.event_time)) >= 15,
        'FORMING',

        'IMMATURE'
    ) AS maturity_state

FROM
(
    SELECT
        flagged.trade_date AS trade_date,
        flagged.event_time AS event_time,
        flagged.level_type AS level_type,
        flagged.structure_side AS structure_side,
        flagged.level_price AS level_price,
        flagged.high_price AS high_price,
        flagged.low_price AS low_price,
        flagged.close_price AS close_price,
        flagged.break_attempt AS break_attempt,
        flagged.failed_break AS failed_break,
        flagged.successful_break AS successful_break,
        flagged.impulse_pts AS impulse_pts,
        flagged.rejection_efficiency AS rejection_efficiency,

        sum(flagged.new_structure_flag) OVER
        (
            PARTITION BY
                flagged.trade_date,
                flagged.level_type,
                flagged.structure_side,
                flagged.level_price
            ORDER BY flagged.event_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS structure_group

    FROM
    (
        SELECT
            events.trade_date AS trade_date,
            events.event_time AS event_time,
            events.level_type AS level_type,
            events.structure_side AS structure_side,
            events.level_price AS level_price,
            events.high_price AS high_price,
            events.low_price AS low_price,
            events.close_price AS close_price,
            events.break_attempt AS break_attempt,
            events.failed_break AS failed_break,
            events.successful_break AS successful_break,
            events.impulse_pts AS impulse_pts,
            events.rejection_efficiency AS rejection_efficiency,

            multiIf(
                lagInFrame(events.event_time) OVER
                (
                    PARTITION BY
                        events.trade_date,
                        events.level_type,
                        events.structure_side,
                        events.level_price
                    ORDER BY events.event_time
                    ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
                ) IS NULL,
                1,

                dateDiff(
                    'second',
                    lagInFrame(events.event_time) OVER
                    (
                        PARTITION BY
                            events.trade_date,
                            events.level_type,
                            events.structure_side,
                            events.level_price
                        ORDER BY events.event_time
                        ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
                    ),
                    events.event_time
                ) > max_gap_seconds,
                1,

                0
            ) AS new_structure_flag

        FROM
        (
            /* ------------------------------------------------------------
               ORB HARD TOUCH EVENTS
               ------------------------------------------------------------ */
            SELECT
                orb_touch.trade_date AS trade_date,
                orb_touch.event_time AS event_time,
                orb_touch.level_type AS level_type,
                orb_touch.structure_side AS structure_side,
                orb_touch.level_price AS level_price,
                orb_touch.high_price AS high_price,
                orb_touch.low_price AS low_price,
                orb_touch.close_price AS close_price,

                multiIf(
                    orb_touch.structure_side = 'RESISTANCE'
                    AND orb_touch.high_price > orb_touch.level_price,
                    1,

                    orb_touch.structure_side = 'SUPPORT'
                    AND orb_touch.low_price < orb_touch.level_price,
                    1,

                    0
                ) AS break_attempt,

                multiIf(
                    orb_touch.structure_side = 'RESISTANCE'
                    AND orb_touch.high_price > orb_touch.level_price
                    AND orb_touch.close_price <= orb_touch.level_price,
                    1,

                    orb_touch.structure_side = 'SUPPORT'
                    AND orb_touch.low_price < orb_touch.level_price
                    AND orb_touch.close_price >= orb_touch.level_price,
                    1,

                    0
                ) AS failed_break,

                multiIf(
                    orb_touch.structure_side = 'RESISTANCE'
                    AND orb_touch.close_price > orb_touch.level_price,
                    1,

                    orb_touch.structure_side = 'SUPPORT'
                    AND orb_touch.close_price < orb_touch.level_price,
                    1,

                    0
                ) AS successful_break,

                abs(orb_touch.close_price - orb_touch.level_price) AS impulse_pts,

                multiIf(
                    orb_touch.structure_side = 'RESISTANCE'
                    AND orb_touch.high_price > orb_touch.level_price,
                    (orb_touch.high_price - orb_touch.close_price)
                    / nullIf(orb_touch.high_price - orb_touch.low_price, 0),

                    orb_touch.structure_side = 'SUPPORT'
                    AND orb_touch.low_price < orb_touch.level_price,
                    (orb_touch.close_price - orb_touch.low_price)
                    / nullIf(orb_touch.high_price - orb_touch.low_price, 0),

                    0
                ) AS rejection_efficiency

            FROM
            (
                SELECT
                    base.trade_date AS trade_date,
                    base.event_time AS event_time,
                    levels.level_type AS level_type,
                    levels.structure_side AS structure_side,
                    levels.level_price AS level_price,
                    base.high_price AS high_price,
                    base.low_price AS low_price,
                    base.close_price AS close_price
                FROM
                (
                    SELECT
                        toDate(toTimeZone(t, 'America/New_York')) AS trade_date,
                        t AS event_time,
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
                        ) BETWEEN 570 AND 960
                ) AS base
                INNER JOIN
                (
                    SELECT
                        trade_date AS trade_date,
                        'ORB_HIGH' AS level_type,
                        'RESISTANCE' AS structure_side,
                        any(orb_high) AS level_price
                    FROM CG_mnq_session_regime_v2
                    GROUP BY trade_date

                    UNION ALL

                    SELECT
                        trade_date AS trade_date,
                        'ORB_LOW' AS level_type,
                        'SUPPORT' AS structure_side,
                        any(orb_low) AS level_price
                    FROM CG_mnq_session_regime_v2
                    GROUP BY trade_date
                ) AS levels
                    ON base.trade_date = levels.trade_date
                WHERE
                    (
                        levels.structure_side = 'RESISTANCE'
                        AND base.high_price >= levels.level_price
                        AND base.low_price <= levels.level_price + orb_touch_band_pts
                    )
                    OR
                    (
                        levels.structure_side = 'SUPPORT'
                        AND base.low_price <= levels.level_price
                        AND base.high_price >= levels.level_price - orb_touch_band_pts
                    )
            ) AS orb_touch

            UNION ALL

            /* ------------------------------------------------------------
               VWAP EVENT-GATED TOUCHES
               ------------------------------------------------------------ */
            SELECT
                vwap_event.trade_date AS trade_date,
                vwap_event.event_time AS event_time,
                'VWAP' AS level_type,
                vwap_event.structure_side AS structure_side,
                vwap_event.level_price AS level_price,
                vwap_event.high_price AS high_price,
                vwap_event.low_price AS low_price,
                vwap_event.close_price AS close_price,

                1 AS break_attempt,

                multiIf(
                    vwap_event.structure_side = 'RESISTANCE'
                    AND vwap_event.high_price >= vwap_event.level_price
                    AND vwap_event.close_price <= vwap_event.level_price,
                    1,

                    vwap_event.structure_side = 'SUPPORT'
                    AND vwap_event.low_price <= vwap_event.level_price
                    AND vwap_event.close_price >= vwap_event.level_price,
                    1,

                    0
                ) AS failed_break,

                multiIf(
                    vwap_event.structure_side = 'RESISTANCE'
                    AND vwap_event.close_price > vwap_event.level_price,
                    1,

                    vwap_event.structure_side = 'SUPPORT'
                    AND vwap_event.close_price < vwap_event.level_price,
                    1,

                    0
                ) AS successful_break,

                abs(vwap_event.close_price - vwap_event.level_price) AS impulse_pts,

                multiIf(
                    vwap_event.structure_side = 'RESISTANCE',
                    (vwap_event.high_price - vwap_event.close_price)
                    / nullIf(vwap_event.high_price - vwap_event.low_price, 0),

                    vwap_event.structure_side = 'SUPPORT',
                    (vwap_event.close_price - vwap_event.low_price)
                    / nullIf(vwap_event.high_price - vwap_event.low_price, 0),

                    0
                ) AS rejection_efficiency

            FROM
            (
                SELECT
                    marked.trade_date AS trade_date,
                    marked.event_time AS event_time,
                    marked.high_price AS high_price,
                    marked.low_price AS low_price,
                    marked.close_price AS close_price,
                    marked.level_price AS level_price,

                    multiIf(
                        marked.prev_close_price < marked.prev_level_price
                        AND marked.close_price >= marked.level_price,
                        'SUPPORT',

                        marked.prev_close_price > marked.prev_level_price
                        AND marked.close_price <= marked.level_price,
                        'RESISTANCE',

                        marked.close_price >= marked.level_price,
                        'SUPPORT',

                        'RESISTANCE'
                    ) AS structure_side

                FROM
                (
                    SELECT
                        vwap_base.trade_date AS trade_date,
                        vwap_base.event_time AS event_time,
                        vwap_base.high_price AS high_price,
                        vwap_base.low_price AS low_price,
                        vwap_base.close_price AS close_price,
                        vwap_base.level_price AS level_price,

                        lagInFrame(vwap_base.close_price) OVER
                        (
                            PARTITION BY vwap_base.trade_date
                            ORDER BY vwap_base.event_time
                            ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
                        ) AS prev_close_price,

                        lagInFrame(vwap_base.level_price) OVER
                        (
                            PARTITION BY vwap_base.trade_date
                            ORDER BY vwap_base.event_time
                            ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
                        ) AS prev_level_price,

                        abs(vwap_base.close_price - vwap_base.level_price) AS dist_to_vwap,

                        abs(
                            lagInFrame(vwap_base.close_price) OVER
                            (
                                PARTITION BY vwap_base.trade_date
                                ORDER BY vwap_base.event_time
                                ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
                            )
                            -
                            lagInFrame(vwap_base.level_price) OVER
                            (
                                PARTITION BY vwap_base.trade_date
                                ORDER BY vwap_base.event_time
                                ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
                            )
                        ) AS prev_dist_to_vwap

                    FROM
                    (
                        SELECT
                            toDate(toTimeZone(o.t, 'America/New_York')) AS trade_date,
                            o.t AS event_time,
                            o.hi_px AS high_price,
                            o.lo_px AS low_price,
                            o.close_px AS close_price,
                            round(r.vwap / tick_size) * tick_size AS level_price
                        FROM mnq_ohlc_5s AS o
                        INNER JOIN CG_mnq_session_regime_v2 AS r
                            ON toDate(toTimeZone(o.t, 'America/New_York')) = r.trade_date
                           AND o.t = r.ts_5s
                        WHERE
                            (
                                toHour(toTimeZone(o.t, 'America/New_York')) * 60
                                + toMinute(toTimeZone(o.t, 'America/New_York'))
                            ) BETWEEN 570 AND 960
                            AND r.vwap IS NOT NULL
                    ) AS vwap_base
                ) AS marked
                WHERE
                    marked.prev_close_price IS NOT NULL
                    AND
                    (
                        /* Cross through VWAP */
                        (
                            marked.prev_close_price < marked.prev_level_price
                            AND marked.close_price >= marked.level_price
                        )
                        OR
                        (
                            marked.prev_close_price > marked.prev_level_price
                            AND marked.close_price <= marked.level_price
                        )

                        /* Or approach from far to near */
                        OR
                        (
                            marked.prev_dist_to_vwap >= vwap_far_band_pts
                            AND marked.dist_to_vwap <= vwap_event_band_pts
                        )
                    )
            ) AS vwap_event
        ) AS events
    ) AS flagged
) AS grouped
GROUP BY
    grouped.trade_date,
    grouped.level_type,
    grouped.structure_side,
    grouped.level_price,
    grouped.structure_group;
