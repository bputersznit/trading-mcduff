#!/usr/bin/env bash
set -euo pipefail

# CG_CH_export_mnqz5_ticks_csv.sh
# Strategy/methodology:
#   Optional one-day smoke-test exporter. Use this only for small test slices.
#   Main pipeline should use Parquet/ZSTD and convert to CSV at the VPS side.

CH_HOST="${CH_HOST:-localhost}"
CH_PORT="${CH_PORT:-9000}"
CH_USER="${CH_USER:-default}"
CH_PASSWORD="${CH_PASSWORD:-}"
CH_DATABASE="${CH_DATABASE:-default}"
CH_TABLE="${CH_TABLE:-mnq_trades}"
CH_SYMBOL="${CH_SYMBOL:-MNQZ5}"
TRADE_DATE="${TRADE_DATE:-2025-09-23}"
OUT_DIR="${OUT_DIR:-$HOME/nt8_exports/csv_smoke}"

mkdir -p "$OUT_DIR"
out_file="$OUT_DIR/MNQ_12-25_${TRADE_DATE}_RTH_ticks_nt8.csv"

client_base=(clickhouse-client --host "$CH_HOST" --port "$CH_PORT" --user "$CH_USER" --database "$CH_DATABASE")
if [[ -n "$CH_PASSWORD" ]]; then
  client_base+=(--password "$CH_PASSWORD")
fi

utc_start="${TRADE_DATE} 13:30:00"
utc_end="${TRADE_DATE} 20:00:00"

echo "[CSV EXPORT] ${TRADE_DATE} RTH → $out_file"

# Common NT-style tick import shape: yyyyMMdd HHmmss;price;volume
# Some NT installs expect semicolon-delimited files with no header. Adjust if your NT import template differs.
"${client_base[@]}" --query "
SELECT
    concat(formatDateTime(toTimeZone(ts_event, 'America/New_York'), '%Y%m%d %H%M%S'), ';',
           toString(price), ';',
           toString(toUInt64(size))) AS nt8_line
FROM ${CH_TABLE}
WHERE symbol = '${CH_SYMBOL}'
  AND ts_event >= toDateTime64('${utc_start}', 9, 'UTC')
  AND ts_event <  toDateTime64('${utc_end}', 9, 'UTC')
ORDER BY ts_event
FORMAT TSVRaw
" > "$out_file"

sha256sum "$out_file" > "${out_file}.sha256"
echo "[DONE] $out_file"
