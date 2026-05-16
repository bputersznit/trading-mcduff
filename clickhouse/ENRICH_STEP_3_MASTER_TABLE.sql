-- ============================================================================
-- Enrichment Step 3: Master Enriched Table
-- ============================================================================
-- Purpose: Join base + response + aggression into single research table
-- This is the PRIMARY table for strategy development
-- Date: 2026-05-04
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_interactions_enriched_v1;

CREATE TABLE CG_mnq_wall_interactions_enriched_v1
ENGINE = MergeTree
PARTITION BY toDate(interaction_time)
ORDER BY (interaction_time, wall_id)
AS
SELECT
    -- ================================================================
    -- BASE INTERACTION (from CG_mnq_wall_interactions)
    -- ================================================================
    base.wall_id,
    base.interaction_time,
    base.`w.wall_side` AS wall_side,
    base.`w.wall_price` AS wall_price,
    base.wall_size,
    base.`w.wall_score` AS wall_score,
    base.`w.wall_rank` AS wall_rank,
    base.`w.wall_type` AS wall_type,
    base.wall_behavior,
    base.pull_ratio,
    base.fill_ratio,
    base.replenish_ratio,
    base.wall_lifetime_ms,
    base.price_at_interaction,
    base.distance_to_wall_ticks,

    -- ================================================================
    -- RESPONSE METRICS (from response_v1)
    -- ================================================================
    resp.future_high_10s,
    resp.future_low_10s,
    resp.future_high_30s,
    resp.future_low_30s,
    resp.mfe_ticks_10s,
    resp.mae_ticks_10s,
    resp.mfe_ticks_30s,
    resp.mae_ticks_30s,
    resp.outcome_basic,

    -- ================================================================
    -- AGGRESSION METRICS (from aggression_v1)
    -- ================================================================
    agg.buy_volume_before_5s,
    agg.sell_volume_before_5s,
    agg.delta_before_5s,
    agg.buy_volume_after_5s,
    agg.sell_volume_after_5s,
    agg.delta_after_5s,
    agg.buy_volume_at_wall,
    agg.sell_volume_at_wall,
    agg.total_volume_at_wall,
    agg.delta_at_wall,
    agg.peak_aggression_score,
    agg.aggression_into_wall,
    agg.aggression_away_from_wall,
    agg.delta_flip_pattern,
    agg.aggression_classification,

    -- ================================================================
    -- DERIVED METRICS (computed from joined data)
    -- ================================================================

    -- Absorption score: high aggression + no price move = absorption
    CASE
        WHEN abs(resp.mfe_ticks_10s) > 0
        THEN round(agg.aggression_into_wall / abs(resp.mfe_ticks_10s), 4)
        ELSE 0
    END AS absorption_score,

    -- Efficiency: price move per volume unit
    CASE
        WHEN agg.total_volume_at_wall > 0
        THEN round(resp.mfe_ticks_10s / agg.total_volume_at_wall, 6)
        ELSE 0
    END AS price_efficiency,

    -- Wall strength indicator (replenishment + iceberg behavior)
    multiIf(
        base.wall_behavior = 'ICEBERG_LIKE_WALL', 1.0,
        base.wall_behavior = 'REPLENISHING_WALL' AND base.replenish_ratio > 2.0, 0.8,
        base.wall_behavior = 'REPLENISHING_WALL', 0.6,
        base.wall_behavior = 'PULLED_WALL', 0.2,
        0.4
    ) AS wall_strength,

    -- Pull + break score (wall pulled → continuation likely)
    CASE
        WHEN base.wall_behavior = 'PULLED_WALL' AND base.pull_ratio > 0.70
        THEN round(base.pull_ratio * abs(resp.mfe_ticks_10s) / 100.0, 4)
        ELSE 0
    END AS pull_break_score,

    -- Iceberg fade score (strong replenishment → fade opportunity)
    CASE
        WHEN base.wall_behavior IN ('ICEBERG_LIKE_WALL', 'REPLENISHING_WALL')
             AND base.replenish_ratio > 2.0
        THEN round(base.replenish_ratio * (1.0 - agg.peak_aggression_score), 4)
        ELSE 0
    END AS iceberg_fade_score,

    -- ================================================================
    -- ADVANCED OUTCOME CLASSIFICATION
    -- ================================================================
    -- McDuff's 6 tradeable patterns

    CASE
        -- Pattern 1: ABSORB_REVERSE
        -- High aggression into wall → price doesn't break → reversal
        WHEN agg.peak_aggression_score > 0.60
             AND abs(resp.mfe_ticks_10s) < 8  -- Price didn't move much
             AND (
                 (base.`w.wall_side` = 'BID' AND resp.mfe_ticks_10s > 3) OR  -- Bounced up from support
                 (base.`w.wall_side` = 'ASK' AND resp.mfe_ticks_10s < -3)    -- Bounced down from resistance
             )
        THEN 'ABSORB_REVERSE'

        -- Pattern 2: PULL_THEN_BREAK
        -- Wall pulled before test → breakout continuation
        WHEN base.wall_behavior = 'PULLED_WALL'
             AND base.pull_ratio > 0.70
             AND abs(resp.mfe_ticks_10s) > 8  -- Significant move
        THEN 'PULL_THEN_BREAK'

        -- Pattern 3: ICEBERG_REJECT
        -- Replenishing wall holds → fade opportunity
        WHEN base.wall_behavior IN ('ICEBERG_LIKE_WALL', 'REPLENISHING_WALL')
             AND base.replenish_ratio > 2.0
             AND (
                 (base.`w.wall_side` = 'BID' AND resp.mae_ticks_10s > -5) OR  -- Support held
                 (base.`w.wall_side` = 'ASK' AND resp.mfe_ticks_10s < 5)      -- Resistance held
             )
        THEN 'ICEBERG_REJECT'

        -- Pattern 4: REPLENISHING_HOLD
        -- Lower replenishment but still holds
        WHEN base.wall_behavior = 'REPLENISHING_WALL'
             AND base.replenish_ratio > 1.0
             AND base.replenish_ratio <= 2.0
             AND (
                 (base.`w.wall_side` = 'BID' AND resp.mae_ticks_10s > -8) OR
                 (base.`w.wall_side` = 'ASK' AND resp.mfe_ticks_10s < 8)
             )
        THEN 'REPLENISHING_HOLD'

        -- Pattern 5: CONSUMED_BREAK
        -- Wall consumed → clean breakout
        WHEN base.fill_ratio > 0.70
             AND abs(resp.mfe_ticks_10s) > 10  -- Strong move
             AND agg.peak_aggression_score > 0.50
        THEN 'CONSUMED_BREAK'

        -- Pattern 6: NO_EDGE
        -- Everything else
        ELSE 'NO_EDGE'
    END AS pattern_classification,

    -- ================================================================
    -- TRADE EXPECTANCY HINT
    -- ================================================================
    -- Quick filter: does this pattern show edge?

    CASE
        -- Positive expectancy patterns (MFE > MAE on average)
        WHEN pattern_classification IN ('ABSORB_REVERSE', 'ICEBERG_REJECT', 'REPLENISHING_HOLD')
             AND (
                 (base.`w.wall_side` = 'BID' AND resp.mfe_ticks_10s > abs(resp.mae_ticks_10s)) OR
                 (base.`w.wall_side` = 'ASK' AND abs(resp.mae_ticks_10s) > resp.mfe_ticks_10s)
             )
        THEN 'POSITIVE_EXPECTANCY'

        -- Breakout patterns
        WHEN pattern_classification IN ('PULL_THEN_BREAK', 'CONSUMED_BREAK')
             AND abs(resp.mfe_ticks_10s) > 12
        THEN 'BREAKOUT_EDGE'

        ELSE 'NO_CLEAR_EDGE'
    END AS expectancy_hint

FROM CG_mnq_wall_interactions base
INNER JOIN CG_mnq_wall_interactions_response_v1 resp
    ON base.wall_id = resp.wall_id
    AND base.interaction_time = resp.interaction_time
INNER JOIN CG_mnq_wall_interaction_aggression_v1 agg
    ON base.wall_id = agg.wall_id
    AND base.interaction_time = agg.interaction_time
ORDER BY interaction_time;

-- ============================================================================
-- VERIFICATION & ANALYSIS
-- ============================================================================

SELECT '=== ENRICHED TABLE COMPLETE ===' AS report FORMAT Pretty;

SELECT
    count() AS total_interactions,
    min(interaction_time) AS first,
    max(interaction_time) AS last,
    countDistinct(toDate(interaction_time)) AS days
FROM CG_mnq_wall_interactions_enriched_v1
FORMAT Pretty;

SELECT '=== Pattern Distribution ===' AS report FORMAT Pretty;

SELECT
    pattern_classification,
    count() AS interactions,
    round(count() * 100.0 / (SELECT count() FROM CG_mnq_wall_interactions_enriched_v1), 2) AS pct,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae,
    round(avg(peak_aggression_score), 3) AS avg_aggression
FROM CG_mnq_wall_interactions_enriched_v1
GROUP BY pattern_classification
ORDER BY interactions DESC
FORMAT Pretty;

SELECT '=== Expectancy Hint Distribution ===' AS report FORMAT Pretty;

SELECT
    expectancy_hint,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae,
    countIf(abs(mfe_ticks_10s) > 10) AS big_moves
FROM CG_mnq_wall_interactions_enriched_v1
GROUP BY expectancy_hint
ORDER BY interactions DESC
FORMAT Pretty;

SELECT '=== Top Patterns by MFE ===' AS report FORMAT Pretty;

SELECT
    pattern_classification,
    wall_side,
    count() AS cases,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(absorption_score), 2) AS avg_absorption,
    round(avg(wall_strength), 2) AS avg_wall_str
FROM CG_mnq_wall_interactions_enriched_v1
WHERE pattern_classification != 'NO_EDGE'
GROUP BY pattern_classification, wall_side
ORDER BY avg_mfe DESC
LIMIT 10
FORMAT Pretty;
