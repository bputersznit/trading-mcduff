# MNQ Intraday Strategy — Restart Doc v8.2

## Strategic Mission
Build and validate a robust MNQ intraday trading system that can survive real execution conditions before scaling capital.

Core doctrine:

```text
Survival first.
Execution integrity second.
Profit scale third.
```

Primary operating constraints:

- Instrument: MNQ futures
- Initial size: 1 MNQ contract
- No overlapping positions
- RTH focus
- NinjaTrader 8 implementation target
- Playback101 / Sim101 validation before any live deployment
- Chicago VPS target environment
- Broker-side protective orders mandatory
- No naked positions

---

# Versioning Convention
Restart documents now use:

```text
<major>.<minor>
```

Current document:

```text
v8.2
```

Minor versions track material tactical refinements. Major versions track core strategy/deployment doctrine shifts.

---

# Strategic Hierarchy

## T3 Cuileanan / Wolfhound Line
Purpose:

```text
Seed-capital preservation strategy
```

Role:

- Lower trade frequency
- Lower drawdown
- More selective
- Capital-defense oriented
- Candidate for first cautious deployment after validation

## T2 ClanMarshal / PlanMarshal Line
Purpose:

```text
Scaling and main-army growth strategy
```

Role:

- Higher trade frequency
- Higher total PnL potential
- More execution complexity
- Requires hardened NinjaScript execution engine before serious deployment testing

Doctrine:

```text
If T3 is safer, deploy T3 first.
If T2 later proves materially more lucrative with acceptable execution risk, transition or combine.
```

---

# Validated T2 Backtest State

The successful T2 / ClanMarshal backtest remains the main high-upside benchmark.

Important validated metrics from the recent strong T2 candidate:

| Metric | Value |
|---|---:|
| Trades | 908 |
| Total PnL | $71,429.40 |
| Expectancy | $78.67 |
| Profit Factor | 2.93 |
| Win Rate | 64.43% |
| Max Drawdown | -$637.70 |

Operational interpretation:

- Strong capital-growth candidate
- Better total opportunity than T3
- Requires faithful NT8 signal and execution translation
- Not yet implemented in NinjaScript as the true alpha model

Critical distinction:

```text
The current NinjaScript smoke-test files do NOT prove T2 alpha.
They only prove or debug order execution mechanics.
```

---

# Earlier T2 Execution Doctrine

Validated execution model:

```text
Signal fires
→ place LIMIT first
→ wait approximately 1 second
→ if filled, trade normally
→ if not filled:
    low flow → MARKET
    medium flow + momentum confirmation → MARKET
    high flow → SKIP
```

Core features associated with T2 research:

- total_event_size
- event_count_delta
- short momentum confirmation
- session regime filters
- opening-range / manipulation awareness
- conditional market fallback
- daily governors
- single-position enforcement

Current NinjaScript status:

```text
True T2 signal layer is not yet wired.
```

Required future build:

```text
CG_T2_ClanMarshal_Model_v1.cs
```

But this must use real NT-accessible features, not placeholder functions.

---

# Validated T3 Research State

## Raw T3
Initial raw T3 wall-defense candidate pool was too broad.

Issues discovered:

- Excessive signal count
- Overlap / cluster distortion
- Path contamination from bad 100ms path data
- Unrealistic early results

Major infrastructure correction:

```text
Rebuilt clean trade-print path from actual trade prints.
```

Clean path table:

```text
CG_mnq_trade_print_path_100ms_clean
```

Path sanity after cleaning:

| Metric | Value |
|---|---:|
| Rows | ~5.59M |
| Avg 100ms range | 0.2784 |
| Median range | 0 |
| P95 range | 1.00 |
| Max capped range | 10.00 |

This removed false target inflation and brought T3 back into physical realism.

---

# T3 Breeding Ladder

## Raw Costed T3
After clean path, slippage, and commission:

| Metric | Value |
|---|---:|
| Trades | 69,983 |
| Total PnL | $1,573,981.90 |
| Expectancy | $22.49 |
| Profit Factor | 1.469 |
| Win Rate | 47.16% |

Assessment:

```text
Real edge exists, but raw form is not deployable.
```

---

## Wolfhound Tier
P75-ish filter:

```text
wall_score >= 3068
fill_through_wall >= 18516
```

Single-position realistic result:

| Metric | Value |
|---|---:|
| Trades | 3,423 |
| Total PnL | $126,093.90 |
| Expectancy | $36.84 |
| Profit Factor | 1.867 |
| Win Rate | 53.14% |
| Max Drawdown | -$3,164.80 |

Assessment:

```text
Improved, but not capital-preservation superior.
```

---

## Dire Wolf Tier
P90-ish filter:

```text
wall_score >= 4023
fill_through_wall >= 24140
```

Candidate diagnostics:

| Side | Candidates | Avg Wall Score | Avg Fill |
|---|---:|---:|---:|
| LONG | 1,246 | ~5,163 | ~30,932 |
| SHORT | 1,259 | ~5,142 | ~30,897 |

Single-position result:

| Metric | Value |
|---|---:|
| Trades | 836 |
| Total PnL | $34,334.80 |
| Expectancy | $41.07 |
| Profit Factor | 2.004 |
| Win Rate | 54.90% |
| Max Drawdown | -$1,636.70 |

Assessment:

```text
True structural improvement. Still not safer than T2 on DD.
```

---

# Session Filtering Discovery

Dire Wolf session breakdown showed strong regime differences.

Best regimes:

- POST_OPEN
- LUNCH
- CLOSE_30

Weaker / noisier regimes:

- POWER
- MIDDAY

POWER explanation:

```text
POWER is the broad active institutional battlefield. It has more opportunity but more noise, more false walls, and lower capital efficiency.
```

---

# Cuileanan v3 — Elite Session-Filtered Dire Wolf

Session whitelist:

```text
POST_OPEN
LUNCH
CLOSE_30
```

Initial result before dedup:

| Metric | Value |
|---|---:|
| Trades | 150 |
| Total PnL | $10,155 |
| Expectancy | $67.70 |
| Profit Factor | 3.195 |
| Win Rate | 66.00% |
| Max Drawdown | -$1,791.60 |

Issue discovered:

```text
Duplicate clustered entries remained at identical timestamps.
```

Fix:

```text
Deduplicate by trade_date + signal_time, keeping strongest wall_score / fill_through_wall candidate.
```

---

# Cuileanan v3 Dedup — Current Best T3 Seed-Capital Candidate

Final deduplicated metrics:

| Metric | Value |
|---|---:|
| Trades | 62 |
| Total PnL | $3,976.60 |
| Expectancy | $64.14 |
| Profit Factor | 2.993 |
| Win Rate | 64.52% |
| Max Drawdown | -$362.80 |

Daily structure:

- Worst day: -$122.80
- Many losing days were single-small-loss days
- Best days reached roughly +$597, +$746, +$895

Strategic meaning:

```text
T3 Cuileanan Dedup is low-frequency but capital-preservation shaped.
```

Comparison:

| Strategy | PF | Expectancy | DD | Trades |
|---|---:|---:|---:|---:|
| T2 ClanMarshal | 2.93 | $78.67 | -$637.70 | 908 |
| T3 Cuileanan Dedup | 2.99 | $64.14 | -$362.80 | 62 |

Interpretation:

```text
T3 Dedup is safer and smaller.
T2 is larger and more scalable.
```

---

# Controlled Expansion Test — Cuileanan v3.1

Expansion attempted by relaxing from P90 to P75/P85-like thresholds.

Result:

| Metric | v3 Dedup | v3.1 Expansion |
|---|---:|---:|
| Trades | 62 | 63 |
| Total PnL | $3,976.60 | $3,405.90 |
| Expectancy | $64.14 | $54.06 |
| PF | 2.993 | 2.502 |
| Win Rate | 64.52% | 60.32% |
| Max DD | -$362.80 | -$427.00 |

Conclusion:

```text
Lowering thresholds added almost no useful frequency and degraded quality.
Revert to Cuileanan v3 Dedup as current best T3 seed-capital candidate.
```

---

# NinjaScript / NT8 Workstream

## Important Distinction
Current NT8 files are execution smoke tests.

They are not the real T2 alpha.

They prove or diagnose:

- Strategy compiles
- Strategy receives Playback data
- Strategy can submit entries
- Strategy can submit protective exits
- Output window diagnostics work
- Orders interact with Playback101
- Stop/target behavior under Playback conditions

They do not prove:

- T2 signal validity
- T3 signal validity
- Backtest reproduction
- Live profitability
- Feature parity with ClickHouse

---

# Smoke-Test Evolution

## `CG_PlanMarshal_SmokeTest.cs`
Initial deterministic smoke test.

Problem:

```text
Too aggressive; produced rapid repeated entries.
```

## `CG_PlanMarshal_SmokeTest_v2.cs`
Added:

- Better throttling
- Explicit bracket submission
- Stop sanity checks
- Output diagnostics

## `CG_PlanMarshal_SmokeTest_v3.cs`
Changed trigger to tick-based:

```text
EntryEveryNTicks = 1
ExpectedAccountName = Playback101
```

Important note:

```text
NinjaScript cannot reliably force the account internally.
The account must be selected manually in the strategy UI.
```

## `CG_PlanMarshal_SmokeTest_v4.cs`
Added:

- `OneTradePerSession = true` default
- Emergency flatten if intended protective stop is invalid
- `EMERGENCY_FLAT` diagnostics

---

# Playback Smoke-Test Findings

Recent playback export showed that the smoke-test engine is alive.

Evidence:

- `PM2_Short` entries occurred on Playback101
- `PM4_Short_Stop`, `PM4_Short_Target`, and `PM4_Short_EmergencyFlat` exits occurred
- Playback account: Playback101
- Connection: Playback

Recent small sample summary:

| Date | Entry | Exit | Result |
|---|---:|---:|---:|
| 2026-03-03 | 24618.00 short | 24629.25 stop | -11.25 pts |
| 2026-03-04 | 25283.75 short | 25275.75 target | +8.00 pts |
| 2026-03-05 | 25248.75 short | 25263.75 stop | -15.00 pts |
| 2026-03-06 | 25040.50 short | 25045.25 emergency flat | -4.75 pts |

Approx result:

```text
4 completed trades
1 win
3 losses
~25% win rate
~ -23.00 points
~ -$46 MNQ estimate at $2/point
```

Interpretation:

```text
This result is irrelevant to alpha quality because the signal is forced/synthetic.
It is useful only for execution and bracket diagnostics.
```

---

# Current NT8 Issue

Even with emergency flatten logic, the smoke-test work revealed a real execution engineering risk:

```text
In fast playback transitions, intended protective stop prices may already be invalid by the time the entry-fill callback arrives.
```

Production implications:

- Must avoid naked positions
- Must validate protective orders immediately after fill
- If bracket cannot be safely placed, flatten instantly
- Add cooldown after emergency flatten
- Do not rely on local-only trailing protection
- Broker-side stop/target protection remains mandatory

---

# OCO++ Doctrine

Mandatory for production:

```text
Entry fills
→ immediately place broker-side stop-loss and profit target
→ OCO-linked where supported
→ local strategy may only tighten protection
→ if protection cannot be placed, flatten immediately
```

Forbidden:

```text
Naked positions
Local-only stop logic
Removing stop before replacement is confirmed
Unvalidated trailing simulation
```

Trailing stop note:

Earlier trailing backtests were invalid due to path leakage and unrealistic first-hit enforcement. Trailing remains discarded for now unless revalidated under strict path rules.

---

# Current Tactical Plan

## Step 1 — Finish NT8 Execution Harness
Required behavior:

- One active position only
- Clean Playback101 account selection warning
- Reliable entry submission
- Reliable stop/target placement
- Immediate flatten if protective stop invalid
- Rich Output window diagnostics
- Optional CSV audit later

## Step 2 — Build Real T2 Signal Inputs
Need NT-accessible equivalents for:

- total_event_size
- event_count_delta
- short momentum
- flow gating
- regime filters
- opening range context

Until those are wired, T2 NS code is not the real model.

## Step 3 — Build Real T2 PlanMarshal
Production candidate should be:

```text
CG_T2_ClanMarshal_Model_v1.cs
```

But only after replacing simulated placeholders with actual data features.

## Step 4 — Compare Live-Realistic T2 vs T3
Comparison must include:

- PnL
- Expectancy
- PF
- DD
- worst day
- slippage sensitivity
- execution failures
- order rejection behavior
- operational complexity

---

# Current Strategic Decision

As of v8.2:

```text
T3 Cuileanan Dedup remains the safer seed-capital strategy candidate.
T2 ClanMarshal remains the larger-scale growth candidate but requires true NT8 implementation.
```

Battle doctrine:

```text
T3 guards the treasury.
T2 trains for conquest.
```

---

# Immediate Next Action

Generate or stabilize a full file:

```text
CG_T2_ClanMarshal_ForcedSmoke_v1.cs
```

Purpose:

- Forced market-entry smoke test
- Managed bracket templates
- Playback101 warning
- Short 09:45–09:46 test window
- One trade per session default
- Output diagnostics

This is still not alpha. It exists to prove mechanical trading and protection with fewer moving parts before reintroducing conditional limit/fallback logic.

---

# Core Philosophy

```text
Do not confuse a working order engine with a working edge.
Do not confuse a backtest edge with a deployable execution system.
Do not deploy before protection works.
```

McDuff doctrine:

```text
First: keep the clan alive.
Second: prove the weapon fires safely.
Third: teach it where to strike.
Fourth: scale only after survival is routine.
```

