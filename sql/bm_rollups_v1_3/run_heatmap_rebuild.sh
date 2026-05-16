#!/bin/bash
# Run state-based heatmap rebuild v2 (with Modify tracking)

export CH_HOST='localhost'
export CH_PORT='8123'
export CH_USER='default'
export CH_PASSWORD='unlucky-strange'
export CH_DATABASE='default'
export CH_SECURE='false'

echo "=== BM_MNQ Heatmap State-Based Rebuild v2 ==="
echo
echo "Improvements:"
echo "  ✓ Tracks order book STATE (not just events)"
echo "  ✓ Handles Modify events correctly (tracks order_id)"
echo "  ✓ Creates CONTINUOUS BANDS at all price levels"
echo
echo "Expected time: 45-90 minutes"
echo "Expected output: 300M-500M rows (vs current 198M)"
echo
read -p "Press Enter to start rebuild..."

python3 rebuild_heatmap_stateful_v2.py 2>&1 | tee heatmap_rebuild_v2_$(date +%Y%m%d_%H%M%S).log

echo
echo "=== Rebuild Complete ===" 
echo "Check log file for details"
