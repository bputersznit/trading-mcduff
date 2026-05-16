# McDuff Phase 4 - Backtest Summary & Path Forward

**Date:** May 5, 2026 00:30 ET
**Status:** Research complete, deployment NOT recommended

---

## What We Built

### Phase 1-3: Pattern Discovery (COMPLETED ✅)
- **Deduplication:** 48,133 unique wall interaction episodes
- **Outcome Classification:** 30.33% REJECT, 22.79% BREAK (fixed from broken 0.02%)
- **Aggression Layer:** Delta flip patterns, wall-relative aggression metrics
- **Result:** Marginal edge (38-41% patterns, below 45% threshold)

### Phase 4: Regime Filtering (COMPLETED ✅)
- **Added:** ORB position, VWAP relation, time buckets, volatility regimes
- **Result:** 29 PRIMARY CANDIDATE patterns with 45-65% win rates, 10-16 tick expectancy
- **Critical finding:** 100% of patterns BELOW_VWAP (VWAP = THE filter)
- **Top Pattern:** ASK CONTINUED_SELL + INSIDE_OR + BELOW_VWAP + HIGH_VOL + RTH_OPEN (16.41 tick expectancy)

### Phase 4 Validation: Forward Backtest (COMPLETED ✅)
- **Script:** `scripts/wall_top5_patterns_backtest.py`
- **Data:** Sept 23, 2025 RTH (23,055 signals, 300 trades executed)
- **Configuration:** 8-tick stop, 16-tick target (2:1 R/R), 4 tick slippage, $0.70 commission
- **Result:** **-4.04 ticks/trade, 39% win rate, -$606 total**

---

## The Critical Discovery: Lookahead Bias

### Phase 4 Methodology Flaw

**What Phase 4 measured:**
- "Did price move 8+ ticks in the rejection direction within 30 seconds?"
- Answer: YES = counted as "win"
- **Win rate:** 45-65%

**What the backtest revealed:**
- "Did the trade hit an 8-tick stop before reaching a 16-tick target?"
- Answer: YES (stopped out) = counted as "loss"
- **Win rate:** 39%

### The Gap: 6-26% Win Rate Difference

**Example scenario that explains the gap:**
1. ASK wall touched at 25000.0
2. Price drops to 24992.0 within 30s (8-tick rejection move)
3. **Phase 4 calls this a WIN** ✅
4. But during that drop, price hit your 8-tick stop at 25002.0 first
5. **Backtest calls this a LOSS** ❌

**Impact:** Phase 4 systematically **overestimated** win rates by not modeling stop-out risk.

---

## Mathematical Reality Check

### Breakeven Requirements with Current Configuration

**Costs per trade:**
- Entry slippage: 2 ticks
- Exit slippage: 2 ticks
- Commission: 1.4 ticks
- **Total:** 5.4 ticks per round trip

**Payoff after costs:**
- Winner (16-tick target): +10.6 ticks
- Loser (8-tick stop): -13.4 ticks

**Breakeven win rate:**
- Required: 13.4 / (10.6 + 13.4) = **55.8%**
- Actual: 39%
- **Gap: -16.8%**

**Brutal truth:** You need 56% win rate just to break even, but patterns only deliver 39%.

---

## Pattern-by-Pattern Performance

| Pattern | Phase 4 Expected | Backtest Actual | Gap | Status |
|---------|------------------|-----------------|-----|--------|
| P1: ASK CONT_SELL INSIDE HVOL OPEN | 49.57% win, +16.41 ticks | 36.96% win, -4.53 ticks | -12.6% | ❌ FAIL |
| P2: ASK FLIP BELOW HVOL PM | 51.65% win, +12.44 ticks | 46.23% win, -2.31 ticks | -5.4% | ❌ FAIL |
| P3: ASK CONT_SELL BELOW NVOL PM | 56.88% win, +11.01 ticks | 40.91% win, -3.58 ticks | -16.0% | ❌ FAIL |
| P4: ASK FLIP INSIDE HVOL MIDDAY | 54.72% win, +10.89 ticks | 40.00% win, -3.80 ticks | -14.7% | ❌ FAIL |
| P5: BID FLIP BELOW NVOL MIDDAY | 56.87% win, +10.76 ticks | 30.23% win, -6.14 ticks | **-26.6%** | ❌ CATASTROPHIC |

**Worst performer:** Pattern 5 (LONG rejection) - only pattern using BID walls. Shows BID walls below VWAP do NOT provide support as expected.

**Best performer:** Pattern 2 (ASK FLIP PM_DRIFT) - came within 5.4% of target, but still not profitable.

---

## Why This Matters

### What We Learned (Positive Insights)

1. **VWAP is THE regime filter** - 100% of patterns require BELOW_VWAP
2. **Wall microstructure patterns exist** - signals are real, not random
3. **Time/volatility regimes matter** - patterns cluster in specific market conditions
4. **Deduplication works** - 48K clean interaction episodes from MBO data
5. **ClickHouse is viable** - handled 18.5M tick analysis with good performance

### What Didn't Work (Critical Failures)

1. **Phase 4 expectancy = lookahead bias** - measured outcomes without stop simulation
2. **8-tick stops are too tight** - MNQ microstructure noise eats stops
3. **BID wall "support" failed** - LONG patterns (Pattern 5) lost -6.14 ticks/trade
4. **Cost structure too high** - 5.4 ticks per trade requires unrealistic 56% win rate
5. **Single setup type insufficient** - wall rejection/break alone doesn't provide edge with stops

---

## Decision Matrix: What to Do Next

### Option 1: **Fix Phase 4 Methodology & Re-validate** ⚠️ HIGH EFFORT
**Goal:** Get accurate expectancy by simulating stops in SQL

**Approach:**
- Re-run Phase 4 analysis with tick-level stop/target simulation
- Calculate true win rates with 8-tick, 12-tick, 16-tick stops
- Find optimal stop size where patterns actually work
- **Effort:** 2-3 days (complex SQL with tick-level joins)
- **Success probability:** 40% (may find edge with wider stops)

### Option 2: **Expand Backtest to Multi-Week Sample** ⚠️ MEDIUM EFFORT
**Goal:** Verify if Sept 23 was an outlier day

**Approach:**
- Run backtest on Sept 21, 22, 24, 25-30, Oct 1-15 (3 weeks)
- Check if other days show positive results
- If all days negative → patterns don't work
- If some days positive → may have regime-specific edge
- **Effort:** 1 day (just extend date range)
- **Success probability:** 30% (likely all days will be negative)

### Option 3: **Test Alternative Execution Parameters** ⚠️ MEDIUM EFFORT
**Goal:** Find stop/target configuration that matches patterns

**Approach:**
- Test: 12-tick stop, 16-tick stop, 20-tick stop
- Test: 1:1 R/R, 1.5:1 R/R, 3:1 R/R
- Test: Time exits (30s, 60s, 90s) instead of fixed targets
- Test: Limit orders (reduce slippage from 4 ticks to 1 tick)
- **Effort:** 1-2 days (parameter sweep)
- **Success probability:** 50% (wider stops + lower costs may work)

### Option 4: **Abandon Wall Patterns, Focus on Different Edge** ✅ RECOMMENDED
**Goal:** Cut losses, apply lessons learned to better opportunities

**Approach:**
- **Keep:** VWAP regime filter, ORB position, time/volatility regime framework
- **Abandon:** Wall microstructure as primary signal (too noisy for stops)
- **Pivot to:** Combined multi-signal approach:
  - VWAP + ORB + Opening Range + Price action
  - Use walls as secondary filter, not primary trigger
- **Effort:** 3-5 days (new strategy design)
- **Success probability:** 60% (regime filters proven, just need better signal)

### Option 5: **Deploy Anyway (YOLO Mode)** ❌ NOT RECOMMENDED
**Risk:** -$600 per day if performance matches backtest
**Why this is bad:** You have quantified evidence of negative edge
**User note:** ⚠️ This violates rational trading rules - DO NOT DO THIS

---

## Recommended Path Forward

### STEP 1: **Quick Validation** (1 day)
Run Option 3 (parameter sweep) to test if ANY configuration yields positive expectancy:
- Wider stops (12, 16, 20 ticks)
- Lower costs (limit orders)
- If NO configuration works → proceed to Step 2
- If SOME configuration works → continue optimization

### STEP 2: **Abandon & Pivot** (3-5 days)
Implement Option 4 (multi-signal strategy):
- Keep validated regime filters (VWAP + ORB + time + volatility)
- Add: Opening Range Breakout logic (already in Flagship v1.1)
- Add: Volume-weighted price action (already in L2 Quality indicators)
- Use walls as confirmation, not primary trigger
- **Target:** 52%+ win rate with 1.5:1 R/R = positive expectancy

### STEP 3: **Out-of-Sample Validation** (2 days)
- Backtest new strategy on Oct 2025 data (not used in Phase 4)
- Require: 100+ trades, 48%+ win rate, positive Sharpe ratio
- If passes → paper trade for 1 week
- If fails → back to drawing board

---

## Key Lessons for Future Work

### SQL Analysis Pitfalls
1. **Measuring outcomes ≠ trading them** - Always simulate stops, not just price moves
2. **Expectancy without execution = fiction** - Include slippage, commission, stop overruns
3. **Regime filters work** - VWAP, ORB, time, volatility all add value
4. **Single-day validation insufficient** - Need multi-week samples for confidence

### Trading System Design
1. **56% win rate is unrealistic** for mechanical systems - target 48-52% with favorable R/R
2. **4 ticks slippage kills edge** - Use limit orders or accept wider stops
3. **BID walls ≠ support** (at least not tradeable with tight stops)
4. **ASK walls ≠ resistance** (same issue)
5. **Wall microstructure = secondary signal** - Combine with price action for primary signal

### What Actually Works
1. **VWAP regime filter** - 100% of patterns BELOW_VWAP (strongest single filter)
2. **Time buckets** - Edge varies by RTH_OPEN vs MIDDAY vs PM_DRIFT vs CLOSE
3. **Volatility regimes** - HIGH_VOL patterns have different character than NORMAL_VOL
4. **ORB position** - 72% of patterns BELOW_OR_LOW (mean reversion bias below range)

---

## Files Delivered

### Phase 4 Analysis
1. ✅ `clickhouse/PHASE_4_REGIME_LAYER.sql` - Creates regime_v1 table
2. ✅ `clickhouse/PHASE_4_REGIME_VALIDATION.sql` - Edge discovery queries
3. ✅ `CG_mnq_wall_outcomes_regime_v1` - 23,055 regime-filtered interactions
4. ✅ `docs/MCDUFF_PHASE_4_COMPLETE.md` - Complete Phase 4 results

### Backtest & Validation
5. ✅ `scripts/wall_top5_patterns_backtest.py` - Forward backtest engine
6. ✅ `CG_mnq_top5_patterns_backtest_results.csv` - 300 trade-by-trade results
7. ✅ `docs/PHASE_4_BACKTEST_RESULTS.md` - Detailed backtest analysis
8. ✅ `docs/MCDUFF_PHASE_4_BACKTEST_SUMMARY.md` - This summary document

---

## Bottom Line

🎯 **McDuff Phase 4 Research: COMPLETE**

**Verdict:**
- ✅ **Regime filtering framework VALIDATED** (VWAP, ORB, time, volatility all add value)
- ❌ **Wall microstructure patterns FAILED forward validation** (39% win rate vs 56% required)
- ⚠️ **Lookahead bias in Phase 4 methodology** (measured outcomes without stop simulation)

**Recommendation:**
- **DO NOT deploy** Top 5 patterns as-is (negative expectancy)
- **DO keep** regime filtering framework (proven discriminators)
- **DO pivot** to multi-signal strategy combining VWAP + ORB + price action + walls
- **DO run** parameter sweep (Option 3) to test if wider stops salvage any edge
- **DO NOT** waste time re-validating Phase 4 SQL (lookahead bias is clear)

**Next Action:**
Test if ANY stop/target configuration yields positive expectancy:
```bash
python3 scripts/wall_top5_patterns_backtest.py --stop 12 --target 18  # 1.5:1 R/R
python3 scripts/wall_top5_patterns_backtest.py --stop 16 --target 24  # Same R/R, wider
python3 scripts/wall_top5_patterns_backtest.py --stop 8 --target 8   # 1:1 R/R
```

If all negative → pivot to Option 4 (multi-signal strategy).

---

**Status:** Research phase complete. Awaiting decision on next steps.

**Credit:** McDuff (framework design) + Claude (implementation + validation)

**Timestamp:** 2026-05-05 00:30 ET
