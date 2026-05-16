-- ============================================================================
-- Phase 3: Wall Detection - P99 Liquidity Levels
-- ============================================================================
-- Purpose: Detect significant liquidity walls using percentile-based scoring
-- Source: CG_mnq_heatmap_100ms (liquidity snapshots)
-- Output: CG_mnq_liquidity_walls_100ms
--
-- This table answers: "Where are the significant resting orders?"
--
-- Methodology:
--   - Calculate liquidity percentiles per 100ms bucket
--   - Identify P90, P95, P99, P99.5, P99.9 walls
--   - Measure distance from mid-price
--   - Classify wall types (support/resistance/thin/thick)
--   - Track nearby liquidity context
--
-- Date: 2026-05-03
-- ============================================================================

-- First, create a helper view for mid-price calculation
DROP VIEW IF EXISTS CG_mnq_mid_price_100ms;
CREATE VIEW CG_mnq_mid_price_100ms AS
WITH bid_ask_extremes AS (
    SELECT
        bucket_time,
        max(price) AS best_bid_approx,  -- Highest price with bid activity
        min(price) AS best_ask_approx   -- Lowest price with ask activity
    FROM CG_mnq_heatmap_100ms
    WHERE bid_add_size > 0 OR ask_add_size > 0
    GROUP BY bucket_time
)
SELECT
    bucket_time,
    (best_bid_approx + best_ask_approx) / 2.0 AS mid_price
FROM bid_ask_extremes;

-- ============================================================================
-- Main Wall Detection Table
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_liquidity_walls_100ms;

CREATE TABLE CG_mnq_liquidity_walls_100ms
ENGINE = MergeTree
PARTITION BY toDate(bucket_time)
ORDER BY (bucket_time, wall_price)
AS
WITH liquidity_context AS (
    SELECT
        h.bucket_time,
        h.trade_date,
        h.price,
        h.price_tick,

        -- Liquidity metrics
        h.bid_resting_delta,
        h.ask_resting_delta,
        greatest(h.bid_resting_delta, h.ask_resting_delta) AS max_resting_delta,

        -- Side classification
        CASE
            WHEN h.bid_resting_delta > h.ask_resting_delta THEN 'BID'
            WHEN h.ask_resting_delta > h.bid_resting_delta THEN 'ASK'
            ELSE 'NEUTRAL'
        END AS wall_side,

        -- Size for scoring
        CASE
            WHEN h.bid_resting_delta > h.ask_resting_delta THEN h.bid_resting_delta
            ELSE h.ask_resting_delta
        END AS wall_size,

        -- Mid price for distance calculation
        m.mid_price,
        round((h.price - m.mid_price) / 0.25) AS distance_from_mid_ticks

    FROM CG_mnq_heatmap_100ms h
    LEFT JOIN CG_mnq_mid_price_100ms m ON h.bucket_time = m.bucket_time
    WHERE h.bid_resting_delta > 0 OR h.ask_resting_delta > 0  -- Only levels with net adds
),

percentile_ranks AS (
    SELECT
        *,
        -- Calculate percentile rank within each time bucket
        percent_rank() OVER (PARTITION BY bucket_time ORDER BY wall_size) AS wall_percentile
    FROM liquidity_context
)

SELECT
    bucket_time,
    toDateTime(bucket_time, 'America/New_York') AS bucket_et,
    trade_date,

    -- Wall location
    wall_side,
    price AS wall_price,
    price_tick AS wall_price_tick,
    distance_from_mid_ticks,

    -- Wall size and scoring
    wall_size,
    wall_percentile,

    -- Wall tier classification
    CASE
        WHEN wall_percentile >= 0.999 THEN 'P99.9'
        WHEN wall_percentile >= 0.995 THEN 'P99.5'
        WHEN wall_percentile >= 0.990 THEN 'P99'
        WHEN wall_percentile >= 0.950 THEN 'P95'
        WHEN wall_percentile >= 0.900 THEN 'P90'
        ELSE 'BELOW_P90'
    END AS wall_rank,

    -- Wall score (normalized 0-1, with P99+ getting highest scores)
    CASE
        WHEN wall_percentile >= 0.999 THEN 1.00
        WHEN wall_percentile >= 0.995 THEN 0.95
        WHEN wall_percentile >= 0.990 THEN 0.90
        WHEN wall_percentile >= 0.950 THEN 0.80
        WHEN wall_percentile >= 0.900 THEN 0.70
        ELSE wall_percentile
    END AS wall_score,

    -- Context
    mid_price,

    -- Type classification (support vs resistance)
    CASE
        WHEN wall_side = 'BID' AND distance_from_mid_ticks < 0 THEN 'BID_WALL_SUPPORT'
        WHEN wall_side = 'BID' AND distance_from_mid_ticks >= 0 THEN 'BID_WALL_ABOVE'
        WHEN wall_side = 'ASK' AND distance_from_mid_ticks > 0 THEN 'ASK_WALL_RESISTANCE'
        WHEN wall_side = 'ASK' AND distance_from_mid_ticks <= 0 THEN 'ASK_WALL_BELOW'
        ELSE 'NEUTRAL'
    END AS wall_type

FROM percentile_ranks
WHERE wall_percentile >= 0.90  -- Only keep P90+ walls (reduces noise)
ORDER BY bucket_time, wall_score DESC, price;

-- ============================================================================
-- VERIFICATION
-- ============================================================================

SELECT '=== CG_mnq_liquidity_walls_100ms Summary ===' AS report FORMAT Pretty;

SELECT
    count() AS total_walls,
    countDistinct(bucket_time) AS unique_buckets,
    countDistinct(wall_price) AS unique_price_levels,
    min(bucket_time) AS first_wall,
    max(bucket_time) AS last_wall
FROM CG_mnq_liquidity_walls_100ms
FORMAT Pretty;

SELECT '=== Wall Rank Distribution ===' AS report FORMAT Pretty;

SELECT
    wall_rank,
    count() AS walls,
    round(count() / (SELECT count() FROM CG_mnq_liquidity_walls_100ms) * 100, 2) AS pct,
    round(avg(wall_size), 0) AS avg_size,
    round(min(wall_score), 4) AS min_score,
    round(max(wall_score), 4) AS max_score
FROM CG_mnq_liquidity_walls_100ms
GROUP BY wall_rank
ORDER BY wall_rank
FORMAT Pretty;

SELECT '=== Wall Type Distribution ===' AS report FORMAT Pretty;

SELECT
    wall_type,
    count() AS walls,
    round(count() / (SELECT count() FROM CG_mnq_liquidity_walls_100ms) * 100, 2) AS pct,
    round(avg(wall_size), 0) AS avg_size,
    round(avg(abs(distance_from_mid_ticks)), 1) AS avg_distance_ticks
FROM CG_mnq_liquidity_walls_100ms
GROUP BY wall_type
ORDER BY walls DESC
FORMAT Pretty;

SELECT '=== Top 20 Largest Walls ===' AS report FORMAT Pretty;

SELECT
    bucket_time,
    wall_side,
    wall_price,
    wall_size,
    wall_rank,
    wall_score,
    distance_from_mid_ticks,
    wall_type
FROM CG_mnq_liquidity_walls_100ms
ORDER BY wall_size DESC
LIMIT 20
FORMAT Pretty;

SELECT '=== Daily Wall Counts ===' AS report FORMAT Pretty;

SELECT
    trade_date,
    count() AS total_walls,
    countIf(wall_rank = 'P99.9') AS p999_walls,
    countIf(wall_rank = 'P99.5') AS p995_walls,
    countIf(wall_rank = 'P99') AS p99_walls,
    countIf(wall_rank = 'P95') AS p95_walls,
    countIf(wall_rank = 'P90') AS p90_walls,
    round(avg(wall_size), 0) AS avg_wall_size
FROM CG_mnq_liquidity_walls_100ms
GROUP BY trade_date
ORDER BY trade_date
FORMAT Pretty;

-- ============================================================================
-- NEXT STEP: Phase 4 - Wall Lifecycle (tracking behavior over time)
-- ============================================================================
