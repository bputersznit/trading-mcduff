-- ============================================================================
-- BOOKMAP-STYLE TRADING STRATEGIES FOR MNQ ORDER FLOW
-- Using mnq_orderflow_1sec table
-- ============================================================================

-- ============================================================================
-- 1. ABSORPTION DETECTION
-- ============================================================================
-- Pattern: Heavy aggression hits a level but price doesn't move
-- Indicates: Large passive participant absorbing flow (potential reversal)

-- BID ABSORPTION (Potential bounce/reversal up)
CREATE OR REPLACE VIEW absorption_bid_levels AS
SELECT
    timestamp_1sec,
    price,
    sell_aggressor_volume,
    bid_adds,
    net_resting_bid,
    sell_aggressor_volume - net_resting_bid as absorption_strength,
    total_volume,
    -- Check if price held (next second price didn't drop significantly)
    (SELECT min(price) FROM mnq_orderflow_1sec n
     WHERE n.timestamp_1sec BETWEEN timestamp_1sec AND timestamp_1sec + INTERVAL 3 SECOND
       AND n.symbol = mnq_orderflow_1sec.symbol) as price_3sec_low
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQZ5'
  AND sell_aggressor_volume > 80        -- Heavy selling
  AND bid_adds > sell_aggressor_volume * 1.3  -- Strong bidding response
  AND net_resting_bid > 50              -- Net liquidity added to bid
  AND total_volume > 100
  -- Price held (didn't drop more than 1 point in next 3 seconds)
  AND (SELECT min(price) FROM mnq_orderflow_1sec n
       WHERE n.timestamp_1sec BETWEEN timestamp_1sec AND timestamp_1sec + INTERVAL 3 SECOND
         AND n.symbol = mnq_orderflow_1sec.symbol) >= price - 1.0
ORDER BY timestamp_1sec DESC;

-- ASK ABSORPTION (Potential rejection/reversal down)
CREATE OR REPLACE VIEW absorption_ask_levels AS
SELECT
    timestamp_1sec,
    price,
    buy_aggressor_volume,
    ask_adds,
    net_resting_ask,
    buy_aggressor_volume - net_resting_ask as absorption_strength,
    total_volume,
    (SELECT max(price) FROM mnq_orderflow_1sec n
     WHERE n.timestamp_1sec BETWEEN timestamp_1sec AND timestamp_1sec + INTERVAL 3 SECOND
       AND n.symbol = mnq_orderflow_1sec.symbol) as price_3sec_high
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQZ5'
  AND buy_aggressor_volume > 80         -- Heavy buying
  AND ask_adds > buy_aggressor_volume * 1.3   -- Strong offer response
  AND net_resting_ask > 50              -- Net liquidity added to ask
  AND total_volume > 100
  -- Price rejected (didn't rally more than 1 point in next 3 seconds)
  AND (SELECT max(price) FROM mnq_orderflow_1sec n
       WHERE n.timestamp_1sec BETWEEN timestamp_1sec AND timestamp_1sec + INTERVAL 3 SECOND
         AND n.symbol = mnq_orderflow_1sec.symbol) <= price + 1.0
ORDER BY timestamp_1sec DESC;


-- ============================================================================
-- 2. ICEBERG DETECTION
-- ============================================================================
-- Pattern: Repeated executions at one price with small visible liquidity
-- Indicates: Hidden large order (iceberg) defending/accumulating

CREATE OR REPLACE VIEW iceberg_levels AS
SELECT
    timestamp_1sec,
    price,
    total_volume,
    bid_adds + ask_adds as visible_adds,
    CASE
        WHEN bid_adds + ask_adds > 0
        THEN round(total_volume / (bid_adds + ask_adds), 2)
        ELSE 0
    END as iceberg_ratio,  -- High ratio = iceberg signature
    buy_aggressor_volume,
    sell_aggressor_volume,
    aggression_delta,
    bid_adds,
    ask_adds,
    -- Determine which side is the iceberg
    CASE
        WHEN sell_aggressor_volume > buy_aggressor_volume * 1.5 AND bid_adds < sell_aggressor_volume * 0.3
            THEN 'BID_ICEBERG'
        WHEN buy_aggressor_volume > sell_aggressor_volume * 1.5 AND ask_adds < buy_aggressor_volume * 0.3
            THEN 'ASK_ICEBERG'
        ELSE 'UNCLEAR'
    END as iceberg_side
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQZ5'
  AND total_volume > 100
  AND bid_adds + ask_adds > 0
  AND total_volume / (bid_adds + ask_adds) > 5.0  -- Traded volume >> visible adds
ORDER BY iceberg_ratio DESC, timestamp_1sec DESC;


-- ============================================================================
-- 3. THIN LIQUIDITY BREAKOUT
-- ============================================================================
-- Pattern: Low resting liquidity + aggressive flow = rapid price movement
-- Indicates: Breakout continuation opportunity

CREATE OR REPLACE VIEW thin_liquidity_breakouts AS
WITH price_levels AS (
    SELECT
        timestamp_1sec,
        price,
        bid_adds,
        ask_adds,
        buy_aggressor_volume,
        sell_aggressor_volume,
        total_volume,
        -- Check liquidity thinness
        CASE
            WHEN buy_aggressor_volume > sell_aggressor_volume
            THEN ask_adds  -- Breaking up through asks
            ELSE bid_adds  -- Breaking down through bids
        END as opposing_liquidity,
        -- Determine breakout direction
        CASE
            WHEN buy_aggressor_volume > sell_aggressor_volume * 2
            THEN 'BULL_BREAKOUT'
            WHEN sell_aggressor_volume > buy_aggressor_volume * 2
            THEN 'BEAR_BREAKOUT'
            ELSE 'NO_BREAKOUT'
        END as breakout_direction
    FROM mnq_orderflow_1sec
    WHERE symbol = 'MNQZ5'
)
SELECT
    timestamp_1sec,
    price,
    opposing_liquidity,
    buy_aggressor_volume,
    sell_aggressor_volume,
    total_volume,
    breakout_direction,
    -- Calculate velocity (price change over next 5 seconds)
    (SELECT max(price) - min(price)
     FROM mnq_orderflow_1sec n
     WHERE n.timestamp_1sec BETWEEN timestamp_1sec AND timestamp_1sec + INTERVAL 5 SECOND
       AND n.symbol = price_levels.symbol) as price_move_5sec
FROM price_levels
WHERE breakout_direction != 'NO_BREAKOUT'
  AND opposing_liquidity < 30           -- Thin opposing liquidity
  AND total_volume > 80                 -- Sufficient aggression
  AND (SELECT max(price) - min(price)
       FROM mnq_orderflow_1sec n
       WHERE n.timestamp_1sec BETWEEN timestamp_1sec AND timestamp_1sec + INTERVAL 5 SECOND
         AND n.symbol = 'MNQZ5') > 1.0  -- Price actually moved
ORDER BY timestamp_1sec DESC;


-- ============================================================================
-- 4. LIQUIDITY WALL REVERSAL (Failed Breakout)
-- ============================================================================
-- Pattern: Price can't break through large resting orders, then reverses
-- Indicates: Exhaustion and reversal opportunity

CREATE OR REPLACE VIEW liquidity_wall_reversals AS
WITH walls AS (
    SELECT
        timestamp_1sec,
        price,
        bid_adds,
        ask_adds,
        buy_aggressor_volume,
        sell_aggressor_volume,
        -- Identify walls
        CASE
            WHEN ask_adds > 150 THEN 'ASK_WALL'
            WHEN bid_adds > 150 THEN 'BID_WALL'
            ELSE 'NO_WALL'
        END as wall_type,
        -- Check if price failed to break through
        CASE
            WHEN ask_adds > 150 AND buy_aggressor_volume > 80 THEN 'BULL_FAILED'
            WHEN bid_adds > 150 AND sell_aggressor_volume > 80 THEN 'BEAR_FAILED'
            ELSE 'NO_FAILURE'
        END as failure_type
    FROM mnq_orderflow_1sec
    WHERE symbol = 'MNQZ5'
)
SELECT
    timestamp_1sec,
    price,
    wall_type,
    failure_type,
    bid_adds,
    ask_adds,
    buy_aggressor_volume,
    sell_aggressor_volume,
    -- Check for reversal in next few seconds
    (SELECT sum(aggression_delta)
     FROM mnq_orderflow_1sec n
     WHERE n.timestamp_1sec BETWEEN timestamp_1sec + INTERVAL 1 SECOND
                                AND timestamp_1sec + INTERVAL 5 SECOND
       AND n.symbol = 'MNQZ5') as aggression_reversal_5sec
FROM walls
WHERE failure_type != 'NO_FAILURE'
  AND wall_type != 'NO_WALL'
ORDER BY timestamp_1sec DESC;


-- ============================================================================
-- 5. SPOOFING DETECTION
-- ============================================================================
-- Pattern: Large liquidity appears then disappears before being hit
-- Indicates: Fake liquidity trying to manipulate price

CREATE OR REPLACE VIEW spoofing_events AS
WITH liquidity_changes AS (
    SELECT
        timestamp_1sec,
        price,
        bid_adds,
        ask_adds,
        bid_cancels,
        ask_cancels,
        buy_aggressor_volume,
        sell_aggressor_volume,
        -- Detect large adds followed by large cancels
        CASE
            WHEN bid_adds > 100 AND bid_cancels > bid_adds * 0.7
                 AND buy_aggressor_volume < bid_adds * 0.2
            THEN 'BID_SPOOF'
            WHEN ask_adds > 100 AND ask_cancels > ask_adds * 0.7
                 AND sell_aggressor_volume < ask_adds * 0.2
            THEN 'ASK_SPOOF'
            ELSE 'NO_SPOOF'
        END as spoof_type
    FROM mnq_orderflow_1sec
    WHERE symbol = 'MNQZ5'
)
SELECT
    timestamp_1sec,
    price,
    spoof_type,
    bid_adds,
    bid_cancels,
    ask_adds,
    ask_cancels,
    buy_aggressor_volume,
    sell_aggressor_volume,
    -- Ratio of cancels to adds
    CASE
        WHEN spoof_type = 'BID_SPOOF' THEN round(bid_cancels / NULLIF(bid_adds, 0), 2)
        WHEN spoof_type = 'ASK_SPOOF' THEN round(ask_cancels / NULLIF(ask_adds, 0), 2)
        ELSE 0
    END as cancel_ratio
FROM liquidity_changes
WHERE spoof_type != 'NO_SPOOF'
ORDER BY timestamp_1sec DESC;


-- ============================================================================
-- 6. STOP RUN DETECTION
-- ============================================================================
-- Pattern: Sudden burst beyond key level, triggers stops, then reverses/exhausts
-- Indicates: Liquidity sweep opportunity (trap or continuation)

CREATE OR REPLACE VIEW stop_runs AS
WITH aggressive_bursts AS (
    SELECT
        timestamp_1sec,
        price,
        buy_aggressor_volume,
        sell_aggressor_volume,
        total_volume,
        aggression_delta,
        bid_adds,
        ask_adds,
        -- Detect sudden aggression spike
        CASE
            WHEN buy_aggressor_volume > 100 AND bid_adds + ask_adds < 50 THEN 'BULL_RUN'
            WHEN sell_aggressor_volume > 100 AND bid_adds + ask_adds < 50 THEN 'BEAR_RUN'
            ELSE 'NO_RUN'
        END as run_type,
        -- Calculate if it's at potential stop level (new high/low)
        (SELECT max(price)
         FROM mnq_orderflow_1sec n
         WHERE n.timestamp_1sec BETWEEN timestamp_1sec - INTERVAL 300 SECOND
                                    AND timestamp_1sec - INTERVAL 1 SECOND
           AND n.symbol = 'MNQZ5') as recent_high,
        (SELECT min(price)
         FROM mnq_orderflow_1sec n
         WHERE n.timestamp_1sec BETWEEN timestamp_1sec - INTERVAL 300 SECOND
                                    AND timestamp_1sec - INTERVAL 1 SECOND
           AND n.symbol = 'MNQZ5') as recent_low
    FROM mnq_orderflow_1sec
    WHERE symbol = 'MNQZ5'
      AND total_volume > 100
)
SELECT
    timestamp_1sec,
    price,
    run_type,
    buy_aggressor_volume,
    sell_aggressor_volume,
    total_volume,
    recent_high,
    recent_low,
    -- Is this beyond recent range?
    CASE
        WHEN run_type = 'BULL_RUN' AND price >= recent_high THEN 'HIGH_BREAK'
        WHEN run_type = 'BEAR_RUN' AND price <= recent_low THEN 'LOW_BREAK'
        ELSE 'NO_BREAK'
    END as stop_trigger,
    -- Check for exhaustion/reversal in next 5 seconds
    (SELECT sum(aggression_delta)
     FROM mnq_orderflow_1sec n
     WHERE n.timestamp_1sec BETWEEN timestamp_1sec + INTERVAL 1 SECOND
                                AND timestamp_1sec + INTERVAL 5 SECOND
       AND n.symbol = 'MNQZ5') as post_run_delta
FROM aggressive_bursts
WHERE run_type != 'NO_RUN'
  AND (price >= recent_high OR price <= recent_low)  -- Beyond recent range
ORDER BY timestamp_1sec DESC;


-- ============================================================================
-- 7. COMBINED VELOCITY + DELTA BREAKOUT
-- ============================================================================
-- Pattern: High velocity spike + strong delta + thin opposing liquidity
-- Indicates: Momentum continuation (aligns with your velocity spike work)

CREATE OR REPLACE VIEW velocity_delta_breakouts AS
SELECT
    o.timestamp_1sec,
    o.price,
    o.buy_aggressor_volume,
    o.sell_aggressor_volume,
    o.aggression_delta,
    o.total_volume,
    o.bid_adds,
    o.ask_adds,
    -- Calculate 3-second velocity
    (SELECT max(price) - min(price)
     FROM mnq_orderflow_1sec n
     WHERE n.timestamp_1sec BETWEEN o.timestamp_1sec - INTERVAL 3 SECOND
                                AND o.timestamp_1sec
       AND n.symbol = 'MNQZ5') as velocity_3sec,
    -- Determine setup quality
    CASE
        WHEN o.aggression_delta > 80 AND o.ask_adds < 30 THEN 'BULL_BREAKOUT'
        WHEN o.aggression_delta < -80 AND o.bid_adds < 30 THEN 'BEAR_BREAKOUT'
        ELSE 'NO_SETUP'
    END as setup_type
FROM mnq_orderflow_1sec o
WHERE o.symbol = 'MNQZ5'
  AND o.total_volume > 100
  AND abs(o.aggression_delta) > 80      -- Strong directional delta
  AND (SELECT max(price) - min(price)
       FROM mnq_orderflow_1sec n
       WHERE n.timestamp_1sec BETWEEN o.timestamp_1sec - INTERVAL 3 SECOND
                                  AND o.timestamp_1sec
         AND n.symbol = 'MNQZ5') > 1.5  -- Fast price movement
HAVING setup_type != 'NO_SETUP'
ORDER BY timestamp_1sec DESC;


-- ============================================================================
-- USAGE EXAMPLES
-- ============================================================================

/*
-- Find absorption levels on 2025-10-10 during market hours
SELECT * FROM absorption_bid_levels
WHERE toDate(timestamp_1sec) = '2025-10-10'
  AND hour(timestamp_1sec) BETWEEN 9 AND 16
ORDER BY absorption_strength DESC
LIMIT 10;

-- Detect icebergs with highest iceberg_ratio
SELECT * FROM iceberg_levels
WHERE toDate(timestamp_1sec) = '2025-10-10'
  AND iceberg_ratio > 10
ORDER BY iceberg_ratio DESC;

-- Find successful thin liquidity breakouts (moved >2 points)
SELECT * FROM thin_liquidity_breakouts
WHERE toDate(timestamp_1sec) = '2025-10-10'
  AND price_move_5sec > 2.0
ORDER BY price_move_5sec DESC;

-- Spot failed breakouts at liquidity walls
SELECT * FROM liquidity_wall_reversals
WHERE toDate(timestamp_1sec) = '2025-10-10'
  AND abs(aggression_reversal_5sec) > 100
ORDER BY timestamp_1sec;

-- Find spoofing events
SELECT * FROM spoofing_events
WHERE toDate(timestamp_1sec) = '2025-10-10'
  AND cancel_ratio > 0.8
ORDER BY timestamp_1sec;

-- Detect stop runs at key levels
SELECT * FROM stop_runs
WHERE toDate(timestamp_1sec) = '2025-10-10'
  AND stop_trigger IN ('HIGH_BREAK', 'LOW_BREAK')
  AND abs(post_run_delta) > 50  -- Significant reversal after run
ORDER BY timestamp_1sec;

-- Velocity + Delta breakouts (momentum trades)
SELECT * FROM velocity_delta_breakouts
WHERE toDate(timestamp_1sec) = '2025-10-10'
  AND velocity_3sec > 2.0
ORDER BY timestamp_1sec;
*/
