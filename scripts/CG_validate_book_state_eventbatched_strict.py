#!/usr/bin/env python3
from __future__ import annotations

import sys
from collections import defaultdict
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

import argparse

import polars as pl

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_sim.models import EventType, MarketEvent

F_LAST = 128


def load_events(
    parquet_path: Path,
    max_events: int | None = None,
) -> list[tuple[MarketEvent, str]]:
    df = pl.read_parquet(parquet_path)

    if max_events is not None:
        df = df.head(max_events)

    out: list[tuple[MarketEvent, str]] = []

    for row in df.iter_rows(named=True):
        evt = MarketEvent(
            ts_event_ns=int(row["ts_event_ns"]),
            ts_recv_ns=(
                int(row["ts_recv_ns"])
                if row.get("ts_recv_ns") is not None
                else None
            ),
            event_type=EventType.MARKER,
            side=row.get("side"),
            price=(
                float(row["price"])
                if row.get("price") is not None
                else None
            ),
            size=(
                int(row["size"])
                if row.get("size") is not None
                else None
            ),
            order_id=(
                int(row["order_id"])
                if row.get("order_id") is not None
                else None
            ),
            flags=(
                int(row["flags"])
                if row.get("flags") is not None
                else None
            ),
        )

        evt_name = str(row["event_type"]).lower()
        out.append((evt, evt_name))

    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--parquet", required=True)
    parser.add_argument("--max-events", type=int, default=500000)
    parser.add_argument("--progress-every-batches", type=int, default=50000)
    args = parser.parse_args()

    parquet_path = Path(args.parquet)

    print(f"[info] Loading events from: {parquet_path}")
    events = load_events(parquet_path, args.max_events)
    print(f"[info] Loaded {len(events):,} events")

    book = OrderBookEventBatchedStrict()
    stats = defaultdict(int)

    current_ts = None
    batch: list[tuple[MarketEvent, str]] = []

    def process_batch(batch_rows: list[tuple[MarketEvent, str]]) -> None:
        if not batch_rows:
            return

        stats["batches_seen"] += 1

        if any(((evt.flags or 0) & F_LAST) != 0 for evt, _ in batch_rows):
            stats["batches_with_f_last"] += 1

        for evt, evt_name in batch_rows:
            book.apply_event(evt, evt_name)

        bid = book.best_bid()
        ask = book.best_ask()

        if bid is not None and ask is not None:
            stats["stable_snapshots"] += 1

            if bid >= ask:
                stats["crossed_after_batch"] += 1

    for idx, (evt, evt_name) in enumerate(events, start=1):
        if current_ts is None:
            current_ts = evt.ts_event_ns

        if evt.ts_event_ns != current_ts:
            process_batch(batch)
            batch = []
            current_ts = evt.ts_event_ns

            if (
                stats["batches_seen"] > 0
                and stats["batches_seen"] % args.progress_every_batches == 0
            ):
                s = book.stats_dict()

                print(
                    f"[progress] "
                    f"rows={idx:,} "
                    f"batches={stats['batches_seen']:,} "
                    f"crossed_after_batch={stats['crossed_after_batch']:,} "
                    f"active_orders={s['active_orders']:,} "
                    f"best_bid={s['best_bid']} "
                    f"best_ask={s['best_ask']} "
                    f"spread={s['spread']}"
                )

        batch.append((evt, evt_name))

    process_batch(batch)

    s = book.stats_dict()
    snap = book.snapshot(depth=5)

    print("\n[summary] Strict event-batched validation results")
    print(f"{'rows_loaded':28s}: {len(events)}")

    for k, v in stats.items():
        print(f"{k:28s}: {v}")

    print("\n[summary] Strict book stats")
    for k, v in s.items():
        print(f"{k:28s}: {v}")

    print("\n[summary] Top-of-book snapshot")
    print(f"best_bid: {snap['best_bid']}")
    print(f"best_ask: {snap['best_ask']}")
    print(f"spread  : {snap['spread']}")
    print(f"active_orders: {snap['active_orders']}")

    print("\n[summary] Top bids")
    for row in snap["bids"]:
        print(row)

    print("\n[summary] Top asks")
    for row in snap["asks"]:
        print(row)


if __name__ == "__main__":
    main()