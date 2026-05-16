-- ============================================================================
-- T3 DOM ZONE DETECTION - Simplified Version
-- ============================================================================
-- Detects and classifies liquidity zones: MARGINAL vs SIGNIFICANT
-- ============================================================================

WITH

-- Reconstruct book: net resting size at each price level per second
book_levels AS (
    SELECT
        toStartOfSecond(ts_event, 'America/New_York') as ts_sec,
        price,
        side,
        sumIf(size, action = 'A') - sumIf(size, action = 'C') as net_size,
        sumIf(size, action = 'C') / nullIf(sumIf(size, action = 'A'), 0) as cancel_ratio
    FROM mnq_mbo
    WHERE symbol = 'MNQZ5'
      AND toDate(ts_event, 'America/New_York') = '2025-10-01'
      AND action IN ('A', 'C')
      AND side IN ('A', 'B')
      AND toHour(ts_event, 'America/New_York') BETWEEN 10 AND 14
    GROUP BY ts_sec, price, side
    HAVING net_size > 40  -- Minimum level threshold
      AND cancel_ratio < 0.6  -- Filter obvious spoofs
),

-- Build zones: sum size across nearby price levels
zones AS (
    SELECT
        ts_sec,
        side,
        price as zone_center,

        -- Sum size across ±3 levels window
        sum(net_size) OVER (
            PARTITION BY ts_sec, side
            ORDER BY price
            ROWS BETWEEN 3 PRECEDING AND 3 FOLLOWING
        ) as zone_size,

        count(*) OVER (
            PARTITION BY ts_sec, side
            ORDER BY price
            ROWS BETWEEN 3 PRECEDING AND 3 FOLLOWING
        ) as zone_levels,

        avg(cancel_ratio) OVER (
            PARTITION BY ts_sec, side
            ORDER BY price
            ROWS BETWEEN 3 PRECEDING AND 3 FOLLOWING
        ) as zone_cancel_ratio

    FROM book_levels
),

-- Classify zones
classified_zones AS (
    SELECT
        ts_sec,
        side,
        zone_center,
        zone_size,
        zone_levels,
        zone_cancel_ratio,

        -- Quality score (0-100)
        LEAST(100,
            (zone_size / 10) +                      -- Size (max 50)
            (zone_levels * 5) +                     -- Levels (max 25)
            ((1 - zone_cancel_ratio) * 25)          -- Legitimacy (max 25)
        ) as quality_score,

        -- Classification
        CASE
            WHEN zone_size >= 800 AND zone_levels >= 5 AND zone_cancel_ratio < 0.25
            THEN 'SIGNIFICANT'
            WHEN zone_size >= 500 AND zone_levels >= 4 AND zone_cancel_ratio < 0.35
            THEN 'MODERATE'
            ELSE 'MARGINAL'
        END as classification

    FROM zones
    WHERE zone_size >= 400  -- Minimum zone threshold
      AND zone_levels >= 3
)

-- Results
SELECT
    formatDateTime(ts_sec, '%H:%M:%S', 'America/New_York') as time,
    CASE WHEN side = 'B' THEN 'BID' ELSE 'ASK' END as side,
    round(zone_center, 2) as price,
    zone_size,
    zone_levels,
    round(zone_cancel_ratio * 100, 1) as cancel_pct,
    round(quality_score, 0) as quality,
    classification
FROM classified_zones
WHERE classification IN ('SIGNIFICANT', 'MODERATE')
ORDER BY quality_score DESC
LIMIT 50
FORMAT Vertical
