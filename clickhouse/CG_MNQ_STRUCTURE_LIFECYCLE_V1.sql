-- ============================================================
-- CG_mnq_structure_lifecycle_v1
-- Price-led structure lifecycle tracking
--
-- Input:
--   mnq_ohlc_5s
--   CG_mnq_regime_v2
--
-- Output:
--   CG_mnq_structure_lifecycle_v1
--
-- Purpose:
--   Track support/resistance lifecycle at key levels:
--     - ORB high/low
--     - VWAP
--   Measure touch count, break attempts, rejections, maturity
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_structure_lifecycle_v1;

CREATE TABLE CG_mnq_structure_lifecycle_v1
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, structure_side, level_type)
AS
WITH
    0.25 AS tick_size,
    4.0  AS level_band_pts,
    90   AS max_gap_seconds
SELECT
    structure.trade_date                                      AS trade_date,
    structure.structure_id                                    AS structure_id,
    structure.structure_side                                  AS structure_side,
    structure.level_type                                      AS level_type,
    structure.level_price                                     AS level_price,

    min(structure.event_time)                                 AS structure_start_time,
    max(structure.event_time)                                 AS structure_end_time,
    dateDiff('second', min(structure.event_time), max(structure.event_time)) AS time_in_structure_secs,

    count()                                                   AS touch_count,

    min(structure.low_price)                                  AS structure_low,
    max(structure.high_price)                                 AS structure_high,
    max(structure.high_price) - min(structure.low_price)      AS range_width_pts,

    sum(structure.break_attempt)                              AS break_attempts,
    sum(structure.failed_break)                               AS failed_breaks,
    sum(structure.successful_break)                           AS successful_breaks,

    avg(structure.impulse_pts)                                AS avg_impulse_pts,
    max(structure.impulse_pts)                                AS max_impulse_pts,

    avg(structure.rejection_efficiency)                       AS avg_rejection_efficiency,
    max(structure.rejection_efficiency)                       AS max_rejection_efficiency,

    multiIf(
        count() >= 3
        AND dateDiff('second', min(structure.event_time), max(structure.event_time)) >= 30
        AND sum(structure.failed_break) >= 1,
        'MATURE',

        count() >= 2
        AND dateDiff('second', min(structure.event_time), max(structure.event_time)) >= 15,
        'FORMING',

        'IMMATURE'
    )                                                         AS maturity_state

FROM
(
    SELECT
        segmented.trade_date                                  AS trade_date,
        cityHash64(
            segmented.trade_date,
            segmented.structure_side,
            segmented.level_type,
            segmented.level_price,
            segmented.structure_group
        )                                                     AS structure_id,

        segmented.structure_side                              AS structure_side,
        segmented.level_type                                  AS level_type,
        segmented.level_price                                 AS level_price,
        segmented.event_time                                  AS event_time,

        segmented.high_price                                  AS high_price,
        segmented.low_price                                   AS low_price,
        segmented.close_price                                 AS close_price,

        segmented.break_attempt                               AS break_attempt,
        segmented.failed_break                                AS failed_break,
        segmented.successful_break                            AS successful_break,
        segmented.impulse_pts                                 AS impulse_pts,
        segmented.rejection_efficiency                        AS rejection_efficiency
    FROM
    (
        SELECT
            marked.trade_date                                 AS trade_date,
            marked.structure_side                             AS structure_side,
            marked.level_type                                 AS level_type,
            marked.level_price                                AS level_price,
            marked.event_time                                 AS event_time,
            marked.high_price                                 AS high_price,
            marked.low_price                                  AS low_price,
            marked.close_price                                AS close_price,
            marked.break_attempt                              AS break_attempt,
            marked.failed_break                               AS failed_break,
            marked.successful_break                           AS successful_break,
            marked.impulse_pts                                AS impulse_pts,
            marked.rejection_efficiency                       AS rejection_efficiency,

            sum(marked.new_structure_flag) OVER
            (
                PARTITION BY
                    marked.trade_date,
                    marked.structure_side,
                    marked.level_type,
                    marked.level_price
                ORDER BY marked.event_time
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
            )                                                 AS structure_group
        FROM
        (
            SELECT
                touched.trade_date                            AS trade_date,
                touched.structure_side                        AS structure_side,
                touched.level_type                            AS level_type,
                touched.level_price                           AS level_price,
                touched.event_time                            AS event_time,
                touched.high_price                            AS high_price,
                touched.low_price                             AS low_price,
                touched.close_price                           AS close_price,
                touched.break_attempt                         AS break_attempt,
                touched.failed_break                          AS failed_break,
                touched.successful_break                      AS successful_break,
                touched.impulse_pts                           AS impulse_pts,
                touched.rejection_efficiency                  AS rejection_efficiency,

                multiIf(
                    lagInFrame(touched.event_time) OVER
                    (
                        PARTITION BY
                            touched.trade_date,
                            touched.structure_side,
                            touched.level_type,
                            touched.level_price
                        ORDER BY touched.event_time
                        ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
                    ) IS NULL,
                    1,

                    dateDiff(
                        'second',
                        lagInFrame(touched.event_time) OVER
                        (
                            PARTITION BY
                                touched.trade_date,
                                touched.structure_side,
                                touched.level_type,
                                touched.level_price
                            ORDER BY touched.event_time
                            ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
                        ),
                        touched.event_time
                    ) > max_gap_seconds,
                    1,

                    0
                )                                             AS new_structure_flag
            FROM
            (
                SELECT
                    base.trade_date                           AS trade_date,
                    base.event_time                           AS event_time,
                    base.high_price                           AS high_price,
                    base.low_price                            AS low_price,
                    base.close_price                          AS close_price,

                    levels.structure_side                     AS structure_side,
                    levels.level_type                         AS level_type,
                    levels.level_price                        AS level_price,

                    multiIf(
                        levels.structure_side = 'RESISTANCE'
                        AND base.high_price > levels.level_price,
                        1,

                        levels.structure_side = 'SUPPORT'
                        AND base.low_price < levels.level_price,
                        1,

                        0
                    )                                         AS break_attempt,

                    multiIf(
                        levels.structure_side = 'RESISTANCE'
                        AND base.high_price > levels.level_price
                        AND base.close_price <= levels.level_price,
                        1,

                        levels.structure_side = 'SUPPORT'
                        AND base.low_price < levels.level_price
                        AND base.close_price >= levels.level_price,
                        1,

                        0
                    )                                         AS failed_break,

                    multiIf(
                        levels.structure_side = 'RESISTANCE'
                        AND base.close_price > levels.level_price,
                        1,

                        levels.structure_side = 'SUPPORT'
                        AND base.close_price < levels.level_price,
                        1,

                        0
                    )                                         AS successful_break,

                    abs(base.close_price - levels.level_price) AS impulse_pts,

                    multiIf(
                        levels.structure_side = 'RESISTANCE'
                        AND base.high_price > levels.level_price,
                        (base.high_price - base.close_price)
                        / nullIf(base.high_price - base.low_price, 0),

                        levels.structure_side = 'SUPPORT'
                        AND base.low_price < levels.level_price,
                        (base.close_price - base.low_price)
                        / nullIf(base.high_price - base.low_price, 0),

                        0
                    )                                         AS rejection_efficiency
                FROM
                (
                    SELECT
                        toDate(toTimeZone(t, 'America/New_York')) AS trade_date,
                        t                                          AS event_time,
                        hi_px                                      AS high_price,
                        lo_px                                      AS low_price,
                        close_px                                   AS close_price
                    FROM mnq_ohlc_5s
                    WHERE toHour(toTimeZone(t, 'America/New_York')) * 60 + toMinute(toTimeZone(t, 'America/New_York')) >= 570
                      AND toHour(toTimeZone(t, 'America/New_York')) * 60 + toMinute(toTimeZone(t, 'America/New_York')) <= 960
                ) AS base
                INNER JOIN
                (
                    SELECT
                        trade_date,
                        'ORB_HIGH'                                AS level_type,
                        'RESISTANCE'                              AS structure_side,
                        orb_high                                  AS level_price
                    FROM CG_mnq_wall_outcomes_regime_v1
                    GROUP BY
                        trade_date,
                        orb_high

                    UNION ALL

                    SELECT
                        trade_date,
                        'ORB_LOW'                                 AS level_type,
                        'SUPPORT'                                 AS structure_side,
                        orb_low                                   AS level_price
                    FROM CG_mnq_wall_outcomes_regime_v1
                    GROUP BY
                        trade_date,
                        orb_low

                    UNION ALL

                    SELECT
                        trade_date,
                        'VWAP'                                    AS level_type,
                        'SUPPORT'                                 AS structure_side,
                        round(session_vwap / tick_size) * tick_size AS level_price
                    FROM CG_mnq_wall_outcomes_regime_v1
                    WHERE session_vwap IS NOT NULL
                    GROUP BY
                        trade_date,
                        session_vwap
                ) AS levels
                    ON base.trade_date = levels.trade_date
                WHERE base.high_price >= levels.level_price - level_band_pts
                  AND base.low_price  <= levels.level_price + level_band_pts
            ) AS touched
        ) AS marked
    ) AS segmented
) AS structure
GROUP BY
    structure.trade_date,
    structure.structure_id,
    structure.structure_side,
    structure.level_type,
    structure.level_price;
