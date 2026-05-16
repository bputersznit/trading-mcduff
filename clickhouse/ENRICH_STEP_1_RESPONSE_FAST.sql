-- ============================================================================
-- Enrichment Step 1: Forward Price Response (FAST VERSION)
-- ============================================================================
-- Purpose: Add MFE/MAE metrics using materialized forward ranges
-- Approach: Pre-compute forward price extremes, then simple JOIN
-- Date: 2026-05-01
-- ============================================================================

-- Use existing price series if available
-- (Already created: CG_mnq_price_series_100ms with 11.5M rows)

-- Step 1: Create materialized forward price ranges using window functions
DROP TABLE IF EXISTS CG_mnq_forward_price_ranges;

CREATE TABLE CG_mnq_forward_price_ranges
ENGINE = MergeTree
ORDER BY bucket_time
AS
SELECT
    bucket_time,
    price,

    -- 10s forward window (next ~100 buckets)
    max(price) OVER (
        ORDER BY bucket_time
        ROWS BETWEEN CURRENT ROW AND 100 FOLLOWING
    ) AS max_price_10s,

    min(price) OVER (
        ORDER BY bucket_time
        ROWS BETWEEN CURRENT ROW AND 100 FOLLOWING
    ) AS min_price_10s,

    -- 30s forward window (next ~300 buckets)
    max(price) OVER (
        ORDER BY bucket_time
        ROWS BETWEEN CURRENT ROW AND 300 FOLLOWING
    ) AS max_price_30s,

    min(price) OVER (
        ORDER BY bucket_time
        ROWS BETWEEN CURRENT ROW AND 300 FOLLOWING
    ) AS min_price_30s

FROM CG_mnq_price_series_100ms
ORDER BY bucket_time;

SELECT '=== Forward Price Ranges Created ===' AS report FORMAT Pretty;
SELECT
    count() AS ranges,
    min(bucket_time) AS first_time,
    max(bucket_time) AS last_time
FROM CG_mnq_forward_price_ranges
FORMAT Pretty;

-- Step 2: Join interactions with forward price ranges (simple equality join)
DROP TABLE IF EXISTS CG_mnq_wall_interactions_response_v1;

CREATE TABLE CG_mnq_wall_interactions_response_v1
ENGINE = MergeTree
PARTITION BY toDate(interaction_time)
ORDER BY (interaction_time, wall_id)
AS
SELECT
    -- All original columns
    i.*,

    -- Forward price extremes from pre-computed ranges
    f.max_price_10s AS future_high_10s,
    f.min_price_10s AS future_low_10s,
    f.max_price_30s AS future_high_30s,
    f.min_price_30s AS future_low_30s,

    -- MFE/MAE calculations (10s)
    round((f.max_price_10s - i.price_at_interaction) / 0.25) AS mfe_ticks_10s,
    round((f.min_price_10s - i.price_at_interaction) / 0.25) AS mae_ticks_10s,

    -- MFE/MAE calculations (30s)
    round((f.max_price_30s - i.price_at_interaction) / 0.25) AS mfe_ticks_30s,
    round((f.min_price_30s - i.price_at_interaction) / 0.25) AS mae_ticks_30s,

    -- Basic outcome classification
    CASE
        -- BID wall: price bounced up (support held)
        WHEN i.`w.wall_side` = 'BID'
             AND round((f.max_price_10s - i.price_at_interaction) / 0.25) > 3
             AND round((f.min_price_10s - i.price_at_interaction) / 0.25) > -3
        THEN 'REJECT_SUPPORT'

        -- BID wall: price broke down (support failed)
        WHEN i.`w.wall_side` = 'BID'
             AND round((f.min_price_10s - i.price_at_interaction) / 0.25) < -5
        THEN 'BREAK_SUPPORT'

        -- ASK wall: price bounced down (resistance held)
        WHEN i.`w.wall_side` = 'ASK'
             AND round((f.min_price_10s - i.price_at_interaction) / 0.25) < -3
             AND round((f.max_price_10s - i.price_at_interaction) / 0.25) < 3
        THEN 'REJECT_RESISTANCE'

        -- ASK wall: price broke up (resistance failed)
        WHEN i.`w.wall_side` = 'ASK'
             AND round((f.max_price_10s - i.price_at_interaction) / 0.25) > 5
        THEN 'BREAK_RESISTANCE'

        -- Price didn't move much
        WHEN abs(round((f.max_price_10s - i.price_at_interaction) / 0.25)) < 3
             AND abs(round((f.min_price_10s - i.price_at_interaction) / 0.25)) < 3
        THEN 'CHOP_NO_MOVE'

        ELSE 'UNCLEAR'
    END AS outcome_basic

FROM CG_mnq_wall_interactions i
INNER JOIN CG_mnq_forward_price_ranges f
    ON i.interaction_time = f.bucket_time
ORDER BY interaction_time;

-- ============================================================================
-- VERIFICATION
-- ============================================================================

SELECT '=== Response Enrichment Complete (FAST) ===' AS report FORMAT Pretty;

SELECT
    count() AS total_interactions,
    min(interaction_time) AS first_interaction,
    max(interaction_time) AS last_interaction,
    countIf(outcome_basic != '') AS with_outcome
FROM CG_mnq_wall_interactions_response_v1
FORMAT Pretty;

SELECT '=== Outcome Distribution ===' AS report FORMAT Pretty;

SELECT
    outcome_basic,
    count() AS interactions,
    round(count() * 100.0 / (SELECT count() FROM CG_mnq_wall_interactions_response_v1), 2) AS pct,
    round(avg(mfe_ticks_10s), 2) AS avg_mfe_10s,
    round(avg(mae_ticks_10s), 2) AS avg_mae_10s
FROM CG_mnq_wall_interactions_response_v1
GROUP BY outcome_basic
ORDER BY interactions DESC
FORMAT Pretty;

SELECT '=== Wall Behavior × Outcome ===' AS report FORMAT Pretty;

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
LIMIT 15
FORMAT Pretty;
