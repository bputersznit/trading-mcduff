# BM_MNQ Frame Repair v1.5 — Batched Memory-Safe Instructions

Generated: 2026-05-10 14:00:00 America/New_York

## Why v1.5 exists

v1.4 failed with:

```text
MEMORY_LIMIT_EXCEEDED
```

The cause was the global `UNION DISTINCT` keyset over the 1S heatmap and aggression tables.

v1.5 avoids that large global keyset. It rebuilds frame sources in small batches:

1. create empty frame-source tables,
2. insert heatmap-led rows by `trade_date`,
3. insert aggression-only rows by `trade_date`,
4. run QA.

## Files

Use these files:

```text
BM_MNQ_03_create_empty_frame_sources_v1_5.sql
BM_MNQ_run_frame_repair_batched_v1_5.sh
BM_MNQ_05_frame_repair_qa_v1_5.sql
```

## Run

Place the files in your current rollup folder, then run:

```bash
chmod +x BM_MNQ_run_frame_repair_batched_v1_5.sh
./BM_MNQ_run_frame_repair_batched_v1_5.sh
```

## Expected QA

Each scale should show:

```text
total_exec_size = 36614148
max_heatmap_intensity <= 1
```

`aggression_only_rows` should be greater than zero. Those are the bubble rows that v1.3 dropped.
