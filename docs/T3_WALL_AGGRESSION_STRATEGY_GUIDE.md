# CG T3 Wall Aggression Strategy v1.0

## Overview

The Wall Aggression strategy combines **liquidity wall detection** from bookmap heatmap data with **aggressive order flow** analysis to identify high-probability trade setups.

## Core Concept

### Data Fusion
- **Heatmap Data**: Identifies significant liquidity walls (large resting orders)
- **Aggression Data**: Tracks aggressive buy/sell executions
- **Signal Generation**: Trades when aggression interacts with walls

### Two Operating Modes

#### 1. Breakout Mode
Trade in the direction of aggression **breaking through** a wall:
- **Long Signal**: Aggressive buying breaks through bid wall → Price likely to surge
- **Short Signal**: Aggressive selling breaks through ask wall → Price likely to drop

**Logic**: When large resting orders are absorbed by aggressive flow, it signals strong directional intent.

#### 2. Rejection Mode
Trade **opposite** the aggression when it's **rejected** by a wall:
- **Long Signal**: Aggressive selling absorbed by bid wall → Price bounces up
- **Short Signal**: Aggressive buying absorbed by ask wall → Price bounces down

**Logic**: Strong walls act as support/resistance, causing price to reverse.

## Data Sources

### Heatmap Table: `BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S`
- Resolution: 5-second buckets
- Provides: `bid_liquidity_event_size`, `ask_liquidity_event_size`
- Purpose: Identify price levels with significant resting liquidity

### Aggression Table: `CG_mnq_aggression_100ms`
- Resolution: 100ms buckets
- Provides: `buy_volume`, `sell_volume`, `delta`
- Purpose: Measure aggressive buying/selling pressure

## Strategy Parameters

### 1. Wall Detection
- **Wall Threshold** (5000): Minimum liquidity to qualify as a wall
- **Wall Distance** (5 ticks): Max distance from price to consider

### 2. Aggression Analysis
- **Aggression Threshold** (1000): Minimum volume for signal
- **Aggression Ratio** (2.0): Buy/Sell ratio for directional bias

### 3. Entry Logic
- **Mode**: Breakout or Rejection

### 4. Risk Management
- **Profit Target** (10 ticks): Take profit level
- **Stop Loss** (5 ticks): Stop loss level
- **Max Bars In Position** (50): Time-based exit

### 5. Performance
- **Query Interval** (5 seconds): Database polling frequency

## Installation

### Prerequisites
1. **NinjaTrader 8** installed
2. **ClickHouse** running with data tables populated:
   - `BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S`
   - `CG_mnq_aggression_100ms`
3. **ClickHouse.Client** NuGet package referenced

### Steps
1. Copy `CG_T3_WallAggression_v1_0.cs` to:
   ```
   Documents/NinjaTrader 8/bin/Custom/Strategies/
   ```

2. In NinjaTrader:
   - Tools → Compile → Compile (F5)
   - Fix any errors (mainly ClickHouse references)

3. Apply to chart:
   - Strategies → CG_T3_WallAggression_v1_0
   - Configure parameters
   - Enable strategy

## Usage Guide

### Recommended Settings for Breakout Mode
```
Wall Threshold: 5000
Wall Distance: 5 ticks
Aggression Threshold: 1000
Aggression Ratio: 2.0
Mode: Breakout
Profit Target: 10 ticks
Stop Loss: 5 ticks
```

**Best for**: Trending markets, strong momentum

### Recommended Settings for Rejection Mode
```
Wall Threshold: 8000 (higher threshold for stronger walls)
Wall Distance: 3 ticks
Aggression Threshold: 1500
Aggression Ratio: 2.5
Mode: Rejection
Profit Target: 8 ticks
Stop Loss: 4 ticks
```

**Best for**: Range-bound markets, mean reversion

## How It Works (Step-by-Step)

### Every 5 Seconds
1. **Query Heatmap Data**:
   - Find prices within N ticks of current price
   - Identify bid/ask walls above threshold
   - Store wall locations and sizes

2. **Query Aggression Data**:
   - Sum last 5 seconds of buy/sell volume
   - Calculate aggression delta
   - Determine directional bias

3. **Evaluate Signals**:
   - Check if price near wall
   - Check if aggression meets thresholds
   - Check buy/sell ratio
   - Generate entry signal if conditions met

### Entry Examples

#### Breakout Long
```
Current Price: 20,500
Bid Wall: 20,499 (6000 contracts)
Recent Buy Volume: 2000
Recent Sell Volume: 500
Ratio: 4.0 (> 2.0 threshold)
Delta: +1500 (positive)

→ ENTER LONG (breakout through support becomes new floor)
```

#### Rejection Short
```
Current Price: 20,505
Ask Wall: 20,506 (7000 contracts)
Recent Buy Volume: 2500
Recent Sell Volume: 800
Ratio: 3.1 (> 2.5 threshold)
Delta: +1700 (positive)

→ ENTER SHORT (buying absorbed by wall, price to reverse)
```

## Performance Considerations

### Database Load
- Queries every 5 seconds (default)
- Each query: ~50-100ms
- Minimal impact on strategy performance

### Optimization Tips
1. **Increase query interval** (10s) for less active markets
2. **Reduce wall distance** (3 ticks) to focus on nearest walls
3. **Use 1S heatmap** for higher resolution (more queries)
4. **Cache wall data** between bars to reduce DB calls

## Backtesting

### Market Replay Setup
1. Enable Market Replay in NinjaTrader
2. Select date range (Sept 21 - Oct 22, 2025)
3. Ensure ClickHouse has data for replay dates
4. Run strategy on 1-minute or 5-minute bars

### Key Metrics to Track
- **Win Rate**: Target >55% for breakout, >60% for rejection
- **Avg Win/Loss Ratio**: Target >1.5:1
- **Max Drawdown**: Monitor risk exposure
- **Trades Per Day**: Should see 5-15 signals in active markets

## Troubleshooting

### No Trades Generated
- Check ClickHouse connection in Output window
- Verify data exists for current date
- Lower wall threshold to detect more walls
- Lower aggression threshold for more signals

### Too Many Trades
- Raise wall threshold (filter weaker walls)
- Raise aggression ratio (require stronger imbalance)
- Increase wall distance filter

### Poor Performance
- Try opposite mode (Breakout ↔ Rejection)
- Adjust profit target / stop loss ratio
- Filter by time (avoid choppy open/close)

## Advanced Enhancements

### Potential Improvements
1. **Volume Profile Integration**: Weight walls by historical volume
2. **Multi-Timeframe Confirmation**: Check 1S and 5S walls align
3. **Trend Filter**: Only take breakout trades with trend
4. **Wall Strength Decay**: Reduce old wall weight over time
5. **Iceberg Detection**: Identify refreshing walls (hidden orders)

### Custom Indicators
- Create indicator to draw walls on chart
- Visualize aggression flow as heatmap overlay
- Plot real-time buy/sell ratio

## Example Scenarios

### Scenario 1: Strong Breakout
```
9:45 AM - Large bid wall detected at 20,500 (8000 contracts)
9:46 AM - Price testing wall, moderate selling
9:47 AM - Massive buy aggression (3000 volume, ratio 5:1)
9:47 AM - ENTER LONG at 20,501
9:48 AM - Price surges to 20,511
9:48 AM - EXIT at 20,511 (+10 ticks profit)
```

### Scenario 2: Failed Breakout (Rejection)
```
2:15 PM - Ask wall at 20,520 (6000 contracts)
2:16 PM - Heavy buying into wall (2000 volume)
2:17 PM - Wall absorbs all buying, price stalls
2:17 PM - ENTER SHORT at 20,519 (rejection mode)
2:18 PM - Price drops to 20,511
2:18 PM - EXIT at 20,511 (+8 ticks profit)
```

## References

- **Heatmap Rebuild**: `sql/bm_rollups_v1_3/rebuild_heatmap_worker_1S_hourly.py`
- **Aggression Data**: `CG_mnq_aggression_100ms` table
- **Multi-Scale Rollups**: `BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_*` tables

## Version History

### v1.0 (2026-05-12)
- Initial release
- Breakout and Rejection modes
- 5S heatmap + 100ms aggression fusion
- Real-time ClickHouse integration
