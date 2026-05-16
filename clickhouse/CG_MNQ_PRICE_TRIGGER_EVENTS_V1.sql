-- ============================================================
-- CG_mnq_price_trigger_events_v1
-- Price trigger events from qualified structures
--
-- Purpose:
--   - Generate actionable signals at structure end times
--   - Filter to non-WEAK structures only
--   - Classify by trigger type, side, and family
--   - Calculate priority scores
--   - Join market context at trigger time
--
-- Trigger Types:
--   - LONG_ORB_HIGH_BREAKOUT_CONTINUATION
--   - SHORT_ORB_HIGH_REJECTION_FADE
--   - LONG_ORB_LOW_RECLAIM_SUPPORT
--   - SHORT_ORB_LOW_SUPPORT_FAILURE
--   - LONG_VWAP_SUPPORT_REVERSION
--   - SHORT_VWAP_SUPPORT_FAILURE
--   - SHORT_VWAP_RESISTANCE_REJECTION
--   - LONG_VWAP_RESISTANCE_RECLAIM
--   - WATCH_* (neutral quality)
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_price_trigger_events_v1;

CREATE TABLE CG_mnq_price_trigger_events_v1
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY
(
    trade_date,
    trigger_side,
    trigger_type,
    maturity_state
)
AS
SELECT
    scored.trade_date AS trade_date,
    scored.structure_id AS structure_id,

    scored.trigger_time AS trigger_time,
    scored.trigger_time_5s AS trigger_time_5s,

    scored.trigger_type AS trigger_type,
    scored.trigger_side AS trigger_side,
    scored.trigger_family AS trigger_family,

    scored.level_type AS level_type,
    scored.structure_side AS structure_side,
    scored.level_price AS level_price,
    scored.maturity_state AS maturity_state,

    scored.structure_start_time AS structure_start_time,
    scored.structure_mid_time AS structure_mid_time,
    scored.structure_end_time AS structure_end_time,
    scored.time_in_structure_secs AS time_in_structure_secs,
    scored.touch_count AS touch_count,
    scored.range_width_pts AS range_width_pts,
    scored.break_attempts AS break_attempts,
    scored.failed_breaks AS failed_breaks,
    scored.successful_breaks AS successful_breaks,

    scored.aggression_quality_state AS aggression_quality_state,
    scored.behavior_candidate AS behavior_candidate,

    scored.start_weighted_alignment_score AS start_weighted_alignment_score,
    scored.mid_weighted_alignment_score AS mid_weighted_alignment_score,
    scored.end_weighted_alignment_score AS end_weighted_alignment_score,
    scored.alignment_quality_delta AS alignment_quality_delta,
    scored.alignment_quality_peak AS alignment_quality_peak,

    scored.open_price AS trigger_open_price,
    scored.high_price AS trigger_high_price,
    scored.low_price AS trigger_low_price,
    scored.close_price AS trigger_close_price,
    scored.vwap AS trigger_vwap,
    scored.orb_high AS trigger_orb_high,
    scored.orb_low AS trigger_orb_low,
    scored.orb_state AS trigger_orb_state,
    scored.vwap_relation AS trigger_vwap_relation,
    scored.time_bucket AS trigger_time_bucket,
    scored.trend_bias AS trigger_trend_bias,

    multiIf(
        scored.trigger_side = 'LONG', scored.close_price,
        scored.trigger_side = 'SHORT', scored.close_price,
        NULL
    ) AS reference_entry_price,

    multiIf(
        scored.aggression_quality_state = 'STRENGTHENING_HIGH', 5,
        scored.aggression_quality_state = 'STRENGTHENING_MODERATE', 4,
        scored.aggression_quality_state = 'EXHAUSTING', 3,
        scored.aggression_quality_state = 'NEUTRAL', 2,
        0
    ) AS aggression_priority,

    multiIf(
        scored.maturity_state = 'MATURE', 2,
        scored.maturity_state = 'FORMING', 1,
        0
    ) AS maturity_priority,

    multiIf(
        scored.level_type IN ('ORB_HIGH', 'ORB_LOW'), 2,
        scored.level_type = 'VWAP', 1,
        0
    ) AS structure_priority,

    (
        multiIf(
            scored.aggression_quality_state = 'STRENGTHENING_HIGH', 5,
            scored.aggression_quality_state = 'STRENGTHENING_MODERATE', 4,
            scored.aggression_quality_state = 'EXHAUSTING', 3,
            scored.aggression_quality_state = 'NEUTRAL', 2,
            0
        )
        +
        multiIf(
            scored.maturity_state = 'MATURE', 2,
            scored.maturity_state = 'FORMING', 1,
            0
        )
        +
        multiIf(
            scored.level_type IN ('ORB_HIGH', 'ORB_LOW'), 2,
            scored.level_type = 'VWAP', 1,
            0
        )
        +
        multiIf(scored.alignment_quality_delta > 0, 1, 0)
    ) AS trigger_priority_score

FROM
(
    SELECT
        classified.trade_date AS trade_date,
        classified.structure_id AS structure_id,

        classified.trigger_time AS trigger_time,
        classified.trigger_time_5s AS trigger_time_5s,

        classified.trigger_type AS trigger_type,
        classified.trigger_side AS trigger_side,
        classified.trigger_family AS trigger_family,

        classified.level_type AS level_type,
        classified.structure_side AS structure_side,
        classified.level_price AS level_price,
        classified.maturity_state AS maturity_state,

        classified.structure_start_time AS structure_start_time,
        classified.structure_mid_time AS structure_mid_time,
        classified.structure_end_time AS structure_end_time,
        classified.time_in_structure_secs AS time_in_structure_secs,
        classified.touch_count AS touch_count,
        classified.range_width_pts AS range_width_pts,
        classified.break_attempts AS break_attempts,
        classified.failed_breaks AS failed_breaks,
        classified.successful_breaks AS successful_breaks,

        classified.aggression_quality_state AS aggression_quality_state,
        classified.behavior_candidate AS behavior_candidate,

        classified.start_weighted_alignment_score AS start_weighted_alignment_score,
        classified.mid_weighted_alignment_score AS mid_weighted_alignment_score,
        classified.end_weighted_alignment_score AS end_weighted_alignment_score,
        classified.alignment_quality_delta AS alignment_quality_delta,
        classified.alignment_quality_peak AS alignment_quality_peak,

        r.open_price AS open_price,
        r.high_price AS high_price,
        r.low_price AS low_price,
        r.close_price AS close_price,
        r.vwap AS vwap,
        r.orb_high AS orb_high,
        r.orb_low AS orb_low,
        r.orb_state AS orb_state,
        r.vwap_relation AS vwap_relation,
        r.time_bucket AS time_bucket,
        r.trend_bias AS trend_bias

    FROM
    (
        SELECT
            q.trade_date AS trade_date,
            q.structure_id AS structure_id,

            q.structure_end_time AS trigger_time,
            toStartOfInterval(q.structure_end_time, toIntervalSecond(5)) AS trigger_time_5s,

            q.level_type AS level_type,
            q.structure_side AS structure_side,
            q.level_price AS level_price,
            q.maturity_state AS maturity_state,

            q.structure_start_time AS structure_start_time,
            q.structure_mid_time AS structure_mid_time,
            q.structure_end_time AS structure_end_time,
            q.time_in_structure_secs AS time_in_structure_secs,
            q.touch_count AS touch_count,
            q.range_width_pts AS range_width_pts,
            q.break_attempts AS break_attempts,
            q.failed_breaks AS failed_breaks,
            q.successful_breaks AS successful_breaks,

            q.aggression_quality_state AS aggression_quality_state,
            q.behavior_candidate AS behavior_candidate,

            q.start_weighted_alignment_score AS start_weighted_alignment_score,
            q.mid_weighted_alignment_score AS mid_weighted_alignment_score,
            q.end_weighted_alignment_score AS end_weighted_alignment_score,
            q.alignment_quality_delta AS alignment_quality_delta,
            q.alignment_quality_peak AS alignment_quality_peak,

            multiIf(
                q.level_type = 'ORB_HIGH'
                AND q.structure_side = 'RESISTANCE'
                AND q.aggression_quality_state IN ('STRENGTHENING_HIGH', 'STRENGTHENING_MODERATE'),
                'LONG_ORB_HIGH_BREAKOUT_CONTINUATION',

                q.level_type = 'ORB_HIGH'
                AND q.structure_side = 'RESISTANCE'
                AND q.aggression_quality_state = 'EXHAUSTING',
                'SHORT_ORB_HIGH_REJECTION_FADE',

                q.level_type = 'ORB_LOW'
                AND q.structure_side = 'SUPPORT'
                AND q.aggression_quality_state IN ('STRENGTHENING_HIGH', 'STRENGTHENING_MODERATE'),
                'LONG_ORB_LOW_RECLAIM_SUPPORT',

                q.level_type = 'ORB_LOW'
                AND q.structure_side = 'SUPPORT'
                AND q.aggression_quality_state = 'EXHAUSTING',
                'SHORT_ORB_LOW_SUPPORT_FAILURE',

                q.level_type = 'VWAP'
                AND q.structure_side = 'SUPPORT'
                AND q.aggression_quality_state IN ('STRENGTHENING_HIGH', 'STRENGTHENING_MODERATE'),
                'LONG_VWAP_SUPPORT_REVERSION',

                q.level_type = 'VWAP'
                AND q.structure_side = 'SUPPORT'
                AND q.aggression_quality_state = 'EXHAUSTING',
                'SHORT_VWAP_SUPPORT_FAILURE',

                q.level_type = 'VWAP'
                AND q.structure_side = 'RESISTANCE'
                AND q.aggression_quality_state IN ('STRENGTHENING_HIGH', 'STRENGTHENING_MODERATE'),
                'SHORT_VWAP_RESISTANCE_REJECTION',

                q.level_type = 'VWAP'
                AND q.structure_side = 'RESISTANCE'
                AND q.aggression_quality_state = 'EXHAUSTING',
                'LONG_VWAP_RESISTANCE_RECLAIM',

                q.aggression_quality_state = 'NEUTRAL'
                AND q.level_type = 'ORB_HIGH'
                AND q.structure_side = 'RESISTANCE',
                'WATCH_ORB_HIGH_NEUTRAL',

                q.aggression_quality_state = 'NEUTRAL'
                AND q.level_type = 'ORB_LOW'
                AND q.structure_side = 'SUPPORT',
                'WATCH_ORB_LOW_NEUTRAL',

                q.aggression_quality_state = 'NEUTRAL'
                AND q.level_type = 'VWAP'
                AND q.structure_side = 'SUPPORT',
                'WATCH_VWAP_SUPPORT_NEUTRAL',

                q.aggression_quality_state = 'NEUTRAL'
                AND q.level_type = 'VWAP'
                AND q.structure_side = 'RESISTANCE',
                'WATCH_VWAP_RESISTANCE_NEUTRAL',

                'UNCLASSIFIED'
            ) AS trigger_type,

            multiIf(
                q.level_type = 'ORB_HIGH'
                AND q.structure_side = 'RESISTANCE'
                AND q.aggression_quality_state IN ('STRENGTHENING_HIGH', 'STRENGTHENING_MODERATE'),
                'LONG',

                q.level_type = 'ORB_HIGH'
                AND q.structure_side = 'RESISTANCE'
                AND q.aggression_quality_state = 'EXHAUSTING',
                'SHORT',

                q.level_type = 'ORB_LOW'
                AND q.structure_side = 'SUPPORT'
                AND q.aggression_quality_state IN ('STRENGTHENING_HIGH', 'STRENGTHENING_MODERATE'),
                'LONG',

                q.level_type = 'ORB_LOW'
                AND q.structure_side = 'SUPPORT'
                AND q.aggression_quality_state = 'EXHAUSTING',
                'SHORT',

                q.level_type = 'VWAP'
                AND q.structure_side = 'SUPPORT'
                AND q.aggression_quality_state IN ('STRENGTHENING_HIGH', 'STRENGTHENING_MODERATE'),
                'LONG',

                q.level_type = 'VWAP'
                AND q.structure_side = 'SUPPORT'
                AND q.aggression_quality_state = 'EXHAUSTING',
                'SHORT',

                q.level_type = 'VWAP'
                AND q.structure_side = 'RESISTANCE'
                AND q.aggression_quality_state IN ('STRENGTHENING_HIGH', 'STRENGTHENING_MODERATE'),
                'SHORT',

                q.level_type = 'VWAP'
                AND q.structure_side = 'RESISTANCE'
                AND q.aggression_quality_state = 'EXHAUSTING',
                'LONG',

                'WATCH'
            ) AS trigger_side,

            multiIf(
                q.aggression_quality_state IN ('STRENGTHENING_HIGH', 'STRENGTHENING_MODERATE'),
                'CONTINUATION_OR_REVERSION_WITH_SUPPORT',

                q.aggression_quality_state = 'EXHAUSTING',
                'FAILURE_OR_FADE',

                q.aggression_quality_state = 'NEUTRAL',
                'WATCH_ONLY',

                'UNCLASSIFIED'
            ) AS trigger_family

        FROM CG_mnq_structure_aggression_quality_v1 AS q
        WHERE q.aggression_quality_state != 'WEAK_OR_OPPOSED'
          AND q.maturity_state IN ('FORMING', 'MATURE')
    ) AS classified

    LEFT JOIN CG_mnq_session_regime_v2 AS r
        ON classified.trade_date = r.trade_date
       AND classified.trigger_time_5s = r.ts_5s

    WHERE classified.trigger_type != 'UNCLASSIFIED'
) AS scored;
