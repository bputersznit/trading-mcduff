# McDuff's Roadmap - Current Progress

**Date:** May 4, 2026
**Status:** Step 2 in progress (aggression enrichment running)

---

## McDuff's Critique Summary

> "The framework is directionally strong, but not yet strategy-ready."

### Key Issues Identified

1. **Production-ready is overstated** - Current table (17 columns) lacks aggression, response labels, and outcome classification
2. **100K interactions too narrow** - Only Sept 21-24 (4 days) instead of full Sept 21 - Oct 22 (28 days)
3. **97,763 interactions lack lifecycle classification** - Sparse behavioral signal (only 1,254 REPLENISHING + 933 PULLED + 50 ICEBERG)
4. **Missing aggression join** - Wall behavior alone is weak; need aggression into/away from wall
5. **Forward response is decisive** - Need MFE/MAE at 10s/30s/60s + break/reject labels

---

## Actual Current State

### ✅ What Exists

| Component | Status | Details |
|-----------|--------|---------|
| **Source Data** | ✅ Complete | 789M MBO events, 28 days (Sept 21 - Oct 22) |
| **Heatmap** | ✅ Complete | 132.65M buckets at 100ms granularity |
| **Liquidity Walls** | ✅ Complete | 6.45M walls detected (P90-P99.9) |
| **Wall Lifecycle** | ✅ Complete | 10.74K lifecycle tracks |
| **Aggression System** | ✅ Complete | 5.62M aggression buckets |
| **Base Interactions** | ⚠️ Partial | 100K interactions (4 days only) |
| **Response Metrics** | ✅ Complete | 100K rows with MFE/MAE/outcome_basic |
| **Aggression Metrics** | ⏳ Running | JOIN-optimized query in progress |
| **Enriched Master** | 📝 Ready | SQL script created, awaiting aggression completion |

### ❌ What's Missing (Per McDuff)

1. **Aggression enrichment** - Running now (ETA: 2-5 minutes)
2. **Enriched join table** - Ready to execute after Step 2 completes
3. **Advanced outcome classification** - Included in Step 3 SQL (6 tradeable patterns)
4. **Coverage expansion** - Need to rebuild with 28 days instead of 4
5. **Regime filters** - Not yet added (ORB, VWAP, ATR, trend, session time)
6. **Wall-zone clustering** - Not yet built
7. **Backtest framework** - Not yet created
8. **NinjaScript implementation** - Waiting for validation

---

## Current Outcome Distribution (Response Table)

| Outcome | Count | % | Avg MFE 10s | Avg MAE 10s |
|---------|-------|---|-------------|-------------|
| **BREAK_SUPPORT** | 50,359 | 50.36% | 297.47 | -379.09 |
| **BREAK_RESISTANCE** | 49,583 | 49.58% | 294.51 | -376.73 |
| **UNCLEAR** | 35 | 0.04% | 113.71 | -162.69 |
| **REJECT_SUPPORT** | 14 | 0.01% | 654.36 | -0.29 |
| **REJECT_RESISTANCE** | 9 | 0.01% | 1.11 | -409.56 |

**Problem:** 99.94% classified as "BREAK" - confirms McDuff's critique about sparse behavioral signals.

---

## McDuff's Recommended Roadmap

### NOW ✅ (In Progress)

1. ✅ Add MFE/MAE response metrics → **DONE** (response_v1 table)
2. ⏳ Add aggression metrics → **RUNNING** (JOIN-optimized query)
3. 📝 Join into enriched interaction table → **SQL READY**
4. 📝 Classify outcomes → **Included in Step 3 SQL**

### THEN (Next 1-2 days)

5. Run statistical edge reports
6. Backtest individual pattern families:
   - ABSORB_REVERSE
   - PULL_THEN_BREAK
   - ICEBERG_REJECT
   - REPLENISHING_HOLD
7. Compare to ClanMarshal v9.4 baseline
8. Select top 1-2 strategies

### ONLY AFTER THAT (If edge validated)

9. Generate NinjaScript
10. Add OCO++ bracket protection
11. Replay validate against ClickHouse
12. Paper/live 1 MNQ deployment

---

## Immediate Next Steps

### Step 2: Aggression Enrichment (⏳ Running)
- **File:** `ENRICH_STEP_2_JOIN_OPTIMIZED.sql`
- **Approach:** LEFT JOIN with time windows (instead of 1.2M subqueries)
- **Output:** `CG_mnq_wall_interaction_aggression_v1`
- **Fields Added:**
  - `buy_volume_before_5s`, `sell_volume_before_5s`, `delta_before_5s`
  - `buy_volume_after_5s`, `sell_volume_after_5s`, `delta_after_5s`
  - `buy_volume_at_wall`, `sell_volume_at_wall`, `total_volume_at_wall`
  - `aggression_into_wall`, `aggression_away_from_wall`
  - `delta_flip_pattern`, `aggression_classification`
  - `peak_aggression_score`

### Step 3: Enriched Master Table (📝 Ready)
- **File:** `ENRICH_STEP_3_MASTER_TABLE.sql`
- **Output:** `CG_mnq_wall_interactions_enriched_v1`
- **Approach:** INNER JOIN base + response + aggression
- **New Fields:**
  - `absorption_score` (aggression / price move)
  - `price_efficiency` (price move / volume)
  - `wall_strength` (iceberg/replenishing indicator)
  - `pull_break_score` (pull behavior × continuation)
  - `iceberg_fade_score` (replenishment × fade opportunity)
  - **`pattern_classification`** (6 patterns):
    1. ABSORB_REVERSE
    2. PULL_THEN_BREAK
    3. ICEBERG_REJECT
    4. REPLENISHING_HOLD
    5. CONSUMED_BREAK
    6. NO_EDGE
  - **`expectancy_hint`** (POSITIVE_EXPECTANCY / BREAKOUT_EDGE / NO_CLEAR_EDGE)

### Step 4: Expand Coverage (After Step 3)
- Rebuild `wall_interactions` with full 28-day range
- Currently: 4 days (Sept 21-24)
- Target: 28 days (Sept 21 - Oct 22)
- Estimate: ~700K - 1M interactions (instead of 100K)

### Step 5: Statistical Analysis
- Query enriched table for edge validation
- Pattern-by-pattern expectancy analysis
- Day-by-day robustness check
- High-vol vs low-vol regime comparison

---

## Critical Questions to Answer (Per McDuff)

1. ✅ **Do P99 walls act as support/resistance?**
   - Can compare ICEBERG_REJECT vs BREAK patterns in enriched table

2. ✅ **Does pulling predict breakouts?**
   - Can filter `pull_break_score > threshold` and measure MFE

3. ✅ **Does absorption lead to reversals?**
   - Can analyze `absorption_score` for ABSORB_REVERSE pattern

4. ✅ **Are icebergs tradeable fade opportunities?**
   - Can check `iceberg_fade_score` and rejection rates

5. ✅ **What aggression level breaks walls?**
   - Can compare `aggression_into_wall` for BREAK vs REJECT outcomes

6. ⏸️ **Regime dependency?**
   - Need to add ORB, VWAP, trend filters (not yet implemented)

---

## Performance Notes

- **Original aggression script** (ENRICH_STEP_2_MINIMAL.sql): **TIMED OUT**
  - Used 100K × 4 subqueries with `dateDiff` = 400K queries
  - Classic "CTE monster" problem McDuff warned about

- **Optimized aggression script** (ENRICH_STEP_2_JOIN_OPTIMIZED.sql): **RUNNING**
  - Uses LEFT JOIN with time window filtering
  - Single query with partition pruning
  - Should complete in 2-5 minutes (vs timeout)

---

## Comparison to ClanMarshal v9.4 Baseline

| Metric | v9.4 Baseline | Wall Framework (Target) |
|--------|---------------|------------------------|
| **Approach** | Simplified L2 thin walls | Full Bookmap microstructure |
| **Trades** | 36 over 19 days | Est. 100-300 (TBD) |
| **PnL** | 442.75 pts gross | TBD (validation needed) |
| **Win Rate** | 69.44% | TBD |
| **Profit Factor** | 13.56 | TBD |
| **Edge** | Thin walls + regime | Wall behavior + aggression |

**Decision Rule:** Only implement if enriched framework beats v9.4 baseline.

---

## Files Created Today

### SQL Scripts
1. ✅ `ENRICH_STEP_2_JOIN_OPTIMIZED.sql` - Aggression enrichment (running)
2. ✅ `ENRICH_STEP_3_MASTER_TABLE.sql` - Enriched join + pattern classification (ready)

### Documentation
1. ✅ `MCDUFF_ROADMAP_PROGRESS.md` - This file

### Next Files Needed
1. `ENRICH_STEP_4_COVERAGE_EXPANSION.sql` - Rebuild with 28 days
2. `ANALYSIS_PATTERN_EXPECTANCY.sql` - Statistical edge validation
3. `BACKTEST_ABSORB_REVERSE.sql` - Pattern family backtest
4. `BACKTEST_PULL_THEN_BREAK.sql` - Pattern family backtest
5. `BACKTEST_ICEBERG_REJECT.sql` - Pattern family backtest

---

## Timeline Estimate

| Task | ETA | Status |
|------|-----|--------|
| Step 2 (Aggression) | 2-5 min | ⏳ Running |
| Step 3 (Enriched Table) | 1-2 min | 📝 Ready |
| Step 4 (Expand Coverage) | 10-15 min | 📋 Planned |
| Step 5 (Statistical Analysis) | 1-2 hours | 📋 Planned |
| Step 6 (Backtest Patterns) | 4-8 hours | 📋 Planned |
| Step 7 (Strategy Selection) | 2-4 hours | 📋 Planned |
| Step 8 (NinjaScript) | 1-2 weeks | ⏸️ Only if validated |

**Total to validation:** ~1-2 days
**Total to deployment:** 1-2 weeks (if edge confirmed)

---

## Bottom Line

**We are at Step 2 of McDuff's roadmap** (aggression enrichment running now).

The foundation is solid:
- ✅ 789M MBO events
- ✅ 132.65M heatmap buckets
- ✅ 6.45M walls
- ✅ 100K interactions with response metrics

The immediate path:
1. Finish Step 2 (aggression) - **2-5 min**
2. Execute Step 3 (enriched table) - **1-2 min**
3. Expand to 28-day coverage - **10-15 min**
4. Statistical validation - **1-2 hours**
5. Backtest top patterns - **4-8 hours**

**Only after validation** → NinjaScript implementation.

---

**Status:** Directionally strong, not yet strategy-ready (McDuff is correct).
**Path forward:** Clear and executable.
**Decision point:** Validate edge before deployment.
