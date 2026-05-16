-- ============================================================================
-- Enrichment Step 2: Aggression Metrics (FIXED - using subqueries)
-- ============================================================================
-- Purpose: Add buy/sell pressure metrics around wall interactions
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
    i.`w.wall_side` AS wall_side,

    -- Aggression BEFORE interaction (-5s to 0s) using subqueries
    (SELECT sum(buy_volume)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -5000 AND 0
    ) AS buy_volume_before_5s,

    (SELECT sum(sell_volume)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -5000 AND 0
    ) AS sell_volume_before_5s,

    (SELECT sum(delta)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -5000 AND 0
    ) AS delta_before_5s,

    -- Aggression AFTER interaction (0s to +5s)
    (SELECT sum(buy_volume)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN 0 AND 5000
    ) AS buy_volume_after_5s,

    (SELECT sum(sell_volume)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN 0 AND 5000
    ) AS sell_volume_after_5s,

    (SELECT sum(delta)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN 0 AND 5000
    ) AS delta_after_5s,

    -- Aggression AT wall (-2.5s to +2.5s)
    (SELECT sum(buy_volume)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500
    ) AS buy_volume_at_wall_5s,

    (SELECT sum(sell_volume)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500
    ) AS sell_volume_at_wall_5s,

    (SELECT sum(total_volume)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500
    ) AS total_volume_at_wall_5s,

    (SELECT sum(delta)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500
    ) AS delta_at_wall_5s,

    (SELECT max(aggression_score)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500
    ) AS peak_aggression_score,

    -- Directional aggression (computed after aggregation)
    multiIf(
        i.`w.wall_side` = 'BID', sell_volume_at_wall_5s,
        i.`w.wall_side` = 'ASK', buy_volume_at_wall_5s,
        0
    ) AS aggression_into_wall,

    multiIf(
        i.`w.wall_side` = 'BID', buy_volume_at_wall_5s,
        i.`w.wall_side` = 'ASK', sell_volume_at_wall_5s,
        0
    ) AS aggression_away_from_wall,

    -- Delta flip detection
    multiIf(
        delta_before_5s > 50 AND delta_after_5s < -50, 1,
        delta_before_5s < -50 AND delta_after_5s > 50, 1,
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
ORDER BY interaction_time;

-- ============================================================================
-- VERIFICATION
-- ============================================================================

SELECT '=== Aggression Enrichment Complete ===' AS report FORMAT Pretty;

SELECT
    count() AS total_interactions,
    min(interaction_time) AS first,
    max(interaction_time) AS last
FROM CG_mnq_wall_interaction_aggression_v1
FORMAT Pretty;

SELECT '=== Aggression Classification ===' AS report FORMAT Pretty;

SELECT
    aggression_classification,
    count() AS interactions,
    round(count() * 100.0 / (SELECT count() FROM CG_mnq_wall_interaction_aggression_v1), 2) AS pct,
    round(avg(aggression_into_wall), 0) AS avg_agg_into,
    round(avg(total_volume_at_wall_5s), 0) AS avg_total_vol
FROM CG_mnq_wall_interaction_aggression_v1
GROUP BY aggression_classification
ORDER BY aggression_classification
FORMAT Pretty;

SELECT '=== Delta Flips ===' AS report FORMAT Pretty;

SELECT
    delta_flipped,
    count() AS interactions,
    round(avg(delta_before_5s), 0) AS avg_delta_before,
    round(avg(delta_after_5s), 0) AS avg_delta_after,
    round(avg(total_volume_at_wall_5s), 0) AS avg_volume
FROM CG_mnq_wall_interaction_aggression_v1
GROUP BY delta_flipped
ORDER BY delta_flipped
FORMAT Pretty;

SELECT '=== High Aggression Sample ===' AS report FORMAT Pretty;

SELECT
    interaction_time,
    wall_side,
    aggression_into_wall,
    aggression_away_from_wall,
    delta_before_5s,
    delta_after_5s,
    total_volume_at_wall_5s,
    aggression_classification
FROM CG_mnq_wall_interaction_aggression_v1
ORDER BY aggression_into_wall DESC
LIMIT 10
FORMAT Pretty;
