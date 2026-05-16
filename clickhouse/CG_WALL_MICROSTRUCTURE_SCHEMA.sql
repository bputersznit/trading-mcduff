-- ============================================================================
-- Wall Microstructure Framework - Event & Interaction Schema
-- ============================================================================
-- Purpose: Translate Bookmap-style order flow analysis into quantifiable metrics
-- Approach: Event-based tracking of liquidity state + aggression + response
-- Foundation: Builds on existing MBO 100ms aggregation
--
-- Framework Components:
--   1. Wall Events (creation, removal, interaction zones)
--   2. Wall Interactions (aggression into wall + outcome)
--   3. Regime Classification (absorption, exhaustion, breakout, spoof)
--
-- Author: Claude Code + User framework
-- Date: 2026-05-03
-- ============================================================================

-- ============================================================================
-- TABLE 1: Wall Events (Liquidity State Detection)
-- ============================================================================
-- Tracks: When significant liquidity appears/disappears/changes behavior
-- Granularity: Event-based (not time-based)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_events;

CREATE TABLE CG_mnq_wall_events
(
    -- Event identification
    event_id UInt64,
    event_time DateTime64(3, 'UTC'),
    event_type Enum8(
        'WALL_CREATED' = 1,
        'WALL_REMOVED' = 2,
        'WALL_PULLED' = 3,
        'WALL_REPLENISHED' = 4,
        'WALL_CONSUMED' = 5
    ),

    -- Wall location
    wall_side Enum8('BID' = 1, 'ASK' = 2),
    wall_price Float64,
    distance_to_mid_ticks Int32,  -- Distance from mid price

    -- Liquidity metrics (L_state)
    wall_size UInt32,               -- Contracts at this price
    wall_depth_levels UInt8,        -- How many price levels deep (1-10)
    cumulative_size_5lvl UInt32,    -- Total size within 5 ticks

    -- Wall behavior classification
    wall_behavior Enum8(
        'STATIC' = 1,      -- Appears and stays
        'ICEBERG' = 2,     -- Replenishes after fills
        'PULLING' = 3,     -- Moves away as price approaches
        'LADDERED' = 4,    -- Stacked across multiple levels
        'SPOOFING' = 5     -- Appears then disappears untested
    ),

    -- Context
    mid_price Float64,
    spread_ticks Float64,

    -- Metadata
    data_source String DEFAULT 'CG_mnq_book_proxy_100ms'
)
ENGINE = MergeTree
PARTITION BY toDate(event_time)
ORDER BY (event_time, wall_side, wall_price);

-- ============================================================================
-- TABLE 2: Wall Interactions (Aggression + Response Tracking)
-- ============================================================================
-- Tracks: What happens when price approaches a wall
-- Duration: From approach start to outcome resolution
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_interactions;

CREATE TABLE CG_mnq_wall_interactions
(
    -- Interaction identification
    interaction_id UInt64,
    wall_event_id UInt64,           -- Links to CG_mnq_wall_events

    -- Time bounds
    start_time DateTime64(3, 'UTC'),
    end_time DateTime64(3, 'UTC'),
    duration_ms UInt32,

    -- Wall state at interaction start
    wall_side Enum8('BID' = 1, 'ASK' = 2),
    wall_price Float64,
    wall_size_initial UInt32,
    wall_size_final UInt32,
    wall_size_consumed UInt32,       -- How much was filled

    -- Aggression metrics (A_state)
    total_volume UInt32,             -- Total contracts traded during interaction
    buy_volume UInt32,
    sell_volume UInt32,
    delta Int32,                     -- buy_volume - sell_volume
    delta_rate Float64,              -- delta per second
    trades_count UInt32,
    trades_per_second Float64,
    volume_per_second Float64,

    -- Imbalance metrics
    imbalance_ratio Float64,         -- delta / total_volume (-1 to +1)
    peak_imbalance Float64,          -- Max imbalance during interaction

    -- Response metrics (R_state)
    price_start Float64,
    price_end Float64,
    price_change_ticks Int32,        -- Signed (+ = up, - = down)
    price_change_speed Float64,      -- ticks per second

    max_favorable_excursion_ticks Int32,  -- How far price moved in expected direction
    max_adverse_excursion_ticks Int32,    -- How far price moved against

    -- Efficiency metrics
    efficiency Float64,              -- price_change_ticks / total_volume
    aggression_efficiency Float64,   -- price_change / abs(delta)

    -- Outcome classification
    outcome Enum8(
        'REJECT' = 1,       -- Wall held, price reversed
        'BREAK' = 2,        -- Wall consumed, price broke through
        'ABSORB' = 3,       -- High aggression but no price move
        'FADE' = 4,         -- Low aggression, price drifted away
        'SPOOF' = 5,        -- Wall pulled before test
        'INCONCLUSIVE' = 6
    ),

    -- Regime at time of interaction
    regime Enum8(
        'ABSORPTION' = 1,   -- High aggression + no move
        'EXHAUSTION' = 2,   -- Low aggression + stall
        'BREAKOUT' = 3,     -- Aggression + liquidity collapse
        'SPOOF' = 4         -- Wall disappears before test
    ),

    -- Metadata
    data_source String DEFAULT 'CG_mnq_book_proxy_100ms'
)
ENGINE = MergeTree
PARTITION BY toDate(start_time)
ORDER BY (start_time, wall_side, wall_price);

-- ============================================================================
-- TABLE 3: Aggression Snapshots (High-Frequency Delta Tracking)
-- ============================================================================
-- Tracks: Moment-by-moment aggression during wall interactions
-- Granularity: 100ms (matches book proxy)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_aggression_snapshots;

CREATE TABLE CG_mnq_aggression_snapshots
(
    -- Link to interaction
    interaction_id UInt64,

    -- Time
    snapshot_time DateTime64(3, 'UTC'),
    offset_ms UInt32,                -- Milliseconds since interaction start

    -- Instantaneous aggression
    delta_100ms Int32,               -- Delta in this 100ms bucket
    volume_100ms UInt32,             -- Total volume in this bucket
    trades_100ms UInt16,

    -- Running aggregates (since interaction start)
    cumulative_delta Int32,
    cumulative_volume UInt32,
    running_imbalance Float64,

    -- Price at this moment
    price Float64,
    price_change_from_start_ticks Int32,

    -- Distance to wall
    distance_to_wall_ticks Int32,

    -- Metadata
    data_source String DEFAULT 'CG_mnq_book_proxy_100ms'
)
ENGINE = MergeTree
PARTITION BY toDate(snapshot_time)
ORDER BY (interaction_id, snapshot_time);

-- ============================================================================
-- TABLE 4: Wall Interaction Outcomes (Trade Entry Candidates)
-- ============================================================================
-- Tracks: High-probability setups based on interaction regime
-- Purpose: Strategy signal generation
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_trade_candidates;

CREATE TABLE CG_mnq_wall_trade_candidates
(
    -- Candidate identification
    candidate_id UInt64,
    interaction_id UInt64,          -- Links to CG_mnq_wall_interactions

    -- Signal timing
    signal_time DateTime64(3, 'UTC'),

    -- Setup classification
    setup_type Enum8(
        'ABSORPTION_FLIP' = 1,      -- Aggression fails → reversal
        'PULL_BREAK' = 2,           -- Wall disappears → breakout
        'ICEBERG_HOLD' = 3,         -- Replenishing wall → fade
        'THIN_BOOK_SPIKE' = 4,      -- No liquidity + delta burst
        'EXHAUSTION_REVERSAL' = 5,  -- Low aggression + stall
        'TRUE_BREAKOUT' = 6         -- Clean acceptance through level
    ),

    -- Entry parameters
    entry_side Enum8('LONG' = 1, 'SHORT' = 2),
    entry_price Float64,
    entry_reason String,

    -- Risk parameters
    stop_price Float64,
    target_price Float64,
    risk_reward_ratio Float64,

    -- Quality metrics (force rank equivalent)
    wall_strength Float64,          -- 0.0-1.0 (iceberg=high, pulling=low)
    aggression_strength Float64,    -- 0.0-1.0 (high delta rate = high)
    regime_clarity Float64,         -- 0.0-1.0 (clean regime = high)

    overall_quality Float64,        -- Combined score (0.0-1.0)

    -- Context
    regime String,
    directional_efficiency Float64,
    vol_ratio_5d Float64,

    -- Metadata
    data_source String DEFAULT 'CG_mnq_wall_interactions'
)
ENGINE = MergeTree
PARTITION BY toDate(signal_time)
ORDER BY (signal_time, setup_type);

-- ============================================================================
-- VIEWS: Key Metrics & Aggregations
-- ============================================================================

-- View 1: Wall Interaction Summary
DROP VIEW IF EXISTS CG_mnq_wall_interaction_summary;
CREATE VIEW CG_mnq_wall_interaction_summary AS
SELECT
    wall_side,
    outcome,
    regime,
    count() AS interactions,
    round(avg(wall_size_initial), 0) AS avg_wall_size,
    round(avg(total_volume), 0) AS avg_volume,
    round(avg(delta), 0) AS avg_delta,
    round(avg(price_change_ticks), 2) AS avg_price_move,
    round(avg(efficiency), 6) AS avg_efficiency,
    round(avg(duration_ms), 0) AS avg_duration_ms
FROM CG_mnq_wall_interactions
GROUP BY wall_side, outcome, regime
ORDER BY interactions DESC;

-- View 2: High-Quality Trade Candidates
DROP VIEW IF EXISTS CG_mnq_wall_high_quality_setups;
CREATE VIEW CG_mnq_wall_high_quality_setups AS
SELECT
    signal_time,
    setup_type,
    entry_side,
    entry_price,
    round(overall_quality, 4) AS quality,
    round(wall_strength, 4) AS wall_str,
    round(aggression_strength, 4) AS agg_str,
    regime,
    entry_reason
FROM CG_mnq_wall_trade_candidates
WHERE overall_quality >= 0.90
ORDER BY signal_time DESC;

-- View 3: Regime Performance Matrix
DROP VIEW IF EXISTS CG_mnq_regime_matrix;
CREATE VIEW CG_mnq_regime_matrix AS
SELECT
    wall_side AS side,
    regime,
    outcome,
    count() AS count,
    round(avg(price_change_ticks), 2) AS avg_move,
    round(avg(efficiency), 6) AS efficiency,
    round(count / (SELECT count() FROM CG_mnq_wall_interactions) * 100, 2) AS pct_of_total
FROM CG_mnq_wall_interactions
GROUP BY wall_side, regime, outcome
ORDER BY count DESC;

-- ============================================================================
-- COMMENTS & USAGE NOTES
-- ============================================================================

-- Usage Pattern 1: Detect Wall Events from Book Proxy
-- (Requires population query - see CG_WALL_EVENT_DETECTION.sql)

-- Usage Pattern 2: Track Interactions
-- When price comes within X ticks of wall → start tracking aggression

-- Usage Pattern 3: Classify Outcomes
-- After interaction ends → label REJECT/BREAK/ABSORB/FADE

-- Usage Pattern 4: Generate Trade Candidates
-- Filter interactions by quality score → create entry signals

-- ============================================================================
-- NEXT STEPS
-- ============================================================================

-- 1. Build population pipeline: CG_mnq_book_proxy_100ms → CG_mnq_wall_events
-- 2. Build interaction tracker: Detect approach → track aggression → classify outcome
-- 3. Backtest trade candidates: Compare to v9.4 baseline (36 trades, 442.75 pts)
-- 4. If edge exists → implement in NT8 (ClanMarshal v10.0)

-- ============================================================================
