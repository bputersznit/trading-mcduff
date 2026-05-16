# T2 Event Imbalance - Choppy Day Protection Implementation Guide

## File Created
`CG_T2_EventImbalance_ChoppyProtected_v1_0.cs`

---

## What Was Implemented

### 3-Layer Protection System

```
┌─────────────────────────────────────────┐
│   LAYER 1: 3-STRIKE CHOPPY FILTER       │
│   ✓ Detects choppy days early          │
│   ✓ Stops after 3 consecutive losses   │
│   ✓ Caps daily loss at ~$107           │
│   ✓ Resets on first win                │
└─────────────────────────────────────────┘
                  ↓ If bypassed
┌─────────────────────────────────────────┐
│   LAYER 2: DAILY MAX LOSS -$200         │
│   ✓ Backup if 3-strike fails           │
│   ✓ Tracks session P&L                 │
│   ✓ Stops at -$200 daily loss          │
│   ✓ Protects against variance          │
└─────────────────────────────────────────┘
                  ↓ If bypassed
┌─────────────────────────────────────────┐
│   LAYER 3: EMERGENCY STOP -$400         │
│   ✓ Final brake before -$600           │
│   ✓ Tracks cumulative P&L from start   │
│   ✓ Requires manual restart if hit     │
│   ✓ Sends CRITICAL alert                │
└─────────────────────────────────────────┘
```

---

## Installation Instructions

### Step 1: Import into NinjaTrader 8

1. Copy `CG_T2_EventImbalance_ChoppyProtected_v1_0.cs` to:
   ```
   Documents\NinjaTrader 8\bin\Custom\Strategies\
   ```

2. Open NinjaTrader 8

3. Tools → Compile → Compile (or press F5)

4. Check Output window for errors

5. If successful, you'll see:
   ```
   Compiled successfully.
   ```

### Step 2: Configure Strategy Parameters

Open Strategy Analyzer or Chart → Strategies → CG_T2_EventImbalance_ChoppyProtected_v1_0

#### Recommended Default Settings

**01. Signals** (Optimal from backtest):
- MinWallScore: `4000`
- MinWidth: `10`

**02. Risk**:
- StopTicks: `16` (4 points)
- TargetTicks: `32` (8 points)

**03. Session**:
- StartTimeEt: `93000` (9:30 AM ET)
- EndTimeEt: `155900` (3:59 PM ET)

**04. Filters**:
- CooldownSeconds: `0` (no cooldown)
- MaxSpreadTicks: `8.0`
- MinEventDelta: `20.0`
- MinEventImbalance: `0.15`
- EventLookbackBars: `200`

**05. Choppy Protection** ⭐ KEY SETTINGS:
- **Enable 3-Strike Filter**: `TRUE` ✅
- **Max Consecutive Losses**: `3`
- **Enable Daily Max Loss**: `TRUE` ✅
- **Daily Max Loss ($)**: `200.00`
- **Enable Emergency Stop**: `TRUE` ✅
- **Emergency Stop DD ($)**: `400.00`
- **Send Alerts**: `TRUE` (configure email first)

**06. Telemetry**:
- EnableTelemetry: `TRUE`
- TelemetryFilePrefix: `T2_ChoppyProtected`
- PrintDiagnostics: `TRUE`

---

## How Protection Works

### Layer 1: 3-Strike Choppy Filter

**Trigger Condition**: 3 consecutive losing trades

**What Happens**:
```csharp
Trade 1: LOSS (-$35.70) → Strike 1 → consecutiveLosses = 1
Trade 2: LOSS (-$35.70) → Strike 2 → consecutiveLosses = 2
Trade 3: LOSS (-$35.70) → Strike 3 → consecutiveLosses = 3
         ↓
    🛑 CHOPPY DAY DETECTED
    ✋ Strategy DISABLED for rest of session
    📧 Alert sent (if enabled)
    📝 Logged to telemetry
```

**Behavior**:
- All new entry signals blocked
- Open positions allowed to exit normally
- Resets next session start
- Logs reason: "3 consecutive losses detected"

**Impact from Backtest**:
- Catches 100% of choppy days (all started LLL)
- Saves $127 over 6 days
- Improves P&L by 171%

---

### Layer 2: Daily Max Loss

**Trigger Condition**: Session P&L ≤ -$200

**What Happens**:
```csharp
Session Start: sessionPnL = $0
Trade 1: -$35.70 → sessionPnL = -$35.70  ✅ OK
Trade 2: -$70.00 → sessionPnL = -$105.70 ✅ OK
Trade 3: -$60.00 → sessionPnL = -$165.70 ✅ OK
Trade 4: -$45.00 → sessionPnL = -$210.70 ⚠️ TRIGGER
         ↓
    ⚠️ DAILY MAX LOSS HIT
    ✋ Strategy DISABLED for rest of session
    📧 Alert sent
```

**Behavior**:
- Blocks all new entries
- Open positions allowed to exit
- Resets next session
- Logs: "Daily loss $-210.70 exceeded -$200"

**Use Case**:
- Backup if 3-strike filter missed (shouldn't happen)
- Protects against extreme variance
- Prevents single-day disasters

---

### Layer 3: Emergency Stop

**Trigger Condition**: Cumulative P&L - Starting Equity ≤ -$400

**What Happens**:
```csharp
Account Start: accountStartEquity = $0
Day 1: -$150 → cumulativePnL = -$150 → DD = -$150  ✅ OK
Day 2: -$100 → cumulativePnL = -$250 → DD = -$250  ✅ OK
Day 3: -$180 → cumulativePnL = -$430 → DD = -$430  🔴 TRIGGER
         ↓
    🔴🔴🔴 EMERGENCY STOP TRIGGERED 🔴🔴🔴
    ⛔ Strategy COMPLETELY DISABLED
    📧 CRITICAL alert sent
    🔧 MANUAL RESTART REQUIRED
```

**Behavior**:
- Blocks ALL new entries forever until manual reset
- Closes any open positions immediately
- Does NOT auto-reset on new session
- Logs: "Cumulative DD $-430 exceeded -$400"
- Sends CRITICAL email alert

**Use Case**:
- Final brake before hitting -$600
- Prevents extended losing streaks
- Forces manual review before continuing

**How to Reset**:
1. Disable strategy
2. Review telemetry to understand what went wrong
3. Optionally adjust parameters
4. Re-enable strategy (starts fresh)

---

## Real-Time Monitoring

### Output Window Messages

**Normal Operation**:
```
=== NEW SESSION START ===
Starting equity: $0.00
Protection layers reset
✅ Choppy Day Protection Enabled - 3-Layer Defense Active
```

**Layer 1 Triggered**:
```
🛑 CHOPPY DAY DETECTED!
   Reason: 3 consecutive losses detected
   Strategy DISABLED for rest of session
   Prevented further losses on choppy day
```

**Layer 2 Triggered**:
```
⚠️ DAILY MAX LOSS HIT!
   Session P&L: $-210.70
   Limit: -$200.00
   Strategy DISABLED for rest of session
```

**Layer 3 Triggered**:
```
🔴🔴🔴 EMERGENCY STOP TRIGGERED! 🔴🔴🔴
   Cumulative P&L: $-430.00
   Drawdown from start: $-430.00
   Emergency threshold: -$400.00
   STRATEGY COMPLETELY DISABLED
   Manual restart required
```

### Telemetry File

Location: `Documents\NinjaTrader 8\trace\T2_ChoppyProtected_YYYYMMDD_HHMMSS.csv`

**Special Protection Events**:
```csv
record_type,trade_id,time,side,regime,event_delta,event_imbalance,total_events,spread_ticks,diagnostic
CHOPPY_FILTER,3,2025-10-01 10:15:23.456,,,,,,,3 consecutive losses detected
DAILY_MAX_LOSS,7,2025-10-02 14:30:12.789,,,,,,,Daily loss: $-210.70 exceeded -$200
EMERGENCY_STOP,15,2025-10-05 11:45:00.123,,,,,,,Cumulative DD $-430.00 exceeded -$400
```

### Summary Report (End of Session)

```
=== T2 CHOPPY PROTECTED SUMMARY ===
Session trades: 10
Cumulative P&L: $-150.50
Session P&L: $-107.10

=== PROTECTION STATUS ===
Layer 1 - 3-Strike Filter: 🛑 TRIGGERED (3 consecutive losses)
Layer 2 - Daily Max Loss: ✅ OK (session P&L: $-107.10)
Layer 3 - Emergency Stop: ✅ OK (cumulative: $-150.50)

=== REJECTION REASONS ===
RTH rejects: 1250
Spread rejects: 45
Event delta rejects: 3200
Imbalance rejects: 2800
Cooldown rejects: 0
Position rejects: 15
🛑 Choppy filter rejects: 150
⚠️ Daily max loss rejects: 0
🔴 Emergency stop rejects: 0

=== SIGNAL STATS ===
Total LONG signals: 5
Total SHORT signals: 5
```

---

## Email Alert Configuration

### Step 1: Configure NinjaTrader Email Settings

1. Tools → Options → Email
2. Configure SMTP settings:
   - SMTP Server: `smtp.gmail.com` (or your provider)
   - Port: `587` (TLS) or `465` (SSL)
   - Username: `your-email@gmail.com`
   - Password: `your-app-password`
   - From: `your-email@gmail.com`

3. Test email to verify configuration

### Step 2: Update Strategy Code

Replace `"trade@yourdomain.com"` with your actual email in:
- Line 280: Choppy filter alert
- Line 306: Daily max loss alert
- Line 334: Emergency stop alert

Or use a distribution list for critical alerts.

### Alert Email Examples

**Choppy Day Alert**:
```
Subject: T2 Choppy Day Alert

Choppy day detected: 3 consecutive losses detected
Strategy disabled for session.

Time: 2025-10-01 10:15:23
```

**Daily Max Loss Alert**:
```
Subject: T2 Daily Max Loss Alert

Daily max loss hit: $-210.70
Limit: -$200.00
Strategy disabled for session.

Time: 2025-10-02 14:30:12
```

**Emergency Stop Alert** (CRITICAL):
```
Subject: 🔴 T2 EMERGENCY STOP 🔴

EMERGENCY STOP TRIGGERED!

Cumulative DD $-430.00 exceeded -$400.00

Cumulative P&L: $-430.00

Strategy completely disabled. Manual restart required.

Time: 2025-10-05 11:45:00
```

---

## Testing & Validation

### Paper Trading Test Plan

**Week 1: Verify Protection Layers**

1. **Test Layer 1** (3-Strike Filter):
   - Run strategy during first choppy day
   - Verify it stops after 3 consecutive losses
   - Check Output window for "CHOPPY DAY DETECTED" message
   - Verify no more trades taken that session
   - Confirm resets next session

2. **Test Layer 2** (Daily Max Loss):
   - Monitor session P&L
   - If approaching -$200, verify strategy stops
   - Check telemetry for DAILY_MAX_LOSS event

3. **Test Layer 3** (Emergency Stop):
   - Track cumulative P&L over multiple sessions
   - If approaching -$400, verify EMERGENCY STOP triggers
   - Verify requires manual restart

**Week 2: Performance Validation**

1. Compare to baseline (no protection):
   - Total trades
   - Win rate
   - P&L
   - Max DD

2. Expected results with protection:
   - Fewer trades (~32% reduction)
   - Higher avg P&L per trade
   - Lower max DD
   - Better risk-adjusted returns

---

## Backtest Validation

### Run in Strategy Analyzer

1. Data: MNQ 100ms bars (or tick data)
2. Date range: Sep 26 - Oct 21, 2025 (matches original backtest)
3. Parameters: Use recommended defaults above
4. Run backtest

### Expected Results (6 days, 28 baseline trades)

**With Protection Enabled**:
- Trades: ~19 (blocked ~9 choppy trades)
- Final P&L: ~$200 (vs $74 without protection)
- Max DD: < -$250 (vs -$339 worst case)
- Choppy days detected: 3 (Oct 1, 2, 20)

**Protection Events**:
- Layer 1 triggers: 3 times (choppy days)
- Layer 2 triggers: 0 (3-strike catches first)
- Layer 3 triggers: 0 (never approach -$400)

---

## Troubleshooting

### Issue: "Compile error - cannot find Performance object"

**Cause**: Strategy manually calculates P&L instead of using Performance object

**Solution**: This is intentional - no action needed

---

### Issue: "Protection not triggering on expected choppy day"

**Check**:
1. `EnableChoppyFilter = TRUE`?
2. `MaxConsecutiveLosses = 3`?
3. Are trades actually losing? (check telemetry)
4. Output window shows protection messages?

**Debug**:
- Add breakpoints in `CheckChoppyDayProtection()`
- Review telemetry EXIT records
- Verify P&L calculation is correct

---

### Issue: "Emergency stop triggering too early"

**Cause**: `EmergencyStopDD = 400` may be too tight for your account

**Solution**:
- Increase to $500 or $600
- Monitor actual worst-case DD over 30+ days
- Adjust based on live data

---

### Issue: "Email alerts not sending"

**Check**:
1. NinjaTrader email configured? (Tools → Options → Email)
2. Test email works?
3. `SendAlertsOnProtection = TRUE`?
4. Email address correct in code? (search for `SendMail`)

**Gmail Users**:
- Use App Password (not regular password)
- Enable "Less secure app access" in Gmail settings

---

## Advanced Configuration

### Adjusting Protection Thresholds

**More Aggressive (tighter protection)**:
```csharp
MaxConsecutiveLosses = 2;     // Stop after 2 losses
DailyMaxLoss = 150.0;         // Tighter daily limit
EmergencyStopDD = 300.0;      // Earlier emergency stop
```

**More Conservative (looser protection)**:
```csharp
MaxConsecutiveLosses = 4;     // Allow 4 losses
DailyMaxLoss = 250.0;         // Higher daily limit
EmergencyStopDD = 500.0;      // Later emergency stop
```

**Disable Specific Layers**:
```csharp
EnableChoppyFilter = false;   // Disable Layer 1
EnableDailyMaxLoss = false;   // Disable Layer 2
EnableEmergencyStop = false;  // Disable Layer 3 (NOT RECOMMENDED!)
```

---

## Key Differences from Baseline Version

| Feature | Baseline v1.0 | ChoppyProtected v1.0 |
|---------|---------------|---------------------|
| 3-Strike Filter | Basic (MaxConsecutiveLosses) | Enhanced with choppy day detection |
| Daily Max Loss | $60 limit | $200 limit (configurable) |
| Cumulative DD Stop | None | NEW: -$400 emergency stop |
| Protection Alerts | None | Email/SMS alerts |
| Telemetry | Basic | Enhanced with protection events |
| Manual P&L Tracking | Relies on Performance object | Manual calculation (more reliable) |
| Reset Logic | Simple | Multi-layer reset with diagnostics |

---

## Recommended Workflow

### Live Trading Deployment

**Phase 1: Paper Trading (2 weeks)**
1. Run with protection enabled
2. Monitor daily Output window
3. Review telemetry each evening
4. Validate protection triggers as expected

**Phase 2: Micro Lot Testing (2 weeks)**
1. Enable with real money, 1 micro contract
2. Verify fills match expectations
3. Confirm protection layers work in live environment
4. Adjust thresholds if needed

**Phase 3: Full Deployment**
1. Scale to full position size
2. Monitor first week closely
3. Set up automated alerts
4. Review weekly performance

---

## Success Metrics

**Week 1 Checkpoint**:
- ✅ Protection layers functioning correctly
- ✅ Choppy days detected and blocked
- ✅ Alerts sending properly
- ✅ Telemetry captured

**Month 1 Checkpoint**:
- ✅ Positive risk-adjusted returns
- ✅ Max DD < $400
- ✅ No emergency stops triggered
- ✅ Consistent with backtest expectations

**Ongoing**:
- ✅ Monthly P&L positive
- ✅ Sharpe ratio > 1.0
- ✅ Max DD under control
- ✅ Protection rarely triggered (good days > choppy days)

---

## Support & Next Steps

### If Strategy Performs Well
- Expand to 28-day full backtest (process missing 17 days)
- Test on additional MNQ contracts
- Consider other instruments (ES, NQ)

### If Protection Triggers Frequently
- May indicate market regime change
- Review signal quality (event delta/imbalance)
- Consider tightening entry filters
- Pause and re-evaluate edge

### If You Hit Emergency Stop
1. **STOP IMMEDIATELY** - don't override
2. Review ALL telemetry
3. Identify root cause (bad signals? execution issues?)
4. Backtest recent period to see if edge degraded
5. Only restart after thorough analysis

---

## Final Checklist Before Going Live

- [ ] Strategy compiled successfully
- [ ] All parameters configured
- [ ] Protection layers enabled
- [ ] Email alerts configured and tested
- [ ] Telemetry working
- [ ] Paper traded for 2+ weeks
- [ ] Protection triggers verified
- [ ] Risk limits appropriate for account size
- [ ] Emergency contact plan established
- [ ] Monitoring process defined

---

**You're now protected against -$600 drawdown with 3 layers of defense. Good luck!** 🎯
