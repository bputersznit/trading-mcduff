# VPS File Locations Reference

## NinjaTrader 8 Directories

### Strategies Folder (IMPORTANT!)
```
C:\Users\Administrator\Documents\NinjaTrader 8\bin\Custom\Strategies\
```
**All .cs NinjaScript files must be here for NT8 to compile them.**

Auto-sync now copies all `.cs` files here automatically on every commit.

### Other NT8 Folders
```
C:\Users\Administrator\Documents\NinjaTrader 8\bin\Custom\
├── Strategies\          ← Your .cs files go here
├── Indicators\
├── DrawingTools\
└── AddOns\
```

---

## Project Directories

### Working Directory
```
C:\home\Administrator\trading\CG_MNQ_MarketReplayLab\
├── ninjascript\         (Source .cs files)
├── scripts\            (Python scripts)
├── sql\                (SQL files)
└── docs\               (Documentation)
```

### Git Repository
```
C:\home\Administrator\git\CG_MNQ_MarketReplayLab.git\
```
Bare repository for version control.

---

## Trading Data Directories

### Signal File
```
C:\Trading\Signals\mnq_signals.csv
```
Updated every second by signal generator.

### Log Files
```
C:\Trading\Logs\
├── daily_pnl.csv        (Daily P&L summary)
└── trade_log.csv        (Trade-by-trade details)
```

---

## Auto-Sync Behavior

When you commit locally, files automatically sync to:

1. **All files** → `C:\home\Administrator\trading\CG_MNQ_MarketReplayLab\`
2. **All .cs files** → `C:\Users\Administrator\Documents\NinjaTrader 8\bin\Custom\Strategies\`

**Result**: Strategies ready to compile in NT8 immediately after commit!

---

## Quick Commands

**Copy .cs file manually (if needed):**
```python
python3 << 'EOF'
import paramiko
ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect("104.245.107.193", username="Administrator", password="ExpressionDriving13_")
sftp = ssh.open_sftp()
sftp.put("ninjascript/YourStrategy.cs",
         "C:/Users/Administrator/Documents/NinjaTrader 8/bin/Custom/Strategies/YourStrategy.cs")
sftp.close()
ssh.close()
EOF
```

**Verify files on VPS:**
```python
python3 << 'EOF'
import paramiko
ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect("104.245.107.193", username="Administrator", password="ExpressionDriving13_")
stdin, stdout, stderr = ssh.exec_command('dir "C:\\Users\\Administrator\\Documents\\NinjaTrader 8\\bin\\Custom\\Strategies\\CG*.cs"')
print(stdout.read().decode('utf-8'))
ssh.close()
EOF
```

---

## Important Notes

- ✅ Auto-sync handles everything automatically
- ✅ .cs files go to BOTH locations (working dir + NT8 folder)
- ✅ After sync, just open NT8 and press F5 to compile
- ✅ No manual copying needed!

---

*Updated: 2024-04-22*
*Auto-sync: ENABLED*
