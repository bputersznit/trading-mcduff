-- ============================================================================
-- Enrichment Step 2: Aggression Metrics
-- ============================================================================
-- Purpose: Add buy/sell pressure metrics around wall interactions
-- Input: CG_mnq_wall_interactions (100K rows)
--        CG_mnq_aggression_100ms (5.62M rows)
-- Output: CG_mnq_wall_interaction_aggression_v1
--
-- This answers: "Was there buying/selling pressure into the wall?"
--
-- Separate table approach avoids ClickHouse CTE scope issues.
--
-- Date: 2026-05-01
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_interaction_aggression_v1;

CREATE TABLE CG_mnq_wall_interaction_aggression_v1
ENGINE = MergeTree
PARTITION BY toDate(interaction_time)
ORDER BY (interaction_time, wall_id)
AS
SELECT
    i.wall_id,
    i.interaction_time,

    -- Aggression in the 5s BEFORE interaction (-5s to 0s)
    sum(if(dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -5000 AND 0,
           agg.buy_volume, 0)) AS buy_volume_before_5s,
    sum(if(dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -5000 AND 0,
           agg.sell_volume, 0)) AS sell_volume_before_5s,
    sum(if(dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -5000 AND 0,
           agg.delta, 0)) AS delta_before_5s,

    -- Aggression in the 5s AFTER interaction (0s to +5s)
    sum(if(dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN 0 AND 5000,
           agg.buy_volume, 0)) AS buy_volume_after_5s,
    sum(if(dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN 0 AND 5000,
           agg.sell_volume, 0)) AS sell_volume_after_5s,
    sum(if(dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN 0 AND 5000,
           agg.delta, 0)) AS delta_after_5s,

    -- Total window (-2.5s to +2.5s for "at wall" measurement)
    sum(if(dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500,
           agg.buy_volume, 0)) AS buy_volume_at_wall_5s,
    sum(if(dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500,
           agg.sell_volume, 0)) AS sell_volume_at_wall_5s,
    sum(if(dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500,
           agg.delta, 0)) AS delta_at_wall_5s,
    sum(if(dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500,
           agg.total_volume, 0)) AS total_volume_at_wall_5s,

    -- Peak aggression score in window
    max(if(dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500,
           agg.aggression_score, 0)) AS peak_aggression_score,

    -- Directional aggression INTO wall (depends on wall side)
    multiIf(
        i.`w.wall_side` = 'BID',
        sell_volume_at_wall_5s,  -- Selling into bid wall
        i.`w.wall_side` = 'ASK',
        buy_volume_at_wall_5s,   -- Buying into ask wall
        0
    ) AS aggression_into_wall,

    -- Directional aggression AWAY from wall (opposite direction)
    multiIf(
        i.`w.wall_side` = 'BID',
        buy_volume_at_wall_5s,   -- Buying away from bid wall
        i.`w.wall_side` = 'ASK',
        sell_volume_at_wall_5s,  -- Selling away from ask wall
        0
    ) AS aggression_away_from_wall,

    -- Delta flip detection (did delta reverse after touching wall?)
    multiIf(
        delta_before_5s > 50 AND delta_after_5s < -50, 1,   -- Flipped from buy to sell
        delta_before_5s < -50 AND delta_after_5s > 50, 1,   -- Flipped from sell to buy
        0
    ) AS delta_flipped,

    -- Aggression classification
    multiIf(
        peak_aggression_score >= 0.80, 'HIGH',
        peak_aggression_score >= 0.50, 'MODERATE',
        peak_aggression_score >= 0.20, 'LOW',
        'VERY_LOW'
    ) AS aggression_classification

FROM CG_mnq_wall_interactions i
LEFT JOIN CG_mnq_aggression_100ms agg
    ON dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -5000 AND 5000
GROUP BY
    i.wall_id,
    i.interaction_time,
    i.`w.wall_side`
ORDER BY interaction_time;

-- ============================================================================
-- VERIFICATION
-- ============================================================================

SELECT '=== Aggression Enrichment Complete ===' AS report FORMAT Pretty;

SELECT
    count() AS total_interactions,
    countDistinct(wall_id) AS unique_walls,
    min(interaction_time) AS first_interaction,
    max(interaction_time) AS last_interaction
FROM CG_mnq_wall_interaction_aggression_v1
FORMAT Pretty;

SELECT '=== Aggression Classification Distribution ===' AS report FORMAT Pretty;

SELECT
    aggression_classification,
    count() AS interactions,
    round(count() / (SELECT count() FROM CG_mnq_wall_interaction_aggression_v1) * 100, 2) AS pct,
    round(avg(aggression_into_wall), 0) AS avg_agg_into,
    round(avg(total_volume_at_wall_5s), 0) AS avg_vol
FROM CG_mnq_wall_interaction_aggression_v1
GROUP BY aggression_classification
ORDER BY aggression_classification
FORMAT Pretty;

SELECT '=== Delta Flip Distribution ===' AS report FORMAT Pretty;

SELECT
    delta_flipped,
    count() AS interactions,
    round(avg(delta_before_5s), 0) AS avg_delta_before,
    round(avg(delta_after_5s), 0) AS avg_delta_after
FROM CG_mnq_wall_interaction_aggression_v1
GROUP BY delta_flipped
FORMAT Pretty;

SELECT '=== High Aggression Into Wall (Top 20) ===' AS report FORMAT Pretty;

SELECT
    interaction_time,
    wall_id,
    aggression_into_wall,
    aggression_away_from_wall,
    delta_before_5s,
    delta_after_5s,
    delta_flipped,
    aggression_classification
FROM CG_mnq_wall_interaction_aggression_v1
ORDER BY aggression_into_wall DESC
LIMIT 20
FORMAT Pretty;

SELECT '=== Delta Flip Cases (Potential Reversals) ===' AS report FORMAT Pretty;

SELECT
    interaction_time,
    wall_id,
    delta_before_5s,
    delta_after_5s,
    aggression_into_wall,
    total_volume_at_wall_5s
FROM CG_mnq_wall_interaction_aggression_v1
WHERE delta_flipped = 1
ORDER BY total_volume_at_wall_5s DESC
LIMIT 20
FORMAT Pretty;

-- ============================================================================
-- NEXT STEP: Join response + aggression into enriched table (Step 3)
-- ============================================================================
-- File: ENRICH_STEP_3_JOIN.sql
-- ============================================================================
