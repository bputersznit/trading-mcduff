# v4.2 Hybrid - QUICK START ⚡

## 1-Minute Setup

### Copy & Compile
```bash
# Copy file to NT8 Strategies folder
cp ninjascript/CGScalpingStrategyNT8Native_v4_2_Hybrid.cs \
   /path/to/NinjaTrader8/Strategies/

# In NT8:
F3 → F5 → Check "Compiled successfully"
```

### Default Settings (Ready to Go)
```
UseHybridMode: TRUE
TrendDetectionEMASeparation: 10.0
RegimeConfirmationBars: 5
TrendModeTarget: 25.0
TrendModeStop: 12.0
TrendModeMaxHold: 600 seconds
```

All other settings same as v4.1.

---

## How It Works (30 Seconds)

**CHOPPY Market** → 5pt stops, 8pt targets, 2-minute holds
**TRENDING Market** → 12pt stops, 25pt targets, 10-minute holds

Auto-detects regime every bar. Switches after 5-bar confirmation.

---

## Test on April 13

**v4.1 Result:** -$63 (64% stopped out)
**v4.2 Expected:** +$300-500 (rides 342-point bull trend)

1. Load v4.2 in NT8
2. Run April 13 Market Replay
3. Watch Output for: "REGIME CHANGE: CHOPPY → TRENDING"
4. Compare P&L

---

## Output Window - What to Watch

```
08:30: "CHOPPY MODE: Stop=5.0 Target=8.0 Hold=120s..."
10:00: "REGIME CHANGE: CHOPPY → TRENDING"
10:00: "TRENDING MODE: Stop=12.0 Target=25.0 Hold=600s..."
```

If you see regime change by 10 AM on April 13, it's working! ✅

---

## Tuning If Needed

**Not detecting trends:**
- Decrease `TrendDetectionEMASeparation` to 8.0

**Too many regime changes:**
- Increase `RegimeConfirmationBars` to 7

**Want bigger trend profits:**
- Increase `TrendModeTarget` to 30.0
- Decrease `TrendModeStop` to 10.0

---

## Files You Need

1. **ninjascript/CGScalpingStrategyNT8Native_v4_2_Hybrid.cs** ← Strategy file
2. **V4_2_HYBRID_COMPLETE.md** ← Full guide
3. **V4_2_QUICK_START.md** ← This file

---

**That's it! Ready to test.** 🚀

Expected improvement: **$363-563** on April 13 vs v4.1 (-$63 → +$300-500)
