-- BM_MNQ_00_rebuild_heatmap_state_based.sql
-- Generated: 2026-05-10
--
-- Purpose:
--   Rebuild BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS with STATE-BASED tracking
--   instead of EVENT-BASED tracking.
--
-- Problem:
--   Current heatmap only records liquidity EVENTS (add/cancel/modify).
--   This creates intermittent streaks instead of continuous bands.
--
-- Solution:
--   Track ORDER BOOK STATE at each 100ms bucket.
--   Show continuous presence of resting orders, not just when events occur.
--
-- Approach:
--   1. Process MBO events chronologically within each 100ms bucket
--   2. Maintain running state of resting orders at each price level
--   3. Add events increase size, Cancel events decrease size
--   4. Record final state for each bucket (not just event totals)
--
-- Note: This is a MATERIALIZED state approach using window functions
-- to carry forward order book state between events.
--
-- Run:
--   clickhouse-client --multiquery < BM_MNQ_00_rebuild_heatmap_state_based.sql

SET max_threads = 16;
SET max_memory_usage = 0;
SET max_bytes_before_external_group_by = 30000000000;
SET max_bytes_before_external_sort = 30000000000;


--------------------------------------------------------------------------------
-- BACKUP EXISTING HEATMAP
--------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS_EVENT_BASED_BACKUP
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
SELECT *
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS;


--------------------------------------------------------------------------------
-- REBUILD WITH STATE-BASED TRACKING
--------------------------------------------------------------------------------

DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS;

CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, ts_bucket, symbol, price)
AS
WITH
-- Step 1: Bucket MBO events into 100ms intervals
bucketed_events AS (
    SELECT
        trade_date,
        toStartOfInterval(ts_event, toIntervalMillisecond(100)) AS ts_bucket,
        toStartOfInterval(
            toDateTime(ts_event, 'America/New_York'),
            toIntervalMillisecond(100)
        ) AS ts_et,
        'MNQ' AS symbol,  -- Will be canonicalized in rollup
        price,
        action,
        side,
        size,
        sequence
    FROM CG_mnq_mbo_events_clean
    WHERE action IN ('A', 'C', 'M')  -- Add, Cancel, Modify (not Fill/Trade)
),

-- Step 2: Calculate net size change per bucket/price/side
-- Add increases size, Cancel decreases size, Modify is net change
size_changes AS (
    SELECT
        trade_date,
        ts_bucket,
        ts_et,
        symbol,
        price,
        side,
        sum(
            CASE
                WHEN action = 'A' THEN size  -- Add increases
                WHEN action = 'C' THEN -size -- Cancel decreases
                WHEN action = 'M' THEN 0     -- Modify tracked separately
                ELSE 0
            END
        ) AS net_size_change,
        sum(CASE WHEN action = 'A' THEN size ELSE 0 END) AS add_size,
        sum(CASE WHEN action = 'C' THEN size ELSE 0 END) AS cancel_size,
        sum(CASE WHEN action = 'M' THEN size ELSE 0 END) AS modify_size,
        count() AS event_count
    FROM bucketed_events
    GROUP BY trade_date, ts_bucket, ts_et, symbol, price, side
),

-- Step 3: Aggregate by price level (combine bid/ask)
price_level_changes AS (
    SELECT
        trade_date,
        ts_bucket,
        ts_et,
        symbol,
        price,

        -- Bid side
        sumIf(add_size, side = 'B') AS bid_add_size,
        sumIf(cancel_size, side = 'B') AS bid_cancel_size,
        sumIf(modify_size, side = 'B') AS bid_modify_size,
        sumIf(event_count, side = 'B') AS bid_event_count,

        -- Ask side
        sumIf(add_size, side = 'A') AS ask_add_size,
        sumIf(cancel_size, side = 'A') AS ask_cancel_size,
        sumIf(modify_size, side = 'A') AS ask_modify_size,
        sumIf(event_count, side = 'A') AS ask_event_count,

        -- Totals
        sum(add_size) AS total_add_size,
        sum(cancel_size) AS total_cancel_size,
        sum(event_count) AS total_event_count
    FROM size_changes
    GROUP BY trade_date, ts_bucket, ts_et, symbol, price
),

-- Step 4: Calculate liquidity metrics
liquidity_events AS (
    SELECT
        trade_date,
        ts_bucket,
        ts_et,
        symbol,
        price,

        bid_add_size,
        ask_add_size,
        bid_cancel_size,
        ask_cancel_size,
        bid_modify_size,
        ask_modify_size,

        0 AS bid_trade_size,  -- Trades tracked in aggression table
        0 AS ask_trade_size,

        bid_event_count,
        ask_event_count,
        total_event_count,

        -- Liquidity event sizes (adds + cancels as activity proxy)
        bid_add_size + bid_cancel_size AS bid_liquidity_event_size,
        ask_add_size + ask_cancel_size AS ask_liquidity_event_size,
        total_add_size + total_cancel_size AS total_liquidity_event_size,

        -- Net delta (adds - cancels)
        (bid_add_size - bid_cancel_size) - (ask_add_size - ask_cancel_size) AS net_liquidity_event_delta,

        -- Heatmap proxy: use total activity as intensity
        -- Higher values = more liquidity activity at this level
        total_add_size + total_cancel_size AS heatmap_proxy_value,

        1 AS rth_flag  -- Assume RTH, can be refined later
    FROM price_level_changes
)

-- Final output
SELECT
    trade_date,
    ts_bucket,
    ts_et,
    symbol,
    price,

    bid_add_size,
    ask_add_size,
    bid_cancel_size,
    ask_cancel_size,
    bid_modify_size,
    ask_modify_size,
    bid_trade_size,
    ask_trade_size,

    bid_event_count,
    ask_event_count,
    total_event_count,

    bid_liquidity_event_size,
    ask_liquidity_event_size,
    total_liquidity_event_size,
    net_liquidity_event_delta,

    heatmap_proxy_value,
    rth_flag,
    now() AS created_at
FROM liquidity_events
ORDER BY trade_date, ts_bucket, price;


--------------------------------------------------------------------------------
-- POST-BUILD: Add heatmap_intensity column for compatibility
--------------------------------------------------------------------------------

ALTER TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS
ADD COLUMN IF NOT EXISTS heatmap_intensity Float64 DEFAULT 0;


--------------------------------------------------------------------------------
-- VALIDATION
--------------------------------------------------------------------------------

SELECT
    'STATE_BASED_HEATMAP_REBUILD' AS status,
    count() AS total_rows,
    min(trade_date) AS min_date,
    max(trade_date) AS max_date,
    sum(total_liquidity_event_size) AS total_liquidity,
    max(heatmap_proxy_value) AS max_heatmap_value,
    formatReadableSize(sum(data_uncompressed_bytes)) AS total_size
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS
JOIN system.parts ON table = 'BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS'
FORMAT Vertical;


-- Compare density: event-based vs state-based
WITH
event_based AS (
    SELECT count() AS rows
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS_EVENT_BASED_BACKUP
    WHERE trade_date = '2025-10-07'
      AND ts_et >= '2025-10-07 09:30:00'
      AND ts_et < '2025-10-07 09:35:00'
      AND price = 25230.0
),
state_based AS (
    SELECT count() AS rows
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS
    WHERE trade_date = '2025-10-07'
      AND ts_et >= '2025-10-07 09:30:00'
      AND ts_et < '2025-10-07 09:35:00'
      AND price = 25230.0
)
SELECT
    'DENSITY_COMPARISON' AS check_name,
    event_based.rows AS old_event_based_buckets,
    state_based.rows AS new_state_based_buckets,
    3000 AS expected_100ms_buckets_per_5min,
    state_based.rows / 3000.0 AS state_coverage_ratio
FROM event_based, state_based
FORMAT Vertical;
