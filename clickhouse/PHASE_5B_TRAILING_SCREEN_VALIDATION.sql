-- ============================================================
-- PHASE 5B TRAILING SCREEN VALIDATION
-- ============================================================

SELECT
    trigger_model,
    trade_side,
    setup_family,
    count() AS trades,
    round(avg(mfe_ticks_10m), 2) AS avg_mfe_ticks_10m,
    round(avg(mae_ticks_10m), 2) AS avg_mae_ticks_10m,
    round(quantile(0.50)(mfe_ticks_10m), 2) AS med_mfe_ticks_10m,
    round(quantile(0.50)(mae_ticks_10m), 2) AS med_mae_ticks_10m
FROM CG_mnq_phase5b_trailing_screen_v1
GROUP BY
    trigger_model,
    trade_side,
    setup_family
ORDER BY
    trigger_model,
    trade_side,
    setup_family;


SELECT
    trigger_model,
    trade_side,
    setup_family,
    trailing_screen_label,
    count() AS trades,
    round(100 * trades / sum(trades) OVER (), 2) AS pct
FROM CG_mnq_phase5b_trailing_screen_v1
GROUP BY
    trigger_model,
    trade_side,
    setup_family,
    trailing_screen_label
ORDER BY
    trailing_screen_label,
    trades DESC;


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

    countIf(trailing_screen_label != 'NO_SCREEN_EDGE') AS possible_edges,
    round(possible_edges / trades, 4) AS possible_edge_rate,

    round(avg(mfe_ticks_10m), 2) AS avg_mfe_ticks_10m,
    round(avg(mae_ticks_10m), 2) AS avg_mae_ticks_10m
FROM CG_mnq_phase5b_trailing_screen_v1
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
HAVING trades >= 50
ORDER BY
    possible_edge_rate DESC,
    trades DESC;
