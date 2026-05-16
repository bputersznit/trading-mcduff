# Flagship Hybrid v1.1 - ORB State Machine Refactor

**Date**: 2026-05-01
**Status**: ✅ IMPLEMENTED
**File**: `ninjascript/CG_MNQ_Flagship_Hybrid_v1_1.cs`
**Size**: 65 KB (vs v1.0: 58 KB)

---

## Executive Summary

Version 1.1 transforms the Flagship Hybrid from a **static ORB bias system** into a **dynamic finite-state regime engine**.

### The Fundamental Problem with v1.0

```text
v1.0 flaw: ORB bias is determined ONCE after the opening range completes at 9:45 AM.

Result: The system is structurally blind to session evolution.
```

**Example failure scenario**:
```
9:45 AM: ORB completes, price inside range → bias = NEUTRAL → no trades allowed
10:30 AM: Price breaks above OR high + buffer → should be LONG_BREAKOUT
         But v1.0 still thinks bias = NEUTRAL → MISSES THE BREAKOUT

Another example:
9:45 AM: Price breaks above OR → bias = BULLISH → longs only
10:15 AM: Price reverses back inside OR → FAILED BREAKOUT
         But v1.0 still thinks bias = BULLISH → TAKES BAD LONGS
```

### The v1.1 Solution

```text
ORB is not a one-time signal.
ORB is the command-state engine for the session.

The ORB state is now continuously re-evaluated as market structure evolves.
```

---

## What Changed from v1.0 to v1.1

### 1. Static Enum Replaced with Dynamic State Machine

**v1.0 (REMOVED)**:
```csharp
enum OrbBias
{
    NONE,
    BULLISH,
    BEARISH,
    NEUTRAL,
    MANIPULATION
}

private OrbBias currentOrbBias = OrbBias.NONE;
```

**v1.1 (NEW)**:
```csharp
enum OrbState
{
    PRE_OR,           // Before opening range starts
    BUILDING_OR,      // During 9:30-9:45 AM
    NEUTRAL,          // OR complete, price inside range
    LONG_BREAKOUT,    // Price above OR high + buffer
    SHORT_BREAKOUT,   // Price below OR low - buffer
    FAILED_LONG,      // Long breakout reversed back inside
    FAILED_SHORT,     // Short breakout reversed back inside
    CHOP,             // Low volatility / whipsaw detected
    FLAT_LOCK         // Treasury protection triggered
}

private OrbState currentOrbState = OrbState.PRE_OR;
private OrbState previousOrbState = OrbState.PRE_OR;
private string currentOrbTransitionReason = "INIT";
```

### 2. One-Time Bias Determination Removed

**v1.0 (REMOVED)**:
```csharp
private OrbBias DetermineOrbBias()
{
    // Called ONCE after OR completion
    // Returns static bias for entire session
}
```

**v1.1 (NEW)**:
```csharp
private void UpdateOrbState(DateTime etNow)
{
    // Called CONTINUOUSLY throughout the session
    // Dynamically transitions between states as market evolves
}
```

### 3. State Transition Infrastructure Added

**New Functions**:
```csharp
SetOrbState(OrbState nextState, string reason, DateTime etNow)
  → Centralized state transition logging
  → Telemetry recording
  → Diagnostic printing

AllowsLong()
  → Returns true if current ORB state permits long entries

AllowsShort()
  → Returns true if current ORB state permits short entries
```

### 4. Continuous State Evaluation

**v1.1 Main Loop**:
```csharp
protected override void OnBarUpdate()
{
    // ... standard checks ...

    // Compute features
    ComputeT2EventFeatures();
    ComputeT3WallFeatures();
    CheckManipulation(etNow);

    // ⭐ CRITICAL NEW STEP: Dynamically update ORB state
    UpdateOrbState(etNow);

    // Use dynamic state for permissions
    if (currentOrbState == OrbState.CHOP || currentOrbState == OrbState.FLAT_LOCK)
        return;

    // ... signal evaluation ...
}
```

### 5. Permission Helper Methods

**v1.0**:
```csharp
if (currentOrbBias != OrbBias.BULLISH)
    return false;  // Reject longs
```

**v1.1**:
```csharp
private bool AllowsLong()
{
    if (currentOrbState == OrbState.LONG_BREAKOUT)
        return true;

    if (AllowFadeAfterFailure && currentOrbState == OrbState.FAILED_SHORT)
        return true;  // Allow fade longs after failed short breakout

    return false;
}
```

### 6. Enhanced Telemetry

**New CSV columns**:
```
orb_state             Current ORB state enum value
orb_reason            Reason for last state transition
```

**Example telemetry records**:
```csv
ORB_STATE,1,09:30:00,,,PRE_OR,OR_START,...
ORB_STATE,1,09:45:15,,,NEUTRAL,OR_COMPLETE_INSIDE_RANGE,...
ORB_STATE,1,10:15:23,,,LONG_BREAKOUT,PRICE_ABOVE_OR_HIGH_BUFFER,...
ORB_STATE,1,10:42:11,,,FAILED_LONG,LONG_BREAKOUT_RECLAIMED_INSIDE_OR,...
```

---

## ORB State Machine Detailed Specification

### State Diagram

```
         ┌─────────────┐
         │   PRE_OR    │  Before 9:30 AM
         └──────┬──────┘
                │ 9:30 AM
                ▼
         ┌─────────────┐
         │ BUILDING_OR │  9:30-9:45 AM (building range)
         └──────┬──────┘
                │ 9:45 AM
                ▼
         ┌─────────────┐
    ┌────│   NEUTRAL   │────┐  OR complete, inside range
    │    └─────────────┘    │
    │                       │
    │ Price > OR high       │ Price < OR low
    │ + buffer              │ - buffer
    ▼                       ▼
┌──────────────┐      ┌──────────────┐
│LONG_BREAKOUT │      │SHORT_BREAKOUT│
└──────┬───────┘      └───────┬──────┘
       │                      │
       │ Reclaim              │ Reclaim
       │ inside OR            │ inside OR
       ▼                      ▼
┌──────────────┐      ┌──────────────┐
│ FAILED_LONG  │      │ FAILED_SHORT │
└──────────────┘      └──────────────┘

         Special States (can transition from any state):

         ┌──────────┐       ┌───────────┐
         │   CHOP   │       │ FLAT_LOCK │
         └──────────┘       └───────────┘
         Low volatility     Treasury protection
```

### State Definitions & Trading Permissions

| State | Condition | Long Permission | Short Permission | Purpose |
|-------|-----------|:---------------:|:----------------:|---------|
| **PRE_OR** | Time < 9:30 ET | ❌ No | ❌ No | Pre-market |
| **BUILDING_OR** | 9:30 ≤ time < 9:45 ET | ❌ No | ❌ No | Build OR structure |
| **NEUTRAL** | OR complete, price inside | ⚠️ Restricted | ⚠️ Restricted | No clear direction |
| **LONG_BREAKOUT** | Price > OR high + buffer | ✅ Yes | ❌ No | Trend following long |
| **SHORT_BREAKOUT** | Price < OR low - buffer | ❌ No | ✅ Yes | Trend following short |
| **FAILED_LONG** | Was LONG_BREAKOUT, reclaimed inside | ❌ No | ✅ Yes* | Fade failed upside |
| **FAILED_SHORT** | Was SHORT_BREAKOUT, reclaimed inside | ✅ Yes* | ❌ No | Fade failed downside |
| **CHOP** | OR width < threshold OR whipsaw | ❌ No | ❌ No | Avoid chop |
| **FLAT_LOCK** | Daily loss OR emergency stop | ❌ No | ❌ No | Treasury protection |

\* = Only if `AllowFadeAfterFailure = true` (default: true)

---

## Key Improvements Over v1.0

### 1. **Delayed Breakout Recognition**

**v1.0 Problem**:
```
9:45 AM: OR completes, price at 21235 (inside range 21230-21240)
         → bias = NEUTRAL → no trades
10:30 AM: Price breaks to 21242 (above OR high + 2pt buffer)
         → v1.0 still thinks NEUTRAL → MISSES BREAKOUT
```

**v1.1 Solution**:
```
9:45 AM: OR completes → state = NEUTRAL
10:30 AM: Price breaks to 21242
         → UpdateOrbState() detects breakout
         → state transitions: NEUTRAL → LONG_BREAKOUT
         → T2 longs NOW ALLOWED
```

### 2. **Failed Breakout Adaptation**

**v1.0 Problem**:
```
10:00 AM: Price breaks above OR → bias = BULLISH
10:15 AM: Price reverses back inside OR (failed breakout)
         → v1.0 still thinks BULLISH → keeps taking bad longs
```

**v1.1 Solution**:
```
10:00 AM: state = LONG_BREAKOUT
10:15 AM: Price reclaims inside OR
         → UpdateOrbState() detects reversal
         → state transitions: LONG_BREAKOUT → FAILED_LONG
         → Longs BLOCKED, shorts ALLOWED (fade the failure)
```

### 3. **Manipulation Detection Integration**

**v1.1 Enhancement**:
```csharp
private void UpdateOrbState(DateTime etNow)
{
    // Padder manipulation detection feeds into ORB state
    if (failedBreakoutDetected && manipulationReason == "FAILED_BREAKOUT_HIGH")
    {
        SetOrbState(OrbState.FAILED_LONG, manipulationReason, etNow);
        return;
    }
}
```

Now Padder layer **directly influences ORB state**, creating true multi-layer integration.

### 4. **Session Evolution Tracking**

**New Diagnostic Outputs**:
```
ORB State: LONG_BREAKOUT
Previous ORB State: NEUTRAL
ORB Transition Reason: PRICE_ABOVE_OR_HIGH_BUFFER
ORB State Transitions: 5
ORB Long breakout transitions: 2
ORB Short breakout transitions: 1
```

This shows **exactly how the session evolved**, not just a single static bias.

---

## UpdateOrbState() Logic Flow

```csharp
private void UpdateOrbState(DateTime etNow)
{
    // Step 1: Check treasury lockouts
    if (dailyMaxLossHit || emergencyStopTriggered)
        → SetOrbState(FLAT_LOCK, "TREASURY_LOCKOUT")

    // Step 2: Check OR width (chop filter)
    if (orWidth < MinRangeWidth)
        → SetOrbState(CHOP, "OR_WIDTH_BELOW_MIN")

    // Step 3: Check for failed breakout (manipulation)
    if (failedBreakoutDetected && manipulationReason == "FAILED_BREAKOUT_HIGH")
        → SetOrbState(FAILED_LONG, manipulationReason)

    if (failedBreakoutDetected && manipulationReason == "FAILED_BREAKDOWN_LOW")
        → SetOrbState(FAILED_SHORT, manipulationReason)

    // Step 4: Check for active breakout
    if (price > orHigh + OrbBreakoutBuffer && price >= orVwap)
        → SetOrbState(LONG_BREAKOUT, "PRICE_ABOVE_OR_HIGH_BUFFER")

    if (price < orLow - OrbBreakoutBuffer && price <= orVwap)
        → SetOrbState(SHORT_BREAKOUT, "PRICE_BELOW_OR_LOW_BUFFER")

    // Step 5: Check for breakout reversal (reclaim inside)
    if (currentOrbState == LONG_BREAKOUT && price < orHigh)
        → SetOrbState(FAILED_LONG, "LONG_BREAKOUT_RECLAIMED_INSIDE_OR")

    if (currentOrbState == SHORT_BREAKOUT && price > orLow)
        → SetOrbState(FAILED_SHORT, "SHORT_BREAKOUT_RECLAIMED_INSIDE_OR")

    // Step 6: Default to NEUTRAL (inside range, no clear direction)
    → SetOrbState(NEUTRAL, "PRICE_INSIDE_OR")
}
```

---

## New Parameters (v1.1)

### Added to Layer 1: ORB Parameters

```csharp
[NinjaScriptProperty]
[Range(0, 60)]
[Display(Name = "ORB State Eval Seconds", Order = 4, GroupName = "02. Layer 1: ORB")]
public int OrbStateEvalSeconds { get; set; }
```
**Purpose**: Throttle ORB state evaluation to avoid excessive state transitions
**Default**: 1 second
**Use**: Set higher (5-10 sec) if getting too many state flips

```csharp
[NinjaScriptProperty]
[Display(Name = "Block CHOP State", Order = 5, GroupName = "02. Layer 1: ORB")]
public bool BlockChopState { get; set; }
```
**Purpose**: Completely block trading when ORB state = CHOP
**Default**: true
**Use**: Disable if you want to allow highly selective trades in low volatility

```csharp
[NinjaScriptProperty]
[Display(Name = "Allow Fade After Failure", Order = 6, GroupName = "02. Layer 1: ORB")]
public bool AllowFadeAfterFailure { get; set; }
```
**Purpose**: Allow counter-trend fades after failed breakouts
**Default**: true
**Example**: If LONG_BREAKOUT fails → state = FAILED_LONG → allow shorts

```csharp
[NinjaScriptProperty]
[Display(Name = "Enable Lunch Suppression", Order = 7, GroupName = "02. Layer 1: ORB")]
public bool EnableLunchSuppression { get; set; }
```
**Purpose**: Block trading during lunch (11:30-13:00 ET)
**Default**: true
**Reason**: Lunch typically low quality, whipsaw prone

---

## Validation Checklist

Before deploying v1.1, verify:

### ✅ Compilation
- [ ] Strategy compiles cleanly in NinjaTrader 8
- [ ] No errors in Output window
- [ ] Class name matches file name exactly

### ✅ Core Safety (CRITICAL)
- [ ] One-position-at-a-time enforcement preserved
- [ ] `EntriesPerDirection = 1` still set
- [ ] All entry calls use hardcoded `quantity=1`
- [ ] OCO++ stop/target armed before entry
- [ ] No entries before OR completion

### ✅ ORB State Machine Behavior
- [ ] State starts as PRE_OR before 9:30 AM
- [ ] Transitions to BUILDING_OR at 9:30 AM
- [ ] Transitions to NEUTRAL or CHOP after 9:45 AM
- [ ] Detects LONG_BREAKOUT when price > OR high + buffer
- [ ] Detects SHORT_BREAKOUT when price < OR low - buffer
- [ ] Detects FAILED_LONG when LONG_BREAKOUT reverses inside
- [ ] Detects FAILED_SHORT when SHORT_BREAKOUT reverses inside
- [ ] State transitions appear in Output window
- [ ] State transitions recorded in telemetry CSV

### ✅ Directional Permissions
- [ ] Longs allowed ONLY in LONG_BREAKOUT or FAILED_SHORT states
- [ ] Shorts allowed ONLY in SHORT_BREAKOUT or FAILED_LONG states
- [ ] No trades in PRE_OR, BUILDING_OR, CHOP, or FLAT_LOCK states
- [ ] NEUTRAL state blocks or restricts trades (default: block)

### ✅ Telemetry
- [ ] CSV includes `orb_state` column
- [ ] CSV includes `orb_reason` column
- [ ] ORB_STATE records written at each transition
- [ ] Can track session evolution from telemetry

### ✅ Protection Layers
- [ ] Choppy day filter still works (3 consecutive losses)
- [ ] Daily max loss still triggers ($200)
- [ ] Emergency stop still triggers ($400 DD from peak)
- [ ] FLAT_LOCK state prevents new entries

---

## Testing Procedure

### Step 1: Compile and Load

```
1. Open NinjaTrader 8 on VPS
2. Tools → Compile
3. Verify "CG_MNQ_Flagship_Hybrid_v1_1" appears in strategy list
4. Fix any compile errors
```

### Step 2: Attach to Playback Chart

```
1. Load MNQ playback data (known session)
2. Set chart to 1-minute bars
3. Right-click → Strategies → Add → CG_MNQ_Flagship_Hybrid_v1_1
4. Use default parameters
5. Enable strategy
```

### Step 3: Verify State Transitions

**Watch Output window for**:
```
[ORB_STATE] PRE_OR -> BUILDING_OR at 09:30:00 reason=OR_START
[ORB_STATE] BUILDING_OR -> NEUTRAL at 09:45:15 reason=OR_COMPLETE_INSIDE_RANGE
[ORB_STATE] NEUTRAL -> LONG_BREAKOUT at 10:15:23 reason=PRICE_ABOVE_OR_HIGH_BUFFER
[ORB_STATE] LONG_BREAKOUT -> FAILED_LONG at 10:42:11 reason=LONG_BREAKOUT_RECLAIMED_INSIDE_OR
```

### Step 4: Verify Trading Behavior

**Check**:
- ❌ No trades during PRE_OR or BUILDING_OR (before 9:45 AM)
- ❌ No trades if CHOP state (low volatility)
- ✅ Longs allowed when state = LONG_BREAKOUT
- ✅ Shorts allowed when state = SHORT_BREAKOUT
- ✅ Direction reverses after failed breakout

### Step 5: Review Session Summary

**At end of playback, check**:
```
ORB State: [final state]
ORB State Transitions: [count]
ORB Long breakout transitions: [count]
ORB Short breakout transitions: [count]
```

### Step 6: Analyze Telemetry

**Import CSV and verify**:
- State transitions match expected session structure
- No premature trades (before state permissions)
- Failed breakouts properly detected and handled
- Correct directional filtering applied

---

## Common Issues & Solutions

### Issue: Too Many State Transitions

**Symptom**: ORB state flipping rapidly between states

**Solution**:
```
Increase OrbStateEvalSeconds from 1 to 5-10 seconds
This throttles state evaluation frequency
```

### Issue: No Trades All Day

**Check**:
1. Was ORB state stuck in CHOP? (low volatility day)
2. Was ORB state stuck in NEUTRAL? (price never broke out)
3. Was T2/T3/Padder blocking all signals?

**Solution**:
```
Review telemetry to see state progression
If stuck in CHOP: Lower MinRangeWidth
If stuck in NEUTRAL: Lower OrbBreakoutBuffer
If T2/T3 blocking: Review those layer parameters
```

### Issue: Missed Breakout

**Symptom**: Price broke out but no trade occurred

**Check**:
1. Did ORB state transition to LONG_BREAKOUT or SHORT_BREAKOUT?
2. Did T2 generate a signal?
3. Did T3 confirm with wall?
4. Did Padder block?

**Debug**:
```
Check Output window for state transition
Check telemetry for rejection reasons
Most likely: T2 or T3 layer filtered the entry
```

### Issue: Wrong Direction Trade

**Symptom**: Took a long when should have taken short (or vice versa)

**This should be IMPOSSIBLE in v1.1**:
```
If this happens, it's a critical bug
ORB state machine strictly enforces directional permissions
Review telemetry to see what state was active at entry time
```

---

## v1.1 vs v1.0 Performance Expectations

### Expected Improvements

**Better Breakout Capture**:
```
v1.0: Misses delayed breakouts (after 9:45)
v1.1: Catches breakouts throughout session
Expected: +10-20% more valid signals
```

**Better Failed Breakout Handling**:
```
v1.0: Continues trading original bias after failure
v1.1: Immediately reverses permissions on failure
Expected: -20-30% reduction in trap losses
```

**Better Regime Classification**:
```
v1.0: Static bias for entire session
v1.1: Dynamic adaptation as session evolves
Expected: +5-10% improvement in win rate
```

### Potential Tradeoffs

**Slightly Lower Trade Frequency**:
```
v1.1 blocks NEUTRAL state by default
v1.0 allowed some trades in neutral conditions
Result: Fewer total trades, but higher quality
```

**More Complex Telemetry**:
```
v1.1 records every state transition
More data to analyze, but better visibility
```

---

## Next Steps After v1.1 Validation

### Immediate (First Week)

- [ ] Run v1.1 in paper trading for 3-5 sessions
- [ ] Compare to v1.0 behavior on same sessions
- [ ] Verify state transitions make sense
- [ ] Confirm directional filtering works correctly
- [ ] Check telemetry completeness

### Near-Term (2-4 Weeks)

- [ ] Backtest v1.1 on Sep-Oct 2025 data
- [ ] Compare performance to v1.0 baseline
- [ ] Measure breakout capture improvement
- [ ] Measure failed breakout avoidance
- [ ] Tune OrbStateEvalSeconds if needed

### Future Versions

**v1.2**: True T2 event-flow reconstruction
- Improve event delta/imbalance calculations
- Add volume profile integration
- Enhance tick data processing

**v1.3**: T3 wall persistence and spoof resistance
- Track wall persistence over time
- Detect spoof orders (add then cancel)
- Measure actual absorption vs spoofing

**v1.4**: Expanded Padder manipulation model
- Prior day high/low sweep detection
- Session box liquidity grab patterns
- Multi-day level tracking

**v2.0**: Flagship candidate integration
- Full multi-timeframe integration
- Adaptive parameter optimization
- Machine learning signal weighting

---

## File Locations

**Strategy**:
```
/home/bernard/trading4/CG_MNQ_MarketReplayLab/ninjascript/CG_MNQ_Flagship_Hybrid_v1_1.cs
```

**Documentation**:
```
/home/bernard/trading4/CG_MNQ_MarketReplayLab/docs/FLAGSHIP_v1_1_ORB_STATE_MACHINE.md
```

**Deployment** (requires sudo password):
```bash
sudo cp ~/trading4/CG_MNQ_MarketReplayLab/ninjascript/CG_MNQ_Flagship_Hybrid_v1_1.cs /mnt/vps_strategies/
```

---

## McDuff's Final Assessment

```
v1.1 is NOT about higher PnL immediately.
v1.1 is about CORRECT COMMAND-STATE BEHAVIOR.

The static bias system was architecturally flawed.
Markets evolve. Strategies must evolve with them.

The ORB state machine is now the macro-structural spine.
It adapts. It transitions. It responds to session reality.

Once this foundation is correct,
the rest of the flagship stack has a reliable command structure.

First make ORB intelligent.
Then make T2 tactical.
Then make T3 precise.
Then make Padder deception-aware.
Then optimize.

This is the path.
```

---

**END OF v1.1 ORB STATE MACHINE DOCUMENTATION**
