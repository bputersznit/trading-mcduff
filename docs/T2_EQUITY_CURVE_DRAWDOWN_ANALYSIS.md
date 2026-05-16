# T2 Strict - Equity Curve Drawdown Analysis

## Your Question
"Would I ever have been down $600 USD from the start of the run?"

## Answer: 🟢 NO - Never Even Went Negative!

---

## Equity Curve Summary (Best Param Per Signal)

**Starting Equity**: $0
**Final Equity**: $98.00
**Peak Equity**: $212.20 (trade #4)
**Worst Equity**: **$70.90** (trade #3)

**Maximum Drawdown from Start**: -$0 (never went negative)
**Closest to -$600**: $670.90 away (never even close)

---

## Trade-by-Trade Equity Progression

```
Trade #1  Sep 26 → Win  +$142.30 → Equity: $142.30 ██████████
Trade #2  Oct 01 → Loss  -$35.70 → Equity: $106.60 ██████████
Trade #3  Oct 01 → Loss  -$35.70 → Equity: $70.90  █████ ← LOWEST POINT
Trade #4  Oct 01 → Win  +$141.30 → Equity: $212.20 ████████████████████ ← PEAK
Trade #5  Oct 02 → Loss  -$34.70 → Equity: $177.50 ███████████████
Trade #6  Oct 02 → Loss  -$35.70 → Equity: $141.80 ██████████
Trade #7  Oct 06 → Win  +$61.30  → Equity: $203.10 ████████████████████
Trade #8  Oct 20 → Loss  -$33.70 → Equity: $169.40 ███████████████
Trade #9  Oct 20 → Loss  -$35.70 → Equity: $133.70 ██████████
Trade #10 Oct 21 → Loss  -$35.70 → Equity: $98.00  █████ ← FINAL
```

**Win Rate**: 3 wins / 10 trades = 30%
**Result**: Started at $0, ended at $98 (never went negative)

---

## Alternative Analysis: Fixed Parameter Set (4000/10/16/32)

Using the most comprehensive parameter combo (28 trades across 6 days):

**Starting Equity**: $0
**Final Equity**: $74.40
**Peak Equity**: $369.80
**Worst Equity**: **$31.30** (trade #21)

**Result**: Also never went negative! Worst was +$31.30

---

## Why This Happened

### Strong Start (Sep 26)
- First trade: +$142.30 profit
- Built equity cushion immediately
- Prevented account from ever going negative

### Losing Streaks Were Manageable
- Worst streak: 2 losses in a row (trades #2-3)
- Each loss ~$35 on average
- Never had 3+ consecutive losses
- Never depleted the initial $142.30 cushion

### Key Stats
- Largest win: +$141.30
- Largest loss: -$35.70
- Win/Loss ratio: 4:1 (winners are 4x bigger than losers)

---

## Important Caveats

### 1. Extremely Small Sample
- Only **10 trades** total (or 28 with fixed params)
- Only **6 days** of data
- Industry standard: 60+ days minimum
- **This is NOT statistically significant**

### 2. Lucky Start Effect
- Sep 26 first trade gave +$142.30 cushion
- If first trade had been a loser, equity curve would be different
- Starting with a loss would have put account negative immediately

### 3. Missing Data
- 17 days of MBO data not processed for T2 signals
- 11 high-quality trading days never analyzed
- Real worst-case drawdown is UNKNOWN

### 4. Parameter-Dependent
- Best params per signal: Never negative (worst +$70.90)
- Fixed params 4000/10/16/32: Never negative (worst +$31.30)
- Different parameter sets could show different results

---

## What Could Happen in Future?

### Scenario 1: Bad Start
If you started trading on a losing day instead of Sep 26:
- First 2-3 trades could be losses
- Account goes negative immediately: -$107 to -$214
- Would need wins to recover

### Scenario 2: Extended Losing Streak
The backtest never saw more than 2 consecutive losses:
- 3 losses in a row = -$107
- 5 losses in a row = -$179
- 8 losses in a row = -$286 (like Oct 1 intraday pattern)
- 17 losses in a row = **-$607** ← Would hit your -$600 threshold

### Scenario 3: Choppy Market
Oct 1 showed an 8-loss streak WITHIN one day (intraday):
- If you started on a day like that, equity could drop -$286
- 2-3 choppy days in a row could easily hit -$600

---

## Conclusion

**Based on 6-day backtest (10 trades)**:
- ✅ Never went below -$600 from start
- ✅ Never even went negative (lowest: +$70.90)
- ⚠️ Only 10 trades - far too small to predict future max DD

**Reality Check**:
- 6 days cannot predict worst-case drawdown
- You COULD hit -$600 in the future
- Need 60+ days or live forward testing to establish realistic max DD
- Missing 17 days of data means we have incomplete picture

**Recommendation**:
If deploying this strategy, assume -$600 IS possible and plan accordingly:
- Use proper risk management
- Implement 3-strike choppy day filter
- Set emergency stop at -$500 to -$650 (based on Oct 1 intraday DD of -$286)
- Start with paper trading to collect more data

---

## Bottom Line

**Your Question**: "Would I be down $600 from the start?"
**Backtest Answer**: No - never went negative
**Real Answer**: Unknown - sample size too small to predict max DD with confidence
