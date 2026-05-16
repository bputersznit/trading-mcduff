# CG ClickHouse → NT8 Playback/Import Pipeline v1

## Purpose
Move local Linux ClickHouse MNQ Sep/Oct 2025 data to a Windows NT8 VPS without shipping gigantic raw CSVs.

Core flow:

```text
Local Linux ClickHouse
→ Parquet/ZSTD day chunks
→ manifest + sha256 checksums
→ rclone transfer
→ Windows VPS staging folder
→ NT8-compatible CSV generation
→ NinjaTrader Historical Data Import / Playback testing
```

## Critical Contract Mapping

Your Sep/Oct 2025 data should be imported/tested as:

```text
Databento/CH symbol: MNQZ5
NinjaTrader instrument: MNQ 12-25
```

Do **not** import this data as `MNQ JUN26`.

## Files

| File | Where | Purpose |
|---|---|---|
| `CG_CH_export_mnqz5_parquet.sh` | Linux CH machine | Exports one Parquet/ZSTD file per RTH day |
| `CG_CH_export_mnqz5_ticks_csv.sh` | Linux CH machine | Optional direct CSV exporter for one day smoke tests |
| `CG_rclone_push_nt8_exports.sh` | Linux CH machine | Pushes compressed export folder to remote/VPS/cloud |
| `CG_nt8_parquet_to_import_csv.py` | Windows VPS or Linux | Converts Parquet chunks to NT8 import CSV |
| `CG_nt8_import_smoke_test_checklist.md` | Windows VPS / reference | NT8 import/playback validation checklist |

## Recommended First Test

Start with one RTH day only:

```bash
export CH_DATABASE=default
export CH_TABLE=mnq_trades
export CH_SYMBOL=MNQZ5
export OUT_DIR=/home/bernard/nt8_exports/mnqz5_sep_oct_2025
export DATE_FROM=2025-09-23
export DATE_TO=2025-09-24

bash CG_CH_export_mnqz5_parquet.sh
bash CG_rclone_push_nt8_exports.sh
```

Then on VPS:

```powershell
python CG_nt8_parquet_to_import_csv.py `
  --input-dir C:\NT8_Staging\mnqz5_sep_oct_2025 `
  --output-dir C:\NT8_Imports\MNQ_12_25 `
  --nt-instrument "MNQ 12-25" `
  --timezone America/New_York
```

## Notes

1. Parquet/ZSTD is the transport/storage format.
2. CSV is only generated at the last step because NT8 import expects text formats.
3. If you need L2/T3 wall logic in Playback, trade-tick CSV alone is not enough. NT8 needs market depth replay data, which is a separate and harder problem.
4. For strategy smoke tests, disable/bypass T3 depth confirmation unless the playback data has depth.
