# Strategy Deployment Standard Operating Procedure (SOP)

## Version Control Policy

**Keep only current development version in working directory. Delete old versions. Git history preserves all previous versions.**

- Working directory: Only current strategy + current utilities
- Git repository: Full version history preserved
- GitHub: Master branch contains all commits

## Deployment Workflow

### 1. Check Downloads for New Version

```bash
ls -lt ~/Downloads | head -20
```

Identify newest strategy file by timestamp. Common pattern:
- `CG_OrderFlow_Aggression_v[X]_[Y]_[FEATURE_NAME].cs`

### 2. Verify File is New/Different

Compare with current version in ninjascript directory:

```bash
diff ~/Downloads/[new-file].cs ninjascript/[current-file].cs
```

If identical, no deployment needed. If different, proceed to step 3.

### 3. Move New Version to ninjascript Directory

**Move** (not copy) from Downloads to working directory:

```bash
mv ~/Downloads/[new-file].cs /home/bernard/trading4/CG_MNQ_MarketReplayLab/ninjascript/
```

This clears the file from Downloads and places it in the git-tracked working directory.

Or create directly in ninjascript directory if developing from scratch.

### 4. Delete Old Version from Working Directory

```bash
rm ninjascript/[old-version].cs
```

**Important**: Only delete the old strategy version. Keep all utilities:
- `CG_L2_Capture_Chunked.cs`
- `CGL2CaptureChunked.cs`
- `CG_L2_Quality_v1_0.cs`

### 5. Git Commit with Detailed Message

```bash
git add ninjascript/[new-version].cs
git add ninjascript/[old-version].cs  # This stages the deletion
git commit -m "$(cat <<'EOF'
[Action]: [Version] - [Feature Summary]

Key Changes:
- [Change 1]
- [Change 2]
- [Change 3]

Parameters:
- [Param 1]: [Value] ([Description])
- [Param 2]: [Value] ([Description])

Deployment:
- Replaces: [old-version]
- Available in VirtualBox: \\VBOXSVR\CG_MNQ_MarketReplayLab\ninjascript\

Co-Authored-By: Claude Sonnet 4.5 <noreply@anthropic.com>
EOF
)"
```

### 6. Push to GitHub

Push happens automatically via post-commit hook to master branch.

Verify on GitHub:
- Navigate to https://github.com/[your-repo]
- Switch to **master** branch (not main)
- Confirm commit appears

### 7. VirtualBox Deployment

File is automatically available in Windows VM via shared folder at:
```
\\VBOXSVR\CG_MNQ_MarketReplayLab\ninjascript\[new-version].cs
```

#### Option A: Automated Remote Deployment (Recommended)

Use VBoxManage guest control to deploy remotely from Linux:

```bash
# Deploy strategy file to NinjaTrader
VBoxManage guestcontrol "win" run \
  --exe "C:\\Windows\\System32\\cmd.exe" \
  --username "Administrator" \
  --password "unlucky-strange" \
  --no-wait-stdout --no-wait-stderr \
  -- /c "copy /Y \\\\VBOXSVR\\CG_MNQ_MarketReplayLab\\ninjascript\\[new-file].cs \"C:\\Users\\Administrator\\Documents\\NinjaTrader 8\\bin\\Custom\\Strategies\\\""
```

**Requirements:**
- Windows VM must be running
- User must be logged in to Windows
- Guest control authentication configured (run `Fix_GuestControl_Auth.ps1` once)

**Then compile in NinjaTrader:**
- Open NinjaTrader 8
- Press F5 (or Tools → Compile)
- Strategy appears in Strategy list

#### Option B: Manual Deployment

1. In Windows VM, open File Explorer
2. Navigate to: `\\VBOXSVR\CG_MNQ_MarketReplayLab\ninjascript\`
3. Copy file to: `C:\Users\Administrator\Documents\NinjaTrader 8\bin\Custom\Strategies\`
4. Open NinjaTrader 8
5. Press F5 to compile
6. Attach to chart (ensure correct series requirements are met)

## Version Naming Convention

Format: `CG_OrderFlow_Aggression_v[MAJOR]_[MINOR]_[FEATURE_TAG].cs`

Examples:
- v2.1_OCO_ORFIX
- v2.2_PERSISTENCE_AUCTION
- v2.3_UNBLOCKED
- v2.7_STAGE2_RESPONSE_MTF

## Current State

**Active Strategy**: CG_OrderFlow_Aggression_v2_7_STAGE2_RESPONSE_MTF.cs
- Deployed: 2026-05-15
- Commit: 1e37bec (initial), bd16279 (guest control), ffa8528 (cleanup)
- Location: master branch
- VirtualBox: Available at \\VBOXSVR\CG_MNQ_MarketReplayLab\ninjascript\
- Guest Control: ✅ Configured and working

**Active Utilities**:
- CG_L2_Capture_Chunked.cs
- CGL2CaptureChunked.cs
- CG_L2_Quality_v1_0.cs

**Setup Scripts**:
- Fix_GuestControl_Auth.ps1 (Windows - enables remote deployment)
- DEPLOY_TO_NINJATRADER.bat (Windows - manual deployment helper)

## Guest Control Setup (One-Time)

To enable automated remote deployment, configure guest control authentication:

**In Windows VM (as Administrator):**
1. Navigate to: `\\VBOXSVR\CG_MNQ_MarketReplayLab\`
2. Right-click `Fix_GuestControl_Auth.ps1` → Run with PowerShell
3. Script will configure Windows Server security policies
4. Keep Windows session logged in

**Test from Linux:**
```bash
VBoxManage guestcontrol "win" run \
  --exe "C:\\Windows\\System32\\cmd.exe" \
  --username "Administrator" \
  --password "unlucky-strange" \
  --no-wait-stdout --no-wait-stderr \
  -- /c "echo test > C:\\guestcontrol_test.txt"
```

If successful (exit code 0), guest control is working.

**What the script fixes:**
- Enables LocalAccountTokenFilterPolicy (remote admin access)
- Configures network logon authentication policies
- Verifies VBoxService is running
- Enables Administrator account

**Limitations:**
- `VERR_NOT_IMPLEMENTED` warnings for stdout/stderr are normal (VirtualBox limitation)
- PowerShell execution may fail; use cmd.exe instead
- Windows must be logged in for guest control to work

## Troubleshooting

### Commit not visible on GitHub
- Check that you're viewing **master** branch, not main
- Post-commit hook pushes to master automatically

### VPS sync warnings
- "Failed: [Errno 2] No such file" warnings after deleting files are expected
- The important part (GitHub push) still succeeds

### VirtualBox shared folder not accessible
- Verify VM is running
- Check shared folder is configured: `VBoxManage showvminfo "win" | grep -i shared`
- Mount in Windows: `net use * \\VBOXSVR\CG_MNQ_MarketReplayLab`

### Guest control authentication fails
- Run `Fix_GuestControl_Auth.ps1` in Windows VM
- Ensure Windows user is logged in (not just VM running)
- Verify password is correct: `unlucky-strange`
- Check VBoxService status: `Get-Service VBoxService` in Windows PowerShell
- Use `--no-wait-stdout --no-wait-stderr` flags to avoid VERR_NOT_IMPLEMENTED errors

### KVM/VirtualBox conflict
If VM won't start with "VERR_SVM_IN_USE" error:
```bash
sudo rmmod kvm_amd kvm
VBoxManage startvm "win" --type headless
```

## Notes

- Never commit directly to VPS
- Git history preserves all deleted versions
- Always test compile in NinjaTrader after deployment
- Verify series requirements (e.g., v2.7 requires MNQ 1 Tick primary series)
- Use `mv` (not `cp`) from Downloads to avoid file duplication
- Working directory path: `/home/bernard/trading4/CG_MNQ_MarketReplayLab/ninjascript/`
