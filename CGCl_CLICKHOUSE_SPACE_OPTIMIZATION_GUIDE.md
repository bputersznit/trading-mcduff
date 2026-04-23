# ClickHouse Space Optimization Guide

## Summary

**Current Space Usage:** 37.29 GiB
**Potential Savings:** ~140 MiB from view conversions + dropping empty tables
**Empty Tables:** 19 tables consuming 0 bytes
**View Candidates:** 15 tables consuming ~140 MiB

## Quick Wins

### 1. Drop Empty Tables (Immediate)

**Space saved:** Minimal (metadata only)
**Risk:** Very low

```bash
# Dry run first
python3 CGCl_execute_drop_empty_tables.py --dry-run

# Execute
python3 CGCl_execute_drop_empty_tables.py --yes
```

Or manually review and execute:
```sql
-- See: CGCl_drop_empty_tables.sql
-- Uncomment lines to drop specific tables
```

### 2. Replace Materialized Views with Regular Views

**Space saved:** Depends on target table size
**Risk:** Medium (check query performance first)

Current materialized views:
- `bookmap_1m_mv` → aggregates from `bookmap_1s`
- `bookmap_5m_mv` → aggregates from `bookmap_1m`

#### Migration Strategy

```sql
-- Step 1: Create regular view with same logic
CREATE VIEW bookmap_1m_view AS
SELECT
    toStartOfMinute(time_bucket) AS time_bucket,
    symbol, price,
    avgOrNull(bid_depth) AS bid_depth,
    avgOrNull(ask_depth) AS ask_depth,
    -- ... (same as materialized view SELECT)
FROM bookmap_1s
GROUP BY toStartOfMinute(time_bucket), symbol, price;

-- Step 2: Test query performance
SELECT * FROM bookmap_1m_view WHERE time_bucket > now() - INTERVAL 1 HOUR LIMIT 100;

-- Step 3: If performance is acceptable, drop materialized view and rename
DROP TABLE bookmap_1m_mv;
DROP TABLE bookmap_1m;  -- Drop the target table too
RENAME TABLE bookmap_1m_view TO bookmap_1m;
```

## Understanding Views vs Materialized Views

### Regular Views (No Disk Space)

**Pros:**
- Zero disk space
- Always up-to-date (computed on-the-fly)
- No maintenance needed
- Perfect for: read-heavy analytical queries, complex joins, rarely-queried data

**Cons:**
- Query latency (recomputes each time)
- CPU/memory overhead per query
- Not suitable for: frequently-queried aggregations, dashboards, hot paths

**When to use:**
- Derived tables with simple transformations
- Infrequently accessed analytical queries
- Small result sets
- When source data changes frequently

### Materialized Views (Uses Disk Space)

**Pros:**
- Fast query performance (pre-computed)
- Efficient for: dashboards, frequently-queried aggregations
- Automatically updated as source data arrives

**Cons:**
- Consumes disk space (both view and target table)
- Slightly increased write latency
- Maintenance overhead

**When to use:**
- Frequently-queried aggregations
- Dashboards and real-time monitoring
- Complex computations that are expensive to repeat
- Time-series aggregations (1m, 5m, 1h bars)

## Conversion Candidates Analysis

### High Priority (Convert to Views)

These tables are small and likely analytical:

```sql
-- Regime detection tables (multiple versions, backup copies)
-- Consider keeping only ONE version as MergeTree, rest as views
mnq_regime_5s_v2         (21.55 MiB)  -- BACKUP, convert to view
mnq_regime_5s_v3         (21.55 MiB)  -- BACKUP, convert to view
mnq_regime_5s_v3_backup  (20.51 MiB)  -- BACKUP, convert to view
mnq_regime_5s_hyst       (15.72 MiB)  -- Hysteresis version, probably unused
mnq_regime_5s_sticky     (4.16 MiB)   -- Sticky version, probably unused

-- Daily aggregations (tiny tables, rarely queried)
CG_mnq_opening_range_15m (1.59 KiB)   -- Convert to view
CG_mnq_daily_atr         (1.55 KiB)   -- Convert to view
CG_mnq_daily_tr          (1.51 KiB)   -- Convert to view
CG_mnq_daily_atr_prior   (659 B)      -- Convert to view

-- Tiny parameter tables
mnq_params_by_regime     (1.22 KiB)   -- Convert to view or drop
mnq_doubling_runs        (1.11 KiB)   -- Convert to view or drop
mnq_features_1s_days     (561 B)      -- Convert to view
```

### Medium Priority (Evaluate Performance)

```sql
-- Features tables (might be queried frequently)
mnq_features_5s          (36.08 MiB)  -- TEST query performance first

-- Active regime detection
mnq_regime_5s            (21.52 MiB)  -- KEEP if used in production

-- Wyckoff phases
mnq_wyckoff_phases       (58.83 KiB)  -- Convert if rarely queried
```

## Space-Saving Strategy

### Phase 1: Immediate Cleanup (5 minutes)

1. Drop all empty tables
   ```bash
   python3 CGCl_execute_drop_empty_tables.py --yes
   ```

2. Drop backup/duplicate regime tables
   ```sql
   DROP TABLE IF EXISTS mnq_regime_5s_v2;
   DROP TABLE IF EXISTS mnq_regime_5s_v3;
   DROP TABLE IF EXISTS mnq_regime_5s_v3_backup;
   DROP TABLE IF EXISTS mnq_regime_5s_hyst;       -- if unused
   DROP TABLE IF EXISTS mnq_regime_5s_sticky;     -- if unused
   ```

### Phase 2: View Conversion (15 minutes)

1. Identify the CREATE queries for tables you want to convert
2. Replace `CREATE TABLE` with `CREATE VIEW`
3. Test query performance
4. If acceptable, drop the table

Example:
```sql
-- Get original creation query
SHOW CREATE TABLE CG_mnq_daily_atr;

-- Create view version (modify INSERT INTO to SELECT)
CREATE VIEW CG_mnq_daily_atr_view AS
SELECT
    date,
    avgPrice,
    atr_14
FROM (
    -- Paste the SELECT logic here
);

-- Test
SELECT * FROM CG_mnq_daily_atr_view LIMIT 10;

-- If OK, replace
DROP TABLE CG_mnq_daily_atr;
RENAME TABLE CG_mnq_daily_atr_view TO CG_mnq_daily_atr;
```

### Phase 3: Evaluate Larger Tables (30 minutes)

Check if these could be views by testing query performance:

```sql
-- Test computing mnq_features_5s on-the-fly
CREATE VIEW mnq_features_5s_view AS
SELECT
    -- paste SELECT logic from SHOW CREATE TABLE
FROM ...;

-- Benchmark
SELECT * FROM mnq_features_5s WHERE ts_event > now() - INTERVAL 1 HOUR;  -- Original
SELECT * FROM mnq_features_5s_view WHERE ts_event > now() - INTERVAL 1 HOUR;  -- View

-- If view performance is acceptable (<1-2 second difference), convert
```

## Best Practices

### DO:
✅ Always backup before dropping tables
✅ Test view performance before converting
✅ Keep source tables (mnq_mbo, etc.) as MergeTree
✅ Convert rarely-queried analytical tables to views
✅ Drop backup/duplicate tables
✅ Use regular views for simple transformations

### DON'T:
❌ Convert frequently-queried tables to views
❌ Convert source/ingestion tables to views
❌ Drop tables without checking dependencies
❌ Convert write-heavy tables to views
❌ Forget to test query performance

## Recommended Architecture

### Keep as MergeTree (Essential Data)
- `mnq_mbo` (17.83 GiB) - source data
- `futures_continuous_mbo` (10.85 GiB) - source data
- `nq_continuous_mbo` (4.46 GiB) - source data
- `mnq_l1_features_rolling` (1.85 GiB) - hot path features
- `nt8_mnq_trades` (435 MiB) - trade log
- `mnq_trades` (251 MiB) - trade log

### Convert to Views (Analytical)
- All `_v2`, `_v3`, `_backup` suffix tables
- Daily aggregation tables (ATR, TR, opening range)
- Regime detection alternatives (keep one as MergeTree)
- Small parameter/config tables

### Replace Materialized Views with Views
- `bookmap_1m_mv` - if query performance is acceptable
- `bookmap_5m_mv` - if query performance is acceptable

## Monitoring Space Usage

```sql
-- Check total space by table
SELECT
    table,
    engine,
    formatReadableSize(total_bytes) as size,
    total_rows
FROM system.tables
WHERE database = 'default'
  AND engine NOT IN ('View')
ORDER BY total_bytes DESC
LIMIT 20;

-- Check partition sizes for large tables
SELECT
    partition,
    formatReadableSize(sum(bytes_on_disk)) as size
FROM system.parts
WHERE table = 'mnq_mbo' AND active = 1
GROUP BY partition
ORDER BY partition DESC
LIMIT 10;
```

## Next Steps

1. **Run the optimizer** to get latest report:
   ```bash
   python3 CGCl_clickhouse_space_optimizer.py
   ```

2. **Drop empty tables**:
   ```bash
   python3 CGCl_execute_drop_empty_tables.py --yes
   ```

3. **Review generated SQL files**:
   - `CGCl_drop_empty_tables.sql`
   - `CGCl_view_conversion_candidates.sql`
   - `CGCl_materialized_view_analysis.sql`

4. **Test and convert** high-priority candidates

5. **Monitor** space usage over time

---

**Remember:** Always test query performance before converting tables to views!
