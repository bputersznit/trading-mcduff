# DEBUGGING: No Trades Executing

## Quick Diagnostic Checklist

### 1. Is the Signal Generator Running?

**Check on VPS:**
```powershell
# Check if Python process is running
Get-Process python*

# Should see: python.exe running CGCl_nt8_signal_generator.py
```

**If NOT running:**
```powershell
cd C:\home\Administrator\trading\CG_MNQ_MarketReplayLab\scripts
python CGCl_nt8_signal_generator.py
```

---

### 2. Is the Signal File Being Created?

**Check if file exists:**
```powershell
dir C:\Trading\Signals\mnq_signals.csv
```

**If file doesn't exist:**
```powershell
# Create directory first
mkdir C:\Trading\Signals

# Then start signal generator
cd C:\home\Administrator\trading\CG_MNQ_MarketReplayLab\scripts
python CGCl_nt8_signal_generator.py
```

**Check if file is being updated:**
```powershell
# View file content and timestamp
Get-Item C:\Trading\Signals\mnq_signals.csv | Select-Object LastWriteTime
type C:\Trading\Signals\mnq_signals.csv
```

**Should see:**
- File timestamp updating every few seconds
- Content like: `2024-01-15 09:30:45,MNQ,ABSORPTION,LONG,17450.25`

---

### 3. Is ClickHouse Running?

**The signal generator needs ClickHouse with order flow data.**

**Check:**
```bash
# On Linux (where ClickHouse is)
clickhouse-client --query "SELECT COUNT(*) FROM mnq_orderflow_1sec WHERE timestamp_1sec >= now() - INTERVAL 1 HOUR"

# Should return: > 0 rows
```

**If 0 rows:**
- ClickHouse doesn't have recent data
- Need to ensure market data is being ingested

---

### 4. Check NT8 Strategy Settings

**In NinjaTrader 8:**

1. Right-click chart → Strategies
2. Select: CGScalpingStrategyLive
3. **Verify Signal File Path:**
   ```
   Signal File Path: C:\Trading\Signals\mnq_signals.csv
   ```
   **Must match EXACTLY** where signal generator writes!

4. Check **Enabled** checkbox
5. Click OK

---

### 5. Check NT8 Output Window

**Press F5 in NT8 to open Output window**

**Look for:**
```
2024-01-15 09:00:00 === LIVE TRADING STARTED ===
2024-01-15 09:00:00 Signal file: C:\Trading\Signals\mnq_signals.csv
2024-01-15 09:00:00 Connection: Connected
```

**Warning signs:**
```
[WARN] Signal too old: 10.5s
[WARN] Rolling gate triggered: 2.0 trades/hour < 4.0 minimum
[ERROR] Error reading signal file: ...
```

---

### 6. Test Signal Generator Manually

**Create a test signal file manually:**

```powershell
# On VPS
echo "2024-01-15 09:30:45,MNQ,ABSORPTION,LONG,17450.25" > C:\Trading\Signals\mnq_signals.csv
```

**Then watch NT8 Output window (F5)**
- Should see strategy attempt to take the trade within 5 seconds
- If not, there's a path or configuration issue

---

### 7. Check Order Flow Data Quality

**The signal generator needs good quality order flow data.**

**Test signal detection:**
```sql
-- Check if we're getting any ABSORPTION signals
SELECT
    timestamp_1sec,
    price,
    sell_aggressor_volume,
    bid_adds,
    net_resting_bid
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQ'
  AND timestamp_1sec >= now() - INTERVAL 1 HOUR
  AND sell_aggressor_volume > 30
  AND bid_adds > sell_aggressor_volume * 1.1
  AND net_resting_bid > 15
ORDER BY timestamp_1sec DESC
LIMIT 10;
```

**If 0 results:**
- Thresholds might be too strict for current market
- Need to lower detection thresholds

---

## Common Issues & Solutions

### Issue 1: Signal Generator Not Finding Data

**Error:** `No signals detected for hours`

**Solution:**
```python
# Edit CGCl_nt8_signal_generator.py
# Lower thresholds temporarily:

SIGNAL_PARAMS = {
    'ABSORPTION': {
        'min_aggressor': 20,      # Was 30
        'response_multiplier': 1.0,  # Was 1.1
        'min_net_resting': 10,    # Was 15
    },
    'ICEBERG': {
        'min_volume': 30,         # Was 40
        'max_visible_adds_ratio': 0.4,  # Was 0.3
        'min_imbalance': 8,       # Was 10
    },
    'BREAKOUT': {
        'volume_spike': 1.8,      # Was 2.0
        'aggression_spike': 2.0,  # Was 2.5
        'min_volume': 20,         # Was 25
        'min_aggression': 10,     # Was 12
    }
}
```

### Issue 2: Path Mismatch

**Error:** `Error reading signal file: FileNotFoundError`

**Solution:**
Ensure EXACT same path in both places:

**Signal Generator (Python):**
```python
SIGNAL_FILE = r'C:\Trading\Signals\mnq_signals.csv'
```

**NT8 Strategy:**
```
Signal File Path: C:\Trading\Signals\mnq_signals.csv
```

**No differences in:**
- Capitalization
- Slashes (use backslash \ on Windows)
- Spaces

### Issue 3: Rolling Gate Blocking

**Error:** `Rolling gate triggered: 0.0 trades/hour < 4.0 minimum`

**Solution:**
This is normal at start of day. Gate only triggers after 5 trades.

**But if blocking after several hours:**
- Market might be too slow
- Signals not being generated
- See Issue 1 (lower thresholds)

### Issue 4: Signal Generator Crashed

**Check Python console for errors:**
```
❌ Error: Connection refused (ClickHouse not running)
❌ Error: Table 'mnq_orderflow_1sec' not found
❌ Error: Permission denied writing to C:\Trading\Signals\
```

**Solution for each:**
1. Start ClickHouse: `sudo systemctl start clickhouse-server`
2. Create table: Run `sql/CGCl_create_orderflow_1sec.sql`
3. Create directory: `mkdir C:\Trading\Signals`

---

## Step-by-Step Debugging Process

### Step 1: Verify Signal File Is Updating

```powershell
# Watch the signal file
while ($true) {
    Get-Item C:\Trading\Signals\mnq_signals.csv | Select-Object LastWriteTime, Length
    Start-Sleep -Seconds 5
}
```

**Expected:** Timestamp changes every few seconds

**If not changing:** Signal generator not running or not detecting signals

---

### Step 2: Check Signal Generator Console Output

**Should see:**
```
✅ Connected to ClickHouse: localhost:8123/marketreplay
🔄 Monitoring for signals... (Ctrl+C to stop)

[2024-01-15 09:30:45] SIGNAL: ABSORPTION LONG @ 17450.25 (strength: 45)
[2024-01-15 09:35:12] SIGNAL: BREAKOUT SHORT @ 17452.75 (strength: 62)
```

**If seeing:**
```
🔄 Monitoring for signals...
(nothing for minutes)
```

**Problem:** No signals being detected
- Check ClickHouse has recent data
- Lower signal thresholds

---

### Step 3: Check NT8 Is Reading Signals

**NT8 Output Window (F5):**

**Good:**
```
2024-01-15 09:30:45 [TRADE] LONG ABSORPTION @ 17450.25 | Target: +6 Stop: -3
2024-01-15 09:30:45 [TRADE] FILLED: LONG @ 17450.50 | Qty: 1
```

**Bad:**
```
(nothing - no output at all)
```

**Diagnosis:**
- Strategy not enabled
- Signal file path wrong
- Signals too old (>5 seconds)

---

### Step 4: Manual Test

**Force a signal:**

1. **Stop signal generator** (if running)

2. **Create test signal:**
```powershell
# Current timestamp in correct format
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$signal = "$timestamp,MNQ,ABSORPTION,LONG,17450.25"
$signal | Out-File -FilePath "C:\Trading\Signals\mnq_signals.csv" -Encoding ASCII
```

3. **Watch NT8 Output** (F5)
   - Should see trade attempt within 5 seconds
   - If not, configuration issue

4. **Restart signal generator** after test

---

## Quick Fix Checklist

**If NO trades after checking all above:**

1. **Restart everything in this order:**
   ```
   1. Stop signal generator (Ctrl+C)
   2. Disable NT8 strategy
   3. Check ClickHouse has data
   4. Start signal generator
   5. Verify signal file updating
   6. Enable NT8 strategy
   7. Watch Output window (F5)
   ```

2. **Lower signal thresholds temporarily** (in signal generator)

3. **Check commission/slippage settings** in NT8:
   ```
   Strategy → Parameters → Commission: $0.70
   Slippage: 0 (using broker fills)
   ```

4. **Verify market is open:**
   - CME Equity Index Futures: 5:00 PM Sun - 4:00 PM Fri (CT)
   - If outside hours: No executions is expected

---

## Emergency Debug Mode

**Enable verbose logging in signal generator:**

```python
# Add at top of main loop in CGCl_nt8_signal_generator.py

print(f"[{datetime.now()}] Checking for signals...")

# After each query
if signals:
    print(f"  Found {len(signals)} potential signals")
else:
    print(f"  No signals this second")
```

**This will show if generator is working but not finding signals.**

---

## Success Indicators

**When everything is working:**

1. **Signal Generator Console:**
   ```
   [2024-01-15 09:30:45] SIGNAL: ABSORPTION LONG @ 17450.25 (strength: 45)
   [2024-01-15 09:31:12] SIGNAL: ICEBERG SHORT @ 17451.00 (strength: 38)
   ```

2. **Signal File:**
   ```
   2024-01-15 09:31:12,MNQ,ICEBERG,SHORT,17451.00
   ```
   (Updates every few seconds when signals detected)

3. **NT8 Output:**
   ```
   [TRADE] SHORT ICEBERG @ 17451.00 | Target: +5 Stop: -2.5
   [TRADE] FILLED: SHORT @ 17451.25 | Qty: 1
   ```

4. **NT8 Chart:**
   - See entry arrows
   - See stop/target levels drawn
   - Position showing in UI

---

## Most Likely Issues (in order)

1. **Signal generator not running** (90%)
2. **ClickHouse not running or no data** (5%)
3. **Path mismatch between generator and NT8** (3%)
4. **Signals not being detected (thresholds too strict)** (2%)

**Start with #1 and work down the list!**

---

## Contact Points

**If still stuck after checking all above:**

1. Verify ClickHouse connection:
   ```bash
   clickhouse-client --query "SELECT COUNT(*) FROM mnq_orderflow_1sec WHERE timestamp_1sec >= now() - INTERVAL 5 MINUTE"
   ```

2. Check Python has required packages:
   ```bash
   pip install clickhouse-connect
   ```

3. Verify NT8 can read files:
   ```powershell
   # Test file permissions
   type C:\Trading\Signals\mnq_signals.csv
   ```

---

*Most debugging issues are signal generator not running or ClickHouse not having data.*
*Start there first!*
