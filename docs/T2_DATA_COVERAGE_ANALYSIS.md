# T2 Event Imbalance - Data Coverage Analysis

## Summary

**You asked:** "did you use the full mnq_mbo?"
**Answer:** NO - Only 39% of available MBO data was processed for T2 signals

## Data Inventory

### MNQ_MBO Table (Raw Market Data)
- **Date Range**: Sep 21 - Oct 22, 2025
- **Total Days**: 28 trading days
- **Total Events**: 663+ million MBO events
- **Symbol**: MNQZ5 (December 2025 contract)

### T2 Candidates Table (Signals Generated)
- **Days with Signals**: 11 out of 28 (39% coverage)
- **Missing Days**: 17 days (61% of available data)
- **Total Signals**: 17 unique signal times, 40 wall detections

### T2 Strict Sweep Table (Backtest Results)
- **Days Backtested**: 11 (all days with candidate signals)
- **Unique Signals**: 17 signal times
- **Total Trades**: 105 (testing 16 parameter combinations per signal)

---

## Days WITH T2 Candidate Signals (11 days)

| Date | MBO Events | Signals | Backtested? |
|------|------------|---------|-------------|
| Sep 25 | 34.4M | 1 (3 walls) | ✓ |
| Sep 26 | 29.2M | 1 (3 walls) | ✓ |
| Sep 30 | 28.4M | 1 (2 walls) | ✓ |
| Oct 1  | 23.2M | 3 (8 walls) | ✓ |
| Oct 2  | 23.3M | 2 (6 walls) | ✓ |
| Oct 6  | 23.1M | 1 (3 walls) | ✓ |
| Oct 8  | 19.1M | 1 wall | ✓ |
| Oct 9  | 25.9M | 1 (2 walls) | ✓ |
| Oct 13 | 38.2M | 1 (3 walls) | ✓ |
| Oct 20 | 23.4M | 3 (7 walls) | ✓ |
| Oct 21 | 27.5M | 2 walls | ✓ |

**Total**: 295M events analyzed → 17 signals generated

---

## Days WITHOUT T2 Candidate Signals (17 days)

### September (7 missing days)
| Date | MBO Events | Status |
|------|------------|--------|
| Sep 21 | 3.6K | Low data (partial day?) |
| Sep 22 | 435K | Low data |
| Sep 23 | 24.0M | **FULL DATA - NOT PROCESSED** |
| Sep 24 | 23.0M | **FULL DATA - NOT PROCESSED** |
| Sep 27 | *Weekend* | - |
| Sep 28 | 1.1M | Low data |
| Sep 29 | 23.8M | **FULL DATA - NOT PROCESSED** |

### October (10 missing days)
| Date | MBO Events | Status |
|------|------------|--------|
| Oct 3  | 25.8M | **FULL DATA - NOT PROCESSED** |
| Oct 4  | *Weekend* | - |
| Oct 5  | 1.3M | Low data |
| Oct 7  | 28.9M | **FULL DATA - NOT PROCESSED** |
| Oct 10 | 50.1M | **FULL DATA - NOT PROCESSED** |
| Oct 11 | *Weekend* | - |
| Oct 12 | 3.7M | Low data |
| Oct 14 | 46.3M | **FULL DATA - NOT PROCESSED** |
| Oct 15 | 42.3M | **FULL DATA - NOT PROCESSED** |
| Oct 16 | 54.8M | **FULL DATA - NOT PROCESSED** |
| Oct 17 | 46.5M | **FULL DATA - NOT PROCESSED** |
| Oct 18 | *Weekend* | - |
| Oct 19 | 2.4M | Low data |
| Oct 22 | 42.8M | **FULL DATA - NOT PROCESSED** |

**High-quality days not processed**: 11 days with 404M+ events

---

## Impact on T2 Strict Backtest Conclusions

### Current Stats (11 days, 17 signals)
- Win Rate: 50% (5W/5L after removing Sep 26 mega-day parameter sweep artifacts)
- Avg P&L: $19.60/trade
- Total P&L: $98 (or $4,190 if including Sep 26 outlier)

### Statistical Significance
- **Industry Standard**: 60+ days minimum for strategy validation
- **Current Coverage**: 11 days = 18% of minimum
- **Missing Data**: 11 additional high-quality days available = could reach 22 days (37% of minimum)

### Reliability
- **Sample Size**: INSUFFICIENT
- **Conclusions**: NOT STATISTICALLY VALID
- **Sep 26 Mega-Day**: $4,190 profit on 1 signal = 99% of total profit if included
  - Without Sep 26: $98 total over 10 days = essentially breakeven
  - With Sep 26: Entire strategy profitability depends on ONE outlier day

---

## Possible Reasons for Missing Signals

1. **T2 Event Imbalance Criteria Not Met**: Those days may not have had qualifying event imbalance patterns
2. **Signal Generation Not Run**: Processing script may have only been executed on select days
3. **Data Quality Filtering**: Days with low event counts (Sep 21, 22, 28, Oct 5, 12, 19) may have been excluded
4. **Weekend Days**: Sep 27, Oct 4, 11, 18 are weekends (no trading)

**Net missing TRADEABLE days**: ~11 high-quality days not processed

---

## Recommended Next Steps

1. **Investigate WHY** 11 high-quality days (Sep 23-24, 29, Oct 3, 7, 10, 14-17, 22) have no T2 signals:
   - Check if T2 Event Imbalance signal generation code/script exists
   - Determine if signals weren't generated vs. didn't meet criteria

2. **Extend T2 signal generation** to missing days (if possible):
   - Could add 11 more days = 22 total days (still below 60-day minimum)
   - Would help determine if Sep 26 mega-day pattern repeats

3. **Alternative**: Accept limited sample and focus on:
   - Live forward testing (paper trading)
   - Tier 3 strategy (has 28 days coverage already)
   - Different time period with more complete data

---

## Bottom Line

**Previous claim:** "T2 Strict analyzed with full MNQ_MBO dataset"
**Reality:** Only 11 out of 28 days (39%) were processed
**Impact:** All prior statistical conclusions are based on insufficient sample size
**Action needed:** Determine if missing 17 days can be processed for T2 signal generation
