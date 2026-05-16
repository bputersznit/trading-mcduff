#!/usr/bin/env bash
set -euo pipefail

# CG_CH_export_mnqz5_parquet.sh
# Strategy/methodology:
#   Export local ClickHouse MNQ trade ticks in daily RTH chunks using compact Parquet/ZSTD.
#   Keep transport files small, resumable, checksum-verifiable, and contract-mapped to NT8 MNQ 12-25.
#
# Required env vars can be overridden before running:
#   CH_HOST=localhost
#   CH_PORT=9000
#   CH_USER=default
#   CH_PASSWORD='...'
#   CH_DATABASE=default
#   CH_TABLE=mnq_trades
#   CH_SYMBOL=MNQZ5
#   DATE_FROM=2025-09-23
#   DATE_TO=2025-10-23     # exclusive
#   OUT_DIR=/home/bernard/nt8_exports/mnqz5_sep_oct_2025
#
# Assumptions:
#   - ts_event is UTC DateTime64
#   - price is Float64
#   - size is trade volume
#   - symbol column exists
#   - RTH for Sep/Oct 2025 is 13:30:00-20:00:00 UTC, corresponding to 09:30-16:00 ET.

CH_HOST="${CH_HOST:-localhost}"
CH_PORT="${CH_PORT:-9000}"
CH_USER="${CH_USER:-default}"
CH_PASSWORD="${CH_PASSWORD:-}"
CH_DATABASE="${CH_DATABASE:-default}"
CH_TABLE="${CH_TABLE:-mnq_trades}"
CH_SYMBOL="${CH_SYMBOL:-MNQZ5}"
DATE_FROM="${DATE_FROM:-2025-09-23}"
DATE_TO="${DATE_TO:-2025-10-23}"
OUT_DIR="${OUT_DIR:-$HOME/nt8_exports/mnqz5_sep_oct_2025}"

mkdir -p "$OUT_DIR/parquet" "$OUT_DIR/logs"
MANIFEST="$OUT_DIR/manifest.csv"
CHECKSUMS="$OUT_DIR/SHA256SUMS.txt"

: > "$CHECKSUMS"
echo "trade_date,symbol,nt_instrument,file_name,utc_start,utc_end,rows,bytes,sha256" > "$MANIFEST"

client_base=(clickhouse-client --host "$CH_HOST" --port "$CH_PORT" --user "$CH_USER" --database "$CH_DATABASE")
if [[ -n "$CH_PASSWORD" ]]; then
  client_base+=(--password "$CH_PASSWORD")
fi

current="$DATE_FROM"
while [[ "$current" < "$DATE_TO" ]]; do
  next=$(date -I -d "$current + 1 day")
  utc_start="${current} 13:30:00"
  utc_end="${current} 20:00:00"
  out_file="$OUT_DIR/parquet/MNQZ5_${current}_RTH.parquet"
  tmp_file="${out_file}.tmp"

  echo "[EXPORT] $current RTH UTC $utc_start → $utc_end"

  query="
    SELECT
      ts_event,
      price,
      toUInt64(size) AS size,
      side,
      symbol
    FROM ${CH_TABLE}
    WHERE symbol = '${CH_SYMBOL}'
      AND ts_event >= toDateTime64('${utc_start}', 9, 'UTC')
      AND ts_event <  toDateTime64('${utc_end}', 9, 'UTC')
    ORDER BY ts_event
    SETTINGS output_format_parquet_compression_method = 'zstd'
    FORMAT Parquet
  "

  "${client_base[@]}" --query "$query" > "$tmp_file"
  mv "$tmp_file" "$out_file"

  rows=$("${client_base[@]}" --query "
    SELECT count()
    FROM ${CH_TABLE}
    WHERE symbol = '${CH_SYMBOL}'
      AND ts_event >= toDateTime64('${utc_start}', 9, 'UTC')
      AND ts_event <  toDateTime64('${utc_end}', 9, 'UTC')
  ")

  bytes=$(stat -c%s "$out_file")
  sha=$(sha256sum "$out_file" | awk '{print $1}')
  echo "$sha  parquet/$(basename "$out_file")" >> "$CHECKSUMS"
  echo "${current},${CH_SYMBOL},MNQ 12-25,parquet/$(basename "$out_file"),${utc_start},${utc_end},${rows},${bytes},${sha}" >> "$MANIFEST"

  current="$next"
done

cat > "$OUT_DIR/README_IMPORT_NOTES.txt" <<TXT
CG ClickHouse → NT8 export set
Source CH symbol: ${CH_SYMBOL}
Target NT8 instrument: MNQ 12-25
Date range: ${DATE_FROM} to ${DATE_TO} exclusive
Format: Parquet with ZSTD compression
RTH window: 13:30-20:00 UTC = 09:30-16:00 New York time for Sep/Oct 2025

Next step:
  rclone copy/sync this folder to VPS/cloud.
  Convert Parquet to NT8 CSV on VPS only when ready to import.
TXT

echo "[DONE] Export folder: $OUT_DIR"
echo "[DONE] Manifest: $MANIFEST"
echo "[DONE] Checksums: $CHECKSUMS"
