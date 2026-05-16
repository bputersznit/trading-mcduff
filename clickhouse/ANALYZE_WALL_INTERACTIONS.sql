-- ============================================================================
-- Wall Interactions Analysis Queries
-- ============================================================================
-- Purpose: Explore CG_mnq_wall_interactions to identify tradeable patterns
-- Date: 2026-05-01
-- ============================================================================

-- Query 1: Overall interaction summary
SELECT '=== Interaction Overview ===' AS report FORMAT Pretty;

SELECT
    count() AS total_interactions,
    countDistinct(wall_id) AS unique_walls,
    countDistinct(toDate(interaction_time)) AS trading_days,
    min(interaction_time) AS first_interaction,
    max(interaction_time) AS last_interaction
FROM CG_mnq_wall_interactions
FORMAT Pretty;

-- Query 2: Wall rank distribution (which tier has most interactions)
SELECT '=== Wall Rank Distribution ===' AS report FORMAT Pretty;

SELECT
    `w.wall_rank` AS wall_rank,
    count() AS interactions,
    round(count() / (SELECT count() FROM CG_mnq_wall_interactions) * 100, 2) AS pct,
    round(avg(wall_size), 0) AS avg_size,
    round(avg(abs(distance_to_wall_ticks)), 1) AS avg_distance
FROM CG_mnq_wall_interactions
GROUP BY wall_rank
ORDER BY interactions DESC
FORMAT Pretty;

-- Query 3: Wall behavior distribution (replenishing vs pulled vs iceberg)
SELECT '=== Wall Behavior Distribution ===' AS report FORMAT Pretty;

SELECT
    wall_behavior,
    count() AS interactions,
    round(count() / (SELECT count() FROM CG_mnq_wall_interactions WHERE wall_behavior != '') * 100, 2) AS pct,
    round(avg(wall_size), 0) AS avg_size,
    round(avg(pull_ratio), 4) AS avg_pull,
    round(avg(fill_ratio), 4) AS avg_fill,
    round(avg(replenish_ratio), 4) AS avg_replenish
FROM CG_mnq_wall_interactions
WHERE wall_behavior != ''
GROUP BY wall_behavior
ORDER BY interactions DESC
FORMAT Pretty;

-- Query 4: Wall side distribution (bid support vs ask resistance)
SELECT '=== Wall Side Distribution ===' AS report FORMAT Pretty;

SELECT
    `w.wall_side` AS wall_side,
    count() AS interactions,
    round(avg(wall_size), 0) AS avg_size,
    countIf(wall_behavior = 'REPLENISHING_WALL') AS replenishing_count,
    countIf(wall_behavior = 'PULLED_WALL') AS pulled_count,
    countIf(wall_behavior = 'ICEBERG_LIKE_WALL') AS iceberg_count
FROM CG_mnq_wall_interactions
GROUP BY wall_side
ORDER BY interactions DESC
FORMAT Pretty;

-- Query 5: Distance distribution (how close was price to wall)
SELECT '=== Distance to Wall Distribution ===' AS report FORMAT Pretty;

SELECT
    distance_to_wall_ticks AS distance,
    count() AS interactions,
    round(count() / (SELECT count() FROM CG_mnq_wall_interactions) * 100, 2) AS pct
FROM CG_mnq_wall_interactions
GROUP BY distance
ORDER BY distance
FORMAT Pretty;

-- Query 6: REPLENISHING walls (potential iceberg fade opportunities)
SELECT '=== REPLENISHING Wall Characteristics ===' AS report FORMAT Pretty;

SELECT
    `w.wall_side` AS wall_side,
    count() AS interactions,
    round(avg(wall_size), 0) AS avg_size,
    round(avg(pull_ratio), 4) AS avg_pull,
    round(avg(fill_ratio), 4) AS avg_fill,
    round(avg(replenish_ratio), 4) AS avg_replenish,
    round(avg(wall_lifetime_ms) / 1000, 1) AS avg_lifetime_sec
FROM CG_mnq_wall_interactions
WHERE wall_behavior = 'REPLENISHING_WALL'
GROUP BY wall_side
FORMAT Pretty;

-- Query 7: PULLED walls (potential spoof/breakout opportunities)
SELECT '=== PULLED Wall Characteristics ===' AS report FORMAT Pretty;

SELECT
    `w.wall_side` AS wall_side,
    count() AS interactions,
    round(avg(wall_size), 0) AS avg_size,
    round(avg(pull_ratio), 4) AS avg_pull,
    round(avg(fill_ratio), 4) AS avg_fill,
    round(avg(wall_lifetime_ms) / 1000, 1) AS avg_lifetime_sec
FROM CG_mnq_wall_interactions
WHERE wall_behavior = 'PULLED_WALL'
GROUP BY wall_side
FORMAT Pretty;

-- Query 8: ICEBERG-LIKE walls (very strong replenishment)
SELECT '=== ICEBERG-LIKE Wall Characteristics ===' AS report FORMAT Pretty;

SELECT
    `w.wall_side` AS wall_side,
    count() AS interactions,
    round(avg(wall_size), 0) AS avg_size,
    round(avg(replenish_ratio), 4) AS avg_replenish,
    round(avg(wall_lifetime_ms) / 1000, 1) AS avg_lifetime_sec
FROM CG_mnq_wall_interactions
WHERE wall_behavior = 'ICEBERG_LIKE_WALL'
GROUP BY wall_side
FORMAT Pretty;

-- Query 9: Hourly distribution (when do wall interactions happen)
SELECT '=== Hourly Distribution (ET) ===' AS report FORMAT Pretty;

SELECT
    toHour(interaction_et) AS hour_et,
    count() AS interactions,
    countIf(wall_behavior = 'REPLENISHING_WALL') AS replenishing,
    countIf(wall_behavior = 'PULLED_WALL') AS pulled,
    countIf(wall_behavior = 'ICEBERG_LIKE_WALL') AS iceberg
FROM CG_mnq_wall_interactions
GROUP BY hour_et
ORDER BY hour_et
FORMAT Pretty;

-- Query 10: Sample of interactions with lifecycle data
SELECT '=== Sample Interactions with Lifecycle Data ===' AS report FORMAT Pretty;

SELECT
    interaction_time,
    `w.wall_side` AS side,
    `w.wall_price` AS price,
    wall_size,
    distance_to_wall_ticks AS dist,
    wall_behavior,
    round(pull_ratio, 3) AS pull,
    round(fill_ratio, 3) AS fill,
    round(replenish_ratio, 3) AS replenish
FROM CG_mnq_wall_interactions
WHERE wall_behavior != ''
ORDER BY interaction_time
LIMIT 20
FORMAT Pretty;

-- ============================================================================
-- NEXT STEPS FOR EDGE DETECTION
-- ============================================================================
--
-- Based on the results above, look for:
--
-- 1. REPLENISHING walls with high replenish_ratio (> 2.0)
--    → Hypothesis: Strong icebergs act as support/resistance
--    → Tradeable: Fade into wall, exit quickly if broken
--
-- 2. PULLED walls with high pull_ratio (> 0.70)
--    → Hypothesis: Wall pulled = smart money exiting
--    → Tradeable: Breakout continuation after pull
--
-- 3. ICEBERG-LIKE walls (replenish_ratio > 3.0)
--    → Hypothesis: Institutional accumulation/distribution
--    → Tradeable: Scalp fade with tight stop
--
-- 4. Time-of-day patterns
--    → Do certain wall behaviors cluster at certain hours?
--    → RTH open/close vs overnight session differences
--
-- To validate edges, we need to add:
-- - Forward price response (MFE/MAE at 10s/30s)
-- - Aggression metrics (was wall absorbed or broken?)
-- - Outcome classification (did price reverse or continue?)
--
-- These can be computed in separate queries or added to the table via ALTER.
-- ============================================================================
