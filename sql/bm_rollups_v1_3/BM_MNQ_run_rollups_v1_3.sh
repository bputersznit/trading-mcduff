#!/usr/bin/env bash
# BM_MNQ_run_rollups_v1_3.sh
# Generated: 2026-05-10 13:18:00 America/New_York
#
# Purpose:
#   Run the BM_MNQ Bookmap emulation rollup files in the correct order.
#
# Usage:
#   chmod +x BM_MNQ_run_rollups_v1_3.sh
#   ./BM_MNQ_run_rollups_v1_3.sh

set -euo pipefail

echo "=== BM_MNQ rollup build v1_3 ==="
echo "[1/5] Verifying files are present..."

for f in \
  BM_MNQ_00_preflight_v1_3.sql \
  BM_MNQ_01_rollup_scales_v1_3.sql \
  BM_MNQ_02_postrun_qa_v1_3.sql
do
  if [[ ! -f "$f" ]]; then
    echo "ERROR: missing required file: $f" >&2
    exit 1
  fi
done

echo "[2/5] Checking that old bad direct aggregate alias patterns are absent..."
if grep -n "max(heatmap_proxy_value) AS heatmap_proxy_value" BM_MNQ_01_rollup_scales_v1_3.sql; then
  echo "ERROR: old heatmap aggregate alias pattern found. Do not run this file." >&2
  exit 1
fi

if grep -n "sum(total_exec_size) AS total_exec_size" BM_MNQ_00_preflight_v1_3.sql; then
  echo "ERROR: old preflight aggregate alias pattern found. Do not run this file." >&2
  exit 1
fi

echo "[3/5] Running preflight..."
clickhouse-client --multiquery < BM_MNQ_00_preflight_v1_3.sql

echo "[4/5] Building rollups..."
clickhouse-client --multiquery < BM_MNQ_01_rollup_scales_v1_3.sql

echo "[5/5] Running post-run QA..."
clickhouse-client --multiquery < BM_MNQ_02_postrun_qa_v1_3.sql

echo "=== BM_MNQ rollup build v1_3 complete ==="
