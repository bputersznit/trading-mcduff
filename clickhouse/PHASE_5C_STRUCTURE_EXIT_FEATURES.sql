-- ============================================================
-- PHASE 5C STRUCTURE-BASED EXIT FEATURE TABLE
--
-- Input:
--   CG_mnq_phase5_trigger_candidates_v1
--   CG_mnq_wall_outcomes_regime_v1
--   mnq_trades
--
-- Output:
--   CG_mnq_phase5c_structure_exit_features_v1
--
-- Purpose:
--   Build structural target/exit context for Phase 5 entries:
--     - VWAP target
--     - ORB high/low target
--     - opposite wall pressure event
--
-- Notes:
--   This is feature prep only.
--   Python simulator is final authority for path order.
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_phase5c_structure_exit_features_v1;

CREATE TABLE CG_mnq_phase5c_structure_exit_features_v1
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
      AND vwap_relation = 'BELOW_VWAP'
      AND atr_regime IN ('LOW_VOL', 'NORMAL_VOL')
      AND time_bucket IN ('RTH_OPEN', 'PM_DRIFT', 'CLOSE')
),

ctx AS
(
    SELECT
        trade_date,
        wall_time,
        session_vwap,
        orb_low,
        orb_high,
        distance_from_vwap,
        distance_from_orb_high,
        distance_from_orb_low
    FROM CG_mnq_wall_outcomes_regime_v1
),

base_with_context AS
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

        c.session_vwap AS session_vwap,
        c.orb_low AS orb_low,
        c.orb_high AS orb_high,

        multiIf
        (
            b.trade_side = 'LONG', c.session_vwap,
            b.trade_side = 'SHORT', c.session_vwap,
            NULL
        ) AS vwap_target_price,

        multiIf
        (
            b.trade_side = 'LONG'
                AND b.entry_price < c.orb_low,
                c.orb_low,

            b.trade_side = 'LONG'
                AND b.entry_price >= c.orb_low
                AND b.entry_price < c.orb_high,
                c.orb_high,

            b.trade_side = 'SHORT'
                AND b.entry_price > c.orb_high,
                c.orb_high,

            b.trade_side = 'SHORT'
                AND b.entry_price <= c.orb_high
                AND b.entry_price > c.orb_low,
                c.orb_low,

            NULL
        ) AS orb_target_price
    FROM base AS b
    LEFT JOIN ctx AS c
        ON b.trade_date = c.trade_date
       AND b.wall_time = c.wall_time
),

opposite_pressure AS
(
    SELECT
        b.trade_date AS trade_date,
        b.entry_time AS entry_time,
        minIf
        (
            r.wall_time,
            b.trade_side = 'LONG'
            AND r.wall_time > b.entry_time
            AND r.wall_time <= b.entry_time + INTERVAL 10 MINUTE
            AND r.wall_side = 'ASK'
            AND r.trade_candidate_class IN ('REJECTION_SHORT_CANDIDATE', 'BREAK_SHORT_CANDIDATE')
        ) AS long_opposite_pressure_time,

        minIf
        (
            r.wall_time,
            b.trade_side = 'SHORT'
            AND r.wall_time > b.entry_time
            AND r.wall_time <= b.entry_time + INTERVAL 10 MINUTE
            AND r.wall_side = 'BID'
            AND r.trade_candidate_class IN ('REJECTION_LONG_CANDIDATE', 'BREAK_LONG_CANDIDATE')
        ) AS short_opposite_pressure_time
    FROM base AS b
    LEFT JOIN CG_mnq_wall_outcomes_regime_v1 AS r
        ON b.trade_date = r.trade_date
       AND r.wall_time > b.entry_time
       AND r.wall_time <= b.entry_time + INTERVAL 10 MINUTE
    GROUP BY
        b.trade_date,
        b.entry_time
)

SELECT
    b.trade_date,
    b.wall_time,
    b.wall_et,
    b.wall_side,
    b.wall_price,
    b.wall_score,
    b.wall_behavior,
    b.delta_flip_pattern,
    b.wall_aggression_pattern,

    b.orb_position,
    b.vwap_relation,
    b.atr_regime,
    b.time_bucket,
    b.session_extreme_location,

    b.trade_candidate_class,
    b.trade_side,
    b.setup_family,
    b.trigger_model,
    b.entry_time,
    b.entry_price,

    b.session_vwap,
    b.orb_low,
    b.orb_high,
    b.vwap_target_price,
    b.orb_target_price,

    multiIf
    (
        b.trade_side = 'LONG',  o.long_opposite_pressure_time,
        b.trade_side = 'SHORT', o.short_opposite_pressure_time,
        NULL
    ) AS opposite_pressure_time
FROM base_with_context AS b
LEFT JOIN opposite_pressure AS o
    ON b.trade_date = o.trade_date
   AND b.entry_time = o.entry_time
WHERE b.session_vwap IS NOT NULL;
