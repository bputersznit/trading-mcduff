# Trading Timeframe Strategies Guide

## Overview

Your system is optimized for **intraday trading** across multiple timeframes. This guide explores the two most effective approaches:

1. **Active Day Trading** (Seconds to Minutes)
2. **Intraday Swing** (Minutes to Hours)

---

## 🎯 Active Day Trading (Seconds to Minutes)

### Timeframe: 10 seconds - 5 minutes

### Core Characteristics

| Aspect | Details |
|--------|---------|
| **Hold Time** | 10-60 seconds average |
| **Entry Signal** | Order flow (sweeps, imbalance, absorption) |
| **Target Size** | 5-15 ticks ($1.25 - $3.75 per contract) |
| **Stop Size** | 3-7 ticks ($0.75 - $1.75 per contract) |
| **R:R Ratio** | 1.5:1 to 3:1 |
| **Win Rate** | 65-75% |
| **Trades/Day** | 20-50 potential setups |

### Strategy Details

#### Entry Triggers (Order Flow Based)

1. **Sweep Detection**
   - Aggressive taker sweeping through multiple price levels
   - Indicates strong directional conviction
   - Entry: Same direction as sweep
   ```python
   if signal.signal_type == "sweep" and signal.strength > 50:
       entry_side = signal.side  # Follow the aggressor
   ```

2. **Imbalance Detection**
   - Buy/Sell pressure ratio > 2:1
   - Indicates one-sided market
   - Entry: Direction of imbalance
   ```python
   if signal.signal_type == "imbalance" and signal.details['ratio'] > 2.0:
       entry_side = signal.side
   ```

3. **Absorption**
   - Large passive order absorbing aggression
   - Indicates strong support/resistance
   - Entry: Fade the aggression (contrarian)
   ```python
   if signal.signal_type == "absorption" and signal.details['size'] > 50:
       entry_side = opposite_side(signal.side)  # Fade
   ```

#### Risk Management

**Tight Stops:**
- 5 ticks maximum ($1.25 per contract)
- Quick exit if wrong
- Preserve capital for next setup

**Quick Targets:**
- 10-15 ticks ($2.50-$3.75 per contract)
- Don't overstay
- Take profits quickly

**Example Trade:**
```
Entry: Buy 5 @ 20000.25 (sweep detected)
Stop:  19999.00 (-5 ticks / -$6.25 total)
Target: 20003.00 (+11 ticks / +13.75 total)
R:R: 2.2:1
Duration: 15 seconds
```

### System Features Used

- **Order Flow Analyzer**: Detect sweeps, imbalance, absorption
- **Delta Tracking**: Monitor cumulative volume delta
- **Market Orders**: Fast execution on signals
- **Stop-Market Orders**: Tight protective stops
- **Performance Metrics**: Track high-frequency execution

### Code Example

```python
# Active day trading setup
analyzer = OrderFlowAnalyzer()
fill_model = StrictFillModel()
stop_mgr = StopOrderManager(fill_model)

# Process order flow
for event in market_events:
    signals = analyzer.process_event(event, event_type, best_bid, best_ask)

    for signal in signals:
        if signal.signal_type == "sweep" and signal.strength > 60:
            # Enter with market order
            fill = fill_model.simulate_market_buy(5, book)

            # Place tight stop
            stop_mgr.place_stop_market(
                "stop_1", "SELL", 5,
                fill.avg_fill_price - 1.25,  # 5 ticks
                ts_ns
            )

            # Target: 10-12 ticks
            target_price = fill.avg_fill_price + 3.00
```

### When to Use Active Day Trading

✅ **Choose this if you:**
- Have sub-millisecond execution capability
- Can monitor markets continuously
- Enjoy fast-paced trading
- Have access to Level 3 MBO data
- Can handle 20-50 trades per session
- Have very low commission structure
- Want to capitalize on microstructure inefficiencies

❌ **Avoid if you:**
- Have high latency (>10ms)
- Can't monitor continuously
- Prefer lower trade frequency
- Don't have order flow data
- Have high commission costs

---

## 📊 Intraday Swing (Minutes to Hours)

### Timeframe: 15 minutes - 3 hours

### Core Characteristics

| Aspect | Details |
|--------|---------|
| **Hold Time** | 30 minutes - 3 hours |
| **Entry Signal** | Trend + pullback/breakout |
| **Target Size** | 50-100 ticks ($12.50 - $25.00 per contract) |
| **Stop Size** | 30-50 ticks ($7.50 - $12.50 per contract) |
| **R:R Ratio** | 2:1 to 3:1 |
| **Win Rate** | 60-70% |
| **Trades/Day** | 2-5 quality setups |

### Strategy Details

#### Entry Triggers (Trend Based)

1. **Pullback Entry**
   - Identify uptrend (higher highs, higher lows)
   - Wait for pullback to support
   - Enter with limit order at support
   ```python
   # Enter on pullback to moving average or support
   entry_price = support_level
   fill_model.place_passive_limit("BUY", entry_price, 5, book, ts_ns)
   ```

2. **Breakout Entry**
   - Identify consolidation range
   - Enter on breakout above resistance
   - Use market order for immediate fill
   ```python
   if price > resistance_level:
       fill = fill_model.simulate_market_buy(5, book)
   ```

3. **Trend Continuation**
   - Strong trend already established
   - Enter on minor retracement
   - Trail stop to maximize profits

#### Risk Management

**Wider Stops:**
- 30-50 ticks ($7.50-$12.50 per contract)
- Allow room for normal volatility
- Don't get shaken out prematurely

**Larger Targets:**
- 50-100 ticks ($12.50-$25.00 per contract)
- Let winners run
- Use trailing stops to lock in profits

**Trailing Stops:**
```python
trail = stop_mgr.place_trailing_stop_market(
    "trail_1", "SELL", 5,
    entry_price - 10.00,  # Initial stop: 40 ticks
    trail_offset=5.00,     # Trail by 20 ticks
    ts_ns=ts
)
```

**Example Trade:**
```
Entry: Buy 5 @ 20000.00 (pullback to support)
Initial Stop: 19990.00 (-40 ticks / -$50.00 total)
Trail Offset: $5.00 (20 ticks)

Price Movement:
  t=15min:  20005.00 (+20 ticks) - Stop: 19990.00
  t=30min:  20015.00 (+60 ticks) - Stop: 19990.00
  t=45min:  20025.00 (+100 ticks) - Stop: 19990.00
  t=60min:  20030.00 (+120 ticks) - Stop: 19990.00
  t=75min:  20035.00 (+140 ticks) - Stop: 19990.00
  t=90min:  20028.00 (reversal) - Trail stop hit

Exit: 20028.00 (+112 ticks / +$140.00 total)
Duration: 90 minutes
Peak was +$175.00, locked in $140.00
```

### System Features Used

- **Bracket Orders**: Automatic stop + target placement
- **Trailing Stops**: Dynamic profit protection
- **Limit Orders**: Patient entries at good prices
- **Performance Metrics**: Track swing trade quality
- **MAE/MFE Analysis**: Optimize stop placement

### Code Example

```python
# Intraday swing setup
fill_model = StrictFillModel(assume_queue_position="front")
stop_mgr = StopOrderManager(fill_model)
oco_mgr = OCOBracketManager(fill_model, stop_mgr)

# Pullback entry with trailing stop
entry_price = support_level  # e.g., 20000.00

# Place limit order at support
passive_order = fill_model.place_passive_limit(
    "BUY", entry_price, 5, book, ts_ns
)

# Once filled, place trailing stop
if passive_order.filled_qty > 0:
    trail = stop_mgr.place_trailing_stop_market(
        "trail_1", "SELL", 5,
        entry_price - 10.00,  # 40 ticks initial stop
        trail_offset=5.00,     # Trail by $5 (20 ticks)
        ts_ns=ts
    )

# Or use bracket order for breakout
bracket = oco_mgr.create_bracket_order(
    bracket_id="swing_1",
    entry_side="BUY",
    entry_qty=5,
    entry_price=breakout_level,
    stop_price=breakout_level - 7.50,   # 30 ticks
    target_price=breakout_level + 20.00, # 80 ticks
    entry_type="market",
    book=book,
    ts_ns=ts,
)
```

### When to Use Intraday Swing

✅ **Choose this if you:**
- Prefer fewer, higher-quality trades
- Can tolerate wider drawdowns
- Want to avoid overtrading
- Focus on trends and patterns
- Can step away during trades
- Have normal retail execution (50-100ms)
- Want better risk:reward ratios
- Prefer lower stress trading

❌ **Avoid if you:**
- Can't tolerate drawdowns
- Need constant action
- Struggle with patience
- Can't identify trends
- Want guaranteed quick exits

---

## 📈 Side-by-Side Comparison

### Performance Comparison

| Metric | Active Day Trading | Intraday Swing |
|--------|-------------------|----------------|
| Trades | 3 | 2 |
| Win Rate | 66.7% | 100.0% |
| Total PnL | $21.25 | $240.00 |
| Avg Win | $13.75 | $120.00 |
| Avg Loss | $-6.25 | $0.00 |
| Profit Factor | 4.40 | inf |
| Avg Duration | 12 seconds | 105 minutes |

### Key Insights

1. **Active Day Trading:**
   - More trades = more opportunities
   - Smaller wins, but adds up
   - Requires constant monitoring
   - Higher win rate (tight stops)
   - Lower profit per trade

2. **Intraday Swing:**
   - Fewer trades = less stress
   - Larger wins when right
   - Can step away (trailing stops)
   - Lower win rate (wider stops)
   - Higher profit per trade

---

## 🔄 Hybrid Approach

Many successful traders combine both:

### Morning: Active Day Trading
- First 90 minutes after open
- High volatility, tight spreads
- Order flow signals abundant
- 5-10 scalping trades

### Midday/Afternoon: Intraday Swing
- Identify trend from morning
- Enter on pullback
- Trail stops for rest of session
- 1-2 swing trades

### Example Day:
```
09:30 - 11:00: Active scalping (8 trades, $50-100 profit)
11:00 - 12:00: Identify trend, wait for pullback
12:30:        Enter swing trade with trailing stop
14:30:        Trail stop hit, lock in $200 profit

Total: 9 trades, $250-300 profit
```

---

## 🛠️ System Configuration

### For Active Day Trading

```python
# Tight execution, fast fills
fill_model = StrictFillModel(
    assume_queue_position="front"  # Assume best case
)

# Aggressive order flow detection
analyzer = OrderFlowAnalyzer(
    imbalance_threshold=2.0,      # Sensitive to imbalance
    absorption_threshold=30,      # Lower threshold
    iceberg_threshold=3,          # Detect faster
)

# Performance tracking for high frequency
metrics = PerformanceMetrics(
    initial_capital=100000.0,
    tick_size=0.25,
)
```

### For Intraday Swing

```python
# Patient execution, realistic queue
fill_model = StrictFillModel(
    assume_queue_position="back"  # Conservative
)

# Trailing stops for trend following
stop_mgr = StopOrderManager(fill_model)

# Track swing trade quality
metrics = PerformanceMetrics(
    initial_capital=100000.0,
    tick_size=0.25,
)

# Focus on MAE/MFE to optimize stops
mae_mfe = metrics.mae_mfe_analysis()
```

---

## 📊 Real Data Usage

### Active Day Trading
Use your MBO replay for:
- Detecting real order flow patterns
- Backtesting sweep/imbalance strategies
- Measuring execution quality
- Optimizing entry timing (milliseconds matter)

```python
# Replay with order flow analysis
for batch_ts, events in grouped_events:
    for event in events:
        signals = analyzer.process_event(event, ...)

        # Act on signals in real-time
        if signals:
            execute_strategy(signals)
```

### Intraday Swing
Use your MBO data for:
- Identifying trend strength
- Finding optimal entry points
- Testing stop placement
- Measuring MAE/MFE

```python
# Aggregate to 1-min or 5-min bars
bars = aggregate_to_bars(events, period="1min")

# Identify trends
trend = detect_trend(bars)

# Find pullback entries
if trend == "bullish" and pullback_detected:
    enter_long()
```

---

## 🎯 Recommendations

### Start With Intraday Swing If You:
- Are learning to trade
- Have limited time
- Want lower stress
- Are testing the system
- Have normal retail execution

**Why:** Wider stops give you breathing room, fewer trades means less to learn, and trailing stops do the work for you.

### Move to Active Day Trading When:
- You've mastered the basics
- You have fast execution (sub-10ms)
- You understand order flow
- You can monitor continuously
- You've proven profitability on swing trades

**Why:** Requires more skill, faster decisions, and better infrastructure. Build foundations first.

---

## 📁 Demo Files

Run these to see both approaches:

```bash
# Compare both strategies side-by-side
python scripts/CGCl_timeframe_strategies_demo.py

# Test individual features
python scripts/CGCl_test_advanced_features.py     # Order flow
python scripts/CGCl_complete_trading_example.py   # Stops/brackets
```

---

## 🚀 Next Steps

1. **Run the demo** to see both strategies in action
2. **Backtest on real data** using your MBO replay
3. **Start with swing trading** to learn the system
4. **Track your metrics** to measure improvement
5. **Graduate to active trading** when ready

---

## 💡 Key Takeaways

### Active Day Trading
- ⚡ **Fast**: 10-60 second holds
- 🎯 **Precise**: Order flow signals
- 📈 **Frequent**: 20-50 trades/day
- 💰 **Small**: $1-4 per contract
- 🔬 **Technical**: Requires infrastructure

### Intraday Swing
- ⏰ **Patient**: 30 min - 3 hour holds
- 📊 **Trendy**: Follow momentum
- 🎲 **Selective**: 2-5 trades/day
- 💵 **Large**: $10-30 per contract
- 🧘 **Relaxed**: Less stressful

**Both are supported by your system with full order flow and execution simulation!**

---

*Generated with Claude Code - Your institutional-grade trading simulator*
