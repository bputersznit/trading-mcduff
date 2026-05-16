-- BM_MNQ_08_heatmap_visibility_diagnostics_v1.sql
-- Generated: 2026-05-10 15:35:00 America/New_York
--
-- Purpose:
--   Diagnose why BM_MNQ renderer shows black background/no visible heatmap.
--
-- This checks:
--   1. Whether the chosen 2025-10-07 09:30-09:35 window has heatmap rows.
--   2. Whether heatmap_intensity is zero in that window.
--   3. Which 5-minute RTH windows have the strongest heatmap content.
--   4. Whether aggression and heatmap are stored as separate frame rows.
--
-- Run:
--   clickhouse-client --multiquery < BM_MNQ_08_heatmap_visibility_diagnostics_v1.sql

SET max_threads = 8;

--------------------------------------------------------------------------------
-- 1. Exact window currently rendered
--------------------------------------------------------------------------------

WITH win AS
(
    SELECT
        count() AS rows,
        countIf(total_liquidity_event_size > 0 OR heatmap_proxy_value > 0 OR heatmap_intensity > 0) AS heatmap_rows,
        countIf(total_exec_size > 0) AS aggression_rows,
        sum(total_liquidity_event_size) AS total_liquidity_event_size,
        sum(total_exec_size) AS total_exec_size,
        max(heatmap_proxy_value) AS max_heatmap_proxy_value,
        max(heatmap_intensity) AS max_heatmap_intensity,
        min(price) AS min_price,
        max(price) AS max_price,
        uniqExact(ts_et) AS time_buckets,
        uniqExact(price) AS price_levels
    FROM BM_MNQ_FRAME_SOURCE_1S
    WHERE trade_date = toDate('2025-10-07')
      AND symbol = 'MNQZ5'
      AND ts_et >= toDateTime64('2025-10-07 09:30:00', 3, 'America/New_York')
      AND ts_et <  toDateTime64('2025-10-07 09:35:00', 3, 'America/New_York')
)
SELECT
    'CURRENT_RENDER_WINDOW_2025_10_07_0930_0935_1S' AS check_name,
    *
FROM win;

--------------------------------------------------------------------------------
-- 2. Heatmap row sample from exact window
--------------------------------------------------------------------------------

SELECT
    'CURRENT_WINDOW_HEATMAP_SAMPLE' AS check_name,
    ts_et,
    price,
    total_liquidity_event_size,
    heatmap_proxy_value,
    heatmap_intensity
FROM BM_MNQ_FRAME_SOURCE_1S
WHERE trade_date = toDate('2025-10-07')
  AND symbol = 'MNQZ5'
  AND ts_et >= toDateTime64('2025-10-07 09:30:00', 3, 'America/New_York')
  AND ts_et <  toDateTime64('2025-10-07 09:35:00', 3, 'America/New_York')
  AND (total_liquidity_event_size > 0 OR heatmap_proxy_value > 0 OR heatmap_intensity > 0)
ORDER BY heatmap_proxy_value DESC, total_liquidity_event_size DESC
LIMIT 20;

--------------------------------------------------------------------------------
-- 3. Top 5-minute RTH windows by heatmap content on 2025-10-07
--------------------------------------------------------------------------------

WITH by_window AS
(
    SELECT
        toStartOfInterval(ts_et, toIntervalMinute(5)) AS window_start_et,
        count() AS rows,
        countIf(total_liquidity_event_size > 0 OR heatmap_proxy_value > 0 OR heatmap_intensity > 0) AS heatmap_rows,
        countIf(total_exec_size > 0) AS aggression_rows,
        sum(total_liquidity_event_size) AS total_liquidity_event_size,
        sum(total_exec_size) AS total_exec_size,
        max(heatmap_proxy_value) AS max_heatmap_proxy_value,
        max(heatmap_intensity) AS max_heatmap_intensity,
        uniqExact(price) AS price_levels
    FROM BM_MNQ_FRAME_SOURCE_1S
    WHERE trade_date = toDate('2025-10-07')
      AND symbol = 'MNQZ5'
      AND ts_et >= toDateTime64('2025-10-07 09:30:00', 3, 'America/New_York')
      AND ts_et <  toDateTime64('2025-10-07 16:00:00', 3, 'America/New_York')
    GROUP BY window_start_et
)
SELECT
    'TOP_5M_RTH_WINDOWS_BY_HEATMAP_2025_10_07' AS check_name,
    window_start_et,
    rows,
    heatmap_rows,
    aggression_rows,
    total_liquidity_event_size,
    total_exec_size,
    max_heatmap_proxy_value,
    max_heatmap_intensity,
    price_levels
FROM by_window
ORDER BY heatmap_rows DESC, total_liquidity_event_size DESC
LIMIT 20;

--------------------------------------------------------------------------------
-- 4. Top 5-minute RTH windows across all dates
--------------------------------------------------------------------------------

WITH by_window AS
(
    SELECT
        trade_date,
        toStartOfInterval(ts_et, toIntervalMinute(5)) AS window_start_et,
        count() AS rows,
        countIf(total_liquidity_event_size > 0 OR heatmap_proxy_value > 0 OR heatmap_intensity > 0) AS heatmap_rows,
        countIf(total_exec_size > 0) AS aggression_rows,
        sum(total_liquidity_event_size) AS total_liquidity_event_size,
        sum(total_exec_size) AS total_exec_size,
        max(heatmap_proxy_value) AS max_heatmap_proxy_value,
        max(heatmap_intensity) AS max_heatmap_intensity,
        uniqExact(price) AS price_levels
    FROM BM_MNQ_FRAME_SOURCE_1S
    WHERE symbol = 'MNQZ5'
      AND ts_et >= toDateTime64(concat(toString(trade_date), ' 09:30:00'), 3, 'America/New_York')
      AND ts_et <  toDateTime64(concat(toString(trade_date), ' 16:00:00'), 3, 'America/New_York')
    GROUP BY
        trade_date,
        window_start_et
)
SELECT
    'TOP_5M_RTH_WINDOWS_ALL_DATES_BY_HEATMAP' AS check_name,
    trade_date,
    window_start_et,
    rows,
    heatmap_rows,
    aggression_rows,
    total_liquidity_event_size,
    total_exec_size,
    max_heatmap_proxy_value,
    max_heatmap_intensity,
    price_levels
FROM by_window
ORDER BY heatmap_rows DESC, total_liquidity_event_size DESC
LIMIT 30;

--------------------------------------------------------------------------------
-- 5. Daily heatmap/aggression summary
--------------------------------------------------------------------------------

WITH daily AS
(
    SELECT
        trade_date,
        count() AS rows,
        countIf(total_liquidity_event_size > 0 OR heatmap_proxy_value > 0 OR heatmap_intensity > 0) AS heatmap_rows,
        countIf(total_exec_size > 0) AS aggression_rows,
        sum(total_liquidity_event_size) AS total_liquidity_event_size,
        sum(total_exec_size) AS total_exec_size,
        max(heatmap_proxy_value) AS max_heatmap_proxy_value,
        max(heatmap_intensity) AS max_heatmap_intensity
    FROM BM_MNQ_FRAME_SOURCE_1S
    WHERE symbol = 'MNQZ5'
    GROUP BY trade_date
)
SELECT
    'DAILY_FRAME_LAYER_SUMMARY_1S' AS check_name,
    *
FROM daily
ORDER BY trade_date;
