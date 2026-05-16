# L2 Market Data Capture Pipeline - Deployment Guide

## Architecture Overview

```
NinjaTrader Market Replay
  ↓ (L2 OnMarketDepth events)
CG_L2_Capture_Chunked.cs
  ↓ (50k-row CSV chunks)
~/Documents/CG_L2_Capture/YYYY-MM-DD/l2_chunk_####.csv
  ↓ (Python watcher monitors)
l2_chunk_watcher.py
  ↓ (converts, loads, deletes)
Parquet (compressed) + ClickHouse BM_MNQ_L2_RAW
  ↓ (periodic aggregation)
l2_aggregator.py
  ↓ (research tables)
BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M
BM_MNQ_AGGRESSION_EXECUTIONS_5M
  ↓ (backtest strategies)
Wall Rejection, Wall Breakout, etc.
```

## Target Capture Period

**March 1, 2025 → May 5, 2026** (14 months)

### Why This Period?
- Includes November 2025 trending spike
- Includes April 2026 massive trend (24000→27500)
- Validates breakout strategy on real trending data

---

## Phase 1: Setup NinjaTrader Strategy

### 1. Copy Strategy to NT8

```bash
# Strategy file already created
cp ninjascript/CG_L2_Capture_Chunked.cs \
   ~/Documents/NinjaTrader\ 8/bin/Custom/Strategies/
```

### 2. Compile in NinjaTrader

1. Open NinjaTrader 8
2. Tools → Edit NinjaScript → Strategy
3. Find `CG_L2_Capture_Chunked`
4. Compile (F5)
5. Check for errors

### 3. Configure Strategy Parameters

**Recommended Settings:**
- `MaxRowsPerChunk`: **50000** (critical for performance)
- `MaxDepthLevels`: **10** (sufficient for wall detection)
- `InstrumentSymbol`: **MNQ**

### 4. Apply to Market Replay

1. Control Center → Tools → Playback Connection
2. Load historical data for MNQ
3. Set date range: **2025-03-01** (start slowly, test first)
4. Add strategy to chart:
   - Right-click chart → Strategies → CG_L2_Capture_Chunked
   - Enable strategy
   - Set parameters
5. Start playback at **100x speed** initially

**Expected Output:**
```
~/Documents/CG_L2_Capture/
  2025-03-01/
    l2_chunk_0001.csv
    l2_chunk_0002.csv
    ...
  2025-03-02/
    ...
```

---

## Phase 2: Start Python Watcher

### 1. Install Dependencies

```bash
pip install pandas pyarrow clickhouse-connect watchdog
```

### 2. Start Watcher

```bash
cd /home/bernard/trading4/CG_MNQ_MarketReplayLab/scripts

# Start in background
python3 l2_chunk_watcher.py \
  --watch-folder ~/Documents/CG_L2_Capture \
  --parquet-folder ./l2_parquet \
  --poll-interval 5 \
  > l2_watcher.log 2>&1 &

# Monitor log
tail -f l2_watcher.log
```

**Expected Behavior:**
```
2026-XX-XX - INFO - L2 Chunk Processor initialized
2026-XX-XX - INFO - Found 1 completed chunk(s)
2026-XX-XX - INFO - Processing chunk: l2_chunk_0001.csv
2026-XX-XX - INFO -   Loaded 50,000 rows (12.5 MB)
2026-XX-XX - INFO -   Parquet saved: 2.3 MB (5.4x compression)
2026-XX-XX - INFO -   Loaded to ClickHouse: 50,000 rows
2026-XX-XX - INFO -   CSV deleted: l2_chunk_0001.csv
2026-XX-XX - INFO -   ✓ Chunk processed in 3.2s (15,625 rows/sec)
```

---

## Phase 3: Aggregate Into Research Tables

### Run After Each Session

```bash
# Check what raw data is available
python3 l2_aggregator.py --status

# Aggregate a date range
python3 l2_aggregator.py \
  --start-date 2025-03-01 \
  --end-date 2025-03-05
```

**Expected Output:**
```
======================================================================
L2 AGGREGATION: 2025-03-01 to 2025-03-05
======================================================================

Processing 2025-03-01 (5,234,567 raw L2 rows)...
  ✓ Inserted 15,432 liquidity event records for 2025-03-01

Processing 2025-03-02 (6,123,456 raw L2 rows)...
  ✓ Inserted 18,234 liquidity event records for 2025-03-02

...

======================================================================
AGGREGATION COMPLETE
======================================================================
  Dates processed: 5
  Total liquidity events: 82,341
======================================================================
```

---

## Phase 4: Backtest on New Data

Once you have trending periods captured (November, April):

```bash
# Test Wall Breakout on April 2026 (trending)
python3 backtest_wall_breakout_tuned_5m.py \
  --start-date 2026-04-01 \
  --end-date 2026-04-30

# Test Wall Rejection on February 2026 (choppy)
python3 backtest_wall_rejection_5m.py \
  --start-date 2026-02-01 \
  --end-date 2026-02-28

# Compare results
```

---

## Performance Expectations

### L2 Data Volume (MNQ RTH)

| Period | Raw L2 Rows/Day | CSV Size/Day | Parquet Size/Day | CH Storage/Day |
|--------|-----------------|--------------|------------------|----------------|
| Open (9:30-10:30) | 2-5M | 50-120 MB | 10-25 MB | 8-20 MB |
| Midday | 3-8M | 75-200 MB | 15-40 MB | 12-32 MB |
| **Full RTH** | **5-15M** | **125-400 MB** | **25-80 MB** | **20-65 MB** |

### Chunk Timing (50k rows)

| Market Phase | Chunk Duration | Chunks/Day |
|--------------|---------------|------------|
| Open (volatile) | 30-90 sec | 100-200 |
| Midday (moderate) | 2-10 min | 50-100 |
| **Average RTH** | **3-5 min** | **75-125** |

### Full Capture Estimates (Mar 2025 - May 2026)

- **Trading days**: ~290
- **Raw L2 rows**: 3-6 billion
- **CSV (before deletion)**: Transient (< 1 GB at any time)
- **Parquet archive**: 7-20 GB
- **ClickHouse raw**: 6-18 GB
- **Aggregated tables**: 500 MB - 2 GB

---

## Monitoring

### Check Capture Status

```bash
# NT8 log
tail -f ~/Documents/NinjaTrader\ 8/log/YYYY-MM-DD.txt

# Watcher log
tail -f l2_watcher.log

# Aggregator log
tail -f l2_aggregator.log
```

### Check ClickHouse Storage

```sql
-- Raw L2 data
SELECT
    trade_date,
    count() as rows,
    formatReadableSize(sum(data_compressed_bytes)) as compressed_size
FROM system.parts
WHERE table = 'BM_MNQ_L2_RAW' AND active
GROUP BY trade_date
ORDER BY trade_date;

-- Aggregated heatmap
SELECT count() as events, min(trade_date), max(trade_date)
FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M;
```

---

## Troubleshooting

### NT8 Playback Freezing

**Symptom**: Market replay stutters or stops

**Causes**:
- Chunk size too large (increase rotation frequency)
- Disk I/O bottleneck (use SSD, reduce buffer size)
- Too many depth levels (reduce `MaxDepthLevels`)

**Solution**:
```csharp
// Reduce chunk size
MaxRowsPerChunk = 25000  // Instead of 50000

// Or reduce depth
MaxDepthLevels = 5  // Instead of 10
```

### Watcher Not Processing Chunks

**Symptom**: CSV files accumulate, not converting

**Check**:
```bash
# Is watcher running?
ps aux | grep l2_chunk_watcher

# Check permissions
ls -la ~/Documents/CG_L2_Capture/

# Check ClickHouse connection
clickhouse-client --query "SELECT 1"
```

### Low Compression Ratio

**Expected**: 5-10x compression (CSV → Parquet)

**If lower**:
- Data may not be compressible (random/noisy)
- Check ZSTD compression is enabled
- Verify data types (use category/uint8 vs object/int64)

---

## Safety Checklist

Before running full 14-month capture:

- [ ] Test on single day first (2025-03-01)
- [ ] Verify chunk rotation working
- [ ] Verify watcher processing correctly
- [ ] Verify ClickHouse ingestion
- [ ] Verify aggregation pipeline
- [ ] Check disk space (need ~50 GB free)
- [ ] Run at moderate playback speed (100-500x)
- [ ] Monitor NT8 stability

---

## Next Steps After Capture

1. **Validate breakout on April 2026** (trending period)
2. **Build regime detector** (ADX, price structure, volatility)
3. **Create hybrid strategy** with regime switching
4. **Deploy live** with confidence

---

## Support Files Created

```
ninjascript/
  CG_L2_Capture_Chunked.cs          ← NT8 strategy

scripts/
  l2_chunk_watcher.py               ← CSV → Parquet → CH
  l2_aggregator.py                  ← Raw L2 → Research tables

docs/
  L2_CAPTURE_DEPLOYMENT.md          ← This file
```

---

## Questions?

Check logs first:
- `l2_watcher.log` - Processing pipeline
- `l2_aggregator.log` - Aggregation status
- NT8 Log - Strategy execution

Good luck capturing! 🚀
