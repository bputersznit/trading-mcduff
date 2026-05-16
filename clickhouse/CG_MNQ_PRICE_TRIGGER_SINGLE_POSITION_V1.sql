-- ============================================================
-- CG_mnq_price_trigger_single_position_v1
-- Single position enforcement with 10-minute lockout
--
-- Purpose:
--   - Enforce 1 MNQ contract maximum (no overlapping positions)
--   - 10-minute lockout after each trigger (600 seconds)
--   - Highest priority wins in each lockout window
--   - Sequential trade numbering per day
--
-- Selection Logic:
--   - Group triggers into 10-minute buckets
--   - Within each bucket, select highest priority trigger
--   - Tiebreaker: priority → end_quality → delta → structure → maturity → time
--
-- Note: This is a quick arbitration pass. Use _strict version
--       if overlap violations detected in sanity check.
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_price_trigger_single_position_v1;

CREATE TABLE CG_mnq_price_trigger_single_position_v1
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
    selected.trade_date AS trade_date,
    selected.trade_sequence AS trade_sequence,

    selected.trigger_time AS selected_trigger_time,
    selected.lockout_end_time AS lockout_end_time,

    selected.structure_id AS structure_id,
    selected.trigger_type AS trigger_type,
    selected.trigger_side AS trigger_side,
    selected.trigger_family AS trigger_family,

    selected.level_type AS level_type,
    selected.structure_side AS structure_side,
    selected.level_price AS level_price,
    selected.maturity_state AS maturity_state,

    selected.aggression_quality_state AS aggression_quality_state,
    selected.behavior_candidate AS behavior_candidate,

    selected.trigger_priority_score AS trigger_priority_score,
    selected.aggression_priority AS aggression_priority,
    selected.maturity_priority AS maturity_priority,
    selected.structure_priority AS structure_priority,

    selected.start_weighted_alignment_score AS start_weighted_alignment_score,
    selected.mid_weighted_alignment_score AS mid_weighted_alignment_score,
    selected.end_weighted_alignment_score AS end_weighted_alignment_score,
    selected.alignment_quality_delta AS alignment_quality_delta,
    selected.alignment_quality_peak AS alignment_quality_peak,

    selected.time_in_structure_secs AS time_in_structure_secs,
    selected.touch_count AS touch_count,
    selected.range_width_pts AS range_width_pts,
    selected.break_attempts AS break_attempts,
    selected.failed_breaks AS failed_breaks,
    selected.successful_breaks AS successful_breaks,

    selected.trigger_open_price AS trigger_open_price,
    selected.trigger_high_price AS trigger_high_price,
    selected.trigger_low_price AS trigger_low_price,
    selected.trigger_close_price AS trigger_close_price,
    selected.reference_entry_price AS reference_entry_price,

    selected.trigger_vwap AS trigger_vwap,
    selected.trigger_orb_high AS trigger_orb_high,
    selected.trigger_orb_low AS trigger_orb_low,
    selected.trigger_orb_state AS trigger_orb_state,
    selected.trigger_vwap_relation AS trigger_vwap_relation,
    selected.trigger_time_bucket AS trigger_time_bucket,
    selected.trigger_trend_bias AS trigger_trend_bias

FROM
(
    SELECT
        deduped.trade_date AS trade_date,
        row_number() OVER
        (
            PARTITION BY deduped.trade_date
            ORDER BY deduped.trigger_time
        ) AS trade_sequence,

        deduped.trigger_time AS trigger_time,
        deduped.trigger_time + toIntervalSecond(lockout_seconds) AS lockout_end_time,

        deduped.structure_id AS structure_id,
        deduped.trigger_type AS trigger_type,
        deduped.trigger_side AS trigger_side,
        deduped.trigger_family AS trigger_family,

        deduped.level_type AS level_type,
        deduped.structure_side AS structure_side,
        deduped.level_price AS level_price,
        deduped.maturity_state AS maturity_state,

        deduped.aggression_quality_state AS aggression_quality_state,
        deduped.behavior_candidate AS behavior_candidate,

        deduped.trigger_priority_score AS trigger_priority_score,
        deduped.aggression_priority AS aggression_priority,
        deduped.maturity_priority AS maturity_priority,
        deduped.structure_priority AS structure_priority,

        deduped.start_weighted_alignment_score AS start_weighted_alignment_score,
        deduped.mid_weighted_alignment_score AS mid_weighted_alignment_score,
        deduped.end_weighted_alignment_score AS end_weighted_alignment_score,
        deduped.alignment_quality_delta AS alignment_quality_delta,
        deduped.alignment_quality_peak AS alignment_quality_peak,

        deduped.time_in_structure_secs AS time_in_structure_secs,
        deduped.touch_count AS touch_count,
        deduped.range_width_pts AS range_width_pts,
        deduped.break_attempts AS break_attempts,
        deduped.failed_breaks AS failed_breaks,
        deduped.successful_breaks AS successful_breaks,

        deduped.trigger_open_price AS trigger_open_price,
        deduped.trigger_high_price AS trigger_high_price,
        deduped.trigger_low_price AS trigger_low_price,
        deduped.trigger_close_price AS trigger_close_price,
        deduped.reference_entry_price AS reference_entry_price,

        deduped.trigger_vwap AS trigger_vwap,
        deduped.trigger_orb_high AS trigger_orb_high,
        deduped.trigger_orb_low AS trigger_orb_low,
        deduped.trigger_orb_state AS trigger_orb_state,
        deduped.trigger_vwap_relation AS trigger_vwap_relation,
        deduped.trigger_time_bucket AS trigger_time_bucket,
        deduped.trigger_trend_bias AS trigger_trend_bias

    FROM
    (
        SELECT
            ranked.*
        FROM
        (
            SELECT
                actionables.*,

                row_number() OVER
                (
                    PARTITION BY
                        actionables.trade_date,
                        actionables.trigger_time_bucket_10m
                    ORDER BY
                        actionables.trigger_priority_score DESC,
                        actionables.end_weighted_alignment_score DESC,
                        actionables.alignment_quality_delta DESC,
                        actionables.structure_priority DESC,
                        actionables.maturity_priority DESC,
                        actionables.trigger_time ASC
                ) AS rank_in_lockout_bucket

            FROM
            (
                SELECT
                    e.*,
                    toStartOfInterval(e.trigger_time, toIntervalMinute(10)) AS trigger_time_bucket_10m
                FROM CG_mnq_price_trigger_events_v1 AS e
                WHERE e.trigger_side IN ('LONG', 'SHORT')
            ) AS actionables
        ) AS ranked
        WHERE ranked.rank_in_lockout_bucket = 1
    ) AS deduped
) AS selected;
