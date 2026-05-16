-- ============================================================================
-- Phase 7: Wall Interactions - MINIMAL VERSION (No forward price calc)
-- ============================================================================
-- Purpose: Get core wall interactions without forward price response
-- We'll add forward price calculations later if needed
-- Date: 2026-05-01
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_interactions;

CREATE TABLE CG_mnq_wall_interactions
ENGINE = MergeTree
PARTITION BY toDate(interaction_time)
ORDER BY (interaction_time, wall_id)
AS
SELECT
    -- Generate wall_id
    concat(toString(w.wall_side), '_', toString(w.wall_price), '_', toString(w.bucket_time)) AS wall_id,

    -- Core identification
    w.bucket_time AS interaction_time,
    toDateTime(w.bucket_time, 'America/New_York') AS interaction_et,
    w.trade_date,

    -- Wall characteristics
    w.wall_side,
    w.wall_price,
    w.wall_size,
    w.wall_score,
    w.wall_rank,
    w.wall_type,

    -- Wall lifecycle (if available)
    wl.wall_behavior,
    wl.pull_ratio,
    wl.fill_ratio,
    wl.replenish_ratio,
    wl.duration_ms AS wall_lifetime_ms,

    -- Price context
    avg(h.price) AS price_at_interaction,
    round((w.wall_price - avg(h.price)) / 0.25) AS distance_to_wall_ticks

FROM CG_mnq_liquidity_walls_100ms w

-- Get approximate price at interaction time
LEFT JOIN CG_mnq_heatmap_100ms h
    ON h.bucket_time = w.bucket_time
    AND (h.bid_add_size > 0 OR h.ask_add_size > 0 OR h.bid_fill_size > 0 OR h.ask_fill_size > 0)

-- Link to wall lifecycle
LEFT JOIN CG_mnq_wall_lifecycle wl
    ON w.wall_price = wl.wall_price
    AND w.wall_side = wl.wall_side
    AND w.bucket_time BETWEEN wl.first_seen_time AND wl.last_seen_time

WHERE
    -- Only top-tier walls
    w.wall_rank IN ('P99.9', 'P99.5', 'P99')

GROUP BY
    w.wall_side, w.wall_price, w.bucket_time, w.trade_date,
    w.wall_size, w.wall_score, w.wall_rank, w.wall_type,
    wl.wall_behavior, wl.pull_ratio, wl.fill_ratio, wl.replenish_ratio, wl.duration_ms

HAVING
    -- Within 2 ticks of current price (computed after aggregation)
    abs(distance_to_wall_ticks) <= 2

ORDER BY interaction_time
LIMIT 100000;

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

SELECT '=== Wall Rank Distribution ===' AS report FORMAT Pretty;

SELECT
    wall_rank,
    count() AS interactions,
    round(avg(wall_size), 0) AS avg_size
FROM CG_mnq_wall_interactions
GROUP BY wall_rank
ORDER BY wall_rank
FORMAT Pretty;

SELECT '=== Wall Behavior Distribution ===' AS report FORMAT Pretty;

SELECT
    wall_behavior,
    count() AS interactions
FROM CG_mnq_wall_interactions
WHERE wall_behavior IS NOT NULL
GROUP BY wall_behavior
ORDER BY interactions DESC
FORMAT Pretty;
