-- ============================================================================
-- Phase 1: Data Foundation - MBO Event Normalization
-- ============================================================================
-- Purpose: Create canonical normalized event table from raw mnq_mbo
-- Source: mnq_mbo (789M events, Sept 21 - Oct 22, 2025)
-- Output: CG_mnq_mbo_events
--
-- This is the foundation layer for all Bookmap-style microstructure analysis.
-- All subsequent tables (heatmap, walls, interactions) derive from this.
--
-- Date: 2026-05-03
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_mbo_events;

CREATE TABLE CG_mnq_mbo_events
ENGINE = MergeTree
PARTITION BY toDate(ts_event)
ORDER BY (symbol, ts_event, sequence)
AS
SELECT
    -- Time
    ts_event,
    toDateTime(ts_event, 'America/New_York') AS ts_event_et,
    toDate(ts_event) AS trade_date,
    sequence,

    -- Event classification
    action,
    CASE action
        WHEN 'A' THEN 1
        WHEN 'M' THEN 0
        WHEN 'C' THEN 0
        WHEN 'T' THEN 0
        WHEN 'F' THEN 0
        WHEN 'R' THEN 0
    END AS is_add,

    CASE action
        WHEN 'M' THEN 1
        ELSE 0
    END AS is_modify,

    CASE action
        WHEN 'C' THEN 1
        ELSE 0
    END AS is_cancel,

    CASE action
        WHEN 'T' THEN 1
        ELSE 0
    END AS is_trade,

    CASE action
        WHEN 'F' THEN 1
        ELSE 0
    END AS is_fill,

    CASE action
        WHEN 'R' THEN 1
        ELSE 0
    END AS is_reset,

    -- Side classification
    side,
    CASE side
        WHEN 'B' THEN 1
        WHEN 'Bid' THEN 1
        WHEN 'BID' THEN 1
        ELSE 0
    END AS is_bid_event,

    CASE side
        WHEN 'A' THEN 1
        WHEN 'Ask' THEN 1
        WHEN 'ASK' THEN 1
        ELSE 0
    END AS is_ask_event,

    -- Price and size
    price,
    round(price / 0.25) * 0.25 AS price_tick,  -- Normalize to 0.25 tick
    size,

    -- Order tracking
    order_id,

    -- Instrument
    symbol,
    instrument_id,

    -- Metadata
    publisher_id,
    channel_id,
    flags,
    ts_in_delta

FROM mnq_mbo
WHERE symbol LIKE 'MNQ%'  -- Only MNQ contracts
  AND ts_event >= '2025-09-21'
  AND ts_event < '2025-10-23';

-- ============================================================================
-- VERIFICATION
-- ============================================================================

SELECT '=== CG_mnq_mbo_events Summary ===' AS report FORMAT Pretty;

SELECT
    count() AS total_events,
    countDistinct(symbol) AS symbols,
    countDistinct(trade_date) AS days,
    min(ts_event) AS first_event,
    max(ts_event) AS last_event,
    formatReadableSize(sum(size) * 8) AS estimated_memory
FROM CG_mnq_mbo_events
FORMAT Pretty;

SELECT '=== Event Type Distribution ===' AS report FORMAT Pretty;

SELECT
    action,
    count() AS events,
    round(count() / (SELECT count() FROM CG_mnq_mbo_events) * 100, 2) AS pct,
    sum(is_add) AS adds,
    sum(is_modify) AS modifies,
    sum(is_cancel) AS cancels,
    sum(is_trade) AS trades,
    sum(is_fill) AS fills
FROM CG_mnq_mbo_events
GROUP BY action
ORDER BY events DESC
FORMAT Pretty;

SELECT '=== Side Distribution ===' AS report FORMAT Pretty;

SELECT
    side,
    count() AS events,
    round(count() / (SELECT count() FROM CG_mnq_mbo_events) * 100, 2) AS pct,
    sum(is_bid_event) AS bid_events,
    sum(is_ask_event) AS ask_events
FROM CG_mnq_mbo_events
GROUP BY side
ORDER BY events DESC
FORMAT Pretty;

SELECT '=== Daily Event Counts ===' AS report FORMAT Pretty;

SELECT
    trade_date,
    count() AS events,
    formatReadableQuantity(count()) AS events_readable,
    sum(is_add) AS adds,
    sum(is_cancel) AS cancels,
    sum(is_fill) AS fills,
    sum(is_trade) AS trades
FROM CG_mnq_mbo_events
GROUP BY trade_date
ORDER BY trade_date
FORMAT Pretty;

-- ============================================================================
-- NEXT STEP: Phase 2 - Heatmap Reconstruction (100ms buckets)
-- ============================================================================
