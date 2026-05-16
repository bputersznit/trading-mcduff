-- ============================================================================
-- Step 2: Aggression Metrics - MINIMAL VERSION
-- ============================================================================
-- Just the essential aggression metrics without before/after breakdown
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

    -- Aggression at wall (±2.5s window) - 4 subqueries total
    (SELECT sum(buy_volume)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500
    ) AS buy_volume_at_wall,

    (SELECT sum(sell_volume)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500
    ) AS sell_volume_at_wall,

    (SELECT sum(total_volume)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500
    ) AS total_volume_at_wall,

    (SELECT max(aggression_score)
     FROM CG_mnq_aggression_100ms agg
     WHERE dateDiff('millisecond', i.interaction_time, agg.bucket_time) BETWEEN -2500 AND 2500
    ) AS peak_aggression_score,

    -- Directional aggression (INTO wall vs AWAY from wall)
    multiIf(
        i.`w.wall_side` = 'BID', sell_volume_at_wall,  -- Selling into bid support
        i.`w.wall_side` = 'ASK', buy_volume_at_wall,   -- Buying into ask resistance
        0
    ) AS aggression_into_wall,

    multiIf(
        i.`w.wall_side` = 'BID', buy_volume_at_wall,   -- Buying away from bid
        i.`w.wall_side` = 'ASK', sell_volume_at_wall,  -- Selling away from ask
        0
    ) AS aggression_away_from_wall,

    -- Aggression classification
    multiIf(
        peak_aggression_score >= 0.80, 'HIGH',
        peak_aggression_score >= 0.50, 'MODERATE',
        peak_aggression_score >= 0.20, 'LOW',
        'VERY_LOW'
    ) AS aggression_classification

FROM CG_mnq_wall_interactions i
ORDER BY interaction_time;

SELECT '=== Aggression Enrichment Complete (Minimal) ===' AS report FORMAT Pretty;
SELECT count() AS interactions, min(interaction_time) AS first, max(interaction_time) AS last
FROM CG_mnq_wall_interaction_aggression_v1 FORMAT Pretty;

SELECT '=== Aggression Distribution ===' AS report FORMAT Pretty;
SELECT
    aggression_classification,
    count() AS interactions,
    round(avg(aggression_into_wall), 0) AS avg_into,
    round(avg(total_volume_at_wall), 0) AS avg_vol
FROM CG_mnq_wall_interaction_aggression_v1
GROUP BY aggression_classification
ORDER BY aggression_classification
FORMAT Pretty;
