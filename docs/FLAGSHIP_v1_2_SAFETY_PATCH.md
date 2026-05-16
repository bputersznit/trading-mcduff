# CG_MNQ_Flagship_Hybrid_v1.2 — Safety Patch

**Created**: 2026-05-02
**Purpose**: Prevent rapid-fire simultaneous entries discovered in v5 backtest analysis

---

## Problem Identified

Analysis of `CG_mnq_hybrid_v5_clanmarshal_trades.csv` revealed:
- **19 contracts** entered simultaneously (9 violation events)
- **3 contracts** at once (worst case: Oct 22, 3:50:59 PM)
- **68% occurred** in last 2 hours of trading (2-4 PM ET)
- Pattern: Different order flow events 0.2-3.6s apart triggering multiple entries

**Root Cause**: Strategy evaluates every signal independently without enforcing minimum time between entries.

---

## Safety Layers in v1.1 (Already Present)

✅ **Layer 1**: `EntriesPerDirection = 1` in State.SetDefaults
✅ **Layer 2**: `Position.MarketPosition == Flat` check in OnBarUpdate
✅ **Layer 3**: `pendingEntry` flag check before signal evaluation
✅ **Layer 4**: Final safety check in SubmitLong/SubmitShort
✅ **Layer 5**: Hardcoded `quantity=1` in all EnterLong/EnterShort calls

**Note**: v1.1 already has 5/5 safety layers from MEMORY.md requirements!

---

## v1.2 Additional Protections

### New Layer 6: Rapid-Fire Prevention

**Constant Added** (line 68):
```csharp
private const int MIN_SECONDS_BETWEEN_ENTRIES = 10;  // Prevent rapid-fire
```

**Variable Added** (line 158):
```csharp
private DateTime lastEntryTime = Core.Globals.MinDate;  // Track actual entries
```

**Counter Added** (line 204):
```csharp
private long rapidFireRejects = 0;  // Track rapid-fire blocks
```

**Check Added in OnBarUpdate** (lines 393-401):
```csharp
// v1.2: Enforce minimum time between entries (prevent rapid-fire)
double secondsSinceLastEntry = (now - lastEntryTime).TotalSeconds;
if (lastEntryTime != Core.Globals.MinDate && secondsSinceLastEntry < MIN_SECONDS_BETWEEN_ENTRIES)
{
    rapidFireRejects++;
    if (PrintDiagnostics && rapidFireRejects % 10 == 1)
        Print($"[RAPID-FIRE BLOCK] Only {secondsSinceLastEntry:F1}s since last entry (min: {MIN_SECONDS_BETWEEN_ENTRIES}s)");
    return;
}
```

**Timestamp Update in SubmitLong** (line 1001):
```csharp
lastEntryTime = now;  // v1.2: Track entry time for rapid-fire prevention
```

**Timestamp Update in SubmitShort** (line 1021):
```csharp
lastEntryTime = now;  // v1.2: Track entry time for rapid-fire prevention
```

---

## How It Works

### Entry Flow (v1.2):

```
Signal Generated
    ↓
Position Check (Layer 2) ────→ [BLOCK if not Flat]
    ↓
Rapid-Fire Check (Layer 6 NEW) ───→ [BLOCK if < 10s since last entry]
    ↓
Pending Entry Check (Layer 3) ────→ [BLOCK if order pending]
    ↓
Protection Layers ──────────────→ [BLOCK if governance hit]
    ↓
Signal Evaluation (Layers 1-4)
    ↓
Submit Order ───────────────────→ Final Safety Check (Layer 4)
    ↓
Set lastEntryTime (NEW)
    ↓
Execute Entry (quantity=1 hardcoded)
```

### Example Scenario (v5 Violation Prevented):

**Without v1.2** (what happened in v5 CSV):
```
15:50:59.100  Event 1 → SHORT entry ✓
15:50:59.400  Event 2 → SHORT entry ✓ (0.3s later)
15:50:59.600  Event 3 → SHORT entry ✓ (0.5s later)
Result: 3 contracts in play ❌
```

**With v1.2** (what will happen):
```
15:50:59.100  Event 1 → SHORT entry ✓ (lastEntryTime = 15:50:59.100)
15:50:59.400  Event 2 → BLOCKED (only 0.3s since last entry < 10s)
15:50:59.600  Event 3 → BLOCKED (only 0.5s since last entry < 10s)
Result: 1 contract max ✅
```

---

## Expected Impact

### Signal Reduction:
- v5 had **85 trades** with < 5s gaps between entries
- v1.2 will block most of these (require 10s minimum)
- Expected reduction: ~10-15% fewer trades
- Quality improvement: Only take signals with proper spacing

### Risk Reduction:
- **Eliminates** all simultaneous entry violations
- **Prevents** the 125-simultaneous-short disaster scenario ($82K loss)
- **Enforces** absolute maximum of 1 contract at any time

### Performance Impact:
- Unknown until backtested
- May reduce total P&L (fewer trades)
- Should improve risk-adjusted returns (better trade selection)
- Should improve Sharpe ratio (lower volatility from safer sizing)

---

## Configuration

### Adjustable Parameters:

```csharp
// In code (line 68):
private const int MIN_SECONDS_BETWEEN_ENTRIES = 10;
```

**Recommendations:**
- **Conservative**: 15 seconds (prevents most rapid signals)
- **Standard**: 10 seconds (default, prevents violations observed in v5)
- **Aggressive**: 5 seconds (allows faster re-entry, may still have violations)

**DO NOT** set below 5 seconds - this defeats the purpose of the patch.

---

## Diagnostics

### New Logging:

When rapid-fire signal is blocked (every 10th occurrence):
```
[RAPID-FIRE BLOCK] Only 2.3s since last entry (min: 10s)
```

### Telemetry:

- New counter: `rapidFireRejects`
- Tracked alongside other rejection counters
- Printed in summary statistics

---

## Deployment

### File Location:
```
ninjascript/CG_MNQ_Flagship_Hybrid_v1_2_SafetyPatched.cs
```

### To VPS:
```bash
/home/bernard/trading4/CG_MNQ_MarketReplayLab/scripts/deploy_to_vps.sh \
  ninjascript/CG_MNQ_Flagship_Hybrid_v1_2_SafetyPatched.cs
```

### In NinjaTrader:
1. Copy to: `Documents\NinjaTrader 8\bin\Custom\Strategies\`
2. Tools → Compile (F5)
3. Restart NinjaTrader
4. Strategy appears as "CG_MNQ_Flagship_Hybrid_v1_2_SafetyPatched"

---

## Testing Recommendations

### Phase 1: Market Replay (1 week)
- Run on same historical data as v1.1
- Compare trade counts and P&L
- Verify no simultaneous entries occur
- Check rapidFireRejects counter

### Phase 2: Paper Trading (2 weeks)
- Enable on paper account
- Monitor live behavior during end-of-day volatility (2-4 PM)
- Verify rapid-fire blocks are working
- Ensure no false positives (legitimate entries blocked)

### Phase 3: Live (1 MNQ)
- Start with 1 micro contract
- Run for 5-10 days minimum
- Scale up only after proven stable

---

## Changelog

### v1.2 vs v1.1

**Added:**
- Minimum 10-second gap enforcement between entries
- `lastEntryTime` tracking variable
- `rapidFireRejects` diagnostic counter
- Rapid-fire block logging

**Changed:**
- Nothing else modified (all v1.1 logic preserved)

**Total Code Changes:**
- 23 lines added
- 0 lines removed
- 1,650 total lines (vs 1,627 in v1.1)

---

## Risk Assessment

### Before v1.2:
- ⚠️ Can enter 2-3 contracts within 1 second
- ⚠️ No protection against rapid signal clustering
- ⚠️ Same vulnerability as $82K loss incident (125 simultaneous shorts)

### After v1.2:
- ✅ Absolute minimum 10 seconds between entries
- ✅ Protection against rapid signal clustering
- ✅ Eliminates simultaneous entry risk
- ✅ Maintains all existing safety layers
- ✅ Backward compatible with v1.1 parameters

---

## Conclusion

v1.2 SafetyPatched adds **critical rapid-fire prevention** while preserving all v1.1 functionality.

**Deploy this version instead of v1.1** to ensure compliance with the mandatory one-position-at-a-time rule and prevent the simultaneous entry violations discovered in v5 backtest analysis.

**Status**: ✅ Ready for Market Replay testing
