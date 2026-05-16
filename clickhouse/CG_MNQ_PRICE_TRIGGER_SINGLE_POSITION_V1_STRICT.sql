-- ============================================================
-- CG_mnq_price_trigger_single_position_v1_strict
-- True sequential single position enforcement
--
-- Purpose:
--   - Enforce 1 MNQ contract maximum (no overlapping positions)
--   - 10-minute lockout after each trigger (600 seconds)
--   - True sequential walk-forward (not bucketed)
--   - Highest priority wins when lockouts conflict
--
-- Selection Logic:
--   - Process triggers in chronological order
--   - For each trigger, check if it falls within lockout of prior trigger
--   - If in lockout AND prior has higher priority, exclude current trigger
--   - If in lockout AND current has equal/higher priority, include current
--
-- This is the correct version when overlap violations detected.
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_price_trigger_single_position_v1_strict;

CREATE TABLE CG_mnq_price_trigger_single_position_v1_strict
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY
(
    trade_date,
    selected_trigger_time,
    trigger_side
)
AS
WITH
    600 AS lockout_seconds
SELECT
    final.trade_date AS trade_date,
    row_number() OVER
    (
        PARTITION BY final.trade_date
        ORDER BY final.trigger_time
    ) AS trade_sequence,

    final.trigger_time AS selected_trigger_time,
    final.trigger_time + toIntervalSecond(lockout_seconds) AS lockout_end_time,

    final.structure_id AS structure_id,
    final.trigger_type AS trigger_type,
    final.trigger_side AS trigger_side,
    final.trigger_family AS trigger_family,

    final.level_type AS level_type,
    final.structure_side AS structure_side,
    final.level_price AS level_price,
    final.maturity_state AS maturity_state,

    final.aggression_quality_state AS aggression_quality_state,
    final.behavior_candidate AS behavior_candidate,

    final.trigger_priority_score AS trigger_priority_score,
    final.aggression_priority AS aggression_priority,
    final.maturity_priority AS maturity_priority,
    final.structure_priority AS structure_priority,

    final.start_weighted_alignment_score AS start_weighted_alignment_score,
    final.mid_weighted_alignment_score AS mid_weighted_alignment_score,
    final.end_weighted_alignment_score AS end_weighted_alignment_score,
    final.alignment_quality_delta AS alignment_quality_delta,
    final.alignment_quality_peak AS alignment_quality_peak,

    final.time_in_structure_secs AS time_in_structure_secs,
    final.touch_count AS touch_count,
    final.range_width_pts AS range_width_pts,
    final.break_attempts AS break_attempts,
    final.failed_breaks AS failed_breaks,
    final.successful_breaks AS successful_breaks,

    final.trigger_open_price AS trigger_open_price,
    final.trigger_high_price AS trigger_high_price,
    final.trigger_low_price AS trigger_low_price,
    final.trigger_close_price AS trigger_close_price,
    final.reference_entry_price AS reference_entry_price,

    final.trigger_vwap AS trigger_vwap,
    final.trigger_orb_high AS trigger_orb_high,
    final.trigger_orb_low AS trigger_orb_low,
    final.trigger_orb_state AS trigger_orb_state,
    final.trigger_vwap_relation AS trigger_vwap_relation,
    final.trigger_time_bucket AS trigger_time_bucket,
    final.trigger_trend_bias AS trigger_trend_bias

FROM
(
    SELECT
        ordered.*
    FROM
    (
        SELECT
            e.*,
            row_number() OVER
            (
                PARTITION BY e.trade_date
                ORDER BY
                    e.trigger_time ASC,
                    e.trigger_priority_score DESC,
                    e.end_weighted_alignment_score DESC,
                    e.alignment_quality_delta DESC
            ) AS event_index
        FROM CG_mnq_price_trigger_events_v1 AS e
        WHERE e.trigger_side IN ('LONG', 'SHORT')
    ) AS ordered
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM CG_mnq_price_trigger_events_v1 AS prior
        WHERE prior.trigger_side IN ('LONG', 'SHORT')
          AND prior.trade_date = ordered.trade_date
          AND prior.trigger_time < ordered.trigger_time
          AND prior.trigger_time + toIntervalSecond(lockout_seconds) > ordered.trigger_time
          AND
          (
              prior.trigger_priority_score > ordered.trigger_priority_score
              OR
              (
                  prior.trigger_priority_score = ordered.trigger_priority_score
                  AND prior.end_weighted_alignment_score >= ordered.end_weighted_alignment_score
              )
          )
    )
) AS final;
