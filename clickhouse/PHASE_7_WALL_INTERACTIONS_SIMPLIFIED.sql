-- ============================================================================
-- Phase 7: Wall Interactions - SIMPLIFIED VERSION (Performance Optimized)
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
-- Simplified approach: Pre-aggregate forward price moves instead of window functions
--
-- Date: 2026-05-01
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_interactions;

CREATE TABLE CG_mnq_wall_interactions
ENGINE = MergeTree
PARTITION BY toDate(interaction_time)
ORDER BY (interaction_time, wall_id)
AS
WITH price_at_bucket AS (
    -- Get best approximation of price at each 100ms bucket
    SELECT
        bucket_time,
        avg(price) AS approx_price
    FROM CG_mnq_heatmap_100ms
    WHERE bid_add_size > 0 OR ask_add_size > 0 OR bid_fill_size > 0 OR ask_fill_size > 0
    GROUP BY bucket_time
),

wall_proximity AS (
    -- Step 1: Identify when price comes within 2 ticks of a P99+ wall
    SELECT
        concat(toString(w.wall_side), '_', toString(w.wall_price), '_', toString(w.bucket_time)) AS wall_id,
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

    WHERE abs(round((w.wall_price - p.approx_price) / 0.25)) <= 2  -- Within 2 ticks
      AND w.wall_rank IN ('P99.9', 'P99.5', 'P99')  -- Only top-tier walls
    LIMIT 100000  -- Safety limit: keep top 100K interactions
),

interaction_aggression_base AS (
    -- Step 2a: Aggregate all aggression metrics first
    SELECT
        wall_proximity.wall_id,
        wall_proximity.interaction_time,
        wall_proximity.trade_date,
        wall_proximity.wall_side,
        wall_proximity.wall_price,
        wall_proximity.wall_size,
        wall_proximity.wall_score,
        wall_proximity.wall_rank,
        wall_proximity.wall_type,
        wall_proximity.price_at_interaction,
        wall_proximity.distance_to_wall_ticks,
        wall_proximity.wall_behavior,
        wall_proximity.pull_ratio,
        wall_proximity.fill_ratio,
        wall_proximity.replenish_ratio,
        wall_proximity.wall_lifetime_ms,

        -- Aggression metrics (5-second window)
        sum(CG_mnq_aggression_100ms.buy_volume) AS buy_volume_5s,
        sum(CG_mnq_aggression_100ms.sell_volume) AS sell_volume_5s,
        sum(CG_mnq_aggression_100ms.delta) AS delta_5s,
        sum(CG_mnq_aggression_100ms.abs_delta) AS abs_delta_5s,
        sum(CG_mnq_aggression_100ms.total_volume) AS total_volume_5s,
        max(CG_mnq_aggression_100ms.aggression_score) AS peak_aggression_score

    FROM wall_proximity
    LEFT JOIN CG_mnq_aggression_100ms
        ON dateDiff('millisecond', wall_proximity.interaction_time, CG_mnq_aggression_100ms.bucket_time) BETWEEN -2500 AND 2500
    GROUP BY
        wall_proximity.wall_id, wall_proximity.interaction_time, wall_proximity.trade_date, wall_proximity.wall_side, wall_proximity.wall_price,
        wall_proximity.wall_size, wall_proximity.wall_score, wall_proximity.wall_rank, wall_proximity.wall_type, wall_proximity.price_at_interaction,
        wall_proximity.distance_to_wall_ticks, wall_proximity.wall_behavior, wall_proximity.pull_ratio, wall_proximity.fill_ratio,
        wall_proximity.replenish_ratio, wall_proximity.wall_lifetime_ms
),

interaction_aggression AS (
    -- Step 2b: Calculate directional aggression
    SELECT
        *,
        -- Aggression into wall
        multiIf(
            wall_side = 'BID', sell_volume_5s,
            wall_side = 'ASK', buy_volume_5s,
            0
        ) AS aggression_into_wall,

        -- Aggression away from wall
        multiIf(
            wall_side = 'BID', buy_volume_5s,
            wall_side = 'ASK', sell_volume_5s,
            0
        ) AS aggression_away_from_wall
    FROM interaction_aggression_base
),

forward_price_response AS (
    -- Step 3: Calculate forward price response using subqueries (not window functions)
    SELECT
        ia.*,

        -- 10s forward max/min price
        (SELECT max(approx_price) FROM price_at_bucket p
         WHERE dateDiff('millisecond', ia.interaction_time, p.bucket_time) BETWEEN 0 AND 10000
        ) AS max_price_10s,

        (SELECT min(approx_price) FROM price_at_bucket p
         WHERE dateDiff('millisecond', ia.interaction_time, p.bucket_time) BETWEEN 0 AND 10000
        ) AS min_price_10s,

        -- 30s forward max/min price
        (SELECT max(approx_price) FROM price_at_bucket p
         WHERE dateDiff('millisecond', ia.interaction_time, p.bucket_time) BETWEEN 0 AND 30000
        ) AS max_price_30s,

        (SELECT min(approx_price) FROM price_at_bucket p
         WHERE dateDiff('millisecond', ia.interaction_time, p.bucket_time) BETWEEN 0 AND 30000
        ) AS min_price_30s

    FROM interaction_aggression ia
)

SELECT
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
             AND mae_ticks_10s < -5
             AND aggression_into_wall > 50
        THEN 'BREAK_CONTINUE'

        WHEN wall_side = 'ASK'
             AND mfe_ticks_10s > 5
             AND aggression_into_wall > 50
        THEN 'BREAK_CONTINUE'

        -- PULL_THEN_BREAK: Wall pulled, then price moves
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

        ELSE 'NO_EDGE_CHOP'
    END AS outcome_label,

    -- Aggression classification
    CASE
        WHEN peak_aggression_score >= 0.80 THEN 'HIGH'
        WHEN peak_aggression_score >= 0.50 THEN 'MODERATE'
        ELSE 'LOW'
    END AS aggression_classification

FROM forward_price_response
WHERE total_volume_5s > 0
ORDER BY interaction_time;

-- ============================================================================
-- VERIFICATION
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
    round(avg(mae_ticks_10s), 2) AS avg_mae_10s
FROM CG_mnq_wall_interactions
GROUP BY outcome_label
ORDER BY interactions DESC
FORMAT Pretty;

SELECT '=== Wall Behavior × Outcome ===' AS report FORMAT Pretty;

SELECT
    wall_behavior,
    outcome_label,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe
FROM CG_mnq_wall_interactions
WHERE wall_behavior IS NOT NULL
GROUP BY wall_behavior, outcome_label
ORDER BY interactions DESC
LIMIT 20
FORMAT Pretty;
