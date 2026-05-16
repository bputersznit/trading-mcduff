-- ============================================================================
-- McDuff Diagnostic Queries - Post-Fix Validation
-- ============================================================================
-- Purpose: Verify that deduplication + outcome classification fixed the issues
-- Date: 2026-05-04
-- ============================================================================

-- ============================================================================
-- DIAGNOSTIC 1: Trade Count by Day
-- ============================================================================
-- Expected: Sept 23 should NOT have 86K+ interactions

SELECT '=== Daily Interaction Count (Deduped) ===' AS report FORMAT Pretty;

SELECT
    trade_date,
    count() AS dedup_interactions
FROM CG_mnq_wall_interactions_dedup_v1
GROUP BY trade_date
ORDER BY trade_date
FORMAT Pretty;

-- ============================================================================
-- DIAGNOSTIC 2: Outcome Distribution by Wall Behavior
-- ============================================================================
-- Expected: NOT 99.98% BREAK - should see balanced REJECT/BREAK/TWO_WAY

SELECT '=== Outcome Distribution by Wall Behavior ===' AS report FORMAT Pretty;

SELECT
    wall_behavior,
    outcome_label_30s,
    count() AS interactions,
    round(100 * count() / sum(count()) OVER (PARTITION BY wall_behavior), 2) AS pct_within_behavior,
    round(avg(reject_ticks_30s), 2) AS avg_reject_ticks,
    round(avg(break_ticks_30s), 2) AS avg_break_ticks
FROM CG_mnq_wall_interactions_outcome_v1
WHERE wall_behavior != ''
GROUP BY
    wall_behavior,
    outcome_label_30s
ORDER BY
    wall_behavior,
    interactions DESC
FORMAT Pretty;

-- ============================================================================
-- DIAGNOSTIC 3: Overall Outcome Balance
-- ============================================================================
-- Expected: Reject rate should be >5%, not 0.023%

SELECT '=== Overall Outcome Distribution ===' AS report FORMAT Pretty;

SELECT
    outcome_label_30s,
    count() AS interactions,
    round(100 * count() / sum(count()) OVER (), 2) AS pct
FROM CG_mnq_wall_interactions_outcome_v1
GROUP BY outcome_label_30s
ORDER BY interactions DESC
FORMAT Pretty;

-- ============================================================================
-- DIAGNOSTIC 4: Find Explosive Days
-- ============================================================================
-- Expected: No single day should dominate (like 86K out of 100K)

SELECT '=== Explosive Day Detection ===' AS report FORMAT Pretty;

SELECT
    trade_date,
    count() AS interactions,
    countDistinct(wall_side, wall_price) AS unique_price_levels,
    round(count() / greatest(countDistinct(wall_side, wall_price), 1), 2) AS touches_per_level
FROM CG_mnq_wall_interactions_dedup_v1
GROUP BY trade_date
ORDER BY interactions DESC
FORMAT Pretty;

-- ============================================================================
-- DIAGNOSTIC 5: Episode Duration Analysis
-- ============================================================================
-- Expected: Episodes should have reasonable durations (not 0ms)

SELECT '=== Episode Duration Analysis ===' AS report FORMAT Pretty;

WITH episode_stats AS (
    SELECT
        wall_side,
        wall_price,
        episode_num,
        min(first_touch_time) AS episode_start,
        max(first_touch_time) AS episode_end,
        count() AS touches_in_episode
    FROM CG_mnq_wall_interactions_dedup_v1
    GROUP BY wall_side, wall_price, episode_num
)
SELECT
    multiIf(
        touches_in_episode = 1, 'Single touch',
        touches_in_episode < 5, '2-4 touches',
        touches_in_episode < 10, '5-9 touches',
        '10+ touches'
    ) AS touch_bucket,
    count() AS episodes,
    round(avg(dateDiff('second', episode_start, episode_end)), 2) AS avg_duration_sec
FROM episode_stats
GROUP BY touch_bucket
ORDER BY
    CASE touch_bucket
        WHEN 'Single touch' THEN 1
        WHEN '2-4 touches' THEN 2
        WHEN '5-9 touches' THEN 3
        WHEN '10+ touches' THEN 4
    END
FORMAT Pretty;

-- ============================================================================
-- DIAGNOSTIC 6: Wall Behavior × Outcome Expectancy
-- ============================================================================
-- Expected: Some wall behaviors should show positive expectancy (reject > break)

SELECT '=== Wall Behavior Expectancy Analysis ===' AS report FORMAT Pretty;

SELECT
    wall_behavior,
    wall_side,
    count() AS interactions,
    round(avgIf(reject_ticks_30s, outcome_label_30s = 'REJECT'), 2) AS avg_reject_move,
    round(avgIf(break_ticks_30s, outcome_label_30s = 'BREAK'), 2) AS avg_break_move,
    countIf(outcome_label_30s = 'REJECT') AS reject_count,
    countIf(outcome_label_30s = 'BREAK') AS break_count,
    round(100 * countIf(outcome_label_30s = 'REJECT') / count(), 2) AS reject_rate_pct
FROM CG_mnq_wall_interactions_outcome_v1
WHERE wall_behavior != ''
GROUP BY wall_behavior, wall_side
ORDER BY interactions DESC
FORMAT Pretty;

-- ============================================================================
-- DIAGNOSTIC 7: High-Quality Rejection Patterns
-- ============================================================================
-- Expected: Find patterns where REJECT occurs frequently with good magnitude

SELECT '=== High-Quality Rejection Patterns ===' AS report FORMAT Pretty;

SELECT
    wall_behavior,
    wall_side,
    count() AS interactions,
    countIf(outcome_label_30s = 'REJECT') AS rejections,
    round(100 * countIf(outcome_label_30s = 'REJECT') / count(), 2) AS reject_rate,
    round(avgIf(reject_ticks_30s, outcome_label_30s = 'REJECT'), 2) AS avg_reject_magnitude,
    round(avg(wall_size), 0) AS avg_wall_size
FROM CG_mnq_wall_interactions_outcome_v1
WHERE wall_behavior != ''
GROUP BY wall_behavior, wall_side
HAVING interactions >= 10 AND reject_rate >= 10
ORDER BY reject_rate DESC, avg_reject_magnitude DESC
LIMIT 20
FORMAT Pretty;

-- ============================================================================
-- DIAGNOSTIC 8: Reduction Verification
-- ============================================================================
-- Expected: Significant reduction from original 100K rows

SELECT '=== Deduplication Impact ===' AS report FORMAT Pretty;

WITH original AS (
    SELECT count() AS rows FROM CG_mnq_wall_interactions
),
deduped AS (
    SELECT count() AS rows FROM CG_mnq_wall_interactions_dedup_v1
),
with_outcome AS (
    SELECT count() AS rows FROM CG_mnq_wall_interactions_outcome_v1
)
SELECT
    'Original (tick-based)' AS stage,
    (SELECT rows FROM original) AS row_count
UNION ALL
SELECT
    'After deduplication',
    (SELECT rows FROM deduped)
UNION ALL
SELECT
    'After outcome classification',
    (SELECT rows FROM with_outcome)
FORMAT Pretty;

SELECT
    (SELECT count() FROM CG_mnq_wall_interactions) AS original,
    (SELECT count() FROM CG_mnq_wall_interactions_dedup_v1) AS deduped,
    round((SELECT count() FROM CG_mnq_wall_interactions_dedup_v1) * 100.0 / (SELECT count() FROM CG_mnq_wall_interactions), 2) AS pct_remaining,
    round((1 - (SELECT count() FROM CG_mnq_wall_interactions_dedup_v1) * 1.0 / (SELECT count() FROM CG_mnq_wall_interactions)) * 100, 2) AS pct_eliminated
FORMAT Pretty;

-- ============================================================================
-- DIAGNOSTIC 9: Sept 23 Deep Dive
-- ============================================================================
-- Expected: Sept 23 should now be reasonable, not 86K

SELECT '=== Sept 23 Explosion Analysis ===' AS report FORMAT Pretty;

SELECT
    'Sept 23 Original' AS dataset,
    countIf(toDate(interaction_time) = '2025-09-23') AS row_count
FROM CG_mnq_wall_interactions
UNION ALL
SELECT
    'Sept 23 Deduped',
    countIf(trade_date = '2025-09-23')
FROM CG_mnq_wall_interactions_dedup_v1
FORMAT Pretty;

-- ============================================================================
-- DIAGNOSTIC 10: Ready for Backtest Check
-- ============================================================================
-- Expected: Reasonable number of tradeable setups with meaningful wall behaviors

SELECT '=== Tradeable Setups Available ===' AS report FORMAT Pretty;

SELECT
    wall_behavior,
    count() AS total_interactions,
    countIf(outcome_label_30s = 'REJECT') AS reject_setups,
    countIf(outcome_label_30s = 'BREAK') AS break_setups,
    round(avgIf(reject_ticks_30s, outcome_label_30s = 'REJECT'), 2) AS avg_reject_ticks,
    round(avgIf(break_ticks_30s, outcome_label_30s = 'BREAK'), 2) AS avg_break_ticks
FROM CG_mnq_wall_interactions_outcome_v1
WHERE wall_behavior IN ('REPLENISHING_WALL', 'PULLED_WALL', 'ICEBERG_LIKE_WALL')
GROUP BY wall_behavior
ORDER BY total_interactions DESC
FORMAT Pretty;
