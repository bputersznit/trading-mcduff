# ClanMarshal v9.4 - Quick Reference Card

**Print and keep at trading desk**

---

## Daily Pre-Flight Checklist (8:00 AM CT)

### 1. Regime Check Query
```bash
clickhouse-client --query "
SELECT directional_efficiency, vol_ratio_5d,
  CASE WHEN directional_efficiency <= 0.70 AND vol_ratio_5d <= 2.0
    THEN '✅ DEPLOY' ELSE '⚠️ SUPPRESS' END AS decision
FROM CG_mnq_cm_v93_daily_regime_report
ORDER BY trade_date DESC LIMIT 1 FORMAT Pretty;"
```

### 2. Decision Matrix
| dir_eff | vol_ratio | Action |
|---------|-----------|--------|
| ≤ 0.70  | ≤ 2.0     | ✅ **DEPLOY** - Set RegimeCheckPassed=TRUE |
| > 0.70  | Any       | ⚠️ **SUPPRESS** - Leave RegimeCheckPassed=FALSE |
| Any     | > 2.0     | ⚠️ **SUPPRESS** - Leave RegimeCheckPassed=FALSE |

### 3. Enable Strategy (9:25 AM if DEPLOY)
- NT8 Control Center → Strategies → ClanMarshal_v94
- Verify RegimeCheckPassed = TRUE
- Right-click → Enable

---

## Key Parameters

| Parameter | Value | Purpose |
|-----------|-------|---------|
| **ForceRankThreshold** | 0.94 | Apex quality filter |
| **MaxDirectionalEfficiency** | 0.70 | Avoid trending days |
| **MaxVolatilityRatio** | 2.0 | Avoid chaotic days |
| **ThinWallMaxVolume** | 10 | L2 thin wall threshold |
| **DailyLossLimit** | 30 pts | Auto-shutdown threshold |
| **MaxDrawdownLimit** | 100 pts | Auto-shutdown threshold |

---

## Kill-Switch Monitoring

### Real-Time Alerts
- **Daily Loss -25 pts:** ⚠️ Early warning (shutdown at -30)
- **Drawdown -80 pts:** ⚠️ Early warning (shutdown at -100)
- **Position Count > 1:** 🚨 CRITICAL ERROR - Disable immediately

### Auto-Shutdown Triggers
1. Daily loss ≥ -30 pts
2. Drawdown from peak ≥ -100 pts
3. Position count > 1 (manual intervention required)

---

## Expected Performance

| Metric | Target | Acceptable Range |
|--------|--------|------------------|
| **Trades/Day** | 1.89 | 0-4 trades |
| **Win Rate** | 69.44% | 60-75% |
| **Expectancy** | 12.30 pts | 8-15 pts |
| **Max Drawdown** | -12 pts | 0 to -30 pts |
| **Profit Factor** | 13.56 | 5.0 - 20.0 |

---

## Archetype Distribution (Expected)

| Archetype | % of Trades | % of PnL | Win Rate |
|-----------|-------------|----------|----------|
| **LONG_P9990** (Apex Sniper) | 8% | 61% | 100% |
| **SHORT_P9900** (Workhorse) | 42% | 23% | 67% |
| **LONG_P9900** (Supplemental) | 44% | 16% | 69% |
| **SHORT_P9990** (Opportunistic) | 6% | 0% | 50% |

---

## L2 Data Quality Check

### Verify During Trading
```
Output Window should show:
"LONG entry submitted. BestAskVol: 7, Spread: 0.25"
```

### Troubleshooting
- **BestAskVol always 0?** → L2 subscription inactive
- **Spread > 2.0 frequently?** → Market conditions changed
- **No trades for 3+ days?** → Check thin wall threshold (increase to 15)

---

## End-of-Day Routine (4:05 PM CT)

### 1. Disable Strategy
- Auto-disables at RTH close
- Verify disabled in Control Center

### 2. Review Performance
- Trades taken: ___
- Winners: ___ | Losers: ___
- Gross PnL: ___ pts
- Realistic PnL (after friction): ___ pts
- Running Equity: ___ pts
- Drawdown from Peak: ___ pts

### 3. Compare to v9.4 Expectations
- Win rate in range? (60-75%)
- Expectancy in range? (8-15 pts)
- Trade count reasonable? (0-4/day)

### 4. Update Trading Journal
- Regime (dir_eff, vol_ratio): ___
- Deploy decision: DEPLOY / SUPPRESS
- Anomalies/Notes: ___

---

## Emergency Procedures

### Situation 1: Position Count > 1
**Action:** Disable strategy IMMEDIATELY → Flatten all positions manually → Review logs → Contact support

### Situation 2: Daily Loss Approaching -30 pts
**Action:** Prepare for auto-shutdown → Review trades → Decide if manual disable earlier

### Situation 3: Multiple Losing Days
**Action:** After 3 consecutive daily losses → Disable strategy → Compare trades to v9.4 reference → Review regime accuracy

### Situation 4: No Trades for 5+ Deployed Days
**Action:** Check L2 data quality → Increase ThinWallMaxVolume to 15 → Lower ForceRankThreshold to 0.90

---

## Quick Commands

### VPS Deployment
```bash
sudo cp ~/trading4/CG_MNQ_MarketReplayLab/ninjascript/ClanMarshal_v94.cs \
  /mnt/vps_strategies/ClanMarshal_v94.cs
```

### Daily Regime Check
```bash
clickhouse-client --query "SELECT directional_efficiency, vol_ratio_5d FROM CG_mnq_cm_v93_daily_regime_report ORDER BY trade_date DESC LIMIT 1 FORMAT Pretty;"
```

### Last 10 v9.4 Reference Trades
```bash
clickhouse-client --query "SELECT entry_time, signal_side, pnl_pts, regime FROM CG_mnq_cm_v94_filtered_production_backtest ORDER BY entry_time DESC LIMIT 10 FORMAT Pretty;"
```

---

## Contact Information

**Documentation:**
- Full validation: `docs/CLANMARSHAL_V94_VALIDATION_COMPLETE.md`
- Deployment guide: `docs/V94_DEPLOYMENT_GUIDE.md`
- Decision matrix: `docs/V94_DEPLOYMENT_DECISION_MATRIX.md`

**Code Location:**
- Local: `ninjascript/ClanMarshal_v94.cs`
- VPS: `/mnt/vps_strategies/ClanMarshal_v94.cs`

**Data Tables:**
- Regime: `CG_mnq_cm_v93_daily_regime_report`
- Reference trades: `CG_mnq_cm_v94_filtered_production_backtest`
- Sequential validation: `CG_mnq_cm_v94_true_sequential_backtest`

---

**Version:** v9.4 Blueprint
**Validation:** 36 trades, 442.75 pts, 69.44% WR, 13.56 PF
**Deployment:** May 3, 2026
**Status:** ✅ APPROVED FOR LIVE TRADING (after Market Replay validation)

**CRITICAL:** NEVER trade without daily regime check. Strategy suppressed by default.
