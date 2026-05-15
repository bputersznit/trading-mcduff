# Order Flow Aggression v2.0 - Architecture Overview

## Critical Changes from v1.0

### 1. EXECUTION AGGRESSION vs BOOK UPDATES

**v1.0 (WRONG):**
```csharp
// Measured passive book changes
if (e.Operation == Operation.Add || e.Operation == Operation.Update)
    bidEventVolume += e.Volume;
```

**v2.0 (CORRECT):**
```csharp
// Measures actual trade executions
if (e.MarketDataType == MarketDataType.Last)
{
    if (e.Price == currentBestAsk)
        aggBuyVol100ms += e.Volume;  // Aggressive buying
    else if (e.Price == currentBestBid)
        aggSellVol100ms += e.Volume; // Aggressive selling
}
```

**Why this matters:**
- v1.0 counted spoofing, layering, fake walls
- v2.0 counts **actual market participation**
- v2.0 measures what traders **did**, not what they **threatened** to do

---

## 2. Multi-Scale Confirmation

**100ms (Tactical):**
- Primary signal generation
- Fast reaction to aggression spikes

**1s (Local):**
- Optional confirmation
- Filters micro-noise false positives
- Parameter: `Require1sConfirmation`

**5s (Structural):**
- Optional macro confirmation
- Ensures persistent directional flow
- Parameter: `Require5sConfirmation`

**Example:**
```
100ms: +80 buy aggression delta → LONG signal
1s:    +250 buy aggression       → Confirms
5s:    -50 sell aggression       → Rejects (if Require5sConfirmation=true)
```

---

## 3. Event Time (Not DateTime.Now)

**v1.0 Bug:**
```csharp
TimeSpan holdTime = DateTime.Now - entryTime;
```

**v2.0 Fix:**
```csharp
currentMarketTime = e.Time; // Updated from events
TimeSpan holdTime = currentMarketTime - entryTime;
```

**Why this matters:**
- Playback speed no longer affects timeout
- 100x playback = same strategy behavior
- Historical backtests now valid

---

## 4. Spread Filter

**New:**
```csharp
double spreadTicks = (currentBestAsk - currentBestBid) / TickSize;
if (spreadTicks > MaxSpreadTicks || spreadTicks < 0.5)
    return false; // Don't enter during dislocations
```

**Prevents:**
- Entering during liquidity vacuums
- Crossed book conditions
- Wide spread degradation

**Default:** MaxSpreadTicks = 3

---

## 5. Cooldown Timer

**Prevents rapid chop:**
```csharp
TimeSpan timeSinceLastTrade = currentMarketTime - lastTradeExitTime;
if (timeSinceLastTrade.TotalSeconds < CooldownSeconds)
    return false;
```

**Dynamic cooldown:**
- Normal: 30 seconds
- After stop: 60 seconds (PostStopCooldownSeconds)

**Prevents:**
- LONG → SHORT → LONG → SHORT noise
- Over-trading during chop
- Revenge trading after stops

---

## 6. Realistic Slippage

**Changed:**
```csharp
Slippage = 3; // 3 ticks = $1.50 per side for MNQ
```

**v1.0 had Slippage = 0 (fantasy)**

MNQ realistic degradation:
- Normal conditions: 2-3 ticks
- Fast markets: 4-8 ticks
- Open/close: 6-12 ticks

---

## 7. Correct P&L Calculation

**v1.0 Bug:**
```csharp
double tradePnL = Position.GetUnrealizedProfitLoss(...); // Returns ~0 after exit
```

**v2.0 Fix:**
```csharp
if (entryDirection == MarketPosition.Long)
    tradePnL = (execution.Price - entryPrice) * quantity * PointValue;
else if (entryDirection == MarketPosition.Short)
    tradePnL = (entryPrice - execution.Price) * quantity * PointValue;
```

**Result:**
- Accurate P&L display
- Daily limits now work correctly
- Consecutive loss tracking fixed

---

## 8. Book Pull Detection (Optional)

**Advanced feature:**
```csharp
long bidNetAdd = bidAddVol100ms - bidRemoveVol100ms;

// If big bid wall pulled during buy signal, cancel entry
if (signal == "LONG" && bidNetAdd < -100)
    signal = "NONE"; // Manipulation detected
```

**Detects:**
- Spoofing (bid wall pulled before execution)
- Layering traps
- Fake breakout setups

**Parameter:** `EnableBookPullDetection` (default: false)

---

## 9. Daily Limits - DISABLED for Testing

**Changed:**
```csharp
EnableDailyLimits = false; // Was true in v1.0
```

**Limits still configurable:**
- MaxDailyLoss: $500 (was $60)
- MaxConsecutiveLosses: 10 (was 3)
- ProfitLockPeak: $10,000 (was $3,000)

**To re-enable:** Set `EnableDailyLimits = true` in strategy parameters

---

## Architecture Comparison

| Component | v1.0 | v2.0 |
|-----------|------|------|
| Signal Source | Book updates (passive) | Trade executions (aggressive) |
| Time Resolution | 100ms only | 100ms + 1s + 5s |
| Timeout Calculation | DateTime.Now (BROKEN) | Event time (CORRECT) |
| P&L Calculation | Unrealized (BROKEN) | Entry-exit (CORRECT) |
| Spread Filter | None | Max 3 ticks |
| Cooldown | None | 30s / 60s post-stop |
| Slippage | 0 ticks (fantasy) | 3 ticks (realistic) |
| Cancel Tracking | None | Optional (book pulls) |
| Daily Limits | Enabled (broken) | Disabled for testing |

---

## What's Still Missing (Future v2.1+)

### Not implemented yet:
1. **Wall persistence tracking** - Need historical depth snapshots
2. **Absorption detection** - Large volume at price without movement
3. **Iceberg detection** - Replenishing hidden size
4. **Sweep detection** - Multi-level aggressive takeovers
5. **Liquidity migration** - Order flow shifting between levels
6. **Heat persistence** - Sustained aggression at same price
7. **Trapped trader logic** - Failed breakout reversals
8. **Secondary data series for OR** - Currently depends on chart timeframe

### Architectural limitations:
- Still rule-based manipulation filters (not data-driven)
- No OCO++ broker confirmation
- No depth event starvation diagnostics
- Single-instrument (no correlation analysis)

---

## Performance Expectations

### Compared to v1.0:
- **Fewer trades** (cooldown + spread filter)
- **Better signal quality** (execution aggression vs book noise)
- **More realistic fills** (3 tick slippage)
- **Working risk limits** (correct P&L tracking)

### Compared to backtest:
- Live will have MORE slippage (increase to 4-5 ticks)
- Playback may throttle depth events (signal loss)
- Fast markets may widen spreads (fewer entries)

---

## Testing Protocol

### Phase 1: Playback Validation (CURRENT)
- Run on same Oct 2025 data as v1.0
- Compare trade count (expect 30-50% fewer)
- Verify P&L accuracy
- Check daily limit enforcement (currently disabled)
- Confirm no timeout bugs

### Phase 2: Parameter Optimization
- Test MinAggressionDelta: 30, 50, 75, 100
- Test 1s confirmation ON/OFF
- Test 5s confirmation ON/OFF
- Test cooldown: 15s, 30s, 60s
- Test spread filter: 2, 3, 4, 5 ticks

### Phase 3: Enable Risk Limits
- Set MaxDailyLoss = $100
- Set MaxConsecutiveLosses = 4
- Verify limits enforce correctly

### Phase 4: Live Paper Trading
- 1 contract only
- Monitor for 3-5 days
- Compare fills vs playback
- Measure actual slippage
- Check depth event completeness

---

## Key Parameters

### Conservative Profile:
```
MinAggressionDelta = 75
MinAggressionImbalance = 0.65
Require1sConfirmation = true
Require5sConfirmation = false
MaxSpreadTicks = 2
CooldownSeconds = 60
```

### Aggressive Profile:
```
MinAggressionDelta = 30
MinAggressionImbalance = 0.55
Require1sConfirmation = false
Require5sConfirmation = false
MaxSpreadTicks = 4
CooldownSeconds = 15
```

### Recommended Starting Point:
```
MinAggressionDelta = 50
MinAggressionImbalance = 0.60
Require1sConfirmation = true
Require5sConfirmation = false
MaxSpreadTicks = 3
CooldownSeconds = 30
PostStopCooldownSeconds = 60
EnableDailyLimits = false (for testing)
```

---

## Critical Differences from ClickHouse Backtest

The CH backtest used:
```sql
bidEventVolume - askEventVolume > 50  -- Book updates (v1.0 model)
```

v2.0 uses:
```csharp
aggBuyVol - aggSellVol > 50  -- Execution aggression (v2.0 model)
```

**These are fundamentally different signals.**

Expected outcomes:
- Different trade entries
- Different trade counts
- Different P&L profile
- More stable in spoofing environments
- Better live/playback correlation

---

## Diagnostic Output

Strategy prints:
```
========== 5/12/2026 - NEW TRADING DAY ==========
9:45 AM | OR CALCULATED: High=29300.25 Low=29180.50
10:23:14.832 | ENTRY LONG @ 29245.75 | TZ:NORMAL OR:ABOVE_OR | AggVol:287 Spread:1.0
10:23 AM | #1 FILL Long @ 29246.00 | Tgt:29256.00 Stp:29241.00
10:28 AM | #1 EXIT Target | P&L:$20.00 | Daily:$20.00/0 | Limits:OFF
```

Key info:
- OR calculation confirmation
- Entry with aggression volume and spread
- Fill with targets/stops
- Exit with accurate P&L
- Daily tracking
- Limit status (ON/OFF)

---

## Upgrade Path

**From v1.0 to v2.0:**
1. Compile v2.0 in NinjaTrader
2. Remove v1.0 from charts
3. Add v2.0 to chart
4. Set `EnableDailyLimits = false` for testing
5. Run playback comparison
6. Expect fewer but higher quality trades

**DO NOT run both v1.0 and v2.0 simultaneously** - they will conflict.

---

## File Location

```
Documents\NinjaTrader 8\bin\Custom\Strategies\CG_OrderFlow_Aggression_v2_0.cs
```

Compile: Tools → Compile (F5)

---

## Version History

**v1.0 (May 14, 2026)**
- Initial release
- Book update imbalance model
- Multiple critical bugs

**v1.0_FIXED (May 14, 2026)**
- Fixed timeout calculation
- Added verbose logging control
- P&L still broken

**v1.0_CORRECTED (May 14, 2026)**
- Fixed P&L calculation
- DateTime.Now still wrong for playback
- Still using book updates (wrong signal)

**v2.0 (May 14, 2026)**
- Complete architectural rewrite
- Execution aggression model
- Multi-scale confirmation
- Event time (not DateTime.Now)
- Spread filter
- Cooldown timer
- Realistic slippage
- Daily limits disabled for testing
- Optional book pull detection

---

## Contact

For questions on v2.0 architecture, refer to:
- This document
- `ninjascript/CG_OrderFlow_Aggression_v2_0.cs` source
- Technical critique (May 14, 2026 conversation)
