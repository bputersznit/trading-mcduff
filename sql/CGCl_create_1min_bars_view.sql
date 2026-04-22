-- Create 1-minute aggregated bars with order flow statistics
-- This aggregates MBO data into tradeable bars for faster backtesting
--
-- Benefits:
-- - 12M events → 390 bars per day (1000x reduction)
-- - Still captures trend, delta, imbalances
-- - Much faster to process
--
-- Remember: CGCl_ prefix for Claude-generated objects

CREATE VIEW IF NOT EXISTS mnq_1min_bars_orderflow AS
SELECT
    -- Time bucket (1 minute intervals)
    toStartOfMinute(ts_event) as bar_time,
    toDate(ts_event) as date,

    -- Front month contract only
    symbol,

    -- OHLC from actual trades
    argMinIf(price, ts_event, action IN ('T', 'F')) as open,
    maxIf(price, action IN ('T', 'F')) as high,
    minIf(price, action IN ('T', 'F')) as low,
    argMaxIf(price, ts_event, action IN ('T', 'F')) as close,

    -- Volume metrics
    -- For trades: side='A' means hitting ASK (buy), side='B' means hitting BID (sell)
    sumIf(size, action = 'T' AND side = 'A') as buy_volume,
    sumIf(size, action = 'T' AND side = 'B') as sell_volume,
    sumIf(size, action IN ('T', 'F')) as total_volume,

    -- Delta (buy volume - sell volume)
    sumIf(size, action = 'T' AND side = 'A') - sumIf(size, action = 'T' AND side = 'B') as delta,

    -- Order flow metrics
    countIf(action = 'A') as adds,
    countIf(action = 'C') as cancels,
    countIf(action = 'M') as modifies,
    countIf(action = 'T') as trades,
    countIf(action = 'F') as fills,

    -- Imbalance ratio (for regime detection)
    if(sumIf(size, action = 'T' AND side = 'B') > 0,
       sumIf(size, action = 'T' AND side = 'A') / sumIf(size, action = 'T' AND side = 'B'),
       0) as imbalance_ratio,

    -- Price movement
    argMaxIf(price, ts_event, action IN ('T', 'F')) - argMinIf(price, ts_event, action IN ('T', 'F')) as net_change,

    -- Event counts
    count(*) as event_count

FROM mnq_mbo
WHERE symbol = 'MNQZ5'  -- Front month only
  AND hour(ts_event) >= 9
  AND hour(ts_event) < 16
  AND action IN ('A', 'C', 'M', 'T', 'F')
GROUP BY
    toStartOfMinute(ts_event),
    toDate(ts_event),
    symbol
ORDER BY bar_time;


-- Query to test the view
-- SELECT * FROM mnq_1min_bars_orderflow
-- WHERE date = '2025-10-01'
-- LIMIT 10;


-- Alternative: Materialized view for faster queries
-- (Only create if you'll query this frequently)
--
-- CREATE MATERIALIZED VIEW IF NOT EXISTS mnq_1min_bars_orderflow_mv
-- ENGINE = MergeTree()
-- PARTITION BY toYYYYMM(date)
-- ORDER BY (date, bar_time)
-- AS
-- SELECT ... (same query as above)
