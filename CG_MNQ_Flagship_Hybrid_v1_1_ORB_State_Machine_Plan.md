# CG_MNQ_Flagship_Hybrid_v1_1 — ORB State Machine Refactor Plan

## Executive Bottom Line

The next correct step is **not** parameter sweeping or adding more filters.

The next step is to refactor the flagship hybrid strategy so that the ORB layer becomes a **dynamic finite-state regime engine** rather than a one-time 9:45 AM bias assignment.

```text
Current flaw:
ORB bias is determined once after the opening range completes.

Required improvement:
ORB state must continuously evolve as the session develops.
```

This creates the structural foundation for the later T2, T3 Wall, Padder, and OCO++ layers.

---

# Target File

```text
CG_MNQ_Flagship_Hybrid_v1_1.cs
```

This should be a controlled refactor of:

```text
CG_MNQ_Flagship_Hybrid_v1_0.cs
```

Do **not** add all future enhancements at once. Version 1.1 should focus mainly on the ORB state machine.

---

# Primary Objective

Replace the static ORB bias system with a dynamic finite-state ORB regime engine.

## Replace

```csharp
enum OrbBias
{
    NONE,
    BULLISH,
    BEARISH,
    NEUTRAL,
    MANIPULATION
}
```

## With

```csharp
enum OrbState
{
    PRE_OR,
    BUILDING_OR,
    NEUTRAL,
    LONG_BREAKOUT,
    SHORT_BREAKOUT,
    FAILED_LONG,
    FAILED_SHORT,
    CHOP,
    FLAT_LOCK
}
```

---

# Why This Comes First

The existing v1.0 logic determines ORB bias once after the opening range completes. That makes the whole system structurally blind to later session evolution.

## Problems caused by static ORB bias

```text
- Delayed breakouts can be missed.
- Failed breakouts are not handled as state transitions.
- Reversal behavior is flattened into simple manipulation flags.
- Neutral sessions do not evolve into trend sessions.
- Trend sessions cannot degrade into failed-breakout sessions.
- T2/T3/Padder layers inherit stale macro context.
```

## Correct doctrine

```text
ORB is not a one-time signal.
ORB is the command-state engine for the session.
```

---

# Version 1.1 Strategic Scope

## Must include

```text
1. Dynamic ORB state machine
2. Directional permission mapping by ORB state
3. State transition telemetry
4. Transition reasons
5. Breakout/failure timestamps
6. Preservation of one-contract limit
7. Preservation of OCO++ stop/target protection
8. Preservation of current telemetry infrastructure
```

## Should include if simple

```text
1. Lunch suppression flag
2. OR width chop classification
3. Delayed breakout recognition
```

## Should NOT include yet

```text
1. Full T2 event-flow redesign
2. Full T3 wall persistence engine
3. Full Padder prior-day sweep engine
4. Parameter sweeping
5. Adaptive position sizing
6. Complex volatility percentile engine
```

Those belong to later versions.

---

# Recommended Version Roadmap

```text
v1.1 = ORB state machine
v1.2 = True T2 event-flow reconstruction
v1.3 = T3 wall persistence and spoof resistance
v1.4 = Expanded Padder manipulation model
v2.0 = Flagship candidate integration
```

---

# ORB State Definitions

## PRE_OR

Used before the opening range begins.

```text
Condition:
Time < 9:30 ET
```

Trading permission:

```text
Flat only
```

---

## BUILDING_OR

Used while the 9:30–9:45 ET opening range is forming.

```text
Condition:
9:30 ET <= time < 9:45 ET
```

Trading permission:

```text
Flat only
```

Purpose:

```text
Build OR high, OR low, OR width, OR VWAP, OR volume.
```

---

## NEUTRAL

Used after OR completion when price has not made a confirmed breakout.

```text
Condition:
OR complete, price inside OR, no confirmed expansion.
```

Trading permission:

```text
Usually restricted.
Can allow only high-quality T3-confirmed trades later if desired.
```

For v1.1, safest behavior:

```text
No trade or highly restricted trade.
```

---

## LONG_BREAKOUT

Used when price confirms an upside break of the opening range.

```text
Condition:
Price > OR high + breakout buffer
and optional confirmation conditions pass.
```

Trading permission:

```text
Allow longs only.
Suppress shorts.
```

Downstream behavior:

```text
T2 longs may fire if tactical conditions pass.
T3 must still confirm execution quality.
Padder may still veto if breakout failure is detected.
```

---

## SHORT_BREAKOUT

Used when price confirms a downside break of the opening range.

```text
Condition:
Price < OR low - breakout buffer
and optional confirmation conditions pass.
```

Trading permission:

```text
Allow shorts only.
Suppress longs.
```

---

## FAILED_LONG

Used when upside breakout fails and price returns back under OR high.

```text
Condition:
Prior state was LONG_BREAKOUT
and price closes or trades back below OR high within failure window.
```

Trading permission:

```text
Suppress longs.
Allow fade shorts only if T2/T3/Padder confirm.
```

---

## FAILED_SHORT

Used when downside breakout fails and price returns back above OR low.

```text
Condition:
Prior state was SHORT_BREAKOUT
and price closes or trades back above OR low within failure window.
```

Trading permission:

```text
Suppress shorts.
Allow fade longs only if T2/T3/Padder confirm.
```

---

## CHOP

Used when range/behavior indicates low-quality trading conditions.

```text
Possible conditions:
- OR width below minimum threshold
- repeated failed breakouts
- low expansion
- excessive whipsaw
```

Trading permission:

```text
Restrict or disable entries.
```

---

## FLAT_LOCK

Used when treasury or safety rules require no trading.

```text
Possible causes:
- Daily max loss hit
- Emergency drawdown stop
- Manual future lockout hook
```

Trading permission:

```text
Flat only.
No new entries.
```

---

# Directional Permission Matrix

| ORB State | Long Permission | Short Permission | Notes |
|---|---:|---:|---|
| PRE_OR | No | No | Before open range |
| BUILDING_OR | No | No | Build structure only |
| NEUTRAL | Restricted | Restricted | Default v1.1 should be no trade |
| LONG_BREAKOUT | Yes | No | Trend-following long mode |
| SHORT_BREAKOUT | No | Yes | Trend-following short mode |
| FAILED_LONG | No | Yes | Fade failed upside breakout |
| FAILED_SHORT | Yes | No | Fade failed downside breakout |
| CHOP | No or restricted | No or restricted | Prefer no trade initially |
| FLAT_LOCK | No | No | Treasury lockout |

---

# Required Code Refactor Tasks

## 1. Add ORB state fields

Add fields similar to:

```csharp
private enum OrbState
{
    PRE_OR,
    BUILDING_OR,
    NEUTRAL,
    LONG_BREAKOUT,
    SHORT_BREAKOUT,
    FAILED_LONG,
    FAILED_SHORT,
    CHOP,
    FLAT_LOCK
}

private OrbState currentOrbState = OrbState.PRE_OR;
private OrbState previousOrbState = OrbState.PRE_OR;
private string orbTransitionReason = "INIT";

private DateTime orbStateChangedTime = Core.Globals.MinDate;
private DateTime firstLongBreakoutTime = Core.Globals.MinDate;
private DateTime firstShortBreakoutTime = Core.Globals.MinDate;
private DateTime failedLongTime = Core.Globals.MinDate;
private DateTime failedShortTime = Core.Globals.MinDate;

private int longBreakoutCount = 0;
private int shortBreakoutCount = 0;
private int failedBreakoutCount = 0;
```

---

## 2. Create SetOrbState()

Purpose:

```text
Centralize state transition logging, telemetry, and diagnostics.
```

Suggested skeleton:

```csharp
private void SetOrbState(OrbState newState, string reason, DateTime etNow)
{
    if (newState == currentOrbState)
        return;

    previousOrbState = currentOrbState;
    currentOrbState = newState;
    orbTransitionReason = reason;
    orbStateChangedTime = etNow;

    if (PrintDiagnostics)
    {
        Print(string.Format(
            "[ORB_STATE] {0} -> {1} at {2:HH:mm:ss} | {3}",
            previousOrbState,
            currentOrbState,
            etNow,
            reason));
    }

    WriteTelemetry("ORB_STATE", reason);
}
```

---

## 3. Create UpdateOrbState()

This should be called after OR completion on every eligible primary update, or throttled if needed.

Suggested responsibilities:

```text
1. Respect FLAT_LOCK if risk lockouts are active.
2. Set PRE_OR before 9:30.
3. Set BUILDING_OR while OR is active.
4. Set CHOP if OR width is below threshold.
5. Detect long breakout.
6. Detect short breakout.
7. Detect failed long breakout.
8. Detect failed short breakout.
9. Keep NEUTRAL if no breakout is confirmed.
```

Suggested skeleton:

```csharp
private void UpdateOrbState(DateTime etNow)
{
    if (dailyMaxLossHit || emergencyStopTriggered)
    {
        SetOrbState(OrbState.FLAT_LOCK, "RISK_LOCKOUT", etNow);
        return;
    }

    if (!orActive && !orComplete)
    {
        SetOrbState(OrbState.PRE_OR, "BEFORE_OPEN_RANGE", etNow);
        return;
    }

    if (orActive)
    {
        SetOrbState(OrbState.BUILDING_OR, "OPENING_RANGE_BUILDING", etNow);
        return;
    }

    if (!orComplete)
        return;

    if (orWidth < MinRangeWidth)
    {
        SetOrbState(OrbState.CHOP, "OR_WIDTH_BELOW_MIN", etNow);
        return;
    }

    double px = Close[0];

    bool aboveOr = px > orHigh + OrbBreakoutBuffer;
    bool belowOr = px < orLow - OrbBreakoutBuffer;
    bool backInsideFromLong = currentOrbState == OrbState.LONG_BREAKOUT && px < orHigh;
    bool backInsideFromShort = currentOrbState == OrbState.SHORT_BREAKOUT && px > orLow;

    if (backInsideFromLong)
    {
        failedBreakoutCount++;
        failedLongTime = etNow;
        failedBreakoutDetected = true;
        manipulationReason = "FAILED_LONG_BREAKOUT";
        SetOrbState(OrbState.FAILED_LONG, "LONG_BREAKOUT_FAILED_BACK_INSIDE_OR", etNow);
        return;
    }

    if (backInsideFromShort)
    {
        failedBreakoutCount++;
        failedShortTime = etNow;
        failedBreakoutDetected = true;
        manipulationReason = "FAILED_SHORT_BREAKOUT";
        SetOrbState(OrbState.FAILED_SHORT, "SHORT_BREAKOUT_FAILED_BACK_INSIDE_OR", etNow);
        return;
    }

    if (aboveOr)
    {
        if (firstLongBreakoutTime == Core.Globals.MinDate)
            firstLongBreakoutTime = etNow;

        longBreakoutCount++;
        failedBreakoutDetected = false;
        manipulationReason = "";
        SetOrbState(OrbState.LONG_BREAKOUT, "PRICE_ABOVE_OR_HIGH_BUFFER", etNow);
        return;
    }

    if (belowOr)
    {
        if (firstShortBreakoutTime == Core.Globals.MinDate)
            firstShortBreakoutTime = etNow;

        shortBreakoutCount++;
        failedBreakoutDetected = false;
        manipulationReason = "";
        SetOrbState(OrbState.SHORT_BREAKOUT, "PRICE_BELOW_OR_LOW_BUFFER", etNow);
        return;
    }

    if (currentOrbState == OrbState.PRE_OR || currentOrbState == OrbState.BUILDING_OR)
    {
        SetOrbState(OrbState.NEUTRAL, "OR_COMPLETE_PRICE_INSIDE_RANGE", etNow);
    }
}
```

---

## 4. Replace DetermineOrbBias()

The current `DetermineOrbBias()` function should be removed or made diagnostic-only.

Do not rely on a static `currentOrbBias` as the primary trade permission gate.

Replace downstream references to:

```csharp
currentOrbBias
```

with:

```csharp
currentOrbState
```

---

## 5. Add permission helper methods

Suggested:

```csharp
private bool OrbAllowsLong()
{
    return currentOrbState == OrbState.LONG_BREAKOUT
        || currentOrbState == OrbState.FAILED_SHORT;
}

private bool OrbAllowsShort()
{
    return currentOrbState == OrbState.SHORT_BREAKOUT
        || currentOrbState == OrbState.FAILED_LONG;
}
```

For v1.1, keep it simple and conservative.

---

## 6. Refactor EvaluateLongSignal()

Replace:

```csharp
if (currentOrbBias != OrbBias.BULLISH)
```

with:

```csharp
if (!OrbAllowsLong())
```

Then keep T2, T3, and Padder confirmation logic mostly unchanged for v1.1.

---

## 7. Refactor EvaluateShortSignal()

Replace:

```csharp
if (currentOrbBias != OrbBias.BEARISH)
```

with:

```csharp
if (!OrbAllowsShort())
```

---

# Main Loop Placement

In `OnBarUpdate()`, the recommended flow is:

```text
1. Session reset
2. Start/update/complete opening range
3. Update ORB state
4. Monitor existing position
5. RTH check
6. Risk lockout checks
7. Ensure OR complete
8. Compute T2/T3/Padder features
9. Evaluate signals
10. Submit orders
```

Suggested location:

```csharp
// After OR update/start/complete logic:
UpdateOrbState(etNow);
```

Be careful not to return before the state machine has a chance to record transitions.

---

# Telemetry Changes

## Add columns

Add these to telemetry header:

```text
orb_state,
previous_orb_state,
orb_transition_reason,
orb_state_changed_time,
first_long_breakout_time,
first_short_breakout_time,
failed_long_time,
failed_short_time,
long_breakout_count,
short_breakout_count,
failed_breakout_count
```

## Replace / keep

You can keep `orb_bias` for backward compatibility, but `orb_state` becomes the authoritative field.

Recommended:

```text
Keep orb_bias column temporarily.
Add orb_state columns.
Later remove orb_bias in v1.2 or v2.0.
```

---

# Diagnostic Print Additions

Add summary output:

```text
ORB state
Previous ORB state
Transition reason
First long breakout time
First short breakout time
Failed long time
Failed short time
Long breakout count
Short breakout count
Failed breakout count
```

Example:

```csharp
Print(string.Format("  ORB State: {0}", currentOrbState));
Print(string.Format("  Previous ORB State: {0}", previousOrbState));
Print(string.Format("  ORB Transition Reason: {0}", orbTransitionReason));
Print(string.Format("  Long Breakouts: {0}", longBreakoutCount));
Print(string.Format("  Short Breakouts: {0}", shortBreakoutCount));
Print(string.Format("  Failed Breakouts: {0}", failedBreakoutCount));
```

---

# Optional v1.1 Enhancements

These are acceptable only if they do not destabilize the refactor.

## 1. Lunch suppression

```csharp
private bool IsLunchRegime(DateTime et)
{
    int hhmmss = et.Hour * 10000 + et.Minute * 100 + et.Second;
    return hhmmss >= 113000 && hhmmss < 133000;
}
```

Then either:

```text
- block new entries during lunch, or
- require stronger T2/T3 confirmation during lunch.
```

For v1.1, safest behavior:

```text
Block lunch entries.
```

---

## 2. Delayed breakout tracking

Track whether breakout occurs after 10:00 ET:

```csharp
private bool IsDelayedBreakout(DateTime et)
{
    int hhmmss = et.Hour * 10000 + et.Minute * 100 + et.Second;
    return hhmmss >= 100000;
}
```

Telemetry label:

```text
DELAYED_LONG_BREAKOUT
DELAYED_SHORT_BREAKOUT
```

---

## 3. Repeated failure chop rule

Simple conservative rule:

```csharp
if (failedBreakoutCount >= 2)
{
    SetOrbState(OrbState.CHOP, "MULTIPLE_FAILED_BREAKOUTS", etNow);
    return;
}
```

This helps avoid whipsaw days.

---

# Acceptance Criteria

Version 1.1 is acceptable only if:

```text
1. Strategy compiles cleanly in NinjaTrader 8.
2. One-contract rule remains enforced.
3. OCO++ stop/target remains armed before entry.
4. No new entries occur before OR completion.
5. ORB state transitions appear in diagnostics.
6. Telemetry records ORB state and transition reason.
7. Longs are only allowed in LONG_BREAKOUT or FAILED_SHORT.
8. Shorts are only allowed in SHORT_BREAKOUT or FAILED_LONG.
9. CHOP and FLAT_LOCK suppress entries.
10. Existing T2/T3/Padder logic remains mostly unchanged for attribution clarity.
```

---

# Validation Procedure

## Step 1 — Compile

```text
NinjaTrader 8 → New → NinjaScript Editor → Compile
```

Fix all compile errors before testing behavior.

---

## Step 2 — Playback smoke test

Use a known MNQ playback session.

Check Output window for:

```text
[ORB_STATE] PRE_OR -> BUILDING_OR
[ORB_STATE] BUILDING_OR -> NEUTRAL
[ORB_STATE] NEUTRAL -> LONG_BREAKOUT
or
[ORB_STATE] NEUTRAL -> SHORT_BREAKOUT
```

---

## Step 3 — Confirm no premature trades

Verify:

```text
No trades before 9:45 ET.
No trades while state is PRE_OR or BUILDING_OR.
No trades in CHOP or FLAT_LOCK.
```

---

## Step 4 — Confirm directional gating

For LONG_BREAKOUT:

```text
Longs may occur.
Shorts must be suppressed.
```

For SHORT_BREAKOUT:

```text
Shorts may occur.
Longs must be suppressed.
```

For FAILED_LONG:

```text
Longs must be suppressed.
Fade shorts may occur if T2/T3 permit.
```

For FAILED_SHORT:

```text
Shorts must be suppressed.
Fade longs may occur if T2/T3 permit.
```

---

## Step 5 — Review telemetry

Confirm CSV contains useful records:

```text
ORB_STATE
OR_COMPLETE
SIGNAL
ENTRY
EXIT
PROTECTION
```

Confirm ORB state fields are populated.

---

# What Not To Optimize Yet

Do not optimize these until v1.1 passes behavior validation:

```text
MinEventDelta
MinEventImbalance
MinWallSize
MinAggressorVolume
StopTicks
TargetTicks
FailedBreakoutBars
OrbBreakoutBuffer
```

Reason:

```text
Until the macro state engine is correct, downstream optimization is contaminated.
```

---

# McDuff Directive

```text
First make ORB intelligent.
Then make T2 tactical.
Then make T3 precise.
Then make Padder deception-aware.
Then optimize.
```

---

# Final Bottom Line

```text
CG_MNQ_Flagship_Hybrid_v1_1 should be a disciplined structural refactor.

Its purpose is not higher PnL immediately.
Its purpose is correct command-state behavior.

Once the ORB state machine is correct, the rest of the flagship stack has a reliable macro spine.
```
