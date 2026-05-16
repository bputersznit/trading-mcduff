# Phase 4 Top 5 Patterns - Backtest Results Analysis

**Date:** May 5, 2026 00:15 ET
**Backtest Script:** `scripts/wall_top5_patterns_backtest.py`
**Data:** Sept 23, 2025 (RTH only)
**Patterns:** Top 5 validated patterns from Phase 4 regime analysis

---

## Executive Summary

**CRITICAL FINDING: Live backtest shows NEGATIVE edge where Phase 4 predicted POSITIVE edge**

- **Phase 4 Prediction:** +10 to +16 tick expectancy, 45-65% win rates
- **Backtest Reality:** -4.04 tick expectancy, 39% win rate
- **Total Performance:** 300 trades, -$606 (-1212 ticks)

**Root Cause Analysis Required:** Significant gap between statistical expectancy and forward-walk execution.

---

## Backtest Configuration

### Execution Rules
- **Stop Loss:** 8 ticks (matches Phase 4 expectancy calculation)
- **Target:** 16 ticks (2:1 R/R)
- **Slippage:** 2 ticks on entry + 2 ticks on exit = 4 ticks total cost
- **Commission:** $0.70 per round trip (~0.14 ticks)
- **Max Hold:** 120 seconds
- **Cooldown:** 30 seconds between trades
- **Position Size:** 1 MNQ contract (hardcoded safety)

### Data Coverage
- **Signals Loaded:** 23,055 regime-filtered interactions
- **Trades Executed:** 300 (1.3% of signals matched Top 5 patterns)
- **Ticks Loaded:** 597,522 (Sept 23, 2025 RTH)
- **Date Range:** 2025-09-23 13:30:02 to 19:59:58 (single day)

---

## Overall Performance

| Metric | Value |
|--------|-------|
| Total Trades | 300 |
| Net P&L | -$606.00 |
| Net Ticks | -1,212.00 |
| Avg per Trade | **-4.04 ticks** |
| Win Rate | **39.00%** |
| Winners | 117 |
| Losers | 183 |
| Profit Factor | 0.51 |
| Max Drawdown | -1,212 ticks |

**Outcome Distribution:**
- **STOP:** 183 trades (61.0%) - stopped out
- **TARGET:** 117 trades (39.0%) - hit target

---

## Pattern-by-Pattern Breakdown

| Pattern | Trades | Net Ticks | Avg Ticks | Win Rate | Expected Win Rate | Gap |
|---------|--------|-----------|-----------|----------|-------------------|-----|
| P1: ASK CONT_SELL INSIDE HVOL OPEN | 46 | -208.4 | -4.53 | 36.96% | 49.57% | **-12.6%** |
| P2: ASK FLIP BELOW HVOL PM | 106 | -244.4 | -2.31 | 46.23% | 51.65% | **-5.4%** |
| P3: ASK CONT_SELL BELOW NVOL PM | 22 | -78.8 | -3.58 | 40.91% | 56.88% | **-16.0%** |
| P4: ASK FLIP INSIDE HVOL MIDDAY | 40 | -152.0 | -3.80 | 40.00% | 54.72% | **-14.7%** |
| P5: BID FLIP BELOW NVOL MIDDAY | 86 | -528.4 | **-6.14** | 30.23% | 56.87% | **-26.6%** |

**Key Observations:**
1. **ALL patterns underperformed** their Phase 4 win rate expectations
2. **Pattern 5 (LONG)** performed worst: -6.14 ticks/trade, only 30.23% win rate
3. **Pattern 2** came closest to expectations but still negative
4. **Gap ranges from -5.4% to -26.6%** below expected win rates

---

## Side Distribution Analysis

| Side | Trades | Net Ticks | Avg Ticks | Win Rate |
|------|--------|-----------|-----------|----------|
| SHORT | 214 | -683.6 | -3.19 | 42.52% |
| LONG | 86 | -528.4 | **-6.14** | 30.23% |

**Critical Finding:** LONG trades (Pattern 5 only) drastically underperformed SHORT trades.

---

## Risk Metrics

| Metric | Value | Analysis |
|--------|-------|----------|
| Avg Win | +10.60 ticks | Close to 2:1 target (16 ticks - 4 slippage = 12 expected) |
| Avg Loss | -13.40 ticks | Worse than expected (8 stop + 4 slippage = 12 expected) |
| Loss Magnitude | **26% larger than expected** | Suggests stops were overrun or slippage underestimated |

---

## Hypotheses for Expectancy Gap

### Hypothesis 1: **Lookahead Bias in Phase 4 Analysis** ⚠️ LIKELY
- **Phase 4** calculated outcomes using 30-second forward MFE/MAE without stop/target simulation
- **Phase 4 logic:** "If price moved 8+ ticks in rejection direction, call it a win"
- **Backtest reality:** Price can move 8+ ticks in 30 seconds but hit your stop first
- **Example:** Price drops 12 ticks (Phase 4 = "REJECT success"), but hits 8-tick stop on the way down (Backtest = stop loss)

**Impact:** Phase 4 likely **overestimated win rates** by 5-27% due to not modeling stop-out risk.

### Hypothesis 2: **Single-Day Sample Size**
- **Data:** Only 1 trading day (Sept 23, 2025)
- **Pattern counts:** 22-106 trades per pattern (vs 200-666 in Phase 4 study)
- **Risk:** Single day could be unrepresentative of average market conditions
- **Validation:** Need multi-week backtest to confirm

### Hypothesis 3: **Execution Cost Underestimation**
- **Phase 4 assumption:** 2-tick slippage per entry
- **Backtest reality:** 4 ticks total slippage (2 entry + 2 exit) + 0.14 commission
- **Impact:** -4.14 ticks per trade baseline cost
- **Note:** Even if ALL trades hit 16-tick target with zero slippage, avg = (16 - 4.14) = 11.86 ticks

### Hypothesis 4: **Regime Mismatch**
- **Phase 4 study:** Sept 21-24, 2025 (4 days pooled)
- **Backtest:** Sept 23, 2025 only
- **Question:** Was Sept 23 an atypical day for these patterns?
- **Validation:** Check if other days (Sept 21, 22, 24) show similar underperformance

### Hypothesis 5: **Stop Placement Issue**
- **Current stop:** 8 ticks
- **Avg loss:** -13.40 ticks
- **Gap:** 5.4 ticks worse than expected
- **Possible causes:**
  - Intrabar stop running (tick-level slippage worse than modeled)
  - Patterns occur at high-volatility moments where stops get blown through
  - 8-tick stop is too tight for MNQ microstructure noise

---

## Comparison to Phase 4 Expectations

### Pattern 1: ASK CONTINUED_SELL + INSIDE_OR + BELOW_VWAP + HIGH_VOL + RTH_OPEN
- **Phase 4:** 230 setups, 49.57% win, 16.41 tick expectancy
- **Backtest:** 46 trades, 36.96% win, -4.53 tick result
- **Gap:** -12.6% win rate, **-20.94 tick expectancy collapse**

### Pattern 2: ASK BUY_TO_SELL_FLIP + BELOW_OR_LOW + BELOW_VWAP + HIGH_VOL + PM_DRIFT
- **Phase 4:** 666 setups, 51.65% win, 12.44 tick expectancy
- **Backtest:** 106 trades, 46.23% win, -2.31 tick result
- **Gap:** -5.4% win rate, **-14.75 tick expectancy collapse**

### Pattern 5: BID SELL_TO_BUY_FLIP + BELOW_OR_LOW + BELOW_VWAP + NORMAL_VOL + MIDDAY
- **Phase 4:** 211 setups, 56.87% win, 10.76 tick expectancy
- **Backtest:** 86 trades, 30.23% win, -6.14 tick result
- **Gap:** **-26.6% win rate**, **-16.90 tick expectancy collapse**

**Worst Pattern:** Pattern 5 (LONG rejection) showed catastrophic underperformance.

---

## Data Coverage Validation

### Regime Distribution in Loaded Signals

| Regime | Count | Percentage |
|--------|-------|------------|
| **Time Buckets** |
| MIDDAY | 10,894 | 47.3% |
| PM_DRIFT | 7,582 | 32.9% |
| RTH_OPEN | 2,852 | 12.4% |
| CLOSE | 1,727 | 7.5% |
| **VWAP Relation** |
| BELOW_VWAP | 20,916 | 90.7% |
| AT_VWAP | 1,149 | 5.0% |
| ABOVE_VWAP | 990 | 4.3% |
| **ORB Position** |
| BELOW_OR_LOW | 14,511 | 63.0% |
| INSIDE_OR | 8,543 | 37.0% |
| ABOVE_OR_HIGH | 1 | 0.0% |
| **ATR Regime** |
| HIGH_VOL | 11,954 | 51.9% |
| NORMAL_VOL | 11,101 | 48.1% |

**Validation:** ✅ Regime distributions match Phase 4 study (BELOW_VWAP dominant, balanced time/vol regimes)

---

## Critical Issues Identified

### Issue 1: **Lookahead Bias in Phase 4 Expectancy Calculations**
- **Severity:** HIGH
- **Description:** Phase 4 measured outcomes using 30s MFE without simulating stop-out risk
- **Impact:** Inflated win rate expectations by 5-27%
- **Fix Required:** Re-run Phase 4 analysis with tick-level stop simulation to get realistic expectancy

### Issue 2: **Single-Day Backtest Sample**
- **Severity:** MEDIUM
- **Description:** 300 trades from 1 day insufficient for strategy validation
- **Impact:** Results may be unrepresentative
- **Fix Required:** Run multi-week backtest (Sept 21-Oct 31, 2025) for statistical confidence

### Issue 3: **LONG Pattern Failure**
- **Severity:** HIGH
- **Description:** Pattern 5 (BID FLIP LONG) lost -6.14 ticks/trade (worst of all patterns)
- **Impact:** 30.23% win rate vs 56.87% expected
- **Fix Required:** Investigate why BID walls below VWAP fail to support bounces

### Issue 4: **Execution Costs Higher Than Expected**
- **Severity:** MEDIUM
- **Description:** 4 ticks slippage + commission = -4.14 ticks baseline cost
- **Impact:** Requires 50%+ win rate just to break even with 2:1 R/R
- **Fix Required:** Either reduce costs (limit orders?) or increase R/R target

---

## Recommended Next Actions

### Option A: **Debug Phase 4 Methodology** (HIGHEST PRIORITY)
**Goal:** Determine if Phase 4 expectancy calculation is flawed

**Steps:**
1. Query Phase 4 `CG_mnq_wall_outcomes_regime_v1` table for Pattern 2 (best performer)
2. For each signal, simulate tick-level stop/target logic
3. Compare simulated win rate to Phase 4 reported win rate
4. If gap exists, Phase 4 has lookahead bias → need to re-validate all 29 patterns

### Option B: **Expand Backtest Date Range**
**Goal:** Verify if Sept 23 is representative or an outlier day

**Steps:**
1. Run backtest on Sept 21, 22, 24 individually
2. Compare per-day results
3. Run combined 4-day backtest
4. If all days show negative expectancy → patterns don't work with stops
5. If only Sept 23 is negative → single-day anomaly

### Option C: **Test Alternative Stop/Target Configurations**
**Goal:** Find execution parameters that match Phase 4 outcomes

**Steps:**
1. Test wider stops: 12-tick, 16-tick, 20-tick
2. Test different R/R: 1:1, 1.5:1, 3:1
3. Test time-based exits: 30s, 60s, 90s instead of stop/target
4. Find configuration where backtest matches Phase 4 win rates

### Option D: **Isolate Best-Performing Pattern**
**Goal:** Salvage any edge from the analysis

**Steps:**
1. Pattern 2 (ASK FLIP BELOW HVOL PM) had -2.31 ticks but 46.23% win rate
2. Test with reduced costs (limit orders at wall price instead of market)
3. Test with wider stop (10-12 ticks) to reduce stop-out rate
4. If can achieve 48%+ win rate with lower costs → may have edge

---

## Bottom Line

🚨 **Phase 4 Top 5 Patterns FAILED forward backtest validation**

**Reality Check:**
- **Phase 4 promised:** +10 to +16 ticks expectancy
- **Backtest delivered:** -4.04 ticks expectancy
- **Gap:** 14-20 tick expectancy collapse

**Root Cause (Preliminary):**
- Phase 4 likely has **lookahead bias** by measuring 30s outcomes without stop simulation
- Patterns may still have edge, but NOT with 8-tick stops and 2:1 R/R
- Single-day sample (300 trades) may be insufficient for validation

**Status:** ⚠️ **DO NOT DEPLOY** until methodology debugged

**Next Step:** Option A (Debug Phase 4 Methodology) is MANDATORY before proceeding.

---

## Files Created

1. ✅ `scripts/wall_top5_patterns_backtest.py` - Top 5 pattern backtest engine
2. ✅ `CG_mnq_top5_patterns_backtest_results.csv` - Trade-by-trade results
3. ✅ `docs/PHASE_4_BACKTEST_RESULTS.md` - This analysis document

---

**Author:** McDuff Framework + Claude Implementation
**Timestamp:** 2026-05-05 00:15 ET
**Status:** Investigation required before deployment
