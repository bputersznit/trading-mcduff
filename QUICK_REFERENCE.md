# v4.1 Short Gate - Quick Reference Card

## 🎯 ONE-PAGE SUMMARY

### The Problem
```
Your LONGS work great:  107 trades | 48.6% win | +$558 ✅
Your SHORTS are bad:     33 trades | 21.2% win | -$297 ❌

Shorts are destroying profitability!
```

### The Solution: Short Gate
```
Filters bad shorts, keeps good ones
6-gate system: Only allows shorts in favorable conditions
Expected: ~15 shorts | ~40% win | -$50 loss → 83% improvement!
```

---

## ⚡ QUICK START (3 Steps)

### 1. Copy File (1 min)
```bash
cp ninjascript/CGScalpingStrategyNT8Native_v4_1_ShortGate.cs \
   /path/to/NinjaTrader8/Strategies/
```

### 2. Compile (1 min)
```
NT8 → F3 → F5 → Check for "Compiled successfully"
```

### 3. Test (30 min)
```
Market Replay → April 13-14, 2026
Settings: UseShortGate=TRUE, MaxFails=1, MinEMASep=5.0
Watch Output window for gate evaluations
```

---

## 🚪 The 6 Gates (What Shorts Must Pass)

| Gate | Check | Tune |
|------|-------|------|
| 1. Downtrend | Fast EMA < Slow, 5pt+ sep | `MinEMASeparation` |
| 2. Strong Signal | 2x normal strength | Base threshold |
| 3. Swing High | Near resistance | Code (3 ticks) |
| 4. Volume Delta | Negative & strong | Binary check |
| 5. Time of Day | After 10:30 AM | Code (cutoff time) |
| 6. Extra Absorption | 1.5x normal | Code (multiplier) |

**Flexibility:** `MaxFailedChecks` (0-3)
- 0 = Must pass ALL (strictest)
- 1 = Can fail 1 (balanced) ⭐
- 2 = Can fail 2 (looser)

---

## 📊 Expected Results

| Metric | v4 Original | v4.1 Short Gate | Change |
|--------|-------------|-----------------|--------|
| **Total Trades** | 140 | ~122 | -18 |
| **Win Rate** | 42.1% | ~47-50% | +6% |
| **Total P&L** | $261 | $400-500 | +72% |
| **Short Trades** | 33 | ~15 | -18 |
| **Short Win %** | 21.2% | ~40% | +19% |
| **Short P&L** | -$297 | -$50 | +83% |

---

## 🔧 Tuning Guide

### Shorts Still Losing Badly?
```
MaxFails = 0 (stricter)
MinEMASeparation = 8.0 (strong downtrend only)
```

### Too Few Shorts?
```
MaxFails = 2 (looser)
MinEMASeparation = 3.0 (slight downtrend OK)
```

### Shorts Near Break-Even? (Good!)
```
Keep current: MaxFails=1, MinEMASep=5.0
Test more days to confirm
```

---

## 🎯 Output Window - What to Look For

### Short ACCEPTED ✅
```
=== SHORT GATE RESULT: PASS ✅ ===
    Failed 0/6 gates (max allowed: 1)
```

### Short REJECTED ❌
```
=== SHORT GATE RESULT: FAIL ❌ ===
    Failed 2/6 gates (max allowed: 1)
```

### Common Rejection Reasons (Good!)
```
❌ GATE 1 FAIL: Not in strong downtrend → Filtering shorts in uptrend ✅
❌ GATE 3 FAIL: Not near swing high → Filtering mid-range shorts ✅
❌ GATE 4 FAIL: Delta not negative → Not enough selling ✅
```

---

## ✅ Success Criteria

### Minimum (Worth Keeping)
- [ ] Short win rate > 35%
- [ ] Short P&L > -$100
- [ ] Overall P&L improved

### Good (Recommended Target)
- [ ] Short win rate > 40%
- [ ] Short P&L near break-even
- [ ] Filtered 50%+ shorts

### Excellent (Ideal)
- [ ] Short win rate > 45%
- [ ] Short P&L positive
- [ ] Filtered 60%+ shorts

---

## 📁 Files Created

| File | Purpose |
|------|---------|
| `CGScalpingStrategyNT8Native_v4_1_ShortGate.cs` | Strategy file |
| `SHORT_GATE_SUMMARY.md` | Overview |
| `V4_1_SHORT_GATE_SETUP.md` | Detailed setup |
| `TEST_RUN_CHECKLIST.md` | Step-by-step test guide ⭐ |
| `TEST_RESULTS_TEMPLATE.md` | Results tracking |
| `SHORT_GATE_IMPLEMENTATION.md` | Technical details |

**Start here:** `TEST_RUN_CHECKLIST.md`

---

## 🚦 Decision Flow

```
Test v4.1 on April 13-14
    ↓
Fill out results template
    ↓
Did shorts improve?
    ├─ YES → Forward test April 15-18
    │         ↓
    │        Still good? → Keep Short Gate ✅
    │
    └─ NO → Tune stricter (MaxFails=0)
             ↓
            Still bad? → Disable shorts (longs = +$558!)
```

---

## ⚙️ Strategy Settings (Copy-Paste)

```
Bar Interval: 1 minute
Contracts: 1

ABSORPTION:
  Target: 8.0 | Stop: 5.0 | MaxHold: 120
  MinAggressor: 40 | Ratio: 1.5

BREAKOUT:
  Target: 10.0 | Stop: 6.0 | MaxHold: 60
  VolumeSpike: 2.5

FILTERS:
  TrendFilter: TRUE | FastEMA: 9 | SlowEMA: 21
  DisableShorts: FALSE | OnlyWithTrend: TRUE
  RTHOnly: TRUE | TrailingStops: TRUE

SHORT GATE: ⭐
  UseShortGate: TRUE
  MaxFailedChecks: 1
  MinEMASeparation: 5.0

RISK:
  MaxTrades/Hour: 10 | WeeklyLimit: 250 | HardLimit: 500
```

---

## 🆘 Quick Troubleshooting

| Problem | Solution |
|---------|----------|
| Won't compile | Check entire file copied, no errors in code |
| Strategy not in list | Refresh NT8, restart NT8 |
| No trades | Check RTH hours, playback running |
| No gate logs | Verify UseShortGate=TRUE |
| Too verbose | Normal - shows all evaluations |
| Still losing | Tune stricter or disable shorts |

---

## 📞 Next Steps

**Today:**
1. Follow `TEST_RUN_CHECKLIST.md`
2. Run April 13-14 test
3. Fill `TEST_RESULTS_TEMPLATE.md`

**This Week:**
4. Forward test April 15-18
5. Tune based on results
6. Test more days

**Next Week:**
7. Finalize settings
8. Prepare for live sim

---

## 💡 Remember

✅ **Goal:** Stop bleeding money on bad shorts
✅ **Not:** Make shorts perfect
✅ **If shorts still bad:** Disable them (longs = +$558!)
✅ **Longs already work:** Don't touch what works

**You're doing data-driven trading system development. This is professional-level stuff! 🚀**

---

**Quick access:**
```bash
# View all docs
ls -lh *.md

# Read checklist
cat TEST_RUN_CHECKLIST.md

# Read this reference
cat QUICK_REFERENCE.md
```
