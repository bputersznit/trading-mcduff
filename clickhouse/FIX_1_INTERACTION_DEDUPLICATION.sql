-- ============================================================================
-- FIX #1: INTERACTION DEDUPLICATION (CRITICAL)
-- ============================================================================
-- Purpose: Convert tick-based "interactions" into proper wall interaction events
-- Problem: 100K rows = overcounting (same wall touched 300x = 300 rows)
-- Solution: Group continuous wall proximity into ONE interaction
-- Expected: 100K rows → ~500-2000 unique interactions
--
-- Date: 2026-05-04
-- Author: McDuff diagnosis + implementation
-- ============================================================================

-- ============================================================================
-- STEP 1: Identify continuous interaction windows
-- ============================================================================
-- An interaction = price stays within ±2 ticks of wall for some duration
-- Multiple consecutive ticks = ONE interaction
-- Gap of >5 seconds OR distance >5 ticks = NEW interaction

DROP TABLE IF EXISTS CG_mnq_wall_interactions_grouped;

CREATE TABLE CG_mnq_wall_interactions_grouped
ENGINE = MergeTree
PARTITION BY toDate(base_interaction_time)
ORDER BY (wall_id, base_interaction_time)
AS
WITH
-- Add sequential row numbers per wall
numbered AS (
    SELECT
        *,
        ROW_NUMBER() OVER (PARTITION BY base_wall_id ORDER BY base_interaction_time) AS row_num
    FROM CG_mnq_wall_interactions_enriched_v1
),

-- Detect gaps (new interaction starts when gap exists)
with_gaps AS (
    SELECT
        *,
        -- Previous row's time and distance
        lagInFrame(base_interaction_time, 1, toDateTime64('1970-01-01', 3, 'UTC')) OVER (
            PARTITION BY base_wall_id
            ORDER BY base_interaction_time
        ) AS prev_time,

        lagInFrame(`base.distance_to_wall_ticks`, 1, 999) OVER (
            PARTITION BY base_wall_id
            ORDER BY base_interaction_time
        ) AS prev_distance,

        -- Detect if this is start of NEW interaction
        -- New interaction if:
        --  1. Time gap > 5 seconds since last tick
        --  2. OR first row for this wall
        --  3. OR distance suddenly increased (price left zone then came back)
        multiIf(
            row_num = 1, 1,  -- First row = new interaction
            dateDiff('second', prev_time, base_interaction_time) > 5, 1,  -- Time gap
            abs(`base.distance_to_wall_ticks` - prev_distance) > 3, 1,  -- Distance jump
            0  -- Continue current interaction
        ) AS is_new_interaction

    FROM numbered
),

-- Assign interaction_id using running sum
with_interaction_ids AS (
    SELECT
        *,
        -- Create unique interaction_id by summing the "new interaction" flags
        sum(is_new_interaction) OVER (
            PARTITION BY base_wall_id
            ORDER BY base_interaction_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS interaction_group_id,

        -- Create globally unique interaction_id
        concat(base_wall_id, '_', toString(interaction_group_id)) AS interaction_id

    FROM with_gaps
)

SELECT * FROM with_interaction_ids
ORDER BY base_wall_id, base_interaction_time;

SELECT '=== Interaction Grouping Complete ===' AS report FORMAT Pretty;
SELECT
    count() AS total_rows,
    countDistinct(interaction_id) AS unique_interactions,
    round(count() / countDistinct(interaction_id), 2) AS avg_ticks_per_interaction,
    min(base_interaction_time) AS first,
    max(base_interaction_time) AS last
FROM CG_mnq_wall_interactions_grouped
FORMAT Pretty;

-- ============================================================================
-- STEP 2: Create ONE ROW PER INTERACTION (deduplicated)
-- ============================================================================
-- For each interaction_id, keep only the PEAK moment (closest approach to wall)

DROP TABLE IF EXISTS CG_mnq_wall_interactions_deduped_v1;

CREATE TABLE CG_mnq_wall_interactions_deduped_v1
ENGINE = MergeTree
PARTITION BY toDate(interaction_time)
ORDER BY (interaction_id, interaction_time)
AS
WITH
-- Find the peak moment (closest approach) for each interaction
interaction_stats AS (
    SELECT
        interaction_id,

        -- Lifecycle timestamps
        min(base_interaction_time) AS approach_time,
        argMin(base_interaction_time, abs(`base.distance_to_wall_ticks`)) AS peak_time,
        max(base_interaction_time) AS resolve_time,

        -- Duration
        dateDiff('millisecond', min(base_interaction_time), max(base_interaction_time)) AS duration_ms,

        -- How many ticks in this interaction
        count() AS ticks_in_interaction,

        -- Peak aggression during interaction
        max(peak_aggression_score) AS peak_agg_score_interaction,
        sum(aggression_into_wall) AS total_agg_into_interaction,

        -- Price movement during interaction
        max(mfe_ticks_10s) AS max_mfe_during_interaction,
        min(mae_ticks_10s) AS min_mae_during_interaction,

        -- Closest approach
        min(abs(`base.distance_to_wall_ticks`)) AS min_distance_ticks,

        -- Wall characteristics (should be constant per interaction)
        any(base_wall_id) AS wall_id,
        any(wall_side) AS wall_side,
        any(wall_price) AS wall_price,
        any(`base.wall_behavior`) AS wall_behavior,
        any(wall_rank) AS wall_rank,
        any(wall_type) AS wall_type

    FROM CG_mnq_wall_interactions_grouped
    GROUP BY interaction_id
),

-- Get the FULL ROW at peak_time for each interaction
peak_rows AS (
    SELECT
        g.*,
        s.approach_time,
        s.peak_time,
        s.resolve_time,
        s.duration_ms,
        s.ticks_in_interaction,
        s.peak_agg_score_interaction,
        s.total_agg_into_interaction,
        s.max_mfe_during_interaction,
        s.min_mae_during_interaction,
        s.min_distance_ticks,

        -- Flag if this is the peak row
        base_interaction_time = s.peak_time AS is_peak_row

    FROM CG_mnq_wall_interactions_grouped g
    INNER JOIN interaction_stats s ON g.interaction_id = s.interaction_id
)

-- Keep ONLY the peak row per interaction
SELECT
    interaction_id,

    -- Lifecycle timestamps
    approach_time,
    peak_time AS interaction_time,  -- Use peak as canonical interaction_time
    resolve_time,
    duration_ms,
    ticks_in_interaction,

    -- Is this the first touch for this wall?
    ROW_NUMBER() OVER (PARTITION BY base_wall_id ORDER BY approach_time) = 1 AS is_first_touch,

    -- Mark as unique (one row per interaction)
    1 AS is_unique_interaction,

    -- Wall characteristics
    base_wall_id AS wall_id,
    wall_side,
    wall_price,
    `base.wall_size` AS wall_size,
    `base.wall_score` AS wall_score,
    wall_rank,
    wall_type,
    wall_behavior,

    -- Lifecycle metrics
    `base.pull_ratio` AS pull_ratio,
    `base.fill_ratio` AS fill_ratio,
    `base.replenish_ratio` AS replenish_ratio,
    `base.wall_lifetime_ms` AS wall_lifetime_ms,

    -- Position at peak
    `base.price_at_interaction` AS price_at_peak,
    `base.distance_to_wall_ticks` AS distance_at_peak,
    min_distance_ticks AS closest_approach_ticks,

    -- Response metrics (from peak moment)
    future_high_10s,
    future_low_10s,
    future_high_30s,
    future_low_30s,
    mfe_ticks_10s,
    mae_ticks_10s,
    mfe_ticks_30s,
    mae_ticks_30s,
    outcome_basic,

    -- Aggregated interaction metrics
    max_mfe_during_interaction,
    min_mae_during_interaction,

    -- Aggression metrics (from peak moment)
    buy_volume_before_5s,
    sell_volume_before_5s,
    delta_before_5s,
    buy_volume_after_5s,
    sell_volume_after_5s,
    delta_after_5s,
    buy_volume_at_wall,
    sell_volume_at_wall,
    total_volume_at_wall,
    delta_at_wall,
    peak_aggression_score,
    aggression_into_wall,
    aggression_away_from_wall,
    delta_flip_pattern,
    aggression_classification,

    -- Aggregated aggression across interaction
    peak_agg_score_interaction,
    total_agg_into_interaction

FROM peak_rows
WHERE is_peak_row = 1  -- Keep only the peak row per interaction
ORDER BY interaction_time;

-- ============================================================================
-- VERIFICATION
-- ============================================================================

SELECT '=== DEDUPLICATION COMPLETE ===' AS report FORMAT Pretty;

SELECT
    count() AS unique_interactions,
    min(interaction_time) AS first_interaction,
    max(interaction_time) AS last_interaction,
    countDistinct(toDate(interaction_time)) AS days,
    round(avg(ticks_in_interaction), 2) AS avg_ticks_per_interaction,
    round(avg(duration_ms), 0) AS avg_duration_ms,
    countIf(is_first_touch = 1) AS first_touch_count,
    sum(ticks_in_interaction) AS original_row_count_check
FROM CG_mnq_wall_interactions_deduped_v1
FORMAT Pretty;

SELECT '=== Daily Breakdown (Deduped) ===' AS report FORMAT Pretty;

SELECT
    toDate(interaction_time) AS trade_date,
    count() AS unique_interactions,
    sum(ticks_in_interaction) AS original_tick_count,
    round(avg(duration_ms), 0) AS avg_duration_ms,
    countIf(wall_behavior = 'REPLENISHING_WALL') AS replenishing,
    countIf(wall_behavior = 'PULLED_WALL') AS pulled,
    countIf(wall_behavior = 'ICEBERG_LIKE_WALL') AS iceberg
FROM CG_mnq_wall_interactions_deduped_v1
GROUP BY trade_date
ORDER BY trade_date
FORMAT Pretty;

SELECT '=== Interaction Duration Distribution ===' AS report FORMAT Pretty;

SELECT
    multiIf(
        duration_ms < 500, '<500ms',
        duration_ms < 1000, '500ms-1s',
        duration_ms < 2000, '1-2s',
        duration_ms < 5000, '2-5s',
        duration_ms < 10000, '5-10s',
        '10s+'
    ) AS duration_bucket,
    count() AS interactions,
    round(avg(ticks_in_interaction), 2) AS avg_ticks,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae
FROM CG_mnq_wall_interactions_deduped_v1
GROUP BY duration_bucket
ORDER BY
    CASE duration_bucket
        WHEN '<500ms' THEN 1
        WHEN '500ms-1s' THEN 2
        WHEN '1-2s' THEN 3
        WHEN '2-5s' THEN 4
        WHEN '5-10s' THEN 5
        WHEN '10s+' THEN 6
    END
FORMAT Pretty;

SELECT '=== Wall Behavior Distribution (Deduped) ===' AS report FORMAT Pretty;

SELECT
    wall_behavior,
    count() AS interactions,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe,
    round(avg(mae_ticks_10s), 2) AS avg_mae,
    round(avg(mfe_ticks_10s) + avg(mae_ticks_10s), 2) AS net_ticks
FROM CG_mnq_wall_interactions_deduped_v1
WHERE wall_behavior != ''
GROUP BY wall_behavior
ORDER BY interactions DESC
FORMAT Pretty;

SELECT '=== BEFORE vs AFTER Comparison ===' AS report FORMAT Pretty;

WITH before AS (
    SELECT
        'BEFORE (tick-based)' AS dataset,
        count() AS row_count,
        countDistinct(base_wall_id) AS unique_walls
    FROM CG_mnq_wall_interactions_enriched_v1
),
after AS (
    SELECT
        'AFTER (deduped)' AS dataset,
        count() AS row_count,
        countDistinct(wall_id) AS unique_walls
    FROM CG_mnq_wall_interactions_deduped_v1
)
SELECT * FROM before
UNION ALL
SELECT * FROM after
FORMAT Pretty;

SELECT '=== Reduction Factor ===' AS report FORMAT Pretty;

SELECT
    100000 AS original_rows,
    (SELECT count() FROM CG_mnq_wall_interactions_deduped_v1) AS deduped_rows,
    round(100000.0 / (SELECT count() FROM CG_mnq_wall_interactions_deduped_v1), 2) AS reduction_factor,
    round((1 - (SELECT count() FROM CG_mnq_wall_interactions_deduped_v1) / 100000.0) * 100, 2) AS pct_reduction
FORMAT Pretty;
