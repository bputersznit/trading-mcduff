# BM_MNQ Rollup Build v1.3 — Run Instructions

Generated: 2026-05-10 13:18:00 America/New_York

## What v1.3 fixes

v1.2 got past the stale heatmap-file confusion, but the preflight failed with another ClickHouse 26.3 alias-resolution trap:

```text
Aggregate function sum(total_exec_size) AS total_exec_size is found inside another aggregate function
```

This was only in the preflight QA query. v1.3 fixes **preflight**, **rollup**, and **post-run QA** by using the same safe pattern everywhere:

1. Inner CTE computes aggregates with `agg_*` aliases.
2. Outer SELECT projects final public column names.
3. No same-SELECT aggregate alias is reused in another expression.

## Files, in order

Use these files only:

1. `BM_MNQ_00_preflight_v1_3.sql`
2. `BM_MNQ_01_rollup_scales_v1_3.sql`
3. `BM_MNQ_02_postrun_qa_v1_3.sql`
4. Optional wrapper: `BM_MNQ_run_rollups_v1_3.sh`

Ignore v1, v1_1, and v1_2.

## Recommended commands

```bash
mkdir -p ~/trading4/CG_MNQ_MarketReplayLab/sql/bm_rollups_v1_3
cd ~/trading4/CG_MNQ_MarketReplayLab/sql/bm_rollups_v1_3
```

Move the v1.3 files into that directory.

Check for stale bad patterns:

```bash
grep -n "max(heatmap_proxy_value) AS heatmap_proxy_value" BM_MNQ_01_rollup_scales_v1_3.sql
grep -n "sum(total_exec_size) AS total_exec_size" BM_MNQ_00_preflight_v1_3.sql
```

Expected result: no output from either command.

Then run:

```bash
chmod +x BM_MNQ_run_rollups_v1_3.sh
./BM_MNQ_run_rollups_v1_3.sh
```

Or manually:

```bash
clickhouse-client --multiquery < BM_MNQ_00_preflight_v1_3.sql
clickhouse-client --multiquery < BM_MNQ_01_rollup_scales_v1_3.sql
clickhouse-client --multiquery < BM_MNQ_02_postrun_qa_v1_3.sql
```

## Outputs built

```text
BM_MNQ_AGGRESSION_EXECUTIONS_1S / 5S / 30S / 1M / 5M
BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S / 5S / 30S / 1M / 5M
BM_MNQ_FRAME_SOURCE_1S / 5S / 30S / 1M / 5M
BM_MNQ_QA_SCALE_SUMMARY
```
