#!/bin/bash
# Quick L2 Pipeline Status Check
# Usage: ./check_l2_pipeline_status.sh

echo "============================================"
echo "L2 Data Pipeline Status Check"
echo "============================================"
echo ""

# Check if puller is running
echo "📡 PULLER PROCESS:"
if ps aux | grep -q "[u]buntu_l2_puller.py"; then
    PULLER_PID=$(ps aux | grep "[u]buntu_l2_puller.py" | awk '{print $2}')
    PULLER_CPU=$(ps aux | grep "[u]buntu_l2_puller.py" | awk '{print $3}')
    PULLER_MEM=$(ps aux | grep "[u]buntu_l2_puller.py" | awk '{print $4}')
    echo "  ✅ Running (PID: $PULLER_PID, CPU: ${PULLER_CPU}%, MEM: ${PULLER_MEM}%)"
else
    echo "  ❌ NOT RUNNING - Start with: nohup python3 scripts/ubuntu_l2_puller.py &"
fi
echo ""

# Check ClickHouse data
echo "💾 CLICKHOUSE DATABASE:"
clickhouse-client --query "
SELECT
    '  Total Rows: ' || formatReadableQuantity(count(*)) as metric
FROM l2_depth_raw
UNION ALL
SELECT
    '  Disk Size: ' || formatReadableSize(sum(bytes_on_disk))
FROM system.parts
WHERE table = 'l2_depth_raw' AND active
UNION ALL
SELECT
    '  Date Range: ' || toString(min(date)) || ' to ' || toString(max(date))
FROM l2_depth_raw
UNION ALL
SELECT
    '  Days of Data: ' || toString(count(DISTINCT date))
FROM l2_depth_raw
FORMAT TSVRaw
"
echo ""

# Check recent activity
echo "📊 RECENT IMPORTS (Last 5 days):"
clickhouse-client --query "
SELECT
    '  ' || toString(date) || ': ' || formatReadableQuantity(count(*)) || ' rows'
FROM l2_depth_raw
GROUP BY date
ORDER BY date DESC
LIMIT 5
FORMAT TSVRaw
"
echo ""

# Check VPS storage
echo "🌐 VPS STORAGE:"
echo "  Checking remote files..."
VPS_STATS=$(rclone size vps:Users/Administrator/Documents/CG_L2_Capture --include "*.parquet" 2>&1)
if [ $? -eq 0 ]; then
    VPS_FILES=$(echo "$VPS_STATS" | grep "Total objects:" | awk '{print $3}')
    VPS_SIZE=$(echo "$VPS_STATS" | grep "Total size:" | awk '{print $3, $4}')
    echo "  📦 $VPS_FILES files ($VPS_SIZE) remaining on VPS"
else
    echo "  ⚠️  Could not connect to VPS"
fi
echo ""

# Check puller log for errors
echo "📝 RECENT ACTIVITY (Last 10 lines of log):"
if [ -f "l2_puller.log" ]; then
    tail -10 l2_puller.log | sed 's/^/  /'
else
    echo "  ⚠️  Log file not found"
fi
echo ""

# Check CPU usage
echo "⚡ CPU USAGE:"
TOTAL_CPU=$(ps aux | grep -E "python3.*l2|rclone" | grep -v grep | awk '{sum+=$3} END {print sum}')
if [ -z "$TOTAL_CPU" ]; then
    TOTAL_CPU=0
fi
echo "  L2 Pipeline: ${TOTAL_CPU}% (Target: <75%)"
if (( $(echo "$TOTAL_CPU < 75" | bc -l) )); then
    echo "  ✅ Within limits"
else
    echo "  ⚠️  Exceeding target"
fi
echo ""

# Summary
echo "============================================"
echo "SUMMARY:"
if ps aux | grep -q "[u]buntu_l2_puller.py"; then
    echo "  🟢 Pipeline is OPERATIONAL"
else
    echo "  🔴 Pipeline is DOWN"
fi
echo "============================================"
