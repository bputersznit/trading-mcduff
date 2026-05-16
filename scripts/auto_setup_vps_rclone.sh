#!/bin/bash
# Auto-configure rclone VPS remote (non-interactive)

echo "═══════════════════════════════════════════════════════"
echo "  Automatic VPS Rclone Configuration"
echo "═══════════════════════════════════════════════════════"
echo ""

# Get credentials
read -p "Windows username [Administrator]: " USERNAME
USERNAME=${USERNAME:-Administrator}

read -sp "Windows password: " PASSWORD
echo ""

if [ -z "$PASSWORD" ]; then
    echo "❌ Password cannot be empty"
    exit 1
fi

echo ""
echo "Configuring rclone remote 'vps'..."

# Create rclone config entry using obscured password
OBSCURED_PASSWORD=$(echo -n "$PASSWORD" | rclone obscure -)

# Check if vps remote already exists
if rclone listremotes | grep -q "vps:"; then
    echo "⚠️  VPS remote already exists. Removing old config..."
    rclone config delete vps 2>/dev/null || true
fi

# Create config directory if it doesn't exist
mkdir -p ~/.config/rclone

# Add VPS remote to config
cat >> ~/.config/rclone/rclone.conf << EOF

[vps]
type = smb
host = 104.245.107.193
user = ${USERNAME}
pass = ${OBSCURED_PASSWORD}
EOF

echo ""
echo "Testing connection..."
if rclone lsd vps:/CG_NT8_AutoDeploy 2>/dev/null; then
    echo ""
    echo "✅ Success! VPS remote configured."
    echo ""
    echo "Test it:"
    echo "  rclone ls vps:/CG_NT8_AutoDeploy"
    echo ""
    echo "Deploy files:"
    echo "  rclone copy [local-file] vps:/CG_NT8_AutoDeploy -P"
    echo ""
else
    echo ""
    echo "❌ Connection failed!"
    echo ""
    echo "Possible issues:"
    echo "  - Wrong username/password"
    echo "  - VPS not running"
    echo "  - C:\\CG_NT8_AutoDeploy folder doesn't exist"
    echo "  - Folder not shared on Windows"
    echo ""
    echo "To retry: ./auto_setup_vps_rclone.sh"
    echo "To configure manually: rclone config"
    exit 1
fi
