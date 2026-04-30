#!/bin/bash
# =====================================================================
# T2 ClanMarshal Parity Test - Execution Script
# =====================================================================

set -e  # Exit on error

echo "=== T2 CLANMARSHAL PARITY TEST ==="
echo ""
echo "This script will:"
echo "  1. Verify existing T2 backtest data (908 trades)"
echo "  2. Regenerate signals from features table"
echo "  3. Compare with existing results"
echo "  4. Export signals for NT8 comparison"
echo ""
echo "Press Enter to continue or Ctrl+C to abort..."
read

# Run the SQL file
clickhouse-client --multiquery < CG_T2_ClanMarshal_Parity_Test.sql

echo ""
echo "=== TEST COMPLETE ==="
echo ""
echo "Output files created:"
echo "  /tmp/CG_T2_signals_for_nt8_comparison.csv"
echo "  /tmp/CG_T2_backtest_trades_reference.csv"
echo ""
echo "Table created:"
echo "  CG_T2_signals_parity_test"
echo ""
echo "Next steps:"
echo "  1. Review the summary report above"
echo "  2. Investigate any missing/extra signals"
echo "  3. Compare NT8 telemetry with /tmp/CG_T2_signals_for_nt8_comparison.csv"
echo ""
