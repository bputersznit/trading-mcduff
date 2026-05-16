#!/usr/bin/env bash
# BM_MNQ_run_frame_repair_v1_4.sh
# Generated: 2026-05-10 13:40:00 America/New_York
#
# Purpose:
#   Rebuild only BM_MNQ_FRAME_SOURCE_* tables after successful v1.3 rollups.
#
# Usage:
#   chmod +x BM_MNQ_run_frame_repair_v1_4.sh
#   ./BM_MNQ_run_frame_repair_v1_4.sh

set -euo pipefail

echo "=== BM_MNQ frame repair v1_4 ==="

for f in \
  BM_MNQ_03_rebuild_frame_sources_v1_4.sql \
  BM_MNQ_04_frame_repair_qa_v1_4.sql
do
  if [[ ! -f "$f" ]]; then
    echo "ERROR: missing required file: $f" >&2
    exit 1
  fi
done

echo "[1/2] Rebuilding frame sources with unified heatmap+aggression keyset..."
clickhouse-client --multiquery < BM_MNQ_03_rebuild_frame_sources_v1_4.sql

echo "[2/2] Running frame repair QA..."
clickhouse-client --multiquery < BM_MNQ_04_frame_repair_qa_v1_4.sql

echo "=== BM_MNQ frame repair v1_4 complete ==="
