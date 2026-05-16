# CG MNQ Intraday Strategy — Restart Summary v8.4

## Strategic Status

Project has successfully progressed from:

```text
Infrastructure setup
→ NT8 playback validation
→ Basic ORB/T2 smoke testing
→ Execution discipline refinement
→ Recognition of missing Premarket Structural Layer
```

---

# Core Infrastructure Proven

## Verified:

### ClickHouse:
- Local Linux CH operational
- Historical MNQ export pipeline functional
- Parquet/ZSTD compression pathway established
- NT-compatible CSV export path proven
- rclone transfer pipeline established
- VPS import workflow operational

---

### NinjaTrader Playback:
- Historical import functional
- MNQ contract mapping validated
- Playback stable
- Strategy compilation functional
- OCO stop/target protections functional
- Session resets functional

---

# Strategy Evolution Timeline

---

## v1.0 Smoke:
```text
ORB + basic T2
```

### Results:
- Infrastructure validation PASS
- Excessive breakout chasing
- Overtrading
- No cooldown
- Weak retest quality
- Alpha weak

---

## v1.1 Smoke:
```text
Retest entries
Cooldown
Trade caps
Relative volume
```

### Results:
- Major improvement
- Better discipline
- Reduced overtrading
- More stable execution
- Still structurally blind to premarket context

---

## v1.2 Smoke:
```text
Breakout expiration
Stronger OR reclaim
Adaptive stops
Midday suppression
```

### Results:
- Improved OR quality
- Better session survivability
- Still misses major premarket reversal opportunities

---

# Major Strategic Discovery

## Critical Missing Layer:

```text
Layer 0: Premarket Structural Engine
```

---

### Missing capabilities:
- Overnight high/low
- Overnight VWAP
- Triple top/bottom
- Failed overnight breakouts
- Premarket exhaustion
- Gap stretch analysis
- Distribution/accumulation recognition
- Session handoff bias

---

# April 24 Key Insight

## Human discretionary recognition:

```text
90-point overnight rise
Triple-top resistance
Breakdown before RTH
High-probability short bias
```

---

## Current system failure:

```text
ORB-only bias
→ Blind long breakout attempts
→ Missed macro fade opportunity
```

---

# Strategic Conclusion

Current architecture begins too late:

```text
Starts at 9:30
Should begin overnight
```

---

# Revised Flagship Architecture

```text
Layer 0 = Premarket Structural Bias
Layer 1 = ORB Macro Spine
Layer 2 = T2 Tactical Trigger
Layer 3 = T3 Precision Confirmation
Layer 4 = Padder Manipulation Shield
Layer 5 = Treasury / Protection Governance
```

---

# Immediate Development Priorities

## Phase Next:

### Build:
```text
CG_MNQ_ORB_T2_PlaybackSmoke_v1_3
```

### Required additions:
- Overnight session range
- Premarket high/low
- Premarket VWAP
- Triple test recognition
- Failed breakout detection
- Gap extension exhaustion
- Premarket directional bias weighting

---

# Practical Trading Doctrine Shift

## Old:
```text
ORB breakout strategy
```

---

## New:
```text
Session-structure institutional execution engine
```

---

# Key Engineering Directives

## Maintain:
- OCO++ protection
- One-contract validation
- NT playback stability
- ClickHouse export backbone
- Modular code architecture
- Telemetry discipline

---

## Improve:
- Layer 0 context
- Adaptive volatility
- Dynamic stop sizing
- Regime classification
- Session-type identification
- Wall persistence (future)
- True delta/event flow (future)

---

# Deployment Ladder

| Stage | Status |
|------|--------|
| CH export/import | Proven |
| NT playback | Proven |
| Basic smoke shell | Proven |
| ORB refinement | Improving |
| Premarket layer | REQUIRED |
| T3 wall engine | Future |
| Full flagship | Not yet |

---

# McDuff Strategic Assessment

```text
Current state:
Operational prototype with emerging edge.

Next leap:
Premarket institutional structure.
```

---

# Final Directive

## Focus:

```text
Do not merely optimize entries.

Expand contextual intelligence backward
into overnight and premarket structure.
```

---

# Bottom Line

Project has moved from:

```text
"Can it run?"
```

to:

```text
"Can it think structurally enough to capture real edge?"
```

The next answer lies in
