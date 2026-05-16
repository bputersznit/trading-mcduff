#!/usr/bin/env bash
# BM_MNQ_run_append_aggression_bubbles_v1_7.sh
# Generated: 2026-05-10 14:28:00 America/New_York
#
# Purpose:
#   Append aggression bubble rows to existing v1.6 heatmap-only frame sources.
#
# Usage:
#   chmod +x BM_MNQ_run_append_aggression_bubbles_v1_7.sh
#   ./BM_MNQ_run_append_aggression_bubbles_v1_7.sh

set -euo pipefail

CH_CLIENT="${CH_CLIENT:-clickhouse-client}"

echo "=== BM_MNQ append aggression bubbles v1_7 ==="

for f in \
  BM_MNQ_06_append_aggression_bubbles_v1_7.sql \
  BM_MNQ_07_frame_repair_qa_v1_7.sql
do
  if [[ ! -f "$f" ]]; then
    echo "ERROR: missing required file: $f" >&2
    exit 1
  fi
done

echo "[1/2] Appending aggression bubble rows..."
"$CH_CLIENT" --multiquery < BM_MNQ_06_append_aggression_bubbles_v1_7.sql

echo "[2/2] Running frame QA..."
"$CH_CLIENT" --multiquery < BM_MNQ_07_frame_repair_qa_v1_7.sql

echo "=== BM_MNQ append aggression bubbles v1_7 complete ==="
