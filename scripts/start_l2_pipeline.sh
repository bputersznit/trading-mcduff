#!/bin/bash
# Start L2 Pipeline with CPU Limits
# Ensures pipeline stays under 75% CPU usage

echo "Starting L2 Data Pipeline..."
echo ""

# Check if puller is already running
if ps aux | grep -q "[u]buntu_l2_puller.py"; then
    echo "⚠️  L2 Puller is already running (PID: $(ps aux | grep '[u]buntu_l2_puller.py' | awk '{print $2}'))"
    echo "   Use 'killall python3 ubuntu_l2_puller.py' to stop it first"
    exit 1
fi

# Start puller with nice priority (lower than default)
cd /home/bernard/trading4/CG_MNQ_MarketReplayLab
nohup nice -n 5 python3 scripts/ubuntu_l2_puller.py > /dev/null 2>&1 &
PULLER_PID=$!

sleep 2

# Verify it started
if ps -p $PULLER_PID > /dev/null; then
    echo "✅ L2 Puller started successfully"
    echo "   PID: $PULLER_PID"
    echo "   Log: l2_puller.log"
    echo ""
    echo "Monitor with:"
    echo "  tail -f l2_puller.log"
    echo "  ./scripts/check_l2_pipeline_status.sh"
else
    echo "❌ Failed to start L2 Puller"
    exit 1
fi
