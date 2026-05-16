-- ============================================================
-- CG_mnq_structure_aggression_quality_v1
-- Structure aggression with normalized magnitude and quality labels
--
-- Upgrades from raw alignment to:
--   - Binary alignment (direction match)
--   - Normalized magnitude (P95-scaled)
--   - Weighted timeframe scores (longer = higher weight)
--   - Start / midpoint / end phase sampling
--   - Participation quality labels
--   - Behavior candidate classification
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_structure_aggression_quality_v1;

CREATE TABLE CG_mnq_structure_aggression_quality_v1
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY
(
    trade_date,
    level_type,
    structure_side,
    maturity_state
)
AS
WITH
    0.25 AS tick_size
SELECT
    scored.trade_date AS trade_date,
    scored.structure_id AS structure_id,
    scored.level_type AS level_type,
    scored.structure_side AS structure_side,
    scored.expected_trade_side AS expected_trade_side,
    scored.level_price AS level_price,
    scored.structure_start_time AS structure_start_time,
    scored.structure_mid_time AS structure_mid_time,
    scored.structure_end_time AS structure_end_time,
    scored.time_in_structure_secs AS time_in_structure_secs,
    scored.touch_count AS touch_count,
    scored.range_width_pts AS range_width_pts,
    scored.break_attempts AS break_attempts,
    scored.failed_breaks AS failed_breaks,
    scored.successful_breaks AS successful_breaks,
    scored.maturity_state AS maturity_state,

    scored.start_weighted_alignment_score AS start_weighted_alignment_score,
    scored.mid_weighted_alignment_score AS mid_weighted_alignment_score,
    scored.end_weighted_alignment_score AS end_weighted_alignment_score,

    scored.start_raw_alignment_score AS start_raw_alignment_score,
    scored.mid_raw_alignment_score AS mid_raw_alignment_score,
    scored.end_raw_alignment_score AS end_raw_alignment_score,

    scored.alignment_quality_delta AS alignment_quality_delta,
    scored.alignment_quality_peak AS alignment_quality_peak,

    multiIf(
        scored.end_weighted_alignment_score >= 0.70
        AND scored.alignment_quality_delta > 0,
        'STRENGTHENING_HIGH',

        scored.end_weighted_alignment_score >= 0.50
        AND scored.alignment_quality_delta >= 0,
        'STRENGTHENING_MODERATE',

        scored.start_weighted_alignment_score >= 0.50
        AND scored.end_weighted_alignment_score < scored.start_weighted_alignment_score - 0.20,
        'EXHAUSTING',

        scored.end_weighted_alignment_score <= 0.25,
        'WEAK_OR_OPPOSED',

        'NEUTRAL'
    ) AS aggression_quality_state,

    multiIf(
        scored.level_type = 'ORB_HIGH'
        AND scored.structure_side = 'RESISTANCE'
        AND scored.maturity_state = 'MATURE'
        AND scored.alignment_quality_delta > 0,
        'ORB_HIGH_CONTINUATION_CANDIDATE',

        scored.level_type = 'ORB_LOW'
        AND scored.structure_side = 'SUPPORT'
        AND scored.maturity_state = 'MATURE'
        AND scored.alignment_quality_delta < 0,
        'ORB_LOW_EXHAUSTION_OR_TRAP_CANDIDATE',

        scored.level_type = 'VWAP'
        AND scored.structure_side = 'SUPPORT'
        AND scored.alignment_quality_delta > 0,
        'VWAP_SUPPORT_REVERSION_CANDIDATE',

        scored.level_type = 'VWAP'
        AND scored.structure_side = 'RESISTANCE'
        AND scored.alignment_quality_delta < 0,
        'VWAP_RESISTANCE_REVERSION_CANDIDATE',

        'UNCLASSIFIED'
    ) AS behavior_candidate,

    scored.start_norm_100ms AS start_norm_100ms,
    scored.start_norm_500ms AS start_norm_500ms,
    scored.start_norm_1s AS start_norm_1s,
    scored.start_norm_3s AS start_norm_3s,
    scored.start_norm_5s AS start_norm_5s,
    scored.start_norm_15s AS start_norm_15s,
    scored.start_norm_30s AS start_norm_30s,
    scored.start_norm_1m AS start_norm_1m,
    scored.start_norm_5m AS start_norm_5m,

    scored.mid_norm_100ms AS mid_norm_100ms,
    scored.mid_norm_500ms AS mid_norm_500ms,
    scored.mid_norm_1s AS mid_norm_1s,
    scored.mid_norm_3s AS mid_norm_3s,
    scored.mid_norm_5s AS mid_norm_5s,
    scored.mid_norm_15s AS mid_norm_15s,
    scored.mid_norm_30s AS mid_norm_30s,
    scored.mid_norm_1m AS mid_norm_1m,
    scored.mid_norm_5m AS mid_norm_5m,

    scored.end_norm_100ms AS end_norm_100ms,
    scored.end_norm_500ms AS end_norm_500ms,
    scored.end_norm_1s AS end_norm_1s,
    scored.end_norm_3s AS end_norm_3s,
    scored.end_norm_5s AS end_norm_5s,
    scored.end_norm_15s AS end_norm_15s,
    scored.end_norm_30s AS end_norm_30s,
    scored.end_norm_1m AS end_norm_1m,
    scored.end_norm_5m AS end_norm_5m

FROM
(
    SELECT
        joined.trade_date AS trade_date,
        joined.structure_id AS structure_id,
        joined.level_type AS level_type,
        joined.structure_side AS structure_side,
        joined.expected_trade_side AS expected_trade_side,
        joined.level_price AS level_price,
        joined.structure_start_time AS structure_start_time,
        joined.structure_mid_time AS structure_mid_time,
        joined.structure_end_time AS structure_end_time,
        joined.time_in_structure_secs AS time_in_structure_secs,
        joined.touch_count AS touch_count,
        joined.range_width_pts AS range_width_pts,
        joined.break_attempts AS break_attempts,
        joined.failed_breaks AS failed_breaks,
        joined.successful_breaks AS successful_breaks,
        joined.maturity_state AS maturity_state,

        joined.start_raw_alignment_score AS start_raw_alignment_score,
        joined.mid_raw_alignment_score AS mid_raw_alignment_score,
        joined.end_raw_alignment_score AS end_raw_alignment_score,

        joined.start_weighted_alignment_score AS start_weighted_alignment_score,
        joined.mid_weighted_alignment_score AS mid_weighted_alignment_score,
        joined.end_weighted_alignment_score AS end_weighted_alignment_score,

        joined.end_weighted_alignment_score - joined.start_weighted_alignment_score AS alignment_quality_delta,

        greatest(
            joined.start_weighted_alignment_score,
            joined.mid_weighted_alignment_score,
            joined.end_weighted_alignment_score
        ) AS alignment_quality_peak,

        joined.start_norm_100ms AS start_norm_100ms,
        joined.start_norm_500ms AS start_norm_500ms,
        joined.start_norm_1s AS start_norm_1s,
        joined.start_norm_3s AS start_norm_3s,
        joined.start_norm_5s AS start_norm_5s,
        joined.start_norm_15s AS start_norm_15s,
        joined.start_norm_30s AS start_norm_30s,
        joined.start_norm_1m AS start_norm_1m,
        joined.start_norm_5m AS start_norm_5m,

        joined.mid_norm_100ms AS mid_norm_100ms,
        joined.mid_norm_500ms AS mid_norm_500ms,
        joined.mid_norm_1s AS mid_norm_1s,
        joined.mid_norm_3s AS mid_norm_3s,
        joined.mid_norm_5s AS mid_norm_5s,
        joined.mid_norm_15s AS mid_norm_15s,
        joined.mid_norm_30s AS mid_norm_30s,
        joined.mid_norm_1m AS mid_norm_1m,
        joined.mid_norm_5m AS mid_norm_5m,

        joined.end_norm_100ms AS end_norm_100ms,
        joined.end_norm_500ms AS end_norm_500ms,
        joined.end_norm_1s AS end_norm_1s,
        joined.end_norm_3s AS end_norm_3s,
        joined.end_norm_5s AS end_norm_5s,
        joined.end_norm_15s AS end_norm_15s,
        joined.end_norm_30s AS end_norm_30s,
        joined.end_norm_1m AS end_norm_1m,
        joined.end_norm_5m AS end_norm_5m

    FROM
    (
        SELECT
            s.trade_date AS trade_date,
            s.structure_id AS structure_id,
            s.level_type AS level_type,
            s.structure_side AS structure_side,
            s.expected_trade_side AS expected_trade_side,
            s.level_price AS level_price,
            s.structure_start_time AS structure_start_time,
            s.structure_end_time AS structure_end_time,

            s.structure_start_time
                + toIntervalSecond(intDiv(s.time_in_structure_secs, 2)) AS structure_mid_time,

            s.time_in_structure_secs AS time_in_structure_secs,
            s.touch_count AS touch_count,
            s.range_width_pts AS range_width_pts,
            s.break_attempts AS break_attempts,
            s.failed_breaks AS failed_breaks,
            s.successful_breaks AS successful_breaks,
            s.maturity_state AS maturity_state,

            /* Raw alignment scores */
            (
                multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_100ms > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_start.exec_delta_100ms < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_500ms > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_start.exec_delta_500ms < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_1s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_start.exec_delta_1s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_3s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_start.exec_delta_3s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_5s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_start.exec_delta_5s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_15s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_start.exec_delta_15s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_30s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_start.exec_delta_30s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_1m > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_start.exec_delta_1m < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_5m > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_start.exec_delta_5m < 0, 1, 0)
            ) AS start_raw_alignment_score,

            (
                multiIf(s.structure_side = 'SUPPORT' AND a_mid.exec_delta_100ms > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_mid.exec_delta_100ms < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_mid.exec_delta_500ms > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_mid.exec_delta_500ms < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_mid.exec_delta_1s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_mid.exec_delta_1s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_mid.exec_delta_3s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_mid.exec_delta_3s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_mid.exec_delta_5s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_mid.exec_delta_5s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_mid.exec_delta_15s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_mid.exec_delta_15s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_mid.exec_delta_30s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_mid.exec_delta_30s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_mid.exec_delta_1m > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_mid.exec_delta_1m < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_mid.exec_delta_5m > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_mid.exec_delta_5m < 0, 1, 0)
            ) AS mid_raw_alignment_score,

            (
                multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_100ms > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_end.exec_delta_100ms < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_500ms > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_end.exec_delta_500ms < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_1s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_end.exec_delta_1s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_3s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_end.exec_delta_3s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_5s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_end.exec_delta_5s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_15s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_end.exec_delta_15s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_30s > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_end.exec_delta_30s < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_1m > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_end.exec_delta_1m < 0, 1, 0)
              + multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_5m > 0, 1,
                        s.structure_side = 'RESISTANCE' AND a_end.exec_delta_5m < 0, 1, 0)
            ) AS end_raw_alignment_score,

            /* Normalized directional magnitude by phase and timeframe */
            multiIf(s.structure_side = 'SUPPORT', greatest(a_start.exec_delta_100ms, 0), greatest(-a_start.exec_delta_100ms, 0)) / q.p95_abs_delta_100ms AS start_norm_100ms,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_start.exec_delta_500ms, 0), greatest(-a_start.exec_delta_500ms, 0)) / q.p95_abs_delta_500ms AS start_norm_500ms,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_start.exec_delta_1s, 0), greatest(-a_start.exec_delta_1s, 0)) / q.p95_abs_delta_1s AS start_norm_1s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_start.exec_delta_3s, 0), greatest(-a_start.exec_delta_3s, 0)) / q.p95_abs_delta_3s AS start_norm_3s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_start.exec_delta_5s, 0), greatest(-a_start.exec_delta_5s, 0)) / q.p95_abs_delta_5s AS start_norm_5s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_start.exec_delta_15s, 0), greatest(-a_start.exec_delta_15s, 0)) / q.p95_abs_delta_15s AS start_norm_15s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_start.exec_delta_30s, 0), greatest(-a_start.exec_delta_30s, 0)) / q.p95_abs_delta_30s AS start_norm_30s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_start.exec_delta_1m, 0), greatest(-a_start.exec_delta_1m, 0)) / q.p95_abs_delta_1m AS start_norm_1m,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_start.exec_delta_5m, 0), greatest(-a_start.exec_delta_5m, 0)) / q.p95_abs_delta_5m AS start_norm_5m,

            multiIf(s.structure_side = 'SUPPORT', greatest(a_mid.exec_delta_100ms, 0), greatest(-a_mid.exec_delta_100ms, 0)) / q.p95_abs_delta_100ms AS mid_norm_100ms,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_mid.exec_delta_500ms, 0), greatest(-a_mid.exec_delta_500ms, 0)) / q.p95_abs_delta_500ms AS mid_norm_500ms,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_mid.exec_delta_1s, 0), greatest(-a_mid.exec_delta_1s, 0)) / q.p95_abs_delta_1s AS mid_norm_1s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_mid.exec_delta_3s, 0), greatest(-a_mid.exec_delta_3s, 0)) / q.p95_abs_delta_3s AS mid_norm_3s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_mid.exec_delta_5s, 0), greatest(-a_mid.exec_delta_5s, 0)) / q.p95_abs_delta_5s AS mid_norm_5s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_mid.exec_delta_15s, 0), greatest(-a_mid.exec_delta_15s, 0)) / q.p95_abs_delta_15s AS mid_norm_15s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_mid.exec_delta_30s, 0), greatest(-a_mid.exec_delta_30s, 0)) / q.p95_abs_delta_30s AS mid_norm_30s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_mid.exec_delta_1m, 0), greatest(-a_mid.exec_delta_1m, 0)) / q.p95_abs_delta_1m AS mid_norm_1m,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_mid.exec_delta_5m, 0), greatest(-a_mid.exec_delta_5m, 0)) / q.p95_abs_delta_5m AS mid_norm_5m,

            multiIf(s.structure_side = 'SUPPORT', greatest(a_end.exec_delta_100ms, 0), greatest(-a_end.exec_delta_100ms, 0)) / q.p95_abs_delta_100ms AS end_norm_100ms,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_end.exec_delta_500ms, 0), greatest(-a_end.exec_delta_500ms, 0)) / q.p95_abs_delta_500ms AS end_norm_500ms,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_end.exec_delta_1s, 0), greatest(-a_end.exec_delta_1s, 0)) / q.p95_abs_delta_1s AS end_norm_1s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_end.exec_delta_3s, 0), greatest(-a_end.exec_delta_3s, 0)) / q.p95_abs_delta_3s AS end_norm_3s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_end.exec_delta_5s, 0), greatest(-a_end.exec_delta_5s, 0)) / q.p95_abs_delta_5s AS end_norm_5s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_end.exec_delta_15s, 0), greatest(-a_end.exec_delta_15s, 0)) / q.p95_abs_delta_15s AS end_norm_15s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_end.exec_delta_30s, 0), greatest(-a_end.exec_delta_30s, 0)) / q.p95_abs_delta_30s AS end_norm_30s,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_end.exec_delta_1m, 0), greatest(-a_end.exec_delta_1m, 0)) / q.p95_abs_delta_1m AS end_norm_1m,
            multiIf(s.structure_side = 'SUPPORT', greatest(a_end.exec_delta_5m, 0), greatest(-a_end.exec_delta_5m, 0)) / q.p95_abs_delta_5m AS end_norm_5m,

            /* Weighted normalized alignment scores, clipped at 1.0 */
            (
                1.0 * least(start_norm_100ms, 1.0)
              + 1.0 * least(start_norm_500ms, 1.0)
              + 1.5 * least(start_norm_1s, 1.0)
              + 2.0 * least(start_norm_3s, 1.0)
              + 2.5 * least(start_norm_5s, 1.0)
              + 3.0 * least(start_norm_15s, 1.0)
              + 3.0 * least(start_norm_30s, 1.0)
              + 4.0 * least(start_norm_1m, 1.0)
              + 5.0 * least(start_norm_5m, 1.0)
            ) / 23.0 AS start_weighted_alignment_score,

            (
                1.0 * least(mid_norm_100ms, 1.0)
              + 1.0 * least(mid_norm_500ms, 1.0)
              + 1.5 * least(mid_norm_1s, 1.0)
              + 2.0 * least(mid_norm_3s, 1.0)
              + 2.5 * least(mid_norm_5s, 1.0)
              + 3.0 * least(mid_norm_15s, 1.0)
              + 3.0 * least(mid_norm_30s, 1.0)
              + 4.0 * least(mid_norm_1m, 1.0)
              + 5.0 * least(mid_norm_5m, 1.0)
            ) / 23.0 AS mid_weighted_alignment_score,

            (
                1.0 * least(end_norm_100ms, 1.0)
              + 1.0 * least(end_norm_500ms, 1.0)
              + 1.5 * least(end_norm_1s, 1.0)
              + 2.0 * least(end_norm_3s, 1.0)
              + 2.5 * least(end_norm_5s, 1.0)
              + 3.0 * least(end_norm_15s, 1.0)
              + 3.0 * least(end_norm_30s, 1.0)
              + 4.0 * least(end_norm_1m, 1.0)
              + 5.0 * least(end_norm_5m, 1.0)
            ) / 23.0 AS end_weighted_alignment_score

        FROM CG_mnq_structure_lifecycle_aggression_v1 AS s

        LEFT JOIN CG_mnq_aggression_multiscale_v1 AS a_start
            ON toStartOfInterval(
                toDateTime64(s.structure_start_time, 3, 'UTC'),
                toIntervalMillisecond(100)
            ) = a_start.ts_100ms

        LEFT JOIN CG_mnq_aggression_multiscale_v1 AS a_mid
            ON toStartOfInterval(
                toDateTime64(
                    s.structure_start_time + toIntervalSecond(intDiv(s.time_in_structure_secs, 2)),
                    3,
                    'UTC'
                ),
                toIntervalMillisecond(100)
            ) = a_mid.ts_100ms

        LEFT JOIN CG_mnq_aggression_multiscale_v1 AS a_end
            ON toStartOfInterval(
                toDateTime64(s.structure_end_time, 3, 'UTC'),
                toIntervalMillisecond(100)
            ) = a_end.ts_100ms

        CROSS JOIN
        (
            SELECT
                nullIf(quantileExact(0.95)(abs(exec_delta_100ms)), 0) AS p95_abs_delta_100ms,
                nullIf(quantileExact(0.95)(abs(exec_delta_500ms)), 0) AS p95_abs_delta_500ms,
                nullIf(quantileExact(0.95)(abs(exec_delta_1s)), 0) AS p95_abs_delta_1s,
                nullIf(quantileExact(0.95)(abs(exec_delta_3s)), 0) AS p95_abs_delta_3s,
                nullIf(quantileExact(0.95)(abs(exec_delta_5s)), 0) AS p95_abs_delta_5s,
                nullIf(quantileExact(0.95)(abs(exec_delta_15s)), 0) AS p95_abs_delta_15s,
                nullIf(quantileExact(0.95)(abs(exec_delta_30s)), 0) AS p95_abs_delta_30s,
                nullIf(quantileExact(0.95)(abs(exec_delta_1m)), 0) AS p95_abs_delta_1m,
                nullIf(quantileExact(0.95)(abs(exec_delta_5m)), 0) AS p95_abs_delta_5m
            FROM CG_mnq_aggression_multiscale_v1
        ) AS q
    ) AS joined
) AS scored;
