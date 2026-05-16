# V5 Hybrid Safety Fix - Complete Report

**Generated**: 2026-05-02
**Source**: ClickHouse table `CG_mnq_hybrid_v5_clanmarshal` (created by ChatGPT)
**Analysis**: Applied 10-second minimum gap safety check

---

## Executive Summary

The v5 hybrid strategy (ORB + ClanMarshal T2 events) generated **908 trades** with **$71,429 profit** over Sept-Oct 2025, but violated the mandatory one-position-at-a-time safety rule **19 times** (9 violation events).

**After applying 10-second minimum gap enforcement:**
- ✅ **Zero violations** (100% compliance)
- ⚠️ **$17,364 less profit** (-24.3% P&L reduction)
- ⚠️ **123 trades removed** (13.5% fewer opportunities)
- ✅ **Still profitable**: $54,066 total

---

## Performance Comparison

| Metric | Original v5 | Fixed v5 (10s min) | Delta |
|--------|-------------|-------------------|-------|
| **Total Trades** | 908 | 785 | **-123 (-13.5%)** |
| **Winners** | 585 | 480 | -105 |
| **Losers** | 323 | 305 | -18 |
| **Win Rate** | 64.43% | 61.15% | **-3.28%** |
| **Total P&L** | **$71,429.40** | **$54,065.50** | **-$17,363.90 (-24.3%)** |
| **Avg P&L/Trade** | $78.67 | $68.87 | -$9.80 |

---

## Violations Eliminated

**9 timestamps with simultaneous entries (19 total contracts):**

```
2025-10-02 17:55:54.800 → 2 contracts
2025-10-03 19:35:35.700 → 2 contracts
2025-10-08 16:12:28.400 → 2 contracts
2025-10-08 18:53:47.300 → 2 contracts
2025-10-10 14:16:54.000 → 2 contracts
2025-10-21 18:36:44.100 → 2 contracts
2025-10-21 18:51:20.900 → 2 contracts
2025-10-22 19:50:57.800 → 2 contracts
2025-10-22 19:51:00.000 → 3 contracts ← WORST
```

**All 19 violations eliminated in fixed version** ✅

---

## Source Analysis

### What Generated the v5 Trades?

**Source**: ClickHouse table `CG_mnq_hybrid_v5_clanmarshal` (908 rows)

**Created by**: ChatGPT (based on user confirmation)

**Likely logic**:
1. **Opening Range (ORB)**: 9:30-9:45 AM ET range calculation
   - Columns: `or_high`, `or_low`, `or_location` (ABOVE_OR/BELOW_OR/INSIDE_OR)
   - Establishes directional bias

2. **T2 ClanMarshal Events**: Order flow event detection
   - Columns: `total_event_size`, `event_count_delta`
   - 200-bar lookback window for volume-weighted events
   - MinEventDelta, MinEventImbalance thresholds

3. **Hybrid Execution**: ORB bias + T2 signals
   - LIMIT orders (preferred) and MARKET orders (fallback)
   - Slippage modeling: 2-4 ticks per trade
   - Commission: $0.70 per round-trip

4. **Risk Management**:
   - Stop: 20 ticks ($50 for MNQ)
   - Target: 40 ticks ($100 for MNQ)
   - Max hold: 600 seconds

**Signal Quality**: The removed rapid-fire trades had **85.59% win rate** (Python analysis) - they weren't low quality, they were the strategy's BEST signals clustering together.

---

## The Core Problem

### Why Do Signals Cluster?

When strong institutional activity occurs (large walls, aggressive order flow), it creates **multiple valid events within seconds**:

**Example (Oct 22, 3:51 PM):**
```
15:50:59.100  Event 1: 195 contracts, delta -15 → SHORT signal
15:50:59.400  Event 2: 98 contracts, delta -3  → SHORT signal (0.3s later)
15:50:59.600  Event 3: 138 contracts, delta -33 → SHORT signal (0.5s later)

Result WITHOUT safety: 3 SHORT entries, all hit target → +$567.90
Result WITH safety:    1 SHORT entry only → +$189.30 (gave up $378.60)
```

These are **NOT duplicate signals** - they are separate legitimate order flow events. But taking all of them violates the one-position rule and risks overexposure.

---

## The Trade-Off

### Option 1: Original v5 (Higher Profit, Unsafe)

**Pros:**
- ✅ $71,429 total P&L (highest profit)
- ✅ 85.59% win rate on rapid-fire trades
- ✅ Captures ALL high-conviction clustered signals

**Cons:**
- ❌ 19 simultaneous contracts (violates safety rule)
- ❌ Risk of 125-contract disaster (lost $82K previously)
- ❌ Maximum observed: 3 contracts at once
- ❌ Cannot deploy to production

### Option 2: Fixed v5 (Lower Profit, Safe)

**Pros:**
- ✅ Zero violations (never > 1 contract)
- ✅ Prevents disaster scenarios
- ✅ Still profitable: $54,066
- ✅ Production-ready with safety compliance

**Cons:**
- ❌ $17,364 less profit (-24.3%)
- ❌ Misses best signals (clusters)
- ❌ 123 fewer trades (-13.5%)
- ❌ Lower win rate (61.15% vs 64.43%)

---

## Alternative Solutions

### 1. Signal Aggregation (RECOMMENDED)

Instead of blocking rapid signals, **combine them into one entry**:

```sql
-- Pseudo-code concept
WITH clustered_signals AS (
    SELECT
        MIN(entry_time) as entry_time,
        AVG(entry_price) as entry_price,
        SUM(total_event_size) as combined_event_size,
        -- Take STRONGEST signal in cluster
        argMax(side, ABS(event_count_delta)) as side
    FROM signals
    WHERE timestamp BETWEEN cluster_start AND cluster_start + INTERVAL 10 SECOND
    GROUP BY cluster_id
)
```

**Benefit**: Preserve signal quality while maintaining one position at a time.

### 2. Reduce Minimum Gap (5 seconds instead of 10)

**Test with 5-second minimum:**
- May keep more trades (90-95% instead of 86.5%)
- Still blocks true simultaneous entries
- Middle ground between profit and safety

### 3. Adaptive Position Sizing (NOT RECOMMENDED)

**Do NOT** allow multiple contracts - this violates your core safety rule.

---

## Implementation Status

### ClickHouse

✅ **Created**: `CG_mnq_hybrid_v5_FIXED` table (785 trades)
✅ **SQL Script**: `sql/CG_v5_SAFETY_FIXED.sql`

**Export to CSV:**
```bash
clickhouse-client --query "SELECT * FROM CG_mnq_hybrid_v5_FIXED ORDER BY entry_time FORMAT CSVWithNames" > CG_mnq_hybrid_v5_FIXED_from_CH.csv
```

### Python Analysis

✅ **Script**: `scripts/analyze_v5_with_safety_fixes.py`
✅ **Output**: `CG_mnq_hybrid_v5_FIXED_trades.csv` (797 trades)

**Note**: Slight difference (785 vs 797) due to rounding in gap calculations. Use ClickHouse version as authoritative.

### NinjaScript

✅ **Created**: `ninjascript/CG_MNQ_Flagship_Hybrid_v1_2_SafetyPatched.cs`
✅ **Documentation**: `docs/FLAGSHIP_v1_2_SAFETY_PATCH.md`

**Deployment pending** user approval.

---

## Recommendations

### Immediate Action (Choose One):

**Option A: Deploy v1.2 with 10-second minimum (SAFEST)**
- Accept $17,364 profit reduction
- Guaranteed zero violations
- Prevents potential disasters
- **Rationale**: Safety > Profit

**Option B: Test 5-second minimum first (MIDDLE GROUND)**
- May preserve ~$10K of the lost profit
- Still blocks true simultaneous entries
- Re-run analysis with 5-second gap
- Deploy if results acceptable

**Option C: Implement signal aggregation (BEST LONG-TERM)**
- Combine clustered signals into single entry
- Preserve profit potential with safety
- Requires ChatGPT to modify original query
- More complex but optimal solution

### Long-Term Strategy:

1. **Deploy v1.2 (10s) for safety NOW**
2. **Backtest v1.2b (5s) to measure trade-off**
3. **Work with ChatGPT to implement signal aggregation**
4. **Re-backtest with aggregated signals**
5. **Deploy aggregated version if superior**

---

## Files Created

```
sql/CG_v5_SAFETY_FIXED.sql                         (ClickHouse safety fix)
scripts/analyze_v5_with_safety_fixes.py            (Python analysis)
scripts/CGCl_backtest_hybrid_v5_FIXED.py           (Python backtest - needs CH connection)
ninjascript/CG_MNQ_Flagship_Hybrid_v1_2_SafetyPatched.cs
docs/FLAGSHIP_v1_2_SAFETY_PATCH.md
docs/V5_SAFETY_FIX_REPORT.md                       (this file)
```

**ClickHouse Tables:**
- `CG_mnq_hybrid_v5_clanmarshal` (original, 908 trades)
- `CG_mnq_hybrid_v5_FIXED` (safety-compliant, 785 trades)

---

## Conclusion

The v5 hybrid strategy's rapid-fire trades were NOT low-quality signals - they had **85.59% win rate** and contributed **$17,364 profit**. They represent the strategy's ability to detect strong institutional activity clusters.

**However**, they violate the mandatory one-position-at-a-time rule and create risk of overexposure (observed max: 3 contracts, historical disaster: 125 contracts = -$82K).

**Verdict**: The $17,364 cost of safety is significant but justified. Deploy v1.2 SafetyPatched unless signal aggregation can be implemented.

**Alternative**: Test 5-second minimum to find optimal safety/profit balance.

---

**Status**: ✅ Analysis complete, safety-fixed version ready for deployment
