#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

import argparse
import os

import clickhouse_connect
import polars as pl
from dotenv import load_dotenv

TABLE_NAME = "mnq_mbo"


def get_env(name: str, default: str = "") -> str:
    return os.getenv(name, default)


def build_client():
    host = get_env("CLICKHOUSE_HOST", get_env("CH_HOST", "localhost"))
    port = int(get_env("CLICKHOUSE_PORT", get_env("CH_PORT", "8123")))
    user = get_env("CLICKHOUSE_USER", get_env("CH_USER", "default"))
    password = get_env("CLICKHOUSE_PASSWORD", get_env("CH_PASSWORD", ""))
    database = get_env("CLICKHOUSE_DATABASE", get_env("CH_DATABASE", "default"))

    return clickhouse_connect.get_client(
        host=host,
        port=port,
        username=user,
        password=password,
        database=database,
    )


def fetch_raw_slice(
    client,
    start_ts: str,
    end_ts: str,
    symbol: str,
    instrument_id: int,
    min_price: float,
    max_price: float,
) -> pl.DataFrame:
    query = f"""
    SELECT
        ts_event,
        ts_recv,
        symbol,
        instrument_id,
        action,
        side,
        price,
        size,
        order_id,
        flags
    FROM {TABLE_NAME}
    WHERE ts_event >= toDateTime64(%(start_ts)s, 9, 'UTC')
      AND ts_event <  toDateTime64(%(end_ts)s, 9, 'UTC')
      AND symbol = %(symbol)s
      AND instrument_id = %(instrument_id)s
      AND price >= %(min_price)s
      AND price <= %(max_price)s
    ORDER BY ts_event, order_id
    """

    pdf = client.query_df(
        query,
        parameters={
            "start_ts": start_ts,
            "end_ts": end_ts,
            "symbol": symbol,
            "instrument_id": instrument_id,
            "min_price": min_price,
            "max_price": max_price,
        },
    )

    return pl.from_pandas(pdf)


def normalize_mbo(df: pl.DataFrame) -> pl.DataFrame:
    """
    Preserve Databento-style raw action semantics.

    A = add
    M = modify
    C = cancel
    R = clear
    T = aggressor trade
    F = resting fill
    N = no book change / informational
    """
    action_map = {
        "A": "add",
        "a": "add",
        "C": "cancel",
        "c": "cancel",
        "M": "modify",
        "m": "modify",
        "R": "clear",
        "r": "clear",
        "T": "trade_aggressor",
        "t": "trade_aggressor",
        "F": "fill_resting",
        "f": "fill_resting",
        "N": "none",
        "n": "none",
    }

    out = (
        df.with_columns([
            pl.col("ts_event").dt.epoch(time_unit="ns").alias("ts_event_ns"),

            pl.when(pl.col("ts_recv").is_not_null())
            .then(pl.col("ts_recv").dt.epoch(time_unit="ns"))
            .otherwise(None)
            .alias("ts_recv_ns"),

            pl.col("action")
            .cast(pl.Utf8)
            .map_elements(
                lambda x: action_map.get(x, x.lower() if x is not None else x),
                return_dtype=pl.Utf8,
            )
            .alias("event_type"),

            pl.when(
                pl.col("side")
                .cast(pl.Utf8)
                .str.to_uppercase()
                .is_in(["B", "BID", "BUY"])
            )
            .then(pl.lit("BID"))
            .when(
                pl.col("side")
                .cast(pl.Utf8)
                .str.to_uppercase()
                .is_in(["A", "ASK", "SELL"])
            )
            .then(pl.lit("ASK"))
            .otherwise(pl.lit("UNKNOWN"))
            .alias("side_norm"),

            pl.col("price").cast(pl.Float64),
            pl.col("size").cast(pl.Int64),
            pl.col("order_id").cast(pl.Int64),
            pl.col("flags").cast(pl.Int64),
            pl.col("instrument_id").cast(pl.Int64),
            pl.col("symbol").cast(pl.Utf8),
        ])
        .filter(pl.col("side_norm") != "UNKNOWN")
        .select([
            "ts_event_ns",
            "ts_recv_ns",
            "symbol",
            "instrument_id",
            "event_type",
            pl.col("side_norm").alias("side"),
            "price",
            "size",
            "order_id",
            "flags",
        ])
    )

    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--start", required=True, help='UTC start, e.g. "2025-10-22 00:00:00"')
    parser.add_argument("--end", required=True, help='UTC end, e.g. "2025-10-22 20:00:00"')
    parser.add_argument("--out", required=True, help="Output parquet path")
    parser.add_argument("--symbol", required=True, help="Exact symbol, e.g. MNQZ5")
    parser.add_argument("--instrument-id", type=int, required=True, help="Exact instrument_id")
    parser.add_argument("--min-price", type=float, required=True, help="Minimum sane price")
    parser.add_argument("--max-price", type=float, required=True, help="Maximum sane price")
    args = parser.parse_args()

    load_dotenv()

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    print("[info] Connecting to ClickHouse...")
    client = build_client()

    print(
        f"[info] Fetching raw slice: {args.start} -> {args.end} "
        f"symbol={args.symbol} instrument_id={args.instrument_id} "
        f"price_band=[{args.min_price}, {args.max_price}]"
    )
    raw_df = fetch_raw_slice(
        client=client,
        start_ts=args.start,
        end_ts=args.end,
        symbol=args.symbol,
        instrument_id=args.instrument_id,
        min_price=args.min_price,
        max_price=args.max_price,
    )
    print(f"[info] Raw rows: {raw_df.height:,}")

    print("[info] Raw side mix:")
    print(
        raw_df.with_columns(pl.col("side").cast(pl.Utf8).str.to_uppercase().alias("side_u"))
        .group_by("side_u")
        .len()
        .sort("len", descending=True)
    )

    print("[info] Normalizing events...")
    norm_df = normalize_mbo(raw_df)
    print(f"[info] Normalized rows: {norm_df.height:,}")

    print("[info] Symbol / instrument / price check:")
    print(
        norm_df.select([
            pl.col("symbol").n_unique().alias("unique_symbols"),
            pl.col("instrument_id").n_unique().alias("unique_instrument_ids"),
            pl.col("price").min().alias("min_price"),
            pl.col("price").max().alias("max_price"),
        ])
    )

    print("[info] Event type mix:")
    print(norm_df.group_by("event_type").len().sort("len", descending=True))

    print(f"[info] Writing parquet: {out_path}")
    norm_df.write_parquet(out_path)

    print("[done] Extraction complete.")


if __name__ == "__main__":
    main()