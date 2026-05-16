-- ============================================================================
-- STEP 4: Statistical Analysis v2 - WITH CORRECTED OUTCOMES
-- ============================================================================
-- Purpose: Recompute expectancy with FIXED outcome classification
-- Date: 2026-05-04 21:45 ET
-- Changes from v1: Uses CG_mnq_wall_interactions_outcome_v1 with correct outcomes
-- ============================================================================

SELECT '=== OVERALL EXPECTANCY (CORRECTED OUTCOMES) ===' AS report FORMAT Pretty;

WITH expectancy AS (
    SELECT
        count() AS total_interactions,
        
        -- Rejection metrics
        countIf(outcome_label_30s = 'REJECT') AS reject_count,
        avgIf(reject_ticks_30s, outcome_label_30s = 'REJECT') AS avg_reject_ticks,
        
        -- Break metrics
        countIf(outcome_label_30s = 'BREAK') AS break_count,
        avgIf(break_ticks_30s, outcome_label_30s = 'BREAK') AS avg_break_ticks,
        
        -- Other outcomes
        countIf(outcome_label_30s = 'TWO_WAY_VOLATILE') AS two_way_count,
        countIf(outcome_label_30s = 'NO_RESOLUTION') AS no_resolution_count,
        
        -- Calculate fade expectancy (trade against wall)
        -- If BID wall: short on approach, profit from rejection down
        -- If ASK wall: long on approach, profit from rejection up
        avgIf(reject_ticks_30s, outcome_label_30s = 'REJECT') AS fade_win_ticks,
        avgIf(-break_ticks_30s, outcome_label_30s = 'BREAK') AS fade_loss_ticks,
        
        -- Calculate breakout expectancy (trade with wall break)
        avgIf(break_ticks_30s, outcome_label_30s = 'BREAK') AS break_win_ticks,
        avgIf(-reject_ticks_30s, outcome_label_30s = 'REJECT') AS break_loss_ticks
        
    FROM CG_mnq_wall_interactions_outcome_v1
    WHERE wall_behavior != ''
)
SELECT
    total_interactions,
    reject_count,
    round(100.0 * reject_count / total_interactions, 2) AS reject_pct,
    break_count,
    round(100.0 * break_count / total_interactions, 2) AS break_pct,
    two_way_count,
    no_resolution_count,
    
    round(avg_reject_ticks, 2) AS avg_reject_ticks,
    round(avg_break_ticks, 2) AS avg_break_ticks,
    
    -- FADE strategy expectancy (trade against the wall)
    round(fade_win_ticks, 2) AS fade_win_avg,
    round(fade_loss_ticks, 2) AS fade_loss_avg,
    round(
        (reject_count * fade_win_ticks + break_count * fade_loss_ticks) / 
        (reject_count + break_count),
        2
    ) AS fade_net_expectancy_ticks,
    
    -- BREAKOUT strategy expectancy (trade with wall break)
    round(break_win_ticks, 2) AS break_win_avg,
    round(break_loss_ticks, 2) AS break_loss_avg,
    round(
        (break_count * break_win_ticks + reject_count * break_loss_ticks) /
        (reject_count + break_count),
        2
    ) AS breakout_net_expectancy_ticks
    
FROM expectancy
FORMAT Pretty;

-- ============================================================================
-- Wall Behavior × Side Expectancy (CORRECTED)
-- ============================================================================

SELECT '=== WALL BEHAVIOR × SIDE EXPECTANCY (CORRECTED) ===' AS report FORMAT Pretty;

WITH pattern_stats AS (
    SELECT
        wall_behavior,
        wall_side,
        count() AS interactions,
        
        countIf(outcome_label_30s = 'REJECT') AS reject_count,
        countIf(outcome_label_30s = 'BREAK') AS break_count,
        
        avgIf(reject_ticks_30s, outcome_label_30s = 'REJECT') AS avg_reject_ticks,
        avgIf(break_ticks_30s, outcome_label_30s = 'BREAK') AS avg_break_ticks,
        
        -- Fade expectancy (against wall)
        avgIf(reject_ticks_30s, outcome_label_30s = 'REJECT') * countIf(outcome_label_30s = 'REJECT') +
        avgIf(-break_ticks_30s, outcome_label_30s = 'BREAK') * countIf(outcome_label_30s = 'BREAK') AS fade_total_ticks,
        
        -- Breakout expectancy (with wall break)
        avgIf(break_ticks_30s, outcome_label_30s = 'BREAK') * countIf(outcome_label_30s = 'BREAK') +
        avgIf(-reject_ticks_30s, outcome_label_30s = 'REJECT') * countIf(outcome_label_30s = 'REJECT') AS breakout_total_ticks
        
    FROM CG_mnq_wall_interactions_outcome_v1
    WHERE wall_behavior IN ('PULLED_WALL', 'REPLENISHING_WALL', 'ICEBERG_LIKE_WALL')
    GROUP BY wall_behavior, wall_side
    HAVING (reject_count + break_count) >= 10  -- Min 10 resolved outcomes
)
SELECT
    wall_behavior,
    wall_side,
    interactions,
    reject_count,
    break_count,
    round(100.0 * reject_count / (reject_count + break_count), 2) AS reject_rate_pct,
    
    round(avg_reject_ticks, 2) AS avg_reject_move,
    round(avg_break_ticks, 2) AS avg_break_move,
    
    -- FADE strategy (trade against wall)
    round(fade_total_ticks / (reject_count + break_count), 2) AS fade_expectancy_ticks,
    
    -- BREAKOUT strategy (trade with break)
    round(breakout_total_ticks / (reject_count + break_count), 2) AS breakout_expectancy_ticks,
    
    -- Flag positive expectancy patterns
    if(fade_total_ticks / (reject_count + break_count) > 0, '✅ FADE', '') AS fade_edge,
    if(breakout_total_ticks / (reject_count + break_count) > 0, '✅ BREAK', '') AS break_edge
    
FROM pattern_stats
ORDER BY 
    CASE 
        WHEN fade_total_ticks / (reject_count + break_count) > 0 THEN 1
        WHEN breakout_total_ticks / (reject_count + break_count) > 0 THEN 2
        ELSE 3
    END,
    fade_total_ticks / (reject_count + break_count) DESC
FORMAT Pretty;

-- ============================================================================
-- Detailed Pattern Breakdown with Trade Direction
-- ============================================================================

SELECT '=== TRADEABLE PATTERNS WITH EDGE (CORRECTED) ===' AS report FORMAT Pretty;

WITH pattern_stats AS (
    SELECT
        wall_behavior,
        wall_side,
        
        -- Count outcomes
        count() AS total,
        countIf(outcome_label_30s = 'REJECT') AS rejects,
        countIf(outcome_label_30s = 'BREAK') AS breaks,
        
        -- Average moves
        avgIf(reject_ticks_30s, outcome_label_30s = 'REJECT') AS avg_rej,
        avgIf(break_ticks_30s, outcome_label_30s = 'BREAK') AS avg_brk,
        
        -- Fade strategy
        (avgIf(reject_ticks_30s, outcome_label_30s = 'REJECT') * countIf(outcome_label_30s = 'REJECT') +
         avgIf(-break_ticks_30s, outcome_label_30s = 'BREAK') * countIf(outcome_label_30s = 'BREAK')) / 
        (countIf(outcome_label_30s = 'REJECT') + countIf(outcome_label_30s = 'BREAK')) AS fade_exp,
        
        -- Breakout strategy  
        (avgIf(break_ticks_30s, outcome_label_30s = 'BREAK') * countIf(outcome_label_30s = 'BREAK') +
         avgIf(-reject_ticks_30s, outcome_label_30s = 'REJECT') * countIf(outcome_label_30s = 'REJECT')) /
        (countIf(outcome_label_30s = 'REJECT') + countIf(outcome_label_30s = 'BREAK')) AS break_exp
        
    FROM CG_mnq_wall_interactions_outcome_v1
    WHERE wall_behavior IN ('PULLED_WALL', 'REPLENISHING_WALL', 'ICEBERG_LIKE_WALL')
    GROUP BY wall_behavior, wall_side
    HAVING (rejects + breaks) >= 10
)
SELECT
    wall_behavior,
    wall_side,
    
    -- Suggested trade direction
    multiIf(
        fade_exp > 2 AND wall_side = 'ASK', 'SHORT (fade ask wall)',
        fade_exp > 2 AND wall_side = 'BID', 'LONG (fade bid wall)',
        break_exp > 2 AND wall_side = 'ASK', 'LONG (break through ask)',
        break_exp > 2 AND wall_side = 'BID', 'SHORT (break through bid)',
        'NO EDGE'
    ) AS suggested_trade,
    
    total AS setups,
    rejects,
    breaks,
    round(100.0 * rejects / (rejects + breaks), 2) AS reject_rate,
    
    round(avg_rej, 2) AS avg_reject_move,
    round(avg_brk, 2) AS avg_break_move,
    
    round(fade_exp, 2) AS fade_expectancy,
    round(break_exp, 2) AS breakout_expectancy,
    
    -- Highlight best strategy
    if(fade_exp > break_exp AND fade_exp > 2, '⭐ FADE', 
       if(break_exp > fade_exp AND break_exp > 2, '⭐ BREAKOUT', '')) AS best_strategy
    
FROM pattern_stats
WHERE fade_exp > 2 OR break_exp > 2  -- Only show patterns with >2 tick edge
ORDER BY greatest(fade_exp, break_exp) DESC
FORMAT Pretty;

-- ============================================================================
-- Comparison: Before vs After Fixes
-- ============================================================================

SELECT '=== BEFORE vs AFTER COMPARISON ===' AS report FORMAT Pretty;

SELECT
    'BEFORE (broken outcomes)' AS dataset,
    '99.98% BREAK / 0.023% REJECT' AS outcome_dist,
    '-81.83 ticks' AS overall_expectancy,
    '498 out of 100,000 rows (0.5%)' AS positive_edge_count
UNION ALL
SELECT
    'AFTER (fixed outcomes)',
    concat(
        toString(round(100.0 * countIf(outcome_label_30s = 'REJECT') / count(), 2)), '% REJECT / ',
        toString(round(100.0 * countIf(outcome_label_30s = 'BREAK') / count(), 2)), '% BREAK'
    ),
    'TBD - computing...',
    'TBD - see above'
FROM CG_mnq_wall_interactions_outcome_v1
FORMAT Pretty;

