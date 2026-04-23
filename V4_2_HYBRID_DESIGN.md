# v4.2 Hybrid Scalp/Trend Strategy Design

## Problem Statement

Current v4.1 performance on April 13 (342-point bull day):
- 47 longs, 64% stopped out
- Lost $63 when market made $1,368
- Scalping 8pt targets when 50-100pt trends available

**Goal:** Automatically switch between scalp mode (chop) and trend mode (trending).

---

## Solution: Regime Detection + Adaptive Parameters

### A. Market Regime Detection

Detect 3 regimes:

1. **TRENDING** (strong directional move)
2. **CHOPPY** (range-bound, no clear direction)
3. **TRANSITION** (starting to trend or breaking down)

#### Trend Strength Indicators

**1. EMA Separation (existing)**
```csharp
double emaSeparation = Math.Abs(emaFast[0] - emaSlow[0]);
bool strongSeparation = emaSeparation >= TrendThreshold; // 10+ points
```

**2. Directional Consistency (NEW)**
```csharp
// Count bars moving in same direction
int barsInDirection = 0;
bool upDirection = Closes[1][0] > Closes[1][10];

for (int i = 1; i < 10; i++) {
    if (upDirection && Closes[1][i-1] > Closes[1][i]) barsInDirection++;
    if (!upDirection && Closes[1][i-1] < Closes[1][i]) barsInDirection++;
}

bool directional = barsInDirection >= 6; // 6/10 bars in same direction
```

**3. New High/Low Detection (NEW)**
```csharp
// Check if making new highs/lows (trend signature)
bool newHigh = Highs[1][0] > MAX(Highs[1], 20);  // 20-bar high
bool newLow = Lows[1][0] < MIN(Lows[1], 20);     // 20-bar low
```

**4. Volatility Expansion (NEW)**
```csharp
// Trending markets have expanding ranges
double currentRange = Highs[1][0] - Lows[1][0];
double avgRange = 0;
for (int i = 1; i <= 10; i++) {
    avgRange += (Highs[1][i] - Lows[1][i]);
}
avgRange /= 10;

bool expandingVolatility = currentRange > avgRange * 1.5;
```

#### Regime Classification Logic

```csharp
enum MarketRegime {
    TRENDING,
    CHOPPY,
    TRANSITION
}

private MarketRegime DetectRegime() {
    double emaSep = Math.Abs(emaFast[0] - emaSlow[0]);

    // TRENDING: 3+ signals confirm
    int trendScore = 0;
    if (emaSep >= 10) trendScore++;              // Strong EMA separation
    if (directionalBars >= 6) trendScore++;      // Consistent direction
    if (newHigh || newLow) trendScore++;         // Making new extremes
    if (expandingVolatility) trendScore++;       // Vol expansion

    if (trendScore >= 3) return MarketRegime.TRENDING;
    if (trendScore <= 1) return MarketRegime.CHOPPY;
    return MarketRegime.TRANSITION;
}
```

---

### B. Adaptive Parameters by Regime

#### TRENDING Mode (Ride the wave)
```csharp
if (currentRegime == MarketRegime.TRENDING) {
    // WIDER STOPS - allow pullbacks
    stopDistance = 12.0;  // vs 5.0 in scalp mode

    // BIGGER TARGETS - or use trailing
    if (UseTrailingStops) {
        // No fixed target, trail with 8pt buffer
        trailDistance = 8.0;
        targetDistance = 50.0;  // Safety net only
    } else {
        targetDistance = 25.0;  // 3x scalp target
    }

    // LONGER HOLD - let trend develop
    maxHoldSeconds = 600;  // 10 minutes vs 2 minutes

    // HIGHER SIGNAL THRESHOLD - be selective
    minAbsorption = AbsorptionMinAggressor * 1.5;
}
```

#### CHOPPY Mode (Quick scalps)
```csharp
if (currentRegime == MarketRegime.CHOPPY) {
    // TIGHT STOPS - get out fast
    stopDistance = 5.0;

    // SMALL TARGETS - take quick profit
    targetDistance = 8.0;

    // SHORT HOLD - don't overstay
    maxHoldSeconds = 120;  // 2 minutes

    // STRICTER FILTERS - less noise
    minAbsorption = AbsorptionMinAggressor * 2.0;  // Higher bar
}
```

#### TRANSITION Mode (Be cautious)
```csharp
if (currentRegime == MarketRegime.TRANSITION) {
    // BALANCED PARAMETERS
    stopDistance = 8.0;   // Middle ground
    targetDistance = 15.0;
    maxHoldSeconds = 300; // 5 minutes
    minAbsorption = AbsorptionMinAggressor * 1.75;
}
```

---

### C. Regime Stability (Anti-Whipsaw)

**Problem:** Don't want to flip between modes every bar.

**Solution:** Hysteresis + Confirmation

```csharp
private MarketRegime currentRegime = MarketRegime.CHOPPY;
private int regimeBarsCount = 0;
private const int MIN_REGIME_BARS = 5;  // Must hold 5 bars

private void UpdateRegime() {
    MarketRegime detected = DetectRegime();

    if (detected == currentRegime) {
        // Same regime, reset counter
        regimeBarsCount = 0;
    } else {
        // Different regime detected
        regimeBarsCount++;

        if (regimeBarsCount >= MIN_REGIME_BARS) {
            // Confirmed regime change
            Print($"REGIME CHANGE: {currentRegime} → {detected}");
            currentRegime = detected;
            regimeBarsCount = 0;

            // Update all parameters
            UpdateParametersForRegime();
        }
    }
}
```

---

### D. Smart Trailing Stops (Trend Mode)

**Current problem:** Fixed 8pt target exits early on big trends.

**Solution:** Asymmetric trailing that protects profit but lets winners run.

```csharp
private void ManageTrailingStop() {
    if (Position.MarketPosition == MarketPosition.Flat)
        return;

    double unrealizedPnL = Position.GetUnrealizedProfitLoss(PerformanceUnit.Points);

    if (currentRegime == MarketRegime.TRENDING) {
        // TREND MODE: Trail with wider buffer
        if (unrealizedPnL >= 10.0) {  // In profit 10pts+
            // Trail by 8pts from high water mark
            double newStop = Position.MarketPosition == MarketPosition.Long
                ? Highs[1][0] - 8.0
                : Lows[1][0] + 8.0;

            // Only move stop in favorable direction
            if (ShouldUpdateTrailingStop(newStop)) {
                SetTrailStop(CalculationMode.Price, newStop);
                Print($"TREND TRAIL: Stop → {newStop:F2} (protecting {unrealizedPnL:F2}pts)");
            }
        }
    } else {
        // SCALP MODE: Tighter trail
        if (unrealizedPnL >= 5.0) {
            double newStop = Position.MarketPosition == MarketPosition.Long
                ? Highs[1][0] - 4.0  // Tighter 4pt trail
                : Lows[1][0] + 4.0;

            if (ShouldUpdateTrailingStop(newStop)) {
                SetTrailStop(CalculationMode.Price, newStop);
            }
        }
    }
}
```

---

### E. Entry Filtering by Regime

**TRENDING:** Take trades WITH the trend only
```csharp
if (currentRegime == MarketRegime.TRENDING) {
    bool upTrend = emaFast[0] > emaSlow[0];

    // Only longs in uptrend, shorts in downtrend
    if (signal.Direction == MarketPosition.Long && !upTrend)
        return false;  // Reject counter-trend long

    if (signal.Direction == MarketPosition.Short && upTrend)
        return false;  // Reject counter-trend short
}
```

**CHOPPY:** Allow counter-trend reversals
```csharp
if (currentRegime == MarketRegime.CHOPPY) {
    // Look for reversals at extremes
    // Allow both directions
    // But require stronger signals
}
```

---

### F. Position Sizing (Optional Enhancement)

```csharp
// Scale up in trends (if account allows)
if (currentRegime == MarketRegime.TRENDING && totalPnL > 200) {
    contracts = 2;  // Double size in strong trends
} else {
    contracts = 1;  // Standard size
}
```

---

## Implementation Plan

### Phase 1: Core Regime Detection ✅
- [x] Add regime enum
- [x] Implement DetectRegime() with 4 indicators
- [x] Add hysteresis logic (5-bar confirmation)
- [x] Logging for regime changes

### Phase 2: Adaptive Parameters ✅
- [x] Dynamic stops by regime
- [x] Dynamic targets by regime
- [x] Dynamic hold times by regime
- [x] UpdateParametersForRegime() method

### Phase 3: Smart Trailing ✅
- [x] Trend-aware trailing logic
- [x] Protect profits while letting winners run
- [x] Separate scalp vs trend trail distances

### Phase 4: Enhanced Filters ✅
- [x] WITH-trend filter in TRENDING mode
- [x] Stricter absorption thresholds in CHOPPY
- [x] Balanced approach in TRANSITION

### Phase 5: Short Gate Integration ✅
- [x] Keep existing Short Gate from v4.1
- [x] Make Short Gate stricter in CHOPPY mode
- [x] Slightly looser in TRENDING mode (if in downtrend)

---

## New Strategy Parameters

```csharp
// REGIME DETECTION
[NinjaScriptProperty]
[Display(Name = "Trend EMA Separation Threshold", Order = 1, GroupName = "3. Hybrid Mode")]
public double TrendEMASeparationThreshold { get; set; }  // Default: 10.0

[NinjaScriptProperty]
[Display(Name = "Min Regime Confirmation Bars", Order = 2, GroupName = "3. Hybrid Mode")]
public int MinRegimeConfirmationBars { get; set; }  // Default: 5

[NinjaScriptProperty]
[Display(Name = "Enable Regime Detection", Order = 3, GroupName = "3. Hybrid Mode")]
public bool UseRegimeDetection { get; set; }  // Default: true

// TRENDING MODE PARAMETERS
[NinjaScriptProperty]
[Display(Name = "Trend Mode: Target", Order = 4, GroupName = "3. Hybrid Mode")]
public double TrendModeTarget { get; set; }  // Default: 25.0

[NinjaScriptProperty]
[Display(Name = "Trend Mode: Stop", Order = 5, GroupName = "3. Hybrid Mode")]
public double TrendModeStop { get; set; }  // Default: 12.0

[NinjaScriptProperty]
[Display(Name = "Trend Mode: Max Hold (seconds)", Order = 6, GroupName = "3. Hybrid Mode")]
public int TrendModeMaxHold { get; set; }  // Default: 600

[NinjaScriptProperty]
[Display(Name = "Trend Mode: Trail Distance", Order = 7, GroupName = "3. Hybrid Mode")]
public double TrendModeTrailDistance { get; set; }  // Default: 8.0

// CHOPPY MODE PARAMETERS (use existing Absorption params)
// ChoppyModeTarget = AbsorptionTarget (8.0)
// ChoppyModeStop = AbsorptionStop (5.0)
// ChoppyModeMaxHold = AbsorptionMaxHold (120)
```

---

## Expected Results on April 13 Bull Day

### Before (v4.1 Scalp Only):
- 47 longs, 36% win, -$63 loss
- 64% stopped out
- Left $1,431 on table

### After (v4.2 Hybrid):
- Detects TRENDING regime early (9:00-10:00 AM)
- Switches to 12pt stops, 25pt targets, 10min holds
- Uses trailing stops to ride trend
- Expected: 60-70% win rate, +$300-500 profit
- Captures 20-30% of the 342pt move

### In Choppy Conditions:
- Detects CHOPPY regime
- Reverts to tight scalping (8pt/5pt)
- Quick in/out, avoids being trapped
- Expected: 45% win rate, small positive

---

## Code Structure

```
v4.2 New Methods:
├── DetectRegime() → MarketRegime
├── UpdateRegime() → void (with hysteresis)
├── UpdateParametersForRegime() → void
├── CheckDirectionalConsistency() → bool
├── CheckNewHighLow() → bool
├── CheckVolatilityExpansion() → bool
├── ManageTrailingStop() → void (regime-aware)
└── GetAdaptiveParameters() → (stop, target, maxHold)

v4.1 Methods (Keep):
├── PassesShortGate() ← Keep but make regime-aware
├── PassesFilters() ← Enhance with regime checks
├── DetectAbsorption() ← Keep as-is
├── DetectBreakout() ← Keep as-is
└── All existing risk management
```

---

## Testing Plan

1. **Test on April 13 (bull trend day)**
   - Should detect TRENDING early
   - Should make +$300-500 (vs -$63)
   - Should ride 50+ point moves

2. **Test on choppy days**
   - Should detect CHOPPY
   - Should scalp successfully
   - Should avoid getting trapped

3. **Test on transition days**
   - Should handle regime changes gracefully
   - Should not whipsaw between modes

---

## Success Metrics

**Trending Days:**
- Capture 20-30% of total range
- Win rate > 55%
- Ride at least 3-4 big winners (20+ pts each)

**Choppy Days:**
- Win rate > 45%
- Small positive or break-even
- Avoid big losses

**Overall:**
- Better P&L than v4.1 on trending days (+$400 improvement)
- Similar or better on choppy days
- More consistent across different market conditions

---

Ready to implement v4.2 Hybrid strategy?
