-- ============================================================
-- CG_mnq_pattern_expansion_v1
-- Pattern expansion test: CORE vs EXPANDED patterns
--
-- Purpose:
--   - Test if core LONG continuation edge extends to broader patterns
--   - Classify patterns by quality state (HIGH, MODERATE, VWAP)
--   - Search for EXPANDED pattern candidates (NEUTRAL, quality floor)
--
-- Result:
--   - 11 trades (all CORE patterns, 0 EXPANDED)
--   - No NEUTRAL or quality floor candidates exist
--   - CORE patterns = complete tradable set
--
-- Categories:
--   - CORE_ORB_HIGH: STRENGTHENING_HIGH (1 trade, 100% WR)
--   - CORE_ORB_MODERATE: STRENGTHENING_MODERATE (3 trades, 100% WR)
--   - CORE_VWAP_RECLAIM: EXHAUSTING (7 trades, 85.71% WR)
--
-- Exclusions:
--   - All SHORT patterns (losing)
--   - LONG_ORB_LOW patterns (0% WR)
--   - LONG_VWAP_SUPPORT patterns (0% WR)
-- ============================================================

DROP TABLE IF EXISTS CG_mnq_pattern_expansion_v1;

CREATE TABLE CG_mnq_pattern_expansion_v1
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, entry_time, expansion_class, trigger_type)
AS
SELECT
    e.trade_date,
    e.trade_sequence,
    e.structure_id,

    -- Expansion classification
    multiIf(
        -- CORE: ORB_HIGH with STRENGTHENING_HIGH quality
        e.trigger_type = 'LONG_ORB_HIGH_BREAKOUT_CONTINUATION'
        AND e.aggression_quality_state = 'STRENGTHENING_HIGH',
        'CORE_ORB_HIGH',

        -- CORE: ORB_HIGH with STRENGTHENING_MODERATE quality
        e.trigger_type = 'LONG_ORB_HIGH_BREAKOUT_CONTINUATION'
        AND e.aggression_quality_state = 'STRENGTHENING_MODERATE',
        'CORE_ORB_MODERATE',

        -- CORE: VWAP_RESISTANCE_RECLAIM (all have EXHAUSTING quality)
        e.trigger_type = 'LONG_VWAP_RESISTANCE_RECLAIM',
        'CORE_VWAP_RECLAIM',

        -- Everything else excluded
        'EXCLUDED'
    ) AS expansion_class,

    e.trigger_type,
    e.trigger_side,
    e.level_type,
    e.structure_side,
    e.maturity_state,
    e.aggression_quality_state,

    e.entry_time,
    e.entry_price,
    e.target_price,
    e.stop_price,
    e.timeout_time,

    e.target_hit_time,
    e.stop_hit_time,
    e.exit_time,
    e.exit_price,
    e.outcome,

    e.gross_pnl_ticks,
    e.net_pnl_ticks_after_cost_floor,
    e.hold_seconds,

    e.trigger_priority_score,
    e.end_weighted_alignment_score,
    e.alignment_quality_delta,
    e.alignment_quality_peak,

    e.touch_count,
    e.time_in_structure_secs,
    e.range_width_pts,

    e.trigger_time_bucket,
    e.trigger_trend_bias,
    e.trigger_vwap_relation,
    e.trigger_orb_state

FROM CG_mnq_pattern_filtered_v1 AS e
WHERE e.is_core_pattern = 1;

-- ============================================================
-- Validation Queries
-- ============================================================

-- 1. Category breakdown
SELECT
    expansion_class,
    COUNT(*) AS trades,
    SUM(net_pnl_ticks_after_cost_floor) AS net_ticks,
    ROUND(AVG(net_pnl_ticks_after_cost_floor), 2) AS avg_per_trade,
    ROUND(100.0 * countIf(outcome = 'TARGET') / COUNT(*), 2) AS win_rate_pct
FROM CG_mnq_pattern_expansion_v1
GROUP BY expansion_class
ORDER BY expansion_class;

-- Expected output:
-- CORE_ORB_HIGH:     1 trade,  +38 ticks (+38/trade),   100% WR
-- CORE_ORB_MODERATE: 3 trades, +114 ticks (+38/trade),  100% WR
-- CORE_VWAP_RECLAIM: 7 trades, +206 ticks (+29.43/trade), 85.71% WR
-- Total:            11 trades, +358 ticks (+32.55/trade), 90.91% WR

-- 2. Check for potential EXPANDED candidates in source
SELECT COUNT(*) AS expanded_candidates
FROM CG_mnq_price_trigger_path_outcomes_v1
WHERE trigger_side = 'LONG'
  AND level_type IN ('ORB_HIGH', 'VWAP')
  AND structure_side = 'RESISTANCE'
  AND aggression_quality_state NOT IN ('STRENGTHENING_HIGH', 'STRENGTHENING_MODERATE', 'EXHAUSTING');

-- Expected output: 0 candidates (no NEUTRAL or other quality states exist)

-- 3. Trade list with NY timestamps
SELECT
    toDateTime(entry_time, 'America/New_York') AS entry_ny,
    expansion_class,
    trigger_type,
    outcome,
    net_pnl_ticks_after_cost_floor AS net
FROM CG_mnq_pattern_expansion_v1
ORDER BY entry_time;

-- 4. Quality state distribution in source
SELECT
    trigger_type,
    aggression_quality_state,
    COUNT(*) AS trades
FROM CG_mnq_price_trigger_path_outcomes_v1
WHERE trigger_side = 'LONG'
  AND level_type IN ('ORB_HIGH', 'VWAP')
  AND structure_side = 'RESISTANCE'
GROUP BY trigger_type, aggression_quality_state
ORDER BY trigger_type, aggression_quality_state;

-- Expected output:
-- LONG_ORB_HIGH_BREAKOUT_CONTINUATION, STRENGTHENING_HIGH:     1 trade
-- LONG_ORB_HIGH_BREAKOUT_CONTINUATION, STRENGTHENING_MODERATE: 3 trades
-- LONG_VWAP_RESISTANCE_RECLAIM, EXHAUSTING:                    7 trades
-- (No other quality states exist)
