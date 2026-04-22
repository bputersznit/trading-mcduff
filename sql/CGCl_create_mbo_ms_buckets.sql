-- Millisecond-Aggregated MBO Buckets
-- Aggregates MBO trade events (action='T'/'F', flags=0) to 1ms buckets
-- This eliminates the nanosecond timestamp issue and provides fast access
-- to pre-aggregated OHLC + volume data at millisecond resolution

-- Step 1: Create target table to store aggregated buckets
CREATE TABLE IF NOT EXISTS mnq_mbo_ms_buckets
(
    ts_ms Int64,              -- Milliseconds since epoch
    symbol String,
    date Date,
    first_price Float64,
    last_price Float64,
    high Float64,
    low Float64,
    buy_volume UInt32,        -- Aggressor buy volume (side='A')
    sell_volume UInt32,       -- Aggressor sell volume (side='B')
    trade_count UInt16
)
ENGINE = MergeTree()
PARTITION BY toYYYYMM(date)
ORDER BY (symbol, date, ts_ms)
SETTINGS index_granularity = 8192;

-- Step 2: Create materialized view to populate table from MBO stream
CREATE MATERIALIZED VIEW IF NOT EXISTS mnq_mbo_ms_buckets_mv
TO mnq_mbo_ms_buckets
AS
SELECT
    toInt64(toUnixTimestamp64Milli(ts_event)) as ts_ms,
    symbol,
    toDate(ts_event) as date,
    argMin(price, ts_event) as first_price,
    argMax(price, ts_event) as last_price,
    max(price) as high,
    min(price) as low,
    sumIf(size, side = 'A') as buy_volume,
    sumIf(size, side = 'B') as sell_volume,
    count() as trade_count
FROM mnq_mbo
WHERE action IN ('T', 'F')
  AND flags = 0
GROUP BY ts_ms, symbol, date;

-- Note: This MV will only process NEW data inserted after creation.
-- To backfill historical data, run the backfill query separately.
