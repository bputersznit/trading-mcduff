# L2 Automated Pipeline Setup - 2 Month Playback Run

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ Windows VPS                                                  │
│                                                              │
│  NinjaTrader → CSV chunks (50K rows each)                   │
│       ↓                                                      │
│  vps_l2_watcher.py (monitors L2_Capture folder)             │
│       ↓                                                      │
│  Convert CSV → Parquet (9-10x compression)                  │
│  Delete CSV immediately                                      │
│  Transfer Parquet → Ubuntu via rclone                       │
│  Delete Parquet from VPS                                     │
└─────────────────────────────────────────────────────────────┘
                        ↓
                   rclone sync
                        ↓
┌─────────────────────────────────────────────────────────────┐
│ Ubuntu Laptop                                                │
│                                                              │
│  data/l2_parquet/ (receives Parquet files)                  │
│       ↓                                                      │
│  ubuntu_l2_importer.py (monitors parquet folder)            │
│       ↓                                                      │
│  Import → ClickHouse                                         │
│  Keep Parquet for analysis                                   │
└─────────────────────────────────────────────────────────────┘
```

## Expected Data Volumes (2 Month Run)

### Assumptions:
- RTH: 6.5 hours/day × ~40 trading days = 260 hours
- Rate: ~350 ops/sec during RTH (based on Mar 2 analysis)
- Total events: 260h × 3600s × 350 ops/s = **327 million events**

### Storage:
```
CSV:      ~13 GB (if stored, which we won't)
Parquet:  ~1.3 GB (10x compression)
ClickHouse: ~2-3 GB (compressed + indexed)
```

## Setup Instructions

### 1. VPS (Windows) Setup

**Install Python dependencies:**
```bash
pip install pandas pyarrow
```

**Configure rclone remote to Ubuntu:**
```bash
rclone config
# Name: ubuntu (or whatever you prefer)
# Type: sftp (or smb if using network share)
# Host: <your Ubuntu IP or hostname>
# User: bernard
# Port: 22
# Key file: <path to SSH key>
```

**Test rclone connection:**
```bash
rclone lsd ubuntu:/home/bernard/
```

**Copy watcher script:**
```bash
# Script is at: scripts/vps_l2_watcher.py
# Copy to VPS and edit configuration if needed
```

**Update script configuration:**
Edit `vps_l2_watcher.py`:
```python
RCLONE_REMOTE = "ubuntu"  # Your rclone remote name
RCLONE_DEST = "ubuntu:/home/bernard/trading4/CG_MNQ_MarketReplayLab/data/l2_parquet/"
```

**Start watcher:**
```bash
python vps_l2_watcher.py
```

Or run as background service (Windows):
```bash
pythonw vps_l2_watcher.py
```

### 2. Ubuntu Setup

**Create data directory:**
```bash
mkdir -p ~/trading4/CG_MNQ_MarketReplayLab/data/l2_parquet
cd ~/trading4/CG_MNQ_MarketReplayLab
```

**Make importer executable:**
```bash
chmod +x scripts/ubuntu_l2_importer.py
```

**Start importer:**
```bash
python3 scripts/ubuntu_l2_importer.py
```

Or run as systemd service:
```bash
# Create service file: /etc/systemd/system/l2-importer.service
sudo systemctl enable l2-importer
sudo systemctl start l2-importer
```

### 3. NinjaTrader Configuration

**Strategy Settings:**
- Max Rows Per Chunk: 50,000 (default)
- Max Depth Levels: 20 (captures 20 positions)
- Instrument Symbol: MNQ

**Chart Setup:**
- Instrument: MNQ (current contract)
- Data Series: 1 Minute (or any interval)
- Enable strategy via Strategies panel

### 4. Start Playback

**Day 1:**
1. Start Ubuntu importer: `python3 scripts/ubuntu_l2_importer.py &`
2. Start VPS watcher: `python vps_l2_watcher.py` (on Windows)
3. Start NinjaTrader Market Replay
4. Enable CG_L2_Capture_Chunked strategy
5. Run playback for first day

**Monitor:**
- VPS: Check `l2_watcher.log` for processing status
- Ubuntu: Check `l2_importer.log` for import status
- ClickHouse: Query `SELECT COUNT(*) FROM l2_depth_raw`

## Monitoring Commands

**VPS (Windows):**
```bash
# Watch watcher log
tail -f l2_watcher.log

# Check CSV count (should stay low)
dir "C:\Users\Administrator\Documents\CG_L2_Capture\*\*.csv" | measure

# Check Parquet count (should be 0 after transfer)
dir "C:\Users\Administrator\Documents\CG_L2_Capture\*\*.parquet" | measure
```

**Ubuntu:**
```bash
# Watch importer log
tail -f scripts/l2_importer.log

# Check incoming Parquet files
ls -lh data/l2_parquet/*/

# Check ClickHouse stats
clickhouse-client --query "
SELECT
    COUNT(*) as rows,
    COUNT(DISTINCT toDate(timestamp)) as days,
    round(COUNT(*) / 1000000, 2) as millions,
    MIN(timestamp) as first_event,
    MAX(timestamp) as last_event
FROM l2_depth_raw
FORMAT Vertical"
```

## Troubleshooting

**VPS watcher not processing:**
- Check `l2_watcher.log` for errors
- Verify rclone remote: `rclone lsd ubuntu:`
- Check CSV files exist: `dir C:\Users\Administrator\Documents\CG_L2_Capture\`

**Ubuntu importer not importing:**
- Check `l2_importer.log` for errors
- Verify ClickHouse running: `clickhouse-client --query "SELECT 1"`
- Check Parquet files arriving: `ls data/l2_parquet/*/`

**CSV files accumulating on VPS:**
- VPS watcher may have stopped - restart it
- Check disk space: `wmic logicaldisk get size,freespace,caption`
- Manually run conversion if needed

**Disk space issues:**
If VPS fills up, manually clean:
```bash
# On VPS, convert and delete CSVs
python scripts/manual_csv_cleanup.py
```

## Performance Tuning

**VPS watcher interval:**
```python
CHECK_INTERVAL = 10  # Faster for more responsive processing
MIN_FILE_AGE = 5     # Lower if files complete quickly
```

**Ubuntu importer interval:**
```python
CHECK_INTERVAL = 30  # Slower is fine, less CPU usage
MIN_FILE_AGE = 10    # Higher to ensure transfer complete
```

**Chunk size:**
50K rows = ~2 MB CSV = ~200 KB Parquet
- Smaller chunks = more responsive, more files
- Larger chunks = fewer files, less overhead

## Expected Timeline

**Per Trading Day (6.5 RTH hours):**
- Events captured: ~8 million
- CSV if stored: ~320 MB (we delete immediately)
- Parquet transferred: ~33 MB
- Processing time: Real-time during playback

**Full 2-Month Run:**
- Total events: ~327 million
- Total Parquet: ~1.3 GB
- ClickHouse DB: ~2-3 GB
- Wall clock time: Depends on playback speed

## Safety Features

1. **CSV deleted only after Parquet verified**
2. **Parquet deleted only after transfer verified**
3. **Processed file tracking** - won't re-process if script restarts
4. **Logs retained** - full audit trail of all operations
5. **Error handling** - continues on failure, logs issues

## Post-Run Analysis

Once complete, you'll have:
- **327M L2 events in ClickHouse** - queryable at ~10M rows/sec
- **~1.3 GB Parquet files** - portable, efficient storage
- **Complete logs** - full processing history

Query examples:
```sql
-- Daily event volumes
SELECT
    toDate(timestamp) as date,
    COUNT(*) as events,
    round(COUNT(*) / 1000000, 2) as millions
FROM l2_depth_raw
GROUP BY date
ORDER BY date;

-- Order book churn by hour
SELECT
    toStartOfHour(timestamp) as hour,
    COUNT(*) as total_ops,
    round(COUNT(*) / 3600, 0) as ops_per_sec
FROM l2_depth_raw
GROUP BY hour
ORDER BY hour;
```
