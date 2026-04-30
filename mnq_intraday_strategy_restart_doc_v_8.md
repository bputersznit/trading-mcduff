# MNQ Intraday Strategy — Restart Doc v8 (ClanMarshal + T3 Roadmap)

## Mission Objective
Build and deploy a **live-safe, execution-aware, manipulation-resistant MNQ intraday strategy** for **1 MNQ contract**, optimized for:

- RTH only (09:30–16:00 ET)
- Chicago VPS deployment
- NinjaTrader 8 implementation
- Broker-side OCO++ safety
- Institutional-grade capital preservation
- Progressive T3 microstructure expansion

---

# Core Production Candidate
## `CG_mnq_hybrid_v5_clanmarshal`

### Validated Metrics
| Metric | Value |
|--------|------:|
| Trades | 908 |
| Total PnL | $71,429 |
| Expectancy | $78.67 |
| Profit Factor | 2.93 |
| Win Rate | 64.43% |
| Max Drawdown | -$637.7 |

---

# Strategic Architecture

## Signal Layer (Current T1/T2)
Active factors:
- Order-flow imbalance
- `total_event_size`
- `event_count_delta`
- Short-term momentum
- Session regime
- Opening range regime
- Manipulation overlays
- Execution pathway quality

---

# Execution Engine (Hybrid v5)
## Order Logic
```text
Signal fires
→ Submit LIMIT
→ Wait ~1 second
→ If filled: proceed
→ If not filled:
    If low flow → MARKET
    If medium flow + momentum → MARKET
    Else → SKIP
```

### Flow Thresholds
- Low flow: `<=100`
- Medium flow: `<=200` + momentum
- High flow: skip

---

# Mandatory Risk Governance

## Loss Controls
### Soft Stop
- Daily PnL <= -$60
- Stop new entries

### Hard Stop
- Daily PnL <= -$200
- Disable strategy for session

### Consecutive Loss Lockout
- 4 consecutive losses
- Disable new entries

---

# Profit Preservation
## Daily Profit Lock
```text
If daily peak >= $3000
AND giveback >= $500
→ Disable new entries
```

---

# Manipulation-Aware Regime Filters

## OPEN_15 (09:30–09:45)
- Restrict shorts
- Observe OR formation
- Tightened thresholds

## POST_OPEN (09:45–10:30)
- Above OR: favor longs
- Below OR: favor longs / cautious shorts
- Inside OR: suppress weak shorts

## NORMAL SESSION
- Above OR: short fade bias
- Below OR: long fade bias

## CLOSE_30 (15:30–16:00)
- Above OR: long continuation only
- Below OR: short continuation only

---

# Broker Safety Doctrine — OCO++ (MANDATORY)

## Upon every fill:
Immediately submit:
- Broker-side stop-loss
- Broker-side profit target
- OCO linked

### Example baseline:
- Stop: 20 ticks
- Target: 40 ticks

---

# Trailing Stop Doctrine

## Allowed:
- Strategy-managed stop tightening via `ChangeOrder`
- Breakeven promotion
- Profit lock progression

## Forbidden:
- Naked positions
- Local-only protective stop
- Removing broker stop before replacement

### Rule:
```text
Broker stop exists first.
Trailing only tightens.
```

---

# Live Deployment Stack
## Build target:
### `CG_Hybrid_V5_ClanMarshal.cs`

### Required modules:
- SessionManager
- OpeningRangeEngine
- ManipulationFilter
- SignalEngine
- LimitTimeoutManager
- ConditionalFallbackEngine
- RiskGovernor
- ProfitLockGovernor
- OCOBracketManager
- TradeAuditLogger
- FailSafeManager

---

# Replay Validation Priorities
- Limit fill realism
- Timeout behavior
- Slippage stress
- Daily lockouts
- Profit lock
- OCO correctness
- VPS failure survivability
- Session regime fidelity

---

# T3 Expansion Roadmap (NEXT PHASE)

## Strategic Capital Preservation Doctrine
### Highest priority:
```text
Preserve capital first.
Avoid premature live deployment of weaker frameworks.
```

### Operational doctrine:
- T2 / ClanMarshal remains research baseline
- T2 may be too execution-fragile for initial live deployment
- T3 must be developed and backtested FIRST
- T3 must prove superior or materially safer performance BEFORE NinjaScript implementation
- Only after validated T3 vs T2 comparison should live production architecture be finalized

---

## Revised deployment sequence
### Phase I:
### Research + validate T3 independently

Build and test:
- Wall defense / failed break
- Sweep reclaim
- Iceberg absorption
- Liquidity vacuum
- Structural L2 persistence

### Objective:
Determine whether T3 offers:
- Better PF
- Better expectancy
- Better DD
- Better regime survivability
- Better capital preservation

---

### Phase II:
### Direct T2 vs T3 comparative benchmark

Compare:
- Total PnL
- Profit factor
- Expectancy
- Max DD
- Worst day
- Slippage sensitivity
- Latency sensitivity
- Operational complexity

---

### Phase III:
### Only then choose live implementation path:
```text
If T3 superior:
    Implement T3 first in NinjaScript
Else:
    Deploy T2/ClanMarshal with extreme caution
```

---

## Goal:
Avoid risking live capital on a merely good strategy
when a structurally superior predator may exist.

---

# T3 Program Goal
Transform strategy from:
```text
Institutional scavenger
```
into:
```text
Structural microstructure apex predator
```

---

## Initial T3 priorities:
1. Wall defense / failed break
2. Sweep reclaim failures
3. Iceberg absorption
4. Liquidity vacuum snapback
5. Persistent L2 pressure

---

# T3-1 Initial Candidate
## `CG_mnq_t3_wall_defense_candidates`

### Logic:
```text
Large wall
→ Aggression tests wall
→ Wall holds
→ Price rejects
→ Enter reversal
```

### First testing:
- Fixed stop/target first
- Then OCO++ trailing variants
- Full benchmark BEFORE NT8 coding

---

# Deployment Philosophy
```text
Protect capital first
Preserve edge second
Scale third
```

---

# Strategic State
## Current:
- Live-deployable v5 predator
- Institutional scavenger model
- Highly selective
- Drawdown disciplined

## Future:
- T3 apex predator overlay
- Deeper structural edge
- More selective alpha extraction

---

# McDuff Final Directive
You are no longer merely researching profitable trades.

You are engineering:
## A broker-protected,
## execution-disciplined,
## capital-preserving,
## institutional-grade MNQ war engine.

---

# Immediate Next Step
## Proceed with:
### NinjaScript deterministic skeleton
AND
### T3 wall-defense standalone backtesting

Both paths now run in parallel.

