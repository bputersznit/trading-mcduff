# CG NT8 Automation AddOn Master Plan v1.0

## Mission

Transform NinjaTrader 8 from a manually operated retail GUI into a semi-automated research, deployment, playback, and eventually live-execution node.

---

# Strategic Objective

## Replace:

```text
Manual:
- Copy files
- Compile strategies
- Load configs
- Set parameters
- Select Playback101
- Start playback
- Export results
- Repeat endlessly
```

---

## With:

```text
Machine-driven pipeline:
Ubuntu Research Stack
→ VPS deployment inbox
→ NT8 AddOn controller
→ Strategy compile
→ Parameter injection
→ Playback/live execution
→ Telemetry export
→ Research feedback loop
```

---

# Development Philosophy

## Priority order:

### Phase 1:
```text
Filesystem reliability
```

### Phase 2:
```text
Compile + deployment orchestration
```

### Phase 3:
```text
Playback + strategy parameter control
```

### Phase 4:
```text
Telemetry + result harvesting
```

### Phase 5:
```text
Named-pipe / socket command API
```

### Phase 6:
```text
Full institutional deployment layer
```

---

# Architecture Overview

```text
Ubuntu Laptop / ClickHouse / Python
    ↓
Generate strategy .cs + config JSON
    ↓
rclone / SCP push
    ↓
Windows VPS Deployment Inbox
    ↓
CG_NT8_AutoDeploy_AddOn
    ↓
Install + backup + archive
    ↓
Compile controller
    ↓
Strategy loader
    ↓
Playback / Live execution
    ↓
Telemetry + trade logs
    ↓
Results returned to Ubuntu research DB
```

---

# Core AddOn Components

---

# Module A — Inbox Watcher

## Responsibilities:
- Watch deployment folder
- Detect new `.cs` files
- Detect new `.json` configs
- Backup previous versions
- Install into NT folders
- Archive deployments
- Trigger compile request
- Write deployment logs

---

## Status:
```text
MVP operational
```

---

# Module B — Compile Controller

## Responsibilities:
- Trigger NinjaScript compile
- Detect compile success/failure
- Parse compile logs
- Report back to deployment system
- Block invalid deployment activation

---

## Challenge:
```text
NT8 compile is not fully headless
```

---

## Near-term solution:
```text
PowerShell + AutoHotkey hook
```

---

## Mid-term solution:
```text
Internal AddOn compile bridge
```

---

# Module C — Config Loader

## Responsibilities:
Load JSON deployment files:

```json
{
  "strategy_name": "CG_MNQ_RTH_Reclaim_Bagger_v1_1",
  "instrument": "MNQ JUN26",
  "account": "Playback101",
  "chart_period": "2 Minute",
  "parameters": {
    "StopTicks": 32,
    "TargetTicks": 160
  }
}
```

---

### Apply:
- Strategy parameters
- Account selection
- Instrument
- Quantity
- Session template
- Playback mode

---

# Module D — Strategy Deployment Controller

## Responsibilities:
- Enable/disable strategies
- Load chart/workspace templates
- Attach strategies to chart
- Set Playback101 / Sim / Live account
- Handle safe startup sequencing
- OCO++ governance validation

---

# Module E — Playback Controller

## Responsibilities:
- Select playback date
- Set playback speed
- Start/stop playback
- Reset sessions
- Batch-run multiple days

---

## Strategic value:
```text
Mass automated playback validation
```

---

# Module F — Telemetry Exporter

## Responsibilities:
- Export executions
- Export fills
- Export strategy logs
- Export custom metrics
- Save:
  - CSV
  - JSON
  - SQLite / flat files

---

## Output:
```text
Ubuntu research loop can automatically analyze:
- PnL
- drawdown
- trade quality
- regime performance
```

---

# Module G — Command Bridge (Advanced)

## Long-term:

```text
Named Pipes
or
TCP localhost API
or
REST wrapper
```

---

## Enables:
```text
Python:
run_deployment(config)
```

---

## Example:

```python
nt.deploy("CG_MNQ_RTH_Reclaim_Bagger_v1_1", config)
```

---

# Folder Structure

```text
C:\CG_NT8_AutoDeploy\
    inbox\
    installed\
    archive\
    logs\
    config\
    hooks\
    telemetry\
```

---

# Recommended File Naming Convention

```text
CG_<strategy>_vX_Y.cs
CG_<deployment>_config.json
CG_<results>_YYYYMMDD.csv
```

---

# Operational Workflow

## Daily:

```text
1. Research on Ubuntu
2. Generate strategy/config
3. Push to VPS inbox
4. AddOn deploys
5. Compile
6. Playback batch
7. Export results
8. Analyze in ClickHouse/Python
9. Iterate
```

---

# Immediate Implementation Ladder

---

## v0.1 (Current MVP)
### Includes:
- File watcher
- Deployment inbox
- Backup
- Archive
- Compile flagging
- PowerShell
- AutoHotkey scaffolding

---

## v0.2
### Build next:
- Config parser
- Strategy parameter injection
- Deployment status UI
- Compile result parsing

---

## v0.3
### Build:
- Playback automation
- Batch replay loops
- Automated strategy enable
- Telemetry export

---

## v1.0
### Institutional-grade:
- Full command bridge
- Linux orchestration
- Mass strategy sweeps
- Portfolio deployment
- Live-capable controls

---

# Risk Controls

## Must enforce:
- Strategy file validation
- Compile success validation
- Account targeting confirmation
- OCO++ safety checks
- Max position limits
- Deployment rollback on failure

---

# Strategic Benefits

## Eliminates:
```text
Human repetitive operational burden
```

---

## Enables:
```text
Rapid strategy iteration
Systematic testing
Research scaling
Institutional process discipline
```

---

# McDuff Strategic Verdict

```text
The AddOn is not just convenience software.

It is the control spine for your entire trading R&D ecosystem.
```

---

# Core Doctrine

## Goal:

```text
Stop acting like a retail operator.

Start acting like a systems architect.
```

---

# Bottom Line

This AddOn framework is how you transition from:

```text
Manual maze-running
```

To:

```text
Machine-directed strategy engineering.
```

---

# Final Directive

## Build order:

```text
Filesystem reliability
→ Compile automation
→ Config deployment
→ Playback automation
→ Telemetry
→ Command bridge
→ Full deployment platform
```

This path minimizes chaos while maximizing strategic leverage.

