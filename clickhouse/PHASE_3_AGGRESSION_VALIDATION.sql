-- ============================================================================
-- PHASE 3: Aggression Layer Validation & Edge Discovery
-- ============================================================================

-- ============================================================================
-- VALIDATION 1: Row count parity
-- ============================================================================
SELECT '=== ROW COUNT PARITY ===' AS report FORMAT Pretty;

SELECT
    'outcome_v1' AS table_name,
    count() AS rows
FROM CG_mnq_wall_interactions_outcome_v1

UNION ALL

SELECT
    'enriched_v2' AS table_name,
    count() AS rows
FROM CG_mnq_wall_outcomes_enriched_v2
FORMAT Pretty;

-- ============================================================================
-- VALIDATION 2: Outcome distribution preserved
-- ============================================================================
SELECT '=== OUTCOME DISTRIBUTION PRESERVED ===' AS report FORMAT Pretty;

SELECT
    outcome_label_30s,
    count() AS n,
    round(100 * count() / sum(count()) OVER (), 2) AS pct
FROM CG_mnq_wall_outcomes_enriched_v2
GROUP BY outcome_label_30s
ORDER BY n DESC
FORMAT Pretty;

-- ============================================================================
-- VALIDATION 3: Delta pattern distribution (CRITICAL FOR MCDUFF)
-- ============================================================================
SELECT '=== DELTA FLIP PATTERN DISTRIBUTION ===' AS report FORMAT Pretty;

SELECT
    delta_flip_pattern,
    count() AS n,
    round(100 * count() / sum(count()) OVER (), 2) AS pct
FROM CG_mnq_wall_outcomes_enriched_v2
GROUP BY delta_flip_pattern
ORDER BY n DESC
FORMAT Pretty;

-- ============================================================================
-- VALIDATION 4: Wall aggression pattern distribution (CRITICAL FOR MCDUFF)
-- ============================================================================
SELECT '=== WALL AGGRESSION PATTERN DISTRIBUTION ===' AS report FORMAT Pretty;

SELECT
    wall_side,
    wall_aggression_pattern,
    count() AS n,
    round(avg(net_aggression_into_wall_5s), 2) AS avg_net_aggr
FROM CG_mnq_wall_outcomes_enriched_v2
GROUP BY wall_side, wall_aggression_pattern
ORDER BY wall_side, n DESC
FORMAT Pretty;

-- ============================================================================
-- EDGE DISCOVERY 1: Outcome by wall behavior + delta flip (CRITICAL FOR MCDUFF)
-- ============================================================================
SELECT '=== OUTCOME × WALL BEHAVIOR × DELTA FLIP ===' AS report FORMAT Pretty;

SELECT
    wall_side,
    wall_behavior,
    delta_flip_pattern,
    outcome_label_30s,
    count() AS n,
    round(100 * count() / sum(count()) OVER (
        PARTITION BY wall_side, wall_behavior, delta_flip_pattern
    ), 2) AS outcome_pct,
    round(avg(reject_ticks_30s), 2) AS avg_reject_ticks,
    round(avg(break_ticks_30s), 2) AS avg_break_ticks
FROM CG_mnq_wall_outcomes_enriched_v2
GROUP BY
    wall_side,
    wall_behavior,
    delta_flip_pattern,
    outcome_label_30s
HAVING n >= 10
ORDER BY
    wall_side,
    wall_behavior,
    delta_flip_pattern,
    outcome_pct DESC
FORMAT Pretty;

-- ============================================================================
-- EDGE DISCOVERY 2: Candidate strategy direction (CRITICAL FOR MCDUFF)
-- ============================================================================
SELECT '=== CANDIDATE STRATEGY PATTERNS ===' AS report FORMAT Pretty;

SELECT
    wall_side,
    wall_behavior,
    delta_flip_pattern,
    count() AS n,
    countIf(outcome_label_30s = 'REJECT') AS rejects,
    countIf(outcome_label_30s = 'BREAK') AS breaks,
    round(100 * rejects / n, 2) AS reject_pct,
    round(100 * breaks / n, 2) AS break_pct,
    round(avg(reject_ticks_30s), 2) AS avg_reject_ticks,
    round(avg(break_ticks_30s), 2) AS avg_break_ticks,
    multiIf(
        reject_pct >= 45 AND reject_pct > break_pct * 1.5, 'FADE_WALL_REJECT',
        break_pct >= 45 AND break_pct > reject_pct * 1.5, 'FOLLOW_WALL_BREAK',
        'NO_CLEAR_EDGE'
    ) AS candidate_strategy
FROM CG_mnq_wall_outcomes_enriched_v2
GROUP BY
    wall_side,
    wall_behavior,
    delta_flip_pattern
HAVING n >= 20
ORDER BY
    candidate_strategy DESC,
    greatest(reject_pct, break_pct) DESC,
    n DESC
FORMAT Pretty;

