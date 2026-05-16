# CG MNQ Hybrid v5 ClanMarshal - Strategy Guide

## Overview

Order flow imbalance strategy that trades 100ms book aggression bursts with comprehensive risk management and manipulation filtering.

**Based on:** ClickHouse v5opt backtest results (904 trades, $5,749.70 profit, 63.27% win rate)

---

## Core Signal Logic

### Entry Conditions

**LONG Signal:**
- Event Delta > 50
- Event Imbalance > 0.60

**SHORT Signal:**
- Event Delta < -50
- Event Imbalance < -0.60

Where:
- **Event Delta** = Bid Event Size - Ask Event Size (100ms bucket)
- **Event Imbalance** = Event Delta / Total Event Size

### Risk Parameters

| Parameter | Value |
|-----------|-------|
| **Target** | 40 ticks (10 points = $20/contract) |
| **Stop** | 20 ticks (5 points = $10/contract) |
| **Risk:Reward** | 1:2 |
| **Max Hold Time** | 10 minutes (timeout) |
| **Slippage** | 3 ticks default |

---

## Filters & Guards

### 1. Opening Range (9:30-9:45 AM ET)

Strategy calculates high/low during first 15 minutes, then labels price location:
- **ABOVE_OR**: Price > OR High
- **BELOW_OR**: Price < OR Low
- **INSIDE_OR**: Price within OR

### 2. Manipulation Gate

Blocks these trade patterns (proven manipulation zones):

| Time Zone | OR Location | Side | Blocked? |
|-----------|-------------|------|----------|
| OPEN_15 (9:30-9:45) | Any | SHORT | ✗ Blocked |
| POST_OPEN (9:45-10:30) | INSIDE_OR | SHORT | ✗ Blocked |
| NORMAL (10:30-15:30) | INSIDE_OR | LONG | ✗ Blocked |
| CLOSE_30 (15:30-16:00) | ABOVE_OR | SHORT | ✗ Blocked |
| CLOSE_30 (15:30-16:00) | BELOW_OR | LONG | ✗ Blocked |

### 3. Single Position Enforcement

- Only 1 position at a time
- No new entry until prior trade exits
- Prevents position overlap

### 4. Daily Loss Limit

- **Threshold:** -$60 daily P&L
- **Action:** Stop trading for remainder of day
- **Reset:** Daily at 9:30 AM ET

### 5. Consecutive Loss Limit

- **Threshold:** 4 consecutive losing trades
- **Action:** Stop trading for remainder of day
- **Reset:** Daily at 9:30 AM ET

### 6. Profit Lock (Equity Guard)

Protects profits after strong performance:

- **Trigger Condition 1:** Daily peak ≥ $3,000
- **Trigger Condition 2:** Drawdown from peak ≤ -$500
- **Action:** Stop trading (lock in profits)

**Example:** Day reaches +$3,200, then drops to +$2,700 → Profit lock triggers (-$500 DD)

---

## NinjaTrader Configuration

### Strategy Parameters

```
Signal:
  Event Delta Threshold: 50
  Event Imbalance Threshold: 0.60

Risk:
  Target (ticks): 40
  Stop (ticks): 20

Guards:
  Daily Loss Limit (USD): 60
  Max Consecutive Losses: 4
  Profit Lock Peak (USD): 3000
  Profit Lock Drawdown (USD): 500

Filters:
  Enable Manipulation Gate: TRUE

Debug:
  Verbose Logging: FALSE (set TRUE for debugging)
```

### Chart Setup

1. **Instrument:** MNQ (Micro E-mini Nasdaq-100)
2. **Primary Series:** Any timeframe (1min recommended)
3. **Data Series:** Tick series auto-added by strategy
4. **Market Depth:** Required for aggression tracking
5. **Calculate:** OnEachTick

### Compilation Requirements

- **NinjaTrader 8** (8.0.23.1 or later)
- **Market Depth subscription** (Level 2 data)
- **Tick data enabled**

---

## Expected Performance (Based on Backtest)

| Metric | Value |
|--------|-------|
| **Period** | Sep 24 - Oct 22, 2025 (12 days) |
| **Total P&L** | $5,749.70 |
| **Trades/Day** | ~75 avg |
| **Win Rate** | 63.27% |
| **Profit Factor** | 2.37 |
| **Max Drawdown** | -$62.40 |
| **Avg Win** | $17.39 |
| **Avg Loss** | -$12.64 |

**Daily Range:** $23.60 - $916.00
**All 12 days profitable**

---

## Deployment

### From Windows VM:

```batch
\\VBOXSVR\CG_MNQ_MarketReplayLab\deploy_v5_clanmarshal.bat
```

### Manual Steps:

1. Copy `CG_MNQ_Hybrid_v5_ClanMarshal.cs` to:
   ```
   %USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Strategies\
   ```

2. Open NinjaTrader 8

3. Press **F5** to compile

4. Apply to MNQ chart

---

## Real-Time vs Backtest Differences

### What NT8 Implementation Provides:

✅ 100ms aggression buckets (via OnMarketData)
✅ Event delta and imbalance calculation
✅ All filters (OR, manipulation gate, guards)
✅ Position management with targets/stops
✅ Daily P&L tracking with profit lock

### Limitations vs ClickHouse:

⚠️ **Market Depth approximation** - Uses OnMarketData (execution flow) instead of full MBO events
⚠️ **Bucket precision** - May miss some 100ms buckets during low activity
⚠️ **Slippage** - Fixed 3 ticks vs dynamic (3-8 based on event size)
⚠️ **No limit order queue simulation** - All market orders

**Result:** Expect similar but not identical trade selection to ClickHouse backtest

---

## Monitoring & Adjustment

### Key Metrics to Watch:

1. **Trades per day** - Should average 60-90
2. **Win rate** - Target 60-65%
3. **Daily P&L variance** - $200-$900 range normal
4. **Manipulation gate blocks** - Log to verify filtering

### If Trade Count Too High (>100/day):

- Increase `EventDeltaThreshold` to 60-75
- Increase `EventImbalanceThreshold` to 0.65-0.70

### If Trade Count Too Low (<50/day):

- Decrease `EventDeltaThreshold` to 35-45
- Decrease `EventImbalanceThreshold` to 0.55

### If Win Rate Too Low (<55%):

- Enable `VerboseLogging` to analyze entries
- Check if manipulation gate is working
- Verify OR calculation at 9:45 AM

---

## Troubleshooting

### "No trades executing"

✓ Market Depth subscription active?
✓ After 9:45 AM (OR must be calculated)?
✓ Daily limits not hit?
✓ Position already open?

### "Too many losing trades"

✓ Manipulation gate enabled?
✓ OR calculating correctly at 9:45?
✓ Slippage settings reasonable?

### "Profit lock triggering too early"

✓ Increase `ProfitLockPeak` to 4000-5000
✓ Increase `ProfitLockDrawdown` to 600-800

### "Daily loss limit hit too quickly"

✓ Increase `DailyLossLimit` to 80-100
✓ Check if stops are too tight
✓ Review `MaxConsecutiveLosses` (increase to 5-6)

---

## Version History

**v5.0 ClanMarshal** (2026-05-16)
- Initial NinjaScript port from ClickHouse v5opt backtest
- 100ms aggression buckets with event delta/imbalance
- Full manipulation gate implementation
- Profit lock and daily limit guards
- Single position enforcement

---

## Support Files

- **Strategy:** `ninjascript/CG_MNQ_Hybrid_v5_ClanMarshal.cs`
- **Deployment:** `deploy_v5_clanmarshal.bat`
- **SQL Source:** `sql/CG_v5_OPTIMIZED_chain_CORRECTED.sql`
- **Backtest Results:** ClickHouse table `CG_mnq_hybrid_v5_clanmarshal_OPTIMIZED`

---

**Strategy Philosophy:**

*"High-frequency order flow imbalance trading with institutional manipulation awareness. Small stops (20 ticks), 2:1 R:R, and 63% win rate create consistent edge. Comprehensive guards protect capital on bad days while profit lock preserves gains on exceptional days."*
