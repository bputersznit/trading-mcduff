# BM_MNQ Frame Repair v1.7 — Append Aggression Bubbles

Generated: 2026-05-10 14:28:00 America/New_York

## Why v1.7 exists

v1.6 completed, but QA showed the frame tables are still heatmap-only:

```text
total_exec_size = 0
aggression_rows = 0
joined_rows = 0
```

The heatmap intensity repair worked:

```text
max_heatmap_intensity = 1
```

So we do not need to rebuild the heatmap rows again.

## What v1.7 does

v1.7 appends all aggression rollup rows as explicit bubble rows into:

```text
BM_MNQ_FRAME_SOURCE_1S
BM_MNQ_FRAME_SOURCE_5S
BM_MNQ_FRAME_SOURCE_30S
BM_MNQ_FRAME_SOURCE_1M
BM_MNQ_FRAME_SOURCE_5M
```

It leaves the existing heatmap rows in place.

## Run

```bash
chmod +x BM_MNQ_run_append_aggression_bubbles_v1_7.sh
./BM_MNQ_run_append_aggression_bubbles_v1_7.sh
```

## Expected QA

Each scale should show:

```text
total_exec_size = 36614148
max_heatmap_intensity <= 1
aggression_rows > 0
```
