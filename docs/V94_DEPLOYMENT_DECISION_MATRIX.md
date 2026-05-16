# ClanMarshal v9.4 Deployment Decision Matrix

**Purpose:** Daily pre-flight regime check to determine deployment stance
**When to run:** Before RTH open (8:00 AM CT) using prior day's regime metrics

---

## Decision Tree

```
START: Query prior day's regime metrics
   ↓
   ├─ directional_efficiency <= 0.70?
   │    ├─ YES → Continue to vol_ratio check
   │    └─ NO → ⚠️ SUPPRESS (trending day expected)
   │
   ├─ vol_ratio_5d <= 2.0?
   │    ├─ YES → ✅ DEPLOY FULL (optimal conditions)
   │    └─ NO → ⚠️ SUPPRESS (high volatility expected)
   │
   └─ Both conditions met → ✅ DEPLOY FULL
```

---

## Regime Classification Reference

### ✅ DEPLOY FULL (Optimal Conditions)
| Regime | dir_eff Range | vol_ratio Range | Historical Avg PnL/Day | Trade Count |
|--------|---------------|-----------------|------------------------|-------------|
| **VOL_COMPRESSION** | < 0.30 | < 0.60 | +33.67 pts | 5 trades (best) |
| **RANGE** | 0.25 - 0.50 | 0.60 - 1.50 | Variable | 23 trades (most common) |

**Characteristics:**
- Low directional efficiency (markets chopping, not trending)
- Normal to compressed volatility (walls hold, not broken)
- Order book structure remains stable
- Thin bid/ask walls provide reliable entry signals

### ⚠️ SUPPRESS (Poor Conditions)
| Regime | dir_eff Range | vol_ratio Range | Historical Avg PnL/Day | Risk |
|--------|---------------|-----------------|------------------------|------|
| **TREND_UP** | > 0.60 | 0.80 - 1.20 | +0.50 pts | Walls steamrolled by buying pressure |
| **TREND_DOWN** | > 0.50 | 1.00 - 1.20 | Variable | Walls steamrolled by selling pressure |
| **VOL_EXPANSION** | Any | > 2.00 | N/A (filtered out) | Whipsaw risk, unstable book |
| **REVERSAL** | < 0.25 | > 1.50 | N/A (filtered out) | Chaotic transitions |

**Characteristics:**
- High directional efficiency (strong directional conviction)
- Markets trending, walls get run through
- Order book structure unreliable
- High volatility creates execution risk

---

## Daily Pre-Flight Checklist

### Step 1: Query Prior Day Metrics (7:55 AM CT)
```sql
SELECT
    trade_date,
    directional_efficiency,
    vol_ratio_5d,
    regime,
    CASE
        WHEN directional_efficiency <= 0.70 AND vol_ratio_5d <= 2.0 THEN '✅ DEPLOY FULL'
        ELSE '⚠️ SUPPRESS'
    END AS deployment_decision
FROM CG_mnq_cm_v93_daily_regime_report
ORDER BY trade_date DESC
LIMIT 1;
```

### Step 2: Interpret Results
- **✅ DEPLOY FULL:** Enable ClanMarshal_v94 strategy in NT8 Control Center
- **⚠️ SUPPRESS:** Disable strategy, wait for next trading day

### Step 3: Log Decision
- Record deployment decision in trading journal
- Note regime type and metrics for post-trade analysis

---

## Example Scenarios

### Scenario A: Ideal Deployment Day
```
Date: 2025-10-13
directional_efficiency: 0.616
vol_ratio_5d: 0.56
regime: VOL_COMPRESSION
deployment_decision: ✅ DEPLOY FULL

Outcome: 3 trades, +25.75 pts, 100% WR
```

### Scenario B: Trending Day (Suppress)
```
Date: 2025-10-17
directional_efficiency: 0.642
vol_ratio_5d: 0.91
regime: TREND_UP
deployment_decision: ⚠️ SUPPRESS

Actual (if deployed): 1 trade, +14.25 pts (lucky, but risky)
```

### Scenario C: Choppy Range Day (Deploy)
```
Date: 2025-10-16
directional_efficiency: 0.495
vol_ratio_5d: 0.99
regime: RANGE
deployment_decision: ✅ DEPLOY FULL

Outcome: 3 trades, +40.50 pts, 66.67% WR
```

### Scenario D: High Volatility (Suppress)
```
Date: [Hypothetical]
directional_efficiency: 0.45
vol_ratio_5d: 2.35
regime: VOL_EXPANSION
deployment_decision: ⚠️ SUPPRESS

Reason: vol_ratio > 2.0 creates whipsaw risk
```

---

## Historical Deployment Statistics

### v9.4 Validation Period (Sept 24 - Oct 22, 2025)

| Decision | Days | Trades | Total PnL | Avg PnL/Day | Win Rate |
|----------|------|--------|-----------|-------------|----------|
| **✅ DEPLOY** | 16 | 36 | +442.75 pts | +27.67 pts | 69.44% |
| **⚠️ SUPPRESS** | 3 | 0 | 0 pts | 0 pts | N/A |

**Accuracy:** 84.2% of days met deployment criteria (16 of 19)

### Breakdown by Regime (Deployed Days Only)
| Regime | Days | Trades | Avg PnL/Day | Notes |
|--------|------|--------|-------------|-------|
| RANGE | 9 | 23 | +14.86 pts | Most common, consistent |
| VOL_COMPRESSION | 2 | 5 | +14.00 pts | Best conditions, limited sample |
| TREND_DOWN | 2 | 5 | +15.13 pts | Edge case, marginal compliance |
| TREND_UP | 1 | 1 | +14.25 pts | Edge case, near threshold |
| No regime | 2 | 2 | +133.25 pts | Includes Oct 12 monster (+269.75) |

**Note:** 2 TREND days (Sept 24, Oct 3) had dir_eff near 0.70 threshold but were historically included in v9.4. Future deployments should strictly enforce dir_eff <= 0.70.

---

## Real-Time Monitoring (Intraday)

### Kill-Switch Conditions (Auto-Shutdown)
Even on DEPLOY days, monitor these thresholds:

1. **Daily Loss Limit:** -30 pts
   - Action: Shutdown strategy, close any open position at market
   - Alert: Send SMS/email notification

2. **Max Drawdown Limit:** -100 pts from equity peak
   - Action: Shutdown strategy, close any open position at market
   - Alert: Send urgent notification

3. **Force Rank Degradation:** Impossible in v9.4 (all trades >= 0.94 by design)
   - No action needed

### Position Monitoring (Continuous)
- Verify Position.MarketPosition == Flat before every entry
- Verify no pending orders before evaluating new signals
- Log every entry with force_rank, structural_case, timestamp
- Track intraday equity vs intraday peak for DD monitoring

---

## Regime Calculation Details

### Directional Efficiency Formula
```
directional_efficiency = abs(close - open) / (high - low)
```
- Range: 0.0 (pure chop) to 1.0 (perfect trend)
- **Deploy threshold:** <= 0.70 (allow some trending, but not strong trends)

### Volatility Ratio Formula
```
vol_ratio_5d = day_range_pts / avg_range_5d
```
- Range: 0.0 (compressed) to 3.0+ (explosive)
- **Deploy threshold:** <= 2.0 (avoid high volatility)

### Data Source
- Built from `CG_mnq_book_proxy_100ms` (cleaned, spread 0.25-2.0)
- Daily OHLC aggregated from 100ms buckets
- 5-day rolling average for volatility context

---

## Override Protocol

### Manual Override: DEPLOY on SUPPRESS day
**Allowed if:**
- Trader observes intraday regime shift (trending → ranging)
- Market opens in compression despite prior day trend
- **Requirement:** Reduce position size to 0.5 MNQ (if possible) or use tighter stop (-15 pts daily loss)

### Manual Override: SUPPRESS on DEPLOY day
**Allowed if:**
- Trader observes unusual market conditions (news event, low liquidity)
- Personal discretion suggests heightened risk
- **Requirement:** No penalty, always err on side of caution

---

## Long-Term Adaptation

### Quarterly Regime Review
- Analyze regime performance every 90 days
- Adjust dir_eff threshold if market character changes (e.g., 0.70 → 0.65 if trends become more common)
- Adjust vol_ratio threshold if volatility regime shifts

### Regime Drift Detection
- Monitor 30-day rolling avg of directional_efficiency
- If avg dir_eff consistently > 0.60, markets are trending more → tighten threshold
- If avg dir_eff consistently < 0.40, markets are ranging more → can loosen threshold

---

## Summary

**ClanMarshal v9.4 is a regime-aware wall strategy that requires:**
1. Low directional efficiency (dir_eff <= 0.70) - avoid trending markets
2. Normal volatility (vol_ratio <= 2.0) - avoid chaotic conditions

**Daily routine:**
- Query prior day metrics before RTH open
- Deploy if both conditions met, suppress otherwise
- Monitor kill-switches intraday even on deploy days

**Historical validation:**
- 84.2% of days met deployment criteria
- 69.44% win rate on deployed days
- +27.67 pts avg PnL per deployed day

**Status:** ✅ DECISION MATRIX VALIDATED FOR PRODUCTION USE

---

**Date:** May 1, 2026
**Version:** v9.4 Blueprint
**Next Review:** After 50 live trades or 90 calendar days
