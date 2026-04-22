-- Create TICK-based bars (volume-weighted, adaptive to market activity)
--
-- Tick bars: New bar every N trades (market activity)
-- - More bars during high activity
-- - Fewer bars during quiet periods
-- - Better captures velocity and momentum
--
-- We'll create two views:
-- 1. 1000-tick bars: ~1000 trades per bar (finer granularity)
-- 2. 5000-tick bars: ~5000 trades per bar (swing trading)
--
-- Remember: CGCl_ prefix for Claude-generated objects

-- First, create a helper view that assigns each event to a tick bar bucket
-- This uses window functions to count trades cumulatively

CREATE VIEW IF NOT EXISTS mnq_tick_bar_assignments AS
SELECT
    ts_event,
    toDate(ts_event) as date,
    symbol,
    action,
    side,
    price,
    size,
    -- Calculate cumulative trade count within each day
    -- Only count actual trades ('T' action)
    sumIf(1, action = 'T') OVER (
        PARTITION BY toDate(ts_event), symbol
        ORDER BY ts_event
        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
    ) as cumulative_trades,
    -- Assign to 1000-tick bar bucket
    intDiv(sumIf(1, action = 'T') OVER (
        PARTITION BY toDate(ts_event), symbol
        ORDER BY ts_event
        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
    ), 1000) as tick_bar_1000,
    -- Assign to 5000-tick bar bucket
    intDiv(sumIf(1, action = 'T') OVER (
        PARTITION BY toDate(ts_event), symbol
        ORDER BY ts_event
        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
    ), 5000) as tick_bar_5000
FROM mnq_mbo
WHERE symbol = 'MNQZ5'
  AND hour(ts_event) >= 9
  AND hour(ts_event) < 16
  AND action IN ('A', 'C', 'M', 'T', 'F')
ORDER BY ts_event;


-- Now create 1000-tick bar aggregation
CREATE VIEW IF NOT EXISTS mnq_1000tick_bars_orderflow AS
SELECT
    date,
    tick_bar_1000 as bar_id,
    symbol,

    -- Time range for this bar
    min(ts_event) as bar_start_time,
    max(ts_event) as bar_end_time,

    -- OHLC from actual trades
    argMinIf(price, ts_event, action IN ('T', 'F')) as open,
    maxIf(price, action IN ('T', 'F')) as high,
    minIf(price, action IN ('T', 'F')) as low,
    argMaxIf(price, ts_event, action IN ('T', 'F')) as close,

    -- Volume metrics
    sumIf(size, action = 'T' AND side = 'A') as buy_volume,
    sumIf(size, action = 'T' AND side = 'B') as sell_volume,
    sumIf(size, action IN ('T', 'F')) as total_volume,

    -- Delta
    sumIf(size, action = 'T' AND side = 'A') - sumIf(size, action = 'T' AND side = 'B') as delta,

    -- Order flow
    countIf(action = 'A') as adds,
    countIf(action = 'C') as cancels,
    countIf(action = 'M') as modifies,
    countIf(action = 'T') as trades,
    countIf(action = 'F') as fills,

    -- Imbalance ratio
    if(sumIf(size, action = 'T' AND side = 'B') > 0,
       sumIf(size, action = 'T' AND side = 'A') / sumIf(size, action = 'T' AND side = 'B'),
       0) as imbalance_ratio,

    -- Price movement
    argMaxIf(price, ts_event, action IN ('T', 'F')) - argMinIf(price, ts_event, action IN ('T', 'F')) as net_change,

    -- Velocity: seconds taken to complete this bar
    dateDiff('second', min(ts_event), max(ts_event)) + 1 as duration_seconds,

    -- Event count
    count(*) as event_count

FROM mnq_tick_bar_assignments
WHERE tick_bar_1000 >= 0  -- Exclude incomplete bars
GROUP BY date, tick_bar_1000, symbol
ORDER BY date, tick_bar_1000;


-- Create 5000-tick bar aggregation
CREATE VIEW IF NOT EXISTS mnq_5000tick_bars_orderflow AS
SELECT
    date,
    tick_bar_5000 as bar_id,
    symbol,

    -- Time range for this bar
    min(ts_event) as bar_start_time,
    max(ts_event) as bar_end_time,

    -- OHLC from actual trades
    argMinIf(price, ts_event, action IN ('T', 'F')) as open,
    maxIf(price, action IN ('T', 'F')) as high,
    minIf(price, action IN ('T', 'F')) as low,
    argMaxIf(price, ts_event, action IN ('T', 'F')) as close,

    -- Volume metrics
    sumIf(size, action = 'T' AND side = 'A') as buy_volume,
    sumIf(size, action = 'T' AND side = 'B') as sell_volume,
    sumIf(size, action IN ('T', 'F')) as total_volume,

    -- Delta
    sumIf(size, action = 'T' AND side = 'A') - sumIf(size, action = 'T' AND side = 'B') as delta,

    -- Order flow
    countIf(action = 'A') as adds,
    countIf(action = 'C') as cancels,
    countIf(action = 'M') as modifies,
    countIf(action = 'T') as trades,
    countIf(action = 'F') as fills,

    -- Imbalance ratio
    if(sumIf(size, action = 'T' AND side = 'B') > 0,
       sumIf(size, action = 'T' AND side = 'A') / sumIf(size, action = 'T' AND side = 'B'),
       0) as imbalance_ratio,

    -- Price movement
    argMaxIf(price, ts_event, action IN ('T', 'F')) - argMinIf(price, ts_event, action IN ('T', 'F')) as net_change,

    -- Velocity: seconds taken to complete this bar
    dateDiff('second', min(ts_event), max(ts_event)) + 1 as duration_seconds,

    -- Event count
    count(*) as event_count

FROM mnq_tick_bar_assignments
WHERE tick_bar_5000 >= 0  -- Exclude incomplete bars
GROUP BY date, tick_bar_5000, symbol
ORDER BY date, tick_bar_5000;


-- Test queries to get statistics
--
-- 1000-tick bars:
-- SELECT
--     date,
--     count(*) as bars_per_day,
--     avg(duration_seconds) as avg_duration_sec,
--     min(duration_seconds) as min_duration_sec,
--     max(duration_seconds) as max_duration_sec,
--     avg(delta) as avg_delta,
--     max(abs(delta)) as max_abs_delta,
--     avg(imbalance_ratio) as avg_imbalance
-- FROM mnq_1000tick_bars_orderflow
-- WHERE date = '2025-10-01'
-- GROUP BY date;
--
-- 5000-tick bars:
-- SELECT
--     date,
--     count(*) as bars_per_day,
--     avg(duration_seconds) as avg_duration_sec,
--     min(duration_seconds) as min_duration_sec,
--     max(duration_seconds) as max_duration_sec,
--     avg(delta) as avg_delta,
--     max(abs(delta)) as max_abs_delta,
--     avg(imbalance_ratio) as avg_imbalance
-- FROM mnq_5000tick_bars_orderflow
-- WHERE date = '2025-10-01'
-- GROUP BY date;
