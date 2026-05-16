# ClanMarshal v9.4 - NinjaTrader 8 Deployment Guide

**Date:** May 3, 2026
**Strategy:** ClanMarshal_v94.cs
**Validation:** 36 trades, 442.75 pts, -12 DD, 69.44% WR, 13.56 PF

---

## Pre-Deployment Checklist

### ✅ Required Components
- [x] NinjaTrader 8 installed on VPS (104.245.107.193)
- [x] Level 2 market depth subscription for MNQ (verified)
- [x] Data feed: CQG, Rithmic, or IQFeed with L2 support
- [x] ClickHouse database for daily regime check
- [x] ClanMarshal_v94.cs file ready in `ninjascript/` directory

### ✅ Data Requirements
- **Level 1:** Bid, Ask, Last, Volume (standard)
- **Level 2:** Market Depth (bid/ask ladder) - CRITICAL for thin wall detection
- **Historical:** Not required for live trading, optional for Market Replay testing

### ✅ Risk Acknowledgment
- **Max Position:** 1 MNQ contract (hardcoded, cannot be overridden)
- **Daily Loss Limit:** -30 pts (auto-shutdown)
- **Max Drawdown Limit:** -100 pts from peak (auto-shutdown)
- **Regime Check:** MANUAL daily verification required before enabling strategy

---

## Step 1: Deploy NinjaScript File to VPS

### Manual Deployment (Recommended)
```bash
# From your local machine:
sudo cp /home/bernard/trading4/CG_MNQ_MarketReplayLab/ninjascript/ClanMarshal_v94.cs \
  /mnt/vps_strategies/ClanMarshal_v94.cs

# Verify copy:
ls -lh /mnt/vps_strategies/ClanMarshal_v94.cs
```

**Expected output:** File size ~25-30 KB, modified date: 2026-05-03

---

## Step 2: Import Strategy into NinjaTrader 8

### On VPS (104.245.107.193):

1. **Open NinjaTrader 8** → Control Center

2. **Import NinjaScript:**
   - Click `Tools` → `Import` → `NinjaScript Add-On...`
   - Browse to: `/mnt/vps_strategies/ClanMarshal_v94.cs`
   - OR: Copy file to `C:\Users\[YourUser]\Documents\NinjaTrader 8\bin\Custom\Strategies\`

3. **Compile Strategy:**
   - Click `Tools` → `Edit NinjaScript` → `Strategy`
   - Find `ClanMarshal_v94` in the list
   - Right-click → `Compile`
   - **Verify:** Green checkmark = success, Red X = compilation error

4. **Troubleshooting Compilation Errors:**
   - Missing `MarketDepth` access: Verify L2 data subscription is active
   - Missing indicators: Strategy uses only built-in NT8 indicators (SMA)
   - Syntax errors: Re-check file wasn't corrupted during transfer

---

## Step 3: Configure Strategy Parameters

### Open Strategy Parameters:
- `Control Center` → `Strategies` → `ClanMarshal_v94` → `Configure`

### Recommended Settings (v9.4 Validated):

#### Blueprint Filters
- **Force Rank Threshold:** 0.94 (apex quality only)
- **Max Directional Efficiency:** 0.70 (suppress trending days)
- **Max Volatility Ratio:** 2.0 (suppress chaotic days)

#### Structural Detection
- **Thin Wall Max Volume:** 10 contracts (L2 threshold)
- **Support/Resistance Ticks:** 5 ticks from session high/low

#### Risk Governance
- **Daily Loss Limit:** 30 pts (auto-shutdown at -30)
- **Max Drawdown Limit:** 100 pts (auto-shutdown at -100 from peak)

#### Daily Pre-Flight
- **Regime Check Passed:** FALSE (default - must manually enable each day)

#### Realistic Tracking
- **Slippage Per Trade:** 1.5 pts (for PnL tracking only, doesn't affect orders)
- **Commission Per Trade:** $0.70 (for PnL tracking only)

### Other NT8 Settings:
- **Quantity:** Ignored (hardcoded to 1)
- **Account:** Select live/sim account
- **Time In Force:** Day
- **Start Behavior:** Wait Until Flat

---

## Step 4: Daily Pre-Flight Regime Check

**CRITICAL:** Must be performed EVERY trading day before enabling strategy.

### 4A: Query ClickHouse for Prior Day Regime
```bash
# SSH into VPS or run from local machine:
clickhouse-client --query "
SELECT
    trade_date,
    directional_efficiency,
    vol_ratio_5d,
    regime,
    CASE
        WHEN directional_efficiency <= 0.70 AND vol_ratio_5d <= 2.0
        THEN '✅ DEPLOY FULL'
        ELSE '⚠️ SUPPRESS'
    END AS deployment_decision
FROM CG_mnq_cm_v93_daily_regime_report
ORDER BY trade_date DESC
LIMIT 1
FORMAT Pretty;
"
```

### 4B: Interpret Results

**✅ DEPLOY FULL** (both conditions met):
- directional_efficiency <= 0.70
- vol_ratio_5d <= 2.0
- **Action:** Set `RegimeCheckPassed = TRUE` in NT8 strategy parameters

**⚠️ SUPPRESS** (either condition violated):
- directional_efficiency > 0.70 (trending day expected)
- vol_ratio_5d > 2.0 (high volatility expected)
- **Action:** Leave `RegimeCheckPassed = FALSE`, do NOT enable strategy

### 4C: Update NinjaTrader Parameter
1. `Control Center` → `Strategies` → `ClanMarshal_v94`
2. `Parameters` → `Daily Pre-Flight` → `Regime Check Passed`
3. Set to `TRUE` if deploy, `FALSE` if suppress
4. Click `Apply`

### 4D: Log Decision
Create a trading journal entry:
```
Date: [Today]
Regime Check:
  - Prior day dir_eff: [value]
  - Prior day vol_ratio: [value]
  - Decision: [DEPLOY/SUPPRESS]
  - Rationale: [notes]
```

---

## Step 5: Enable Strategy in Market Replay (Testing)

**BEFORE LIVE TRADING:** Validate in Market Replay first.

### 5A: Setup Market Replay Session
1. `Control Center` → `Tools` → `Market Replay`
2. Select MNQ contract
3. Load historical date with L2 data (if available)
4. Start replay at 9:00 AM CT (before RTH open)

### 5B: Enable Strategy
1. `Control Center` → `Strategies`
2. Right-click → `Enable`
3. Monitor output window for:
   - Regime check pass/fail message
   - L2 data availability
   - Entry signal detection
   - Order execution

### 5C: Verify Safety Enforcement
- **Test 1:** Attempt to enter second position → should be blocked
- **Test 2:** Hit daily loss limit → should auto-shutdown
- **Test 3:** L2 thin wall detection → verify volume thresholds work

### 5D: Review Output Log
Check for:
```
2026-05-03 - Session start. Equity: 0.00 pts
2026-05-03 09:35:15 - LONG entry submitted. Price: 21045.50, BestAskVol: 7, Spread: 0.25
2026-05-03 09:35:16 - LONG filled at 21045.75
2026-05-03 09:42:30 - Trade closed. NT PnL: 10.00 pts, Realistic PnL: 8.50 pts, Total Equity: 8.50 pts
```

**Expected behavior:** ~1-3 trades per day on DEPLOY days, 0 trades on SUPPRESS days.

---

## Step 6: Enable Strategy for Live Trading

**ONLY AFTER** successful Market Replay validation.

### 6A: Pre-Market Routine (Daily at 8:00 AM CT)
1. Run regime check query (Step 4A)
2. Update `RegimeCheckPassed` parameter (Step 4B)
3. Log decision in trading journal (Step 4D)
4. Verify L2 data connection active

### 6B: Enable Strategy at 9:25 AM CT
1. `Control Center` → `Strategies` → `ClanMarshal_v94`
2. Verify parameters are correct
3. Verify account is correct (live vs sim)
4. Right-click → `Enable`
5. **Confirmation:** Strategy should print "Session start" message

### 6C: Monitor Throughout RTH (9:30 AM - 4:00 PM CT)
- Watch for entry signals (output window)
- Verify L2 thin wall detection working
- Monitor realistic PnL vs NT8 PnL
- Check kill-switch status (daily loss, max DD)

### 6D: Disable Strategy at 4:00 PM CT
- Strategy auto-disables at RTH close (configured in `ExitOnSessionCloseSeconds = 30`)
- Manually disable if needed: `Control Center` → Right-click → `Disable`

### 6E: End-of-Day Review
1. Check trade log in `Control Center` → `Strategies` → `Executions`
2. Compare actual fills vs v9.4 reference trades
3. Verify realistic PnL tracking accuracy
4. Update trading journal with:
   - Trades taken
   - Win/loss outcome
   - Realistic PnL
   - Running equity
   - Any anomalies

---

## Step 7: Monitoring & Alerts

### Real-Time Monitoring
Monitor these metrics continuously:

1. **Position Status:** Should ALWAYS be 0 or 1, NEVER 2+
2. **Daily PnL:** If approaching -25 pts, prepare for shutdown at -30
3. **Drawdown from Peak:** If approaching -80 pts, prepare for shutdown at -100
4. **L2 Data Quality:** Verify `bestBidVolume` and `bestAskVolume` updating

### Alert Configuration (Optional)
Create NT8 alerts for:
- Daily loss approaching -25 pts (early warning)
- Drawdown approaching -80 pts (early warning)
- Position count != 0 or 1 (safety violation)

### SMS/Email Notifications
- Enable NT8 email alerts: `Tools` → `Options` → `Email`
- Configure for kill-switch events and trade executions

---

## Step 8: Performance Tracking

### Daily Tracking Spreadsheet
Create Excel/Google Sheets with columns:
| Date | Regime (dir_eff, vol_ratio) | Deploy? | Trades | Winners | Losers | Gross PnL | Realistic PnL | Equity | DD from Peak |
|------|----------------------------|---------|--------|---------|--------|-----------|---------------|--------|--------------|
| 5/3  | 0.45, 0.88                 | YES     | 2      | 2       | 0      | +18.5     | +15.5         | +15.5  | 0            |

### Weekly Review (Every Friday)
- Total trades vs v9.4 expectation (1.89/day avg = 9-10/week)
- Win rate vs 69.44% target
- Expectancy vs 12.30 pts target
- Regime accuracy (deployed on right days?)
- L2 detection accuracy (thin walls actually thin?)

### Monthly Review (Every 30 days or 50 trades)
- Cumulative PnL vs v9.4 pro-rated expectation
- Max drawdown vs -12 pts validated
- Profit factor vs 13.56 target
- Regime filter effectiveness
- Force rank proxy accuracy

### Adjustments Trigger Points
- **If win rate < 55% after 30 trades:** Review force rank threshold, consider lowering to 0.90
- **If trades/day < 1.0 after 20 days:** Review thin wall threshold, consider increasing to 15 contracts
- **If max DD > -30 pts:** Tighten force rank to 0.96 or add profit lock filter
- **If regime filter blocks > 50% of days:** Review dir_eff threshold, market may have shifted

---

## Common Issues & Troubleshooting

### Issue 1: No Trades for Multiple Days
**Symptoms:** Strategy enabled, regime passed, but no entries

**Diagnosis:**
1. Check L2 data: `Print(bestBidVolume)` and `Print(bestAskVolume)` in code
   - If always 0: L2 subscription not active
2. Check thin wall threshold: May be too strict (< 10 contracts rarely occurs)
3. Check force rank proxy: May be filtering out all signals

**Fix:**
- Verify L2 subscription active: `Control Center` → `Connections` → Check "Market Depth" enabled
- Increase `ThinWallMaxVolume` to 15-20 if market liquidity higher than backtest period
- Lower `ForceRankThreshold` to 0.90 for more signals (expect lower quality)

### Issue 2: Too Many Trades (> 5/day)
**Symptoms:** More trades than expected, potentially lower quality

**Diagnosis:**
1. Force rank proxy too lenient
2. Thin wall threshold too loose
3. Regime check not working

**Fix:**
- Increase `ForceRankThreshold` to 0.96 for stricter filtering
- Decrease `ThinWallMaxVolume` to 5-8 for only extreme thin walls
- Verify regime check ran and parameter updated

### Issue 3: Position Count > 1 (CRITICAL)
**Symptoms:** Strategy shows 2+ contracts open

**Diagnosis:**
1. `EntriesPerDirection` not set to 1
2. Multi-layer enforcement bypassed somehow
3. Manual order placed while strategy running

**Fix:**
- **IMMEDIATE:** Disable strategy, flatten all positions manually
- Review strategy parameters: `EntriesPerDirection` MUST be 1
- Review code: verify all entry calls use `EnterLong(1, ...)` not `EnterLong(Quantity, ...)`
- **Never manually trade while strategy enabled**

### Issue 4: Kill-Switch Not Triggering
**Symptoms:** Daily loss exceeds -30 pts but strategy still trading

**Diagnosis:**
1. Kill-switch logic error in code
2. Realistic PnL tracking error
3. Parameter not set correctly

**Fix:**
- Verify `DailyLossLimit = 30` (not -30, code handles negative)
- Check output window for "KILL-SWITCH" message
- Add debug prints: `Print("Daily PnL: " + dailyPnL)`

### Issue 5: L2 Data Gaps
**Symptoms:** `bestBidVolume` or `bestAskVolume` frequently 0

**Diagnosis:**
1. L2 feed interruption
2. Market closed or pre-market
3. Subscription lapsed

**Fix:**
- Check data connection status
- Verify trading during RTH (9:30 AM - 4:00 PM CT)
- Contact data provider if persistent

---

## Performance Expectations (v9.4 Validated)

### Trade Frequency
- **Avg:** 1.89 trades/day on deployed days
- **Range:** 0-4 trades/day
- **Zero trade days:** Normal, especially on SUPPRESS days

### Win Rate
- **Target:** 69.44%
- **Acceptable:** 60-75%
- **Concerning:** < 55% after 30 trades

### Expectancy
- **Target:** 12.30 pts/trade
- **With friction:** 10.80 pts/trade (1.5 pts slippage)
- **Acceptable:** 8-15 pts/trade

### Drawdown
- **Validated max:** -12.00 pts
- **Kill-switch:** -100 pts from peak
- **Concerning:** -30 pts (review if hit)

### Monthly P&L (Pro-Rated from v9.4)
- **Validation period:** 19 days, 442.75 pts
- **Per-day avg:** 23.30 pts/day (deployed days only)
- **Monthly estimate (20 trading days):** ~466 pts gross, ~350 pts net
- **Actual will vary:** Sample size small (36 trades), expect variance

---

## Rollback Plan

### If Strategy Underperforms:
1. **After 20 trades with < 50% WR:** Disable immediately, review logs
2. **After -50 pts loss:** Disable, compare to v9.4 reference trades
3. **After 3 consecutive daily loss limit hits:** Disable, regime analysis

### Return to Prior Version:
- **v9.3 (research):** No NinjaScript exists, ClickHouse only
- **v9.2 (baseline):** No filters, pure structural signals (if v9.2 NinjaScript exists)

### Emergency Manual Trading:
- Use `V94_DEPLOYMENT_DECISION_MATRIX.md` for daily regime check
- Trade only LONG_P9990 setups manually (61% of v9.4 PnL, 100% WR)
- Strict 1 MNQ discipline

---

## Next Steps After Deployment

### Week 1: Validation Phase
- Run in simulation account only
- Compare results to v9.4 reference trades
- Verify L2 thin wall detection accuracy
- Confirm regime filter working

### Week 2-4: Paper Trading
- Enable in paper trading account
- Monitor for 50 trades or 30 calendar days
- Compare performance to v9.4 expectations
- Refine parameters if needed

### Month 2+: Live Trading
- Enable in live account with 1 MNQ position
- Continue daily regime checks
- Monthly performance review vs v9.4
- Quarterly regime threshold adjustment

---

## Support & Maintenance

### Code Repository
- **Location:** `ninjascript/ClanMarshal_v94.cs`
- **Backup:** `/mnt/vps_strategies/ClanMarshal_v94.cs`
- **Version control:** Git repo in `/home/bernard/trading4/CG_MNQ_MarketReplayLab/`

### Documentation
- **Validation report:** `docs/CLANMARSHAL_V94_VALIDATION_COMPLETE.md`
- **Decision matrix:** `docs/V94_DEPLOYMENT_DECISION_MATRIX.md`
- **This guide:** `docs/V94_DEPLOYMENT_GUIDE.md`

### Data Tables (ClickHouse)
- `CG_mnq_cm_v93_daily_regime_report` - Daily regime classifications
- `CG_mnq_cm_v94_filtered_production_backtest` - v9.4 reference trades
- `CG_mnq_cm_v94_true_sequential_backtest` - Sequential validation

### Contact & Escalation
- **Strategy questions:** Review validation documentation first
- **Code bugs:** Check NT8 output window, review OnExecutionUpdate logic
- **Data issues:** Contact data provider for L2 feed problems

---

**Status:** ✅ READY FOR DEPLOYMENT
**Date:** May 3, 2026
**Next Action:** Step 1 - Deploy to VPS, Step 5 - Market Replay testing

**CRITICAL REMINDER:** This strategy requires DAILY manual regime check. Do NOT enable without verifying prior day's directional_efficiency and vol_ratio_5d meet deployment criteria.
