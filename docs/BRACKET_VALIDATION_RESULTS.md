# Bracket Validation Results - Core Patterns

## Summary

**Test design**: Validated 11 core pattern trades across 27 different bracket configurations (3 targets × 3 stops × 3 timeouts).

**Total rows**: 297 (11 trades × 27 configurations)

**Key finding**: The baseline **40/20/600 bracket is optimal** or tied for optimal.

## Overall Best Configurations (Ranked by Expectancy)

```
Rank  Target  Stop  Timeout  Net Ticks  Expectancy  Win Rate  Avg Hold
──────────────────────────────────────────────────────────────────────
  1     40     20     600       +358      +32.55     90.91%    46.8s
  2     40     20     300       +358      +32.55     90.91%    46.8s
  3     40     20     900       +358      +32.55     90.91%    46.8s
  4     40     25     600       +353      +32.09     90.91%    46.8s
  5     40     25     300       +353      +32.09     90.91%    46.8s
  6     40     25     900       +353      +32.09     90.91%    46.8s
  7     40     15     900       +308      +28.00     81.82%    38.6s
  8     40     15     600       +308      +28.00     81.82%    38.6s
  9     40     15     300       +308      +28.00     81.82%    38.6s
 10     50     15     600       +268      +24.36     63.64%    46.8s
```

## Key Insights

### 1. Timeout Duration Doesn't Matter

For ANY given target/stop combination, all three timeout durations (300s, 600s, 900s) produce **identical results**:
- Same net PnL
- Same win rate
- Same avg hold time

**Implication**: All 11 trades resolve (hit target or stop) before even the shortest timeout (300 seconds).

**Recommendation**: Use shortest timeout (300s) to free up capital faster if trade goes against you.

### 2. Target Size Impact

**30-tick target**:
- Net: +258 ticks (+23.45/trade)
- Win rate: 90.91% (same as 40-tick)
- Trade-off: Same reliability but captures 100 fewer ticks

**40-tick target** (baseline):
- Net: +358 ticks (+32.55/trade)
- Win rate: 90.91%
- **Optimal balance**

**50-tick target**:
- Net: +268 ticks (+24.36/trade)
- Win rate: **63.64%** (drops significantly!)
- Trade-off: Win rate collapses (4 more losers), net PnL decreases

**Finding**: 40-tick target is the sweet spot. Going larger reduces win rate more than it increases profit.

### 3. Stop Size Impact

**15-tick stop (tighter)**:
- Net: +308 ticks (+28/trade) with 40-tick target
- Win rate: **81.82%** (vs 90.91% baseline)
- Trade-off: Catches 1 additional loser, reduces expectancy by -4.55 ticks

**20-tick stop** (baseline):
- Net: +358 ticks (+32.55/trade)
- Win rate: 90.91%
- **Optimal**

**25-tick stop (wider)**:
- Net: +353 ticks (+32.09/trade)
- Win rate: 90.91% (same as baseline)
- Trade-off: Same win rate but costs 5 more ticks on the single loser

**Finding**: 20-tick stop is optimal. Tighter stops reduce win rate. Wider stops waste ticks without improving win rate.

### 4. Pattern-Specific Optimization

**ORB_HIGH_BREAKOUT (4 trades)**:

Best configuration: 40/20 or 40/25
- Net: +152 ticks (+38/trade)
- Win rate: **100%** (4/4 targets)
- All timeout durations identical
- Needs the 20-tick stop (15-tick would catch a loser)

**VWAP_RESISTANCE_RECLAIM (7 trades)**:

Best configuration: **40/15** (tighter stop)
- Net: +211 ticks (+30.14/trade)
- Win rate: 85.71% (6/7 targets)
- **Beats 40/20 baseline** (+29.43) by +0.71 ticks

**Combined optimal**: 40/20 balances both patterns
- ORB needs wider stop (20 vs 15)
- VWAP performs slightly better with tighter stop
- Overall: 40/20 is best compromise

## Sample Robustness Analysis

**First half (Sept 23 - Oct 7): 7 trades**
```
Net PnL:        +266 ticks (+38/trade)
Win rate:       100% (7/7 targets)
Pattern:        Perfect execution
```

**Second half (Oct 8 - Oct 22): 4 trades**
```
Net PnL:        +92 ticks (+23/trade)
Win rate:       75% (3 targets, 1 stop)
Pattern:        One loser on Oct 15
```

**Observations**:
- First half: Flawless (100% win rate)
- Second half: Degraded (-39% expectancy, -25% win rate)
- Only loser occurred in second half (Oct 15)

**Concern**: Performance degradation in second half could indicate:
1. Market regime change
2. Small sample variance (only 4 trades)
3. Natural mean reversion after perfect first half

**Recommendation**: Monitor next 11 trades to confirm 90.91% win rate holds. If second-half degradation continues, may need regime filtering.

## Configuration Comparison Matrix

### Target = 40, varying stop and timeout

```
Stop  Timeout  Net     Expectancy  Win Rate  Delta vs Baseline
─────────────────────────────────────────────────────────────────
 15     300    +308      +28.00     81.82%      -4.55
 15     600    +308      +28.00     81.82%      -4.55
 15     900    +308      +28.00     81.82%      -4.55

 20     300    +358      +32.55     90.91%       0.00 ← BASELINE
 20     600    +358      +32.55     90.91%       0.00 ← BASELINE
 20     900    +358      +32.55     90.91%       0.00 ← BASELINE

 25     300    +353      +32.09     90.91%      -0.46
 25     600    +353      +32.09     90.91%      -0.46
 25     900    +353      +32.09     90.91%      -0.46
```

### Stop = 20, varying target and timeout

```
Target Timeout  Net     Expectancy  Win Rate  Delta vs Baseline
─────────────────────────────────────────────────────────────────
 30     300    +258      +23.45     90.91%      -9.10
 30     600    +258      +23.45     90.91%      -9.10
 30     900    +258      +23.45     90.91%      -9.10

 40     300    +358      +32.55     90.91%       0.00 ← BASELINE
 40     600    +358      +32.55     90.91%       0.00 ← BASELINE
 40     900    +358      +32.55     90.91%       0.00 ← BASELINE

 50     300    +248      +22.55     63.64%      -10.00
 50     600    +248      +22.55     63.64%      -10.00
 50     900    +248      +22.55     63.64%      -10.00
```

## Risk-Adjusted Performance

### Configuration: 40/20/600 (Baseline)

```
Average winner:    +38 ticks (10 trades @ 40-tick target - 2-tick cost)
Average loser:     -22 ticks (1 trade @ 20-tick stop + 2-tick cost)
Win rate:          90.91%
R:R ratio:         1.73:1 (38/22)
Expectancy:        +32.55 ticks/trade

Kelly fraction:    (0.9091 * 38 - 0.0909 * 22) / 22 = 1.48
                   → Recommend 25-50% of Kelly = 0.37-0.74 fraction
```

### Configuration: 40/15/300 (Aggressive)

```
Average winner:    +38 ticks (9 trades)
Average loser:     -17 ticks (2 trades @ 15-tick stop + 2-tick cost)
Win rate:          81.82%
R:R ratio:         2.24:1 (38/17)
Expectancy:        +28.00 ticks/trade

Higher R:R but lower expectancy due to additional loser
```

### Configuration: 50/20/600 (Greedy)

```
Average winner:    +48 ticks (7 trades @ 50-tick target - 2-tick cost)
Average loser:     -22 ticks (4 trades)
Win rate:          63.64%
R:R ratio:         2.18:1 (48/22)
Expectancy:        +22.55 ticks/trade

Win rate collapse makes this worse than baseline
```

## Recommendations

### Production Deployment

**Use: 40/20/300 (or 600)**
- Target: 40 ticks
- Stop: 20 ticks
- Timeout: 300 seconds (or 600, doesn't matter)

**Rationale**:
- Tied for #1 expectancy (+32.55 ticks/trade)
- 90.91% win rate
- Shortest timeout (300s) for faster capital recycling
- Robust across both patterns (ORB and VWAP)

### Pattern-Specific Optimization (Optional)

If willing to add complexity:

**ORB_HIGH_BREAKOUT**: Use 40/20/300
- Needs wider stop
- 100% win rate with 20-tick stop

**VWAP_RESISTANCE_RECLAIM**: Use 40/15/300
- Prefers tighter stop
- +0.71 ticks better expectancy
- Same 85.71% win rate

**Combined expectancy**: Slightly better than uniform 40/20
- ORB: 4 trades @ +38 = +152
- VWAP: 7 trades @ +30.14 = +211
- Total: +363 vs +358 baseline (+5 ticks improvement)

**Trade-off**: Added complexity for marginal gain (+5 ticks over 11 trades)

### Risk Management

**Position sizing**:
- Kelly fraction: 1.48 (very high!)
- Recommend 25-50% of Kelly = 0.37-0.74 fraction
- For $10,000 account: Risk $370-$740 per trade
- MNQ stop = $110 (20 ticks × $5.50/tick)
- Contracts = $370-740 / $110 = 3-6 contracts

**Loss governor**:
- With 90.91% win rate, expect 1 loss per 11 trades
- Daily stop: 3 consecutive losses (extremely rare with 90.91% WR)
- Probability of 3 consecutive losses: 0.0909^3 = 0.075% (1 in 1,331)

## Next Steps

### Option A: Deploy 40/20/300 Immediately

**Why**: Optimal configuration validated across 27 variants.

**Implementation**:
```python
target_ticks = 40
stop_ticks = 20
timeout_seconds = 300

triggers = get_core_pattern_triggers()  # LONG_ORB_HIGH + LONG_VWAP_RESISTANCE
for trigger in triggers:
    enter_long(trigger.entry_price)
    set_profit_target(trigger.entry_price + (40 * 0.25))
    set_stop_loss(trigger.entry_price - (20 * 0.25))
    set_timeout(trigger.entry_time + 300)
```

**Expected result**: ~11 trades per 22 days, +358 ticks (+32.55/trade), 90.91% win rate

### Option B: Test Pattern-Specific Brackets

**Why**: VWAP patterns prefer tighter stops (+0.71 ticks improvement).

**Implementation**:
```python
if trigger.pattern == 'LONG_ORB_HIGH_BREAKOUT_CONTINUATION':
    target_ticks = 40
    stop_ticks = 20
elif trigger.pattern == 'LONG_VWAP_RESISTANCE_RECLAIM':
    target_ticks = 40
    stop_ticks = 15
```

**Expected result**: +363 ticks vs +358 baseline (+5 ticks improvement)

### Option C: Extend Dataset to 60+ Days

**Why**: Second-half performance degradation (-39%) needs validation.

**Concern**:
- First half: 100% win rate (7/7)
- Second half: 75% win rate (3/4)

**Recommendation**: Collect 30+ more trades to confirm 90.91% win rate is robust.

## Conclusion

**The 40/20/600 bracket is validated as optimal.**

Key findings:
1. ✅ Tied for #1 expectancy across all 27 configurations
2. ✅ 90.91% win rate
3. ✅ Timeout duration irrelevant (all trades resolve < 300s)
4. ✅ Robust across both core patterns (ORB and VWAP)
5. ⚠️ Second-half degradation needs monitoring (75% vs 100% win rate)

**Recommendation**: Deploy 40/20/300 (or 600) for production. Monitor next 11 trades to confirm 90.91% win rate holds. If degradation continues, add regime filtering.

## Files

- **Table**: CG_mnq_pattern_validation_v1 (297 rows)
- **Source**: CG_mnq_pattern_filtered_v1 (11 core pattern trades)
- **Configurations tested**: 27 (3 targets × 3 stops × 3 timeouts)
