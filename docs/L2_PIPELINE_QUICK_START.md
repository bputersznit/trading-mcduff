# L2 Automated Pipeline - Quick Start Guide

## Overview
Automated pipeline for 2-month Market Replay run:
- Captures L2 depth in NinjaTrader → CSV chunks
- Auto-converts CSV → Parquet (10x compression)
- Deletes CSVs immediately
- Transfers Parquet to Ubuntu
- Imports to ClickHouse
- Keeps only Parquet on Ubuntu, nothing on VPS

## Files Created

### Ubuntu Scripts:
```
scripts/vps_l2_watcher.py       - VPS watcher (copy to Windows)
scripts/ubuntu_l2_importer.py   - Ubuntu importer
scripts/start_pipeline.sh       - Quick-start helper
```

### Documentation:
```
docs/L2_AUTOMATED_PIPELINE_SETUP.md  - Full setup guide
docs/L2_PIPELINE_QUICK_START.md      - This file
```

### NinjaScript:
```
ninjascript/CGL2CaptureChunked.cs   - Already on VPS
```

## Step-by-Step Setup (10 minutes)

### 1. VPS (Windows) - Copy Watcher Script

**Method A - Manual Copy:**
1. Open this file on VPS: `scripts/vps_l2_watcher.py`
2. Copy to: `C:\Users\Administrator\vps_l2_watcher.py`

**Method B - Via network share:**
```bash
# From Ubuntu:
scp scripts/vps_l2_watcher.py administrator@VPS_IP:C:/Users/Administrator/
```

### 2. VPS (Windows) - Configure Rclone

**On Windows VPS, open PowerShell or CMD:**

```bash
# Install rclone if not installed
# Download from: https://rclone.org/downloads/

# Configure remote to Ubuntu
rclone config

# Settings:
#   Name: ubuntu
#   Type: sftp
#   Host: <Ubuntu IP or hostname>
#   User: bernard
#   Port: 22
#   Key file: <path to SSH key>
#   OR Password: <your password>

# Test connection:
rclone lsd ubuntu:/home/bernard/
```

**Edit watcher config:**
Open `vps_l2_watcher.py` and verify:
```python
RCLONE_REMOTE = "ubuntu"  # Match your rclone remote name
RCLONE_DEST = f"{RCLONE_REMOTE}:/home/bernard/trading4/CG_MNQ_MarketReplayLab/data/l2_parquet/"
```

### 3. Ubuntu - Start Importer

```bash
cd ~/trading4/CG_MNQ_MarketReplayLab

# Quick start (creates directories, starts importer)
./scripts/start_pipeline.sh

# OR manually:
mkdir -p data/l2_parquet logs
python3 scripts/ubuntu_l2_importer.py &
```

**Monitor:**
```bash
tail -f scripts/l2_importer.log
```

### 4. VPS (Windows) - Start Watcher

```bash
cd C:\Users\Administrator
python vps_l2_watcher.py
```

**Monitor:**
```bash
# In PowerShell:
Get-Content -Wait -Tail 20 l2_watcher.log
```

### 5. NinjaTrader - Start Capture

1. **Recompile NinjaScript** (F5) - to pick up 20-level depth
2. Open chart with MNQ
3. Apply **CG_L2_Capture_Chunked** strategy
4. Verify settings:
   - Max Depth Levels: **20**
   - Max Rows Per Chunk: **50000**
5. Start Market Replay
6. Watch Output window for "Opened chunk" messages

## Verification (2 minutes after start)

**Ubuntu:**
```bash
# Check Parquet files arriving
ls -lh data/l2_parquet/*/

# Check ClickHouse import
clickhouse-client --query "
SELECT COUNT(*) as rows, MAX(timestamp) as latest
FROM l2_depth_raw"
```

**VPS:**
```bash
# CSV count should stay low (0-2 files)
dir "C:\Users\Administrator\Documents\CG_L2_Capture\*\*.csv" /s

# Parquet count should be 0 (transferred immediately)
dir "C:\Users\Administrator\Documents\CG_L2_Capture\*\*.parquet" /s
```

## Expected Behavior

**Normal operation:**
```
VPS:    CSV chunk closes → 30 sec later → Parquet created → CSV deleted
        → Transfer to Ubuntu → Parquet deleted from VPS

Ubuntu: Parquet arrives → 1 min later → Imported to ClickHouse
        → Parquet kept for analysis
```

**Timeline per chunk (50K rows):**
- Capture: ~2-3 minutes during RTH (real-time)
- Convert: 2-5 seconds
- Transfer: 5-15 seconds (depends on network)
- Import: 5-10 seconds
- **Total lag**: ~30-60 seconds behind live capture

## Monitoring Commands

**Check pipeline health:**
```bash
# Ubuntu - Check recent imports
clickhouse-client --query "
SELECT
    toStartOfMinute(MAX(timestamp)) as last_import,
    age(NOW(), MAX(timestamp)) as minutes_behind
FROM l2_depth_raw"

# Ubuntu - Check hourly volume
clickhouse-client --query "
SELECT
    toStartOfHour(timestamp) as hour,
    COUNT(*) as events,
    round(COUNT(*) / 3600, 0) as ops_per_sec
FROM l2_depth_raw
WHERE timestamp > NOW() - INTERVAL 24 HOUR
GROUP BY hour
ORDER BY hour DESC
LIMIT 10"
```

**Check disk usage:**
```bash
# Ubuntu
du -sh data/l2_parquet/

# VPS (PowerShell)
Get-ChildItem "C:\Users\Administrator\Documents\CG_L2_Capture" -Recurse | Measure-Object -Property Length -Sum
```

## Troubleshooting

### CSV files piling up on VPS
**Symptoms:** Disk space running out, CSV count > 10

**Fix:**
```bash
# Check watcher log
type l2_watcher.log | findstr ERROR

# Restart watcher
python vps_l2_watcher.py
```

### No Parquet files arriving on Ubuntu
**Symptoms:** Ubuntu importer has no files to import

**Check:**
1. VPS watcher running? `tasklist | findstr python`
2. Rclone working? `rclone lsd ubuntu:/home/bernard/`
3. Network connected?

**Fix:**
```bash
# On VPS, test rclone manually
rclone copy "C:\Users\Administrator\test.txt" "ubuntu:/home/bernard/"
```

### ClickHouse not importing
**Symptoms:** Parquet files accumulating in data/l2_parquet/

**Check:**
```bash
# Ubuntu - Check ClickHouse
clickhouse-client --query "SELECT 1"

# Check importer log
tail -50 scripts/l2_importer.log | grep ERROR
```

**Fix:**
```bash
# Restart importer
pkill -f ubuntu_l2_importer
python3 scripts/ubuntu_l2_importer.py &
```

## Stopping the Pipeline

**Graceful shutdown:**

1. **Stop NinjaTrader** strategy (or finish playback)
2. **Wait 5 minutes** for watcher to process remaining files
3. **VPS:** Ctrl+C on watcher window
4. **Ubuntu:** `pkill -f ubuntu_l2_importer`

**Verify complete:**
```bash
# No CSV or Parquet on VPS
dir "C:\Users\Administrator\Documents\CG_L2_Capture" /s

# All Parquet on Ubuntu
ls data/l2_parquet/*/

# All imported to ClickHouse
clickhouse-client --query "
SELECT COUNT(*) as total_rows, MAX(timestamp) as latest_event
FROM l2_depth_raw"
```

## Resume After Stop

Both scripts track processed files, so you can safely restart:

```bash
# Ubuntu
python3 scripts/ubuntu_l2_importer.py &

# VPS
python vps_l2_watcher.py
```

They'll skip already-processed files and continue from where they left off.

## Expected Results (2 Month Run)

**After completion:**
- **Ubuntu Parquet**: ~1.3 GB (portable, efficient)
- **ClickHouse DB**: ~2-3 GB (indexed, queryable)
- **VPS storage**: 0 MB (all cleaned up)
- **Total events**: ~327 million
- **Query speed**: ~10M rows/sec

**Analysis ready:**
```sql
-- Daily summaries
SELECT
    toDate(timestamp) as date,
    COUNT(*) / 1000000 as millions_events
FROM l2_depth_raw
GROUP BY date
ORDER BY date;
```

## Support

**Logs locations:**
- VPS: `C:\Users\Administrator\l2_watcher.log`
- Ubuntu: `~/trading4/CG_MNQ_MarketReplayLab/scripts/l2_importer.log`

**Processed file tracking:**
- VPS: `C:\Users\Administrator\processed_chunks.txt`
- Ubuntu: `~/trading4/CG_MNQ_MarketReplayLab/scripts/imported_files.txt`

## Tips for Long Runs

1. **Use screen/tmux** on Ubuntu for persistent sessions
2. **Monitor disk space** daily: `df -h`
3. **Check logs weekly** for errors
4. **Backup ClickHouse** after each week
5. **Keep VPS watcher visible** or use Windows Task Scheduler

Good luck with your 2-month run! 🚀
