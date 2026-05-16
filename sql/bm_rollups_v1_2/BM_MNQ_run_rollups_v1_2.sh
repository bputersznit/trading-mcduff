#!/usr/bin/env bash
# BM_MNQ_run_rollups_v1_2.sh
# Generated: 2026-05-10 13:05:00 America/New_York
#
# Purpose:
#   Run the BM_MNQ Bookmap emulation rollup files in the correct order.
#
# Usage:
#   chmod +x BM_MNQ_run_rollups_v1_2.sh
#   ./BM_MNQ_run_rollups_v1_2.sh
#
# Notes:
#   - Run this from the same directory as the SQL files.
#   - This intentionally uses unique v1_2 filenames to avoid stale terminal
#     history or old downloaded files.

set -euo pipefail

echo "=== BM_MNQ rollup build v1_2 ==="
echo "[1/4] Verifying files are present..."

for f in \
  BM_MNQ_00_preflight_v1_2.sql \
  BM_MNQ_01_rollup_scales_v1_2.sql \
  BM_MNQ_02_postrun_qa_v1_2.sql
do
  if [[ ! -f "$f" ]]; then
    echo "ERROR: missing required file: $f" >&2
    exit 1
  fi
done

echo "[2/4] Checking that old bad direct heatmap aggregate pattern is absent..."
if grep -n "max(heatmap_proxy_value) AS heatmap_proxy_value" BM_MNQ_01_rollup_scales_v1_2.sql; then
  echo "ERROR: old bad aggregate alias pattern found. Do not run this file." >&2
  exit 1
fi

echo "[3/4] Running preflight..."
clickhouse-client --multiquery < BM_MNQ_00_preflight_v1_2.sql

echo "[4/4] Building rollups..."
clickhouse-client --multiquery < BM_MNQ_01_rollup_scales_v1_2.sql

echo "[5/5] Running post-run QA..."
clickhouse-client --multiquery < BM_MNQ_02_postrun_qa_v1_2.sql

echo "=== BM_MNQ rollup build v1_2 complete ==="
