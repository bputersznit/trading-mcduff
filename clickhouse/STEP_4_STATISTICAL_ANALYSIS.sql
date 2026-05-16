-- ============================================================================
-- Step 4: Statistical Analysis - Edge Detection
-- ============================================================================
-- Purpose: Validate if enriched wall interactions show tradeable patterns
-- Approach: Analyze expectancy (MFE vs MAE) for different pattern combinations
-- Date: 2026-05-04
-- ============================================================================

-- ============================================================================
-- ANALYSIS 1: Wall Behavior × Side × Expectancy
-- ============================================================================
-- Question: Do REPLENISHING/ICEBERG walls act as support/resistance?

SELECT '=== Wall Behavior × Side Expectancy ===' AS report FORMAT Pretty;

SELECT
    `base.wall_behavior` AS wall_behavior,
    wall_side,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe_10s,
    round(avg(mae_ticks_10s), 2) AS avg_mae_10s,
    round(avg(mfe_ticks_30s), 2) AS avg_mfe_30s,
    round(avg(mae_ticks_30s), 2) AS avg_mae_30s,

    -- Expectancy calculation (is MFE > |MAE| on average?)
    multiIf(
        wall_side = 'BID' AND avg(mfe_ticks_10s) > abs(avg(mae_ticks_10s)), 'POSITIVE',
        wall_side = 'ASK' AND abs(avg(mae_ticks_10s)) > avg(mfe_ticks_10s), 'POSITIVE',
        'NEGATIVE'
    ) AS expectancy_10s,

    round(avg(aggression_into_wall), 0) AS avg_agg_into,
    round(avg(peak_aggression_score), 3) AS avg_agg_score,

    -- How many held vs broke?
    countIf(outcome_basic LIKE 'REJECT%') AS rejections,
    countIf(outcome_basic LIKE 'BREAK%') AS breaks,

    round(countIf(outcome_basic LIKE 'REJECT%') * 100.0 / count(), 2) AS reject_pct

FROM CG_mnq_wall_interactions_enriched_v1
WHERE `base.wall_behavior` != ''
GROUP BY wall_behavior, wall_side
ORDER BY interactions DESC
FORMAT Pretty;

-- ============================================================================
-- ANALYSIS 2: Replenishment Ratio × Rejection Rate
-- ============================================================================
-- Question: Higher replenishment = stronger rejection?

SELECT '=== Replenishment Strength × Rejection Rate ===' AS report FORMAT Pretty;

SELECT
    multiIf(
        `base.replenish_ratio` >= 3.0, 'VERY_STRONG (≥3.0)',
        `base.replenish_ratio` >= 2.0, 'STRONG (2.0-3.0)',
        `base.replenish_ratio` >= 1.0, 'MODERATE (1.0-2.0)',
        'WEAK (<1.0)'
    ) AS replenish_category,
    wall_side,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe_10s,
    round(avg(mae_ticks_10s), 2) AS avg_mae_10s,
    countIf(outcome_basic LIKE 'REJECT%') AS rejections,
    round(countIf(outcome_basic LIKE 'REJECT%') * 100.0 / count(), 2) AS reject_pct,
    round(avg(`base.replenish_ratio`), 2) AS avg_replenish
FROM CG_mnq_wall_interactions_enriched_v1
WHERE `base.wall_behavior` IN ('REPLENISHING_WALL', 'ICEBERG_LIKE_WALL')
  AND `base.replenish_ratio` IS NOT NULL
GROUP BY replenish_category, wall_side
ORDER BY avg_replenish DESC
FORMAT Pretty;

-- ============================================================================
-- ANALYSIS 3: Delta Flip × Price Move
-- ============================================================================
-- Question: Do delta flips predict reversals?

SELECT '=== Delta Flip Pattern × Price Response ===' AS report FORMAT Pretty;

SELECT
    delta_flip_pattern,
    wall_side,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe_10s,
    round(avg(mae_ticks_10s), 2) AS avg_mae_10s,
    round(avg(abs(mfe_ticks_10s)), 2) AS avg_abs_mfe,

    -- After delta flip, did price reverse or continue?
    multiIf(
        delta_flip_pattern = 'BUY_TO_SELL_FLIP' AND wall_side = 'BID' AND avg(mfe_ticks_10s) > 5, 'REVERSAL_UP',
        delta_flip_pattern = 'SELL_TO_BUY_FLIP' AND wall_side = 'ASK' AND avg(mae_ticks_10s) < -5, 'REVERSAL_DOWN',
        delta_flip_pattern LIKE 'CONTINUED%', 'CONTINUATION',
        'UNCLEAR'
    ) AS pattern_behavior,

    round(avg(delta_before_5s), 0) AS avg_delta_before,
    round(avg(delta_after_5s), 0) AS avg_delta_after

FROM CG_mnq_wall_interactions_enriched_v1
GROUP BY delta_flip_pattern, wall_side
HAVING interactions > 100
ORDER BY interactions DESC
FORMAT Pretty;

-- ============================================================================
-- ANALYSIS 4: Aggression Level × Outcome
-- ============================================================================
-- Question: Does high aggression break walls or get absorbed?

SELECT '=== Aggression × Outcome Matrix ===' AS report FORMAT Pretty;

SELECT
    aggression_classification,
    outcome_basic,
    count() AS interactions,
    round(count() * 100.0 / sum(count()) OVER (PARTITION BY aggression_classification), 2) AS pct_of_aggression_class,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae,
    round(avg(aggression_into_wall), 0) AS avg_agg_into,
    round(avg(total_volume_at_wall), 0) AS avg_vol_at_wall
FROM CG_mnq_wall_interactions_enriched_v1
GROUP BY aggression_classification, outcome_basic
ORDER BY aggression_classification, interactions DESC
FORMAT Pretty;

-- ============================================================================
-- ANALYSIS 5: Pulled Wall × Breakout Continuation
-- ============================================================================
-- Question: Do pulled walls predict breakouts?

SELECT '=== Pulled Walls × Breakout Strength ===' AS report FORMAT Pretty;

SELECT
    multiIf(
        `base.pull_ratio` >= 0.90, 'VERY_HIGH_PULL (≥90%)',
        `base.pull_ratio` >= 0.70, 'HIGH_PULL (70-90%)',
        `base.pull_ratio` >= 0.50, 'MODERATE_PULL (50-70%)',
        'LOW_PULL (<50%)'
    ) AS pull_category,
    wall_side,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe_10s,
    round(avg(mae_ticks_10s), 2) AS avg_mae_10s,
    countIf(abs(mfe_ticks_10s) > 10) AS big_moves,
    round(countIf(abs(mfe_ticks_10s) > 10) * 100.0 / count(), 2) AS big_move_pct,
    round(avg(`base.pull_ratio`), 3) AS avg_pull_ratio
FROM CG_mnq_wall_interactions_enriched_v1
WHERE `base.wall_behavior` = 'PULLED_WALL'
  AND `base.pull_ratio` IS NOT NULL
GROUP BY pull_category, wall_side
ORDER BY avg_pull_ratio DESC
FORMAT Pretty;

-- ============================================================================
-- ANALYSIS 6: Day-by-Day Consistency Check
-- ============================================================================
-- Question: Is edge consistent across days or just one lucky day?

SELECT '=== Daily Expectancy Consistency ===' AS report FORMAT Pretty;

SELECT
    toDate(base_interaction_time) AS trade_date,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae,
    round(avg(mfe_ticks_10s) + avg(mae_ticks_10s), 2) AS net_expectancy_10s,

    -- Count meaningful wall behaviors per day
    countIf(`base.wall_behavior` = 'REPLENISHING_WALL') AS replenishing,
    countIf(`base.wall_behavior` = 'PULLED_WALL') AS pulled,
    countIf(`base.wall_behavior` = 'ICEBERG_LIKE_WALL') AS iceberg,

    -- Aggression distribution
    countIf(aggression_classification = 'HIGH') AS high_agg,
    round(avg(peak_aggression_score), 3) AS avg_agg_score

FROM CG_mnq_wall_interactions_enriched_v1
GROUP BY trade_date
ORDER BY trade_date
FORMAT Pretty;

-- ============================================================================
-- ANALYSIS 7: Top Edge Candidates (Positive Expectancy Patterns)
-- ============================================================================
-- Question: Which specific combinations show the clearest edge?

SELECT '=== Top Positive Expectancy Patterns ===' AS report FORMAT Pretty;

SELECT
    `base.wall_behavior` AS wall_behavior,
    wall_side,
    aggression_classification,
    delta_flip_pattern,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae,
    round(avg(mfe_ticks_10s) + avg(mae_ticks_10s), 2) AS net_ticks_10s,

    -- Tradeable metric: avg profit - avg loss
    multiIf(
        net_ticks_10s > 5, 'STRONG_EDGE',
        net_ticks_10s > 0, 'WEAK_EDGE',
        'NO_EDGE'
    ) AS edge_classification,

    round(avg(aggression_into_wall), 0) AS avg_agg_into

FROM CG_mnq_wall_interactions_enriched_v1
WHERE `base.wall_behavior` != ''
GROUP BY wall_behavior, wall_side, aggression_classification, delta_flip_pattern
HAVING interactions >= 10
ORDER BY net_ticks_10s DESC
LIMIT 20
FORMAT Pretty;

-- ============================================================================
-- ANALYSIS 8: BID Support vs ASK Resistance Asymmetry
-- ============================================================================
-- Question: Are BID walls (support) stronger than ASK walls (resistance)?

SELECT '=== BID vs ASK Wall Strength ===' AS report FORMAT Pretty;

SELECT
    wall_side,
    count() AS total_interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae,

    -- For BID: positive MFE = bounce up (support held)
    -- For ASK: negative MAE = bounce down (resistance held)
    multiIf(
        wall_side = 'BID', round(avg(mfe_ticks_10s), 2),
        wall_side = 'ASK', round(avg(mae_ticks_10s), 2),
        0
    ) AS directional_expectancy,

    countIf(outcome_basic LIKE 'REJECT%') AS rejections,
    countIf(outcome_basic LIKE 'BREAK%') AS breaks,
    round(countIf(outcome_basic LIKE 'REJECT%') * 100.0 / count(), 2) AS reject_pct,

    round(avg(aggression_into_wall), 0) AS avg_agg_into,
    round(avg(`base.wall_size`), 0) AS avg_wall_size

FROM CG_mnq_wall_interactions_enriched_v1
GROUP BY wall_side
FORMAT Pretty;

-- ============================================================================
-- ANALYSIS 9: Absorption Score Distribution
-- ============================================================================
-- Question: Can we identify absorption events (high agg + no move)?

SELECT '=== Potential Absorption Events ===' AS report FORMAT Pretty;

SELECT
    multiIf(
        peak_aggression_score >= 0.90 AND abs(mfe_ticks_10s) < 5, 'ABSORBED',
        peak_aggression_score >= 0.70 AND abs(mfe_ticks_10s) < 10, 'MODERATE_ABSORPTION',
        peak_aggression_score >= 0.50 AND abs(mfe_ticks_10s) > 15, 'BREAKOUT',
        'OTHER'
    ) AS absorption_category,

    wall_side,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae,
    round(avg(peak_aggression_score), 3) AS avg_agg_score,
    round(avg(aggression_into_wall), 0) AS avg_agg_into,
    round(avg(total_volume_at_wall), 0) AS avg_vol

FROM CG_mnq_wall_interactions_enriched_v1
WHERE peak_aggression_score IS NOT NULL
  AND peak_aggression_score > 0.5
GROUP BY absorption_category, wall_side
ORDER BY interactions DESC
FORMAT Pretty;

-- ============================================================================
-- ANALYSIS 10: Summary - Is There Edge?
-- ============================================================================

SELECT '=== FINAL VERDICT: Edge Summary ===' AS report FORMAT Pretty;

WITH overall_stats AS (
    SELECT
        count() AS total_interactions,
        round(avg(mfe_ticks_10s), 2) AS overall_avg_mfe,
        round(avg(mae_ticks_10s), 2) AS overall_avg_mae,
        round(avg(mfe_ticks_10s) + avg(mae_ticks_10s), 2) AS overall_net_expectancy,
        countIf(`base.wall_behavior` != '') AS with_behavior,
        countIf(outcome_basic LIKE 'REJECT%') AS total_rejections,
        countIf(aggression_classification = 'HIGH') AS high_agg_count
    FROM CG_mnq_wall_interactions_enriched_v1
)
SELECT
    total_interactions,
    overall_avg_mfe AS avg_mfe_10s,
    overall_avg_mae AS avg_mae_10s,
    overall_net_expectancy AS net_expectancy_10s,

    multiIf(
        overall_net_expectancy > 10, 'STRONG POSITIVE EDGE',
        overall_net_expectancy > 0, 'WEAK POSITIVE EDGE',
        overall_net_expectancy > -10, 'NEUTRAL / NO EDGE',
        'NEGATIVE EDGE'
    ) AS edge_verdict,

    with_behavior AS interactions_with_behavior,
    round(with_behavior * 100.0 / total_interactions, 2) AS behavior_coverage_pct,
    total_rejections,
    round(total_rejections * 100.0 / total_interactions, 4) AS rejection_rate_pct,

    multiIf(
        rejection_rate_pct < 0.1, 'CRITICAL: <0.1% rejection rate (outcome logic broken)',
        rejection_rate_pct < 5, 'WARNING: <5% rejection rate (weak behavioral signal)',
        rejection_rate_pct < 20, 'MODERATE: 5-20% rejection rate',
        'GOOD: >20% rejection rate'
    ) AS signal_quality

FROM overall_stats
FORMAT Pretty;
