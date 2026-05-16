#!/bin/bash
# Copy AutoDeploy MVP files to VPS

SOURCE="/home/bernard/Downloads/CG_NT8_AutoDeploy_MVP_v0_1/"
DEST="vps:/CG_NT8_AutoDeploy"

echo "═══════════════════════════════════════════════════════"
echo "  Copy AutoDeploy MVP to VPS"
echo "═══════════════════════════════════════════════════════"
echo ""

# Check if VPS remote is configured
if ! rclone listremotes | grep -q "vps:"; then
    echo "❌ VPS remote not configured!"
    echo ""
    echo "Run this first:"
    echo "  /home/bernard/trading4/CG_MNQ_MarketReplayLab/scripts/auto_setup_vps_rclone.sh"
    exit 1
fi

# Check if source directory exists
if [ ! -d "$SOURCE" ]; then
    echo "❌ Source directory not found: $SOURCE"
    exit 1
fi

echo "Source: $SOURCE"
echo "Destination: $DEST"
echo ""
echo "Files to copy:"
ls -1 "$SOURCE"
echo ""

read -p "Continue? (y/n): " -n 1 -r
echo ""

if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Cancelled."
    exit 0
fi

echo ""
echo "Copying files..."
rclone copy "$SOURCE" "$DEST" -P

if [ $? -eq 0 ]; then
    echo ""
    echo "✅ Copy complete!"
    echo ""
    echo "Verify on VPS:"
    echo "  rclone ls vps:/CG_NT8_AutoDeploy"
    echo ""
    echo "Files copied:"
    rclone ls "$DEST"
else
    echo ""
    echo "❌ Copy failed!"
    exit 1
fi
