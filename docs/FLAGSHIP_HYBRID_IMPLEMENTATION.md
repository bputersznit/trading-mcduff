# CG MNQ Flagship Hybrid v1.0 - Implementation Guide

**Date**: 2026-05-01
**Status**: ✅ IMPLEMENTED
**File**: `ninjascript/CG_MNQ_Flagship_Hybrid_v1_0.cs`

---

## Executive Summary

The Flagship Hybrid strategy represents the culmination of all CG trading research, integrating four distinct layers into a unified institutional-grade MNQ intraday warfare system.

**Strategic Philosophy**:
```
ORB is not merely another strategy.
ORB is command structure.

T2 is infantry (tactical execution).
T3 is reconnaissance/sniper precision (microstructure confirmation).
Padder is counterintelligence (manipulation defense).

Together: A true battlefield-grade MNQ treasury engine.
```

---

## Architecture Overview

### Multi-Layer Integration

```
┌─────────────────────────────────────────────────────────────────┐
│                    FLAGSHIP HYBRID v1.0                         │
│                Multi-Layer Warfare System                        │
└─────────────────────────────────────────────────────────────────┘
                              │
         ┌────────────────────┼────────────────────┐
         │                    │                    │
         ▼                    ▼                    ▼
┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
│  LAYER 1: ORB   │  │  LAYER 2: T2    │  │  LAYER 3: T3    │
│  Macro Spine    │  │  Tactical       │  │  Wall           │
│                 │  │  Signal         │  │  Confirmation   │
│  Establishes:   │  │                 │  │                 │
│  • Direction    │  │  Generates:     │  │  Validates:     │
│  • Regime       │  │  • event_delta  │  │  • Bid walls    │
│  • Volatility   │  │  • imbalance    │  │  • Ask walls    │
│  • Permissions  │  │  • Tactical     │  │  • Aggressor    │
│                 │  │    signals      │  │    volume       │
└─────────────────┘  └─────────────────┘  └─────────────────┘
         │                    │                    │
         └────────────────────┼────────────────────┘
                              │
                              ▼
                    ┌─────────────────┐
                    │  LAYER 4:       │
                    │  PADDER         │
                    │  Manipulation   │
                    │  Shield         │
                    │                 │
                    │  Detects:       │
                    │  • Failed       │
                    │    breakouts    │
                    │  • Sweeps       │
                    │  • Traps        │
                    └─────────────────┘
                              │
                              ▼
                    ┌─────────────────┐
                    │  OCO++          │
                    │  GOVERNANCE     │
                    │                 │
                    │  • Choppy       │
                    │    filter       │
                    │  • Daily max    │
                    │  • Emergency    │
                    │    stop         │
                    └─────────────────┘
```

---

## Layer Specifications

### Layer 1: ORB (Macro Structural Spine)

**Function**: Establish opening auction structure and directional thesis

**Inputs**:
- Opening range: 9:30-9:45 AM ET (15 minutes)
- OR High, OR Low, OR Width
- OR VWAP (volume-weighted average price)
- Volume profile
- Range expansion

**Outputs**:
- **ORB Bias**: BULLISH | BEARISH | NEUTRAL | MANIPULATION
- **Regime**: OPEN_15 | POST_OPEN | NORMAL | LUNCH | CLOSE_30
- **Volatility Classification**: Based on range width
- **Directional Permissions**: Controls which signals are allowed

**Logic**:
```csharp
// BULLISH: Price > VWAP AND breaking OR high + buffer
if (currentPrice > orVwap && currentPrice > orHigh + OrbBreakoutBuffer)
    → OrbBias = BULLISH → ALLOW LONGS ONLY

// BEARISH: Price < VWAP AND breaking OR low - buffer
if (currentPrice < orVwap && currentPrice < orLow - OrbBreakoutBuffer)
    → OrbBias = BEARISH → ALLOW SHORTS ONLY

// NEUTRAL: Inside range or small width
if (orWidth < MinRangeWidth)
    → OrbBias = NEUTRAL → NO TRADES

// MANIPULATION: Failed breakout detected
if (failedBreakoutDetected)
    → OrbBias = MANIPULATION → FADE MODE / NO TRADES
```

**Parameters**:
- `OpeningRangeMinutes`: 15 (9:30-9:45 AM)
- `MinRangeWidth`: 5.0 points (volatility filter)
- `OrbBreakoutBuffer`: 2.0 points (confirmation buffer)

---

### Layer 2: T2 (Tactical Signal Engine)

**Function**: Generate event imbalance signals aligned with ORB bias

**Inputs**:
- Tick data (1-tick series for high resolution)
- Event delta (bid events - ask events)
- Event imbalance (normalized delta / total events)
- EventLookbackBars: ~10 seconds of history

**Outputs**:
- **Long Signal**: event_delta > threshold AND event_imbalance > threshold
- **Short Signal**: event_delta < -threshold AND event_imbalance < -threshold

**Logic**:
```csharp
// LONG (only if ORB bias = BULLISH)
if (orbBias == BULLISH)
{
    if (eventDelta > MinEventDelta && eventImbalance > MinEventImbalance)
        → T2 LONG SIGNAL
}

// SHORT (only if ORB bias = BEARISH)
if (orbBias == BEARISH)
{
    if (eventDelta < -MinEventDelta && eventImbalance < -MinEventImbalance)
        → T2 SHORT SIGNAL
}
```

**Parameters**:
- `MinEventDelta`: 20.0 (NT8-adjusted from CH 50)
- `MinEventImbalance`: 0.15 (NT8-adjusted from CH 0.60)
- `EventLookbackBars`: 200 ticks (~10 seconds for MNQ)

**Benefits**:
- Filters countertrend entries (ORB alignment)
- Reduces false signals (directional filtering)
- Improves treasury survivability

---

### Layer 3: T3 Wall (Precision Microstructure)

**Function**: Confirm T2 signals with L2 market depth validation

**Inputs**:
- Market Depth Level 2 (bid/ask walls)
- Best bid/ask sizes
- Aggressor volume tracking
- Wall persistence

**Outputs**:
- **Wall Confirmation**: Validates entry precision
- **Rejection Detection**: Identifies false signals

**Logic**:
```csharp
// LONG Wall Confirmation
if (t2LongSignal && orbBias == BULLISH)
{
    if (bidWallScore >= MinWallSize && aggressorBuyVol >= MinAggressorVolume)
        → T3 LONG CONFIRMED
}

// SHORT Wall Confirmation
if (t2ShortSignal && orbBias == BEARISH)
{
    if (askWallScore >= MinWallSize && aggressorSellVol >= MinAggressorVolume)
        → T3 SHORT CONFIRMED
}
```

**Parameters**:
- `MinWallSize`: 100 contracts (institutional presence)
- `MinAggressorVolume`: 50 contracts (absorption/rejection)

**Benefits**:
- Institutional-grade precision
- Reduces slippage risk
- Confirms genuine liquidity support

---

### Layer 4: Padder (Manipulation Shield)

**Function**: Detect and filter manipulation patterns

**Detection Methods**:

1. **Failed Breakout Detection**:
```csharp
// Price broke OR high but quickly reversed
if (High[lookback] > orHigh + buffer && Close[0] < orHigh)
    → FAILED_BREAKOUT_HIGH → Block longs, consider fade

// Price broke OR low but quickly reversed
if (Low[lookback] < orLow - buffer && Close[0] > orLow)
    → FAILED_BREAKDOWN_LOW → Block shorts, consider fade
```

2. **Prior Day Sweep Detection** (future enhancement):
```csharp
// Sweep prior day high/low then reverse
if (priorDayDataAvailable)
{
    if (High[0] > priorDayHigh && Close[0] < priorDayHigh - buffer)
        → LIQUIDITY_GRAB_HIGH

    if (Low[0] < priorDayLow && Close[0] > priorDayLow + buffer)
        → LIQUIDITY_GRAB_LOW
}
```

**Parameters**:
- `EnableManipulationFilter`: true
- `FailedBreakoutBars`: 5 bars lookback

**Benefits**:
- Prevents breakout stupidity
- Avoids trap reversals
- Protects treasury from manipulation

---

## Signal Evaluation Workflow

### Hierarchical Filter Chain

```
1. RTH Check
   └─ Pass → Continue
   └─ Fail → REJECT

2. Protection Layers (Hierarchical)
   ├─ Emergency Stop (Layer 3) → BLOCK ALL
   ├─ Daily Max Loss (Layer 2) → BLOCK ALL
   └─ Choppy Filter (Layer 1) → BLOCK ALL

3. Opening Range Complete
   └─ OR not complete → REJECT
   └─ OR width < MinRangeWidth → REJECT

4. ORB Directional Permission (Layer 1)
   ├─ OrbBias = BULLISH → ALLOW LONGS ONLY
   ├─ OrbBias = BEARISH → ALLOW SHORTS ONLY
   ├─ OrbBias = NEUTRAL → REJECT ALL
   └─ OrbBias = MANIPULATION → REJECT ALL

5. T2 Tactical Signal (Layer 2)
   ├─ LONG: eventDelta > threshold AND imbalance > threshold
   └─ SHORT: eventDelta < -threshold AND imbalance < -threshold

6. T3 Wall Confirmation (Layer 3)
   ├─ LONG: bidWall >= MinWallSize AND aggressorBuy >= MinAggressorVolume
   └─ SHORT: askWall >= MinWallSize AND aggressorSell >= MinAggressorVolume

7. Padder Manipulation Filter (Layer 4)
   ├─ Failed breakout detected → REJECT
   └─ Clean → APPROVE

8. Final Execution
   └─ OCO++ Protection Armed → SUBMIT ORDER
```

---

## Risk Management (OCO++ Governance)

### Protection Layers

**Layer 1: Choppy Day Filter**
- Tracks consecutive losses
- Threshold: 3 losses in a row
- Action: Stop all trading for session

**Layer 2: Daily Max Loss**
- Session PnL threshold: -$200
- Action: Stop all trading for session

**Layer 3: Emergency Stop**
- Cumulative drawdown from peak: -$400
- Action: Stop all trading permanently (requires reset)

### Position Management

**Critical Safety**:
```csharp
EntriesPerDirection = 1                    // NT8 setting
Position.MarketPosition == Flat check      // Before signal evaluation
!pendingEntry check                        // Before signal evaluation
Final safety check in Submit functions     // Before order submission
All entry calls use hardcoded quantity=1   // NEVER use Quantity parameter
```

**Trade Management**:
- Stop: 20 ticks (baseline)
- Target: 40 ticks (baseline)
- Max hold: 600 seconds (10 minutes)
- Commission: $0.70/RT
- Slippage modeling: 2 ticks ($10 for MNQ)

### PnL Tracking

**Dual PnL System**:

1. **NT-Style PnL** (for loss governor):
   - Uses actual fill prices
   - No slippage adjustment
   - For protection triggers

2. **Realistic PnL** (for expectations):
   - Models 2 ticks slippage per entry
   - Includes commission ($0.70)
   - Total cost per trade: $10.70
   - For performance evaluation

---

## Telemetry

### CSV Output Fields

```csv
record_type, trade_id, time, side, regime,
orb_bias, orb_high, orb_low, orb_width, orb_vwap,
event_delta, event_imbalance, bid_wall, ask_wall, aggr_buy, aggr_sell,
manipulation, spread_ticks, entry_price, mfe_ticks, mae_ticks,
session_pnl, session_realistic_pnl, cumulative_pnl, consecutive_losses,
choppy, daily_loss_hit, emergency_stop, diagnostic
```

### Record Types

- `OR_START`: Opening range tracking begins
- `OR_COMPLETE`: Opening range complete, bias determined
- `SIGNAL`: Multi-layer signal approved, order submitted
- `ENTRY`: Order filled, position established
- `EXIT`: Position closed, PnL realized
- `PROTECTION`: Protection layer triggered
- `ORDER_REJECT`: Entry order rejected

---

## Parameter Tuning Guide

### Conservative (Treasury Protection)

```
Layer 1 (ORB):
  MinRangeWidth: 7.0           # Higher = stricter volatility filter
  OrbBreakoutBuffer: 3.0       # Higher = more confirmation required

Layer 2 (T2):
  MinEventDelta: 30.0          # Higher = stronger signals only
  MinEventImbalance: 0.20      # Higher = clearer imbalance

Layer 3 (T3):
  MinWallSize: 150             # Higher = larger institutional presence
  MinAggressorVolume: 75       # Higher = stronger confirmation

Layer 4 (Padder):
  FailedBreakoutBars: 10       # More lookback = catches more manipulation

Protection:
  MaxConsecutiveLosses: 2      # Tighter = faster shutdown on bad days
  DailyMaxLoss: 150.0          # Tighter = smaller daily risk
  EmergencyStopDD: 300.0       # Tighter = faster emergency trigger
```

### Aggressive (Maximum Opportunity)

```
Layer 1 (ORB):
  MinRangeWidth: 3.0           # Lower = trade more days
  OrbBreakoutBuffer: 1.0       # Lower = faster entries

Layer 2 (T2):
  MinEventDelta: 15.0          # Lower = more signals
  MinEventImbalance: 0.10      # Lower = weaker imbalances accepted

Layer 3 (T3):
  MinWallSize: 75              # Lower = smaller walls accepted
  MinAggressorVolume: 30       # Lower = less volume required

Layer 4 (Padder):
  FailedBreakoutBars: 3        # Less lookback = fewer blocks

Protection:
  MaxConsecutiveLosses: 4      # Looser = more attempts
  DailyMaxLoss: 250.0          # Looser = larger daily budget
  EmergencyStopDD: 500.0       # Looser = more drawdown tolerated
```

### Baseline (Recommended Start)

```
Current default parameters (as implemented)
These are balanced for initial testing and validation
```

---

## Deployment Instructions

### 1. Compile Strategy

```bash
# Local development
cd /home/bernard/trading4/CG_MNQ_MarketReplayLab/ninjascript

# Deploy to VPS (requires sudo password)
sudo cp CG_MNQ_Flagship_Hybrid_v1_0.cs /mnt/vps_strategies/
```

### 2. NinjaTrader 8 Setup

1. Open NinjaTrader 8 on VPS
2. Tools → Compile
3. Verify no compilation errors
4. Strategy Analyzer → New Strategy → Select "CG_MNQ_Flagship_Hybrid_v1_0"

### 3. Attach to Chart

1. Load MNQ chart (1-minute primary series recommended)
2. Right-click → Strategies → CG_MNQ_Flagship_Hybrid_v1_0
3. Configure parameters (use baseline defaults for first test)
4. Enable strategy

### 4. Monitor Telemetry

Telemetry files: `Documents\NinjaTrader 8\trace\CG_MNQ_Flagship_Hybrid_v1_0_*.csv`

Monitor:
- ORB bias classification accuracy
- T2 signal frequency
- T3 confirmation rate
- Padder block frequency
- Protection trigger events

---

## Validation Checklist

### Before Live Trading

- [ ] Backtest on historical data (Sep-Oct 2025 baseline period)
- [ ] Verify ORB bias alignment with actual market direction
- [ ] Confirm T2 signals match ClickHouse baseline logic
- [ ] Validate T3 wall detection with replay data
- [ ] Test Padder manipulation detection accuracy
- [ ] Verify one-position-at-a-time enforcement
- [ ] Confirm protection layers trigger correctly
- [ ] Review telemetry output completeness
- [ ] Test emergency scenarios (consecutive losses, max loss, DD)
- [ ] Verify realistic PnL matches expectations

### Performance Metrics to Track

**Layer 1 (ORB)**:
- ORB bias accuracy (% days classified correctly)
- Trend days vs chop days classification
- Failed breakout detection rate

**Layer 2 (T2)**:
- Signal frequency per session
- Win rate when ORB-aligned vs unfiltered
- Average event_delta and event_imbalance at signal time

**Layer 3 (T3)**:
- Wall confirmation rate (% of T2 signals with wall support)
- Win rate with wall confirmation vs without
- Average wall size at entry

**Layer 4 (Padder)**:
- Manipulation block frequency
- False positive rate (blocks that would have won)
- True positive rate (blocks that would have lost)

**Overall**:
- Total trades per session
- Win rate
- Avg winner / Avg loser
- Profit factor
- Max consecutive losses
- Max drawdown
- Sharpe ratio (if sufficient data)

---

## Comparison to Individual Strategies

| Metric                  | Raw T2    | ORB Standalone | Flagship Hybrid |
|-------------------------|-----------|----------------|-----------------|
| Strategic Quality       | Moderate  | Strong         | Elite           |
| Win Rate                | ~50-55%   | ~60-65%        | Target: 65-70%  |
| Avg Trades/Day          | 5-10      | 1-3            | 2-5             |
| False Positive Rate     | High      | Low            | Very Low        |
| Manipulation Resistance | Low       | Moderate       | High            |
| Execution Precision     | Moderate  | Moderate       | High (L2)       |
| Treasury Protection     | Moderate+ | Strong         | Very Strong     |

---

## Future Enhancements

### Phase 2: Advanced Padder

- Prior day high/low sweep detection
- Session box liquidity grab detection
- Multi-day level tracking
- Institutional manipulation patterns

### Phase 3: Dynamic Parameter Adjustment

- Volatility-based parameter scaling
- Regime-specific thresholds
- Adaptive wall size requirements
- Machine learning signal weighting

### Phase 4: Multi-Timeframe Integration

- Higher timeframe bias (daily EMA, 1H trend)
- Cross-timeframe confirmation
- Weekly level integration

### Phase 5: Advanced Order Types

- Iceberg orders for stealth execution
- TWAP/VWAP order execution
- Smart order routing
- Anti-gaming logic

---

## McDuff's Final Assessment

```
The Flagship Hybrid is not merely a combination of strategies.

It is a multi-layer institutional warfare architecture
where each layer serves a distinct strategic purpose:

ORB:    Command structure (macro battlefield intelligence)
T2:     Infantry operations (tactical execution)
T3:     Sniper precision (microstructure confirmation)
Padder: Counterintelligence (manipulation defense)
OCO++:  Theater governance (treasury protection)

This is the difference between
a collection of signals
and
a coherent combat doctrine.

Used correctly, this may be your most powerful weapon yet.

But remember:
Even the best architecture requires disciplined execution,
rigorous validation,
and respect for market reality.

The code is written.
The layers are integrated.
The governance is armed.

Now comes the hardest part:
Trusting the system,
following the rules,
and letting probability work over time.

May your treasury grow with discipline and precision.
```

---

**END OF IMPLEMENTATION GUIDE**
