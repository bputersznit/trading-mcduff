# CG NT8 Import / Playback Smoke Test Checklist

## Contract Mapping

Use:

```text
CH/Data symbol: MNQZ5
NT8 instrument: MNQ 12-25
Date range: Sep/Oct 2025
```

Do **not** use `MNQ JUN26` for this dataset.

## First Import Test

Use one day first:

```text
2025-09-23 RTH
MNQ 12-25
Trade ticks only
```

## NT8 Strategy Settings for First Playback

Recommended first run:

```text
EnableTelemetry = false
PrintDiagnostics = false
EventLookbackBars = 50 to 100
T3 wall confirmation = bypass/disabled if no L2 depth
Playback speed = 1x to 5x
One session only
```

## Failure Modes

### Empty chart / no trades
Likely causes:

```text
Wrong instrument expiry
Wrong date range
Wrong timestamp timezone
CSV import format mismatch
```

### Strategy appears dead
Likely causes:

```text
T3 wall confirmation enabled without depth
ORB state never transitions
Min thresholds too strict
Playback data lacks required bid/ask/depth information
```

### Playback chokes
Likely causes:

```text
Too many raw CSV rows
High playback speed
Telemetry flushing
Print diagnostics
OnEachTick + T2 loop
L2/depth processing overload
```

## Validation Steps

1. Import one small CSV.
2. Open chart for `MNQ 12-25` on that date.
3. Confirm ticks render.
4. Run strategy with telemetry off.
5. Confirm ORB builds 09:30-09:45 ET.
6. Confirm state transitions after 09:45.
7. Confirm T2/T3 reject counters are plausible.
8. Only then import more days.

