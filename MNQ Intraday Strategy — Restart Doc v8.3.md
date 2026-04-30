## MNQ Intraday Strategy — Restart Doc v8.3

### Strategic Continuation Update

### Status: NT8 execution chassis operational; parity restoration and selective density tuning underway.

Primary source baseline remains v8.2 / v8.2.1. This update reflects major NT8 LiveSignal progression. 

---

# Strategic Mission

```text
Survival first.
Execution integrity second.
Profit scale third.
```

---

# Version Update

```text
v8.3
```

### Major additions:

* `CG_T2_ClanMarshal_LiveSignal_v1.cs` operational
* OCO++ execution harness materially validated
* Emergency flatten bug corrected
* RTH leakage largely corrected
* Parity diagnostics integrated
* Frequency restoration mode validated
* Controlled-density tuning phase initiated

---

# Current Strategic Hierarchy

## T3 Cuileanan Dedup

### Role:

```text
Treasury defense / first cautious deployment candidate
```

### Metrics:

| Metric     |    Value |
| ---------- | -------: |
| Trades     |       62 |
| PF         |    2.993 |
| Expectancy |   $64.14 |
| Max DD     | -$362.80 |

---

## T2 ClanMarshal

### Role:

```text
Primary capital-growth and scaling strategy
```

### CH benchmark:

| Metric     |    Value |
| ---------- | -------: |
| Trades     |      908 |
| PF         |     2.93 |
| Expectancy |   $78.67 |
| Max DD     | -$637.70 |

---

# NT8 Workstream — Major Advancement

---

# `CG_T2_ClanMarshal_LiveSignal_v1`

## Phase:

```text
Execution chassis + parity diagnostic scaffold
```

---

## Validated:

### Mechanical success:

* Compiles
* Playback101 operational
* Entries submit
* Limit-first works
* Profit targets work
* Stop losses work
* Broker-side protective brackets attach
* OCO cancellations function
* Emergency flatten corrected
* No naked positions
* Daily governors active
* RTH controls active
* Diagnostics active

---

## Core doctrine:

```text
Signal
→ LIMIT first
→ timeout
→ conditional MARKET fallback
→ broker-side stop/target
→ immediate flatten only if true protection failure
```

---

# Major Bug Fixes Completed

## 1. False Emergency Flatten

### Previous issue:

```text
Flattening valid trades despite active stop orders
```

### Fixed:

```text
Emergency flatten now reserved for:
- Missing stop
- Invalid protection
- Order rejection
- Structural protection failure
```

---

## 2. RTH Leakage

### Previous:

Pre-open 9:29 / 9:30 leakage

### Correction:

```text
StartTimeEt adjusted
RTH buffer enforced
```

---

## 3. Frequency Suppression

### Discovery:

Low trade counts were caused by:

* Threshold overrestriction
* Cooldown
* OneTradePerSession
* Proxy conservatism

### Result:

Parity mode restored materially higher trade density.

---

# Parity Diagnostic System Added

## Tracks:

```text
RTH rejects
Spread rejects
Wall rejects
Delta rejects
Momentum rejects
Cooldown rejects
Pending-order rejects
Governor rejects
Limit timeouts
Fallback markets
Fallback skips
Signal long/short counts
```

### Purpose:

```text
Restore CH trade opportunity density before selective re-hardening
```

---

# Tactical Evolution

---

## Phase A — Conservative Chassis

```text
Very low trade count
```

---

## Phase B — Full Parity Mode

```text
High trade count
Overactive open cluster
```

---

## Phase C — Controlled Density (Current)

### Default posture:

```text
StartTimeEt = 93500
CooldownSeconds = 30
EmergencyCooldownSeconds = 60

MinWallScoreProxy = 5
MinDeltaAbs = 10
MinEventCountDeltaProxy = -250
MomentumConfirmTicks = 1

LowFlowMarketThreshold = 5000
MediumFlowMarketThreshold = 15000

MaxSpreadTicks = 8
```

---

# Strategic Objective (Current)

```text
Recover meaningful trade density
WITHOUT
open-cluster overfire
```

---

# Current Doctrine

```text
Parity first.
Then selective lethality.
Then CH feature fidelity.
Then deployment.
```

---

# Immediate Next Engineering Targets

---

## `CG_T2_ClanMarshal_LiveSignal_v1.1`

### Required upgrades:

* Better opening-range awareness
* Session regime discrimination:

  * POST_OPEN
  * LUNCH
  * CLOSE_30
* Consecutive-loss governor
* Enhanced wall-score proxy tuning
* Better short-side parity
* CH feature calibration
* CSV audit trail
* Live/CH parity measurement

---

## Future:

### `CG_T2_ClanMarshal_Model_v1.cs`

### Goal:

```text
True production alpha implementation
```

---

# Deployment Ladder

```text
Playback101
→ Sim101
→ VPS
→ Micro-live
→ Scale cautiously
```

---

# Forbidden

```text
Naked positions
Local-only stop logic
Unvalidated trailing stops
Premature size increase
Alpha assumptions from smoke-test data
```

---

# Strategic Decision as of v8.3

```text
T3 remains treasury guardian.
T2 now possesses a functioning battlefield chassis.
Current mission:
Restore and refine T2 selective lethality.
```

---

# McDuff Doctrine Update

```text
The rifle is operational.
The trigger discipline is improving.
Now we train battlefield accuracy.
```

---

# Bottom Line

You have now transitioned from:

```text
Conceptual strategy research
```

to:

```text
Execution systems engineering
Parity restoration
Signal calibration
Pre-production deployment architecture
```

---

# Current Highest-Value Priority

```text
T2 LiveSignal v1.1
Controlled-density refinement
CH parity calibration
```

