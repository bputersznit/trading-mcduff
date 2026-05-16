# BM_MNQ Frame Repair v1.4 Instructions

Generated: 2026-05-10 13:40:00 America/New_York

## Why this repair is needed

The v1.3 build completed successfully, but the frame-source QA showed that the frame tables had:

```text
total_exec_size = 0
```

while the aggression rollup tables correctly had:

```text
total_exec_size = 36,614,148
buy_exec_size   = 18,368,163
sell_exec_size  = 18,245,985
```

That means the frame tables lost the aggression bubbles.

The cause is the frame-source join: v1.3 used heatmap rows as the left side and joined aggression by exact time/price. Aggression rows with no matching heatmap row disappeared.

## What v1.4 does

v1.4 rebuilds only these tables:

```text
BM_MNQ_FRAME_SOURCE_1S
BM_MNQ_FRAME_SOURCE_5S
BM_MNQ_FRAME_SOURCE_30S
BM_MNQ_FRAME_SOURCE_1M
BM_MNQ_FRAME_SOURCE_5M
```

It does not rebuild the already-successful aggression and heatmap rollups.

The new frame build creates a unified keyset:

```text
heatmap keys UNION DISTINCT aggression keys
```

Then it joins both domains into the same frame table. This preserves:

1. heatmap-only rows,
2. aggression-only rows,
3. rows where heatmap and aggression overlap.

It also clamps `heatmap_intensity` to `<= 1`.

## Run commands

Put these files in your v1.3 folder or a new v1.4 folder:

```text
BM_MNQ_03_rebuild_frame_sources_v1_4.sql
BM_MNQ_04_frame_repair_qa_v1_4.sql
BM_MNQ_run_frame_repair_v1_4.sh
```

Run:

```bash
chmod +x BM_MNQ_run_frame_repair_v1_4.sh
./BM_MNQ_run_frame_repair_v1_4.sh
```

## Expected QA

Each scale should show:

```text
total_exec_size = 36614148
max_heatmap_intensity <= 1
```

Some `aggression_only_rows` are expected and good. Those are the bubble rows that v1.3 was dropping.
