# ClickHouse Backtest Optimization Guide

## Current Performance Analysis

**Total Runtime**: 750 seconds (12.5 minutes)
**Data Flow**: 789M MBO events → 908 final trades

### Bottlenecks Identified

1. **STEP 1** - MBO Filtering: **383 seconds (51%)**
2. **STEP 9** - Exit Simulation: **351 seconds (47%)**
3. All other steps: **16 seconds (2%)**

---

## Optimization Strategies

### 🚀 HIGH IMPACT (3-5x speedup possible)

#### 1. **Partition Source Table by Symbol and Date**

**Problem**: Step 1 scans 50GB to filter one symbol (MNQZ5)

**Solution**: Partition mnq_mbo by symbol
```sql
CREATE TABLE mnq_mbo_optimized
ENGINE = MergeTree
PARTITION BY symbol  -- Isolates MNQZ5 data physically
ORDER BY (ts_event, sequence)
SETTINGS index_granularity = 8192
AS SELECT * FROM mnq_mbo;
```

**Expected Impact**: Step 1 drops from 383s → **50-60s** (6-7x faster)
**Why**: ClickHouse skips entire partitions, reads only MNQZ5 data

---

#### 2. **Use PREWHERE Instead of WHERE**

**Problem**: Step 1 WHERE clause evaluated after reading columns

**Solution**: Replace Step 1 with PREWHERE
```sql
CREATE TABLE CG_mnq_mbo_events
ENGINE = MergeTree
ORDER BY (ts_event, sequence)
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
PREWHERE symbol = 'MNQZ5';  -- Filters BEFORE reading other columns
```

**Expected Impact**: Step 1 drops from 383s → **200-250s** (1.5-2x faster)
**Why**: PREWHERE reads only `symbol` column first, skips rows early

---

#### 3. **Optimize Exit Simulation Join (Step 9)**

**Problem**: Cross-join between trade_candidates and 6.07M features with time range filter

**Current Query**:
```sql
FROM CG_mnq_trade_candidates_100ms AS t
INNER JOIN CG_mnq_features_100ms_clean AS f
    ON f.ts_bucket > t.entry_time
   AND f.ts_bucket <= t.entry_time + INTERVAL 10 MINUTE
```

**Solution A - Add Secondary Index on features table**:
```sql
-- Recreate features table with skip index
CREATE TABLE CG_mnq_features_100ms_clean_v2
ENGINE = MergeTree
ORDER BY ts_bucket
PRIMARY KEY ts_bucket
SETTINGS index_granularity = 1024  -- Smaller granularity for time-range queries
AS SELECT * FROM CG_mnq_features_100ms_clean;
```

**Expected Impact**: Step 9 drops from 351s → **120-180s** (2-3x faster)

---

**Solution B - Use ASOF JOIN** (if applicable):
```sql
-- If you only need the FIRST matching feature after entry_time
SELECT
    t.*,
    f.best_bid,
    f.best_ask
FROM CG_mnq_trade_candidates_100ms AS t
ASOF LEFT JOIN CG_mnq_features_100ms_clean AS f
    ON t.entry_time >= f.ts_bucket
```

**Expected Impact**: Step 9 drops from 351s → **5-15s** (20-70x faster)
**Limitation**: Only gets nearest match, not all matches in 10-minute window

---

**Solution C - Partition features table by date**:
```sql
CREATE TABLE CG_mnq_features_100ms_clean_partitioned
ENGINE = MergeTree
PARTITION BY toDate(ts_bucket)  -- Partition by day
ORDER BY ts_bucket
SETTINGS index_granularity = 1024
AS SELECT * FROM CG_mnq_features_100ms_clean;
```

**Expected Impact**: Step 9 drops from 351s → **150-200s** (1.7-2.3x faster)
**Why**: Prunes partitions outside trade date range

---

#### 4. **Use Materialized Columns for Repeated Calculations**

**Problem**: Steps 5-15 repeatedly convert timestamps to ET timezone

**Solution**: Add materialized date column to base tables
```sql
-- Alter features table to include date
ALTER TABLE CG_mnq_features_100ms_clean
ADD COLUMN trade_date Date MATERIALIZED toDate(toTimeZone(ts_bucket, 'America/New_York'));

-- Now queries can filter faster
SELECT * FROM CG_mnq_features_100ms_clean
WHERE trade_date = '2025-09-24';  -- Uses materialized column
```

**Expected Impact**: Steps 11-15 combined drop from ~1s → **0.2-0.3s** (3-5x faster)
**Why**: Avoids repeated timezone conversions

---

### ⚡ MEDIUM IMPACT (30-50% speedup)

#### 5. **Reorder Columns by Query Frequency**

**Problem**: Columnar storage reads columns in declared order - frequently accessed columns should be first

**Solution**: Recreate tables with hot columns first
```sql
CREATE TABLE CG_mnq_features_100ms_clean_reordered
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT
    ts_bucket,           -- Always needed (ORDER BY)
    best_bid,            -- Used in exit simulation
    best_ask,            -- Used in exit simulation
    total_event_size,    -- Used in slippage model
    event_delta,         -- Used in signal generation
    event_imbalance,     -- Used in signal generation
    spread,              -- Used in filters
    bid_event_size,
    ask_event_size,
    bid_events,
    ask_events,
    event_count_delta
FROM CG_mnq_features_100ms_clean;
```

**Expected Impact**: 10-20% faster reads across Steps 5-15

---

#### 6. **Use LowCardinality for String Columns**

**Problem**: `signal`, `side`, `outcome` columns store repetitive strings

**Solution**: Mark as LowCardinality
```sql
CREATE TABLE CG_mnq_signals_100ms_lc
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT
    *,
    CAST(
        CASE
            WHEN event_delta > 50 AND event_imbalance > 0.60 THEN 'LONG'
            WHEN event_delta < -50 AND event_imbalance < -0.60 THEN 'SHORT'
            ELSE 'NONE'
        END,
        'LowCardinality(String)'
    ) AS signal
FROM CG_mnq_features_100ms;
```

**Expected Impact**: 5-15% faster, 20-30% less storage for signal tables

---

#### 7. **Drop Intermediate Tables After Use**

**Problem**: 12.6M row tables consume RAM and disk space unnecessarily

**Solution**: Add cleanup to pipeline
```sql
-- After Step 5 (signals) is done, drop features table
DROP TABLE IF EXISTS CG_mnq_features_100ms;

-- After Step 9 (exits) is done, drop signals table
DROP TABLE IF EXISTS CG_mnq_signals_100ms;
```

**Expected Impact**: Reduces memory pressure, allows CH to allocate more RAM to active queries

---

### 🔧 LOW IMPACT (5-15% speedup)

#### 8. **Use Codec Compression for Large Numeric Columns**

```sql
CREATE TABLE CG_mnq_features_100ms_compressed
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT
    ts_bucket,
    best_bid           CODEC(Delta, ZSTD(3)),  -- Price changes slowly
    best_ask           CODEC(Delta, ZSTD(3)),
    spread             CODEC(ZSTD(3)),
    bid_event_size     CODEC(DoubleDelta, ZSTD(3)),  -- Volume fluctuates
    ask_event_size     CODEC(DoubleDelta, ZSTD(3)),
    total_event_size   CODEC(DoubleDelta, ZSTD(3))
FROM CG_mnq_features_100ms_clean;
```

**Expected Impact**: 20-40% smaller on disk, 5-10% faster reads (less I/O)

---

#### 9. **Increase max_threads for Heavy Queries**

```sql
-- Run Step 9 (exit simulation) with more parallelism
SET max_threads = 16;  -- Default is often 8

CREATE TABLE CG_mnq_trade_results_100ms
ENGINE = MergeTree
ORDER BY entry_time
AS
SELECT ...
```

**Expected Impact**: 10-20% faster on multi-core systems (scales with cores)

---

#### 10. **Use ReplacingMergeTree to Auto-Deduplicate**

**Problem**: Step 6 manually deduplicates signals

**Solution**: Use ReplacingMergeTree engine
```sql
CREATE TABLE CG_mnq_signals_100ms_replacing
ENGINE = ReplacingMergeTree
ORDER BY (ts_bucket, signal)  -- Deduplication key
AS SELECT * FROM CG_mnq_features_100ms;

-- Read only latest version
SELECT * FROM CG_mnq_signals_100ms_replacing FINAL
WHERE signal != 'NONE';
```

**Expected Impact**: Eliminates Step 6 entirely, but `FINAL` has cost

---

## 🎯 Recommended Implementation Plan

### Phase 1: Quick Wins (1 hour, 3-4x speedup)
1. Add PREWHERE to Step 1 (5 min)
2. Reduce index_granularity on features table (10 min)
3. Add trade_date materialized column (15 min)
4. Use LowCardinality for string columns (15 min)

**Expected Result**: 750s → **200-250s** (3-3.75x faster)

---

### Phase 2: Structural Improvements (2-3 hours, 5-7x speedup)
1. Partition mnq_mbo by symbol (45 min including rebuild)
2. Partition features table by date (30 min)
3. Reorder columns by access frequency (30 min)
4. Add cleanup to drop intermediate tables (15 min)

**Expected Result**: 750s → **110-150s** (5-7x faster)

---

### Phase 3: Advanced Optimizations (4-6 hours, 8-12x speedup)
1. Evaluate ASOF JOIN for exit simulation (2 hours - requires logic validation)
2. Add custom codecs for compression (1 hour)
3. Tune max_threads and CH settings (1 hour)
4. Benchmark and profile remaining bottlenecks (2 hours)

**Expected Result**: 750s → **60-90s** (8-12x faster)

---

## 📊 Estimated Final Performance

| Phase | Runtime | Speedup | Effort |
|-------|---------|---------|--------|
| **Current** | 750s (12.5 min) | 1x | - |
| **Phase 1** | 200-250s (3-4 min) | 3-4x | 1 hour |
| **Phase 2** | 110-150s (2-3 min) | 5-7x | 3-4 hours |
| **Phase 3** | 60-90s (1-1.5 min) | 8-12x | 8-10 hours |

---

## 🧪 Testing Strategy

1. **Backup first**: `CREATE TABLE mnq_mbo_backup AS SELECT * FROM mnq_mbo`
2. **Test on subset**: Filter to 1 week of data first
3. **Validate results**: Compare trade counts and P&L against baseline
4. **Benchmark**: Use `EXPLAIN` and `system.query_log` to measure improvements

---

## 🚨 CPU Usage Control

To stay under 75% CPU during backtests:

```sql
-- Limit threads per query
SET max_threads = 6;  -- Adjust based on core count

-- Limit concurrent queries
SET max_concurrent_queries = 2;

-- Throttle I/O
SET max_execution_speed = 1000000000;  -- 1GB/s max
```

---

## Summary

Your SQL chain is already well-optimized. The two main bottlenecks are:
1. **Scanning 50GB for one symbol** (383s) - Fix with partitioning + PREWHERE
2. **Cross-join for exit simulation** (351s) - Fix with smaller index_granularity or ASOF JOIN

With Phase 1 optimizations (1 hour work), you can get to **~3.5 minutes** per full backtest.
With Phase 2 (4 hours total), you can reach **~2 minutes** per full backtest.

The pipeline is **already faster than Python** - these optimizations make it even better.
