# v4: Chart-Independent Strategy

## Problem Solved

**v3 and earlier** depended on whatever chart you applied the strategy to:
- Apply to 1-minute chart → uses 1-minute bars for trend/levels
- Apply to 5-minute chart → uses 5-minute bars for trend/levels
- Apply to 15-minute chart → uses 15-minute bars for trend/levels

This created **inconsistent behavior** and made the strategy unpredictable.

## v4 Solution: Hardcoded Bar Interval

v4 **controls its own data series** using NinjaTrader's `AddDataSeries()`:

```csharp
// State.Configure - lines 283-286
AddDataSeries(BarsPeriodType.Minute, BarIntervalMinutes);
```

Now the strategy:
- ✅ Works the same regardless of what chart you apply it to
- ✅ Uses configurable bar interval (default: 1 minute)
- ✅ Can be applied to ANY chart (even tick charts, Renko, etc.)
- ✅ Completely self-contained and predictable

---

## Key Technical Changes

### 1. New Parameter: BarIntervalMinutes

```csharp
[NinjaScriptProperty]
[Range(1, 60)]
[Display(Name = "Bar Interval (minutes)", Order = 0, GroupName = "0. Data Series")]
public int BarIntervalMinutes { get; set; }  // Default: 1 minute
```

You can adjust this to:
- **1 minute**: Fast-reacting trend (recommended for scalping)
- **2 minutes**: Balanced
- **5 minutes**: Slower, smoother trend

### 2. Uses BarsArray[1] for All Price Data

v4 maintains TWO data series:
- **BarsArray[0]**: Primary chart (whatever you apply strategy to)
- **BarsArray[1]**: Strategy's own bars (controlled by BarIntervalMinutes)

All price references updated:
```csharp
// OLD (v3):
Close[0]          → Current close
High[0]           → Current high
Low[0]            → Current low
Time[0]           → Current time
CurrentBar        → Bar count

// NEW (v4):
Closes[1][0]      → Current close on strategy bars
Highs[1][0]       → Current high on strategy bars
Lows[1][0]        → Current low on strategy bars
Times[1][0]       → Current time on strategy bars
CurrentBars[1]    → Bar count on strategy bars
```

### 3. OnBarUpdate Filters for Strategy Bars

```csharp
protected override void OnBarUpdate()
{
    // Only process strategy bars (BarsArray[1])
    if (BarsInProgress != 1)
        return;

    // Rest of logic...
}
```

This ensures the strategy only acts on its own bar closes, not the primary chart.

### 4. EMAs Use Strategy Bars

```csharp
// State.DataLoaded - lines 322-323
emaFast = EMA(Closes[1], FastEMA);
emaSlow = EMA(Closes[1], SlowEMA);
```

Trend detection now uses the strategy's own bars, not the chart.

---

## What Stays the Same

### Order Flow Detection (1-second bars)

Still built from tick data in `OnMarketData()`:
- ABSORPTION detection: Every second
- BREAKOUT detection: Every second
- Order book tracking: Real-time

This is INDEPENDENT of both chart and strategy bars.

### All v3 Improvements

v4 includes ALL v3 features:
- ✅ Trend-aware entry detection
- ✅ Near-price order book filtering (5 ticks)
- ✅ Swing high/low context
- ✅ Tighter price action filters (±1 tick)
- ✅ Breakouts must break 10-bar high/low
- ✅ All v2 features (wider stops, RTH, trailing stops)

---

## Usage Example

### Before (v3):
1. Open 1-minute chart of MNQ
2. Apply CGScalpingStrategyNT8Native_v3
3. Strategy uses 1-minute bars for trend/levels

**Problem**: Different behavior on different charts!

### Now (v4):
1. Open **ANY** chart of MNQ (1-min, 5-min, tick, Renko, etc.)
2. Apply CGScalpingStrategyNT8Native_v4
3. Strategy **always** uses its own 1-minute bars (configurable)

**Result**: Consistent behavior everywhere!

---

## Recommended Settings

### For Scalping (Default)
```
BarIntervalMinutes: 1
FastEMA: 9
SlowEMA: 21
```

This gives:
- 10-bar breakout high/low = Last 10 minutes
- Responsive trend detection
- Fast reaction to market shifts

### For More Stability
```
BarIntervalMinutes: 2
FastEMA: 9
SlowEMA: 21
```

This gives:
- 10-bar breakout high/low = Last 20 minutes
- Smoother trend detection
- Less sensitive to whipsaws

### For Trend Trading (Not Scalping)
```
BarIntervalMinutes: 5
FastEMA: 9
SlowEMA: 21
```

This gives:
- 10-bar breakout high/low = Last 50 minutes
- Very smooth trend
- Only catches major moves

---

## How to Test

### 1. Deploy to VPS
```bash
# Copy v4 to VPS
scp /path/to/CGScalpingStrategyNT8Native_v4.cs username@vps:/path/to/NT8/Strategies/
```

### 2. Apply to Chart
- Open any chart (recommend 1-minute for visual clarity)
- Apply CGScalpingStrategyNT8Native_v4
- Verify in Output window:
  ```
  NEW v4 FEATURES:
    ✓ HARDCODED BAR INTERVAL: 1 minute(s)
    ✓ Independent of chart settings
    ✓ Uses BarsArray[1] for all price data
  ```

### 3. Verify Independence
Test that it works the same on different charts:
- Apply to 1-minute chart → Check output
- Apply to 5-minute chart → Check output (should be identical behavior)
- Apply to tick chart → Check output (should be identical behavior)

---

## Migration from v3

All parameters are the same, just add:
- **BarIntervalMinutes** = 1 (for same behavior as v3 on 1-min chart)

Everything else is identical.

---

## Summary

| Feature | v3 | v4 |
|---------|----|----|
| Chart dependency | ❌ Depends on chart | ✅ Independent |
| Bar interval | From chart | Hardcoded (configurable) |
| Trend/levels | Uses chart bars | Uses own bars |
| Order flow | 1-second (same) | 1-second (same) |
| Consistency | Varies by chart | Always the same |
| Professional quality | Good | ✅ Production-ready |

**Bottom line**: v4 is what production strategies should look like - self-contained, predictable, and chart-independent.
