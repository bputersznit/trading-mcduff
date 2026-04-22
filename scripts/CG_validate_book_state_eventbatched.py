#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

import argparse
from collections import defaultdict

import polars as pl

from cg_book.order_book_relaxed import OrderBookRelaxed
from cg_sim.models import EventType, MarketEvent


EVENT_TYPE_MAP = {
    "add": EventType.ADD,
    "cancel": EventType.CANCEL,
    "modify": EventType.MODIFY,
    "clear": EventType.MARKER,
    "trade_aggressor": EventType.MARKER,
    "fill_resting": EventType.MARKER,
    "none": EventType.MARKER,
    # backward compatibility
    "trade": EventType.MARKER,
}

F_LAST = 128


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
                meta={"event_type_name": str(row["event_type"]).lower()},
            )
        )

    return events


def process_batch(batch: list[MarketEvent], book: OrderBookRelaxed, stats: dict) -> None:
    if not batch:
        return

    stats["batches_seen"] += 1
    has_f_last = any(((evt.flags or 0) & F_LAST) != 0 for evt in batch)
    if has_f_last:
        stats["batches_with_f_last"] += 1

    for evt in batch:
        evt_name = evt.meta.get("event_type_name", evt.event_type.value)
        evt_for_book = MarketEvent(
            ts_event_ns=evt.ts_event_ns,
            ts_recv_ns=evt.ts_recv_ns,
            event_type=EventType.MARKER,  # ignored by relaxed book; name used below
            side=evt.side,
            price=evt.price,
            size=evt.size,
            order_id=evt.order_id,
            flags=evt.flags,
            meta={"event_type_name": evt_name},
        )

        evt_for_book.event_type = type("PseudoEventType", (), {"value": evt_name})()  # lightweight shim
        book.apply_event(evt_for_book)  # type: ignore[arg-type]

    best_bid = book.best_bid()
    best_ask = book.best_ask()

    if best_bid is not None and best_ask is not None:
        stats["stable_snapshots"] += 1
        if best_bid >= best_ask:
            stats["crossed_after_batch"] += 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--parquet", required=True)
    parser.add_argument("--max-events", type=int, default=500000)
    parser.add_argument(
        "--mode",
        choices=["add_cancel_only", "add_cancel_modify", "full"],
        default="full",
    )
    parser.add_argument("--progress-every-batches", type=int, default=50000)
    args = parser.parse_args()

    parquet_path = Path(args.parquet)

    print(f"[info] Loading events from: {parquet_path}")
    events = load_events(parquet_path, max_events=args.max_events)
    print(f"[info] Loaded {len(events):,} events")
    print(f"[info] Mode: {args.mode}")

    book = OrderBookRelaxed(mode=args.mode)
    stats = defaultdict(int)

    current_ts = None
    batch: list[MarketEvent] = []

    for idx, evt in enumerate(events, start=1):
        if current_ts is None:
            current_ts = evt.ts_event_ns

        if evt.ts_event_ns != current_ts:
            process_batch(batch, book, stats)
            batch = []
            current_ts = evt.ts_event_ns

            if stats["batches_seen"] % args.progress_every_batches == 0 and stats["batches_seen"] > 0:
                s = book.stats_dict()
                print(
                    f"[progress] rows={idx:,} "
                    f"batches={stats['batches_seen']:,} "
                    f"stable={stats['stable_snapshots']:,} "
                    f"crossed_after_batch={stats['crossed_after_batch']:,} "
                    f"best_bid={s['best_bid']} "
                    f"best_ask={s['best_ask']} "
                    f"spread={s['spread']}"
                )

        batch.append(evt)

    process_batch(batch, book, stats)

    s = book.stats_dict()
    snap = book.snapshot(depth=5)

    print()
    print("[summary] Event-batched validation results")
    print(f"rows_loaded               : {len(events):,}")
    print(f"batches_seen              : {stats['batches_seen']:,}")
    print(f"batches_with_f_last       : {stats['batches_with_f_last']:,}")
    print(f"stable_snapshots          : {stats['stable_snapshots']:,}")
    print(f"crossed_after_batch       : {stats['crossed_after_batch']:,}")

    print()
    print("[summary] Relaxed book stats")
    for k, v in s.items():
        print(f"{k:24s}: {v}")

    print()
    print("[summary] Top-of-book snapshot")
    print(f"best_bid: {snap['best_bid']}")
    print(f"best_ask: {snap['best_ask']}")
    print(f"spread  : {snap['spread']}")


if __name__ == "__main__":
    main()
