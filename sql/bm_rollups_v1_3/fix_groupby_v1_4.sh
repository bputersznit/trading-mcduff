#!/bin/bash

INPUT="BM_MNQ_01_rollup_scales_v1_4_CANONICAL.sql"
OUTPUT="BM_MNQ_01_rollup_scales_v1_4_CANONICAL_FIXED.sql"

# Fix GROUP BY to use canonical_symbol instead of src.symbol
sed -E \
  -e 's/GROUP BY([^;]*)[[:space:]]+src\.symbol,/GROUP BY\1 canonical_symbol,/g' \
  -e 's/,([[:space:]]*)src\.symbol([[:space:]]*$)/,\1canonical_symbol\2/g' \
  "$INPUT" > "$OUTPUT"

echo "Fixed GROUP BY clauses"
mv "$OUTPUT" "$INPUT"
