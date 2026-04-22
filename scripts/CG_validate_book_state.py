#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

import argparse

import polars as pl

from cg_book.order_book import OrderBook
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--parquet", required=True)
    parser.add_argument("--max-events", type=int, default=500000)
    args = parser.parse_args()

    parquet_path = Path(args.parquet)

    print(f"[info] Loading events from: {parquet_path}")
    events = load_events(parquet_path, max_events=args.max_events)
    print(f"[info] Loaded {len(events):,} events")

    book = OrderBook()

    crossed_book_count = 0
    negative_level_count = 0
    missing_order_reference_count = 0
    zero_removed_levels = 0
    max_bid_levels = 0
    max_ask_levels = 0

    event_counts: dict[str, int] = {}

    for idx, evt in enumerate(events, start=1):
        evt_name = evt.event_type.value
        event_counts[evt_name] = event_counts.get(evt_name, 0) + 1

        if evt.event_type in (EventType.CANCEL, EventType.MODIFY, EventType.TRADE):
            if evt.order_id is not None and evt.order_id not in book.orders:
                missing_order_reference_count += 1

        bid_levels_before = len(book.bids)
        ask_levels_before = len(book.asks)

        book.apply_event(evt)

        bid_levels_after = len(book.bids)
        ask_levels_after = len(book.asks)

        if bid_levels_after < bid_levels_before or ask_levels_after < ask_levels_before:
            zero_removed_levels += 1

        max_bid_levels = max(max_bid_levels, bid_levels_after)
        max_ask_levels = max(max_ask_levels, ask_levels_after)

        best_bid = book.best_bid()
        best_ask = book.best_ask()

        if best_bid is not None and best_ask is not None:
            if best_bid >= best_ask:
                crossed_book_count += 1

        for _, level in book.bids.items():
            if level.total_size < 0:
                negative_level_count += 1

        for _, level in book.asks.items():
            if level.total_size < 0:
                negative_level_count += 1

        if idx % 100000 == 0:
            print(f"[progress] processed={idx:,}")

    print()
    print("[summary] Book validation results")
    print(f"events_processed           : {len(events):,}")
    print(f"crossed_book_count         : {crossed_book_count:,}")
    print(f"negative_level_count       : {negative_level_count:,}")
    print(f"missing_order_refs         : {missing_order_reference_count:,}")
    print(f"zero_removed_levels        : {zero_removed_levels:,}")
    print(f"max_bid_levels             : {max_bid_levels:,}")
    print(f"max_ask_levels             : {max_ask_levels:,}")

    print()
    print("[summary] Event counts")
    for k, v in sorted(event_counts.items()):
        print(f"{k:12s}: {v:,}")


if __name__ == "__main__":
    main()
