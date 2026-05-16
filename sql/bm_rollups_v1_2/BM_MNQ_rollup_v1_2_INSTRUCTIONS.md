# BM_MNQ Rollup Build v1.2 — Run Instructions

Generated: 2026-05-10 13:05:00 America/New_York

## Why this package exists

You hit this ClickHouse 26.3 error:

```text
ILLEGAL_AGGREGATION:
Aggregate function max(heatmap_proxy_value) AS heatmap_proxy_value
is found inside another aggregate function
```

The old SQL block directly projected:

```sql
max(heatmap_proxy_value) AS heatmap_proxy_value
```

The corrected v1.2 files do **not** do that. They use a safe two-stage pattern:

```sql
max(src.heatmap_proxy_value) AS agg_heatmap_proxy_value
```

inside a grouped CTE, then:

```sql
agg_heatmap_proxy_value AS heatmap_proxy_value
```

in the outer SELECT.

## Files, in order

Run these in this exact order:

1. `BM_MNQ_00_preflight_v1_2.sql`
2. `BM_MNQ_01_rollup_scales_v1_2.sql`
3. `BM_MNQ_02_postrun_qa_v1_2.sql`

Optional convenience script:

4. `BM_MNQ_run_rollups_v1_2.sh`

## Recommended shell commands

Put all four files in one directory, for example:

```bash
mkdir -p ~/trading4/CG_MNQ_MarketReplayLab/sql/bm_rollups_v1_2
cd ~/trading4/CG_MNQ_MarketReplayLab/sql/bm_rollups_v1_2
```

Before running, verify you are not using the stale file:

```bash
grep -n "max(heatmap_proxy_value) AS heatmap_proxy_value" BM_MNQ_01_rollup_scales_v1_2.sql
```

Expected result:

```text
# no output
```

Then run either the wrapper:

```bash
chmod +x BM_MNQ_run_rollups_v1_2.sh
./BM_MNQ_run_rollups_v1_2.sh
```

Or run manually:

```bash
clickhouse-client --multiquery < BM_MNQ_00_preflight_v1_2.sql
clickhouse-client --multiquery < BM_MNQ_01_rollup_scales_v1_2.sql
clickhouse-client --multiquery < BM_MNQ_02_postrun_qa_v1_2.sql
```

## What this builds

Aggression:

```text
BM_MNQ_AGGRESSION_EXECUTIONS_1S
BM_MNQ_AGGRESSION_EXECUTIONS_5S
BM_MNQ_AGGRESSION_EXECUTIONS_30S
BM_MNQ_AGGRESSION_EXECUTIONS_1M
BM_MNQ_AGGRESSION_EXECUTIONS_5M
```

Heatmap / liquidity-event proxy:

```text
BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S
BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M
BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M
```

Frame sources:

```text
BM_MNQ_FRAME_SOURCE_1S
BM_MNQ_FRAME_SOURCE_5S
BM_MNQ_FRAME_SOURCE_30S
BM_MNQ_FRAME_SOURCE_1M
BM_MNQ_FRAME_SOURCE_5M
```

QA:

```text
BM_MNQ_QA_SCALE_SUMMARY
```

## Important reminder

This build still uses the current `BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS` table as a **liquidity-event proxy**, not a fully reconstructed resting order book. The true Bookmap-grade heatmap comes later from order-id lifecycle reconstruction.
