-- L2 Market Depth Table Schema
-- Stores tick-by-tick order book events from NinjaTrader Market Replay

CREATE TABLE IF NOT EXISTS l2_depth_raw
(
    timestamp DateTime64(3, 'America/Chicago'),
    side FixedString(1),              -- 'B' = Bid, 'A' = Ask
    operation FixedString(1),         -- 'A' = Add, 'U' = Update, 'R' = Remove
    position UInt8,                   -- Depth level (0-9)
    price Float64,                    -- Price level
    size Float64,                     -- Order size at this level
    date Date MATERIALIZED toDate(timestamp)
)
ENGINE = MergeTree()
PARTITION BY date
ORDER BY (date, timestamp, side, position)
SETTINGS index_granularity = 8192;

-- Index for time-range queries
CREATE INDEX IF NOT EXISTS idx_timestamp ON l2_depth_raw (timestamp) TYPE minmax GRANULARITY 1;

-- Index for price-range queries
CREATE INDEX IF NOT EXISTS idx_price ON l2_depth_raw (price) TYPE minmax GRANULARITY 1;
