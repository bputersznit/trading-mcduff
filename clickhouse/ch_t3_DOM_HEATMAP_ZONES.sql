-- ============================================================================
-- T3 DOM HEATMAP ZONE STRATEGY - Institutional Flow Detection
-- ============================================================================
--
-- Detects stacked liquidity zones and adjudicates between:
--   MARGINAL: Weak zones (retail, spoofs, likely to break)
--   SIGNIFICANT: Strong zones (institutional, conviction, tradeable)
--
-- Only trades SIGNIFICANT zones with confirmed breakout/rejection
--
-- ============================================================================

WITH

-- ============================================================================
-- STEP 1: Reconstruct Order Book State (Adds - Cancels)
-- ============================================================================

order_book_events AS (
    SELECT
        toStartOfSecond(ts_event, 'America/New_York') as ts_sec,
        price,
        side,
        action,
        size,

        -- Track adds vs cancels for spoof detection
        CASE WHEN action = 'A' THEN size ELSE 0 END as size_added,
        CASE WHEN action = 'C' THEN size ELSE 0 END as size_cancelled

    FROM mnq_mbo
    WHERE symbol = 'MNQZ5'
      AND toDate(ts_event, 'America/New_York') >= '2025-10-01'  -- Best trending day
      AND toDate(ts_event, 'America/New_York') <= '2025-10-01'
      AND action IN ('A', 'C')  -- Only book events (not trades)
      AND side IN ('A', 'B')    -- Ask and Bid
      AND toHour(ts_event, 'America/New_York') BETWEEN 9 AND 15  -- RTH
),

book_snapshot AS (
    SELECT
        ts_sec,
        price,
        side,

        -- Net resting size at this level
        sum(size_added) - sum(size_cancelled) as net_size,

        -- Spoof detection: cancel ratio
        sum(size_cancelled) as total_cancelled,
        sum(size_added) as total_added,

        CASE
            WHEN sum(size_added) > 0
            THEN sum(size_cancelled) / sum(size_added)
            ELSE 0
        END as cancel_ratio

    FROM order_book_events
    GROUP BY ts_sec, price, side
    HAVING net_size > 0  -- Only levels with resting orders
),

-- ============================================================================
-- STEP 2: Detect Individual Large Levels (Building Blocks)
-- ============================================================================

large_levels AS (
    SELECT
        ts_sec,
        price,
        side,
        net_size,
        cancel_ratio,

        -- Level quality score
        CASE
            WHEN net_size >= 150 THEN 3  -- STRONG (institutional size)
            WHEN net_size >= 80 THEN 2   -- MODERATE
            WHEN net_size >= 40 THEN 1   -- WEAK (marginal)
            ELSE 0
        END as level_strength

    FROM book_snapshot
    WHERE net_size >= 40  -- Minimum threshold
      AND cancel_ratio < 0.6  -- Filter obvious spoofs
),

-- ============================================================================
-- STEP 3: Build Stacked Zones (Consecutive Levels)
-- ============================================================================

-- For each level, calculate zone metrics over nearby levels
zone_candidates AS (
    SELECT
        ts_sec,
        price,
        side,
        net_size,
        cancel_ratio,
        level_strength,

        -- Zone metrics: 5-level window (±2 levels = ~0.5-1.0 point range)
        sum(net_size) OVER (
            PARTITION BY ts_sec, side
            ORDER BY price
            ROWS BETWEEN 2 PRECEDING AND 2 FOLLOWING
        ) as zone_total_size,

        count(*) OVER (
            PARTITION BY ts_sec, side
            ORDER BY price
            ROWS BETWEEN 2 PRECEDING AND 2 FOLLOWING
        ) as zone_levels_count,

        sum(level_strength) OVER (
            PARTITION BY ts_sec, side
            ORDER BY price
            ROWS BETWEEN 2 PRECEDING AND 2 FOLLOWING
        ) as zone_strength_score,

        avg(cancel_ratio) OVER (
            PARTITION BY ts_sec, side
            ORDER BY price
            ROWS BETWEEN 2 PRECEDING AND 2 FOLLOWING
        ) as zone_avg_cancel_ratio,

        min(price) OVER (
            PARTITION BY ts_sec, side
            ORDER BY price
            ROWS BETWEEN 2 PRECEDING AND 2 FOLLOWING
        ) as zone_low,

        max(price) OVER (
            PARTITION BY ts_sec, side
            ORDER BY price
            ROWS BETWEEN 2 PRECEDING AND 2 FOLLOWING
        ) as zone_high

    FROM large_levels
),

-- ============================================================================
-- STEP 4: Classify Zones (MARGINAL vs SIGNIFICANT)
-- ============================================================================

classified_zones AS (
    SELECT
        ts_sec,
        side,
        zone_low,
        zone_high,
        zone_high - zone_low as zone_width,
        zone_total_size,
        zone_levels_count,
        zone_strength_score,
        zone_avg_cancel_ratio,

        -- ZONE QUALITY CLASSIFICATION
        CASE
            -- SIGNIFICANT: Institutional conviction
            WHEN zone_total_size >= 800          -- Large total size
              AND zone_levels_count >= 5         -- Many consecutive levels
              AND zone_strength_score >= 10      -- Strong individual levels
              AND zone_avg_cancel_ratio < 0.25   -- Low spoofing
            THEN 'SIGNIFICANT'

            -- MODERATE: Possible institutional, needs confirmation
            WHEN zone_total_size >= 500
              AND zone_levels_count >= 4
              AND zone_strength_score >= 6
              AND zone_avg_cancel_ratio < 0.35
            THEN 'MODERATE'

            -- MARGINAL: Weak zone, likely retail or spoof
            ELSE 'MARGINAL'
        END as zone_classification,

        -- Composite zone quality score (0-100)
        LEAST(100,
            (zone_total_size / 10.0) +                    -- Size component (max 50)
            (zone_levels_count * 5) +                     -- Levels component (max 25)
            (zone_strength_score * 2) +                   -- Strength component (max 30)
            ((1 - zone_avg_cancel_ratio) * 20)            -- Legitimacy component (max 20)
        ) as zone_quality_score

    FROM zone_candidates
    WHERE zone_total_size >= 400  -- Minimum zone threshold
      AND zone_levels_count >= 3
),

-- Deduplicate zones (same zone can be detected from multiple center points)
unique_zones AS (
    SELECT
        ts_sec,
        side,
        zone_low,
        zone_high,
        zone_width,
        zone_total_size,
        zone_levels_count,
        zone_classification,
        zone_quality_score,
        zone_avg_cancel_ratio,

        -- Keep only the strongest representation of each zone
        row_number() OVER (
            PARTITION BY ts_sec, side,
                round(zone_low, 0),  -- Group by approximate zone location
                round(zone_high, 0)
            ORDER BY zone_quality_score DESC
        ) as zone_rank

    FROM classified_zones
),

final_zones AS (
    SELECT *
    FROM unique_zones
    WHERE zone_rank = 1  -- Keep best version of each zone
),

-- ============================================================================
-- STEP 5: Zone Persistence Tracking (How Long Does Zone Last?)
-- ============================================================================

zone_persistence AS (
    SELECT
        *,

        -- How many consecutive seconds does this zone persist?
        -- (Approximation: count how many times similar zone appears in next 10 seconds)
        (SELECT count(*)
         FROM final_zones z2
         WHERE z2.side = final_zones.side
           AND z2.ts_sec BETWEEN final_zones.ts_sec AND final_zones.ts_sec + INTERVAL 10 SECOND
           AND abs(z2.zone_low - final_zones.zone_low) < 2.0  -- Same general area
           AND abs(z2.zone_high - final_zones.zone_high) < 2.0
        ) as zone_persistence_count

    FROM final_zones
),

-- ============================================================================
-- STEP 6: Get Trade Prices for Breakout Detection
-- ============================================================================

trade_prices AS (
    SELECT
        toStartOfSecond(ts_event, 'America/New_York') as ts_sec,
        ts_event,
        price,
        side,
        size,

        -- Aggressor direction
        CASE
            WHEN side = 'A' THEN 'BUY'   -- Trade at ask = buy aggression
            WHEN side = 'B' THEN 'SELL'  -- Trade at bid = sell aggression
        END as aggressor_side

    FROM mnq_mbo
    WHERE symbol = 'MNQZ5'
      AND toDate(ts_event, 'America/New_York') = '2025-10-01'
      AND action IN ('T', 'F')  -- Trades only
      AND toHour(ts_event, 'America/New_York') BETWEEN 9 AND 15
),

trade_summary AS (
    SELECT
        ts_sec,

        -- Price action
        min(price) as low,
        max(price) as high,
        anyLast(price) as close,  -- Use anyLast instead of argMax

        -- Volume by aggressor
        sumIf(size, aggressor_side = 'BUY') as buy_volume,
        sumIf(size, aggressor_side = 'SELL') as sell_volume

    FROM trade_prices
    GROUP BY ts_sec
),

-- ============================================================================
-- STEP 7: Generate Signals (Breakout Through SIGNIFICANT Zones)
-- ============================================================================

signals AS (
    SELECT
        z.ts_sec,
        z.side,
        z.zone_low,
        z.zone_high,
        z.zone_width,
        z.zone_total_size,
        z.zone_levels_count,
        z.zone_classification,
        z.zone_quality_score,
        z.zone_persistence_count,

        t.close as current_price,
        t.buy_volume,
        t.sell_volume,
        t.buy_volume - t.sell_volume as volume_delta,

        -- SIGNAL LOGIC: Breakout through SIGNIFICANT zones only
        CASE
            -- LONG: Bid zone detected, price breaks ABOVE zone (institutions failed to defend, now buying)
            WHEN z.side = 'B'  -- Bid zone
              AND z.zone_classification = 'SIGNIFICANT'
              AND t.close > z.zone_high + 1.0  -- Breakout above zone + 1 point buffer
              AND t.buy_volume > z.zone_total_size * 0.3  -- Aggressive buying exceeded 30% of zone size
            THEN 'LONG'

            -- SHORT: Ask zone detected, price breaks BELOW zone (institutions failed to cap, now selling)
            WHEN z.side = 'A'  -- Ask zone
              AND z.zone_classification = 'SIGNIFICANT'
              AND t.close < z.zone_low - 1.0  -- Breakout below zone - 1 point buffer
              AND t.sell_volume > z.zone_total_size * 0.3  -- Aggressive selling exceeded 30% of zone size
            THEN 'SHORT'

            ELSE NULL
        END as signal_type,

        -- For tracking: also flag MODERATE zones for analysis
        CASE
            WHEN z.zone_classification = 'MODERATE'
              AND ((z.side = 'B' AND t.close > z.zone_high + 0.5)
                OR (z.side = 'A' AND t.close < z.zone_low - 0.5))
            THEN 'MODERATE_SIGNAL'
            ELSE NULL
        END as moderate_signal

    FROM zone_persistence z
    LEFT JOIN trade_summary t
        ON z.ts_sec = t.ts_sec
    WHERE t.close IS NOT NULL  -- Only when we have price data
),

valid_signals AS (
    SELECT *
    FROM signals
    WHERE signal_type IS NOT NULL
)

-- ============================================================================
-- RESULTS
-- ============================================================================

SELECT '════════════════════════════════════════════════════════════════════════' as output
UNION ALL SELECT '   T3 DOM HEATMAP ZONE STRATEGY - Oct 1, 2025'
UNION ALL SELECT '════════════════════════════════════════════════════════════════════════'
UNION ALL SELECT ''
UNION ALL SELECT '--- Zone Classification Criteria ---'
UNION ALL SELECT 'MARGINAL:    <500 size, <4 levels, weak, possible spoof'
UNION ALL SELECT 'MODERATE:    500-800 size, 4-5 levels, needs confirmation'
UNION ALL SELECT 'SIGNIFICANT: 800+ size, 5+ levels, <25% cancel ratio, TRADEABLE'
UNION ALL SELECT ''
UNION ALL SELECT '--- Signal Logic ---'
UNION ALL SELECT 'LONG:  Bid zone (SIGNIFICANT) + price breaks ABOVE + buy volume > 30% zone size'
UNION ALL SELECT 'SHORT: Ask zone (SIGNIFICANT) + price breaks BELOW + sell volume > 30% zone size'
UNION ALL SELECT ''
UNION ALL SELECT '--- Zone Detection Summary ---'
UNION ALL SELECT concat('Total Zones Detected: ', toString((SELECT count(*) FROM final_zones)))
UNION ALL SELECT concat('  SIGNIFICANT: ', toString((SELECT countIf(zone_classification = 'SIGNIFICANT') FROM final_zones)))
UNION ALL SELECT concat('  MODERATE: ', toString((SELECT countIf(zone_classification = 'MODERATE') FROM final_zones)))
UNION ALL SELECT concat('  MARGINAL: ', toString((SELECT countIf(zone_classification = 'MARGINAL') FROM final_zones)))
UNION ALL SELECT ''
UNION ALL SELECT concat('Breakout Signals: ', toString((SELECT count(*) FROM valid_signals)))
UNION ALL SELECT concat('  LONG Signals: ', toString((SELECT countIf(signal_type = 'LONG') FROM valid_signals)))
UNION ALL SELECT concat('  SHORT Signals: ', toString((SELECT countIf(signal_type = 'SHORT') FROM valid_signals)))
UNION ALL SELECT ''
UNION ALL SELECT '--- Top 10 SIGNIFICANT Zones (Highest Quality) ---'
UNION ALL SELECT concat(
    formatDateTime(ts_sec, '%H:%M:%S', 'America/New_York'), ' | ',
    CASE WHEN side = 'B' THEN 'BID ' ELSE 'ASK ' END, ' | ',
    toString(round(zone_low, 2)), '-', toString(round(zone_high, 2)), ' | ',
    'Size: ', leftPad(toString(zone_total_size), 4, ' '), ' | ',
    'Levels: ', toString(zone_levels_count), ' | ',
    'Quality: ', leftPad(toString(round(zone_quality_score, 0)), 3, ' '), ' | ',
    'Persist: ', toString(zone_persistence_count), 's'
)
FROM final_zones
WHERE zone_classification = 'SIGNIFICANT'
ORDER BY zone_quality_score DESC
LIMIT 10

UNION ALL SELECT ''
UNION ALL SELECT '--- Signal Details (SIGNIFICANT Breakouts Only) ---'
UNION ALL SELECT concat(
    formatDateTime(ts_sec, '%H:%M:%S', 'America/New_York'), ' | ',
    signal_type, ' | ',
    'Zone: ', toString(round(zone_low, 2)), '-', toString(round(zone_high, 2)), ' | ',
    'Price: ', toString(round(current_price, 2)), ' | ',
    'ZoneSize: ', toString(zone_total_size), ' | ',
    'VolDelta: ', toString(volume_delta), ' | ',
    'Quality: ', toString(round(zone_quality_score, 0))
)
FROM valid_signals
ORDER BY ts_sec

UNION ALL SELECT ''
UNION ALL SELECT '--- Zone Classification Distribution ---'
UNION ALL SELECT concat('SIGNIFICANT Zones - Avg Size: ', toString(round((SELECT avg(zone_total_size) FROM final_zones WHERE zone_classification = 'SIGNIFICANT'), 0)))
UNION ALL SELECT concat('MODERATE Zones    - Avg Size: ', toString(round((SELECT avg(zone_total_size) FROM final_zones WHERE zone_classification = 'MODERATE'), 0)))
UNION ALL SELECT concat('MARGINAL Zones    - Avg Size: ', toString(round((SELECT avg(zone_total_size) FROM final_zones WHERE zone_classification = 'MARGINAL'), 0)))
UNION ALL SELECT ''
UNION ALL SELECT concat('SIGNIFICANT Zones - Avg Quality Score: ', toString(round((SELECT avg(zone_quality_score) FROM final_zones WHERE zone_classification = 'SIGNIFICANT'), 0)))
UNION ALL SELECT concat('MODERATE Zones    - Avg Quality Score: ', toString(round((SELECT avg(zone_quality_score) FROM final_zones WHERE zone_classification = 'MODERATE'), 0)))
UNION ALL SELECT concat('MARGINAL Zones    - Avg Quality Score: ', toString(round((SELECT avg(zone_quality_score) FROM final_zones WHERE zone_classification = 'MARGINAL'), 0)))
UNION ALL SELECT ''
UNION ALL SELECT '════════════════════════════════════════════════════════════════════════'
UNION ALL SELECT ''
UNION ALL SELECT 'Key Insight: Only SIGNIFICANT zones generate signals'
UNION ALL SELECT 'MODERATE/MARGINAL zones ignored to prevent false breakouts'
UNION ALL SELECT ''
UNION ALL SELECT '════════════════════════════════════════════════════════════════════════'

FORMAT TSVRaw;
