#!/bin/bash
# Setup rclone remote for VPS C:\CG_NT8_AutoDeploy
#
# This creates an rclone remote called 'vps' that connects to
# the Windows VPS auto-deploy directory

echo "Setting up rclone remote for VPS..."
echo ""
echo "You'll need:"
echo "  - VPS IP: 104.245.107.193"
echo "  - Windows username"
echo "  - Windows password"
echo "  - Target path: C:\CG_NT8_AutoDeploy"
echo ""
read -p "Press Enter to continue..."

# Run rclone config interactively
cat << 'EOF'

=================================================================
RCLONE CONFIGURATION STEPS
=================================================================

When prompted, enter the following:

1. Choose: n (for new remote)
2. Name: vps
3. Storage type: smb
4. Host: 104.245.107.193
5. User: [your Windows username]
6. Port: [press Enter for default]
7. Password: y (yes, type in my own password)
   - Enter your Windows password
   - Confirm password
8. Domain: [press Enter to skip]
9. Advanced config: n (no)
10. Keep this remote: y (yes)
11. Quit config: q

=================================================================
EOF

read -p "Ready? Press Enter to start rclone config..."

rclone config

echo ""
echo "Testing connection..."
rclone lsd vps:/CG_NT8_AutoDeploy

if [ $? -eq 0 ]; then
    echo ""
    echo "✅ Success! VPS remote configured."
    echo ""
    echo "Test commands:"
    echo "  rclone ls vps:/CG_NT8_AutoDeploy"
    echo "  rclone copy [local-file] vps:/CG_NT8_AutoDeploy"
    echo ""
else
    echo ""
    echo "❌ Connection failed. Check credentials and try again."
    echo "   Run: rclone config"
fi
EOF
chmod +x /home/bernard/trading4/CG_MNQ_MarketReplayLab/scripts/setup_vps_rclone.sh
