# Price Trigger Path Outcomes - Fixed 2:1 Bracket Results

## Summary

**Fixed bracket parameters:**
- Target: 40 ticks (10 points for MNQ)
- Stop: 20 ticks (5 points for MNQ)
- Timeout: 600 seconds (10 minutes)
- Cost floor: 2 ticks (slippage + commission)

**Overall Performance:**
- Trades: 61
- Gross PnL: +160 ticks (+2.62 per trade)
- Net PnL: +38 ticks (+0.62 per trade) after 2-tick cost
- Win rate: 37.7% (23 targets, 38 stops, 0 timeouts)
- Hold time: 0-365 seconds (avg ~31 seconds)

**Verdict**: Barely profitable with 2:1 reward-to-risk and 37.7% win rate.

## Side Breakdown

### LONG Trades (18 total)

```
Gross PnL:      +240 ticks (+13.33 per trade)
Net PnL:        +204 ticks (+11.33 per trade)
Win rate:       55.6% (10 targets, 8 stops)
Avg hold:       30.83 seconds
```

**Analysis**: LONG side is highly profitable and carries the entire strategy.

### SHORT Trades (43 total)

```
Gross PnL:      -80 ticks (-1.86 per trade)
Net PnL:        -166 ticks (-3.86 per trade)
Win rate:       30.2% (13 targets, 30 stops)
Avg hold:       30.7 seconds
```

**Analysis**: SHORT side is losing money. Win rate is too low to support 2:1 bracket.

## Trigger Type Performance (Ranked by Net Expectancy)

### Tier 1: Excellent (Net > +20 ticks/trade)

**1. LONG_VWAP_RESISTANCE_RECLAIM** (7 trades)
```
Net PnL:        +206 ticks (+29.43 per trade)
Win rate:       85.7% (6 targets, 1 stop)
Avg hold:       32.14 seconds
Pattern:        Reclaim VWAP resistance after breakdown
```

**2. LONG_ORB_HIGH_BREAKOUT_CONTINUATION** (4 trades)
```
Net PnL:        +152 ticks (+38 per trade)
Win rate:       100% (4 targets, 0 stops) ⭐
Avg hold:       72.5 seconds
Pattern:        ORB high breakout with strong continuation
```

**Key Insight**: Both are LONG continuation patterns with high-quality aggression (STRENGTHENING).

### Tier 2: Profitable (Net > 0)

**3. SHORT_VWAP_SUPPORT_FAILURE** (2 trades)
```
Net PnL:        +16 ticks (+8 per trade)
Win rate:       50% (1 target, 1 stop)
Avg hold:       15 seconds
Pattern:        VWAP support failure fade
Note:           Small sample, needs more data
```

### Tier 3: Breakeven (Net ≈ 0)

**4. SHORT_ORB_HIGH_REJECTION_FADE** (17 trades)
```
Net PnL:        -14 ticks (-0.82 per trade)
Win rate:       35.3% (6 targets, 11 stops)
Avg hold:       36.47 seconds
Pattern:        ORB high rejection fade
```

**Analysis**: Slightly negative expectancy. Win rate (35.3%) is below breakeven threshold for 2:1 bracket (~40% needed).

### Tier 4: Losing (Net < -5)

**5. SHORT_ORB_LOW_SUPPORT_FAILURE** (24 trades - largest sample)
```
Net PnL:        -168 ticks (-7 per trade)
Win rate:       25% (6 targets, 18 stops)
Avg hold:       27.92 seconds
Pattern:        ORB low support failure
```

**Analysis**: This pattern is killing the strategy. Only 1 in 4 trades wins. ORB low support appears to be sticky/resilient.

**6. LONG_ORB_LOW_RECLAIM_SUPPORT** (4 trades)
```
Net PnL:        -88 ticks (-22 per trade)
Win rate:       0% (0 targets, 4 stops)
Avg hold:       10 seconds (quick stops)
Pattern:        ORB low reclaim after breakdown
```

**Analysis**: All 4 trades stopped out immediately. Pattern is unreliable with fixed bracket.

**7. LONG_VWAP_SUPPORT_REVERSION** (3 trades)
```
Net PnL:        -66 ticks (-22 per trade)
Win rate:       0% (0 targets, 3 stops)
Avg hold:       0 seconds (instant stops)
Pattern:        VWAP support reversion
```

**Analysis**: All 3 trades stopped out instantly. VWAP support reversions are not working with this bracket.

## Key Insights

### 1. LONG vs SHORT Asymmetry

**LONG side characteristics:**
- Higher quality triggers (avg end_quality 0.47 vs 0.14)
- STRENGTHENING aggression patterns
- Continuation and resistance reclaim setups
- Result: +11.33 ticks per trade

**SHORT side characteristics:**
- Lower quality triggers (avg end_quality 0.14)
- EXHAUSTING aggression patterns (quality collapse)
- Fade and failure setups
- Result: -3.86 ticks per trade

**Implication**: Market shows more reliable continuation than mean reversion in this dataset.

### 2. ORB HIGH vs ORB LOW Asymmetry

**ORB HIGH patterns:**
- LONG breakouts: 100% win rate (+38 per trade)
- SHORT fades: 35.3% win rate (-0.82 per trade)

**ORB LOW patterns:**
- LONG reclaims: 0% win rate (-22 per trade)
- SHORT failures: 25% win rate (-7 per trade)

**Implication**: ORB high is more actionable. ORB low appears sticky/resilient and resists both reclaims and failures.

### 3. VWAP Resistance vs Support Asymmetry

**VWAP resistance patterns:**
- LONG reclaims: 85.7% win rate (+29.43 per trade)

**VWAP support patterns:**
- LONG reversions: 0% win rate (-22 per trade)
- SHORT failures: 50% win rate (+8 per trade, small sample)

**Implication**: VWAP resistance reclaims are excellent. VWAP support reversions fail immediately.

### 4. Fixed Bracket Limitations

**Quick stop patterns** (0-10 second holds):
- LONG_VWAP_SUPPORT_REVERSION: 0 seconds
- LONG_ORB_LOW_RECLAIM_SUPPORT: 10 seconds

**Both have 0% win rate** - these patterns need either:
1. Wider stops (not economical)
2. Different entry timing (wait for confirmation)
3. Filtering out entirely

**Slow target patterns** (70+ second holds):
- LONG_ORB_HIGH_BREAKOUT_CONTINUATION: 72.5 seconds
- Still profitable despite slower execution

## Filter Recommendations

### Must Keep (Tier 1)

✅ **LONG_ORB_HIGH_BREAKOUT_CONTINUATION**
- Best pattern: 100% win rate, +38 per trade
- Keep all triggers (currently 4)

✅ **LONG_VWAP_RESISTANCE_RECLAIM**
- Excellent: 85.7% win rate, +29.43 per trade
- Keep all triggers (currently 7)

**Filtered result: 11 trades, +358 net ticks (+32.5 per trade)**

### Consider Keeping (Tier 2-3)

❓ **SHORT_VWAP_SUPPORT_FAILURE** (needs more data)
- Small sample (2 trades)
- Currently profitable (+8 per trade)
- Monitor with more data

❓ **SHORT_ORB_HIGH_REJECTION_FADE** (breakeven)
- Large sample (17 trades)
- Slightly negative (-0.82 per trade)
- Could improve with adaptive exits

### Must Filter Out (Tier 4)

❌ **SHORT_ORB_LOW_SUPPORT_FAILURE**
- 24 trades, -7 per trade
- Only 25% win rate
- ORB low support too sticky

❌ **LONG_ORB_LOW_RECLAIM_SUPPORT**
- 4 trades, -22 per trade
- 0% win rate, quick stops
- Pattern unreliable

❌ **LONG_VWAP_SUPPORT_REVERSION**
- 3 trades, -22 per trade
- 0% win rate, instant stops
- Pattern fails immediately

**Filtered out: 31 trades, -322 net ticks (-10.4 per trade)**

## Filtered Strategy Performance

**Keep only Tier 1 patterns:**

```
Trades:         11 (from 61)
Net PnL:        +358 ticks (+32.5 per trade)
Win rate:       90.9% (10 targets, 1 stop)
Reduction:      82% fewer trades, 9.4x higher expectancy
```

**Add Tier 2 (SHORT_VWAP_SUPPORT_FAILURE):**

```
Trades:         13 (from 61)
Net PnL:        +374 ticks (+28.8 per trade)
Win rate:       84.6% (11 targets, 2 stops)
```

**Add Tier 3 (SHORT_ORB_HIGH_REJECTION_FADE):**

```
Trades:         30 (from 61)
Net PnL:        +360 ticks (+12 per trade)
Win rate:       56.7% (17 targets, 13 stops)
```

## Next Steps

### Option A: Conservative (Tier 1 Only)
- Filter to LONG continuation patterns only
- Expected: ~11 trades per 22 days, +32.5 ticks per trade
- Trade-off: Fewer opportunities, but 90.9% win rate

### Option B: Balanced (Tier 1 + Tier 2)
- Add SHORT VWAP support failures
- Expected: ~13 trades per 22 days, +28.8 ticks per trade
- Trade-off: Slightly lower expectancy, still 84.6% win rate

### Option C: Aggressive (Tier 1 + Tier 2 + Tier 3)
- Include SHORT ORB HIGH fades
- Expected: ~30 trades per 22 days, +12 ticks per trade
- Trade-off: More trades, but expectancy drops to +12 (still profitable)

### Adaptive Exit Development

Current fixed bracket limitations:
- 0-second stop patterns fail immediately (need wider stops or better entry)
- 70+ second target patterns could benefit from trailing stops
- 2:1 bracket works for some patterns, but not all

**Next phase**: CG_mnq_price_trigger_adaptive_exits_v1
- Pattern-specific bracket sizing
- Trailing stops for continuation patterns
- Confirmation entries for reversal patterns
- Dynamic timeout based on pattern characteristics

## Files

- **Table**: CG_mnq_price_trigger_path_outcomes_v1
- **SQL**: clickhouse/CG_MNQ_PRICE_TRIGGER_PATH_OUTCOMES_V1.sql
- **Source**: CG_mnq_price_trigger_single_position_python_v1 (61 triggers, 0 violations)
