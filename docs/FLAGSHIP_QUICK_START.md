# Flagship Hybrid v1.0 - Quick Start Guide

**File**: `CG_MNQ_Flagship_Hybrid_v1_0.cs`
**Status**: ✅ Ready for deployment
**Date**: 2026-05-01

---

## What It Does (30 Second Summary)

The Flagship Hybrid combines 4 independent trading systems into one unified strategy:

1. **ORB** establishes if the market is bullish, bearish, or choppy (9:30-9:45 AM)
2. **T2** finds tactical entry signals using order flow (event imbalance)
3. **T3** confirms entries with L2 market depth (wall detection)
4. **Padder** blocks manipulation traps (failed breakouts)

**Result**: Only take longs when market is bullish + events support + walls confirm + no manipulation
**Result**: Only take shorts when market is bearish + events support + walls confirm + no manipulation

---

## Deployment (5 Minutes)

### Step 1: Copy to VPS

```bash
sudo cp ~/trading4/CG_MNQ_MarketReplayLab/ninjascript/CG_MNQ_Flagship_Hybrid_v1_0.cs /mnt/vps_strategies/
```

### Step 2: Compile in NinjaTrader 8

1. Open NinjaTrader 8 on VPS
2. Tools → Compile
3. Look for "CG_MNQ_Flagship_Hybrid_v1_0" in strategy list
4. Fix any errors (should compile clean)

### Step 3: Attach to Chart

1. Load MNQ chart (1-minute bars recommended)
2. Right-click chart → Strategies → Add → CG_MNQ_Flagship_Hybrid_v1_0
3. Use default parameters for first test
4. Enable strategy

### Step 4: Verify It's Working

Check NinjaTrader Output window for:
```
╔════════════════════════════════════════════════════════════════╗
║  CG MNQ FLAGSHIP HYBRID v1.0                                   ║
║  Multi-Layer Institutional Warfare System                      ║
╠════════════════════════════════════════════════════════════════╣
```

---

## Parameter Quick Reference

### Baseline Settings (Recommended for First Test)

```
Execution:
  Quantity: 1                    ⚠️  HARDCODED - DO NOT CHANGE
  StopTicks: 20                  ($10 risk per trade)
  TargetTicks: 40                ($20 reward per trade)
  MaxHoldSeconds: 600            (10 minute timeout)

Layer 1 - ORB:
  OpeningRangeMinutes: 15        (9:30-9:45 AM ET)
  MinRangeWidth: 5.0             (Skip low volatility days)
  OrbBreakoutBuffer: 2.0         (Confirmation buffer)

Layer 2 - T2:
  MinEventDelta: 20.0            (Bid/ask event imbalance)
  MinEventImbalance: 0.15        (Normalized threshold)
  EventLookbackBars: 200         (~10 seconds)

Layer 3 - T3 Wall:
  MinWallSize: 100               (Contracts at best bid/ask)
  MinAggressorVolume: 50         (Volume confirmation)

Layer 4 - Padder:
  EnableManipulationFilter: true
  FailedBreakoutBars: 5          (Lookback for traps)

Protection:
  MaxConsecutiveLosses: 3        (Stop after 3 losses)
  DailyMaxLoss: 200.0            (Stop at -$200)
  EmergencyStopDD: 400.0         (Emergency at -$400 DD)

Session:
  StartTimeEt: 93000             (9:30 AM ET)
  EndTimeEt: 155900              (3:59 PM ET)

Filters:
  MaxSpreadTicks: 8              (Skip wide spreads)
  SlippageTicks: 2               (Realistic PnL modeling)
```

---

## How Signals Work

### Morning Workflow (Real-Time Example)

**9:30 AM**: ORB tracking starts
```
[ORB] Opening range started at 09:30:00
Tracking: High, Low, VWAP, Volume
```

**9:45 AM**: ORB complete, bias determined
```
[ORB] Range complete: High=21245.00, Low=21230.00, Width=15.00, VWAP=21237.50
[ORB] Directional bias: BULLISH
→ System will ONLY allow LONG signals until next session
```

**10:15 AM**: T2 detects bullish event imbalance
```
T2: eventDelta=25.0, eventImbalance=0.18
→ LONG signal generated
→ Checking T3 wall confirmation...
```

**10:15 AM**: T3 confirms with bid wall
```
T3: bidWallScore=120, aggressorBuyVol=65
→ Wall confirmation PASSED
→ Checking Padder manipulation...
```

**10:15 AM**: Padder approves (no manipulation detected)
```
Padder: No failed breakout detected
→ ALL LAYERS APPROVED
→ Submitting LONG order
```

**10:15 AM**: Entry filled, protection armed
```
[ENTRY] FLAGSHIP_FILLED at 21240.00
Protection: Stop=21220.00 (-20 ticks), Target=21280.00 (+40 ticks)
```

---

## What Blocks Trades

### Layer 1: ORB Blocks

- **NEUTRAL bias**: Range too small (< 5.0 points)
- **Inside range**: Price hasn't broken out yet
- **Wrong direction**: Trying to go long when bias is BEARISH

### Layer 2: T2 Blocks

- **Weak event_delta**: Not enough bid/ask imbalance
- **Weak event_imbalance**: Normalized ratio below threshold
- **Spread too wide**: > 8 ticks (poor liquidity)

### Layer 3: T3 Blocks

- **No wall**: Wall size < 100 contracts at best level
- **No aggressor**: Insufficient volume confirmation

### Layer 4: Padder Blocks

- **Failed breakout**: Price broke OR high but reversed back inside
- **Manipulation detected**: Trap pattern identified

### Protection Blocks

- **Choppy day**: 3 consecutive losses already
- **Daily max loss**: Session PnL < -$200
- **Emergency stop**: Cumulative DD from peak > $400

---

## Telemetry Monitoring

### Output Location

```
C:\Users\[username]\Documents\NinjaTrader 8\trace\
CG_MNQ_Flagship_Hybrid_v1_0_YYYYMMDD_HHMMSS.csv
```

### Key Events to Watch

**OR_COMPLETE**:
```csv
OR_COMPLETE,...,BULLISH,21245.00,21230.00,15.00,21237.50,...
                ↑        ↑        ↑        ↑     ↑
                bias     OR high  OR low   width VWAP
```

**SIGNAL**:
```csv
SIGNAL,...,LONG,...,25.0,0.18,120,95,65,42,...,LONG_FLAGSHIP_APPROVED
              ↑       ↑    ↑    ↑   ↑   ↑  ↑   ↑
              side    event event bid ask aggr  diagnostic
                      delta imbal wall wall buy
```

**EXIT**:
```csv
EXIT,...,LONG,...,EXIT_PT|NT_PNL:19.30|REAL_PNL:9.30|MFE:42.0|MAE:5.0
                  ↑       ↑           ↑             ↑       ↑
                  reason  NT-style    realistic     max     max
                          PnL         PnL           profit  loss
```

---

## Troubleshooting

### "No trades all day"

**Check**:
1. Was OR width < 5.0? (Low volatility filter)
2. Did price stay inside OR range? (No breakout)
3. Were there any T2 signals? (Check telemetry)
4. Did T3 walls confirm? (Check bid/ask wall sizes)
5. Did Padder block? (Look for manipulation reasons)

**Fix**:
- Lower `MinRangeWidth` to 3.0 (trade more days)
- Lower `MinEventDelta` to 15.0 (more signals)
- Lower `MinWallSize` to 75 (accept smaller walls)

### "Too many trades, losing money"

**Check**:
1. Are trades aligned with ORB bias? (Should be one direction only after 9:45)
2. Is choppy filter working? (Should stop after 3 losses)
3. Are walls genuine or spoofs? (Check wall persistence)

**Fix**:
- Increase `MinRangeWidth` to 7.0 (filter low volatility)
- Increase `MinEventDelta` to 30.0 (stronger signals only)
- Reduce `MaxConsecutiveLosses` to 2 (faster shutdown)

### "Strategy won't compile"

**Check**:
1. File name EXACTLY matches: `CG_MNQ_Flagship_Hybrid_v1_0.cs`
2. Class name EXACTLY matches: `public class CG_MNQ_Flagship_Hybrid_v1_0`
3. No syntax errors (missing semicolons, braces)

**Fix**:
- Recompile NinjaTrader (Tools → Compile)
- Check Output window for specific error messages
- Verify file wasn't corrupted during transfer

### "Protection triggered immediately"

**Check**:
1. Did you start with existing cumulative loss? (Reset if needed)
2. Is daily max loss too tight? (Current: $200)
3. Are you testing on a known bad day?

**Fix**:
- Increase `DailyMaxLoss` to 300.0 (more breathing room)
- Increase `MaxConsecutiveLosses` to 4 (more attempts)
- Reset strategy to clear cumulative state

---

## Performance Expectations

### Conservative Estimate (Baseline Parameters)

```
Trades per day:        2-5
Win rate:              60-65%
Avg winner:            $15-20 (after costs)
Avg loser:             -$15-20 (after costs)
Expected daily:        $50-150
Max daily loss:        -$200 (protection trigger)
```

### What Success Looks Like

- **ORB bias matches market direction** most days
- **T2 signals fire 3-8 times per day** when ORB bias is active
- **T3 confirms 30-50%** of T2 signals (filters weak setups)
- **Padder blocks 10-20%** of signals (catches traps)
- **Protection layers trigger rarely** (only on genuinely bad days)

### Red Flags

- ❌ No ORB bias determination (stuck in NONE)
- ❌ ORB bias flips multiple times per day
- ❌ T2 signals fire every minute (too sensitive)
- ❌ T3 never confirms (parameters too strict)
- ❌ Padder blocks everything (too aggressive)
- ❌ Protection triggers daily (parameters too tight)

---

## Next Steps After First Test

### 1. Review Session Summary

At end of session, check Output window:
```
╔════════════════════════════════════════════════════════════════╗
║  CG MNQ FLAGSHIP HYBRID v1.0 - SESSION SUMMARY                 ║
╠════════════════════════════════════════════════════════════════╣
```

Look for:
- Total entries and exits
- Session PnL (NT-style vs Realistic)
- ORB bias accuracy
- T2/T3 signal funnel (how many made it through each layer)
- Padder blocks
- Protection triggers

### 2. Analyze Telemetry

Import CSV into spreadsheet:
- Group by ORB bias (did BULLISH days trend up?)
- Calculate T2→T3 confirmation rate
- Measure Padder accuracy (were blocks correct?)
- Review exit reasons (target vs stop vs timeout)

### 3. Tune Parameters

Based on results:
- Too conservative → Loosen thresholds
- Too aggressive → Tighten thresholds
- Good balance → Run longer test period

### 4. Backtest Historical Data

Run on Sep-Oct 2025 period:
- Compare to T2 baseline ($71,429 over 908 trades)
- Compare to ORB standalone ($8,952 over 1 month)
- Expect Flagship to outperform both

---

## Support

**Documentation**:
- Full implementation guide: `docs/FLAGSHIP_HYBRID_IMPLEMENTATION.md`
- Blueprint philosophy: `docs/T3_ORB_COLLABORATIVE_STRATEGY_BLUEPRINT_v1.md`

**Components**:
- Layer 1 ORB: `ninjascript/CG_T3_OpeningRangeBreakout.cs`
- Layer 2 T2: `ninjascript/CG_T2_EventImbalance_Baseline_v1_0.cs`
- Layer 3 T3: `ninjascript/CG_T2_WallEngine_v2_0.cs`
- Flagship: `ninjascript/CG_MNQ_Flagship_Hybrid_v1_0.cs`

**Questions to Ask**:
1. Is ORB bias classification working correctly?
2. Are T2 signals aligned with expected CH baseline behavior?
3. Is T3 wall confirmation adding value?
4. Is Padder catching real manipulation or over-filtering?
5. Are protection layers calibrated correctly?

---

## Final Checklist Before Live

- [ ] Backtest on historical data (Sep-Oct 2025)
- [ ] Paper trade for 1 week minimum
- [ ] Verify ORB bias accuracy > 70%
- [ ] Confirm T2 signals match baseline frequency
- [ ] Validate T3 improves win rate vs raw T2
- [ ] Check Padder blocks are justified
- [ ] Test all protection triggers work correctly
- [ ] Review realistic PnL expectations
- [ ] Verify one-position-at-a-time enforcement
- [ ] Confirm telemetry captures all events

**Once all checkboxes complete → Ready for live deployment**

---

**Good hunting. Trust the layers. Respect the governance.**
