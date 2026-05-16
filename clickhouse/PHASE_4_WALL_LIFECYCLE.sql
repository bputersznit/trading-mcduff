-- ============================================================================
-- Phase 4: Wall Lifecycle - Tracking Wall Behavior Over Time
-- ============================================================================
-- Purpose: Track each detected wall's lifecycle to classify behavior
-- Source: CG_mnq_liquidity_walls_100ms (wall detections)
--         CG_mnq_heatmap_100ms (activity at wall price levels)
-- Output: CG_mnq_wall_lifecycle
--
-- This table answers: "Is this wall real, fake, replenishing, or consumed?"
--
-- Methodology:
--   - Group consecutive wall detections at same price into "wall events"
--   - Track size changes over time (adds, cancels, fills)
--   - Calculate pull_ratio, fill_ratio, replenish_ratio
--   - Classify: STATIC, PULLED, CONSUMED, REPLENISHING, ICEBERG-LIKE, LADDERED
--
-- Date: 2026-05-03
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_lifecycle;

CREATE TABLE CG_mnq_wall_lifecycle
ENGINE = MergeTree
PARTITION BY toDate(first_seen_time)
ORDER BY (wall_id, first_seen_time)
AS
WITH wall_with_lags AS (
    -- Step 1a: Compute lag values first
    SELECT
        bucket_time,
        trade_date,
        wall_side,
        wall_price,
        wall_size,
        wall_rank,
        wall_score,
        wall_type,

        -- Track previous values for gap detection
        lagInFrame(wall_price, 1, wall_price) OVER (
            PARTITION BY wall_side
            ORDER BY bucket_time
        ) AS prev_price,

        lagInFrame(wall_side, 1, wall_side) OVER (
            PARTITION BY wall_side
            ORDER BY bucket_time
        ) AS prev_side,

        lagInFrame(bucket_time, 1, bucket_time) OVER (
            PARTITION BY wall_side
            ORDER BY bucket_time
        ) AS prev_bucket_time

    FROM CG_mnq_liquidity_walls_100ms
    WHERE wall_rank IN ('P99.9', 'P99.5', 'P99')  -- Only track top-tier walls
),

wall_continuity AS (
    -- Step 1b: Use lag values to create wall groups
    SELECT
        bucket_time,
        trade_date,
        wall_side,
        wall_price,
        wall_size,
        wall_rank,
        wall_score,
        wall_type,

        -- Create wall groups: new wall_id when price or side changes
        -- or when gap > 1 second (wall disappeared then reappeared)
        sum(if(
            prev_price != wall_price
            OR prev_side != wall_side
            OR dateDiff('millisecond', prev_bucket_time, bucket_time) > 1000,
            1,
            0
        )) OVER (
            PARTITION BY wall_side
            ORDER BY bucket_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS wall_group_id

    FROM wall_with_lags
),

wall_lifecycle_agg AS (
    -- Step 2: Aggregate wall groups into lifecycle events
    SELECT
        -- Generate unique wall_id
        row_number() OVER (ORDER BY min(bucket_time)) AS wall_id,

        -- Time bounds
        min(bucket_time) AS first_seen_time,
        max(bucket_time) AS last_seen_time,
        count() * 100 AS duration_ms,  -- Each bucket is 100ms

        -- Wall identity
        wall_side,
        wall_price,
        any(wall_type) AS wall_type,
        any(wall_rank) AS wall_rank,

        -- Size tracking
        argMin(wall_size, bucket_time) AS initial_size,
        max(wall_size) AS max_size,
        argMax(wall_size, bucket_time) AS final_size,

        -- Derived metrics will be added from heatmap activity
        0 AS total_added,     -- Placeholder, will update below
        0 AS total_canceled,  -- Placeholder
        0 AS total_filled     -- Placeholder

    FROM wall_continuity
    GROUP BY wall_group_id, wall_side, wall_price
),

wall_activity AS (
    -- Step 3: Join with heatmap to get actual add/cancel/fill activity
    SELECT
        wl.wall_id,
        wl.first_seen_time,
        wl.last_seen_time,
        wl.duration_ms,
        wl.wall_side,
        wl.wall_price,
        wl.wall_type,
        wl.wall_rank,
        wl.initial_size,
        wl.max_size,
        wl.final_size,

        -- Aggregate activity from heatmap during wall lifetime
        sum(if(wl.wall_side = 'BID', h.bid_add_size, h.ask_add_size)) AS total_added,
        sum(if(wl.wall_side = 'BID', h.bid_cancel_size, h.ask_cancel_size)) AS total_canceled,
        sum(if(wl.wall_side = 'BID', h.bid_fill_size, h.ask_fill_size)) AS total_filled

    FROM wall_lifecycle_agg wl
    LEFT JOIN CG_mnq_heatmap_100ms h
        ON h.price = wl.wall_price
        AND h.bucket_time BETWEEN wl.first_seen_time AND wl.last_seen_time
    GROUP BY
        wl.wall_id,
        wl.first_seen_time,
        wl.last_seen_time,
        wl.duration_ms,
        wl.wall_side,
        wl.wall_price,
        wl.wall_type,
        wl.wall_rank,
        wl.initial_size,
        wl.max_size,
        wl.final_size
)

SELECT
    wall_id,
    first_seen_time,
    last_seen_time,
    duration_ms,
    wall_side,
    wall_price,
    wall_type,
    wall_rank,

    -- Size metrics
    initial_size,
    max_size,
    final_size,
    total_added,
    total_canceled,
    total_filled,

    -- Behavior ratios
    round(total_canceled / nullIf(max_size, 0), 4) AS pull_ratio,
    round(total_filled / nullIf(max_size, 0), 4) AS fill_ratio,
    round(total_added / nullIf(total_filled, 0), 4) AS replenish_ratio,

    -- Survival time after first fill (proxy for absorption strength)
    -- Note: This requires more complex logic to track "first fill time"
    -- For now, use duration as proxy
    duration_ms AS survival_after_touch_ms,

    -- Behavior classification
    CASE
        -- PULLED: Large cancellations before fills
        WHEN pull_ratio > 0.70 AND fill_ratio < 0.30 THEN 'PULLED_WALL'

        -- CONSUMED: Mostly filled, little replenishment
        WHEN fill_ratio > 0.70 AND replenish_ratio < 1.20 THEN 'CONSUMED_WALL'

        -- ICEBERG/REPLENISHING: Filled but size maintained via adds
        WHEN fill_ratio > 0.30 AND replenish_ratio > 1.50 THEN 'REPLENISHING_WALL'

        -- ICEBERG-LIKE: Very strong replenishment
        WHEN replenish_ratio > 3.0 THEN 'ICEBERG_LIKE_WALL'

        -- STATIC: Little activity
        WHEN fill_ratio < 0.20 AND pull_ratio < 0.20 THEN 'STATIC_WALL'

        -- Default
        ELSE 'MIXED_BEHAVIOR'
    END AS wall_behavior

FROM wall_activity
WHERE duration_ms >= 500  -- Only keep walls that existed for at least 500ms (5+ buckets)
ORDER BY first_seen_time;

-- ============================================================================
-- VERIFICATION
-- ============================================================================

SELECT '=== CG_mnq_wall_lifecycle Summary ===' AS report FORMAT Pretty;

SELECT
    count() AS total_walls,
    countDistinct(wall_price) AS unique_prices,
    min(first_seen_time) AS first_wall,
    max(last_seen_time) AS last_wall,
    round(avg(duration_ms), 0) AS avg_duration_ms,
    round(avg(initial_size), 0) AS avg_initial_size
FROM CG_mnq_wall_lifecycle
FORMAT Pretty;

SELECT '=== Wall Behavior Distribution ===' AS report FORMAT Pretty;

SELECT
    wall_behavior,
    count() AS walls,
    round(count() / (SELECT count() FROM CG_mnq_wall_lifecycle) * 100, 2) AS pct,
    round(avg(duration_ms), 0) AS avg_duration_ms,
    round(avg(pull_ratio), 4) AS avg_pull_ratio,
    round(avg(fill_ratio), 4) AS avg_fill_ratio,
    round(avg(replenish_ratio), 4) AS avg_replenish_ratio
FROM CG_mnq_wall_lifecycle
GROUP BY wall_behavior
ORDER BY walls DESC
FORMAT Pretty;

SELECT '=== Wall Rank vs Behavior ===' AS report FORMAT Pretty;

SELECT
    wall_rank,
    wall_behavior,
    count() AS walls,
    round(avg(initial_size), 0) AS avg_size,
    round(avg(duration_ms), 0) AS avg_duration_ms
FROM CG_mnq_wall_lifecycle
GROUP BY wall_rank, wall_behavior
ORDER BY wall_rank, walls DESC
FORMAT Pretty;

SELECT '=== Top 20 Most Persistent Walls ===' AS report FORMAT Pretty;

SELECT
    first_seen_time,
    wall_side,
    wall_price,
    wall_rank,
    round(duration_ms / 1000, 1) AS duration_sec,
    initial_size,
    max_size,
    final_size,
    wall_behavior,
    round(pull_ratio, 4) AS pull_r,
    round(fill_ratio, 4) AS fill_r,
    round(replenish_ratio, 4) AS replenish_r
FROM CG_mnq_wall_lifecycle
ORDER BY duration_ms DESC
LIMIT 20
FORMAT Pretty;

-- ============================================================================
-- NEXT STEP: Phase 5 - Aggression System (measuring buy/sell pressure)
-- ============================================================================
