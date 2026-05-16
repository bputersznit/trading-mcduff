#!/bin/bash
# Automated Strategy Deployment System
# Watches Downloads for new CG_OrderFlow_Aggression .cs files
# Auto-deploys to git, GitHub, and Windows VM

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
DOWNLOADS_DIR="$HOME/Downloads"
NINJASCRIPT_DIR="$PROJECT_DIR/ninjascript"
LOG_FILE="$PROJECT_DIR/logs/auto_deploy.log"

# Create logs directory if needed
mkdir -p "$PROJECT_DIR/logs"

log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] $1" | tee -a "$LOG_FILE"
}

log "=== Auto-deploy check started ==="

# Find newest .cs file in Downloads
NEWEST_DL=$(find "$DOWNLOADS_DIR" -maxdepth 1 -name "*.cs" -type f -printf '%T@ %p\n' 2>/dev/null | sort -rn | head -1 | cut -d' ' -f2-)

if [ -z "$NEWEST_DL" ]; then
    log "No .cs files found in Downloads"
    exit 0
fi

NEWEST_DL_NAME=$(basename "$NEWEST_DL")
log "Found in Downloads: $NEWEST_DL_NAME"

# Find current version with same base name in ninjascript
BASE_NAME="${NEWEST_DL_NAME%.*}"  # Remove .cs extension
CURRENT_VERSION=$(find "$NINJASCRIPT_DIR" -maxdepth 1 -name "${BASE_NAME}.cs" -type f | head -1)

if [ -z "$CURRENT_VERSION" ]; then
    log "No current version in ninjascript directory"
    DEPLOY_NEW=true
else
    CURRENT_NAME=$(basename "$CURRENT_VERSION")
    log "Current version: $CURRENT_NAME"

    # Compare timestamps
    DL_TIME=$(stat -c %Y "$NEWEST_DL")
    CURRENT_TIME=$(stat -c %Y "$CURRENT_VERSION")

    if [ "$DL_TIME" -le "$CURRENT_TIME" ]; then
        log "Downloads version is not newer - skipping"
        exit 0
    fi

    # Check if files are identical
    if cmp -s "$NEWEST_DL" "$CURRENT_VERSION"; then
        log "Files are identical - skipping"
        exit 0
    fi

    DEPLOY_NEW=true
    OLD_VERSION="$CURRENT_VERSION"
fi

if [ "$DEPLOY_NEW" = true ]; then
    log "=== Starting deployment of $NEWEST_DL_NAME ==="

    cd "$PROJECT_DIR"

    # Step 1: Move new version to ninjascript
    log "Step 1: Moving to ninjascript directory"
    mv "$NEWEST_DL" "$NINJASCRIPT_DIR/"

    # Step 2: Delete ALL old OrderFlow versions except the new one (per SOP)
    if echo "$NEWEST_DL_NAME" | grep -q "CG_OrderFlow_Aggression"; then
        log "Step 2: Removing old OrderFlow versions (keep only latest)"
        find "$NINJASCRIPT_DIR" -maxdepth 1 -name "CG_OrderFlow_Aggression_v*.cs" ! -name "$NEWEST_DL_NAME" -type f | while read old_file; do
            log "  Removing: $(basename "$old_file")"
            rm "$old_file"
        done
    elif [ -n "$OLD_VERSION" ]; then
        log "Step 2: Removing old version: $(basename "$OLD_VERSION")"
        rm "$OLD_VERSION"
    fi

    # Extract version info from filename (if present)
    VERSION=$(echo "$NEWEST_DL_NAME" | sed -n 's/.*_\(v[0-9_]*\)_.*/\1/p')
    FEATURE=$(echo "$NEWEST_DL_NAME" | sed -n 's/.*_v[0-9_]*_\(.*\)\.cs/\1/p')

    # Build commit message
    if [ -n "$VERSION" ]; then
        COMMIT_TITLE="Auto-deploy: $NEWEST_DL_NAME ($VERSION)"
    else
        COMMIT_TITLE="Auto-deploy: $NEWEST_DL_NAME"
    fi

    # Step 3: Git commit
    log "Step 3: Git commit"
    git add ninjascript/
    git commit -m "$COMMIT_TITLE

File: $NEWEST_DL_NAME
$(if [ -n "$FEATURE" ]; then echo "Feature: $FEATURE"; fi)
Automated deployment from Downloads directory

$(if [ -n "$OLD_VERSION" ]; then echo "Replaces: $(basename "$OLD_VERSION")"; fi)

Co-Authored-By: Claude Sonnet 4.5 <noreply@anthropic.com>" || {
        log "ERROR: Git commit failed"
        exit 1
    }

    log "Step 4: Push to GitHub (automatic via post-commit hook)"
    # Post-commit hook handles the push

    # Step 5: Deploy to Windows VM via PowerShell
    log "Step 5: Deploying to Windows VM"

    # Execute via guest control
    if VBoxManage list runningvms | grep -q "win"; then
        # Create PowerShell deployment script in shared folder
        cat > "$PROJECT_DIR/deploy_auto_temp.ps1" <<EOF
Copy-Item "\\\\VBOXSVR\\CG_MNQ_MarketReplayLab\\ninjascript\\$NEWEST_DL_NAME" -Destination "\\\$env:USERPROFILE\\Documents\\NinjaTrader 8\\bin\\Custom\\Strategies\\" -Force
Write-Host "Auto-deployment complete: $NEWEST_DL_NAME"
EOF

        # Execute the script via guest control
        VM_DEPLOY_EXIT=0
        VBoxManage guestcontrol "win" run \
          --exe "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" \
          --username "Administrator" \
          --password "13!XenoZendo" \
          --no-wait-stdout --no-wait-stderr \
          -- -ExecutionPolicy Bypass -File "\\\\VBOXSVR\\CG_MNQ_MarketReplayLab\\deploy_auto_temp.ps1" 2>&1 || VM_DEPLOY_EXIT=$?

        if [ $VM_DEPLOY_EXIT -eq 0 ]; then
            log "✅ Deployed to Windows VM successfully (exit code 0)"
        else
            log "⚠️  Windows VM deployment failed (exit code $VM_DEPLOY_EXIT)"
        fi

        rm -f "$PROJECT_DIR/deploy_auto_temp.ps1"
    else
        log "⚠️  Windows VM not running - skipping VM deployment"
    fi

    if [ -n "$VERSION" ]; then
        log "=== Deployment complete: $VERSION $(if [ -n "$FEATURE" ]; then echo "($FEATURE)"; fi) ==="
    else
        log "=== Deployment complete: $NEWEST_DL_NAME ==="
    fi
    log "Next step: Compile in NinjaTrader (F5)"
fi

log "=== Auto-deploy check finished ==="
