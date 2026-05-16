-- ============================================================
-- PHASE 5B TRAILING STOP SCREEN
-- Fast ClickHouse approximation, not final authority.
--
-- Input:
--   CG_mnq_phase5_trigger_candidates_v1
--   mnq_trades
--
-- Output:
--   CG_mnq_phase5b_trailing_screen_v1
--
-- Purpose:
--   Estimate whether extended hold + adaptive trail is worth
--   full Python tick-path simulation.
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_phase5b_trailing_screen_v1;

CREATE TABLE CG_mnq_phase5b_trailing_screen_v1
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY
(
    trade_date,
    trigger_model,
    trade_side
)
AS
WITH
base AS
(
    SELECT
        trade_date,
        wall_time,
        wall_et,
        wall_side,
        wall_price,
        wall_score,
        wall_behavior,
        delta_flip_pattern,
        wall_aggression_pattern,
        orb_position,
        vwap_relation,
        atr_regime,
        time_bucket,
        session_extreme_location,
        trade_candidate_class,
        trade_side,
        setup_family,
        trigger_model,
        entry_time,
        entry_price
    FROM CG_mnq_phase5_trigger_candidates_v1
    WHERE trigger_model IN ('CONFIRM_6T_PULLBACK', 'CONFIRM_8T_PULLBACK')
),

path AS
(
    SELECT
        b.trade_date AS trade_date,
        b.wall_time AS wall_time,
        b.wall_et AS wall_et,
        b.wall_side AS wall_side,
        b.wall_price AS wall_price,
        b.wall_score AS wall_score,
        b.wall_behavior AS wall_behavior,
        b.delta_flip_pattern AS delta_flip_pattern,
        b.wall_aggression_pattern AS wall_aggression_pattern,
        b.orb_position AS orb_position,
        b.vwap_relation AS vwap_relation,
        b.atr_regime AS atr_regime,
        b.time_bucket AS time_bucket,
        b.session_extreme_location AS session_extreme_location,
        b.trade_candidate_class AS trade_candidate_class,
        b.trade_side AS trade_side,
        b.setup_family AS setup_family,
        b.trigger_model AS trigger_model,
        b.entry_time AS entry_time,
        b.entry_price AS entry_price,

        t.ts_event AS path_time,
        t.price AS path_price
    FROM base AS b
    INNER JOIN mnq_trades AS t
        ON toDate(toTimeZone(t.ts_event, 'America/New_York')) = b.trade_date
       AND t.ts_event >= b.entry_time
       AND t.ts_event <  b.entry_time + INTERVAL 10 MINUTE
),

path_summary AS
(
    SELECT
        trade_date,
        wall_time,
        wall_et,
        wall_side,
        wall_price,
        wall_score,
        wall_behavior,
        delta_flip_pattern,
        wall_aggression_pattern,
        orb_position,
        vwap_relation,
        atr_regime,
        time_bucket,
        session_extreme_location,
        trade_candidate_class,
        trade_side,
        setup_family,
        trigger_model,
        entry_time,
        entry_price,

        min(path_time) AS first_path_time,
        max(path_time) AS last_path_time,

        maxIf(path_price, trade_side = 'LONG') AS long_max_price,
        minIf(path_price, trade_side = 'LONG') AS long_min_price,
        minIf(path_price, trade_side = 'SHORT') AS short_min_price,
        maxIf(path_price, trade_side = 'SHORT') AS short_max_price,

        multiIf
        (
            trade_side = 'LONG', max(path_price) - entry_price,
            trade_side = 'SHORT', entry_price - min(path_price),
            NULL
        ) AS mfe_pts_10m,

        multiIf
        (
            trade_side = 'LONG', entry_price - min(path_price),
            trade_side = 'SHORT', max(path_price) - entry_price,
            NULL
        ) AS mae_pts_10m
    FROM path
    GROUP BY
        trade_date,
        wall_time,
        wall_et,
        wall_side,
        wall_price,
        wall_score,
        wall_behavior,
        delta_flip_pattern,
        wall_aggression_pattern,
        orb_position,
        vwap_relation,
        atr_regime,
        time_bucket,
        session_extreme_location,
        trade_candidate_class,
        trade_side,
        setup_family,
        trigger_model,
        entry_time,
        entry_price
),

screened AS
(
    SELECT
        trade_date,
        wall_time,
        wall_et,
        wall_side,
        wall_price,
        wall_score,
        wall_behavior,
        delta_flip_pattern,
        wall_aggression_pattern,
        orb_position,
        vwap_relation,
        atr_regime,
        time_bucket,
        session_extreme_location,
        trade_candidate_class,
        trade_side,
        setup_family,
        trigger_model,
        entry_time,
        entry_price,

        mfe_pts_10m,
        mae_pts_10m,

        mfe_pts_10m / 0.25 AS mfe_ticks_10m,
        mae_pts_10m / 0.25 AS mae_ticks_10m,

        -- Approximate adaptive trail feasibility.
        -- This does NOT determine exact stop-hit order.
        multiIf
        (
            mae_pts_10m <= 4.00 AND mfe_pts_10m >= 7.50, 'SURVIVES_16T_REACHES_30T',
            mae_pts_10m <= 5.00 AND mfe_pts_10m >= 10.00, 'SURVIVES_20T_REACHES_40T',
            mae_pts_10m <= 6.00 AND mfe_pts_10m >= 12.00, 'SURVIVES_24T_REACHES_48T',
            'NO_SCREEN_EDGE'
        ) AS trailing_screen_label
    FROM path_summary
)

SELECT
    trade_date,
    wall_time,
    wall_et,
    wall_side,
    wall_price,
    wall_score,
    wall_behavior,
    delta_flip_pattern,
    wall_aggression_pattern,
    orb_position,
    vwap_relation,
    atr_regime,
    time_bucket,
    session_extreme_location,
    trade_candidate_class,
    trade_side,
    setup_family,
    trigger_model,
    entry_time,
    entry_price,

    mfe_pts_10m,
    mae_pts_10m,
    mfe_ticks_10m,
    mae_ticks_10m,
    trailing_screen_label
FROM screened;
