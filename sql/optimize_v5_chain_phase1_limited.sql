-- ============================================================================
-- Phase 1 Quick Optimizations - CPU LIMITED VERSION
-- ============================================================================
-- Limits: max_threads=4, execution throttled to stay under 75% CPU
-- ============================================================================

-- Set resource limits
SET max_threads = 4;
SET max_execution_speed = 500000000;  -- 500 MB/s max
SET max_memory_usage = 8000000000;    -- 8 GB max

-- ============================================================================
-- OPT 1: Optimize Step 1 - Use PREWHERE (CPU Limited)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_mbo_events_opt;

CREATE TABLE CG_mnq_mbo_events_opt
ENGINE = MergeTree
ORDER BY (ts_event, sequence)
SETTINGS index_granularity = 8192
AS
SELECT
    ts_event,
    sequence,
    action,
    side,
    price,
    size,
    order_id
FROM mnq_mbo
PREWHERE symbol = 'MNQZ5'
SETTINGS max_threads = 4;


-- ============================================================================
-- OPT 2: Optimize Features Table - Smaller Index Granularity
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_features_100ms_clean_opt;

CREATE TABLE CG_mnq_features_100ms_clean_opt
ENGINE = MergeTree
ORDER BY ts_bucket
SETTINGS index_granularity = 1024
AS
SELECT * FROM CG_mnq_features_100ms_clean
SETTINGS max_threads = 4;


-- ============================================================================
-- OPT 3: Add Materialized Date Column
-- ============================================================================

ALTER TABLE CG_mnq_features_100ms_clean_opt
ADD COLUMN trade_date Date MATERIALIZED toDate(toTimeZone(ts_bucket, 'America/New_York'));


-- ============================================================================
-- OPT 4: Create Signals Table with LowCardinality
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_signals_100ms_opt;

CREATE TABLE CG_mnq_signals_100ms_opt
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT
    ts_bucket,
    best_bid,
    best_ask,
    spread,
    bid_event_size,
    ask_event_size,
    event_delta,
    total_event_size,
    event_imbalance,
    bid_events,
    ask_events,
    event_count_delta,
    CAST(
        CASE
            WHEN event_delta > 50 AND event_imbalance > 0.60 THEN 'LONG'
            WHEN event_delta < -50 AND event_imbalance < -0.60 THEN 'SHORT'
            ELSE 'NONE'
        END,
        'LowCardinality(String)'
    ) AS signal
FROM CG_mnq_features_100ms
SETTINGS max_threads = 4;


-- ============================================================================
-- VALIDATION
-- ============================================================================

SELECT
    'features_clean' AS table_name,
    (SELECT count() FROM CG_mnq_features_100ms_clean) AS original,
    (SELECT count() FROM CG_mnq_features_100ms_clean_opt) AS optimized,
    if(original = optimized, '✅ MATCH', '❌ MISMATCH') AS status
UNION ALL
SELECT
    'signals' AS table_name,
    (SELECT count() FROM CG_mnq_signals_100ms) AS original,
    (SELECT count() FROM CG_mnq_signals_100ms_opt) AS optimized,
    if(original = optimized, '✅ MATCH', '❌ MISMATCH') AS status
FORMAT Pretty;


-- ============================================================================
-- STORAGE COMPARISON
-- ============================================================================

SELECT
    table AS table_name,
    formatReadableSize(sum(bytes_on_disk)) AS disk_size,
    round(sum(data_compressed_bytes) / sum(data_uncompressed_bytes) * 100, 1) AS compression_pct
FROM system.parts
WHERE table IN ('CG_mnq_features_100ms_clean', 'CG_mnq_features_100ms_clean_opt')
  AND active
GROUP BY table
ORDER BY table
FORMAT Pretty;

SELECT '✅ Phase 1 optimizations complete (CPU-limited)' AS status FORMAT Pretty;
