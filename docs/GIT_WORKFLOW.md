# GIT WORKFLOW - AUTO-SYNC TO VPS

## ✅ AUTO-SYNC ENABLED

Every time you commit, your code is automatically:
1. ✅ Committed to local git
2. ✅ Synced to VPS working directory
3. ✅ NT8 strategy copied to Strategies folder

**No manual deployment needed!**

---

## HOW TO USE

### Making Changes

```bash
# 1. Edit your files normally
vim scripts/CGCl_backtest_scalping.py
vim ninjascript/CGScalpingStrategy.cs

# 2. Stage your changes
git add scripts/CGCl_backtest_scalping.py
git add ninjascript/CGScalpingStrategy.cs

# 3. Commit (auto-sync triggers automatically)
git commit -m "Update strategy parameters"
```

**What happens automatically:**
```
📝 Git commit created
↓
🔄 Post-commit hook runs
↓
📤 Files sync to VPS: C:/home/Administrator/trading/CG_MNQ_MarketReplayLab/
↓
📋 NT8 strategy copied to: C:/Users/Administrator/Documents/NinjaTrader 8/bin/Custom/Strategies/
↓
✅ Done!
```

---

## EXAMPLE WORKFLOW

### Scenario: You update the strategy parameters

```bash
# Edit the file
vim ninjascript/CGScalpingStrategy.cs

# Check what changed
git diff

# Stage and commit
git add ninjascript/CGScalpingStrategy.cs
git commit -m "Increase ABSORPTION target to 7pts"
```

**Output you'll see:**
```
================================================================================
POST-COMMIT: Pushing to VPS and syncing files...
================================================================================

📤 Pushing to VPS git repository...

📤 Syncing files to VPS...
  ✅ NT8 strategy updated in Strategies folder

✅ Synced 57 files to VPS
📍 Location: C:/home/Administrator/trading/CG_MNQ_MarketReplayLab

================================================================================
✅ POST-COMMIT COMPLETE: Files synced to VPS successfully!
================================================================================
```

---

## VPS FILE LOCATIONS

After every commit, files are automatically at:

| Location | Purpose |
|----------|---------|
| `C:\home\Administrator\trading\CG_MNQ_MarketReplayLab\` | Main working directory |
| `C:\Users\Administrator\Documents\NinjaTrader 8\bin\Custom\Strategies\CGScalpingStrategy.cs` | NT8 strategy (ready to compile) |
| `C:\home\Administrator\git\CG_MNQ_MarketReplayLab.git\` | Bare git repository |

---

## AFTER SYNC ON VPS

### If you updated the NT8 strategy:

1. Open NinjaTrader 8 on VPS
2. Go to: **Tools → Edit NinjaScript → Strategy**
3. Click **Compile** (or F5)
4. Strategy is updated!

### If you updated Python scripts:

1. SSH to VPS or use Remote Desktop
2. Scripts are already at: `C:\home\Administrator\trading\CG_MNQ_MarketReplayLab\scripts\`
3. Just run them (they're already updated)

---

## WHAT GETS SYNCED

**Always synced:**
- ✅ All Python scripts (`scripts/`)
- ✅ All SQL files (`sql/`)
- ✅ All documentation (`docs/`)
- ✅ NinjaScript strategy (`ninjascript/`)

**Never synced:**
- ❌ `.git/` directory
- ❌ `__pycache__/` directories
- ❌ `.pyc` files
- ❌ Hidden files (`.gitignore`, etc.)

---

## COMMIT MESSAGE FORMAT

Use clear, descriptive commit messages:

**Good examples:**
```bash
git commit -m "Update ABSORPTION target from 6pt to 7pt"
git commit -m "Add emergency stop loss to signal generator"
git commit -m "Fix connection timeout in NT8 strategy"
```

**Bad examples:**
```bash
git commit -m "changes"
git commit -m "update"
git commit -m "fix stuff"
```

---

## TROUBLESHOOTING

### Problem: Auto-sync not working

**Check:**
```bash
# 1. Is hook executable?
ls -l .git/hooks/post-commit
# Should show: -rwxr-xr-x (executable)

# 2. Test hook manually
.git/hooks/post-commit

# 3. Check hook file exists
cat .git/hooks/post-commit
```

### Problem: VPS connection fails

**Check:**
```bash
# Test SSH connection
ssh Administrator@104.245.107.193

# Or test with Python
python3 << 'EOF'
import paramiko
ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect("104.245.107.193", username="Administrator", password="ExpressionDriving13_")
print("✅ Connected!")
ssh.close()
EOF
```

### Problem: NT8 strategy not updating

**Check on VPS:**
```powershell
# Check file timestamp
Get-Item "C:\Users\Administrator\Documents\NinjaTrader 8\bin\Custom\Strategies\CGScalpingStrategy.cs" | Select-Object LastWriteTime

# Should show recent timestamp after your commit
```

---

## MANUAL SYNC (If Needed)

If auto-sync fails, you can manually sync:

```bash
# Run the hook script manually
.git/hooks/post-commit
```

Or copy files directly:

```bash
# Using Python script
python3 << 'PYEOF'
import paramiko
import os

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect("104.245.107.193", username="Administrator", password="ExpressionDriving13_")

sftp = ssh.open_sftp()
sftp.put("ninjascript/CGScalpingStrategy.cs",
         "C:/Users/Administrator/Documents/NinjaTrader 8/bin/Custom/Strategies/CGScalpingStrategy.cs")
sftp.close()
ssh.close()
print("✅ Manually synced!")
PYEOF
```

---

## DISABLE AUTO-SYNC

If you want to disable auto-sync temporarily:

```bash
# Rename the hook
mv .git/hooks/post-commit .git/hooks/post-commit.disabled

# Re-enable later
mv .git/hooks/post-commit.disabled .git/hooks/post-commit
```

---

## BEST PRACTICES

### 1. Commit Often
```bash
# Good: Small, focused commits
git commit -m "Update ABSORPTION stop from 3pt to 2.5pt"
git commit -m "Add logging to signal generator"

# Bad: One huge commit
git commit -m "Changed everything"
```

### 2. Test Before Committing

```bash
# Run backtest locally first
python3 scripts/CGCl_backtest_scalping.py

# If good, commit
git add scripts/CGCl_backtest_scalping.py
git commit -m "Optimize scalping parameters based on backtest"
```

### 3. Check VPS After Important Changes

After committing major changes:
1. RDP to VPS
2. Verify files updated
3. Test NT8 compilation
4. Run signal generator test

---

## SUMMARY

**You just:**
```bash
git commit -m "Your changes"
```

**And automatically:**
- ✅ Code committed locally
- ✅ Files synced to VPS
- ✅ NT8 strategy updated
- ✅ Ready to use on VPS

**That's it! No manual deployment needed.**

---

*Hook location: `.git/hooks/post-commit`*
*VPS: Administrator@104.245.107.193*
*Auto-sync status: ✅ ENABLED*
