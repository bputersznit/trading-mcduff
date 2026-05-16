-- ============================================================================
-- Phase 7: Wall Interactions - PRIMARY RESEARCH TABLE
-- ============================================================================
-- Purpose: Track what happens when price approaches significant liquidity walls
-- Source: CG_mnq_liquidity_walls_100ms (wall detections)
--         CG_mnq_wall_lifecycle (wall behavior)
--         CG_mnq_aggression_100ms (buy/sell pressure)
--         CG_mnq_heatmap_100ms (price action)
-- Output: CG_mnq_wall_interactions
--
-- THIS IS THE CENTRAL TABLE - All analysis flows from here.
--
-- Research Question:
--   "When price interacts with resting liquidity, how does it react under
--    different aggression, absorption, pulling, and replenishment conditions?"
--
-- Methodology:
--   1. Detect wall (P99+ liquidity level)
--   2. Track price approach (within 2 ticks)
--   3. Measure aggression during interaction
--   4. Observe price response (10s and 30s forward)
--   5. Classify outcome (REJECT, BREAK, ABSORB, FADE, SPOOF)
--
-- Date: 2026-05-03
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_interactions;

CREATE TABLE CG_mnq_wall_interactions
ENGINE = MergeTree
PARTITION BY toDate(interaction_time)
ORDER BY (interaction_time, wall_id)
AS
WITH price_at_bucket AS (
    -- Get best approximation of price at each 100ms bucket
    -- Use mid of highest bid activity and lowest ask activity
    SELECT
        bucket_time,
        avg(price) AS approx_price
    FROM CG_mnq_heatmap_100ms
    WHERE bid_add_size > 0 OR ask_add_size > 0 OR bid_fill_size > 0 OR ask_fill_size > 0
    GROUP BY bucket_time
),

wall_proximity AS (
    -- Step 1: Identify when price comes within 2 ticks of a wall
    SELECT
        w.wall_id,
        w.bucket_time AS interaction_time,
        w.trade_date,
        w.wall_side,
        w.wall_price,
        w.wall_size,
        w.wall_score,
        w.wall_rank,
        w.wall_type,

        -- Price proximity
        p.approx_price AS price_at_interaction,
        round((w.wall_price - p.approx_price) / 0.25) AS distance_to_wall_ticks,

        -- Link to wall lifecycle
        wl.wall_behavior,
        wl.pull_ratio,
        wl.fill_ratio,
        wl.replenish_ratio,
        wl.duration_ms AS wall_lifetime_ms

    FROM CG_mnq_liquidity_walls_100ms w
    INNER JOIN price_at_bucket p
        ON w.bucket_time = p.bucket_time
    LEFT JOIN CG_mnq_wall_lifecycle wl
        ON w.wall_price = wl.wall_price
        AND w.wall_side = wl.wall_side
        AND w.bucket_time BETWEEN wl.first_seen_time AND wl.last_seen_time

    WHERE abs(distance_to_wall_ticks) <= 2  -- Within 2 ticks of wall
      AND w.wall_rank IN ('P99.9', 'P99.5', 'P99')  -- Only top-tier walls
),

interaction_aggression AS (
    -- Step 2: Measure aggression during the interaction window
    -- Use a 5-second window centered on interaction time
    SELECT
        wp.wall_id,
        wp.interaction_time,
        wp.trade_date,
        wp.wall_side,
        wp.wall_price,
        wp.wall_size,
        wp.wall_score,
        wp.wall_rank,
        wp.wall_type,
        wp.price_at_interaction,
        wp.distance_to_wall_ticks,
        wp.wall_behavior,
        wp.pull_ratio,
        wp.fill_ratio,
        wp.replenish_ratio,
        wp.wall_lifetime_ms,

        -- Aggression metrics (5-second window around interaction)
        sum(agg.buy_volume) AS buy_volume_5s,
        sum(agg.sell_volume) AS sell_volume_5s,
        sum(agg.delta) AS delta_5s,
        sum(agg.abs_delta) AS abs_delta_5s,
        sum(agg.total_volume) AS total_volume_5s,
        max(agg.aggression_score) AS peak_aggression_score,

        -- Aggression into wall (directional)
        CASE
            WHEN wp.wall_side = 'BID' THEN sum(agg.sell_volume)  -- Selling into bid wall
            WHEN wp.wall_side = 'ASK' THEN sum(agg.buy_volume)   -- Buying into ask wall
            ELSE 0
        END AS aggression_into_wall,

        -- Aggression away from wall (opposite direction)
        CASE
            WHEN wp.wall_side = 'BID' THEN sum(agg.buy_volume)
            WHEN wp.wall_side = 'ASK' THEN sum(agg.sell_volume)
            ELSE 0
        END AS aggression_away_from_wall

    FROM wall_proximity wp
    LEFT JOIN CG_mnq_aggression_100ms agg
        ON agg.bucket_time BETWEEN wp.interaction_time - INTERVAL 2500 MILLISECOND
                                AND wp.interaction_time + INTERVAL 2500 MILLISECOND
    GROUP BY
        wp.wall_id, wp.interaction_time, wp.trade_date, wp.wall_side, wp.wall_price,
        wp.wall_size, wp.wall_score, wp.wall_rank, wp.wall_type, wp.price_at_interaction,
        wp.distance_to_wall_ticks, wp.wall_behavior, wp.pull_ratio, wp.fill_ratio,
        wp.replenish_ratio, wp.wall_lifetime_ms
),

forward_price_moves AS (
    -- Step 3: Measure price response 10s and 30s forward
    SELECT
        ia.*,

        -- 10-second forward move
        first_value(p.approx_price) OVER (
            PARTITION BY ia.wall_id
            ORDER BY p.bucket_time
            ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING
        ) AS price_10s_later,

        -- Calculate MFE/MAE over next 10 seconds
        -- (Simplified: use max/min price in next 10s)
        max(p.approx_price) OVER (
            PARTITION BY ia.wall_id
            ORDER BY p.bucket_time
            ROWS BETWEEN CURRENT ROW AND 100 FOLLOWING  -- ~10 seconds
        ) AS max_price_10s,

        min(p.approx_price) OVER (
            PARTITION BY ia.wall_id
            ORDER BY p.bucket_time
            ROWS BETWEEN CURRENT ROW AND 100 FOLLOWING
        ) AS min_price_10s,

        -- 30-second forward move
        max(p.approx_price) OVER (
            PARTITION BY ia.wall_id
            ORDER BY p.bucket_time
            ROWS BETWEEN CURRENT ROW AND 300 FOLLOWING  -- ~30 seconds
        ) AS max_price_30s,

        min(p.approx_price) OVER (
            PARTITION BY ia.wall_id
            ORDER BY p.bucket_time
            ROWS BETWEEN CURRENT ROW AND 300 FOLLOWING
        ) AS min_price_30s

    FROM interaction_aggression ia
    LEFT JOIN price_at_bucket p
        ON p.bucket_time >= ia.interaction_time
        AND p.bucket_time <= ia.interaction_time + INTERVAL 30 SECOND
)

SELECT
    -- Generate unique interaction_id
    row_number() OVER (ORDER BY interaction_time) AS interaction_id,

    -- Core identification
    wall_id,
    interaction_time,
    toDateTime(interaction_time, 'America/New_York') AS interaction_et,
    trade_date,

    -- Wall characteristics
    wall_side,
    wall_price,
    wall_size,
    wall_score,
    wall_rank,
    wall_type,
    wall_behavior,
    pull_ratio,
    fill_ratio,
    replenish_ratio,
    wall_lifetime_ms,

    -- Interaction context
    price_at_interaction,
    distance_to_wall_ticks,

    -- Aggression metrics
    buy_volume_5s,
    sell_volume_5s,
    delta_5s,
    abs_delta_5s,
    total_volume_5s,
    peak_aggression_score,
    aggression_into_wall,
    aggression_away_from_wall,

    -- Price response (10s)
    round((max_price_10s - price_at_interaction) / 0.25) AS mfe_ticks_10s,
    round((min_price_10s - price_at_interaction) / 0.25) AS mae_ticks_10s,

    -- Price response (30s)
    round((max_price_30s - price_at_interaction) / 0.25) AS mfe_ticks_30s,
    round((min_price_30s - price_at_interaction) / 0.25) AS mae_ticks_30s,

    -- Outcome classification
    CASE
        -- ABSORB: High aggression into wall but price doesn't move
        WHEN aggression_into_wall > 100
             AND abs(mfe_ticks_10s) < 3
             AND abs(mae_ticks_10s) < 3
        THEN 'ABSORB_REVERSE'

        -- BREAK: Wall breaks, price continues through
        WHEN wall_side = 'BID'
             AND mae_ticks_10s < -5  -- Price broke below bid wall
             AND aggression_into_wall > 50
        THEN 'BREAK_CONTINUE'

        WHEN wall_side = 'ASK'
             AND mfe_ticks_10s > 5   -- Price broke above ask wall
             AND aggression_into_wall > 50
        THEN 'BREAK_CONTINUE'

        -- PULL_THEN_BREAK: Wall pulled (from lifecycle), then price moves
        WHEN pull_ratio > 0.70
             AND abs(mfe_ticks_10s) > 5
        THEN 'PULL_THEN_BREAK'

        -- ICEBERG_REJECT: Replenishing wall holds price
        WHEN wall_behavior = 'REPLENISHING_WALL'
             AND abs(mfe_ticks_10s) < 3
        THEN 'ICEBERG_REJECT'

        -- EXHAUSTION_FADE: Low aggression, price drifts away
        WHEN total_volume_5s < 50
             AND abs(distance_to_wall_ticks) > 3
        THEN 'EXHAUSTION_FADE'

        -- Default
        ELSE 'NO_EDGE_CHOP'
    END AS outcome_label,

    -- Aggression score classification
    CASE
        WHEN peak_aggression_score >= 0.80 THEN 'HIGH'
        WHEN peak_aggression_score >= 0.50 THEN 'MODERATE'
        ELSE 'LOW'
    END AS aggression_classification

FROM forward_price_moves
WHERE total_volume_5s > 0  -- Filter out dead interactions
ORDER BY interaction_time;

-- ============================================================================
-- VERIFICATION & ANALYSIS
-- ============================================================================

SELECT '=== CG_mnq_wall_interactions Summary ===' AS report FORMAT Pretty;

SELECT
    count() AS total_interactions,
    countDistinct(wall_id) AS unique_walls,
    countDistinct(trade_date) AS trading_days,
    min(interaction_time) AS first_interaction,
    max(interaction_time) AS last_interaction
FROM CG_mnq_wall_interactions
FORMAT Pretty;

SELECT '=== Outcome Distribution ===' AS report FORMAT Pretty;

SELECT
    outcome_label,
    count() AS interactions,
    round(count() / (SELECT count() FROM CG_mnq_wall_interactions) * 100, 2) AS pct,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe_10s,
    round(avg(mae_ticks_10s), 2) AS avg_mae_10s,
    round(avg(total_volume_5s), 0) AS avg_volume
FROM CG_mnq_wall_interactions
GROUP BY outcome_label
ORDER BY interactions DESC
FORMAT Pretty;

SELECT '=== Wall Type × Outcome Matrix ===' AS report FORMAT Pretty;

SELECT
    wall_type,
    outcome_label,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe_10s,
    round(avg(aggression_into_wall), 0) AS avg_agg_into
FROM CG_mnq_wall_interactions
GROUP BY wall_type, outcome_label
ORDER BY interactions DESC
LIMIT 30
FORMAT Pretty;

SELECT '=== Aggression × Outcome ===' AS report FORMAT Pretty;

SELECT
    aggression_classification,
    outcome_label,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe_10s,
    round(avg(mae_ticks_10s), 2) AS avg_mae_10s
FROM CG_mnq_wall_interactions
GROUP BY aggression_classification, outcome_label
ORDER BY interactions DESC
FORMAT Pretty;

SELECT '=== Wall Behavior × Outcome ===' AS report FORMAT Pretty;

SELECT
    wall_behavior,
    outcome_label,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(pull_ratio), 4) AS avg_pull,
    round(avg(fill_ratio), 4) AS avg_fill,
    round(avg(replenish_ratio), 4) AS avg_replenish
FROM CG_mnq_wall_interactions
WHERE wall_behavior IS NOT NULL
GROUP BY wall_behavior, outcome_label
ORDER BY interactions DESC
FORMAT Pretty;

-- ============================================================================
-- KEY RESEARCH QUESTIONS ANSWERED BY THIS TABLE
-- ============================================================================
-- 1. Do P99 walls act as support/resistance?
--    → Query: outcome_label = 'ICEBERG_REJECT' vs 'BREAK_CONTINUE'
--
-- 2. Does pulling predict breakouts?
--    → Query: WHERE pull_ratio > 0.70, check mfe_ticks_10s distribution
--
-- 3. Does absorption lead to reversals?
--    → Query: WHERE outcome_label = 'ABSORB_REVERSE', measure success rate
--
-- 4. Are icebergs tradeable fade opportunities?
--    → Query: WHERE wall_behavior = 'REPLENISHING_WALL', check rejection rate
--
-- 5. What aggression level breaks walls?
--    → Query: Compare aggression_into_wall for BREAK vs REJECT outcomes
--
-- ============================================================================
-- NEXT STEPS
-- ============================================================================
-- Phase 8: Statistical Analysis (group by wall type × aggression × outcome)
-- Phase 9: Strategy Families (ABSORB_REVERSE, PULL_BREAK, ICEBERG_REJECT, etc.)
-- Phase 10: Backtest Engine (one-position sequential walk with OCO)
-- ============================================================================
