-- ============================================================================
-- Phase 1 Quick Optimizations for v5 SQL Chain
-- ============================================================================
-- Expected speedup: 3-4x (750s → 200-250s)
-- Time to implement: ~1 hour
-- Safe: Creates new tables without dropping originals
-- ============================================================================

-- ============================================================================
-- OPT 1: Optimize Step 1 - Use PREWHERE for Symbol Filter
-- ============================================================================
-- OLD: 383 seconds
-- NEW: ~200 seconds (1.9x faster)
--
-- PREWHERE reads only the symbol column first, then fetches other columns
-- only for matching rows. This is a huge win when filtering 50GB of data.
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
PREWHERE symbol = 'MNQZ5';  -- ✅ PREWHERE instead of WHERE


-- ============================================================================
-- OPT 2: Optimize Features Table - Reduce Index Granularity
-- ============================================================================
-- Smaller granularity = more index entries = faster time-range lookups
-- This helps Step 9 (exit simulation) which does range scans
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_features_100ms_clean_opt;

CREATE TABLE CG_mnq_features_100ms_clean_opt
ENGINE = MergeTree
ORDER BY ts_bucket
SETTINGS index_granularity = 1024  -- ✅ Was 8192, now 1024 (8x more index entries)
AS
SELECT * FROM CG_mnq_features_100ms_clean;


-- ============================================================================
-- OPT 3: Add Materialized Date Column for Fast Date Filtering
-- ============================================================================
-- Avoid repeated timezone conversions in Steps 11-15
-- ============================================================================

ALTER TABLE CG_mnq_features_100ms_clean_opt
ADD COLUMN trade_date Date MATERIALIZED toDate(toTimeZone(ts_bucket, 'America/New_York'));


-- ============================================================================
-- OPT 4: Use LowCardinality for Signal/Side/Outcome Strings
-- ============================================================================
-- These columns have only 2-3 unique values but millions of rows
-- LowCardinality stores a dictionary (like an enum) instead of full strings
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
        'LowCardinality(String)'  -- ✅ LowCardinality for 3-value column
    ) AS signal
FROM CG_mnq_features_100ms;


-- ============================================================================
-- OPT 5: Optimize Trade Candidates with LowCardinality Side
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_trade_candidates_100ms_opt;

CREATE TABLE CG_mnq_trade_candidates_100ms_opt
ENGINE = MergeTree
ORDER BY entry_time
AS
SELECT
    entry_time,
    CAST(side, 'LowCardinality(String)') AS side,  -- ✅ LowCardinality
    entry_price,
    target_price,
    stop_price
FROM CG_mnq_trade_candidates_100ms;


-- ============================================================================
-- VALIDATION: Compare Row Counts
-- ============================================================================
-- Run these to ensure optimized tables match originals
-- ============================================================================

SELECT 'mbo_events' AS table_name,
       (SELECT count() FROM CG_mnq_mbo_events) AS original,
       (SELECT count() FROM CG_mnq_mbo_events_opt) AS optimized,
       if(original = optimized, '✅ MATCH', '❌ MISMATCH') AS status
UNION ALL
SELECT 'features_clean' AS table_name,
       (SELECT count() FROM CG_mnq_features_100ms_clean) AS original,
       (SELECT count() FROM CG_mnq_features_100ms_clean_opt) AS optimized,
       if(original = optimized, '✅ MATCH', '❌ MISMATCH') AS status
UNION ALL
SELECT 'signals' AS table_name,
       (SELECT count() FROM CG_mnq_signals_100ms) AS original,
       (SELECT count() FROM CG_mnq_signals_100ms_opt) AS optimized,
       if(original = optimized, '✅ MATCH', '❌ MISMATCH') AS status;


-- ============================================================================
-- BENCHMARK: Test Exit Simulation Performance
-- ============================================================================
-- Run Step 9 with optimized features table and compare timing
-- ============================================================================

-- ⏱️ Benchmark with ORIGINAL features table
-- EXPECTED: ~351 seconds
/*
SET max_execution_time = 600;

SELECT count(*) FROM CG_mnq_trade_candidates_100ms AS t
INNER JOIN CG_mnq_features_100ms_clean AS f
    ON f.ts_bucket > t.entry_time
   AND f.ts_bucket <= t.entry_time + INTERVAL 10 MINUTE;
*/

-- ⏱️ Benchmark with OPTIMIZED features table
-- EXPECTED: ~180-220 seconds (1.5-2x faster)
/*
SET max_execution_time = 600;

SELECT count(*) FROM CG_mnq_trade_candidates_100ms_opt AS t
INNER JOIN CG_mnq_features_100ms_clean_opt AS f
    ON f.ts_bucket > t.entry_time
   AND f.ts_bucket <= t.entry_time + INTERVAL 10 MINUTE;
*/


-- ============================================================================
-- STORAGE COMPARISON
-- ============================================================================

SELECT
    table AS table_name,
    formatReadableSize(sum(bytes_on_disk)) AS disk_size,
    formatReadableSize(sum(data_compressed_bytes)) AS compressed_size,
    formatReadableSize(sum(data_uncompressed_bytes)) AS uncompressed_size,
    round(sum(data_compressed_bytes) / sum(data_uncompressed_bytes) * 100, 1) AS compression_ratio_pct
FROM system.parts
WHERE table IN ('CG_mnq_features_100ms_clean', 'CG_mnq_features_100ms_clean_opt')
  AND active
GROUP BY table
ORDER BY table;


-- ============================================================================
-- NEXT STEPS
-- ============================================================================
--
-- 1. Run this script: clickhouse-client < optimize_v5_chain_phase1.sql
-- 2. Validate row counts match
-- 3. Benchmark Step 9 JOIN performance
-- 4. If satisfied, update v5 chain to use _opt tables:
--    - Replace CG_mnq_mbo_events → CG_mnq_mbo_events_opt
--    - Replace CG_mnq_features_100ms_clean → CG_mnq_features_100ms_clean_opt
--    - Replace CG_mnq_signals_100ms → CG_mnq_signals_100ms_opt
-- 5. Re-run full v5 chain and measure total runtime
--
-- Expected Result: 750s → 200-250s (3-4x speedup)
--
-- ============================================================================
