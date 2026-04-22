-- 1-Second Order Flow Table - Bookmap-Style Heatmap Data
-- Aggregates MBO events to (timestamp_1sec, symbol, price) buckets
-- Tracks both resting liquidity and aggression for complete order flow picture

CREATE TABLE IF NOT EXISTS mnq_orderflow_1sec
(
    timestamp_1sec DateTime,          -- 1-second bucket
    symbol String,
    price Float64,                    -- Tick-level (0.25 increments for MNQ)

    -- RESTING LIQUIDITY (limit orders in book)
    bid_adds UInt32,                  -- Volume added to bid (action='A', side='B')
    ask_adds UInt32,                  -- Volume added to ask (action='A', side='A')
    bid_cancels UInt32,               -- Volume cancelled from bid (action='C', side='B')
    ask_cancels UInt32,               -- Volume cancelled from ask (action='C', side='A')
    bid_modifies UInt32,              -- Volume modified on bid (action='M', side='B')
    ask_modifies UInt32,              -- Volume modified on ask (action='M', side='A')

    -- AGGRESSION (liquidity takers)
    buy_aggressor_volume UInt32,      -- Hitting ask (action='T'/'F', side='A', flags=0)
    sell_aggressor_volume UInt32,     -- Hitting bid (action='T'/'F', side='B', flags=0)
    buy_aggressor_trades UInt16,      -- Count of buy aggressor trades
    sell_aggressor_trades UInt16,     -- Count of sell aggressor trades

    -- DERIVED METRICS
    net_resting_bid Int32,            -- (bid_adds - bid_cancels) - Net liquidity added to bid
    net_resting_ask Int32,            -- (ask_adds - ask_cancels) - Net liquidity added to ask
    resting_imbalance Int32,          -- (net_resting_bid - net_resting_ask) - Book imbalance
    aggression_delta Int32,           -- (buy_aggressor - sell_aggressor) - Directional pressure
    total_volume UInt32               -- Total traded volume at this price level
)
ENGINE = MergeTree()
PARTITION BY toYYYYMM(timestamp_1sec)
ORDER BY (symbol, timestamp_1sec, price)
SETTINGS index_granularity = 8192;
