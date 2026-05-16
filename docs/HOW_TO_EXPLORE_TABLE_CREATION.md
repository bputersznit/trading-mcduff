# How to Explore Table Creation in ClickHouse

## Complete Chain from mnq_mbo to v5

```
mnq_mbo (source: all MBO data)
    ↓
CG_mnq_mbo_events (filtered: symbol = 'MNQZ5')
    ↓
CG_mnq_book_proxy_100ms (aggregated to 100ms buckets)
    ↓
CG_mnq_features_100ms (calculated event features)
    ↓
... (continue through all steps)
```

## Basic Commands

### 1. See any table's creation query
```bash
clickhouse-client --query "
SELECT query
FROM system.query_log
WHERE query LIKE 'CREATE TABLE%<table_name>%'
  AND type = 'QueryFinish'
ORDER BY event_time ASC
LIMIT 1
" | sed 's/\\n/\n/g'
```

### 2. See table schema
```bash
clickhouse-client --query "SHOW CREATE TABLE <table_name>"
```

### 3. List all tables matching a pattern
```bash
clickhouse-client --query "SHOW TABLES LIKE '%pattern%'"
```

### 4. See table row count
```bash
clickhouse-client --query "SELECT COUNT(*) FROM <table_name>"
```

### 5. See sample data
```bash
clickhouse-client --query "SELECT * FROM <table_name> LIMIT 10 FORMAT Vertical"
```

### 6. Find all queries mentioning a table
```bash
clickhouse-client --query "
SELECT
    event_time,
    type,
    substring(query, 1, 200) as query_preview
FROM system.query_log
WHERE query LIKE '%<table_name>%'
  AND type = 'QueryFinish'
ORDER BY event_time DESC
LIMIT 10
FORMAT Vertical
"
```

## The Complete v5 Creation Queries

### Step 1: Filter MBO data
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
WHERE symbol = 'MNQZ5';
```

### Step 2: Aggregate to 100ms buckets
```sql
CREATE TABLE CG_mnq_book_proxy_100ms
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT
    toStartOfInterval(ts_event, toIntervalMillisecond(100)) AS ts_bucket,
    maxIf(price, side = 'B') AS best_bid,
    minIf(price, side = 'A') AS best_ask,
    sumIf(size, side = 'B') AS bid_event_size,
    sumIf(size, side = 'A') AS ask_event_size,
    countIf(side = 'B') AS bid_events,
    countIf(side = 'A') AS ask_events
FROM CG_mnq_mbo_events
GROUP BY ts_bucket;
```

### Step 3: Calculate features
```sql
CREATE TABLE CG_mnq_features_100ms
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT
    ts_bucket,
    best_bid,
    best_ask,
    best_ask - best_bid AS spread,
    bid_event_size,
    ask_event_size,
    bid_event_size - ask_event_size AS event_delta,
    bid_event_size + ask_event_size AS total_event_size,
    (bid_event_size - ask_event_size) / nullIf(bid_event_size + ask_event_size, 0) AS event_imbalance,
    bid_events,
    ask_events,
    bid_events - ask_events AS event_count_delta
FROM CG_mnq_book_proxy_100ms;
```

### Step 4: Clean features
```sql
CREATE TABLE CG_mnq_features_100ms_clean
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT *
FROM CG_mnq_features_100ms
WHERE
    best_bid > 0
    AND best_ask > 0
    AND best_ask >= best_bid
    AND (best_ask - best_bid) <= 2.0;
```

### Step 5: Generate signals
```sql
CREATE TABLE CG_mnq_signals_100ms
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT
    *,
    CASE
        WHEN event_delta > 50 AND event_imbalance > 0.60 THEN 'LONG'
        WHEN event_delta < -50 AND event_imbalance < -0.60 THEN 'SHORT'
        ELSE 'NONE'
    END AS signal
FROM CG_mnq_features_100ms;
```

## Useful Exploration Queries

### Count rows in each table
```sql
SELECT
    'mnq_mbo' as table,
    COUNT(*) as rows
FROM mnq_mbo
WHERE symbol = 'MNQZ5'

UNION ALL

SELECT 'CG_mnq_mbo_events', COUNT(*) FROM CG_mnq_mbo_events
UNION ALL
SELECT 'CG_mnq_book_proxy_100ms', COUNT(*) FROM CG_mnq_book_proxy_100ms
UNION ALL
SELECT 'CG_mnq_features_100ms', COUNT(*) FROM CG_mnq_features_100ms
UNION ALL
SELECT 'CG_mnq_signals_100ms', COUNT(*) FROM CG_mnq_signals_100ms;
```

### See event_delta distribution
```sql
SELECT
    quantile(0.05)(event_delta) as p5,
    quantile(0.25)(event_delta) as p25,
    quantile(0.50)(event_delta) as p50,
    quantile(0.75)(event_delta) as p75,
    quantile(0.95)(event_delta) as p95,
    min(event_delta) as min,
    max(event_delta) as max,
    avg(event_delta) as avg
FROM CG_mnq_features_100ms;
```

### See signal distribution
```sql
SELECT
    signal,
    COUNT(*) as count,
    COUNT(*) * 100.0 / (SELECT COUNT(*) FROM CG_mnq_signals_100ms) as pct
FROM CG_mnq_signals_100ms
GROUP BY signal
ORDER BY count DESC;
```

### Find when ChatGPT created tables
```sql
SELECT
    substring(query, 14, 50) as table_name,
    event_time,
    query_duration_ms / 1000.0 as duration_seconds
FROM system.query_log
WHERE query LIKE 'CREATE TABLE CG_mnq%'
  AND type = 'QueryFinish'
ORDER BY event_time ASC
FORMAT Vertical;
```

## Pro Tips

1. **Query log retention**: The query_log may not keep queries forever. Save important CREATE TABLE queries.

2. **Export creation queries**: Save all discovered queries to .sql files for documentation:
   ```bash
   clickhouse-client --query "SELECT query FROM system.query_log WHERE ..." > table_creation.sql
   ```

3. **Check table sizes**:
   ```sql
   SELECT
       table,
       formatReadableSize(sum(bytes)) as size,
       sum(rows) as rows
   FROM system.parts
   WHERE database = 'default'
     AND table LIKE 'CG_mnq%'
   GROUP BY table
   ORDER BY sum(bytes) DESC;
   ```

4. **Find dependencies**: Look for tables that reference each other:
   ```bash
   clickhouse-client --query "SELECT query FROM system.query_log WHERE query LIKE '%FROM <table>%'"
   ```
