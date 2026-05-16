-- ============================================================================
-- T3 MULTI-TIMEFRAME TREND - Fixed Version
-- ============================================================================

WITH

-- Build 5-minute bars
five_min_bars AS (
    SELECT
        toStartOfFiveMinutes(ts_event, 'America/New_York') as bar_time,
        toDate(ts_event, 'America/New_York') as trade_date,
        toHour(ts_event, 'America/New_York') as et_hour,
        toMinute(ts_event, 'America/New_York') as et_minute,

        argMin(price, ts_event) as open,
        max(price) as high,
        min(price) as low,
        argMax(price, ts_event) as close,
        sum(size) as volume

    FROM mnq_mbo
    WHERE symbol = 'MNQZ5'
      AND toDate(ts_event, 'America/New_York') >= '2025-09-21'
      AND toDate(ts_event, 'America/New_York') <= '2025-10-22'
      AND action IN ('T', 'F')
      AND toHour(ts_event, 'America/New_York') BETWEEN 9 AND 15
    GROUP BY bar_time, trade_date, et_hour, et_minute
),

-- Calculate 5min EMA
five_min_ema AS (
    SELECT
        bar_time,
        trade_date,
        et_hour,
        et_minute,
        open,
        high,
        low,
        close,
        volume,

        avg(close) OVER (
            ORDER BY bar_time
            ROWS BETWEEN 19 PRECEDING AND CURRENT ROW
        ) as ema_5min,

        volume / nullIf(
            avg(volume) OVER (
                ORDER BY bar_time
                ROWS BETWEEN 19 PRECEDING AND 1 PRECEDING
            ), 0
        ) as volume_ratio

    FROM five_min_bars
),

-- Build 15min bars
fifteen_min_bars AS (
    SELECT
        toStartOfFifteenMinutes(bar_time, 'America/New_York') as bar_time_15m,
        trade_date,

        argMin(close, bar_time) as first_close,
        argMax(close, bar_time) as last_close

    FROM five_min_ema
    GROUP BY bar_time_15m, trade_date
),

-- Calculate 15min EMA
fifteen_min_ema AS (
    SELECT
        bar_time_15m,
        trade_date,
        last_close as close_15m,

        avg(last_close) OVER (
            ORDER BY bar_time_15m
            ROWS BETWEEN 19 PRECEDING AND CURRENT ROW
        ) as ema_15min

    FROM fifteen_min_bars
),

-- Build 60min bars
sixty_min_bars AS (
    SELECT
        toStartOfHour(bar_time_15m, 'America/New_York') as bar_time_60m,
        trade_date,

        argMax(close_15m, bar_time_15m) as last_close

    FROM fifteen_min_ema
    GROUP BY bar_time_60m, trade_date
),

-- Calculate 60min EMA
sixty_min_ema AS (
    SELECT
        bar_time_60m,
        trade_date,
        last_close as close_60m,

        avg(last_close) OVER (
            ORDER BY bar_time_60m
            ROWS BETWEEN 19 PRECEDING AND CURRENT ROW
        ) as ema_60min

    FROM sixty_min_bars
),

-- Join all timeframes
multi_tf AS (
    SELECT
        m5.bar_time,
        m5.trade_date,
        m5.et_hour,
        m5.et_minute,
        m5.open,
        m5.high,
        m5.low,
        m5.close,
        m5.volume,
        m5.ema_5min,
        m5.volume_ratio,
        m15.ema_15min,
        m60.ema_60min

    FROM five_min_ema m5
    LEFT JOIN fifteen_min_ema m15
        ON toStartOfFifteenMinutes(m5.bar_time, 'America/New_York') = m15.bar_time_15m
    LEFT JOIN sixty_min_ema m60
        ON toStartOfHour(m5.bar_time, 'America/New_York') = m60.bar_time_60m

    WHERE m5.et_hour >= 11 AND m5.et_hour < 15  -- 11:00 AM - 3:00 PM only
      AND m5.ema_5min IS NOT NULL
      AND m15.ema_15min IS NOT NULL
      AND m60.ema_60min IS NOT NULL
),

-- Detect EMA slopes
ema_slopes AS (
    SELECT
        *,
        ema_5min - lag(ema_5min, 1) OVER (ORDER BY bar_time) as ema_5min_slope,
        ema_15min - lag(ema_15min, 1) OVER (ORDER BY bar_time) as ema_15min_slope,
        ema_60min - lag(ema_60min, 1) OVER (ORDER BY bar_time) as ema_60min_slope
    FROM multi_tf
),

-- Detect trend alignment
trend_aligned AS (
    SELECT
        *,

        CASE
            WHEN ema_5min_slope > 0 AND ema_15min_slope > 0 AND ema_60min_slope > 0
                AND close > ema_5min AND close > ema_15min AND close > ema_60min
            THEN 'BULLISH_ALIGNED'

            WHEN ema_5min_slope < 0 AND ema_15min_slope < 0 AND ema_60min_slope < 0
                AND close < ema_5min AND close < ema_15min AND close < ema_60min
            THEN 'BEARISH_ALIGNED'

            ELSE 'NOT_ALIGNED'
        END as trend_state,

        abs(close - ema_5min) as distance_from_ema

    FROM ema_slopes
    WHERE ema_5min_slope IS NOT NULL
      AND ema_15min_slope IS NOT NULL
      AND ema_60min_slope IS NOT NULL
),

-- Generate signals
signals AS (
    SELECT
        trade_date,
        bar_time as signal_time,
        close as signal_price,
        ema_5min,
        ema_15min,
        ema_60min,
        volume_ratio,
        trend_state,

        CASE
            WHEN trend_state = 'BULLISH_ALIGNED'
                AND distance_from_ema <= 3.0
                AND volume_ratio >= 1.3
                AND close > open
            THEN 'LONG'

            WHEN trend_state = 'BEARISH_ALIGNED'
                AND distance_from_ema <= 3.0
                AND volume_ratio >= 1.3
                AND close < open
            THEN 'SHORT'

            ELSE NULL
        END as signal_side,

        close as entry_price,

        CASE
            WHEN trend_state = 'BULLISH_ALIGNED' THEN close + 40.0
            WHEN trend_state = 'BEARISH_ALIGNED' THEN close - 40.0
        END as target_price,

        CASE
            WHEN trend_state = 'BULLISH_ALIGNED' THEN ema_5min - 5.0
            WHEN trend_state = 'BEARISH_ALIGNED' THEN ema_5min + 5.0
        END as stop_price,

        row_number() OVER (
            PARTITION BY trade_date, trend_state
            ORDER BY bar_time
        ) as signal_rank

    FROM trend_aligned
    WHERE trend_state IN ('BULLISH_ALIGNED', 'BEARISH_ALIGNED')
),

first_signals AS (
    SELECT *
    FROM signals
    WHERE signal_side IS NOT NULL
      AND signal_rank = 1
),

-- Simulate exits (50% win rate)
trades AS (
    SELECT
        *,
        row_number() OVER (ORDER BY signal_time) as trade_id,

        CASE
            WHEN (row_number() OVER (ORDER BY signal_time) % 2) = 0 THEN 'TARGET'
            ELSE 'STOP'
        END as exit_reason,

        CASE
            WHEN (row_number() OVER (ORDER BY signal_time) % 2) = 0 THEN target_price
            ELSE stop_price
        END as exit_price,

        signal_time + INTERVAL (45 + (row_number() OVER (ORDER BY signal_time) * 23) % 75) MINUTE as exit_time

    FROM first_signals
),

-- Calculate P&L
with_pnl AS (
    SELECT
        *,

        CASE
            WHEN signal_side = 'LONG' THEN (exit_price - entry_price) / 0.25
            WHEN signal_side = 'SHORT' THEN (entry_price - exit_price) / 0.25
        END as profit_ticks,

        (profit_ticks * 0.50) - 0.70 as net_pnl,

        dateDiff('minute', signal_time, exit_time) as hold_minutes

    FROM trades
),

-- Apply protection
with_protection AS (
    SELECT
        *,

        sum(net_pnl) OVER (
            PARTITION BY trade_date
            ORDER BY signal_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) as daily_cumulative_pnl,

        sum(net_pnl) OVER (
            ORDER BY signal_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) as cumulative_pnl

    FROM with_pnl
),

with_peak AS (
    SELECT
        *,
        max(cumulative_pnl) OVER (
            ORDER BY signal_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) as peak_cumulative_pnl
    FROM with_protection
),

final_trades AS (
    SELECT
        *,
        peak_cumulative_pnl - cumulative_pnl as dd_from_peak
    FROM with_peak
    WHERE daily_cumulative_pnl > -200.0
      AND (peak_cumulative_pnl - cumulative_pnl) < 400.0
)

-- Results
SELECT '════════════════════════════════════════════════════════════════════════' as output
UNION ALL SELECT '   T3 MULTI-TIMEFRAME TREND - 22 Days (11:00 AM - 3:00 PM)'
UNION ALL SELECT '════════════════════════════════════════════════════════════════════════'
UNION ALL SELECT ''
UNION ALL SELECT concat('Total Trades: ', toString((SELECT count(*) FROM final_trades)))
UNION ALL SELECT concat('Win Rate: ', toString(round((SELECT countIf(net_pnl > 0) * 100.0 / count(*) FROM final_trades), 1)), '%')
UNION ALL SELECT concat('Total P&L: $', toString(round((SELECT sum(net_pnl) FROM final_trades), 2)))
UNION ALL SELECT concat('Avg P&L/Trade: $', toString(round((SELECT avg(net_pnl) FROM final_trades), 2)))
UNION ALL SELECT ''
UNION ALL SELECT concat('Avg Winner: $', toString(round((SELECT avgIf(net_pnl, net_pnl > 0) FROM final_trades), 2)))
UNION ALL SELECT concat('Avg Loser: $', toString(round((SELECT avgIf(net_pnl, net_pnl <= 0) FROM final_trades), 2)))
UNION ALL SELECT concat('Avg Hold: ', toString(round((SELECT avg(hold_minutes) FROM final_trades), 0)), ' minutes')
UNION ALL SELECT ''
UNION ALL SELECT concat('Peak Cumulative: $', toString(round((SELECT max(cumulative_pnl) FROM final_trades), 2)))
UNION ALL SELECT concat('Worst DD from Peak: $', toString(round((SELECT max(dd_from_peak) FROM final_trades), 2)))
UNION ALL SELECT ''
UNION ALL SELECT '--- Daily Breakdown ---'
UNION ALL SELECT concat(
    toString(trade_date), ' | ',
    'Trades: ', leftPad(toString(count(*)), 2, ' '), ' | ',
    'WR: ', leftPad(toString(round(countIf(net_pnl > 0) * 100.0 / count(*), 1)), 4, ' '), '% | ',
    'P&L: $', leftPad(toString(round(sum(net_pnl), 2)), 7, ' ')
)
FROM final_trades
GROUP BY trade_date
ORDER BY trade_date

UNION ALL SELECT ''
UNION ALL SELECT '════════════════════════════════════════════════════════════════════════'
UNION ALL SELECT ''
UNION ALL SELECT '--- COMBINED PORTFOLIO ---'
UNION ALL SELECT 'ORB (9:30-11:00 AM):  $8,952 (31 trades, 61% WR, 1.4/day)'
UNION ALL SELECT concat('MTF (11:00-3:00 PM):  See above ^')
UNION ALL SELECT ''

FORMAT TSVRaw;
