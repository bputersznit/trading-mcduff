-- ============================================================
-- CG_mnq_price_trigger_path_outcomes_v1
-- Price trigger path simulation with fixed 2:1 bracket
--
-- Purpose:
--   - Simulate trade execution for selected triggers
--   - Fixed parameters: 40 tick target, 20 tick stop, 600s timeout
--   - Track target/stop/timeout outcomes
--   - Calculate PnL with 2-tick cost floor
--
-- Parameters:
--   - Target: 40 ticks (10 points for MNQ)
--   - Stop: 20 ticks (5 points for MNQ)
--   - Timeout: 600 seconds (10 minutes)
--   - Cost floor: 2 ticks (slippage + commission)
--
-- Entry Price: reference_entry_price from trigger (close at trigger time)
-- Exit Logic: First of target hit, stop hit, or timeout
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_price_trigger_path_outcomes_v1;

CREATE TABLE CG_mnq_price_trigger_path_outcomes_v1
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY
(
    trade_date,
    entry_time,
    trigger_side,
    trigger_type
)
AS
WITH
    0.25 AS tick_size,
    40 AS target_ticks,
    20 AS stop_ticks,
    600 AS timeout_seconds
SELECT
    resolved.trade_date AS trade_date,
    resolved.trade_sequence AS trade_sequence,

    resolved.structure_id AS structure_id,
    resolved.trigger_type AS trigger_type,
    resolved.trigger_side AS trigger_side,
    resolved.trigger_family AS trigger_family,

    resolved.level_type AS level_type,
    resolved.structure_side AS structure_side,
    resolved.level_price AS level_price,
    resolved.maturity_state AS maturity_state,
    resolved.aggression_quality_state AS aggression_quality_state,

    resolved.entry_time AS entry_time,
    resolved.entry_price AS entry_price,
    resolved.target_price AS target_price,
    resolved.stop_price AS stop_price,
    resolved.timeout_time AS timeout_time,

    resolved.target_hit_time AS target_hit_time,
    resolved.stop_hit_time AS stop_hit_time,
    resolved.exit_time AS exit_time,
    resolved.exit_price AS exit_price,
    resolved.outcome AS outcome,

    multiIf(
        resolved.trigger_side = 'LONG',
        (resolved.exit_price - resolved.entry_price) / tick_size,

        resolved.trigger_side = 'SHORT',
        (resolved.entry_price - resolved.exit_price) / tick_size,

        0
    ) AS gross_pnl_ticks,

    multiIf(
        resolved.trigger_side = 'LONG',
        (resolved.exit_price - resolved.entry_price) / tick_size,

        resolved.trigger_side = 'SHORT',
        (resolved.entry_price - resolved.exit_price) / tick_size,

        0
    ) - 2.0 AS net_pnl_ticks_after_cost_floor,

    dateDiff('second', resolved.entry_time, resolved.exit_time) AS hold_seconds,

    resolved.trigger_priority_score AS trigger_priority_score,
    resolved.end_weighted_alignment_score AS end_weighted_alignment_score,
    resolved.alignment_quality_delta AS alignment_quality_delta,
    resolved.alignment_quality_peak AS alignment_quality_peak,

    resolved.touch_count AS touch_count,
    resolved.time_in_structure_secs AS time_in_structure_secs,
    resolved.range_width_pts AS range_width_pts,

    resolved.trigger_vwap AS trigger_vwap,
    resolved.trigger_orb_high AS trigger_orb_high,
    resolved.trigger_orb_low AS trigger_orb_low,
    resolved.trigger_orb_state AS trigger_orb_state,
    resolved.trigger_vwap_relation AS trigger_vwap_relation,
    resolved.trigger_time_bucket AS trigger_time_bucket,
    resolved.trigger_trend_bias AS trigger_trend_bias

FROM
(
    SELECT
        hits.trade_date AS trade_date,
        hits.trade_sequence AS trade_sequence,

        hits.structure_id AS structure_id,
        hits.trigger_type AS trigger_type,
        hits.trigger_side AS trigger_side,
        hits.trigger_family AS trigger_family,

        hits.level_type AS level_type,
        hits.structure_side AS structure_side,
        hits.level_price AS level_price,
        hits.maturity_state AS maturity_state,
        hits.aggression_quality_state AS aggression_quality_state,

        hits.entry_time AS entry_time,
        hits.entry_price AS entry_price,
        hits.target_price AS target_price,
        hits.stop_price AS stop_price,
        hits.timeout_time AS timeout_time,

        hits.target_hit_time AS target_hit_time,
        hits.stop_hit_time AS stop_hit_time,

        multiIf(
            hits.stop_hit_time IS NOT NULL
            AND hits.target_hit_time IS NOT NULL
            AND hits.stop_hit_time <= hits.target_hit_time,
            hits.stop_hit_time,

            hits.target_hit_time IS NOT NULL
            AND hits.stop_hit_time IS NULL,
            hits.target_hit_time,

            hits.target_hit_time IS NOT NULL
            AND hits.stop_hit_time IS NOT NULL
            AND hits.target_hit_time < hits.stop_hit_time,
            hits.target_hit_time,

            hits.stop_hit_time IS NOT NULL,
            hits.stop_hit_time,

            hits.timeout_time
        ) AS exit_time,

        multiIf(
            hits.stop_hit_time IS NOT NULL
            AND hits.target_hit_time IS NOT NULL
            AND hits.stop_hit_time <= hits.target_hit_time,
            hits.stop_price,

            hits.target_hit_time IS NOT NULL
            AND hits.stop_hit_time IS NULL,
            hits.target_price,

            hits.target_hit_time IS NOT NULL
            AND hits.stop_hit_time IS NOT NULL
            AND hits.target_hit_time < hits.stop_hit_time,
            hits.target_price,

            hits.stop_hit_time IS NOT NULL,
            hits.stop_price,

            hits.timeout_close_price
        ) AS exit_price,

        multiIf(
            hits.stop_hit_time IS NOT NULL
            AND hits.target_hit_time IS NOT NULL
            AND hits.stop_hit_time <= hits.target_hit_time,
            'STOP',

            hits.target_hit_time IS NOT NULL
            AND hits.stop_hit_time IS NULL,
            'TARGET',

            hits.target_hit_time IS NOT NULL
            AND hits.stop_hit_time IS NOT NULL
            AND hits.target_hit_time < hits.stop_hit_time,
            'TARGET',

            hits.stop_hit_time IS NOT NULL,
            'STOP',

            'TIMEOUT'
        ) AS outcome,

        hits.trigger_priority_score AS trigger_priority_score,
        hits.end_weighted_alignment_score AS end_weighted_alignment_score,
        hits.alignment_quality_delta AS alignment_quality_delta,
        hits.alignment_quality_peak AS alignment_quality_peak,

        hits.touch_count AS touch_count,
        hits.time_in_structure_secs AS time_in_structure_secs,
        hits.range_width_pts AS range_width_pts,

        hits.trigger_vwap AS trigger_vwap,
        hits.trigger_orb_high AS trigger_orb_high,
        hits.trigger_orb_low AS trigger_orb_low,
        hits.trigger_orb_state AS trigger_orb_state,
        hits.trigger_vwap_relation AS trigger_vwap_relation,
        hits.trigger_time_bucket AS trigger_time_bucket,
        hits.trigger_trend_bias AS trigger_trend_bias

    FROM
    (
        SELECT
            candidates.trade_date AS trade_date,
            candidates.trade_sequence AS trade_sequence,

            candidates.structure_id AS structure_id,
            candidates.trigger_type AS trigger_type,
            candidates.trigger_side AS trigger_side,
            candidates.trigger_family AS trigger_family,

            candidates.level_type AS level_type,
            candidates.structure_side AS structure_side,
            candidates.level_price AS level_price,
            candidates.maturity_state AS maturity_state,
            candidates.aggression_quality_state AS aggression_quality_state,

            candidates.entry_time AS entry_time,
            candidates.entry_price AS entry_price,
            candidates.target_price AS target_price,
            candidates.stop_price AS stop_price,
            candidates.timeout_time AS timeout_time,

            minIf(
                path.ts_5s,
                candidates.trigger_side = 'LONG'
                AND path.high_price >= candidates.target_price
            ) AS target_hit_time_long,

            minIf(
                path.ts_5s,
                candidates.trigger_side = 'SHORT'
                AND path.low_price <= candidates.target_price
            ) AS target_hit_time_short,

            minIf(
                path.ts_5s,
                candidates.trigger_side = 'LONG'
                AND path.low_price <= candidates.stop_price
            ) AS stop_hit_time_long,

            minIf(
                path.ts_5s,
                candidates.trigger_side = 'SHORT'
                AND path.high_price >= candidates.stop_price
            ) AS stop_hit_time_short,

            -- Convert epoch zero to NULL (minIf returns epoch when no match)
            multiIf(
                candidates.trigger_side = 'LONG' AND target_hit_time_long > toDateTime('1970-01-01 00:00:01', 'UTC'),
                target_hit_time_long,
                candidates.trigger_side = 'SHORT' AND target_hit_time_short > toDateTime('1970-01-01 00:00:01', 'UTC'),
                target_hit_time_short,
                NULL
            ) AS target_hit_time,

            multiIf(
                candidates.trigger_side = 'LONG' AND stop_hit_time_long > toDateTime('1970-01-01 00:00:01', 'UTC'),
                stop_hit_time_long,
                candidates.trigger_side = 'SHORT' AND stop_hit_time_short > toDateTime('1970-01-01 00:00:01', 'UTC'),
                stop_hit_time_short,
                NULL
            ) AS stop_hit_time,

            argMin(path.close_price, abs(dateDiff('second', path.ts_5s, candidates.timeout_time))) AS timeout_close_price,

            candidates.trigger_priority_score AS trigger_priority_score,
            candidates.end_weighted_alignment_score AS end_weighted_alignment_score,
            candidates.alignment_quality_delta AS alignment_quality_delta,
            candidates.alignment_quality_peak AS alignment_quality_peak,

            candidates.touch_count AS touch_count,
            candidates.time_in_structure_secs AS time_in_structure_secs,
            candidates.range_width_pts AS range_width_pts,

            candidates.trigger_vwap AS trigger_vwap,
            candidates.trigger_orb_high AS trigger_orb_high,
            candidates.trigger_orb_low AS trigger_orb_low,
            candidates.trigger_orb_state AS trigger_orb_state,
            candidates.trigger_vwap_relation AS trigger_vwap_relation,
            candidates.trigger_time_bucket AS trigger_time_bucket,
            candidates.trigger_trend_bias AS trigger_trend_bias

        FROM
        (
            SELECT
                sp.trade_date AS trade_date,
                sp.trade_sequence AS trade_sequence,

                sp.structure_id AS structure_id,
                sp.trigger_type AS trigger_type,
                sp.trigger_side AS trigger_side,
                sp.trigger_family AS trigger_family,

                sp.level_type AS level_type,
                sp.structure_side AS structure_side,
                sp.level_price AS level_price,
                sp.maturity_state AS maturity_state,
                sp.aggression_quality_state AS aggression_quality_state,

                sp.selected_trigger_time AS entry_time,
                sp.reference_entry_price AS entry_price,

                sp.selected_trigger_time + toIntervalSecond(timeout_seconds) AS timeout_time,

                multiIf(
                    sp.trigger_side = 'LONG',
                    sp.reference_entry_price + (target_ticks * tick_size),

                    sp.trigger_side = 'SHORT',
                    sp.reference_entry_price - (target_ticks * tick_size),

                    NULL
                ) AS target_price,

                multiIf(
                    sp.trigger_side = 'LONG',
                    sp.reference_entry_price - (stop_ticks * tick_size),

                    sp.trigger_side = 'SHORT',
                    sp.reference_entry_price + (stop_ticks * tick_size),

                    NULL
                ) AS stop_price,

                sp.trigger_priority_score AS trigger_priority_score,
                sp.end_weighted_alignment_score AS end_weighted_alignment_score,
                sp.alignment_quality_delta AS alignment_quality_delta,
                sp.alignment_quality_peak AS alignment_quality_peak,

                sp.touch_count AS touch_count,
                sp.time_in_structure_secs AS time_in_structure_secs,
                sp.range_width_pts AS range_width_pts,

                sp.trigger_vwap AS trigger_vwap,
                sp.trigger_orb_high AS trigger_orb_high,
                sp.trigger_orb_low AS trigger_orb_low,
                sp.trigger_orb_state AS trigger_orb_state,
                sp.trigger_vwap_relation AS trigger_vwap_relation,
                sp.trigger_time_bucket AS trigger_time_bucket,
                sp.trigger_trend_bias AS trigger_trend_bias

            FROM CG_mnq_price_trigger_single_position_python_v1 AS sp
            WHERE sp.trigger_side IN ('LONG', 'SHORT')
              AND sp.reference_entry_price > 0
        ) AS candidates

        INNER JOIN
        (
            SELECT
                t AS ts_5s,
                toDate(toTimeZone(t, 'America/New_York')) AS trade_date,
                hi_px AS high_price,
                lo_px AS low_price,
                close_px AS close_price
            FROM mnq_ohlc_5s
        ) AS path
            ON candidates.trade_date = path.trade_date
           AND path.ts_5s >= candidates.entry_time
           AND path.ts_5s <= candidates.timeout_time

        GROUP BY
            candidates.trade_date,
            candidates.trade_sequence,
            candidates.structure_id,
            candidates.trigger_type,
            candidates.trigger_side,
            candidates.trigger_family,
            candidates.level_type,
            candidates.structure_side,
            candidates.level_price,
            candidates.maturity_state,
            candidates.aggression_quality_state,
            candidates.entry_time,
            candidates.entry_price,
            candidates.target_price,
            candidates.stop_price,
            candidates.timeout_time,
            candidates.trigger_priority_score,
            candidates.end_weighted_alignment_score,
            candidates.alignment_quality_delta,
            candidates.alignment_quality_peak,
            candidates.touch_count,
            candidates.time_in_structure_secs,
            candidates.range_width_pts,
            candidates.trigger_vwap,
            candidates.trigger_orb_high,
            candidates.trigger_orb_low,
            candidates.trigger_orb_state,
            candidates.trigger_vwap_relation,
            candidates.trigger_time_bucket,
            candidates.trigger_trend_bias
    ) AS hits
) AS resolved;
