# CG Order Flow Imbalance Strategy - NinjaTrader Guide

## Overview

**File**: `CG_OrderFlow_Imbalance_v1_0.cs`
**Strategy**: Order flow imbalance detection in 100ms buckets
**Tested Performance**: 63% win rate, $6.36/trade expectancy
**Instrument**: MNQ (Micro E-mini Nasdaq-100)

---

## Installation

### Step 1: Import to NinjaTrader

1. Copy `ninjascript/CG_OrderFlow_Imbalance_v1_0.cs`
2. Open NinjaTrader 8
3. Go to **Tools → Import → NinjaScript Add-On**
4. Browse to the .cs file and import
5. **OR** manually:
   - Navigate to `Documents\NinjaTrader 8\bin\Custom\Strategies\`
   - Copy the .cs file there
   - In NT8: **Tools → Compile** (F5)

### Step 2: Verify Compilation

- Check **Tools → Output Window** for errors
- Should see: "Compilation successful"

---

## Strategy Logic

### Signal Generation

**LONG Signal:**
```
Bid volume - Ask volume > 50 contracts  AND
Bid% > 60% of total volume
```

**SHORT Signal:**
```
Ask volume - Bid volume > 50 contracts  AND
Ask% > 60% of total volume
```

**Time Resolution:** 100ms buckets (configurable)

### Entry/Exit

| Parameter | Value | MNQ Profit/Loss |
|-----------|-------|-----------------|
| **Target** | 40 ticks | $20.00 |
| **Stop** | 20 ticks | $10.00 |
| **Timeout** | 10 minutes | Exit at market |
| **Risk/Reward** | 1:2 | Expectancy: $6.36 |

### Filters Applied

**Opening Range (9:30-9:45 AM ET):**
- Tracks or_high and or_low
- Classifies price as ABOVE_OR, BELOW_OR, INSIDE_OR

**Manipulation Filters (6 rules):**
1. No shorts during OPEN_15 (9:00-9:45)
2. No shorts POST_OPEN when ABOVE_OR
3. No shorts POST_OPEN when INSIDE_OR
4. No longs NORMAL when INSIDE_OR
5. No shorts CLOSE_30 when ABOVE_OR
6. No longs CLOSE_30 when BELOW_OR

**Daily Risk Limits:**
- Max daily loss: $60
- Max consecutive losses: 3
- Profit lock: Stop at $500 drawdown from $3,000 peak

---

## Configuration Parameters

### 1. Signal Parameters

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Bucket Size (ms)** | 100 | 50-500 | Time window for event aggregation |
| **Min Event Delta** | 50 | 10-200 | Minimum volume imbalance (contracts) |
| **Min Event Imbalance** | 0.60 | 0.3-0.9 | Minimum imbalance ratio (60% = 0.60) |

### 2. Risk Management

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Target (ticks)** | 40 | 10-100 | Profit target |
| **Stop (ticks)** | 20 | 5-50 | Stop loss |
| **Timeout (minutes)** | 10 | 1-30 | Max hold time |

### 3. Daily Limits

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Max Daily Loss ($)** | 60 | 30-500 | Stop trading after loss |
| **Max Consecutive Losses** | 3 | 1-10 | Stop after X losses |
| **Profit Lock Peak ($)** | 3000 | 500-10000 | Peak profit threshold |
| **Profit Lock Drawdown ($)** | 500 | 100-2000 | Drawdown to trigger lock |

### 4. Filters

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Enable Opening Range Filter** | True | Use OR-based manipulation filters |
| **Enable Manipulation Filters** | True | Apply 6 manipulation rules |
| **Enable Daily Limits** | True | Apply daily loss/profit limits |

### 5. RTH Hours (Eastern Time)

| Parameter | Default | Description |
|-----------|---------|-------------|
| **RTH Start Hour** | 9 | Start hour (24h format) |
| **RTH Start Minute** | 30 | Start minute |
| **RTH End Hour** | 16 | End hour |
| **RTH End Minute** | 0 | End minute |

---

## Setup in NinjaTrader

### For Strategy Analyzer (Backtesting)

**Requirements:**
- **Data**: Market Replay data with Level 2 (Market Depth)
- **Instrument**: MNQ (Micro E-mini Nasdaq-100)
- **Data Series**: Any (1 min recommended for chart display)

**Steps:**
1. Open **New → Strategy Analyzer**
2. Select **Strategy**: CG_OrderFlow_Imbalance_v1_0
3. Instrument: **MNQ 12-24** (or current contract)
4. Data Series: **1 Minute**
5. **IMPORTANT**: Enable **Market Replay** mode
6. Set date range (e.g., Sep 24 - Oct 22, 2025)
7. Click **Run**

**Note:** This strategy REQUIRES Market Depth data. Standard historical data won't work - you must use Market Replay recordings.

### For Live Trading

**Steps:**
1. Open chart for **MNQ** (current contract)
2. Right-click chart → **Strategies**
3. Add: **CG_OrderFlow_Imbalance_v1_0**
4. Configure parameters (see above)
5. **Enable**: Check "Enabled" box
6. Click **OK**

**Important Checks:**
- ✅ Market Depth subscription active
- ✅ RTH hours correct (Eastern Time)
- ✅ Account has sufficient margin ($500+ recommended for MNQ)
- ✅ Risk limits appropriate for account size

---

## Expected Performance

Based on 904-trade backtest (Sep 24 - Oct 22, 2025):

| Metric | Value |
|--------|-------|
| **Total Trades** | 904 |
| **Win Rate** | 63.27% |
| **Total P/L** | $5,749.70 |
| **Avg per Trade** | $6.36 |
| **Avg Winner** | $17.41 |
| **Avg Loser** | -$12.86 |
| **Daily Avg** | $239/day |
| **Max Daily Gain** | ~$3,000 (profit lock) |
| **Max Daily Loss** | -$60 (daily limit) |

**Slippage/Commission:** Already modeled in backtest results

---

## Monitoring & Troubleshooting

### Output Window Messages

Strategy prints key events:

```
ENTRY LONG @ 20125.50 | TZ: POST_OPEN | OR: ABOVE_OR | EventSize: 143
FILL Long @ 20125.50 | Target: 20135.50 | Stop: 20120.50
EXIT OFI_Target | P&L: $17.30 | Daily: $245.60 | Peak: $1250.00
```

### Common Issues

**1. No trades executing:**
- Check if Market Depth is subscribed
- Verify RTH hours match your timezone
- Check if daily limits already hit
- Ensure Opening Range calculated (after 9:45 AM ET)

**2. Strategy not loading:**
- Check compilation errors (F5)
- Verify all using declarations at top
- Check NinjaTrader version (requires NT8)

**3. Performance differs from backtest:**
- Market Replay data quality (need L2 depth)
- Different market conditions
- Slippage in live vs. backtest
- Commission/exchange fees

---

## Optimization Notes

### Parameters to Test

**Conservative (Higher win rate, lower volume):**
- Min Event Delta: 75
- Min Event Imbalance: 0.70
- Bucket Size: 150ms

**Aggressive (Lower win rate, higher volume):**
- Min Event Delta: 30
- Min Event Imbalance: 0.50
- Bucket Size: 50ms

**Risk-Adjusted:**
- Target: 30 ticks ($15)
- Stop: 15 ticks ($7.50)
- Still 1:2 R/R but tighter

### Walk-Forward Testing

Recommended approach:
1. Optimize on 2 weeks of data
2. Test on following 1 week
3. Re-optimize monthly
4. Track parameter drift

---

## Data Requirements

### Market Replay

**Required for backtesting:**
- CME Market Depth data
- Recorded via NinjaTrader Market Replay
- Must include Level 2 order book events

**Where to get:**
1. Record your own: **Connect → Playback Connection → Record**
2. Purchase: NinjaTrader Market Replay subscriptions
3. Use: Existing recordings from trading days

**File location:**
`Documents\NinjaTrader 8\db\replay\`

### Live Data

**Required feed:**
- CME Real-Time Data (includes Market Depth)
- CQG, Rithmic, or NinjaTrader Continuum

**Cost:** ~$50-100/month for MNQ data

---

## Risk Disclaimer

**This strategy:**
- Uses Market Depth/Order Flow data (may lag or be unreliable)
- Has been backtested on LIMITED historical data (24 days)
- May not perform in different market regimes
- Includes manipulation filters that may become obsolete
- Requires fast execution and low latency

**Recommended:**
- Test thoroughly in simulation first
- Start with 1 contract only
- Monitor for 1-2 weeks before scaling
- Use appropriate position sizing for account
- Understand you can lose more than expected

**Position Sizing:**
- MNQ margin: ~$1,500 per contract
- Recommended account: $5,000+ per contract
- Max risk per trade: $10-15 (stop + slippage)
- Daily risk limit: $60

---

## Version History

**v1.0 (May 14, 2026)**
- Initial release
- Based on ClickHouse backtest results
- 100ms bucket aggregation
- 6 manipulation filters
- Opening Range awareness
- Daily risk limits

---

## Support & Modifications

**File location:**
`Documents\NinjaTrader 8\bin\Custom\Strategies\CG_OrderFlow_Imbalance_v1_0.cs`

**To modify:**
1. Edit .cs file in text editor
2. Tools → Compile (F5) in NT8
3. Reload strategy on chart

**Common modifications:**
- Line 105: Bucket size logic
- Line 387-415: Manipulation filters
- Line 451-476: Daily tracking logic
- Line 185: Entry price calculation (slippage model)

---

## Contact

For questions or issues with this strategy implementation, refer to the backtest documentation:
- `docs/CG_v5_OPTIMIZED_chain.sql` - SQL backtest logic
- `CG_mnq_hybrid_v5_CORRECTED_trades.csv` - Reference trade list

**Note**: This is a research/educational implementation. Always test thoroughly before live trading.
