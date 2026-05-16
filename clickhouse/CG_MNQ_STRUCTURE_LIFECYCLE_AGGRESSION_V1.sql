-- ============================================================
-- CG_mnq_structure_lifecycle_aggression_v1
-- Join structure lifecycle events with aggression snapshots
--
-- Purpose:
--   - Capture aggression at structure start/end times
--   - Calculate alignment scores (does aggression match expected direction?)
--   - Enable filtering structures by aggression context
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_structure_lifecycle_aggression_v1;

CREATE TABLE CG_mnq_structure_lifecycle_aggression_v1
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, level_type, structure_side, maturity_state)
AS
SELECT
    s.trade_date AS trade_date,
    s.structure_id AS structure_id,
    s.level_type AS level_type,
    s.structure_side AS structure_side,
    s.level_price AS level_price,
    s.structure_start_time AS structure_start_time,
    s.structure_end_time AS structure_end_time,
    s.time_in_structure_secs AS time_in_structure_secs,
    s.touch_count AS touch_count,
    s.structure_low AS structure_low,
    s.structure_high AS structure_high,
    s.range_width_pts AS range_width_pts,
    s.break_attempts AS break_attempts,
    s.failed_breaks AS failed_breaks,
    s.successful_breaks AS successful_breaks,
    s.avg_impulse_pts AS avg_impulse_pts,
    s.max_impulse_pts AS max_impulse_pts,
    s.avg_rejection_efficiency AS avg_rejection_efficiency,
    s.max_rejection_efficiency AS max_rejection_efficiency,
    s.maturity_state AS maturity_state,

    -- Expected trade direction for this structure
    multiIf(
        s.structure_side = 'SUPPORT', 'LONG',
        s.structure_side = 'RESISTANCE', 'SHORT',
        'NONE'
    ) AS expected_trade_side,

    -- Aggression snapshot at structure START
    a_start.exec_delta_100ms AS start_exec_delta_100ms,
    a_start.exec_delta_500ms AS start_exec_delta_500ms,
    a_start.exec_delta_1s AS start_exec_delta_1s,
    a_start.exec_delta_3s AS start_exec_delta_3s,
    a_start.exec_delta_5s AS start_exec_delta_5s,
    a_start.exec_delta_15s AS start_exec_delta_15s,
    a_start.exec_delta_30s AS start_exec_delta_30s,
    a_start.exec_delta_1m AS start_exec_delta_1m,
    a_start.exec_delta_5m AS start_exec_delta_5m,

    a_start.exec_imbalance_100ms AS start_exec_imbalance_100ms,
    a_start.exec_imbalance_500ms AS start_exec_imbalance_500ms,
    a_start.exec_imbalance_1s AS start_exec_imbalance_1s,
    a_start.exec_imbalance_3s AS start_exec_imbalance_3s,
    a_start.exec_imbalance_5s AS start_exec_imbalance_5s,
    a_start.exec_imbalance_15s AS start_exec_imbalance_15s,
    a_start.exec_imbalance_30s AS start_exec_imbalance_30s,
    a_start.exec_imbalance_1m AS start_exec_imbalance_1m,
    a_start.exec_imbalance_5m AS start_exec_imbalance_5m,

    -- Aggression snapshot at structure END
    a_end.exec_delta_100ms AS end_exec_delta_100ms,
    a_end.exec_delta_500ms AS end_exec_delta_500ms,
    a_end.exec_delta_1s AS end_exec_delta_1s,
    a_end.exec_delta_3s AS end_exec_delta_3s,
    a_end.exec_delta_5s AS end_exec_delta_5s,
    a_end.exec_delta_15s AS end_exec_delta_15s,
    a_end.exec_delta_30s AS end_exec_delta_30s,
    a_end.exec_delta_1m AS end_exec_delta_1m,
    a_end.exec_delta_5m AS end_exec_delta_5m,

    a_end.exec_imbalance_100ms AS end_exec_imbalance_100ms,
    a_end.exec_imbalance_500ms AS end_exec_imbalance_500ms,
    a_end.exec_imbalance_1s AS end_exec_imbalance_1s,
    a_end.exec_imbalance_3s AS end_exec_imbalance_3s,
    a_end.exec_imbalance_5s AS end_exec_imbalance_5s,
    a_end.exec_imbalance_15s AS end_exec_imbalance_15s,
    a_end.exec_imbalance_30s AS end_exec_imbalance_30s,
    a_end.exec_imbalance_1m AS end_exec_imbalance_1m,
    a_end.exec_imbalance_5m AS end_exec_imbalance_5m,

    -- Alignment flags: 1 if aggression matches expected direction, 0 otherwise
    -- SUPPORT expects positive delta (buying pressure), RESISTANCE expects negative delta (selling pressure)
    multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_100ms > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_start.exec_delta_100ms < 0, 1, 0) AS start_align_100ms,
    multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_500ms > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_start.exec_delta_500ms < 0, 1, 0) AS start_align_500ms,
    multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_1s > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_start.exec_delta_1s < 0, 1, 0) AS start_align_1s,
    multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_3s > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_start.exec_delta_3s < 0, 1, 0) AS start_align_3s,
    multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_5s > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_start.exec_delta_5s < 0, 1, 0) AS start_align_5s,
    multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_15s > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_start.exec_delta_15s < 0, 1, 0) AS start_align_15s,
    multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_30s > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_start.exec_delta_30s < 0, 1, 0) AS start_align_30s,
    multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_1m > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_start.exec_delta_1m < 0, 1, 0) AS start_align_1m,
    multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_5m > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_start.exec_delta_5m < 0, 1, 0) AS start_align_5m,

    multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_100ms > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_end.exec_delta_100ms < 0, 1, 0) AS end_align_100ms,
    multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_500ms > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_end.exec_delta_500ms < 0, 1, 0) AS end_align_500ms,
    multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_1s > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_end.exec_delta_1s < 0, 1, 0) AS end_align_1s,
    multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_3s > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_end.exec_delta_3s < 0, 1, 0) AS end_align_3s,
    multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_5s > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_end.exec_delta_5s < 0, 1, 0) AS end_align_5s,
    multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_15s > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_end.exec_delta_15s < 0, 1, 0) AS end_align_15s,
    multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_30s > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_end.exec_delta_30s < 0, 1, 0) AS end_align_30s,
    multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_1m > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_end.exec_delta_1m < 0, 1, 0) AS end_align_1m,
    multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_5m > 0, 1,
            s.structure_side = 'RESISTANCE' AND a_end.exec_delta_5m < 0, 1, 0) AS end_align_5m,

    -- Raw alignment scores: count of aligned timeframes (0-9)
    (
        multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_100ms > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_start.exec_delta_100ms < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_500ms > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_start.exec_delta_500ms < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_1s > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_start.exec_delta_1s < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_3s > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_start.exec_delta_3s < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_5s > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_start.exec_delta_5s < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_15s > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_start.exec_delta_15s < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_30s > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_start.exec_delta_30s < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_1m > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_start.exec_delta_1m < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_start.exec_delta_5m > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_start.exec_delta_5m < 0, 1, 0)
    ) AS start_alignment_score_raw,

    (
        multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_100ms > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_end.exec_delta_100ms < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_500ms > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_end.exec_delta_500ms < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_1s > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_end.exec_delta_1s < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_3s > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_end.exec_delta_3s < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_5s > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_end.exec_delta_5s < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_15s > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_end.exec_delta_15s < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_30s > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_end.exec_delta_30s < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_1m > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_end.exec_delta_1m < 0, 1, 0) +
        multiIf(s.structure_side = 'SUPPORT' AND a_end.exec_delta_5m > 0, 1,
                s.structure_side = 'RESISTANCE' AND a_end.exec_delta_5m < 0, 1, 0)
    ) AS end_alignment_score_raw

FROM CG_mnq_structure_lifecycle_v1_1 AS s

LEFT JOIN CG_mnq_aggression_multiscale_v1 AS a_start
    ON toDate(toTimeZone(a_start.ts_100ms, 'America/New_York')) = s.trade_date
    AND a_start.ts_100ms = toStartOfInterval(
        toDateTime64(s.structure_start_time, 3, 'UTC'),
        toIntervalMillisecond(100)
    )

LEFT JOIN CG_mnq_aggression_multiscale_v1 AS a_end
    ON toDate(toTimeZone(a_end.ts_100ms, 'America/New_York')) = s.trade_date
    AND a_end.ts_100ms = toStartOfInterval(
        toDateTime64(s.structure_end_time, 3, 'UTC'),
        toIntervalMillisecond(100)
    )

WHERE s.maturity_state IN ('FORMING', 'MATURE');
