# Strategy Logic Improvements - Trend-Aware Entry Detection

## Problem: Current Strategy is Trend-Blind

On a +400pt bull day:
- Longs: +$42.50 (riding the trend)
- Shorts: -$26.50 (fighting the trend)

The current logic treats all signals equally regardless of market direction.

## Solution: Trend-Aware, Asymmetric Entry Requirements

---

## 1. ABSORPTION Detection - IMPROVED

### Current Issues:
1. Uses total order book size (including far levels)
2. No price level context
3. Same requirements for with-trend vs counter-trend
4. Too loose price change filter (±2 ticks)

### Improved Logic:

```csharp
private Signal DetectAbsorption()
{
    if (orderFlowHistory.Count < 5)
        return null;

    OrderFlowBar lastBar = orderFlowHistory[orderFlowHistory.Count - 1];
    OrderFlowBar prevBar = orderFlowHistory[orderFlowHistory.Count - 2];

    // Accumulate recent volume
    long recentSellVolume = lastBar.SellVolume + prevBar.SellVolume;
    long recentBuyVolume = lastBar.BuyVolume + prevBar.BuyVolume;

    // IMPROVED: Get trend direction
    bool upTrend = emaFast[0] > emaSlow[0];
    bool downTrend = emaFast[0] < emaSlow[0];
    double trendStrength = Math.Abs(emaFast[0] - emaSlow[0]);

    // SELL ABSORPTION → LONG signal (fade sellers)
    if (recentSellVolume > AbsorptionMinAggressor)
    {
        // IMPROVED: Only check bids near current price (within 5 ticks)
        double nearBidLevels = 0;
        double currentPrice = Close[0];

        foreach (var kvp in lastBar.BidSizeByPrice)
        {
            if (kvp.Key >= currentPrice - 5 * TickSize)  // Only near bids
                nearBidLevels += kvp.Value;
        }

        // IMPROVED: Stricter absorption ratio
        if (nearBidLevels > recentSellVolume * AbsorptionRatio)
        {
            // IMPROVED: Tighter price action filter
            double priceChange = Close[0] - Close[3];  // Over 3 bars

            // Must hold within 1 tick despite selling
            if (priceChange > -1 * TickSize)
            {
                // IMPROVED: Asymmetric requirements based on trend
                bool validSignal = false;
                int confidenceBoost = 0;

                if (upTrend)
                {
                    // WITH TREND (easier requirements)
                    validSignal = true;
                    confidenceBoost = 2;  // Higher confidence
                }
                else if (downTrend)
                {
                    // COUNTER TREND (stricter requirements)
                    // Require MUCH stronger absorption
                    if (nearBidLevels > recentSellVolume * (AbsorptionRatio + 0.5))
                    {
                        // And must be near support (e.g., recent swing low)
                        if (IsNearSwingLow(currentPrice))
                        {
                            validSignal = true;
                            confidenceBoost = 0;  // Lower confidence
                        }
                    }
                }
                else
                {
                    // NEUTRAL (standard requirements)
                    validSignal = true;
                    confidenceBoost = 1;
                }

                if (validSignal)
                {
                    return new Signal
                    {
                        Type = "ABSORPTION",
                        Direction = MarketPosition.Long,
                        Price = Close[0],
                        Strength = (int)recentSellVolume + confidenceBoost * 10
                    };
                }
            }
        }
    }

    // BUY ABSORPTION → SHORT signal (fade buyers)
    if (recentBuyVolume > AbsorptionMinAggressor)
    {
        // IMPROVED: Only check asks near current price (within 5 ticks)
        double nearAskLevels = 0;
        double currentPrice = Close[0];

        foreach (var kvp in lastBar.AskSizeByPrice)
        {
            if (kvp.Key <= currentPrice + 5 * TickSize)  // Only near asks
                nearAskLevels += kvp.Value;
        }

        // IMPROVED: Stricter absorption ratio
        if (nearAskLevels > recentBuyVolume * AbsorptionRatio)
        {
            // IMPROVED: Tighter price action filter
            double priceChange = Close[0] - Close[3];  // Over 3 bars

            // Must hold within 1 tick despite buying
            if (priceChange < 1 * TickSize)
            {
                // IMPROVED: Asymmetric requirements based on trend
                bool validSignal = false;
                int confidenceBoost = 0;

                if (downTrend)
                {
                    // WITH TREND (easier requirements)
                    validSignal = true;
                    confidenceBoost = 2;  // Higher confidence
                }
                else if (upTrend)
                {
                    // COUNTER TREND (stricter requirements)
                    // Require MUCH stronger absorption
                    if (nearAskLevels > recentBuyVolume * (AbsorptionRatio + 0.5))
                    {
                        // And must be near resistance (e.g., recent swing high)
                        if (IsNearSwingHigh(currentPrice))
                        {
                            validSignal = true;
                            confidenceBoost = 0;  // Lower confidence
                        }
                    }
                }
                else
                {
                    // NEUTRAL (standard requirements)
                    validSignal = true;
                    confidenceBoost = 1;
                }

                if (validSignal)
                {
                    return new Signal
                    {
                        Type = "ABSORPTION",
                        Direction = MarketPosition.Short,
                        Price = Close[0],
                        Strength = (int)recentBuyVolume + confidenceBoost * 10
                    };
                }
            }
        }
    }

    return null;
}

// Helper: Check if near recent swing low
private bool IsNearSwingLow(double price)
{
    if (CurrentBar < 20) return false;

    double recentLow = Math.Min(Low[0], Math.Min(Low[1], Low[2]));
    for (int i = 3; i < 20; i++)
    {
        recentLow = Math.Min(recentLow, Low[i]);
    }

    return Math.Abs(price - recentLow) < 3 * TickSize;
}

// Helper: Check if near recent swing high
private bool IsNearSwingHigh(double price)
{
    if (CurrentBar < 20) return false;

    double recentHigh = Math.Max(High[0], Math.Max(High[1], High[2]));
    for (int i = 3; i < 20; i++)
    {
        recentHigh = Math.Max(recentHigh, High[i]);
    }

    return Math.Abs(price - recentHigh) < 3 * TickSize;
}
```

### Key Improvements:
1. ✅ **Only checks near order book levels** (within 5 ticks)
2. ✅ **Tighter price action** (must hold within 1 tick, over 3 bars)
3. ✅ **Trend-aware asymmetry:**
   - With-trend: Easier entry (standard requirements)
   - Counter-trend: Much stricter (needs stronger absorption + key level)
4. ✅ **Confidence scoring** affects position sizing or can filter trades

---

## 2. BREAKOUT Detection - IMPROVED

### Current Issues:
1. No price level context (breaking what?)
2. No trend awareness
3. No confirmation
4. One-bar spike can be a trap

### Improved Logic:

```csharp
private Signal DetectBreakout()
{
    if (orderFlowHistory.Count < 30 || CurrentBar < 20)
        return null;

    // Calculate baseline
    var last30 = orderFlowHistory.Skip(orderFlowHistory.Count - 30).Take(29).ToList();
    double avgVolume = last30.Average(b => b.TotalVolume);
    double avgDelta = last30.Average(b => Math.Abs(b.AggressorDelta));

    OrderFlowBar lastBar = orderFlowHistory[orderFlowHistory.Count - 1];
    OrderFlowBar prevBar = orderFlowHistory[orderFlowHistory.Count - 2];

    // IMPROVED: Get trend and recent price levels
    bool upTrend = emaFast[0] > emaSlow[0];
    bool downTrend = emaFast[0] < emaSlow[0];

    // Recent 10-bar high/low
    double recent10High = High[0];
    double recent10Low = Low[0];
    for (int i = 1; i < 10; i++)
    {
        recent10High = Math.Max(recent10High, High[i]);
        recent10Low = Math.Min(recent10Low, Low[i]);
    }

    // IMPROVED: Require volume spike AND breaking a level
    if (lastBar.TotalVolume > avgVolume * BreakoutVolumeSpike &&
        Math.Abs(lastBar.AggressorDelta) > avgDelta * 2.5)
    {
        // BULLISH BREAKOUT
        if (lastBar.AggressorDelta > 0)
        {
            // IMPROVED: Must be breaking above recent high
            if (Close[0] > recent10High + TickSize)
            {
                // IMPROVED: Asymmetric requirements
                bool validSignal = false;
                int confidenceBoost = 0;

                if (upTrend)
                {
                    // WITH TREND (easier - standard requirements)
                    validSignal = true;
                    confidenceBoost = 2;
                }
                else if (downTrend)
                {
                    // COUNTER TREND (much stricter)
                    // Require MASSIVE volume (3.5x instead of 2.5x)
                    if (lastBar.TotalVolume > avgVolume * (BreakoutVolumeSpike + 1.0))
                    {
                        // And sustained buying over 2 bars
                        if (prevBar.AggressorDelta > avgDelta * 2.0)
                        {
                            validSignal = true;
                            confidenceBoost = -1;  // Lower confidence
                        }
                    }
                }
                else
                {
                    // NEUTRAL (standard)
                    validSignal = true;
                    confidenceBoost = 1;
                }

                if (validSignal)
                {
                    return new Signal
                    {
                        Type = "BREAKOUT",
                        Direction = MarketPosition.Long,
                        Price = Close[0],
                        Strength = (int)Math.Abs(lastBar.AggressorDelta) + confidenceBoost * 10
                    };
                }
            }
        }
        // BEARISH BREAKOUT
        else
        {
            // IMPROVED: Must be breaking below recent low
            if (Close[0] < recent10Low - TickSize)
            {
                // IMPROVED: Asymmetric requirements
                bool validSignal = false;
                int confidenceBoost = 0;

                if (downTrend)
                {
                    // WITH TREND (easier - standard requirements)
                    validSignal = true;
                    confidenceBoost = 2;
                }
                else if (upTrend)
                {
                    // COUNTER TREND (much stricter)
                    // Require MASSIVE volume
                    if (lastBar.TotalVolume > avgVolume * (BreakoutVolumeSpike + 1.0))
                    {
                        // And sustained selling over 2 bars
                        if (prevBar.AggressorDelta < -avgDelta * 2.0)
                        {
                            validSignal = true;
                            confidenceBoost = -1;  // Lower confidence
                        }
                    }
                }
                else
                {
                    // NEUTRAL (standard)
                    validSignal = true;
                    confidenceBoost = 1;
                }

                if (validSignal)
                {
                    return new Signal
                    {
                        Type = "BREAKOUT",
                        Direction = MarketPosition.Short,
                        Price = Close[0],
                        Strength = (int)Math.Abs(lastBar.AggressorDelta) + confidenceBoost * 10
                    };
                }
            }
        }
    }

    return null;
}
```

### Key Improvements:
1. ✅ **Must break recent high/low** (10-bar range)
2. ✅ **Trend-aware asymmetry:**
   - With-trend: Standard volume requirement (2.5x)
   - Counter-trend: Much higher volume (3.5x) + 2-bar confirmation
3. ✅ **Confidence scoring** for position sizing
4. ✅ **Two-bar confirmation** for counter-trend trades

---

## Expected Impact on +400pt Bull Day:

### Original Results:
- Longs: +$42.50
- Shorts: -$26.50
- Net: +$16.00

### With Improvements (Estimated):
- **Longs:** +$50 to +$60 (slight improvement, already good)
- **Shorts:** -$5 to +$5 (huge improvement, fewer bad shorts)
- **Net:** +$55 to +$65 (3-4x better!)

### Why:
1. Counter-trend shorts would be filtered out or require extreme confirmation
2. With-trend longs would have same or slightly better entry quality
3. ABSORPTION shorts on a bull day would mostly be rejected
4. BREAKOUT longs would require breaking highs (higher quality)

---

## Testing Recommendation:

1. **Test on different market conditions:**
   - Bull day (+400pts) ← Already tested
   - Bear day (-400pts) ← Need to test
   - Ranging day (±50pts) ← Need to test
   - Trend reversal day ← Need to test

2. **Compare strategies:**
   - Current: Equal treatment
   - Improved: Trend-aware asymmetry
   - Long-only: Baseline

3. **Measure:**
   - Win rate by market condition
   - P&L by trend direction
   - False signals filtered

---

## Implementation Priority:

1. **HIGH:** Add swing high/low detection helpers
2. **HIGH:** Implement trend-aware asymmetric requirements
3. **MEDIUM:** Add near-price order book filtering (5 ticks)
4. **MEDIUM:** Tighten price action filters (±1 tick vs ±2)
5. **LOW:** Add confidence scoring system

Would you like me to implement these improvements in the strategy code?
