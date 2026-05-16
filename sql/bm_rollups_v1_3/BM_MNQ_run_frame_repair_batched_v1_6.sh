#!/usr/bin/env bash
# BM_MNQ_run_frame_repair_batched_v1_6.sh
# Generated: 2026-05-10 14:12:00 America/New_York
#
# Purpose:
#   Memory-safer frame-source repair for BM_MNQ_* Bookmap emulation.
#
# v1.6 fix:
#   Removed unsupported ClickHouse setting:
#       the unsupported external-join memory setting
#
# Usage:
#   chmod +x BM_MNQ_run_frame_repair_batched_v1_6.sh
#   ./BM_MNQ_run_frame_repair_batched_v1_6.sh

set -euo pipefail

CH_CLIENT="${CH_CLIENT:-clickhouse-client}"
SCALES=("1S" "5S" "30S" "1M" "5M")

echo "=== BM_MNQ frame repair batched v1_6 ==="

for f in \
  BM_MNQ_03_create_empty_frame_sources_v1_6.sql \
  BM_MNQ_05_frame_repair_qa_v1_6.sql
do
  if [[ ! -f "$f" ]]; then
    echo "ERROR: missing required file: $f" >&2
    exit 1
  fi
done

echo "[1/4] Recreating empty frame-source tables..."
"$CH_CLIENT" --multiquery < BM_MNQ_03_create_empty_frame_sources_v1_6.sql

echo "[2/4] Reading trade_date partitions..."
mapfile -t TRADE_DATES < <("$CH_CLIENT" --query "
SELECT toString(trade_date)
FROM
(
    SELECT trade_date FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
    UNION DISTINCT
    SELECT trade_date FROM BM_MNQ_AGGRESSION_EXECUTIONS_1S
)
ORDER BY trade_date
")

echo "Found ${#TRADE_DATES[@]} trade_date partitions."

for SCALE in "${SCALES[@]}"; do
  echo "=== Scale ${SCALE}: inserting heatmap-led rows ==="

  for D in "${TRADE_DATES[@]}"; do
    echo "[${SCALE}] heatmap-led ${D}"

    "$CH_CLIENT" --multiquery --query "
SET max_threads = 6;
SET max_memory_usage = 0;
SET max_bytes_before_external_group_by = 20000000000;
SET max_bytes_before_external_sort = 20000000000;

INSERT INTO BM_MNQ_FRAME_SOURCE_${SCALE}
SELECT
    h.trade_date AS trade_date,
    h.ts_bucket AS ts_bucket,
    h.ts_et AS ts_et,
    h.symbol AS symbol,
    h.price AS price,

    h.bid_add_size AS bid_add_size,
    h.ask_add_size AS ask_add_size,
    h.bid_cancel_size AS bid_cancel_size,
    h.ask_cancel_size AS ask_cancel_size,
    h.bid_modify_size AS bid_modify_size,
    h.ask_modify_size AS ask_modify_size,
    h.bid_trade_size AS bid_trade_size,
    h.ask_trade_size AS ask_trade_size,

    h.bid_event_count AS bid_event_count,
    h.ask_event_count AS ask_event_count,
    h.total_event_count AS total_event_count,

    h.bid_liquidity_event_size AS bid_liquidity_event_size,
    h.ask_liquidity_event_size AS ask_liquidity_event_size,
    h.total_liquidity_event_size AS total_liquidity_event_size,
    h.net_liquidity_event_delta AS net_liquidity_event_delta,

    h.heatmap_proxy_value AS heatmap_proxy_value,
    h.max_heatmap_proxy_value AS max_heatmap_proxy_value,
    h.avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
    h.persistence_bucket_count AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    least
    (
        1,
        if
        (
            ifNull(d.daily_max_heatmap_proxy_value, 0) <= 0,
            0,
            log(1 + h.heatmap_proxy_value) / log(1 + d.daily_max_heatmap_proxy_value)
        )
    ) AS heatmap_intensity,

    if
    (
        h.rth_flag = 1
        AND h.bid_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.bid_liquidity_event_size > h.ask_liquidity_event_size
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_bid_liquidity_event_wall,

    if
    (
        h.rth_flag = 1
        AND h.ask_liquidity_event_size >= ifNull(d.daily_p999_heatmap_proxy_value, 0)
        AND h.ask_liquidity_event_size > h.bid_liquidity_event_size
        AND ifNull(d.daily_p999_heatmap_proxy_value, 0) > 0,
        1,
        0
    ) AS is_ask_liquidity_event_wall,

    ifNull(a.buy_exec_size, 0) AS buy_exec_size,
    ifNull(a.sell_exec_size, 0) AS sell_exec_size,
    ifNull(a.total_exec_size, 0) AS total_exec_size,
    ifNull(a.exec_delta, 0) AS exec_delta,
    ifNull(a.exec_imbalance, 0) AS exec_imbalance,
    ifNull(a.buy_trade_count, 0) AS buy_trade_count,
    ifNull(a.sell_trade_count, 0) AS sell_trade_count,
    ifNull(a.total_trade_count, 0) AS total_trade_count,
    ifNull(a.bubble_total_size, 0) AS bubble_total_size,
    ifNull(a.bubble_buy_share, 0) AS bubble_buy_share,
    ifNull(a.bubble_sell_share, 0) AS bubble_sell_share,
    ifNull(a.bubble_side, 'NONE') AS bubble_side,

    greatest(h.rth_flag, ifNull(a.rth_flag, 0)) AS rth_flag
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_${SCALE} AS h
LEFT ANY JOIN
(
    SELECT *
    FROM BM_MNQ_AGGRESSION_EXECUTIONS_${SCALE}
    WHERE trade_date = toDate('${D}')
) AS a
    ON  h.trade_date = a.trade_date
    AND h.ts_bucket  = a.ts_bucket
    AND h.symbol     = a.symbol
    AND h.price      = a.price
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON h.trade_date = d.trade_date
WHERE h.trade_date = toDate('${D}');
"
  done

  echo "=== Scale ${SCALE}: inserting aggression-only rows ==="

  for D in "${TRADE_DATES[@]}"; do
    echo "[${SCALE}] aggression-only ${D}"

    "$CH_CLIENT" --multiquery --query "
SET max_threads = 6;
SET max_memory_usage = 0;
SET max_bytes_before_external_group_by = 20000000000;
SET max_bytes_before_external_sort = 20000000000;

INSERT INTO BM_MNQ_FRAME_SOURCE_${SCALE}
SELECT
    a.trade_date AS trade_date,
    a.ts_bucket AS ts_bucket,
    a.ts_et AS ts_et,
    a.symbol AS symbol,
    a.price AS price,

    0 AS bid_add_size,
    0 AS ask_add_size,
    0 AS bid_cancel_size,
    0 AS ask_cancel_size,
    0 AS bid_modify_size,
    0 AS ask_modify_size,
    0 AS bid_trade_size,
    0 AS ask_trade_size,

    0 AS bid_event_count,
    0 AS ask_event_count,
    0 AS total_event_count,

    0 AS bid_liquidity_event_size,
    0 AS ask_liquidity_event_size,
    0 AS total_liquidity_event_size,
    0 AS net_liquidity_event_delta,

    0 AS heatmap_proxy_value,
    0 AS max_heatmap_proxy_value,
    0 AS avg_heatmap_proxy_value,
    0 AS persistence_bucket_count,

    ifNull(d.daily_max_heatmap_proxy_value, 0) AS daily_max_heatmap_proxy_value,
    ifNull(d.daily_p999_heatmap_proxy_value, 0) AS daily_p999_heatmap_proxy_value,

    0 AS heatmap_intensity,
    0 AS is_bid_liquidity_event_wall,
    0 AS is_ask_liquidity_event_wall,

    a.buy_exec_size AS buy_exec_size,
    a.sell_exec_size AS sell_exec_size,
    a.total_exec_size AS total_exec_size,
    a.exec_delta AS exec_delta,
    a.exec_imbalance AS exec_imbalance,
    a.buy_trade_count AS buy_trade_count,
    a.sell_trade_count AS sell_trade_count,
    a.total_trade_count AS total_trade_count,
    a.bubble_total_size AS bubble_total_size,
    a.bubble_buy_share AS bubble_buy_share,
    a.bubble_sell_share AS bubble_sell_share,
    a.bubble_side AS bubble_side,

    a.rth_flag AS rth_flag
FROM BM_MNQ_AGGRESSION_EXECUTIONS_${SCALE} AS a
LEFT ANY JOIN
(
    SELECT
        trade_date,
        ts_bucket,
        symbol,
        price
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_${SCALE}
    WHERE trade_date = toDate('${D}')
) AS h
    ON  a.trade_date = h.trade_date
    AND a.ts_bucket  = h.ts_bucket
    AND a.symbol     = h.symbol
    AND a.price      = h.price
LEFT ANY JOIN BM_MNQ_HEATMAP_DAILY_MAX_RTH AS d
    ON a.trade_date = d.trade_date
WHERE a.trade_date = toDate('${D}')
  AND h.trade_date IS NULL;
"
  done

  echo "=== Scale ${SCALE}: optimize table ==="
  "$CH_CLIENT" --query "OPTIMIZE TABLE BM_MNQ_FRAME_SOURCE_${SCALE} FINAL;"
done

echo "[3/4] Running frame repair QA..."
"$CH_CLIENT" --multiquery < BM_MNQ_05_frame_repair_qa_v1_6.sql

echo "[4/4] Done."
echo "=== BM_MNQ frame repair batched v1_6 complete ==="
