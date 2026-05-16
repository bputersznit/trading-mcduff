-- ============================================================
-- PHASE 5 TRIGGER VALIDATION
-- ============================================================

-- 1. Trigger counts by model
SELECT
    trigger_model,
    trade_side,
    count() AS triggers,
    round(avg(mfe_pts_after_entry / 0.25), 2) AS avg_mfe_ticks,
    round(avg(mae_pts_after_entry / 0.25), 2) AS avg_mae_ticks,
    round(quantile(0.50)(mfe_pts_after_entry / 0.25), 2) AS med_mfe_ticks,
    round(quantile(0.50)(mae_pts_after_entry / 0.25), 2) AS med_mae_ticks
FROM CG_mnq_phase5_trigger_candidates_v1
GROUP BY
    trigger_model,
    trade_side
ORDER BY
    trigger_model,
    trade_side;


-- 2. Stop/target survivability matrix
SELECT
    trigger_model,
    trade_side,

    count() AS trades,

    countIf(mfe_pts_after_entry >= 4.00 AND mae_pts_after_entry < 2.00) AS tp16_sl8_wins,
    round(tp16_sl8_wins / trades, 4) AS tp16_sl8_win_rate,

    countIf(mfe_pts_after_entry >= 5.00 AND mae_pts_after_entry < 3.00) AS tp20_sl12_wins,
    round(tp20_sl12_wins / trades, 4) AS tp20_sl12_win_rate,

    countIf(mfe_pts_after_entry >= 6.00 AND mae_pts_after_entry < 4.00) AS tp24_sl16_wins,
    round(tp24_sl16_wins / trades, 4) AS tp24_sl16_win_rate,

    round((tp16_sl8_win_rate * 16) - ((1 - tp16_sl8_win_rate) * 8), 2) AS exp_ticks_tp16_sl8_raw,
    round((tp20_sl12_win_rate * 20) - ((1 - tp20_sl12_win_rate) * 12), 2) AS exp_ticks_tp20_sl12_raw,
    round((tp24_sl16_win_rate * 24) - ((1 - tp24_sl16_win_rate) * 16), 2) AS exp_ticks_tp24_sl16_raw
FROM CG_mnq_phase5_trigger_candidates_v1
GROUP BY
    trigger_model,
    trade_side
HAVING trades >= 25
ORDER BY
    exp_ticks_tp20_sl12_raw DESC;


-- 3. Best conditioned trigger groups
SELECT
    trigger_model,
    trade_side,
    setup_family,
    wall_side,
    delta_flip_pattern,
    orb_position,
    time_bucket,
    atr_regime,
    session_extreme_location,

    count() AS trades,

    countIf(mfe_pts_after_entry >= 5.00 AND mae_pts_after_entry < 3.00) AS wins_tp20_sl12,
    round(wins_tp20_sl12 / trades, 4) AS win_rate_tp20_sl12,

    round(avg(mfe_pts_after_entry / 0.25), 2) AS avg_mfe_ticks,
    round(avg(mae_pts_after_entry / 0.25), 2) AS avg_mae_ticks,

    round((win_rate_tp20_sl12 * 20) - ((1 - win_rate_tp20_sl12) * 12), 2) AS raw_expectancy_ticks,

    multiIf
    (
        trades >= 50
        AND win_rate_tp20_sl12 >= 0.48
        AND raw_expectancy_ticks >= 3.0,
        'PRIMARY_PHASE5_EDGE',

        trades >= 30
        AND win_rate_tp20_sl12 >= 0.45
        AND raw_expectancy_ticks >= 1.0,
        'WATCHLIST_PHASE5_EDGE',

        'NO_EDGE'
    ) AS phase5_label
FROM CG_mnq_phase5_trigger_candidates_v1
GROUP BY
    trigger_model,
    trade_side,
    setup_family,
    wall_side,
    delta_flip_pattern,
    orb_position,
    time_bucket,
    atr_regime,
    session_extreme_location
HAVING trades >= 20
ORDER BY
    phase5_label ASC,
    raw_expectancy_ticks DESC,
    trades DESC;


-- 4. Check whether pullback improves MAE
SELECT
    trade_side,
    setup_family,

    round(avgIf(mae_pts_after_entry / 0.25, trigger_model = 'CONFIRM_6T_MARKET'), 2) AS mae_confirm_6t_market,
    round(avgIf(mae_pts_after_entry / 0.25, trigger_model = 'CONFIRM_6T_PULLBACK'), 2) AS mae_confirm_6t_pullback,

    round(avgIf(mae_pts_after_entry / 0.25, trigger_model = 'CONFIRM_8T_MARKET'), 2) AS mae_confirm_8t_market,
    round(avgIf(mae_pts_after_entry / 0.25, trigger_model = 'CONFIRM_8T_PULLBACK'), 2) AS mae_confirm_8t_pullback,

    countIf(trigger_model = 'CONFIRM_6T_MARKET') AS n_6t_market,
    countIf(trigger_model = 'CONFIRM_6T_PULLBACK') AS n_6t_pullback,
    countIf(trigger_model = 'CONFIRM_8T_MARKET') AS n_8t_market,
    countIf(trigger_model = 'CONFIRM_8T_PULLBACK') AS n_8t_pullback
FROM CG_mnq_phase5_trigger_candidates_v1
GROUP BY
    trade_side,
    setup_family
ORDER BY
    trade_side,
    setup_family;


-- 5. Deployment shortlist
SELECT
    trigger_model,
    trade_side,
    setup_family,
    wall_side,
    delta_flip_pattern,
    orb_position,
    time_bucket,
    atr_regime,
    session_extreme_location,

    count() AS trades,

    countIf(mfe_pts_after_entry >= 5.00 AND mae_pts_after_entry < 3.00) AS wins,
    round(wins / trades, 4) AS win_rate,

    round((win_rate * 20) - ((1 - win_rate) * 12), 2) AS gross_expectancy_ticks,

    -- Approximate execution cost:
    -- 4 ticks slippage + 1.4 ticks commission = 5.4 ticks.
    round(gross_expectancy_ticks - 5.4, 2) AS net_expectancy_ticks,

    multiIf
    (
        trades >= 50
        AND win_rate >= 0.56
        AND net_expectancy_ticks > 0,
        'DEPLOYABLE_CANDIDATE',

        trades >= 30
        AND win_rate >= 0.52
        AND net_expectancy_ticks > -1,
        'RESEARCH_CANDIDATE',

        'REJECT'
    ) AS deployment_label
FROM CG_mnq_phase5_trigger_candidates_v1
GROUP BY
    trigger_model,
    trade_side,
    setup_family,
    wall_side,
    delta_flip_pattern,
    orb_position,
    time_bucket,
    atr_regime,
    session_extreme_location
HAVING trades >= 20
ORDER BY
    deployment_label ASC,
    net_expectancy_ticks DESC,
    trades DESC;
