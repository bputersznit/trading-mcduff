# T2 ClanMarshal Statistical Parity Testing

## Overview

**Problem**: CH data is from October 2025, NT8 playback is from March-April 2026
**Solution**: Compare statistical distributions, not exact signals

## What is Statistical Parity?

Instead of matching exact signals (impossible with different dates), we compare:
- Do both systems produce similar **trade frequency**?
- Do both systems have similar **win rates**?
- Do both systems have similar **profit factors**?
- Do both systems trade at similar **session times**?
- Do both systems have similar **PnL distributions**?

**If YES** → NT8 logic is statistically equivalent to CH → Safe to trade live
**If NO** → NT8 logic differs from CH → Investigate before going live

## Quick Start

### Step 1: Generate CH Baseline
```bash
cd /home/bernard/trading4/CG_MNQ_MarketReplayLab

# Generate statistical profile from CH Oct 2025
clickhouse-client --multiquery < sql/CG_T2_Statistical_Baseline.sql

# This creates: /tmp/ch_baseline_oct2025.csv
```

### Step 2: Run NT8 Playback
```
1. Open NinjaTrader 8
2. Load Playback data for March-April 2026
3. Run strategy: CG_T2_ClanMarshal_LiveSignal_v1_1
4. Enable telemetry (EnableTelemetry = true)
5. Let it run to completion
6. Find CSV files in: ~/Documents/NinjaTrader 8/trace/
```

### Step 3: Compare Distributions
```bash
python3 sql/compare_statistical_parity.py

# Or if telemetry is elsewhere:
python3
>>> from sql.compare_statistical_parity import *
>>> ch = load_ch_baseline()
>>> nt8 = load_nt8_telemetry('/path/to/telemetry')
>>> compare_distributions(ch, nt8)
```

### Step 4: Review Results
```bash
# Open in browser
firefox /tmp/t2_statistical_parity.html

# Check parity score
# 85%+  = Excellent (proceed to live)
# 70%+  = Good (minor tweaks)
# 60%+  = Acceptable (investigate)
# <60%  = Poor (DO NOT trade live)
```

## Metrics Compared

### Performance Metrics
- Total trades
- Total PnL
- Win rate
- Profit factor
- Avg winner / loser
- Expectancy

### Distribution Metrics
- Trades per day (mean, median, std)
- Long vs Short ratio
- Hourly distribution
- PnL distribution shape

### Statistical Tests
- Chi-square test for side distribution
- Kolmogorov-Smirnov test for PnL distribution
- T-test for mean PnL

## Example Output

```
================================================================
STATISTICAL PARITY COMPARISON
================================================================

Metric                         CH Baseline          NT8 Test            Delta
-------------------------------------------------------------------------------------
Total Trades                   908                  845                 -6.9%
Total PnL                      $71,429.40           $65,234.20          -8.7%
Win Rate                       64.4%                62.1%               -2.3pp
Profit Factor                  2.93                 2.74                -6.5%
Avg Winner                     $184.30              $178.45             -3.2%
Avg Loser                      -$120.70             -$125.30            +3.8%
Trades/Day (mean)              75.7                 70.4                -7.0%
Long %                         46.6%                48.2%               +1.6pp
Short %                        53.4%                51.8%               -1.6pp

================================================================
STATISTICAL TESTS
================================================================

Side Distribution (Chi-Square Test):
  CH:  423 LONG (46.6%), 485 SHORT (53.4%)
  NT8: 407 LONG (48.2%), 438 SHORT (51.8%)
  p-value: 0.3721 ✅ Similar

PnL Distribution (Kolmogorov-Smirnov Test):
  KS statistic: 0.0842
  p-value: 0.1234 ✅ Similar

Mean PnL (T-Test):
  CH mean: $78.67
  NT8 mean: $77.21
  p-value: 0.7891 ✅ Similar

================================================================
PARITY ASSESSMENT
================================================================
Win Rate Similarity: 2.3pp difference ✅
Profit Factor Similarity: 93.5% match ✅
Side Distribution Match: ✅
PnL Distribution Match: ✅
Trade Frequency Match: 93.0% ✅
Expectancy Match: 98.1% ✅

================================================================
OVERALL PARITY SCORE: 92/100 (92%)
✅ EXCELLENT - NT8 is statistically equivalent to CH baseline
================================================================
```

## Interpreting Results

### Excellent (85%+)
- NT8 behavior matches CH very closely
- Safe to proceed with live trading
- Minor differences are expected (different market conditions)

### Good (70-84%)
- NT8 behavior is similar to CH
- Small adjustments may improve match
- Consider testing with more data
- Proceed with caution

### Acceptable (60-69%)
- NT8 shows some differences from CH
- Investigate specific metrics that differ
- May need parameter tuning
- Do NOT go live yet

### Poor (<60%)
- NT8 behavior differs significantly from CH
- NT8 logic may be incorrect
- Proxy calculations may be inadequate
- DO NOT trade live - fix issues first

## Common Issues

### High Trade Frequency Difference
**Problem**: NT8 generates 50% more/fewer trades than CH
**Causes**:
- Cooldown not enforced properly
- Session regime filter incorrect
- Threshold calibration off

### Low Win Rate Parity
**Problem**: NT8 win rate is 10%+ different
**Causes**:
- Signal quality differs (proxy calculations)
- Entry timing differs
- Stop/target placement differs

### Side Distribution Mismatch
**Problem**: NT8 heavily favors one side
**Causes**:
- Momentum calculation bias
- Delta proxy bias
- Missing regime context

## What's Next?

### If Parity is Good (70%+):
1. Run additional playback periods (May, June 2026)
2. Confirm results hold across multiple months
3. Consider paper trading
4. Then small live size

### If Parity is Poor (<70%):
1. Review NT8 signal logic vs CH query
2. Check proxy calculations (wall_score, delta, momentum)
3. Verify session regime classification
4. Test individual components separately
5. Re-run comparison after fixes

## Files

- `CG_T2_Statistical_Baseline.sql` - Extract CH Oct 2025 statistics
- `compare_statistical_parity.py` - Compare CH vs NT8 distributions
- `/tmp/ch_baseline_oct2025.csv` - CH baseline export
- `/tmp/t2_statistical_parity.html` - Visual comparison report

## Notes

- Statistical parity is more realistic than exact signal matching
- Different market conditions (Oct 2025 vs Mar 2026) are expected
- Focus on distributions, not absolute values
- A perfect 100% match is not necessary (or expected)
- 85%+ parity indicates NT8 captures the same edge as CH
