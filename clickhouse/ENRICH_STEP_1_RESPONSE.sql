-- ============================================================================
-- Enrichment Step 1: Forward Price Response
-- ============================================================================
-- Purpose: Add MFE/MAE metrics to wall interactions
-- Input: CG_mnq_wall_interactions (100K rows)
-- Output: CG_mnq_wall_interactions_response_v1
--
-- This answers: "What happened to price after touching the wall?"
--
-- Date: 2026-05-01
-- ============================================================================

-- First, create a price series for efficient lookups
DROP TABLE IF EXISTS CG_mnq_price_series_100ms;

CREATE TABLE CG_mnq_price_series_100ms
ENGINE = MergeTree
ORDER BY bucket_time
AS
SELECT
    bucket_time,
    avg(price) AS price
FROM CG_mnq_heatmap_100ms
WHERE bid_add_size > 0 OR ask_add_size > 0 OR bid_fill_size > 0 OR ask_fill_size > 0
GROUP BY bucket_time
ORDER BY bucket_time;

-- Verify price series
SELECT '=== Price Series Created ===' AS report FORMAT Pretty;
SELECT
    count() AS buckets,
    min(bucket_time) AS first_time,
    max(bucket_time) AS last_time,
    round(avg(price), 2) AS avg_price
FROM CG_mnq_price_series_100ms
FORMAT Pretty;

-- ============================================================================
-- Main Enrichment: Add Forward Response Metrics
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_interactions_response_v1;

CREATE TABLE CG_mnq_wall_interactions_response_v1
ENGINE = MergeTree
PARTITION BY toDate(interaction_time)
ORDER BY (interaction_time, wall_id)
AS
SELECT
    -- Original interaction columns
    i.*,

    -- Forward price levels (10s window)
    (SELECT max(price)
     FROM CG_mnq_price_series_100ms p
     WHERE p.bucket_time > i.interaction_time
       AND p.bucket_time <= i.interaction_time + INTERVAL 10 SECOND
    ) AS future_high_10s,

    (SELECT min(price)
     FROM CG_mnq_price_series_100ms p
     WHERE p.bucket_time > i.interaction_time
       AND p.bucket_time <= i.interaction_time + INTERVAL 10 SECOND
    ) AS future_low_10s,

    -- Forward price levels (30s window)
    (SELECT max(price)
     FROM CG_mnq_price_series_100ms p
     WHERE p.bucket_time > i.interaction_time
       AND p.bucket_time <= i.interaction_time + INTERVAL 30 SECOND
    ) AS future_high_30s,

    (SELECT min(price)
     FROM CG_mnq_price_series_100ms p
     WHERE p.bucket_time > i.interaction_time
       AND p.bucket_time <= i.interaction_time + INTERVAL 30 SECOND
    ) AS future_low_30s,

    -- MFE/MAE calculations (10s)
    round((future_high_10s - i.price_at_interaction) / 0.25) AS mfe_ticks_10s,
    round((future_low_10s - i.price_at_interaction) / 0.25) AS mae_ticks_10s,

    -- MFE/MAE calculations (30s)
    round((future_high_30s - i.price_at_interaction) / 0.25) AS mfe_ticks_30s,
    round((future_low_30s - i.price_at_interaction) / 0.25) AS mae_ticks_30s,

    -- Basic outcome classification
    CASE
        -- BID wall: price bounced up (support held)
        WHEN `w.wall_side` = 'BID'
             AND mfe_ticks_10s > 3
             AND mae_ticks_10s > -3
        THEN 'REJECT_SUPPORT'

        -- BID wall: price broke down (support failed)
        WHEN `w.wall_side` = 'BID'
             AND mae_ticks_10s < -5
        THEN 'BREAK_SUPPORT'

        -- ASK wall: price bounced down (resistance held)
        WHEN `w.wall_side` = 'ASK'
             AND mae_ticks_10s < -3
             AND mfe_ticks_10s < 3
        THEN 'REJECT_RESISTANCE'

        -- ASK wall: price broke up (resistance failed)
        WHEN `w.wall_side` = 'ASK'
             AND mfe_ticks_10s > 5
        THEN 'BREAK_RESISTANCE'

        -- Price didn't move much
        WHEN abs(mfe_ticks_10s) < 3 AND abs(mae_ticks_10s) < 3
        THEN 'CHOP_NO_MOVE'

        -- Default
        ELSE 'UNCLEAR'
    END AS outcome_basic

FROM CG_mnq_wall_interactions i
WHERE i.interaction_time < (SELECT max(bucket_time) - INTERVAL 30 SECOND FROM CG_mnq_price_series_100ms)
ORDER BY interaction_time;

-- ============================================================================
-- VERIFICATION
-- ============================================================================

SELECT '=== Response Enrichment Complete ===' AS report FORMAT Pretty;

SELECT
    count() AS total_interactions,
    countDistinct(wall_id) AS unique_walls,
    min(interaction_time) AS first_interaction,
    max(interaction_time) AS last_interaction
FROM CG_mnq_wall_interactions_response_v1
FORMAT Pretty;

SELECT '=== Outcome Distribution ===' AS report FORMAT Pretty;

SELECT
    outcome_basic,
    count() AS interactions,
    round(count() / (SELECT count() FROM CG_mnq_wall_interactions_response_v1) * 100, 2) AS pct,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe_10s,
    round(avg(mae_ticks_10s), 2) AS avg_mae_10s
FROM CG_mnq_wall_interactions_response_v1
GROUP BY outcome_basic
ORDER BY interactions DESC
FORMAT Pretty;

SELECT '=== Wall Side × Outcome ===' AS report FORMAT Pretty;

SELECT
    `w.wall_side` AS wall_side,
    outcome_basic,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae
FROM CG_mnq_wall_interactions_response_v1
GROUP BY wall_side, outcome_basic
ORDER BY wall_side, interactions DESC
FORMAT Pretty;

SELECT '=== Wall Behavior × Outcome (Top Patterns) ===' AS report FORMAT Pretty;

SELECT
    wall_behavior,
    outcome_basic,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae
FROM CG_mnq_wall_interactions_response_v1
WHERE wall_behavior != ''
GROUP BY wall_behavior, outcome_basic
ORDER BY interactions DESC
LIMIT 20
FORMAT Pretty;

SELECT '=== Sample: REPLENISHING walls that REJECTED ===' AS report FORMAT Pretty;

SELECT
    interaction_time,
    `w.wall_side` AS side,
    `w.wall_price` AS price,
    wall_size,
    round(replenish_ratio, 2) AS replenish,
    mfe_ticks_10s,
    mae_ticks_10s,
    outcome_basic
FROM CG_mnq_wall_interactions_response_v1
WHERE wall_behavior = 'REPLENISHING_WALL'
  AND outcome_basic IN ('REJECT_SUPPORT', 'REJECT_RESISTANCE')
ORDER BY interaction_time
LIMIT 10
FORMAT Pretty;

SELECT '=== Sample: PULLED walls that BROKE ===' AS report FORMAT Pretty;

SELECT
    interaction_time,
    `w.wall_side` AS side,
    `w.wall_price` AS price,
    wall_size,
    round(pull_ratio, 2) AS pull,
    mfe_ticks_10s,
    mae_ticks_10s,
    outcome_basic
FROM CG_mnq_wall_interactions_response_v1
WHERE wall_behavior = 'PULLED_WALL'
  AND outcome_basic IN ('BREAK_SUPPORT', 'BREAK_RESISTANCE')
ORDER BY interaction_time
LIMIT 10
FORMAT Pretty;

-- ============================================================================
-- EDGE DETECTION QUERIES
-- ============================================================================

SELECT '=== HYPOTHESIS 1: REPLENISHING walls hold (reject) ===' AS report FORMAT Pretty;

SELECT
    outcome_basic,
    count() AS cases,
    round(avg(replenish_ratio), 3) AS avg_replenish,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae,
    round(avg(mfe_ticks_30s), 2) AS avg_mfe_30s
FROM CG_mnq_wall_interactions_response_v1
WHERE wall_behavior = 'REPLENISHING_WALL'
  AND replenish_ratio > 1.5
GROUP BY outcome_basic
ORDER BY cases DESC
FORMAT Pretty;

SELECT '=== HYPOTHESIS 2: PULLED walls predict breaks ===' AS report FORMAT Pretty;

SELECT
    outcome_basic,
    count() AS cases,
    round(avg(pull_ratio), 3) AS avg_pull,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae,
    round(avg(mfe_ticks_30s), 2) AS avg_mfe_30s
FROM CG_mnq_wall_interactions_response_v1
WHERE wall_behavior = 'PULLED_WALL'
  AND pull_ratio > 0.70
GROUP BY outcome_basic
ORDER BY cases DESC
FORMAT Pretty;

SELECT '=== HYPOTHESIS 3: ICEBERG walls strongly reject ===' AS report FORMAT Pretty;

SELECT
    outcome_basic,
    count() AS cases,
    round(avg(replenish_ratio), 3) AS avg_replenish,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae
FROM CG_mnq_wall_interactions_response_v1
WHERE wall_behavior = 'ICEBERG_LIKE_WALL'
GROUP BY outcome_basic
ORDER BY cases DESC
FORMAT Pretty;

-- ============================================================================
-- NEXT STEP: Aggression metrics (Step 2)
-- ============================================================================
-- File: ENRICH_STEP_2_AGGRESSION.sql
-- ============================================================================
