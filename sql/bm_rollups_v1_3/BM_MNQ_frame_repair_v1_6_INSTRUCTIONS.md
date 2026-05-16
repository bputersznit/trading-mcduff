# BM_MNQ Frame Repair v1.6 — Batched Memory-Safe Instructions

Generated: 2026-05-10 14:12:00 America/New_York

## What v1.6 fixes

v1.5 failed immediately because this ClickHouse 26.3 server does not support:

```sql
SET the unsupported external-join memory setting = ...
```

v1.6 removes that setting everywhere.

## Files

```text
BM_MNQ_03_create_empty_frame_sources_v1_6.sql
BM_MNQ_run_frame_repair_batched_v1_6.sh
BM_MNQ_05_frame_repair_qa_v1_6.sql
```

## Run

```bash
chmod +x BM_MNQ_run_frame_repair_batched_v1_6.sh
./BM_MNQ_run_frame_repair_batched_v1_6.sh
```

## Expected QA

Each scale should show:

```text
total_exec_size = 36614148
max_heatmap_intensity <= 1
```
