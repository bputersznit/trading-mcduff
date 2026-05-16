-- ============================================================
-- PHASE 5 TRIGGER LAYER
-- Pullback + Confirmation Model
--
-- Input:
--   CG_mnq_wall_outcomes_regime_v1
--   mnq_trades
--
-- Output:
--   CG_mnq_phase5_trigger_candidates_v1
--
-- Purpose:
--   Convert Phase 4 contextual wall pressure into executable
--   path-aware entry candidates.
--
-- Strategy:
--   1. Keep only Phase 4 candidate regimes.
--   2. Observe post-wall price path.
--   3. Require confirmation move away from wall.
--   4. Require controlled pullback/retest.
--   5. Define delayed entry price/time.
--
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_phase5_trigger_candidates_v1;

CREATE TABLE CG_mnq_phase5_trigger_candidates_v1
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY
(
    trade_date,
    wall_time,
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

        outcome_label_30s,
        outcome_move_ticks_30s,

        wall_behavior,
        delta_flip_pattern,
        wall_aggression_pattern,

        orb_position,
        vwap_relation,
        atr_regime,
        time_bucket,
        session_extreme_location,

        trade_candidate_class,

        multiIf
        (
            trade_candidate_class = 'REJECTION_LONG_CANDIDATE',  'LONG',
            trade_candidate_class = 'BREAK_LONG_CANDIDATE',      'LONG',
            trade_candidate_class = 'REJECTION_SHORT_CANDIDATE', 'SHORT',
            trade_candidate_class = 'BREAK_SHORT_CANDIDATE',     'SHORT',
            'NONE'
        ) AS trade_side,

        multiIf
        (
            trade_candidate_class IN ('REJECTION_LONG_CANDIDATE', 'REJECTION_SHORT_CANDIDATE'),
            'REJECTION',

            trade_candidate_class IN ('BREAK_LONG_CANDIDATE', 'BREAK_SHORT_CANDIDATE'),
            'BREAK',

            'NONE'
        ) AS setup_family
    FROM CG_mnq_wall_outcomes_regime_v1
    WHERE trade_candidate_class != 'NO_CLEAR_TRADE_CANDIDATE'
      AND vwap_relation = 'BELOW_VWAP'
      AND time_bucket IN ('RTH_OPEN', 'PM_DRIFT', 'CLOSE')
      AND atr_regime IN ('LOW_VOL', 'NORMAL_VOL')
),

trade_path AS
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

        b.orb_position AS orb_position,
        b.vwap_relation AS vwap_relation,
        b.atr_regime AS atr_regime,
        b.time_bucket AS time_bucket,
        b.session_extreme_location AS session_extreme_location,

        b.trade_candidate_class AS trade_candidate_class,
        b.trade_side AS trade_side,
        b.setup_family AS setup_family,

        t.ts_event AS path_time,
        t.price AS path_price
    FROM base AS b
    INNER JOIN mnq_trades AS t
        ON toDate(toTimeZone(t.ts_event, 'America/New_York')) = b.trade_date
       AND t.ts_event >= b.wall_time
       AND t.ts_event <  b.wall_time + INTERVAL 90 SECOND
),

-- First pass: calculate confirmation times
confirmation_times AS
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

        orb_position,
        vwap_relation,
        atr_regime,
        time_bucket,
        session_extreme_location,

        trade_candidate_class,
        trade_side,
        setup_family,

        -- Confirmation: price moves away from wall in intended direction.
        minIf
        (
            path_time,
            trade_side = 'LONG'
            AND path_price >= wall_price + 1.50
        ) AS long_confirm_time_6t,

        minIf
        (
            path_time,
            trade_side = 'SHORT'
            AND path_price <= wall_price - 1.50
        ) AS short_confirm_time_6t,

        minIf
        (
            path_time,
            trade_side = 'LONG'
            AND path_price >= wall_price + 2.00
        ) AS long_confirm_time_8t,

        minIf
        (
            path_time,
            trade_side = 'SHORT'
            AND path_price <= wall_price - 2.00
        ) AS short_confirm_time_8t,

        min(path_price) AS min_price_90s,
        max(path_price) AS max_price_90s
    FROM trade_path
    GROUP BY
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

        orb_position,
        vwap_relation,
        atr_regime,
        time_bucket,
        session_extreme_location,

        trade_candidate_class,
        trade_side,
        setup_family
),

-- Second pass: join back to path for pullback detection
path_with_confirms AS
(
    SELECT
        tp.*,
        ct.long_confirm_time_6t,
        ct.short_confirm_time_6t,
        ct.long_confirm_time_8t,
        ct.short_confirm_time_8t,
        ct.min_price_90s,
        ct.max_price_90s
    FROM trade_path tp
    INNER JOIN confirmation_times ct
        ON tp.trade_date = ct.trade_date
       AND tp.wall_time = ct.wall_time
),

-- Third pass: calculate pullback times
path_features AS
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

        orb_position,
        vwap_relation,
        atr_regime,
        time_bucket,
        session_extreme_location,

        trade_candidate_class,
        trade_side,
        setup_family,

        long_confirm_time_6t,
        short_confirm_time_6t,
        long_confirm_time_8t,
        short_confirm_time_8t,
        min_price_90s,
        max_price_90s,

        -- Pullback/retest after 6-tick confirmation.
        minIf
        (
            path_time,
            trade_side = 'LONG'
            AND path_time > long_confirm_time_6t
            AND path_price <= wall_price + 0.75
            AND path_price >= wall_price - 0.50
        ) AS long_pullback_time_after_6t,

        minIf
        (
            path_time,
            trade_side = 'SHORT'
            AND path_time > short_confirm_time_6t
            AND path_price >= wall_price - 0.75
            AND path_price <= wall_price + 0.50
        ) AS short_pullback_time_after_6t,

        -- Pullback/retest after 8-tick confirmation.
        minIf
        (
            path_time,
            trade_side = 'LONG'
            AND path_time > long_confirm_time_8t
            AND path_price <= wall_price + 1.00
            AND path_price >= wall_price - 0.50
        ) AS long_pullback_time_after_8t,

        minIf
        (
            path_time,
            trade_side = 'SHORT'
            AND path_time > short_confirm_time_8t
            AND path_price >= wall_price - 1.00
            AND path_price <= wall_price + 0.50
        ) AS short_pullback_time_after_8t
    FROM path_with_confirms
    GROUP BY
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

        orb_position,
        vwap_relation,
        atr_regime,
        time_bucket,
        session_extreme_location,

        trade_candidate_class,
        trade_side,
        setup_family,

        long_confirm_time_6t,
        short_confirm_time_6t,
        long_confirm_time_8t,
        short_confirm_time_8t,
        min_price_90s,
        max_price_90s
),

models AS
(
    SELECT
        *,
        'CONFIRM_6T_MARKET' AS trigger_model,
        multiIf
        (
            trade_side = 'LONG', long_confirm_time_6t,
            trade_side = 'SHORT', short_confirm_time_6t,
            NULL
        ) AS entry_time,
        multiIf
        (
            trade_side = 'LONG', wall_price + 1.50,
            trade_side = 'SHORT', wall_price - 1.50,
            NULL
        ) AS entry_price
    FROM path_features

    UNION ALL

    SELECT
        *,
        'CONFIRM_8T_MARKET' AS trigger_model,
        multiIf
        (
            trade_side = 'LONG', long_confirm_time_8t,
            trade_side = 'SHORT', short_confirm_time_8t,
            NULL
        ) AS entry_time,
        multiIf
        (
            trade_side = 'LONG', wall_price + 2.00,
            trade_side = 'SHORT', wall_price - 2.00,
            NULL
        ) AS entry_price
    FROM path_features

    UNION ALL

    SELECT
        *,
        'CONFIRM_6T_PULLBACK' AS trigger_model,
        multiIf
        (
            trade_side = 'LONG', long_pullback_time_after_6t,
            trade_side = 'SHORT', short_pullback_time_after_6t,
            NULL
        ) AS entry_time,
        multiIf
        (
            trade_side = 'LONG', wall_price + 0.75,
            trade_side = 'SHORT', wall_price - 0.75,
            NULL
        ) AS entry_price
    FROM path_features

    UNION ALL

    SELECT
        *,
        'CONFIRM_8T_PULLBACK' AS trigger_model,
        multiIf
        (
            trade_side = 'LONG', long_pullback_time_after_8t,
            trade_side = 'SHORT', short_pullback_time_after_8t,
            NULL
        ) AS entry_time,
        multiIf
        (
            trade_side = 'LONG', wall_price + 1.00,
            trade_side = 'SHORT', wall_price - 1.00,
            NULL
        ) AS entry_price
    FROM path_features
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

    min_price_90s,
    max_price_90s,

    multiIf
    (
        trade_side = 'LONG', max_price_90s - entry_price,
        trade_side = 'SHORT', entry_price - min_price_90s,
        NULL
    ) AS mfe_pts_after_entry,

    multiIf
    (
        trade_side = 'LONG', entry_price - min_price_90s,
        trade_side = 'SHORT', max_price_90s - entry_price,
        NULL
    ) AS mae_pts_after_entry
FROM models
WHERE entry_time IS NOT NULL;
