-- ============================================================================
-- T2 v1.2_FULL ClickHouse Backtest - ACCURATE EXIT MATCHING
-- ============================================================================
--
-- This version uses TRUE tick-by-tick price matching for stop/target exits
-- More accurate but slower than simulated version
--
-- ============================================================================

WITH

-- ============================================================================
-- STEP 1: Generate Entry Signals (Same as v1.2 NT8 Logic)
-- ============================================================================

tick_features AS (
    SELECT
        toDate(ts_event, 'America/New_York') as trade_date,
        ts_event,
        price,
        size,

        -- Tick direction
        CASE
            WHEN price > lagInFrame(price, 1) OVER (ORDER BY ts_event) THEN 1
            WHEN price < lagInFrame(price, 1) OVER (ORDER BY ts_event) THEN -1
            ELSE 0
        END as tick_direction,

        -- Rolling 200-tick event features
        sumIf(size, tick_direction = 1) OVER (
            ORDER BY ts_event
            ROWS BETWEEN 200 PRECEDING AND CURRENT ROW
        ) as up_events,

        sumIf(size, tick_direction = -1) OVER (
            ORDER BY ts_event
            ROWS BETWEEN 200 PRECEDING AND CURRENT ROW
        ) as down_events,

        sum(size) OVER (
            ORDER BY ts_event
            ROWS BETWEEN 200 PRECEDING AND CURRENT ROW
        ) as total_events

    FROM mnq_mbo
    WHERE symbol = 'MNQZ5'
      AND action = 'T'
      AND toDate(ts_event, 'America/New_York') >= '2025-09-26'
      AND toDate(ts_event, 'America/New_York') <= '2025-10-21'
),

signals AS (
    SELECT
        trade_date,
        ts_event as signal_time,
        price,

        up_events - down_events as event_delta,
        (up_events - down_events) / nullIf(total_events, 0) as event_imbalance,

        -- Signal logic
        CASE
            WHEN (up_events - down_events) > 20.0
              AND ((up_events - down_events) / nullIf(total_events, 0)) > 0.15
            THEN 'LONG'

            WHEN (up_events - down_events) < -20.0
              AND ((up_events - down_events) / nullIf(total_events, 0)) < -0.15
            THEN 'SHORT'

            ELSE NULL
        END as signal_side,

        toHour(ts_event, 'America/New_York') as et_hour,
        toMinute(ts_event, 'America/New_York') as et_minute,
        toSecond(ts_event, 'America/New_York') as et_second

    FROM tick_features
    WHERE total_events > 0
      AND (et_hour * 10000 + et_minute * 100 + et_second) >= 93500  -- RTH
      AND (et_hour * 10000 + et_minute * 100 + et_second) <= 155900
),

entries AS (
    SELECT
        *,
        row_number() OVER (ORDER BY signal_time) as trade_id,

        -- Entry with slippage
        CASE
            WHEN signal_side = 'LONG' THEN price + 0.25
            WHEN signal_side = 'SHORT' THEN price - 0.25
        END as entry_price

    FROM signals
    WHERE signal_side IS NOT NULL
),

-- ============================================================================
-- STEP 2: Find Actual Exit Prices Using Tick Data
-- ============================================================================

entry_with_targets AS (
    SELECT
        *,

        -- Calculate stop/target prices
        CASE
            WHEN signal_side = 'LONG' THEN entry_price - (16 * 0.25)
            WHEN signal_side = 'SHORT' THEN entry_price + (16 * 0.25)
        END as stop_price,

        CASE
            WHEN signal_side = 'LONG' THEN entry_price + (32 * 0.25)
            WHEN signal_side = 'SHORT' THEN entry_price - (32 * 0.25)
        END as target_price,

        signal_time + INTERVAL 900 SECOND as max_hold_time

    FROM entries
),

-- Join with future tick data to find exit
tick_exits AS (
    SELECT
        e.trade_id,
        e.trade_date,
        e.signal_time,
        e.signal_side,
        e.entry_price,
        e.stop_price,
        e.target_price,
        e.max_hold_time,
        e.event_delta,
        e.event_imbalance,

        m.ts_event as tick_time,
        m.price as tick_price,

        -- Check if this tick hits stop or target
        CASE
            WHEN e.signal_side = 'LONG' AND m.price <= e.stop_price THEN 'STOP'
            WHEN e.signal_side = 'LONG' AND m.price >= e.target_price THEN 'TARGET'
            WHEN e.signal_side = 'SHORT' AND m.price >= e.stop_price THEN 'STOP'
            WHEN e.signal_side = 'SHORT' AND m.price <= e.target_price THEN 'TARGET'
            WHEN m.ts_event >= e.max_hold_time THEN 'MAX_HOLD'
            ELSE NULL
        END as exit_reason

    FROM entry_with_targets e
    INNER JOIN mnq_mbo m
        ON m.symbol = 'MNQZ5'
        AND m.action = 'T'
        AND m.ts_event > e.signal_time
        AND m.ts_event <= e.max_hold_time
        AND toDate(m.ts_event, 'America/New_York') = e.trade_date
),

first_exit AS (
    SELECT
        trade_id,
        trade_date,
        signal_time,
        signal_side,
        entry_price,
        stop_price,
        target_price,
        event_delta,
        event_imbalance,

        minIf(tick_time, exit_reason IS NOT NULL) as exit_time,
        argMinIf(exit_reason, tick_time, exit_reason IS NOT NULL) as exit_reason,

        CASE
            WHEN exit_reason = 'TARGET' THEN target_price
            WHEN exit_reason = 'STOP' THEN stop_price
            WHEN exit_reason = 'MAX_HOLD' THEN argMinIf(tick_price, tick_time, exit_reason = 'MAX_HOLD')
            ELSE entry_price
        END as exit_price

    FROM tick_exits
    GROUP BY
        trade_id,
        trade_date,
        signal_time,
        signal_side,
        entry_price,
        stop_price,
        target_price,
        event_delta,
        event_imbalance
),

-- ============================================================================
-- STEP 3: Calculate P&L
-- ============================================================================

with_pnl AS (
    SELECT
        *,

        CASE
            WHEN signal_side = 'LONG' THEN (exit_price - entry_price) / 0.25
            WHEN signal_side = 'SHORT' THEN (entry_price - exit_price) / 0.25
        END as profit_ticks,

        (profit_ticks * 0.50) - 0.70 as net_pnl,

        dateDiff('second', signal_time, exit_time) as hold_seconds

    FROM first_exit
    WHERE exit_time IS NOT NULL  -- Only trades that found an exit
),

-- ============================================================================
-- STEP 4: Remove Overlapping Trades
-- ============================================================================

non_overlapping AS (
    SELECT
        *,
        lag(exit_time) OVER (ORDER BY signal_time) as prev_exit_time,

        CASE
            WHEN prev_exit_time IS NULL THEN true
            WHEN signal_time >= prev_exit_time THEN true
            ELSE false
        END as is_valid

    FROM with_pnl
),

valid_trades AS (
    SELECT * FROM non_overlapping WHERE is_valid = true
),

-- ============================================================================
-- STEP 5: Apply 3-Layer Protection
-- ============================================================================

with_protection AS (
    SELECT
        *,

        -- Consecutive losses
        CASE
            WHEN net_pnl < 0 THEN
                row_number() OVER (
                    PARTITION BY trade_date,
                    sum(CASE WHEN net_pnl >= 0 THEN 1 ELSE 0 END) OVER (
                        PARTITION BY trade_date
                        ORDER BY signal_time
                        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                    )
                    ORDER BY signal_time
                )
            ELSE 0
        END as consecutive_losses,

        -- Daily cumulative P&L
        sum(net_pnl) OVER (
            PARTITION BY trade_date
            ORDER BY signal_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) as daily_cumulative_pnl,

        -- Overall cumulative P&L
        sum(net_pnl) OVER (
            ORDER BY signal_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) as cumulative_pnl,

        max(cumulative_pnl) OVER (
            ORDER BY signal_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) as peak_cumulative_pnl

    FROM valid_trades
),

after_protection AS (
    SELECT
        *,
        peak_cumulative_pnl - cumulative_pnl as dd_from_peak,

        -- Layer 1: Choppy filter (allow 3rd loss, block after)
        consecutive_losses <= 3 as passes_choppy,

        -- Layer 2: Daily max loss
        daily_cumulative_pnl > -200.0 as passes_daily_loss,

        -- Layer 3: Emergency stop
        (peak_cumulative_pnl - cumulative_pnl) < 400.0 as passes_emergency

    FROM with_protection
),

final_trades AS (
    SELECT *
    FROM after_protection
    WHERE passes_choppy = true
      AND passes_daily_loss = true
      AND passes_emergency = true
)

-- ============================================================================
-- RESULTS
-- ============================================================================

SELECT '════════════════════════════════════════════════════════════════════════' as output
UNION ALL SELECT '   T2 v1.2_FULL CH BACKTEST - ACCURATE EXIT MATCHING'
UNION ALL SELECT '════════════════════════════════════════════════════════════════════════'
UNION ALL SELECT ''
UNION ALL SELECT concat('Total Trades: ', toString((SELECT count(*) FROM final_trades)))
UNION ALL SELECT concat('Win Rate: ', toString(round((SELECT countIf(net_pnl > 0) * 100.0 / count(*) FROM final_trades), 1)), '%')
UNION ALL SELECT concat('Total P&L: $', toString(round((SELECT sum(net_pnl) FROM final_trades), 2)))
UNION ALL SELECT concat('Avg P&L: $', toString(round((SELECT avg(net_pnl) FROM final_trades), 2)))
UNION ALL SELECT ''
UNION ALL SELECT concat('Avg Winner: $', toString(round((SELECT avgIf(net_pnl, net_pnl > 0) FROM final_trades), 2)))
UNION ALL SELECT concat('Avg Loser: $', toString(round((SELECT avgIf(net_pnl, net_pnl <= 0) FROM final_trades), 2)))
UNION ALL SELECT concat('Avg Hold: ', toString(round((SELECT avg(hold_seconds) FROM final_trades), 0)), ' seconds')
UNION ALL SELECT ''
UNION ALL SELECT concat('Worst Cumulative DD: $', toString(round((SELECT min(cumulative_pnl) FROM final_trades), 2)))
UNION ALL SELECT concat('Peak Cumulative: $', toString(round((SELECT max(cumulative_pnl) FROM final_trades), 2)))
UNION ALL SELECT ''
UNION ALL SELECT '--- Daily Breakdown ---'
UNION ALL SELECT concat(
    toString(trade_date), ' | ',
    'Trades: ', leftPad(toString(count(*)), 2, ' '), ' | ',
    'WR: ', leftPad(toString(round(countIf(net_pnl > 0) * 100.0 / count(*), 1)), 4, ' '), '% | ',
    'P&L: $', leftPad(toString(round(sum(net_pnl), 2)), 7, ' '), ' | ',
    'MaxConsecL: ', toString(maxIf(consecutive_losses, net_pnl < 0))
)
FROM final_trades
GROUP BY trade_date
ORDER BY trade_date

UNION ALL SELECT ''
UNION ALL SELECT '════════════════════════════════════════════════════════════════════════'

FORMAT TSVRaw;
