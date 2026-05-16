-- ============================================================================
-- ClanMarshal v9.3 Equity Curve - Master Risk Spine (Simplified)
-- ============================================================================
-- Purpose: Build comprehensive equity tracking with drawdown governance
-- Source: CG_mnq_cm_v93_production_dynamic_backtest (119 trades, 573.5 pts)
-- Output: CG_mnq_cm_v93_equity_curve
-- Date: 2026-05-03
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_cm_v93_equity_curve;

CREATE TABLE CG_mnq_cm_v93_equity_curve
ENGINE = MergeTree
ORDER BY (trade_date, entry_time)
AS
WITH base_metrics AS (
    SELECT
        -- Trade identification
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
        if(pnl_pts > 0, 1, 0) AS is_winner,

        -- Cumulative equity
        sum(pnl_pts) OVER (ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS equity_pts,

        -- Running counts
        sum(if(pnl_pts > 0, 1, 0)) OVER (ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS total_wins,
        sum(if(pnl_pts <= 0, 1, 0)) OVER (ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS total_losses,

        -- Rolling metrics
        sum(pnl_pts) OVER (ORDER BY entry_time ROWS BETWEEN 9 PRECEDING AND CURRENT ROW) AS last_10_pnl_pts,
        sum(pnl_pts) OVER (ORDER BY entry_time ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS last_20_pnl_pts,
        sum(if(pnl_pts > 0, 1, 0)) OVER (ORDER BY entry_time ROWS BETWEEN 9 PRECEDING AND CURRENT ROW) AS last_10_wins,

        -- Daily metrics
        row_number() OVER (PARTITION BY trade_date ORDER BY entry_time) AS daily_trade_num,
        sum(pnl_pts) OVER (PARTITION BY trade_date ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS daily_pnl_pts,
        sum(if(pnl_pts > 0, 1, 0)) OVER (PARTITION BY trade_date ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS daily_wins,
        sum(if(pnl_pts <= 0, 1, 0)) OVER (PARTITION BY trade_date ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS daily_losses,

        -- Force rank rolling
        avg(force_edge_rank) OVER (ORDER BY entry_time ROWS BETWEEN 9 PRECEDING AND CURRENT ROW) AS avg_force_rank_last_10

    FROM CG_mnq_cm_v93_production_dynamic_backtest
)
SELECT
    *,

    -- Equity peak and drawdown (calculated from equity_pts)
    max(equity_pts) OVER (ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS equity_peak_pts,
    equity_pts - equity_peak_pts AS drawdown_pts,

    -- Running statistics
    round(total_wins / trade_seq, 4) AS running_win_rate,
    round(equity_pts / trade_seq, 2) AS running_expectancy_pts,
    round(last_10_wins / least(trade_seq, 10), 4) AS last_10_win_rate,
    round(avg_force_rank_last_10, 4) AS avg_force_rank_last_10_rounded,

    -- Daily worst DD
    min(equity_pts - equity_peak_pts) OVER (PARTITION BY trade_date ORDER BY entry_time ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS daily_worst_dd_pts,

    -- Kill-switch flags
    if(daily_pnl_pts <= -30, 1, 0) AS daily_loss_limit_breach,
    if((equity_pts - equity_peak_pts) <= -100, 1, 0) AS max_dd_limit_breach,
    if(avg_force_rank_last_10 < 0.85, 1, 0) AS force_rank_degradation

FROM base_metrics
ORDER BY entry_time;

-- ============================================================================
-- VERIFICATION QUERIES
-- ============================================================================

SELECT '=== EQUITY CURVE SUMMARY ===' AS report_section FORMAT Pretty;

SELECT
    count() AS total_trades,
    round(min(equity_pts), 2) AS min_equity_pts,
    round(max(equity_pts), 2) AS final_equity_pts,
    round(min(drawdown_pts), 2) AS max_drawdown_pts,
    round(max(running_win_rate), 4) AS final_win_rate,
    round(max(running_expectancy_pts), 2) AS final_expectancy_pts
FROM CG_mnq_cm_v93_equity_curve
FORMAT Pretty;

SELECT '=== KILL-SWITCH EVENTS ===' AS report_section FORMAT Pretty;

SELECT
    countIf(daily_loss_limit_breach = 1) AS daily_loss_breaches,
    countIf(max_dd_limit_breach = 1) AS max_dd_breaches,
    countIf(force_rank_degradation = 1) AS force_degradation_events
FROM CG_mnq_cm_v93_equity_curve
FORMAT Pretty;

SELECT '=== WORST DRAWDOWN PERIODS ===' AS report_section FORMAT Pretty;

SELECT
    trade_date,
    trade_seq,
    toDateTime(entry_time) AS entry_time,
    structural_case_v2,
    round(pnl_pts, 2) AS pnl_pts,
    round(equity_pts, 2) AS equity_pts,
    round(equity_peak_pts, 2) AS equity_peak_pts,
    round(drawdown_pts, 2) AS drawdown_pts
FROM CG_mnq_cm_v93_equity_curve
ORDER BY drawdown_pts ASC
LIMIT 10
FORMAT Pretty;

SELECT '=== DAILY PERFORMANCE ===' AS report_section FORMAT Pretty;

SELECT
    trade_date,
    max(daily_trade_num) AS trades,
    round(max(daily_pnl_pts), 2) AS daily_pnl_pts,
    max(daily_wins) AS wins,
    max(daily_losses) AS losses,
    round(min(daily_worst_dd_pts), 2) AS worst_intraday_dd_pts,
    countIf(daily_loss_limit_breach = 1) AS loss_limit_hits
FROM CG_mnq_cm_v93_equity_curve
GROUP BY trade_date
ORDER BY trade_date
FORMAT Pretty;

SELECT '=== ARCHETYPE PERFORMANCE ===' AS report_section FORMAT Pretty;

SELECT
    structural_case_v2,
    count() AS trades,
    round(avg(force_edge_rank), 4) AS avg_force_rank,
    round(sum(pnl_pts), 2) AS total_pnl_pts,
    round(avg(pnl_pts), 2) AS avg_pnl_pts,
    round(countIf(pnl_pts > 0) / count(), 4) AS win_rate
FROM CG_mnq_cm_v93_equity_curve
GROUP BY structural_case_v2
ORDER BY total_pnl_pts DESC
FORMAT Pretty;
