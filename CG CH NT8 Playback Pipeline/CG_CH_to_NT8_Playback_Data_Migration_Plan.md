# CG ClickHouse → NT8 VPS Playback Data Migration Plan

## Purpose

Move MNQ historical data from local Linux ClickHouse into a Windows VPS environment for NinjaTrader 8 playback, historical import, and strategy validation.

Primary path:

```text
Local Linux ClickHouse
→ compressed Parquet/ZSTD staging export
→ rclone transfer
→ Windows VPS staging folder
→ VPS-side conversion to NT8 import CSV only when needed
→ NT8 Historical Data import / Playback validation
```

---

# 1. Strategic Bottom Line

ClickHouse should remain the master research warehouse.

NinjaTrader should receive curated export slices, not the entire raw database.

Recommended doctrine:

```text
ClickHouse = source of truth / transform engine
Parquet/ZSTD = compressed logistics/staging layer
CSV = final NT8 import artifact only
rclone = transport backbone
NT8 VPS = playback / execution test environment
```

Do not try to move the whole ClickHouse database to the VPS unless there is a strong reason. Export only the contracts, dates, sessions, and fields needed for NT8 playback or historical import.

---

# 2. Recommended Directory Layout

## Local Linux

```bash
/home/bernard/nt_exports/
/home/bernard/nt_exports/parquet/
/home/bernard/nt_exports/csv_final/
/home/bernard/nt_exports/zipped/
/home/bernard/nt_exports/logs/
/home/bernard/nt_exports/archive/
```

## Windows VPS

```text
C:\NT_Imports\
C:\NT_Imports\incoming\
C:\NT_Imports\parquet\
C:\NT_Imports\csv_final\
C:\NT_Imports\unzipped\
C:\NT_Imports\imported\
C:\NT_Imports\logs\
```

## Cloud / rclone remote

Example Google Drive path:

```text
gdrive:bento/nt_exports/
```

or direct SFTP path:

```text
vps:C:/NT_Imports/parquet/
```

---

# 3. Preferred Transfer Method

## Best simple route

```text
Linux → rclone → Google Drive / OneDrive → VPS rclone pull
```

## Best direct route

```text
Linux → rclone SFTP → Windows VPS folder
```

Direct SFTP avoids cloud storage limits and extra manual steps, but Google Drive/OneDrive can be easier to configure initially.

---

# 4. Data Scope Doctrine

Start small.

Initial smoke-test export:

```text
1 instrument
1 contract
1 RTH session
trades/ticks only
no depth yet
```

Recommended first target:

```text
MNQ JUN26 or the exact NT8 contract being tested
One known playback day
RTH: 09:30–16:00 ET
```

Only after this works should you export multi-day batches.

---

# 5. Compression Doctrine: Parquet First, CSV Last

Raw CSV should not be the primary transport format. It is too large, slow to copy, and brittle for very large MNQ tick/MBO exports.

Preferred doctrine:

```text
ClickHouse native table
→ Parquet with ZSTD compression
→ rclone transfer
→ VPS-side conversion to NT8 CSV
→ NT8 import
```

Why:

```text
Parquet/ZSTD is much smaller than CSV
Parquet preserves typed columns
Parquet is chunkable by session/day
Parquet supports fast validation in Python/PowerShell pipelines
CSV is only needed because NT8 import expects text-oriented files
```

Rule:

```text
Never transfer raw giant CSV unless it is a one-day smoke test.
For multi-day or tick-heavy sessions, transfer Parquet/ZSTD.
```

---

# 6. Preferred Chunking Scheme

Do not create one titanic file. Create one file per instrument/session/day.

Recommended file naming:

```text
MNQ_YYYYMMDD_RTH_ticks.parquet.zst-equivalent via Parquet ZSTD
MNQ_YYYYMMDD_ETH_ticks.parquet.zst-equivalent via Parquet ZSTD
MNQ_YYYYMMDD_RTH_depth_l1.parquet
MNQ_YYYYMMDD_RTH_mbo_raw.parquet
```

Recommended partitioning:

```text
1 instrument
1 contract
1 session
1 date
1 data type
```

Examples:

```text
MNQ_20260304_RTH_ticks.parquet
MNQ_20260304_RTH_ticks_nt8.csv
MNQ_20260304_RTH_ticks_nt8.csv.gz
```

Keep a manifest beside every batch:

```text
manifest.csv
```

Manifest columns:

```text
file_name,date,instrument,contract,session,row_count,min_ts,max_ts,min_price,max_price,format,compressed_size_bytes,sha256
```

---

# 7. ClickHouse Export Path: Tick Data First

## Goal

Create a compressed tick/trade Parquet staging file, then convert it to NT8 CSV only at the final import step.

## Example ClickHouse export shell command

Adjust symbol/date/table names as needed.

```bash
mkdir -p /home/bernard/nt_exports/parquet

clickhouse-client --query "
SELECT
    ts_event,
    toTimeZone(ts_event, 'America/New_York') AS ts_et,
    price,
    toUInt64(size) AS volume,
    side,
    sequence,
    symbol
FROM default.mnq_trades
WHERE ts_event >= toDateTime64('2026-03-04 14:30:00', 9, 'UTC')
  AND ts_event <  toDateTime64('2026-03-04 21:00:00', 9, 'UTC')
ORDER BY ts_event, sequence
FORMAT Parquet
SETTINGS output_format_parquet_compression_method = 'zstd'
" > /home/bernard/nt_exports/parquet/MNQ_20260304_RTH_ticks.parquet
```

Important: preserve `ts_event` and `sequence` in Parquet. Convert to NT8 timestamp text only at the final CSV stage.

---

# 8. Final CSV Generation for NT8

Generate CSV only after transfer, preferably on the VPS or in a final staging step. This keeps transport compact and lets you regenerate NT8-specific formats without re-querying ClickHouse.

Python conversion example:

```python
import pandas as pd
from pathlib import Path

src = Path(r"C:/NT_Imports/parquet/MNQ_20260304_RTH_ticks.parquet")
out = Path(r"C:/NT_Imports/csv_final/MNQ_20260304_RTH_ticks_nt8.csv")
out.parent.mkdir(parents=True, exist_ok=True)

df = pd.read_parquet(src)

# Confirm NT8 import format before bulk conversion.
# Common simple tick format: timestamp, price, volume.
df["nt_time"] = pd.to_datetime(df["ts_et"]).dt.strftime("%Y%m%d %H%M%S")

df[["nt_time", "price", "volume"]].to_csv(out, index=False, header=False)
print(out)
```

Optional final compression for archiving:

```powershell
gzip C:\NT_Imports\csv_final\MNQ_20260304_RTH_ticks_nt8.csv
```

Do not feed compressed files directly to NT8 unless the specific import workflow supports it. Usually, NT8 wants the final CSV/plain text file.

---

# 9. Optional CSV Compression for Small Smoke Tests

For one-day smoke tests, CSV plus gzip/zip is acceptable:

```bash
mkdir -p /home/bernard/nt_exports/zipped

gzip -k -f /home/bernard/nt_exports/csv_final/MNQ_20260304_RTH_ticks_nt8.csv
mv /home/bernard/nt_exports/csv_final/MNQ_20260304_RTH_ticks_nt8.csv.gz /home/bernard/nt_exports/zipped/
```

For large batches, prefer Parquet/ZSTD over zipped CSV.

---

# 10. rclone Setup

## Install on Linux

```bash
sudo apt update
sudo apt install -y rclone
```

or:

```bash
curl https://rclone.org/install.sh | sudo bash
```

## Configure remote

```bash
rclone config
```

Recommended remote names:

```text
gdrive
vps
onedrive
```

---

# 11. Transfer Examples

## Google Drive upload

```bash
rclone copy /home/bernard/nt_exports/parquet gdrive:bento/nt_exports/parquet/ \
  --progress \
  --transfers 4 \
  --checkers 8 \
  --log-file /home/bernard/nt_exports/logs/rclone_upload.log \
  --log-level INFO
```

## Direct SFTP to VPS

```bash
rclone copy /home/bernard/nt_exports/parquet vps:/C:/NT_Imports/parquet/ \
  --progress \
  --transfers 2 \
  --checkers 8 \
  --log-file /home/bernard/nt_exports/logs/rclone_vps_upload.log \
  --log-level INFO
```

## Verify remote files

```bash
rclone ls gdrive:bento/nt_exports/parquet/
```

or:

```bash
rclone ls vps:/C:/NT_Imports/parquet/
```

---

# 12. VPS Pull Workflow

If using Google Drive/OneDrive, install rclone on the Windows VPS and pull files down.

PowerShell example:

```powershell
mkdir C:\NT_Imports\incoming
mkdir C:\NT_Imports\unzipped
mkdir C:\NT_Imports\imported
mkdir C:\NT_Imports\logs

rclone copy gdrive:bento/nt_exports/parquet C:\NT_Imports\parquet --progress
```

No unzip is needed for Parquet. Convert Parquet to final NT8 CSV in `C:\NT_Imports\csv_final`.

---

# 13. NinjaTrader 8 Import

In NT8 on the VPS:

```text
Tools
→ Historical Data
→ Import
→ Select file
→ Match instrument and format
```

Critical checks:

```text
1. Instrument name matches the contract in NT8.
2. Timestamp timezone matches NT8 expectation.
3. File format matches NT8 import template.
4. Data type is tick/trade first, not depth.
5. Import one day first.
```

---

# 14. Playback Reality Check

NT8 Historical Data Import is not the same as native Market Replay `.nrd` generation.

Expected result from CSV tick import:

```text
Good for historical strategy testing and chart reconstruction.
May not produce full Market Replay behavior.
Will not reconstruct full L2 DOM unless depth-specific support exists.
```

Native NT8 playback `.nrd` files are proprietary and should not be the first engineering target.

---

# 15. L2 / MBO Depth Strategy

Do not attempt full MBO-to-NT replay first.

Phased approach:

```text
Phase A: Tick/trade CSV import
Phase B: Bid/ask enriched tick export if NT format supports it
Phase C: NT AddOn or bridge for synthetic depth playback
Phase D: Full ClickHouse-backed replay infrastructure
```

For the flagship strategy, remember:

```text
If NT playback lacks L2 depth,
T3 wall confirmation may reject everything.
```

Therefore, first playback tests should use:

```text
ORB + T2 mode
T3 disabled or bypassed
Telemetry off
Diagnostics off
1x–5x speed
one session only
```

---

# 16. Recommended Smoke Test Procedure

## Step 1 — Export one RTH day

```text
MNQ one session
Tick/trade data only
```

## Step 2 — Transfer to VPS

```text
rclone copy
verify size
verify row count if possible
```

## Step 3 — Import into NT8

```text
Historical Data import
load chart
confirm bars/ticks appear
```

## Step 4 — Run simple strategy

Use a very lightweight strategy first.

Confirm:

```text
Time alignment
Price alignment
Session boundaries
No huge gaps
No duplicate explosion
```

## Step 5 — Run flagship smoke test

Use conservative settings:

```text
EnableTelemetry = false
PrintDiagnostics = false
EventLookbackBars = 50
T3 disabled/bypassed if no depth
Playback speed = 1x to 5x
```

---

# 17. Validation Queries Before Export

## Row count

```sql
SELECT count()
FROM default.mnq_trades
WHERE ts_event >= toDateTime64('2026-03-04 14:30:00', 9, 'UTC')
  AND ts_event <  toDateTime64('2026-03-04 21:00:00', 9, 'UTC');
```

## Price sanity

```sql
SELECT
    min(price) AS min_price,
    max(price) AS max_price,
    min(ts_event) AS first_ts,
    max(ts_event) AS last_ts
FROM default.mnq_trades
WHERE ts_event >= toDateTime64('2026-03-04 14:30:00', 9, 'UTC')
  AND ts_event <  toDateTime64('2026-03-04 21:00:00', 9, 'UTC');
```

## Duplicate timestamp check

Duplicate timestamps are normal in tick data, but sequencing must be stable.

```sql
SELECT
    ts_event,
    count() AS rows
FROM default.mnq_trades
WHERE ts_event >= toDateTime64('2026-03-04 14:30:00', 9, 'UTC')
  AND ts_event <  toDateTime64('2026-03-04 21:00:00', 9, 'UTC')
GROUP BY ts_event
ORDER BY rows DESC
LIMIT 20;
```

---

# 18. Export Script Skeleton

Create:

```text
/home/bernard/nt_exports/CG_CH_To_NT8_Tick_Parquet_Export.sh
```

```bash
#!/usr/bin/env bash
set -euo pipefail

EXPORT_ROOT="/home/bernard/nt_exports"
PARQUET_DIR="$EXPORT_ROOT/parquet"
LOG_DIR="$EXPORT_ROOT/logs"
MANIFEST="$EXPORT_ROOT/manifest.csv"

mkdir -p "$PARQUET_DIR" "$LOG_DIR"

DAY="${1:-2026-03-04}"
OUT="$PARQUET_DIR/MNQ_${DAY//-/}_RTH_ticks.parquet"

# RTH ET 09:30-16:00 converted manually here as 14:30-21:00 UTC for standard EST dates.
# For DST, prefer parameterizing with Python/pytz or explicit UTC inputs.
START_UTC="${DAY} 14:30:00"
END_UTC="${DAY} 21:00:00"

echo "[EXPORT:PARQUET] $DAY → $OUT"

clickhouse-client --query "
SELECT
    ts_event,
    toTimeZone(ts_event, 'America/New_York') AS ts_et,
    price,
    toUInt64(size) AS volume,
    side,
    sequence,
    symbol
FROM default.mnq_trades
WHERE ts_event >= toDateTime64('$START_UTC', 9, 'UTC')
  AND ts_event <  toDateTime64('$END_UTC', 9, 'UTC')
ORDER BY ts_event, sequence
FORMAT Parquet
SETTINGS output_format_parquet_compression_method = 'zstd'
" > "$OUT"

ROWS=$(clickhouse-client --query "
SELECT count()
FROM default.mnq_trades
WHERE ts_event >= toDateTime64('$START_UTC', 9, 'UTC')
  AND ts_event <  toDateTime64('$END_UTC', 9, 'UTC')
")

SHA=$(sha256sum "$OUT" | awk '{print $1}')
SIZE=$(stat -c%s "$OUT")

if [ ! -f "$MANIFEST" ]; then
  echo "file_name,date,instrument,session,row_count,start_utc,end_utc,format,compressed_size_bytes,sha256" > "$MANIFEST"
fi

echo "$(basename "$OUT"),$DAY,MNQ,RTH,$ROWS,$START_UTC,$END_UTC,parquet_zstd,$SIZE,$SHA" >> "$MANIFEST"

echo "[DONE] rows=$ROWS size=$(du -h "$OUT" | awk '{print $1}') sha256=$SHA"
```

Make executable:

```bash
chmod +x /home/bernard/nt_exports/CG_CH_To_NT8_Tick_Parquet_Export.sh
```

Run:

```bash
/home/bernard/nt_exports/CG_CH_To_NT8_Tick_Parquet_Export.sh 2026-03-04
```

VPS-side conversion script:

```text
C:\NT_Imports\CG_Parquet_To_NT8_CSV.py
```

```python
from pathlib import Path
import sys
import pandas as pd

src = Path(sys.argv[1])
out_dir = Path(r"C:/NT_Imports/csv_final")
out_dir.mkdir(parents=True, exist_ok=True)
out = out_dir / src.name.replace(".parquet", "_nt8.csv")

df = pd.read_parquet(src)
df = df.sort_values(["ts_event", "sequence"], kind="mergesort") if "sequence" in df.columns else df.sort_values("ts_event")
df["nt_time"] = pd.to_datetime(df["ts_et"]).dt.strftime("%Y%m%d %H%M%S")
df[["nt_time", "price", "volume"]].to_csv(out, index=False, header=False)
print(f"[DONE] {out} rows={len(df):,}")
```

Run on VPS:

```powershell
python C:\NT_Imports\CG_Parquet_To_NT8_CSV.py C:\NT_Imports\parquet\MNQ_20260304_RTH_ticks.parquet
```
---

# 19. Upload Script Skeleton

Create:

```text
/home/bernard/nt_exports/CG_Rclone_NT8_Upload.sh
```

```bash
#!/usr/bin/env bash
set -euo pipefail

EXPORT_ROOT="/home/bernard/nt_exports"
PARQUET_DIR="$EXPORT_ROOT/parquet"
LOG_DIR="$EXPORT_ROOT/logs"
REMOTE="${1:-gdrive:bento/nt_exports/parquet}"

mkdir -p "$LOG_DIR"

echo "[RCLONE] Uploading $PARQUET_DIR → $REMOTE"

rclone copy "$PARQUET_DIR" "$REMOTE" \
  --progress \
  --transfers 4 \
  --checkers 8 \
  --log-file "$LOG_DIR/rclone_upload_$(date +%Y%m%d_%H%M%S).log" \
  --log-level INFO

echo "[VERIFY] Remote listing"
rclone ls "$REMOTE" | tail -20
```

Make executable:

```bash
chmod +x /home/bernard/nt_exports/CG_Rclone_NT8_Upload.sh
```

Run:

```bash
/home/bernard/nt_exports/CG_Rclone_NT8_Upload.sh gdrive:bento/nt_exports
```

---

# 20. Operational Checklist

Before export:

```text
[ ] Confirm ClickHouse server running
[ ] Confirm table name: default.mnq_trades or required CG_* table
[ ] Confirm contract/date range
[ ] Confirm timezone conversion
[ ] Confirm NT8 target instrument
```

Before transfer:

```text
[ ] Parquet exists
[ ] Manifest updated
[ ] Row count sanity checked
[ ] sha256 generated
[ ] rclone remote configured
```

On VPS:

```text
[ ] Parquet arrived
[ ] sha256 verified if needed
[ ] Converted to final NT8 CSV
[ ] NT8 closed before heavy file operations
[ ] Import one CSV file first
[ ] Chart loads correctly
```

Before flagship playback:

```text
[ ] Playback speed 1x–5x
[ ] Telemetry off
[ ] Diagnostics off
[ ] T3 disabled unless depth exists
[ ] One day only
```

---

# 21. McDuff Directive

Start with a one-day, tick-only Parquet/ZSTD export.

Transfer Parquet, convert to CSV on the VPS, then validate import, timestamps, and chart reconstruction.

Then scale to multi-day.

Do not begin with full MBO/L2 replay.

```text
Tick import first.
Depth bridge later.
```

