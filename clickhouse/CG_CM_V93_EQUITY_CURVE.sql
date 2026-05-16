-- ============================================================================
-- ClanMarshal v9.3 Equity Curve - Master Risk Spine
-- ============================================================================
-- Purpose: Build comprehensive equity tracking with drawdown governance,
--          kill-switch thresholds, and capital survivability metrics
-- Source: CG_mnq_cm_v93_production_dynamic_backtest (119 trades, 573.5 pts)
-- Output: CG_mnq_cm_v93_equity_curve
-- Date: 2026-05-03
-- ============================================================================

-- Drop existing table if it exists
DROP TABLE IF EXISTS CG_mnq_cm_v93_equity_curve;

-- Create equity curve table with full risk telemetry
CREATE TABLE CG_mnq_cm_v93_equity_curve
ENGINE = MergeTree
ORDER BY (trade_date, entry_time)
AS
WITH trades_sequenced AS (
    -- Step 1: Sequence trades chronologically and add trade metadata
    SELECT
        row_number() OVER (ORDER BY entry_time) AS trade_seq,
        trade_date,
        entry_time,
        exit_time,
        signal_side,
        structural_case_v2,
        force_edge_rank,
        entry_price,
        exit_price,
        exit_threshold,
        pnl_pts,
        hold_seconds,
        exit_reason,

        -- Session classification
        toHour(entry_time) AS entry_hour,
        CASE
            WHEN toHour(entry_time) BETWEEN 9 AND 10 THEN 'OPEN'
            WHEN toHour(entry_time) BETWEEN 11 AND 13 THEN 'MIDDAY'
            WHEN toHour(entry_time) BETWEEN 14 AND 15 THEN 'POWER_HOUR'
            WHEN toHour(entry_time) >= 16 THEN 'CLOSE'
            ELSE 'OTHER'
        END AS session_window,

        -- Win/loss flag
        if(pnl_pts > 0, 1, 0) AS is_winner

    FROM CG_mnq_cm_v93_production_dynamic_backtest
    ORDER BY entry_time
),

equity_running_sum AS (
    -- Step 2a: Calculate running equity sum
    SELECT
        *,
        sum(pnl_pts) OVER (ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS equity_pts,
        sum(is_winner) OVER (ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS total_wins,
        sum(1 - is_winner) OVER (ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS total_losses
    FROM trades_sequenced
),

equity_progression AS (
    -- Step 2b: Calculate equity peak and drawdown
    SELECT
        *,
        max(equity_pts) OVER (ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS equity_peak_pts,
        equity_pts - equity_peak_pts AS drawdown_pts,
        round(total_wins / trade_seq, 4) AS running_win_rate,
        round(equity_pts / trade_seq, 2) AS running_expectancy_pts
    FROM equity_running_sum
),

with_prev_result AS (
    -- Step 3a: Get previous trade result
    SELECT
        *,
        lag(is_winner, 1, is_winner) OVER (ORDER BY entry_time) AS prev_is_winner
    FROM equity_progression
),

streak_groups AS (
    -- Step 3b: Create streak groups for consecutive wins/losses
    SELECT
        *,
        sum(if(is_winner != prev_is_winner, 1, 0))
            OVER (ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS streak_group
    FROM with_prev_result
),

rolling_metrics AS (
    -- Step 3b: Add rolling performance windows
    SELECT
        *,

        -- Last 10 trades PnL
        sum(pnl_pts) OVER (ORDER BY entry_time ROWS BETWEEN 9 PRECEDING AND CURRENT ROW) AS last_10_pnl_pts,

        -- Last 20 trades PnL
        sum(pnl_pts) OVER (ORDER BY entry_time ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS last_20_pnl_pts,

        -- Last 10 trades win rate
        round(
            sum(is_winner) OVER (ORDER BY entry_time ROWS BETWEEN 9 PRECEDING AND CURRENT ROW) /
            least(trade_seq, 10),
            4
        ) AS last_10_win_rate,

        -- Consecutive wins (count within current winning streak)
        if(is_winner = 1,
            row_number() OVER (PARTITION BY streak_group ORDER BY entry_time),
            0
        ) AS consecutive_wins,

        -- Consecutive losses (count within current losing streak)
        if(is_winner = 0,
            row_number() OVER (PARTITION BY streak_group ORDER BY entry_time),
            0
        ) AS consecutive_losses

    FROM streak_groups
),

daily_context AS (
    -- Step 4: Add daily aggregation context
    SELECT
        r.*,

        -- Daily trade count (how many trades today so far)
        row_number() OVER (PARTITION BY trade_date ORDER BY entry_time) AS daily_trade_num,

        -- Daily PnL so far
        sum(pnl_pts) OVER (PARTITION BY trade_date ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS daily_pnl_pts,

        -- Daily wins so far
        sum(is_winner) OVER (PARTITION BY trade_date ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS daily_wins,

        -- Daily losses so far
        sum(1 - is_winner) OVER (PARTITION BY trade_date ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS daily_losses,

        -- Worst daily drawdown so far today
        min(drawdown_pts) OVER (PARTITION BY trade_date ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS daily_worst_dd_pts

    FROM rolling_metrics r
),

risk_telemetry AS (
    -- Step 5: Add kill-switch and governance flags
    SELECT
        *,

        -- Daily loss limit kill-switch (example: -30 pts per day)
        if(daily_pnl_pts <= -30, 1, 0) AS daily_loss_limit_breach,

        -- Max drawdown kill-switch (example: -100 pts from peak)
        if(drawdown_pts <= -100, 1, 0) AS max_dd_limit_breach,

        -- Consecutive loss limit (example: 5 losses in a row)
        if(consecutive_losses >= 5, 1, 0) AS consecutive_loss_breach,

        -- Force rank health (average force rank of last 10 trades)
        round(
            avg(force_edge_rank) OVER (ORDER BY entry_time ROWS BETWEEN 9 PRECEDING AND CURRENT ROW),
            4
        ) AS avg_force_rank_last_10,

        -- Signal quality degradation flag
        if(avg_force_rank_last_10 < 0.85, 1, 0) AS force_rank_degradation,

        -- Overall kill-switch (any breach triggers)
        greatest(
            daily_loss_limit_breach,
            max_dd_limit_breach,
            consecutive_loss_breach
        ) AS kill_switch_active

    FROM daily_context
)

-- Final selection with all telemetry
SELECT
    trade_seq,
    trade_date,
    entry_time,
    exit_time,
    signal_side,
    structural_case_v2,
    force_edge_rank,
    entry_price,
    exit_price,
    exit_threshold,
    pnl_pts,
    hold_seconds,
    exit_reason,

    -- Session metadata
    entry_hour,
    session_window,
    is_winner,

    -- Equity progression
    equity_pts,
    equity_peak_pts,
    drawdown_pts,

    -- Running metrics
    total_wins,
    total_losses,
    running_win_rate,
    running_expectancy_pts,

    -- Rolling windows
    last_10_pnl_pts,
    last_20_pnl_pts,
    last_10_win_rate,
    consecutive_wins,
    consecutive_losses,

    -- Daily context
    daily_trade_num,
    daily_pnl_pts,
    daily_wins,
    daily_losses,
    daily_worst_dd_pts,

    -- Risk governance
    daily_loss_limit_breach,
    max_dd_limit_breach,
    consecutive_loss_breach,
    avg_force_rank_last_10,
    force_rank_degradation,
    kill_switch_active

FROM risk_telemetry
ORDER BY entry_time;

-- ============================================================================
-- VERIFICATION QUERIES
-- ============================================================================

-- Summary statistics
SELECT
    '=== EQUITY CURVE SUMMARY ===' AS report_section;

SELECT
    count() AS total_trades,
    round(min(equity_pts), 2) AS min_equity_pts,
    round(max(equity_pts), 2) AS max_equity_pts,
    round(min(drawdown_pts), 2) AS max_drawdown_pts,
    round(avg(running_win_rate), 4) AS avg_win_rate,
    round(avg(running_expectancy_pts), 2) AS avg_expectancy_pts
FROM CG_mnq_cm_v93_equity_curve;

-- Kill-switch breach analysis
SELECT
    '=== KILL-SWITCH EVENTS ===' AS report_section;

SELECT
    countIf(daily_loss_limit_breach = 1) AS daily_loss_breaches,
    countIf(max_dd_limit_breach = 1) AS max_dd_breaches,
    countIf(consecutive_loss_breach = 1) AS consecutive_loss_breaches,
    countIf(force_rank_degradation = 1) AS force_degradation_events,
    countIf(kill_switch_active = 1) AS total_kill_switch_activations
FROM CG_mnq_cm_v93_equity_curve;

-- Worst drawdown periods
SELECT
    '=== WORST DRAWDOWN PERIODS ===' AS report_section;

SELECT
    trade_date,
    trade_seq,
    entry_time,
    structural_case_v2,
    pnl_pts,
    round(equity_pts, 2) AS equity_pts,
    round(equity_peak_pts, 2) AS equity_peak_pts,
    round(drawdown_pts, 2) AS drawdown_pts
FROM CG_mnq_cm_v93_equity_curve
ORDER BY drawdown_pts ASC
LIMIT 10;

-- Daily breakdown
SELECT
    '=== DAILY PERFORMANCE ===' AS report_section;

SELECT
    trade_date,
    max(daily_trade_num) AS trades,
    round(max(daily_pnl_pts), 2) AS daily_pnl_pts,
    max(daily_wins) AS wins,
    max(daily_losses) AS losses,
    round(max(daily_worst_dd_pts), 2) AS worst_intraday_dd_pts,
    countIf(kill_switch_active = 1) AS kill_switch_hits
FROM CG_mnq_cm_v93_equity_curve
GROUP BY trade_date
ORDER BY trade_date;

-- Force rank distribution
SELECT
    '=== FORCE RANK TELEMETRY ===' AS report_section;

SELECT
    structural_case_v2,
    count() AS trades,
    round(avg(force_edge_rank), 4) AS avg_force_rank,
    round(min(force_edge_rank), 4) AS min_force_rank,
    round(max(force_edge_rank), 4) AS max_force_rank,
    round(sum(pnl_pts), 2) AS total_pnl_pts
FROM CG_mnq_cm_v93_equity_curve
GROUP BY structural_case_v2
ORDER BY total_pnl_pts DESC;
