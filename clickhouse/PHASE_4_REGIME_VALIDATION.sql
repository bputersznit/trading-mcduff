-- ============================================================
-- PHASE 4 REGIME VALIDATION / EDGE DISCOVERY
-- ============================================================

-- ------------------------------------------------------------
-- 1. Row count parity check
-- ------------------------------------------------------------
SELECT
    'ROW COUNT PARITY' AS report,
    src.rows AS phase3_rows,
    dst.rows AS phase4_rows,
    multiIf(src.rows = dst.rows, 'PASS', 'FAIL') AS status
FROM
(
    SELECT count() AS rows
    FROM CG_mnq_wall_outcomes_enriched_v2
) AS src
CROSS JOIN
(
    SELECT count() AS rows
    FROM CG_mnq_wall_outcomes_regime_v1
) AS dst;


-- ------------------------------------------------------------
-- 2. Regime coverage
-- ------------------------------------------------------------
SELECT
    time_bucket,
    atr_regime,
    count() AS n,
    round(100 * n / sum(n) OVER (), 2) AS pct
FROM CG_mnq_wall_outcomes_regime_v1
GROUP BY
    time_bucket,
    atr_regime
ORDER BY
    time_bucket,
    atr_regime;


-- ------------------------------------------------------------
-- 3. ORB / VWAP / wall-side distribution
-- ------------------------------------------------------------
SELECT
    wall_side,
    orb_position,
    vwap_relation,
    count() AS n,
    round(100 * n / sum(n) OVER (), 2) AS pct
FROM CG_mnq_wall_outcomes_regime_v1
GROUP BY
    wall_side,
    orb_position,
    vwap_relation
ORDER BY
    n DESC;


-- ------------------------------------------------------------
-- 4. Core conditional edge discovery
-- ------------------------------------------------------------
SELECT
    wall_side,
    delta_flip_pattern,
    orb_position,
    vwap_relation,
    atr_regime,
    time_bucket,
    session_extreme_location,

    count() AS setups,

    countIf(outcome_label_30s = 'REJECT') AS rejects,
    countIf(outcome_label_30s = 'BREAK') AS breaks,
    countIf(outcome_label_30s = 'TWO_WAY_VOLATILE') AS two_way,
    countIf(outcome_label_30s = 'NO_RESOLUTION') AS no_resolution,

    round(rejects / setups, 4) AS reject_rate,
    round(breaks / setups, 4) AS break_rate,

    round(avgIf(outcome_move_ticks_30s, outcome_label_30s = 'REJECT'), 2) AS avg_reject_ticks,
    round(avgIf(outcome_move_ticks_30s, outcome_label_30s = 'BREAK'), 2) AS avg_break_ticks,

    multiIf
    (
        setups >= 200
        AND reject_rate >= 0.45
        AND avg_reject_ticks >= 18,
        'TRADE_REJECTION_EDGE',

        setups >= 200
        AND break_rate >= 0.45
        AND avg_break_ticks >= 18,
        'TRADE_BREAK_EDGE',

        setups >= 100
        AND reject_rate >= 0.42
        AND avg_reject_ticks >= 18,
        'WATCH_REJECTION_EDGE',

        setups >= 100
        AND break_rate >= 0.42
        AND avg_break_ticks >= 18,
        'WATCH_BREAK_EDGE',

        'NO_CLEAR_EDGE'
    ) AS edge_label
FROM CG_mnq_wall_outcomes_regime_v1
WHERE delta_flip_pattern != 'NO_CLEAR_DELTA'
GROUP BY
    wall_side,
    delta_flip_pattern,
    orb_position,
    vwap_relation,
    atr_regime,
    time_bucket,
    session_extreme_location
HAVING setups >= 50
ORDER BY
    edge_label ASC,
    setups DESC,
    greatest(reject_rate, break_rate) DESC;


-- ------------------------------------------------------------
-- 5. Rejection-candidate ranking only
-- ------------------------------------------------------------
SELECT
    wall_side,
    delta_flip_pattern,
    orb_position,
    vwap_relation,
    atr_regime,
    time_bucket,
    session_extreme_location,

    count() AS setups,
    countIf(outcome_label_30s = 'REJECT') AS wins,
    countIf(outcome_label_30s IN ('BREAK', 'TWO_WAY_VOLATILE', 'NO_RESOLUTION')) AS losses,

    round(wins / setups, 4) AS win_rate,
    round(avgIf(outcome_move_ticks_30s, outcome_label_30s = 'REJECT'), 2) AS avg_win_ticks,
    round(avgIf(outcome_move_ticks_30s, outcome_label_30s != 'REJECT'), 2) AS avg_loss_move_ticks,

    round((win_rate * avg_win_ticks) - ((1 - win_rate) * 8), 2) AS expectancy_ticks_using_8t_stop,

    multiIf
    (
        wall_side = 'ASK', 'SHORT_REJECT',
        wall_side = 'BID', 'LONG_REJECT',
        'UNKNOWN'
    ) AS strategy_side
FROM CG_mnq_wall_outcomes_regime_v1
WHERE trade_candidate_class IN
(
    'REJECTION_SHORT_CANDIDATE',
    'REJECTION_LONG_CANDIDATE'
)
GROUP BY
    wall_side,
    delta_flip_pattern,
    orb_position,
    vwap_relation,
    atr_regime,
    time_bucket,
    session_extreme_location
HAVING setups >= 100
ORDER BY
    expectancy_ticks_using_8t_stop DESC,
    setups DESC;


-- ------------------------------------------------------------
-- 6. Break-candidate ranking only
-- ------------------------------------------------------------
SELECT
    wall_side,
    delta_flip_pattern,
    orb_position,
    vwap_relation,
    atr_regime,
    time_bucket,
    session_extreme_location,

    count() AS setups,
    countIf(outcome_label_30s = 'BREAK') AS wins,
    countIf(outcome_label_30s IN ('REJECT', 'TWO_WAY_VOLATILE', 'NO_RESOLUTION')) AS losses,

    round(wins / setups, 4) AS win_rate,
    round(avgIf(outcome_move_ticks_30s, outcome_label_30s = 'BREAK'), 2) AS avg_win_ticks,
    round(avgIf(outcome_move_ticks_30s, outcome_label_30s != 'BREAK'), 2) AS avg_loss_move_ticks,

    round((win_rate * avg_win_ticks) - ((1 - win_rate) * 8), 2) AS expectancy_ticks_using_8t_stop,

    multiIf
    (
        wall_side = 'ASK', 'LONG_BREAK',
        wall_side = 'BID', 'SHORT_BREAK',
        'UNKNOWN'
    ) AS strategy_side
FROM CG_mnq_wall_outcomes_regime_v1
WHERE trade_candidate_class IN
(
    'BREAK_LONG_CANDIDATE',
    'BREAK_SHORT_CANDIDATE'
)
GROUP BY
    wall_side,
    delta_flip_pattern,
    orb_position,
    vwap_relation,
    atr_regime,
    time_bucket,
    session_extreme_location
HAVING setups >= 100
ORDER BY
    expectancy_ticks_using_8t_stop DESC,
    setups DESC;


-- ------------------------------------------------------------
-- 7. Final tradeable strategy candidates
-- ------------------------------------------------------------
SELECT
    trade_candidate_class,
    wall_side,
    delta_flip_pattern,
    orb_position,
    vwap_relation,
    atr_regime,
    time_bucket,
    session_extreme_location,

    count() AS setups,

    countIf
    (
        trade_candidate_class IN ('REJECTION_SHORT_CANDIDATE', 'REJECTION_LONG_CANDIDATE')
        AND outcome_label_30s = 'REJECT'
    )
    +
    countIf
    (
        trade_candidate_class IN ('BREAK_LONG_CANDIDATE', 'BREAK_SHORT_CANDIDATE')
        AND outcome_label_30s = 'BREAK'
    ) AS model_wins,

    round(model_wins / setups, 4) AS model_win_rate,

    round
    (
        avgIf
        (
            outcome_move_ticks_30s,
            (
                trade_candidate_class IN ('REJECTION_SHORT_CANDIDATE', 'REJECTION_LONG_CANDIDATE')
                AND outcome_label_30s = 'REJECT'
            )
            OR
            (
                trade_candidate_class IN ('BREAK_LONG_CANDIDATE', 'BREAK_SHORT_CANDIDATE')
                AND outcome_label_30s = 'BREAK'
            )
        ),
        2
    ) AS avg_model_win_ticks,

    round((model_win_rate * avg_model_win_ticks) - ((1 - model_win_rate) * 8), 2) AS expectancy_ticks_using_8t_stop,

    multiIf
    (
        setups >= 200
        AND model_win_rate >= 0.45
        AND expectancy_ticks_using_8t_stop >= 2.0,
        'PRIMARY_CANDIDATE',

        setups >= 100
        AND model_win_rate >= 0.42
        AND expectancy_ticks_using_8t_stop >= 1.0,
        'SECONDARY_CANDIDATE',

        'REJECT_FOR_NOW'
    ) AS deployment_label
FROM CG_mnq_wall_outcomes_regime_v1
WHERE trade_candidate_class != 'NO_CLEAR_TRADE_CANDIDATE'
GROUP BY
    trade_candidate_class,
    wall_side,
    delta_flip_pattern,
    orb_position,
    vwap_relation,
    atr_regime,
    time_bucket,
    session_extreme_location
HAVING setups >= 50
ORDER BY
    deployment_label ASC,
    expectancy_ticks_using_8t_stop DESC,
    setups DESC;
