#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

import argparse

import polars as pl

from cg_book.order_book_relaxed import OrderBookRelaxed
from cg_sim.models import EventType, MarketEvent


EVENT_TYPE_MAP = {
    "add": EventType.ADD,
    "cancel": EventType.CANCEL,
    "modify": EventType.MODIFY,
    "trade": EventType.TRADE,
}


def load_events(parquet_path: Path, max_events: int | None = None) -> list[MarketEvent]:
    df = pl.read_parquet(parquet_path)

    if max_events is not None:
        df = df.head(max_events)

    events: list[MarketEvent] = []

    for row in df.iter_rows(named=True):
        evt_type = EVENT_TYPE_MAP.get(str(row["event_type"]).lower())
        if evt_type is None:
            continue

        events.append(
            MarketEvent(
                ts_event_ns=int(row["ts_event_ns"]),
                ts_recv_ns=int(row["ts_recv_ns"]) if row.get("ts_recv_ns") is not None else None,
                event_type=evt_type,
                side=row.get("side"),
                price=float(row["price"]) if row.get("price") is not None else None,
                size=int(row["size"]) if row.get("size") is not None else None,
                order_id=int(row["order_id"]) if row.get("order_id") is not None else None,
                flags=int(row["flags"]) if row.get("flags") is not None else None,
            )
        )

    return events


def invert_side(side: str | None) -> str | None:
    if side is None:
        return side

    s = str(side).upper()

    if s in ("B", "BUY", "BID"):
        return "ASK"

    if s in ("S", "SELL", "ASK"):
        return "BID"

    return side


def maybe_invert_event_side(evt: MarketEvent, invert_set: set[str]) -> MarketEvent:
    evt_name = evt.event_type.value.lower()

    if evt_name not in invert_set:
        return evt

    return MarketEvent(
        ts_event_ns=evt.ts_event_ns,
        ts_recv_ns=evt.ts_recv_ns,
        event_type=evt.event_type,
        side=invert_side(evt.side),
        price=evt.price,
        size=evt.size,
        order_id=evt.order_id,
        flags=evt.flags,
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--parquet", required=True)
    parser.add_argument("--max-events", type=int, default=500000)
    parser.add_argument(
        "--mode",
        choices=["add_cancel_only", "add_cancel_modify", "full"],
        default="add_cancel_modify",
    )
    parser.add_argument(
        "--invert",
        default="",
        help=(
            "Comma-separated event types to invert side for. "
            "Examples: modify | cancel | trade | modify,cancel"
        ),
    )
    args = parser.parse_args()

    parquet_path = Path(args.parquet)

    invert_set = {
        x.strip().lower()
        for x in args.invert.split(",")
        if x.strip()
    }

    print(f"[info] Loading events from: {parquet_path}")
    events = load_events(parquet_path, max_events=args.max_events)
    print(f"[info] Loaded {len(events):,} events")
    print(f"[info] Mode: {args.mode}")
    print(f"[info] Inverting event types: {sorted(invert_set) if invert_set else 'none'}")

    book = OrderBookRelaxed(mode=args.mode)

    for idx, evt in enumerate(events, start=1):
        evt2 = maybe_invert_event_side(evt, invert_set)
        book.apply_event(evt2)

        if idx % 100000 == 0:
            stats = book.stats_dict()
            print(
                f"[progress] processed={idx:,} "
                f"crossed={stats['crossed_book_count']:,} "
                f"missing_levels={stats['missing_level_refs']:,} "
                f"capped={stats['capped_decrements']:,} "
                f"best_bid={stats['best_bid']} "
                f"best_ask={stats['best_ask']} "
                f"spread={stats['spread']}"
            )

    stats = book.stats_dict()
    snap = book.snapshot(depth=5)

    print()
    print("[summary] Relaxed book validation results")
    for k, v in stats.items():
        print(f"{k:24s}: {v}")

    print()
    print("[summary] Top-of-book snapshot")
    print(f"best_bid: {snap['best_bid']}")
    print(f"best_ask: {snap['best_ask']}")
    print(f"spread  : {snap['spread']}")

    print()
    print("[summary] Top bids")
    for row in snap["bids"]:
        print(row)

    print()
    print("[summary] Top asks")
    for row in snap["asks"]:
        print(row)


if __name__ == "__main__":
    main()
