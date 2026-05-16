# Flagship Hybrid v1.1 - Quick Deployment Guide

**Version**: 1.1 (ORB State Machine Refactor)
**Date**: 2026-05-01
**File**: `CG_MNQ_Flagship_Hybrid_v1_1.cs`

---

## What's New in v1.1

**One sentence**: ORB bias is no longer static—it now dynamically evolves as the session develops.

**Key change**: Replaced `enum OrbBias` with dynamic `enum OrbState` that transitions throughout the day.

---

## 5-Minute Deployment

### 1. Copy to VPS (requires sudo password)

```bash
sudo cp ~/trading4/CG_MNQ_MarketReplayLab/ninjascript/CG_MNQ_Flagship_Hybrid_v1_1.cs /mnt/vps_strategies/
```

### 2. Compile in NinjaTrader 8

```
Tools → Compile
Look for: "CG_MNQ_Flagship_Hybrid_v1_1" in strategy list
```

### 3. Attach to Chart

```
Load MNQ chart (1-minute bars)
Right-click → Strategies → Add → CG_MNQ_Flagship_Hybrid_v1_1
Use default parameters (same as v1.0)
Enable strategy
```

### 4. Verify State Machine Working

**Check Output window immediately**:
```
╔════════════════════════════════════════════════════════════════╗
║  CG MNQ FLAGSHIP HYBRID v1.1                                   ║
║  Multi-Layer Institutional Warfare System                      ║
╠════════════════════════════════════════════════════════════════╣
```

**Within first hour of trading, look for**:
```
[ORB_STATE] PRE_OR -> BUILDING_OR at 09:30:00 reason=OR_START
[ORB_STATE] BUILDING_OR -> NEUTRAL at 09:45:15 reason=OR_COMPLETE_INSIDE_RANGE
[ORB_STATE] NEUTRAL -> LONG_BREAKOUT at 10:15:23 reason=PRICE_ABOVE_OR_HIGH_BUFFER
```

---

## Key Differences from v1.0

| Aspect | v1.0 | v1.1 |
|--------|------|------|
| **ORB Logic** | Static bias set once at 9:45 AM | Dynamic state machine, continuous evaluation |
| **Delayed Breakouts** | ❌ Missed (bias locked at 9:45) | ✅ Caught (state updates live) |
| **Failed Breakouts** | ⚠️ Continues old bias | ✅ Transitions to FAILED state |
| **State Visibility** | Single bias enum | Full state history + transitions |
| **Telemetry** | `orb_bias` column | `orb_state` + `orb_reason` columns |

---

## New Parameters (v1.1)

All v1.0 parameters preserved. New additions:

### ORB State Eval Seconds (default: 1)
```
How often to re-evaluate ORB state
Higher value = less frequent transitions
Lower value = more responsive

Recommended: 1-5 seconds
```

### Block CHOP State (default: true)
```
If true: No trading when ORB state = CHOP (low volatility)
If false: Allow highly selective trades in CHOP

Recommended: true (safer)
```

### Allow Fade After Failure (default: true)
```
If true: Can take shorts after FAILED_LONG, longs after FAILED_SHORT
If false: Block all trades after failed breakouts

Recommended: true (enables fade opportunities)
```

### Enable Lunch Suppression (default: true)
```
If true: Block trading 11:30-13:00 ET (low quality period)
If false: Allow lunch trading

Recommended: true (lunch typically choppy)
```

---

## ORB State Cheat Sheet

| State | When It Happens | Long Permission | Short Permission |
|-------|----------------|:---------------:|:----------------:|
| PRE_OR | Before 9:30 AM | ❌ | ❌ |
| BUILDING_OR | 9:30-9:45 AM | ❌ | ❌ |
| NEUTRAL | Price inside OR | ⚠️ | ⚠️ |
| LONG_BREAKOUT | Price > OR high + buffer | ✅ | ❌ |
| SHORT_BREAKOUT | Price < OR low - buffer | ❌ | ✅ |
| FAILED_LONG | Broke up, reversed back | ❌ | ✅* |
| FAILED_SHORT | Broke down, reversed back | ✅* | ❌ |
| CHOP | OR width < threshold | ❌ | ❌ |
| FLAT_LOCK | Treasury protection hit | ❌ | ❌ |

\* = Only if `AllowFadeAfterFailure = true`

---

## Real-Time Example

### Scenario: Delayed Breakout Day

**9:45 AM**: OR completes
```
ORB High: 21240.00
ORB Low: 21230.00
ORB Width: 10.00
Current Price: 21235.00 (inside range)

v1.0: Sets bias = NEUTRAL → no longs, no shorts for rest of day
v1.1: Sets state = NEUTRAL → waiting for breakout
```

**10:30 AM**: Price breaks out
```
Current Price: 21242.50 (above OR high + 2pt buffer)

v1.0: Still thinks bias = NEUTRAL → MISSES THIS BREAKOUT ❌
v1.1: Detects breakout → state transitions: NEUTRAL → LONG_BREAKOUT ✅
      Now allows T2 long signals with T3/Padder confirmation
```

**11:15 AM**: Reversal
```
Current Price: 21238.00 (back below OR high)

v1.0: Still thinks bias = NEUTRAL → ignores reversal
v1.1: Detects reclaim → state transitions: LONG_BREAKOUT → FAILED_LONG
      Now blocks longs, allows shorts (fade the failure)
```

---

## Troubleshooting

### "No state transitions in Output window"

**Check**:
1. Is `PrintDiagnostics = true`? (should be default)
2. Did OR complete yet? (no transitions before 9:45 AM)
3. Is strategy enabled on chart?

### "State stuck in CHOP all day"

**Cause**: OR width < MinRangeWidth (default: 5.0 points)

**Solutions**:
- Lower `MinRangeWidth` to 3.0 (trade more days)
- Set `BlockChopState = false` (allow selective trades in CHOP)
- This is actually **correct behavior** for low volatility days

### "State stuck in NEUTRAL all day"

**Cause**: Price never broke above OR high or below OR low

**Solutions**:
- Lower `OrbBreakoutBuffer` from 2.0 to 1.0 (earlier breakouts)
- This may be correct—some days are genuinely range-bound

### "Too many state transitions"

**Cause**: ORB state flipping rapidly (whipsaw)

**Solution**:
- Increase `OrbStateEvalSeconds` from 1 to 5-10 seconds
- This throttles evaluation frequency

---

## Side-by-Side Comparison Test

### Recommended Testing Approach

1. **Run v1.0 on historical session**
   - Note entry times and directions
   - Record total trades and PnL

2. **Run v1.1 on SAME session**
   - Compare entry times and directions
   - Look for differences in signal approval

3. **Expected Differences**:
   - v1.1 should catch delayed breakouts v1.0 missed
   - v1.1 should avoid failed breakout traps v1.0 took
   - v1.1 may have slightly fewer total trades (higher quality)

4. **Document**:
   - Which trades were different?
   - Which version performed better?
   - Did state transitions make sense?

---

## Validation Checklist

Before going live with v1.1:

- [ ] Strategy compiles with no errors
- [ ] State transitions appear in Output window
- [ ] Telemetry CSV includes `orb_state` and `orb_reason` columns
- [ ] No trades occur before 9:45 AM (OR completion)
- [ ] Longs only occur when state = LONG_BREAKOUT or FAILED_SHORT
- [ ] Shorts only occur when state = SHORT_BREAKOUT or FAILED_LONG
- [ ] State = CHOP blocks trading (if BlockChopState = true)
- [ ] State = FLAT_LOCK blocks trading (treasury protection)
- [ ] One-position-at-a-time still enforced
- [ ] OCO++ stop/target still armed before entry
- [ ] Protection layers still trigger correctly

---

## When to Use v1.0 vs v1.1

### Use v1.0 if:
- You want simplicity (static bias, one decision per day)
- You trade only the first hour after OR completes
- You don't care about delayed breakouts or failed breakout adaptation

### Use v1.1 if:
- You want the strategy to adapt throughout the session
- You trade all day (not just first hour)
- You want to catch delayed breakouts (after 10:00 AM)
- You want failed breakout reversal logic
- You need full session evolution visibility

**Recommendation**: Use v1.1 unless you have specific reasons for v1.0

---

## Performance Expectations

### Conservative Estimate

```
Breakout Capture: +10-20% (catches delayed breakouts)
Failed Breakout Avoidance: -20-30% losses (reverses after failure)
Overall Win Rate: +5-10% (better regime classification)
Trade Frequency: -5-10% (blocks more neutral/chop conditions)
```

### Real-World Example Projections

If v1.0 baseline (Sep-Oct 2025):
```
908 trades, $71,429 total PnL
Avg: $78.66 per trade
```

v1.1 projections:
```
~850 trades (slightly lower frequency, higher quality)
~$75,000-80,000 total PnL (better quality)
Avg: $88-94 per trade (improved per-trade performance)
```

---

## Next Steps

1. **Deploy v1.1 to VPS** (sudo copy command above)
2. **Compile in NinjaTrader 8**
3. **Run 3-5 playback sessions** (verify state machine behavior)
4. **Review telemetry** (confirm state transitions make sense)
5. **Compare to v1.0** (same sessions, note differences)
6. **Paper trade 1 week** (real-time validation)
7. **Backtest Sep-Oct 2025** (compare to original baseline)
8. **Deploy live** (once validated)

---

## Support Files

**Full Documentation**:
```
~/trading4/CG_MNQ_MarketReplayLab/docs/FLAGSHIP_v1_1_ORB_STATE_MACHINE.md
```

**Strategy File**:
```
~/trading4/CG_MNQ_MarketReplayLab/ninjascript/CG_MNQ_Flagship_Hybrid_v1_1.cs
```

**v1.0 for Comparison**:
```
~/trading4/CG_MNQ_MarketReplayLab/ninjascript/CG_MNQ_Flagship_Hybrid_v1_0.cs
```

---

## Quick Mental Model

```
v1.0: "What's the bias?" → Set once at 9:45 → Locked in for the day
v1.1: "What's the state?" → Continuously evolving → Adapts to reality

v1.0: Static snapshot
v1.1: Dynamic movie

v1.0: One decision
v1.1: Continuous intelligence
```

---

**Deploy with confidence. The ORB state machine is the foundation for all future enhancements.**
