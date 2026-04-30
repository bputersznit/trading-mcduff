#!/usr/bin/env bash
set -euo pipefail

# CG_insert_heatmap_wall_tests_by_date.sh
#
# Strategy:
# - Create target table once if missing
# - Detect which trade_date partitions are not yet loaded
# - Insert one trade_date at a time for restartability
# - Battery-aware pause before each new date batch
# - Safe to rerun after interruption
# - Compatible with existing 13-column CG_mnq_heatmap_wall_tests_100ms schema
#
# Usage:
# chmod +x CG_insert_heatmap_wall_tests_by_date.sh
# ./CG_insert_heatmap_wall_tests_by_date.sh
#
# Optional:
# BATTERY_PAUSE_PCT=40 ./CG_insert_heatmap_wall_tests_by_date.sh
# CH_CLIENT=/usr/bin/clickhouse-client ./CG_insert_heatmap_wall_tests_by_date.sh

CH_CLIENT="${CH_CLIENT:-clickhouse-client}"
CH_DATABASE="${CH_DATABASE:-default}"
BATTERY_PAUSE_PCT="${BATTERY_PAUSE_PCT:-35}"

TARGET="CG_mnq_heatmap_wall_tests_100ms"
WALLS="CG_mnq_heatmap_walls_15m"
HEATMAP="CG_mnq_heatmap_100ms"

run_ch() {
  "$CH_CLIENT" --database "$CH_DATABASE" --multiquery --query "$1"
}

battery_pct() {
  local cap_file
  cap_file=$(find /sys/class/power_supply/ -maxdepth 2 -path '*/BAT*/*' -name capacity 2>/dev/null | head -n 1 || true)

  if [[ -z "$cap_file" ]]; then
    echo "[warn] No battery detected under /sys/class/power_supply/BAT*. Assuming AC-powered system." >&2
    echo 100
    return
  fi

  cat "$cap_file"
}

battery_state() {
  local status_file
  status_file=$(find /sys/class/power_supply/ -maxdepth 2 -path '*/BAT*/*' -name status 2>/dev/null | head -n 1 || true)

  if [[ -z "$status_file" ]]; then
    echo "[warn] No battery status detected under /sys/class/power_supply/BAT*. Assuming Full." >&2
    echo "Full"
    return
  fi

  cat "$status_file"
}

battery_state() {
  local status_file
  status_file=$(find /sys/class/power_supply/ -maxdepth 2 -name status 2>/dev/null | head -n 1 || true)

  if [[ -z "$status_file" ]]; then
    echo "[warn] No battery status source found. Assuming Full." >&2
    echo "Full"
    return
  fi

  cat "$status_file"
}

battery_guard() {
  local pct state

  while true; do
    pct="$(battery_pct)"
    state="$(battery_state)"

    echo "[battery] pct=${pct}% state=${state}"

    # Safe to proceed
    if [[ "$state" == "Charging" || "$state" == "Full" || "$pct" -ge 55 ]]; then
      return
    fi

    # Pause zone
    if [[ "$pct" -le "$BATTERY_PAUSE_PCT" ]]; then
      echo "[pause] Battery is ${pct}% and state=${state}."
      echo "[pause] Waiting until battery reaches 55% or system is charging."
    else
      echo "[wait] Battery below safe restart threshold (55%). Current=${pct}%"
    fi

    sleep 60
  done
}

echo "[info] Creating target table if needed..."

run_ch "
CREATE TABLE IF NOT EXISTS $TARGET
(
    trade_date Date,
    wall_time DateTime64(3, 'UTC'),
    wall_et DateTime,
    wall_side LowCardinality(String),
    wall_price Float64,
    wall_score Float64,

    first_test_time Nullable(DateTime64(3, 'UTC')),
    max_price_after Float64,
    min_price_after Float64,
    fill_through_wall Float64,
    cancel_near_wall Float64,
    add_near_wall Float64,
    wall_outcome LowCardinality(String)
)
ENGINE = MergeTree
PARTITION BY trade_date
ORDER BY (trade_date, wall_time, wall_side, wall_price);
"

echo "[info] Finding missing trade_date values..."

DATES=$(
  "$CH_CLIENT" --database "$CH_DATABASE" --query "
SELECT trade_date
FROM
(
    SELECT DISTINCT trade_date
    FROM $WALLS
)
WHERE trade_date NOT IN
(
    SELECT DISTINCT trade_date
    FROM $TARGET
)
ORDER BY trade_date
FORMAT TSV
"
)

if [[ -z "$DATES" ]]; then
  echo "[done] No missing dates. Target is already populated."
  exit 0
fi

echo "[info] Missing dates to process:"
printf '%s\n' $DATES

for D in $DATES; do
  battery_guard

  echo "[info] Inserting trade_date=$D"

  run_ch "
INSERT INTO $TARGET
(
    trade_date,
    wall_time,
    wall_et,
    wall_side,
    wall_price,
    wall_score,
    first_test_time,
    max_price_after,
    min_price_after,
    fill_through_wall,
    cancel_near_wall,
    add_near_wall,
    wall_outcome
)
SELECT
    trade_date,
    wall_time,
    wall_et,
    wall_side,
    wall_price,
    wall_score,
    first_test_time,
    max_price_after,
    min_price_after,
    fill_through_wall,
    cancel_near_wall,
    add_near_wall,
    multiIf(
        test_rows = 0,
            'UNTOUCHED',

        wall_side = 'ASK' AND max_price_after > wall_price + 2.,
            'BROKE_UP',

        wall_side = 'BID' AND min_price_after < wall_price - 2.,
            'BROKE_DOWN',

        cancel_near_wall > add_near_wall * 1.5,
            'PULLED',

        fill_through_wall > wall_score * 2
            AND abs(max_price_after - min_price_after) < 4.,
            'ABSORBED',

        'HELD'
    ) AS wall_outcome
FROM
(
    SELECT
        w.trade_date,
        w.bucket_time AS wall_time,
        w.bucket_et AS wall_et,
        w.wall_side,
        w.price AS wall_price,
        w.wall_score,

        count(h.bucket_time) AS test_rows,
        minOrNull(h.bucket_time) AS first_test_time,

        maxIf(
            h.price,
            h.bucket_time <= w.bucket_time + toIntervalMinute(30)
        ) AS max_price_after,

        minIf(
            h.price,
            h.bucket_time <= w.bucket_time + toIntervalMinute(30)
        ) AS min_price_after,

        sumIf(
            h.bid_fill_size + h.ask_fill_size,
            h.bucket_time <= w.bucket_time + toIntervalMinute(30)
        ) AS fill_through_wall,

        sumIf(
            h.bid_cancel_size + h.ask_cancel_size,
            h.bucket_time <= w.bucket_time + toIntervalMinute(30)
        ) AS cancel_near_wall,

        sumIf(
            h.bid_add_size + h.ask_add_size,
            h.bucket_time <= w.bucket_time + toIntervalMinute(30)
        ) AS add_near_wall

    FROM $WALLS AS w
    LEFT JOIN $HEATMAP AS h
        ON w.trade_date = h.trade_date
       AND h.bucket_time >= w.bucket_time
       AND h.bucket_time <= w.bucket_time + toIntervalMinute(30)
       AND abs(h.price - w.price) <= 4.

    WHERE w.trade_date = toDate('$D')

    GROUP BY
        w.trade_date,
        wall_time,
        wall_et,
        wall_side,
        wall_price,
        wall_score
);
"

  echo "[verify] Rows loaded for $D:"
  "$CH_CLIENT" --database "$CH_DATABASE" --query "
SELECT
    trade_date,
    count() AS inserted_rows
FROM $TARGET
WHERE trade_date = toDate('$D')
GROUP BY trade_date;
"

done

echo "[done] Finished all missing dates."

