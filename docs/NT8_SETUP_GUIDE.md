# NT8 SETUP GUIDE - CG Scalping Strategy

Complete guide to deploying your order flow scalping strategy in NinjaTrader 8.

---

## ARCHITECTURE OVERVIEW

```
┌─────────────────┐         ┌──────────────────┐         ┌─────────────────┐
│  ClickHouse DB  │         │  Python Signal   │         │  NinjaTrader 8  │
│                 │────────▶│   Generator      │────────▶│                 │
│ Order Flow Data │         │                  │         │  Strategy       │
└─────────────────┘         └──────────────────┘         └─────────────────┘
                                     │                            │
                                     ▼                            ▼
                            mnq_signals.csv                  Broker API
                                                          (Stops/Targets/OCO)
```

**Flow**:
1. ClickHouse stores real-time 1-second order flow aggregations
2. Python script detects signals (ABSORPTION, ICEBERG, BREAKOUT)
3. Writes strongest signal to CSV file every second
4. NT8 strategy reads CSV file, executes trades
5. NT8 manages positions with broker-side stops/targets (OCO)

---

## PREREQUISITES

### Software Requirements
- ✅ NinjaTrader 8 (latest version)
- ✅ Python 3.8+ with clickhouse-connect
- ✅ ClickHouse database with `mnq_orderflow_1sec` table
- ✅ Live market data feed (CME/Rithmic/CQG)
- ✅ Broker connection (Rithmic, CQG, etc.)

### Account Requirements
- ✅ Futures trading account with MNQ permissions
- ✅ Minimum margin for 1 MNQ contract (~$1,500)
- ✅ Commission structure compatible with backtest ($0.70/rt)

---

## INSTALLATION

### Step 1: Install NinjaScript Strategy

1. **Locate the strategy file**:
   ```
   /home/bernard/trading4/CG_MNQ_MarketReplayLab/ninjascript/CGScalpingStrategy.cs
   ```

2. **Copy to NinjaTrader**:
   - Open NinjaTrader 8
   - Go to: Tools → Edit NinjaScript → Strategy
   - Click "New" → From existing file
   - Browse to `CGScalpingStrategy.cs`
   - Click "Compile"
   - Verify: "Compiled successfully" message

3. **Alternative method** (manual):
   - Copy `CGScalpingStrategy.cs` to:
     ```
     C:\Users\<YourName>\Documents\NinjaTrader 8\bin\Custom\Strategies\
     ```
   - In NT8: Tools → Compile All

### Step 2: Set Up Directories

Create required directories (Windows):

```batch
mkdir C:\Trading\Signals
mkdir C:\Trading\Logs
```

Or (Linux/WSL):

```bash
mkdir -p /mnt/c/Trading/Signals
mkdir -p /mnt/c/Trading/Logs
```

### Step 3: Install Python Signal Generator

1. **Ensure Python dependencies**:
   ```bash
   pip install clickhouse-connect
   ```

2. **Configure the signal generator**:
   Edit `/home/bernard/trading4/CG_MNQ_MarketReplayLab/scripts/CGCl_nt8_signal_generator.py`:

   ```python
   # ClickHouse connection
   CH_HOST = 'localhost'  # Change if ClickHouse on different server
   CH_PORT = 8123
   CH_USER = 'default'
   CH_PASSWORD = ''

   # NT8 signal file
   SIGNAL_FILE = r'C:\Trading\Signals\mnq_signals.csv'  # Adjust path if needed
   ```

3. **Test the signal generator**:
   ```bash
   python3 /home/bernard/trading4/CG_MNQ_MarketReplayLab/scripts/CGCl_nt8_signal_generator.py
   ```

   Should see:
   ```
   ✅ Connected to ClickHouse: localhost:8123/marketreplay
   🔄 Monitoring for signals... (Ctrl+C to stop)
   ```

---

## STRATEGY CONFIGURATION

### Step 4: Configure NT8 Strategy Parameters

1. **Add strategy to chart**:
   - Open MNQ chart (any timeframe, strategy uses tick data)
   - Right-click → Strategies → CGScalpingStrategy

2. **Configure parameters**:

#### 1. Signal Source
```
Signal File Path: C:\Trading\Signals\mnq_signals.csv
```

#### 2. Position
```
Contracts: 1
```
⚠️ **NEVER change this to more than 1**

#### 3a. ABSORPTION
```
Target (points):    6.0
Stop (points):      3.0
Max Hold (seconds): 120
```

#### 3b. ICEBERG
```
Target (points):    5.0
Stop (points):      2.5
Max Hold (seconds): 90
```

#### 3c. BREAKOUT
```
Target (points):    8.0
Stop (points):      4.0
Max Hold (seconds): 60
```

#### 4. Risk Management
```
Min Trades/Hour:     4.0
Weekly Loss Limit:   250.0
Hard Loss Limit:     500.0
Enable Emergency Flatten: YES (checked)
P&L File Path:       C:\Trading\Logs\daily_pnl.csv
```

3. **Enable strategy**:
   - Check "Enabled" box
   - Click "OK"

---

## OPERATION

### Starting the System

**Order matters - follow this sequence**:

1. **Start ClickHouse** (if not running):
   ```bash
   sudo systemctl start clickhouse-server
   ```

2. **Verify market data feed**:
   - In NT8: Connections → Your broker → Connected (green)
   - Open MNQ chart, verify live data streaming

3. **Start Python signal generator**:
   ```bash
   cd /home/bernard/trading4/CG_MNQ_MarketReplayLab/scripts
   python3 CGCl_nt8_signal_generator.py
   ```

4. **Enable NT8 strategy**:
   - Right-click chart → Strategies → CGScalpingStrategy
   - Check "Enabled"
   - Click "Apply"

### Monitoring

**Watch these indicators**:

1. **Signal Generator Console**:
   ```
   [2024-01-15 09:30:45] SIGNAL: ABSORPTION LONG @ 17450.25 (strength: 45)
   [2024-01-15 09:31:12] SIGNAL: BREAKOUT SHORT @ 17448.50 (strength: 62)
   ```

2. **NT8 Strategy Output** (F5 → Output window):
   ```
   2024-01-15 09:30:45 LONG ABSORPTION @ 17450.25 | Target: +6 Stop: -3 MaxHold: 120s
   2024-01-15 09:32:01 Time-based exit: 76s >= 120s max hold
   2024-01-15 09:32:01 Trade closed: $48.00 | Today: $48.00 | Cumulative: $1,254.40
   ```

3. **NT8 Chart**:
   - Watch for entry markers (arrows)
   - Verify stop/target levels drawn
   - Monitor position status

4. **P&L File** (`C:\Trading\Logs\daily_pnl.csv`):
   ```
   date,daily_pnl,cumulative_pnl
   2024-01-15,48.00,1254.40
   ```

### During Trading Day

**Check every hour**:
- [ ] Signal generator still running
- [ ] NT8 strategy enabled
- [ ] Broker connection active
- [ ] Today's P&L reasonable (not approaching limits)

**After every trade**:
- [ ] Verify execution quality (fill price vs signal price)
- [ ] Check stop/target were placed correctly
- [ ] Monitor cumulative P&L

### End of Day

1. **Disable strategy**:
   - Right-click chart → Strategies → CGScalpingStrategy
   - Uncheck "Enabled"
   - Verify position is flat

2. **Stop signal generator**:
   - Ctrl+C in terminal

3. **Review logs**:
   - Check `C:\Trading\Logs\daily_pnl.csv`
   - Review NT8 output window (F5)
   - Save any important notes

---

## SAFETY FEATURES

### 1. Connection Loss Protection

**Automatic**:
- Strategy checks connection every 2 seconds
- On disconnect: Emergency flatten ALL positions
- Audible alert: Alert4.wav (loud siren)
- On reconnect: Resumes normal operation

**Manual override**:
If auto-flatten fails, use NT8 "Flatten Everything" button (F7)

### 2. Broker-Side Stops/Targets (OCO)

**How it works**:
- Every entry automatically places TWO orders at broker:
  - Stop loss order (market if hit)
  - Profit target order (limit)
- OCO (One-Cancels-Other): If one fills, other cancels
- Orders live at BROKER, not on your PC
- Protected even if NT8 crashes or disconnects

**Verification**:
- After entry, check NT8 "Orders" tab
- Should see both Stop and Target orders
- Status: "Working" (at broker)

### 3. Time-Based Exits

**Automatic**:
- Every second, checks time in position
- If exceeds max hold → market exit
- Prevents holding losers too long
- Independent of stop/target

### 4. Rolling Trades/Hour Gate

**Automatic**:
- Tracks trades in last 60 minutes
- If < 4 trades/hour → stops taking new signals
- Indicates poor market conditions
- Resets when conditions improve

### 5. Weekly Loss Limit

**Automatic**:
- Tracks P&L for last 5 trading days
- If cumulative ≤ -$250 → stops trading
- Alert: "Weekly loss limit hit"
- Resets next trading day (fresh 5-day window)

**Manual check**:
Review `daily_pnl.csv` for last 5 days

### 6. Hard Loss Limit

**Automatic**:
- Tracks cumulative P&L across ALL time
- If ≤ -$500 → STRATEGY DISABLED PERMANENTLY
- Alert: "HARD LIMIT BREACHED - STRATEGY DISABLED"
- Flattens any open position
- Will NOT re-enable until manual intervention

**Recovery**:
1. Review what went wrong
2. Reset cumulative in `daily_pnl.csv` (if warranted)
3. Re-enable strategy manually

---

## TROUBLESHOOTING

### Problem: No signals being generated

**Check**:
1. Python script running?
   ```bash
   ps aux | grep signal_generator
   ```

2. ClickHouse has recent data?
   ```sql
   SELECT max(timestamp_1sec) FROM mnq_orderflow_1sec;
   -- Should be within last few seconds
   ```

3. Signal file exists and is updating?
   ```bash
   ls -lh /mnt/c/Trading/Signals/mnq_signals.csv
   # Check timestamp
   ```

4. Market open? (CME hours: 17:00 Sun - 16:00 Fri CT)

### Problem: NT8 not taking signals

**Check**:
1. Strategy enabled? (green "Enabled" checkbox)

2. Signal file path correct?
   - In strategy parameters, verify path matches signal generator output

3. Position already open?
   - Strategy only enters when flat
   - Check if stuck in position

4. Weekly/hard limit hit?
   - Check NT8 Output window (F5) for "LIMIT HIT" messages
   - Review `daily_pnl.csv`

5. Rolling gate triggered?
   - Output window: "Rolling gate triggered: X trades/hour < 4.0 minimum"
   - Wait for market conditions to improve

### Problem: Excessive slippage

**Check**:
1. Using market orders instead of limits?
   - Strategy uses market orders for entries (by design)
   - Some slippage expected (budgeted $1/contract)

2. Trading during low liquidity?
   - Avoid first/last 5 minutes of session
   - MNQ thinnest at: 3:15-3:30pm CT, overnight

3. Broker routing?
   - Ensure direct routing, not aggregated
   - Check with broker for best execution settings

### Problem: Stops not being placed

**Check**:
1. Broker supports broker-side stops?
   - Rithmic: YES
   - CQG: YES
   - Simulated data: NO (won't work in sim)

2. Check NT8 "Orders" tab after entry
   - Should show Stop and Target orders "Working"

3. Error in Output window?
   - Look for "Order rejected" or similar

### Problem: Emergency flatten not working

**If connection lost but position NOT flattened**:

1. Use NT8 "Flatten Everything" (F7)
2. Or manually: Close position from "Positions" tab
3. Call broker as last resort

**After recovery**:
- Review why auto-flatten failed
- Check Output window for errors
- May need to contact broker about API reliability

---

## PERFORMANCE MONITORING

### Daily Checklist

**End of day review**:

```
Date: __________

Trades: ____
Wins: ____
Losses: ____
Win Rate: ____%

Gross P&L: $______
Commission: $______ (trades × $0.70)
Net P&L: $______

Signal Quality:
  ABSORPTION: ____ trades
  ICEBERG:    ____ trades
  BREAKOUT:   ____ trades

Execution Quality:
  Avg slippage: $______ per contract
  Stops hit:    ____ times
  Targets hit:  ____ times
  Time exits:   ____ times

Issues:
  Connection drops: ____
  Missed signals: ____
  Over-slippage: ____

Cumulative P&L: $______
Distance from -$500: $______

Status: [  ] GOOD  [  ] OK  [  ] CAUTION  [  ] STOP
```

### Weekly Review

**Compare to expectations**:

```
Expected: ~$215/week ($43/day × 5 days)
Actual:   $______

Weekly rolling P&L (last 5 days): $______
Distance from -$250 limit: $______

Strategy stats:
  Avg trades/day: ____
  Win rate: ____%
  Best day: $______
  Worst day: $______

Adjustments needed?
[  ] YES - what: _________________________
[  ] NO - continue as-is
```

---

## RISK WARNINGS

### CRITICAL REMINDERS

1. **ALWAYS trade 1 contract only**
   - Never override this parameter
   - More contracts = proportionally more risk

2. **Respect the weekly limit**
   - If hit -$250 in 5 days: STOP
   - Don't try to "make it back"
   - Take a break, review what went wrong

3. **Monitor cumulative P&L**
   - Check `daily_pnl.csv` frequently
   - If approaching -$400: Be very cautious
   - If hit -$500: Strategy auto-disables

4. **NT8 privileges are precious**
   - Automated trading is a privilege
   - One breach = loss of privilege
   - Better to make less than risk it all

5. **Test thoroughly before going live**
   - Paper trade for at least 1 week
   - Verify all safety mechanisms work
   - Understand every parameter

6. **Never trade unattended**
   - Always monitor during market hours
   - Check signal generator running
   - Watch for connection issues

---

## GOING LIVE CHECKLIST

### Before First Live Trade

- [ ] Backtested strategy thoroughly (28+ days)
- [ ] Paper traded for 1+ week successfully
- [ ] Verified broker-side stops work
- [ ] Tested connection loss recovery
- [ ] Understand all risk limits
- [ ] Created backup of P&L tracking
- [ ] Set up alerts/notifications
- [ ] Broker account funded adequately
- [ ] Commission structure confirmed ($0.70/rt)
- [ ] Saved emergency contact info (broker support)

### First Week Live

- [ ] Start with reduced hours (2-3 hours/day)
- [ ] Monitor EVERY trade closely
- [ ] Log all issues/observations
- [ ] Compare live vs backtest results
- [ ] Verify execution quality
- [ ] Test all safety limits (paper trade limits first!)
- [ ] Review daily with DAILY_TRADING_CHECKLIST.md

### After First Month

- [ ] Calculate actual vs expected returns
- [ ] Review commission costs
- [ ] Analyze signal quality (which types work best)
- [ ] Identify any patterns in losses
- [ ] Decide: Continue, adjust, or stop

---

## SUPPORT & RESOURCES

### Files Reference

| File | Purpose | Location |
|------|---------|----------|
| Strategy | NT8 execution | `/ninjascript/CGScalpingStrategy.cs` |
| Signal Gen | Order flow detection | `/scripts/CGCl_nt8_signal_generator.py` |
| Setup Guide | This file | `/docs/NT8_SETUP_GUIDE.md` |
| Daily Checklist | Operations | `/docs/DAILY_TRADING_CHECKLIST.md` |
| Strategy Doc | Full specs | `/docs/FINAL_STRATEGY_IMPLEMENTATION.md` |

### Logs & Data

| File | Purpose | Location |
|------|---------|----------|
| Signals | Live signals | `C:\Trading\Signals\mnq_signals.csv` |
| P&L Tracking | Daily results | `C:\Trading\Logs\daily_pnl.csv` |
| NT8 Output | Execution log | NT8 Output Window (F5) |

### Quick Commands

**Start signal generator**:
```bash
cd /home/bernard/trading4/CG_MNQ_MarketReplayLab/scripts
python3 CGCl_nt8_signal_generator.py
```

**Check recent signals**:
```bash
tail -f /mnt/c/Trading/Signals/mnq_signals.csv
```

**Monitor P&L**:
```bash
tail -f /mnt/c/Trading/Logs/daily_pnl.csv
```

**Emergency stop**:
1. NT8: Disable strategy (uncheck "Enabled")
2. NT8: F7 (Flatten Everything)
3. Terminal: Ctrl+C (stop signal generator)

---

## FINAL THOUGHTS

This is a **SURVIVAL** strategy, not a profit maximization strategy.

**Priorities**:
1. Preserve capital
2. Protect NT8 privileges
3. Trade consistently
4. Make money (in that order)

**If in doubt**:
- Stop trading
- Review the situation
- Ask for help
- Better safe than sorry

**Expected results with discipline**:
- Monthly: ~$950
- Worst day: ~-$30
- Risk of NT8 breach: Nearly zero

Good luck! Trade safe! 🎯

---

*Last updated: 2024-01-15*
*Strategy version: 1.0*
*NT8 version tested: NT8 8.0.29.1*
