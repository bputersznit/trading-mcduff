-- ============================================================================
-- Enrichment Step 2: Aggression Metrics (JOIN-OPTIMIZED)
-- ============================================================================
-- Purpose: Add aggression metrics using efficient JOIN instead of subqueries
-- Approach: Use time-range JOIN to avoid 1.2M subqueries
-- Date: 2026-05-04
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_interaction_aggression_v1;

CREATE TABLE CG_mnq_wall_interaction_aggression_v1
ENGINE = MergeTree
PARTITION BY toDate(interaction_time)
ORDER BY (interaction_time, wall_id)
AS
WITH interaction_windows AS (
    SELECT
        wall_id,
        interaction_time,
        `w.wall_side` AS wall_side,
        -- Pre-compute time windows for efficient JOIN
        toDateTime(interaction_time - toIntervalSecond(5)) AS window_start_before,
        toDateTime(interaction_time) AS window_mid,
        toDateTime(interaction_time + toIntervalSecond(5)) AS window_end_after
    FROM CG_mnq_wall_interactions
),
aggression_joined AS (
    SELECT
        i.wall_id,
        i.interaction_time,
        i.wall_side,

        -- Aggregate aggression in different time windows
        sumIf(agg.buy_volume, agg.bucket_time BETWEEN i.window_start_before AND i.window_mid) AS buy_volume_before_5s,
        sumIf(agg.sell_volume, agg.bucket_time BETWEEN i.window_start_before AND i.window_mid) AS sell_volume_before_5s,
        sumIf(agg.delta, agg.bucket_time BETWEEN i.window_start_before AND i.window_mid) AS delta_before_5s,

        sumIf(agg.buy_volume, agg.bucket_time BETWEEN i.window_mid AND i.window_end_after) AS buy_volume_after_5s,
        sumIf(agg.sell_volume, agg.bucket_time BETWEEN i.window_mid AND i.window_end_after) AS sell_volume_after_5s,
        sumIf(agg.delta, agg.bucket_time BETWEEN i.window_mid AND i.window_end_after) AS delta_after_5s,

        -- At-wall window (±2.5s)
        sumIf(agg.buy_volume, abs(dateDiff('millisecond', i.interaction_time, agg.bucket_time)) <= 2500) AS buy_volume_at_wall,
        sumIf(agg.sell_volume, abs(dateDiff('millisecond', i.interaction_time, agg.bucket_time)) <= 2500) AS sell_volume_at_wall,
        sumIf(agg.total_volume, abs(dateDiff('millisecond', i.interaction_time, agg.bucket_time)) <= 2500) AS total_volume_at_wall,
        sumIf(agg.delta, abs(dateDiff('millisecond', i.interaction_time, agg.bucket_time)) <= 2500) AS delta_at_wall,
        maxIf(agg.aggression_score, abs(dateDiff('millisecond', i.interaction_time, agg.bucket_time)) <= 2500) AS peak_aggression_score

    FROM interaction_windows i
    LEFT JOIN CG_mnq_aggression_100ms agg
        ON agg.bucket_time BETWEEN i.window_start_before AND i.window_end_after
        AND toDate(agg.bucket_time) = toDate(i.interaction_time)  -- Partition pruning
    GROUP BY i.wall_id, i.interaction_time, i.wall_side
)
SELECT
    wall_id,
    interaction_time,
    wall_side,

    -- Before window
    buy_volume_before_5s,
    sell_volume_before_5s,
    delta_before_5s,

    -- After window
    buy_volume_after_5s,
    sell_volume_after_5s,
    delta_after_5s,

    -- At-wall window
    buy_volume_at_wall,
    sell_volume_at_wall,
    total_volume_at_wall,
    delta_at_wall,
    peak_aggression_score,

    -- Directional aggression (INTO wall vs AWAY from wall)
    multiIf(
        wall_side = 'BID', sell_volume_at_wall,  -- Selling into bid support
        wall_side = 'ASK', buy_volume_at_wall,   -- Buying into ask resistance
        0
    ) AS aggression_into_wall,

    multiIf(
        wall_side = 'BID', buy_volume_at_wall,   -- Buying away from bid
        wall_side = 'ASK', sell_volume_at_wall,  -- Selling away from ask
        0
    ) AS aggression_away_from_wall,

    -- Delta flip detection
    CASE
        WHEN delta_before_5s > 0 AND delta_after_5s < 0 THEN 'BUY_TO_SELL_FLIP'
        WHEN delta_before_5s < 0 AND delta_after_5s > 0 THEN 'SELL_TO_BUY_FLIP'
        WHEN delta_before_5s > 0 AND delta_after_5s > 0 THEN 'CONTINUED_BUY'
        WHEN delta_before_5s < 0 AND delta_after_5s < 0 THEN 'CONTINUED_SELL'
        ELSE 'NO_CLEAR_DELTA'
    END AS delta_flip_pattern,

    -- Aggression classification
    multiIf(
        peak_aggression_score >= 0.80, 'HIGH',
        peak_aggression_score >= 0.50, 'MODERATE',
        peak_aggression_score >= 0.20, 'LOW',
        'VERY_LOW'
    ) AS aggression_classification,

    -- Absorption ratio (aggression into wall / price move)
    -- Will compute in enriched table after joining with response metrics
    0 AS absorption_score_placeholder

FROM aggression_joined
ORDER BY interaction_time;

-- ============================================================================
-- VERIFICATION
-- ============================================================================

SELECT '=== Aggression Enrichment Complete (JOIN-OPTIMIZED) ===' AS report FORMAT Pretty;

SELECT
    count() AS interactions,
    min(interaction_time) AS first,
    max(interaction_time) AS last,
    countDistinct(toDate(interaction_time)) AS days
FROM CG_mnq_wall_interaction_aggression_v1
FORMAT Pretty;

SELECT '=== Aggression Classification Distribution ===' AS report FORMAT Pretty;

SELECT
    aggression_classification,
    count() AS interactions,
    round(avg(aggression_into_wall), 0) AS avg_into,
    round(avg(total_volume_at_wall), 0) AS avg_vol,
    round(avg(peak_aggression_score), 3) AS avg_score
FROM CG_mnq_wall_interaction_aggression_v1
GROUP BY aggression_classification
ORDER BY aggression_classification
FORMAT Pretty;

SELECT '=== Delta Flip Pattern Distribution ===' AS report FORMAT Pretty;

SELECT
    delta_flip_pattern,
    count() AS interactions,
    round(count() * 100.0 / (SELECT count() FROM CG_mnq_wall_interaction_aggression_v1), 2) AS pct
FROM CG_mnq_wall_interaction_aggression_v1
GROUP BY delta_flip_pattern
ORDER BY interactions DESC
FORMAT Pretty;
