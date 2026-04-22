# NT8 STRATEGY - QUICK REFERENCE CARD

## STRATEGY PARAMETERS (Copy-Paste Ready)

### 1. Signal Source
```
Signal File Path: C:\Trading\Signals\mnq_signals.csv
```

### 2. Position
```
Contracts: 1
```
⚠️ **NEVER CHANGE - Must be 1**

### 3a. ABSORPTION Parameters
```
Target (points):    6.0
Stop (points):      3.0
Max Hold (seconds): 120
```
*Risk/Reward: 2:1, Hold: 2 minutes*

### 3b. ICEBERG Parameters
```
Target (points):    5.0
Stop (points):      2.5
Max Hold (seconds): 90
```
*Risk/Reward: 2:1, Hold: 1.5 minutes*

### 3c. BREAKOUT Parameters
```
Target (points):    8.0
Stop (points):      4.0
Max Hold (seconds): 60
```
*Risk/Reward: 2:1, Hold: 1 minute*

### 4. Risk Management
```
Min Trades/Hour:          4.0
Weekly Loss Limit:        250.0
Hard Loss Limit:          500.0
Enable Emergency Flatten: ✓ (checked)
P&L File Path:           C:\Trading\Logs\daily_pnl.csv
```

---

## STARTUP SEQUENCE

```
1. ✓ Start ClickHouse
2. ✓ Verify NT8 broker connection (green)
3. ✓ Start Python signal generator
4. ✓ Enable NT8 strategy
```

---

## EMERGENCY PROCEDURES

### Connection Lost
```
1. Strategy auto-flattens (if enabled)
2. Manual: Press F7 in NT8
3. Verify position closed
```

### Weekly Limit Hit (-$250)
```
1. Strategy stops trading automatically
2. Review logs
3. Don't trade rest of week
4. Resume Monday (fresh window)
```

### Hard Limit Approaching (-$400+)
```
1. STOP trading immediately
2. Review all trades
3. Consider longer break
4. Don't approach -$500
```

### Hard Limit Breached (-$500)
```
1. Strategy DISABLED automatically
2. NT8 privileges at risk
3. Full review required
4. Manual reset only after approval
```

---

## DAILY MONITORING

### Pre-Market
- [ ] Check cumulative P&L: $______
- [ ] Check last 5 days: $______
- [ ] Start signal generator
- [ ] Enable NT8 strategy

### During Trading
- [ ] Signal generator running
- [ ] NT8 strategy enabled
- [ ] Broker connected
- [ ] Check P&L every hour

### End of Day
- [ ] Disable NT8 strategy
- [ ] Stop signal generator (Ctrl+C)
- [ ] Review daily_pnl.csv
- [ ] Update trading journal

---

## KEY METRICS (Expected)

| Metric | Value |
|--------|-------|
| Daily avg | $43 |
| Weekly avg | $215 |
| Monthly avg | $950 |
| Win rate | 48% |
| Trades/day | 40-60 |
| Worst day | ~-$30 |

---

## FILE LOCATIONS

```
Strategy:      C:\Users\<You>\Documents\NinjaTrader 8\bin\Custom\Strategies\CGScalpingStrategy.cs
Signals:       C:\Trading\Signals\mnq_signals.csv
P&L Log:       C:\Trading\Logs\daily_pnl.csv
Signal Gen:    /home/bernard/trading4/CG_MNQ_MarketReplayLab/scripts/CGCl_nt8_signal_generator.py
```

---

## QUICK COMMANDS

**Start signal generator:**
```bash
cd /home/bernard/trading4/CG_MNQ_MarketReplayLab/scripts
python3 CGCl_nt8_signal_generator.py
```

**Monitor signals:**
```bash
tail -f /mnt/c/Trading/Signals/mnq_signals.csv
```

**Check P&L:**
```bash
cat /mnt/c/Trading/Logs/daily_pnl.csv
```

**Emergency flatten:**
```
NT8: Press F7 (Flatten Everything)
```

---

## SAFETY LIMITS SUMMARY

| Limit | Value | Action |
|-------|-------|--------|
| Rolling gate | < 4 tr/hr | Stop new entries |
| Weekly limit | -$250 (5 days) | Stop trading today |
| Hard limit | -$500 (total) | Disable strategy |

---

## SIGNAL TYPES

| Type | Target | Stop | Hold | R:R |
|------|--------|------|------|-----|
| ABSORPTION | 6pt | 3pt | 120s | 2:1 |
| ICEBERG | 5pt | 2.5pt | 90s | 2:1 |
| BREAKOUT | 8pt | 4pt | 60s | 2:1 |

---

## TROUBLESHOOTING

| Problem | Quick Fix |
|---------|-----------|
| No signals | Check signal generator running |
| Not taking trades | Check strategy enabled |
| Position stuck | Manual exit or F7 |
| Connection lost | Auto-flatten or F7 |
| Limit hit | Check daily_pnl.csv |

---

## CRITICAL RULES

1. ✅ Trade 1 contract ONLY
2. ✅ Respect weekly limit (-$250)
3. ✅ Monitor cumulative P&L
4. ✅ Never trade unattended
5. ✅ Test in sim first
6. ✅ NT8 privileges precious

---

*Print this card and keep at your trading desk*
*For full documentation: NT8_SETUP_GUIDE.md*
