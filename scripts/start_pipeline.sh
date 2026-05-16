#!/bin/bash
# Quick-start script for L2 pipeline on Ubuntu

set -e

echo "=== Starting L2 Automated Pipeline ==="
echo ""

# Create data directory
echo "Creating data directory..."
mkdir -p ~/trading4/CG_MNQ_MarketReplayLab/data/l2_parquet

# Check ClickHouse
echo "Checking ClickHouse..."
if ! clickhouse-client --query "SELECT 1" &>/dev/null; then
    echo "ERROR: ClickHouse not running or not accessible"
    exit 1
fi
echo "✓ ClickHouse OK"

# Check table exists
echo "Checking l2_depth_raw table..."
if ! clickhouse-client --query "EXISTS TABLE l2_depth_raw" &>/dev/null; then
    echo "Creating table..."
    clickhouse-client --query "$(cat sql/l2_depth_schema.sql)"
fi
echo "✓ Table OK"

# Start importer
echo ""
echo "Starting L2 importer..."
cd ~/trading4/CG_MNQ_MarketReplayLab/scripts
nohup python3 ubuntu_l2_importer.py > ../logs/importer.out 2>&1 &
IMPORTER_PID=$!
echo "✓ Importer started (PID: $IMPORTER_PID)"

echo ""
echo "=== Pipeline Started ==="
echo "Monitor with:"
echo "  tail -f logs/importer.out"
echo "  tail -f scripts/l2_importer.log"
echo ""
echo "Stop with:"
echo "  kill $IMPORTER_PID"
echo ""
echo "Now start the VPS watcher on Windows"
