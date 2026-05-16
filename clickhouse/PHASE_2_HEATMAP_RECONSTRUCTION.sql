-- ============================================================================
-- Phase 2: Heatmap Reconstruction - 100ms Liquidity Snapshots
-- ============================================================================
-- Purpose: Reconstruct Bookmap-style heatmap showing resting liquidity by price level
-- Source: CG_mnq_mbo_events (normalized MBO events)
-- Output: CG_mnq_heatmap_100ms
--
-- This table answers: "What was the order book structure at each moment?"
--
-- Methodology:
--   - Bucket events into 100ms windows
--   - Track resting liquidity by price level
--   - Measure adds, cancels, fills, and net changes
--   - Separate bid vs ask side activity
--
-- Date: 2026-05-03
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_heatmap_100ms;

CREATE TABLE CG_mnq_heatmap_100ms
ENGINE = MergeTree
PARTITION BY toDate(bucket_time)
ORDER BY (bucket_time, price)
AS
SELECT
    -- Time bucket (100ms)
    toStartOfInterval(ts_event, INTERVAL 100 MILLISECOND) AS bucket_time,
    toDateTime(toStartOfInterval(ts_event, INTERVAL 100 MILLISECOND), 'America/New_York') AS bucket_et,
    toDate(ts_event) AS trade_date,

    -- Price level
    price,
    price_tick,

    -- BID side metrics
    sumIf(size, is_bid_event AND is_add) AS bid_add_size,
    sumIf(size, is_bid_event AND is_cancel) AS bid_cancel_size,
    sumIf(size, is_bid_event AND is_fill) AS bid_fill_size,
    sumIf(size, is_bid_event AND is_modify) AS bid_modify_size,

    -- ASK side metrics
    sumIf(size, is_ask_event AND is_add) AS ask_add_size,
    sumIf(size, is_ask_event AND is_cancel) AS ask_cancel_size,
    sumIf(size, is_ask_event AND is_fill) AS ask_fill_size,
    sumIf(size, is_ask_event AND is_modify) AS ask_modify_size,

    -- Net changes (adds - cancels - fills)
    (bid_add_size - bid_cancel_size - bid_fill_size) AS net_bid_change,
    (ask_add_size - ask_cancel_size - ask_fill_size) AS net_ask_change,

    -- Total activity at this price level
    sum(size) AS total_size,
    count() AS event_count,

    -- Resting liquidity snapshot (cumulative)
    -- Note: This is a simplified version. True resting size requires full order book reconstruction.
    -- For now, we use net_change as proxy for liquidity delta at this level.
    greatest(0, net_bid_change) AS bid_resting_delta,
    greatest(0, net_ask_change) AS ask_resting_delta,

    -- Metadata
    min(sequence) AS first_sequence,
    max(sequence) AS last_sequence

FROM CG_mnq_mbo_events
WHERE symbol LIKE 'MNQZ5%'  -- Focus on December 2025 contract (primary during Sept-Oct)
GROUP BY
    bucket_time,
    bucket_et,
    trade_date,
    price,
    price_tick
ORDER BY
    bucket_time,
    price;

-- ============================================================================
-- Add cumulative resting liquidity (running sum of deltas)
-- ============================================================================

-- Note: This requires window functions over price levels within each time bucket.
-- ClickHouse window functions have limitations, so we'll compute this in Phase 3
-- when we detect walls. For now, we have the delta snapshots.

-- ============================================================================
-- VERIFICATION
-- ============================================================================

SELECT '=== CG_mnq_heatmap_100ms Summary ===' AS report FORMAT Pretty;

SELECT
    count() AS total_buckets,
    countDistinct(bucket_time) AS unique_times,
    countDistinct(price) AS unique_prices,
    min(bucket_time) AS first_bucket,
    max(bucket_time) AS last_bucket,
    formatReadableSize(sum(total_size) * 8) AS estimated_memory
FROM CG_mnq_heatmap_100ms
FORMAT Pretty;

SELECT '=== Liquidity Activity Summary ===' AS report FORMAT Pretty;

SELECT
    round(sum(bid_add_size), 0) AS total_bid_adds,
    round(sum(ask_add_size), 0) AS total_ask_adds,
    round(sum(bid_cancel_size), 0) AS total_bid_cancels,
    round(sum(ask_cancel_size), 0) AS total_ask_cancels,
    round(sum(bid_fill_size), 0) AS total_bid_fills,
    round(sum(ask_fill_size), 0) AS total_ask_fills,
    round(sum(net_bid_change), 0) AS total_net_bid_change,
    round(sum(net_ask_change), 0) AS total_net_ask_change
FROM CG_mnq_heatmap_100ms
FORMAT Pretty;

SELECT '=== Price Level Activity (Top 20) ===' AS report FORMAT Pretty;

SELECT
    price,
    count() AS buckets,
    formatReadableQuantity(sum(total_size)) AS total_activity,
    round(sum(bid_add_size), 0) AS bid_adds,
    round(sum(ask_add_size), 0) AS ask_adds,
    round(sum(bid_cancel_size), 0) AS bid_cancels,
    round(sum(ask_cancel_size), 0) AS ask_cancels
FROM CG_mnq_heatmap_100ms
GROUP BY price
ORDER BY total_activity DESC
LIMIT 20
FORMAT Pretty;

SELECT '=== Temporal Distribution ===' AS report FORMAT Pretty;

SELECT
    toDate(bucket_time) AS date,
    count() AS buckets,
    countDistinct(price) AS price_levels,
    formatReadableQuantity(sum(total_size)) AS total_activity,
    round(sum(bid_add_size), 0) AS bid_adds,
    round(sum(ask_add_size), 0) AS ask_adds
FROM CG_mnq_heatmap_100ms
GROUP BY date
ORDER BY date
FORMAT Pretty;

-- ============================================================================
-- NEXT STEP: Phase 3 - Wall Detection (P99 liquidity levels)
-- ============================================================================
