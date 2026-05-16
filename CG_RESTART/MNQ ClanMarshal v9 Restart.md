# MNQ ClanMarshal v9 Restart Document

## Full Burn-Down / Rebuild Plan

### Date: May 2026

---

# Executive Summary

Legacy ClanMarshal versions (v5–v8c) are **not institutionally valid** due to major structural flaws:

```text
1. MBO action conflation
2. False top-of-book assumptions
3. Incorrect exit-time handling (epoch DateTime bug)
4. Missing full DOM/heatmap structure
5. Single-edge overreliance
```

---

# Core Strategic Pivot

## Old Model:

```text
Execution imbalance only
```

---

## New Model:

```text
Execution
+ Heatmap liquidity
+ Wall persistence
+ Pulling/stacking
+ Liquidity vacuums
+ Absorption
+ Stop-runs
+ Icebergs
+ Opening range interaction
+ Multi-timeframe convergence
```

---

# New Principle

```text
No single edge is sufficient.
Only convergence among multiple independent microstructure edges is deployable.
```

---

# New Architecture Overview

```text
Raw MBO
→ Action Classification
→ Price-Level Heatmap
→ Wall Detection
→ Bookmap Edge Modules
→ Convergence Scores
→ Trade Candidate Generation
→ Correct Exit Simulation
→ Risk Governance
→ Capital Scaling
→ NT8 Deployment
```

---

# PHASE 1 — Clean Action-Aware Foundation

## Objective:

Correct prior semantic contamination.

## Tables:

```text
CG_mnq_mbo_events_clean
CG_mnq_mbo_action_features_100ms
CG_mnq_mbo_action_derived_100ms
```

## Included features:

```text
ADD
CANCEL
MODIFY
TRADE
FILL
exec_delta
add_delta
cancel_delta
net_liquidity_delta
upside/downside vacuum
```

---

# PHASE 2 — Heatmap / DOM Structural Layer

## Objective:

Model liquidity walls, persistence, and structural market pressure.

## Tables:

```text
CG_mnq_heatmap_price_levels_100ms
CG_mnq_heatmap_walls_100ms
CG_mnq_heatmap_features_100ms
```

## Features:

```text
resting liquidity
wall_score
persistent walls
cancel ratios
wall vacuums
wall defense
wall breakout potential
```

---

# PHASE 3 — Bookmap Strategy Modules

---

## Module 1: Wall Rejection

```text
Large wall
Aggressive execution into wall
Wall persists
Price stalls
```

---

## Module 2: Wall Breakout

```text
Wall consumed
Price lifts through
Liquidity beyond thin
```

---

## Module 3: Absorption Failure

```text
Extreme aggression
Minimal price progress
Reloading liquidity
Trapped traders
```

---

## Module 4: Stop-Run Fade

```text
Prior level break
Aggressive stop trigger
Failure back inside
```

---

## Module 5: Pulling / Stacking

```text
Liquidity pulls ahead
Liquidity stacks behind
Directional pressure
```

---

## Module 6: Liquidity Vacuum Continuation

```text
Thin book
Fast execution
Opposite liquidity disappears
```

---

## Module 7: Iceberg / Reload Detection

```text
Repeated fills
Visible wall refresh
Hidden liquidity
```

---

## Module 8: OR / Session Liquidity Interaction

```text
OR high/low
VWAP
Prior high/low
Wall interaction
```

---

# PHASE 4 — Convergence Engine

## Table:

```text
CG_mnq_convergence_features_100ms
CG_mnq_bm_convergence_scores_100ms
```

---

## Scoring Logic:

### Bullish:

```text
Execution momentum
Liquidity vacuum
Wall support
Wall vacuum
Net liquidity
Absorption
OR alignment
```

### Bearish:

```text
Inverse of above
```

---

## Trade trigger:

```text
LONG:
bull_score >= threshold
AND bull_score > bear_score

SHORT:
bear_score >= threshold
AND bear_score > bull_score
```

---

# Threshold tiers

```text
2 = exploratory
3 = balanced
4 = elite
5+ = institutional sniper
```

---

# PHASE 5 — Multi-Timeframe Expansion

## Scalping:

```text
100ms–1s
```

## Medium:

```text
5s–1m
```

## Whale:

```text
5m–session
```

---

## MTF tables:

```text
CG_mnq_convergence_mtf_features
CG_mnq_convergence_mtf_regimes
```

---

# PHASE 6 — Correct Exit Engine

## Mandatory:

```text
No nullable DateTime errors
True first target/stop
Time-based exits
Correct overlap prevention
Single-position enforcement
Multi-position scaling later
```

---

# PHASE 7 — Risk Governance

---

## Required controls:

### Global:

```text
Max account drawdown
Max rolling 10-trade drawdown
Max loss streak
```

### Daily:

```text
Daily drawdown
Profit lock
Session regime filters
```

---

# PHASE 8 — Capital Scaling Model

When equity grows:

```text
Risk per trade = fixed % capital
```

---

## Example:

```text
<$25K: 1 contract
$25K–50K: 2 contracts
$50K–100K: 3–5 contracts
>$100K: dynamic risk tiering
```

---

# PHASE 9 — NT8 / ACE Deployment

Only after:

```text
Corrected CH backtests
CSV exports
Market Replay validation
Slippage stress
Latency testing
OCO++ protection
```

---

# Immediate Next SQL Steps

---

## Step 1:

```text
Build CG_mnq_convergence_features_100ms
```

---

## Step 2:

```text
Build first convergence score table
```

---

## Step 3:

Test:

```text
Wall rejection
Wall breakout
Absorption
```

---

## Step 4:

Export CSVs for all major strategy variants.

---

# CSV Export Policy

For all large strategy outputs:

```text
Always export CSV files
Never rely solely on terminal row dumps
```

---

# Strategic Goals

## Near-term:

```text
Recover corrected profitable edge
```

---

## Mid-term:

```text
Build modular multi-edge system
```

---

## Long-term:

```text
Institutional-grade adaptive microstructure engine
```

---

# Final Strategic Doctrine

```text
ClanMarshal v9 is no longer a single strategy.

It is a diversified order-flow / heatmap intelligence platform
designed to discover, validate, and combine multiple independent
microstructure edges across timeframes.
```

---

# Immediate Priority

```text
Build convergence feature table
→ score modules
→ corrected backtests
→ CSV exports
→ stress tests
```

