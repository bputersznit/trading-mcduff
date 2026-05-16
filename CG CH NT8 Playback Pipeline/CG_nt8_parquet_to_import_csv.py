#!/usr/bin/env python3
"""
CG_nt8_parquet_to_import_csv.py

Strategy/methodology:
  Convert compressed Parquet/ZSTD CH export chunks into NT8 tick import CSVs only at the final stage.
  This avoids moving titanic raw CSV files across the network.

Output default line format:
  yyyyMMdd HHmmss;price;volume

Important:
  Confirm your local NT8 import settings. Some NT8 import formats vary by data type/vendor/export template.
  This script intentionally emits one file per Parquet input to keep imports/chunks manageable.
"""

from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path

import pandas as pd


def sha256_file(path: Path, chunk_size: int = 1024 * 1024) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        while True:
            b = f.read(chunk_size)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def convert_one(parquet_path: Path, output_dir: Path, timezone: str, delimiter: str, include_header: bool) -> Path:
    df = pd.read_parquet(parquet_path)

    required = {"ts_event", "price", "size"}
    missing = required.difference(df.columns)
    if missing:
        raise ValueError(f"{parquet_path} missing required columns: {sorted(missing)}")

    ts = pd.to_datetime(df["ts_event"], utc=True).dt.tz_convert(timezone)
    out = pd.DataFrame({
        "time": ts.dt.strftime("%Y%m%d %H%M%S"),
        "price": df["price"].map(lambda x: f"{float(x):.2f}"),
        "volume": df["size"].astype("int64").astype(str),
    })

    output_dir.mkdir(parents=True, exist_ok=True)
    out_path = output_dir / (parquet_path.stem.replace("MNQZ5", "MNQ_12-25") + "_nt8_ticks.csv")
    out.to_csv(out_path, sep=delimiter, index=False, header=include_header, lineterminator="\n")

    checksum = sha256_file(out_path)
    with (out_path.with_suffix(out_path.suffix + ".sha256")).open("w", encoding="utf-8") as f:
        f.write(f"{checksum}  {out_path.name}\n")

    return out_path


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--input-dir", required=True, help="Directory containing Parquet files or a parquet/ subfolder")
    ap.add_argument("--output-dir", required=True, help="Directory for NT8 CSV files")
    ap.add_argument("--timezone", default="America/New_York", help="Target timestamp timezone for NT8 import")
    ap.add_argument("--delimiter", default=";", help="CSV delimiter; NT8 commonly uses semicolon")
    ap.add_argument("--include-header", action="store_true", help="Include CSV header; default is no header")
    args = ap.parse_args()

    input_dir = Path(args.input_dir)
    parquet_dir = input_dir / "parquet" if (input_dir / "parquet").exists() else input_dir
    output_dir = Path(args.output_dir)

    files = sorted(parquet_dir.glob("*.parquet"))
    if not files:
        raise SystemExit(f"No parquet files found in {parquet_dir}")

    print(f"[INFO] Found {len(files)} parquet files")
    print(f"[INFO] Output: {output_dir}")
    print("[INFO] Target NT instrument should be MNQ 12-25")

    manifest_lines = ["source_parquet,output_csv,rows,bytes,sha256"]
    for p in files:
        print(f"[CONVERT] {p.name}")
        out_path = convert_one(p, output_dir, args.timezone, args.delimiter, args.include_header)
        rows = sum(1 for _ in out_path.open("r", encoding="utf-8", errors="ignore"))
        if args.include_header:
            rows -= 1
        sha = sha256_file(out_path)
        size = out_path.stat().st_size
        manifest_lines.append(f"{p.name},{out_path.name},{rows},{size},{sha}")
        print(f"[DONE] {out_path.name} rows={rows} bytes={size}")

    manifest = output_dir / "nt8_csv_manifest.csv"
    manifest.write_text("\n".join(manifest_lines) + "\n", encoding="utf-8")
    print(f"[DONE] Manifest: {manifest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
