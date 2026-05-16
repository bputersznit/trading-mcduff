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

File is automatically available in Windows VM via shared folder:

```
\\VBOXSVR\CG_MNQ_MarketReplayLab\ninjascript\[new-version].cs
```

**Windows VM Steps:**
1. Open shared folder in Windows Explorer
2. Copy file to: `C:\Users\[YourUser]\Documents\NinjaTrader 8\bin\Custom\Strategies\`
3. Open NinjaTrader 8
4. Compile: Tools → Compile (F5)
5. Attach to chart (ensure correct series requirements are met)

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
- Commit: 1e37bec (initial), 4cb8d11 (SOP update)
- Location: master branch
- VirtualBox: Available at \\VBOXSVR\CG_MNQ_MarketReplayLab\ninjascript\

**Active Utilities**:
- CG_L2_Capture_Chunked.cs
- CGL2CaptureChunked.cs
- CG_L2_Quality_v1_0.cs

## Troubleshooting

### Commit not visible on GitHub
- Check that you're viewing **master** branch, not main
- Post-commit hook pushes to master automatically

### VPS sync warnings
- "Failed: [Errno 2] No such file" warnings after deleting files are expected
- The important part (GitHub push) still succeeds

### VirtualBox shared folder not accessible
- Verify VM is running
- Check shared folder is configured: `VBoxManage showvminfo [VM-Name] | grep -i shared`
- Mount in Windows: `net use * \\VBOXSVR\CG_MNQ_MarketReplayLab`

## Notes

- Never commit directly to VPS
- Git history preserves all deleted versions
- Always test compile in NinjaTrader after deployment
- Verify series requirements (e.g., v2.7 requires MNQ 1 Tick primary series)
- Use `mv` (not `cp`) from Downloads to avoid file duplication
- Working directory path: `/home/bernard/trading4/CG_MNQ_MarketReplayLab/ninjascript/`
