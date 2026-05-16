#!/bin/bash
# Deploy NinjaTrader strategies to VPS using rclone
# Target: C:\CG_NT8_AutoDeploy on VPS

NINJASCRIPT_DIR="/home/bernard/trading4/CG_MNQ_MarketReplayLab/ninjascript"
VPS_REMOTE="vps:/CG_NT8_AutoDeploy"

# Colors for output
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo "═══════════════════════════════════════════════════════════"
echo "  NinjaTrader Strategy Deployment to VPS"
echo "═══════════════════════════════════════════════════════════"
echo ""

# Check if rclone remote exists
if ! rclone listremotes | grep -q "vps:"; then
    echo -e "${RED}❌ VPS remote not configured!${NC}"
    echo ""
    echo "Run this first:"
    echo "  ./setup_vps_rclone.sh"
    echo ""
    echo "Or manually configure:"
    echo "  rclone config"
    exit 1
fi

# If specific file provided as argument
if [ -n "$1" ]; then
    FILE="$1"

    # Check if file exists
    if [ ! -f "$FILE" ]; then
        echo -e "${RED}❌ File not found: $FILE${NC}"
        exit 1
    fi

    FILENAME=$(basename "$FILE")

    echo -e "${YELLOW}Deploying: $FILENAME${NC}"
    echo ""

    rclone copy "$FILE" "$VPS_REMOTE" -P

    if [ $? -eq 0 ]; then
        echo ""
        echo -e "${GREEN}✅ Deployed successfully!${NC}"
        echo ""
        echo "Next steps:"
        echo "  1. RDP to VPS (104.245.107.193)"
        echo "  2. Check C:\CG_NT8_AutoDeploy\$FILENAME"
        echo "  3. Copy to NinjaTrader Strategies folder if needed"
        echo "  4. NinjaTrader → Tools → Compile"
    else
        echo ""
        echo -e "${RED}❌ Deployment failed!${NC}"
        exit 1
    fi
else
    # Deploy all Flagship Hybrid versions
    echo "Available strategies:"
    echo ""

    ls -1 "$NINJASCRIPT_DIR"/CG_MNQ_Flagship_Hybrid_v*.cs 2>/dev/null | while read -r file; do
        echo "  $(basename "$file")"
    done

    echo ""
    read -p "Deploy all Flagship versions? (y/n): " -n 1 -r
    echo ""

    if [[ $REPLY =~ ^[Yy]$ ]]; then
        echo ""
        rclone copy "$NINJASCRIPT_DIR" "$VPS_REMOTE" \
            --include "CG_MNQ_Flagship_Hybrid_v*.cs" \
            -P

        if [ $? -eq 0 ]; then
            echo ""
            echo -e "${GREEN}✅ All strategies deployed!${NC}"
            echo ""
            echo "Next steps:"
            echo "  1. RDP to VPS"
            echo "  2. Check C:\CG_NT8_AutoDeploy\"
            echo "  3. NinjaTrader → Tools → Compile"
        else
            echo ""
            echo -e "${RED}❌ Deployment failed!${NC}"
            exit 1
        fi
    fi
fi

echo ""
echo "═══════════════════════════════════════════════════════════"
