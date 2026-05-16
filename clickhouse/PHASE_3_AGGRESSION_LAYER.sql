-- ============================================================================
-- McDuff Package 03: Wall Interaction Aggression Layer
-- ============================================================================
-- Purpose:
--   Add pre/during/post aggression metrics to validated wall outcomes.
--
-- Method:
--   For each wall interaction, measure trade pressure in three windows:
--     PRE:    [-5s, 0s)
--     TOUCH:  [0s, +5s)
--     POST:   [+5s, +10s)
--
-- Side values: B = BUY, A = SELL (verified from mnq_trades)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_wall_outcomes_enriched_v2;

CREATE TABLE CG_mnq_wall_outcomes_enriched_v2
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, first_touch_time, interaction_id)
AS
SELECT
    o.interaction_id AS interaction_id,
    o.trade_date AS trade_date,
    o.wall_id AS wall_id,
    o.episode_num AS episode_num,
    o.first_touch_time AS first_touch_time,

    o.wall_side AS wall_side,
    o.wall_price AS wall_price,
    o.wall_size AS wall_size,
    o.wall_score AS wall_score,
    o.wall_rank AS wall_rank,
    o.wall_type AS wall_type,
    o.wall_behavior AS wall_behavior,

    o.pull_ratio AS pull_ratio,
    o.fill_ratio AS fill_ratio,
    o.replenish_ratio AS replenish_ratio,
    o.price_at_interaction AS price_at_interaction,
    o.distance_to_wall_ticks AS distance_to_wall_ticks,

    o.future_high_30s AS future_high_30s,
    o.future_low_30s AS future_low_30s,
    o.reject_ticks_30s AS reject_ticks_30s,
    o.break_ticks_30s AS break_ticks_30s,
    o.outcome_label_30s AS outcome_label_30s,

    -- ------------------------------------------------------------------------
    -- PRE WINDOW: -5s to touch
    -- ------------------------------------------------------------------------
    sumIf(t.size, t.side = 'B'
        AND t.ts_event >= o.first_touch_time - INTERVAL 5 SECOND
        AND t.ts_event <  o.first_touch_time
    ) AS buy_volume_pre_5s,

    sumIf(t.size, t.side = 'A'
        AND t.ts_event >= o.first_touch_time - INTERVAL 5 SECOND
        AND t.ts_event <  o.first_touch_time
    ) AS sell_volume_pre_5s,

    countIf(t.side = 'B'
        AND t.ts_event >= o.first_touch_time - INTERVAL 5 SECOND
        AND t.ts_event <  o.first_touch_time
    ) AS buy_trades_pre_5s,

    countIf(t.side = 'A'
        AND t.ts_event >= o.first_touch_time - INTERVAL 5 SECOND
        AND t.ts_event <  o.first_touch_time
    ) AS sell_trades_pre_5s,

    -- ------------------------------------------------------------------------
    -- TOUCH WINDOW: touch to +5s
    -- ------------------------------------------------------------------------
    sumIf(t.size, t.side = 'B'
        AND t.ts_event >= o.first_touch_time
        AND t.ts_event <  o.first_touch_time + INTERVAL 5 SECOND
    ) AS buy_volume_touch_5s,

    sumIf(t.size, t.side = 'A'
        AND t.ts_event >= o.first_touch_time
        AND t.ts_event <  o.first_touch_time + INTERVAL 5 SECOND
    ) AS sell_volume_touch_5s,

    countIf(t.side = 'B'
        AND t.ts_event >= o.first_touch_time
        AND t.ts_event <  o.first_touch_time + INTERVAL 5 SECOND
    ) AS buy_trades_touch_5s,

    countIf(t.side = 'A'
        AND t.ts_event >= o.first_touch_time
        AND t.ts_event <  o.first_touch_time + INTERVAL 5 SECOND
    ) AS sell_trades_touch_5s,

    -- ------------------------------------------------------------------------
    -- POST WINDOW: +5s to +10s
    -- ------------------------------------------------------------------------
    sumIf(t.size, t.side = 'B'
        AND t.ts_event >= o.first_touch_time + INTERVAL 5 SECOND
        AND t.ts_event <  o.first_touch_time + INTERVAL 10 SECOND
    ) AS buy_volume_post_5s,

    sumIf(t.size, t.side = 'A'
        AND t.ts_event >= o.first_touch_time + INTERVAL 5 SECOND
        AND t.ts_event <  o.first_touch_time + INTERVAL 10 SECOND
    ) AS sell_volume_post_5s,

    countIf(t.side = 'B'
        AND t.ts_event >= o.first_touch_time + INTERVAL 5 SECOND
        AND t.ts_event <  o.first_touch_time + INTERVAL 10 SECOND
    ) AS buy_trades_post_5s,

    countIf(t.side = 'A'
        AND t.ts_event >= o.first_touch_time + INTERVAL 5 SECOND
        AND t.ts_event <  o.first_touch_time + INTERVAL 10 SECOND
    ) AS sell_trades_post_5s,

    -- ------------------------------------------------------------------------
    -- Derived delta metrics
    -- ------------------------------------------------------------------------
    buy_volume_pre_5s - sell_volume_pre_5s AS delta_pre_5s,
    buy_volume_touch_5s - sell_volume_touch_5s AS delta_touch_5s,
    buy_volume_post_5s - sell_volume_post_5s AS delta_post_5s,

    buy_volume_pre_5s + sell_volume_pre_5s AS total_volume_pre_5s,
    buy_volume_touch_5s + sell_volume_touch_5s AS total_volume_touch_5s,
    buy_volume_post_5s + sell_volume_post_5s AS total_volume_post_5s,

    buy_trades_pre_5s + sell_trades_pre_5s AS total_trades_pre_5s,
    buy_trades_touch_5s + sell_trades_touch_5s AS total_trades_touch_5s,
    buy_trades_post_5s + sell_trades_post_5s AS total_trades_post_5s,

    total_trades_touch_5s / 5.0 AS trades_per_second_touch,
    total_volume_touch_5s / 5.0 AS volume_per_second_touch,

    -- ------------------------------------------------------------------------
    -- Aggression into wall
    -- For ASK wall, BUY aggression attacks the wall.
    -- For BID wall, SELL aggression attacks the wall.
    -- ------------------------------------------------------------------------
    if(
        o.wall_side = 'ASK',
        buy_volume_touch_5s,
        sell_volume_touch_5s
    ) AS aggression_into_wall_5s,

    if(
        o.wall_side = 'ASK',
        sell_volume_touch_5s,
        buy_volume_touch_5s
    ) AS aggression_away_from_wall_5s,

    aggression_into_wall_5s - aggression_away_from_wall_5s AS net_aggression_into_wall_5s,

    -- ------------------------------------------------------------------------
    -- Directional delta classification
    -- ------------------------------------------------------------------------
    multiIf(
        delta_pre_5s > 0 AND delta_touch_5s < 0, 'BUY_TO_SELL_FLIP',
        delta_pre_5s < 0 AND delta_touch_5s > 0, 'SELL_TO_BUY_FLIP',
        delta_pre_5s > 0 AND delta_touch_5s > 0, 'CONTINUED_BUY',
        delta_pre_5s < 0 AND delta_touch_5s < 0, 'CONTINUED_SELL',
        'NO_CLEAR_DELTA'
    ) AS delta_flip_pattern,

    -- ------------------------------------------------------------------------
    -- Wall-relative aggression classification
    -- ------------------------------------------------------------------------
    multiIf(
        o.wall_side = 'ASK' AND delta_touch_5s > 0, 'BUY_ATTACKING_ASK',
        o.wall_side = 'ASK' AND delta_touch_5s < 0, 'SELL_AWAY_FROM_ASK',
        o.wall_side = 'BID' AND delta_touch_5s < 0, 'SELL_ATTACKING_BID',
        o.wall_side = 'BID' AND delta_touch_5s > 0, 'BUY_AWAY_FROM_BID',
        'NO_CLEAR_WALL_AGGRESSION'
    ) AS wall_aggression_pattern,

    -- ------------------------------------------------------------------------
    -- Absorption proxy
    -- High aggression into wall + REJECT outcome = absorption candidate.
    -- ------------------------------------------------------------------------
    if(
        aggression_into_wall_5s > 0,
        reject_ticks_30s / aggression_into_wall_5s,
        0
    ) AS reject_per_aggression_unit,

    if(
        aggression_into_wall_5s > 0,
        break_ticks_30s / aggression_into_wall_5s,
        0
    ) AS break_per_aggression_unit

FROM CG_mnq_wall_interactions_outcome_v1 AS o
LEFT JOIN mnq_trades AS t
    ON t.ts_event >= o.first_touch_time - INTERVAL 5 SECOND
   AND t.ts_event <  o.first_touch_time + INTERVAL 10 SECOND
   AND toDate(t.ts_event) = o.trade_date
GROUP BY
    o.interaction_id,
    o.trade_date,
    o.wall_id,
    o.episode_num,
    o.first_touch_time,
    o.wall_side,
    o.wall_price,
    o.wall_size,
    o.wall_score,
    o.wall_rank,
    o.wall_type,
    o.wall_behavior,
    o.pull_ratio,
    o.fill_ratio,
    o.replenish_ratio,
    o.price_at_interaction,
    o.distance_to_wall_ticks,
    o.future_high_30s,
    o.future_low_30s,
    o.reject_ticks_30s,
    o.break_ticks_30s,
    o.outcome_label_30s;

-- Verification
SELECT '=== AGGRESSION LAYER CREATED ===' AS report FORMAT Pretty;
SELECT count() AS rows FROM CG_mnq_wall_outcomes_enriched_v2 FORMAT Pretty;
