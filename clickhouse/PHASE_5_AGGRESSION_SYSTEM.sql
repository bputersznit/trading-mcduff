-- ============================================================================
-- Phase 5: Aggression System - Buy/Sell Pressure Measurement
-- ============================================================================
-- Purpose: Quantify aggression (market orders) in 100ms buckets
-- Source: CG_mnq_mbo_events (trade and fill events)
-- Output: CG_mnq_aggression_100ms
--
-- This table answers: "What is the directional buying/selling pressure?"
--
-- Methodology:
--   - Aggregate trade/fill events into 100ms buckets
--   - Calculate buy vs sell volume (delta)
--   - Measure trade frequency (aggression rate)
--   - Classify aggression intensity
--
-- Date: 2026-05-03
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_aggression_100ms;

CREATE TABLE CG_mnq_aggression_100ms
ENGINE = MergeTree
PARTITION BY toDate(bucket_time)
ORDER BY bucket_time
AS
SELECT
    -- Time bucket (100ms)
    toStartOfInterval(ts_event, INTERVAL 100 MILLISECOND) AS bucket_time,
    toDateTime(toStartOfInterval(ts_event, INTERVAL 100 MILLISECOND), 'America/New_York') AS bucket_et,
    toDate(ts_event) AS trade_date,

    -- Volume metrics
    sumIf(size, is_fill AND is_bid_event) AS buy_volume,
    sumIf(size, is_fill AND is_ask_event) AS sell_volume,
    buy_volume - sell_volume AS delta,
    abs(delta) AS abs_delta,
    buy_volume + sell_volume AS total_volume,

    -- Trade counts
    countIf(is_fill AND is_bid_event) AS buy_trades,
    countIf(is_fill AND is_ask_event) AS sell_trades,
    buy_trades + sell_trades AS total_trades,

    -- Rates (per second)
    total_trades * 10 AS trades_per_second,      -- 100ms * 10 = 1 second
    total_volume * 10 AS volume_per_second,      -- 100ms * 10 = 1 second
    delta * 10 AS delta_rate,                    -- Delta per second

    -- Aggression classification
    CASE
        WHEN buy_volume > sell_volume * 2 THEN 'STRONG_BUY'
        WHEN buy_volume > sell_volume THEN 'BUY_AGGRESSION'
        WHEN sell_volume > buy_volume * 2 THEN 'STRONG_SELL'
        WHEN sell_volume > buy_volume THEN 'SELL_AGGRESSION'
        WHEN total_volume > 0 THEN 'BALANCED'
        ELSE 'NONE'
    END AS aggression_side,

    -- Aggression intensity score (0-1)
    CASE
        WHEN total_volume = 0 THEN 0.0
        ELSE least(abs_delta / nullIf(total_volume, 0), 1.0)
    END AS aggression_score,

    -- Additional classification flags
    if(abs_delta > 100 AND total_volume > 200, 1, 0) AS is_spike,
    if(total_volume < 10, 1, 0) AS is_low_volume,
    if(abs_delta < 10 AND total_volume > 50, 1, 0) AS is_balanced

FROM CG_mnq_mbo_events
WHERE (is_fill = 1 OR is_trade = 1)  -- Only actual executions
  AND symbol LIKE 'MNQZ5%'           -- December 2025 contract
GROUP BY
    bucket_time,
    bucket_et,
    trade_date
ORDER BY bucket_time;

-- ============================================================================
-- VERIFICATION
-- ============================================================================

SELECT '=== CG_mnq_aggression_100ms Summary ===' AS report FORMAT Pretty;

SELECT
    count() AS total_buckets,
    countDistinct(trade_date) AS days,
    min(bucket_time) AS first_bucket,
    max(bucket_time) AS last_bucket,
    round(sum(total_volume), 0) AS total_volume,
    round(avg(total_volume), 2) AS avg_volume_per_bucket,
    round(sum(abs_delta), 0) AS total_abs_delta
FROM CG_mnq_aggression_100ms
FORMAT Pretty;

SELECT '=== Aggression Side Distribution ===' AS report FORMAT Pretty;

SELECT
    aggression_side,
    count() AS buckets,
    round(count() / (SELECT count() FROM CG_mnq_aggression_100ms) * 100, 2) AS pct,
    round(avg(total_volume), 2) AS avg_volume,
    round(avg(abs_delta), 2) AS avg_abs_delta,
    round(avg(aggression_score), 4) AS avg_score
FROM CG_mnq_aggression_100ms
GROUP BY aggression_side
ORDER BY buckets DESC
FORMAT Pretty;

SELECT '=== Aggression Intensity ===' AS report FORMAT Pretty;

SELECT
    CASE
        WHEN aggression_score >= 0.80 THEN 'VERY_HIGH'
        WHEN aggression_score >= 0.60 THEN 'HIGH'
        WHEN aggression_score >= 0.40 THEN 'MODERATE'
        WHEN aggression_score >= 0.20 THEN 'LOW'
        ELSE 'VERY_LOW'
    END AS intensity,
    count() AS buckets,
    round(avg(total_volume), 2) AS avg_volume,
    round(avg(abs_delta), 2) AS avg_delta,
    round(avg(trades_per_second), 2) AS avg_tps
FROM CG_mnq_aggression_100ms
GROUP BY intensity
ORDER BY intensity
FORMAT Pretty;

SELECT '=== Daily Aggression Summary ===' AS report FORMAT Pretty;

SELECT
    trade_date,
    count() AS buckets,
    round(sum(total_volume), 0) AS total_volume,
    round(sum(buy_volume), 0) AS buy_vol,
    round(sum(sell_volume), 0) AS sell_vol,
    round(sum(delta), 0) AS net_delta,
    countIf(aggression_side IN ('STRONG_BUY', 'BUY_AGGRESSION')) AS buy_bias_buckets,
    countIf(aggression_side IN ('STRONG_SELL', 'SELL_AGGRESSION')) AS sell_bias_buckets,
    countIf(is_spike = 1) AS spike_buckets
FROM CG_mnq_aggression_100ms
GROUP BY trade_date
ORDER BY trade_date
FORMAT Pretty;

SELECT '=== Top 20 Highest Aggression Buckets ===' AS report FORMAT Pretty;

SELECT
    bucket_time,
    aggression_side,
    total_volume,
    buy_volume,
    sell_volume,
    delta,
    aggression_score,
    trades_per_second,
    is_spike
FROM CG_mnq_aggression_100ms
ORDER BY aggression_score DESC, abs_delta DESC
LIMIT 20
FORMAT Pretty;

-- ============================================================================
-- NEXT STEP: Phase 6 - Price Response Engine (forward-looking outcomes)
-- ============================================================================
