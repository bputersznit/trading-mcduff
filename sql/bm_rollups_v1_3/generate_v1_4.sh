#!/bin/bash
# Generate v1_4 with canonical symbol normalization from v1_3

SOURCE_FILE="BM_MNQ_01_rollup_scales_v1_3.sql"
OUTPUT_FILE="BM_MNQ_01_rollup_scales_v1_4_CANONICAL.sql"

cat > "$OUTPUT_FILE" << 'HEADER'
-- BM_MNQ_01_rollup_scales_v1_4_CANONICAL.sql
-- Generated: 2026-05-10
--
-- Project: Bookmap Emulation / MNQ
--
-- CRITICAL FIX: CANONICAL SYMBOL NORMALIZATION
--   Problem: Heatmap uses symbol='MNQ', Aggression uses symbol='MNQZ5'
--   Solution: All symbols normalized to front-month canonical: 'MNQZ5'
--
-- Canonical mapping:
--   'MNQ' → 'MNQZ5' (front month December 2025)
--   'MNQZ5' → 'MNQZ5' (passthrough)
--
-- Changes from v1_3:
--   - Added canonicalSymbol() function
--   - All rollups use canonicalSymbol(src.symbol) AS canonical_symbol
--   - Grouping by canonical_symbol instead of src.symbol
--   - Output column renamed: canonical_symbol AS symbol
--
-- Source tables:
--   BM_MNQ_AGGRESSION_EXECUTIONS_100MS (MNQZ5)
--   BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS (MNQ)
--
-- Output scales:
--   1S, 5S, 30S, 1M, 5M (all with canonical symbol MNQZ5)
--
-- Run:
--   clickhouse-client --multiquery < BM_MNQ_01_rollup_scales_v1_4_CANONICAL.sql

SET max_threads = 16;
SET max_memory_usage = 0;
SET max_bytes_before_external_group_by = 20000000000;
SET max_bytes_before_external_sort = 20000000000;
SET optimize_move_to_prewhere = 1;


--------------------------------------------------------------------------------
-- CANONICAL SYMBOL NORMALIZATION FUNCTION
--------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION canonicalSymbol AS (s) -> multiIf(
    s = 'MNQ', 'MNQZ5',
    s = 'MNQZ5', 'MNQZ5',
    s  -- passthrough unknown symbols
);

HEADER

# Transform v1_3: Replace symbol handling with canonical_symbol
sed -E \
  -e '/^-- BM_MNQ_01_rollup_scales_v1_3\.sql/,/^SET max_threads/d' \
  -e 's/src\.symbol AS symbol/canonicalSymbol(src.symbol) AS canonical_symbol/g' \
  -e 's/GROUP BY([^;]*)\n[[:space:]]*src\.symbol,/GROUP BY\1\n        canonical_symbol,/g' \
  -e 's/GROUP BY([^;]*), src\.symbol$/GROUP BY\1, canonical_symbol/g' \
  -e 's/([[:space:]])symbol AS symbol/\1canonical_symbol AS symbol/g' \
  "$SOURCE_FILE" >> "$OUTPUT_FILE"

echo "Generated $OUTPUT_FILE"
wc -l "$OUTPUT_FILE"
