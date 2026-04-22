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


def level_snapshot(book: OrderBook, side: str, price: float | None):
    if price is None:
        return None

    if side in ("BID", "BUY"):
        level = book.bids.get(price)
    else:
        level = book.asks.get(price)

    if level is None:
        return None

    return {
        "price": price,
        "size": level.total_size,
        "orders": level.order_count,
        "added": level.added_size,
        "canceled": level.canceled_size,
        "traded": level.traded_size,
    }


def order_snapshot(book: OrderBook, order_id: int | None):
    if order_id is None:
        return None

    order = book.orders.get(order_id)
    if order is None:
        return None

    return {
        "order_id": order.order_id,
        "side": order.side,
        "price": order.price,
        "size": order.size,
        "ts_event_ns": order.ts_event_ns,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--parquet", required=True)
    parser.add_argument("--max-events", type=int, default=100000)
    parser.add_argument("--max-failures", type=int, default=20)
    args = parser.parse_args()

    parquet_path = Path(args.parquet)

    print(f"[info] Loading events from: {parquet_path}")
    events = load_events(parquet_path, max_events=args.max_events)
    print(f"[info] Loaded {len(events):,} events")

    book = OrderBook()

    failures_found = 0

    for idx, evt in enumerate(events, start=1):
        best_bid_before = book.best_bid()
        best_ask_before = book.best_ask()

        order_before = order_snapshot(book, evt.order_id)
        level_before = level_snapshot(book, evt.side or "", evt.price)

        missing_order = (
            evt.event_type in (EventType.CANCEL, EventType.MODIFY, EventType.TRADE)
            and evt.order_id is not None
            and evt.order_id not in book.orders
        )

        negative_before = False
        crossed_before = (
            best_bid_before is not None
            and best_ask_before is not None
            and best_bid_before >= best_ask_before
        )

        book.apply_event(evt)

        best_bid_after = book.best_bid()
        best_ask_after = book.best_ask()

        level_after = level_snapshot(book, evt.side or "", evt.price)
        order_after = order_snapshot(book, evt.order_id)

        negative_after = False

        for _, level in book.bids.items():
            if level.total_size < 0:
                negative_after = True
                break

        if not negative_after:
            for _, level in book.asks.items():
                if level.total_size < 0:
                    negative_after = True
                    break

        crossed_after = (
            best_bid_after is not None
            and best_ask_after is not None
            and best_bid_after >= best_ask_after
        )

        if missing_order or negative_after or crossed_after:
            failures_found += 1

            print("=" * 100)
            print(f"[failure #{failures_found}] event_idx={idx:,}")
            print(f"event_type     : {evt.event_type}")
            print(f"side           : {evt.side}")
            print(f"price          : {evt.price}")
            print(f"size           : {evt.size}")
            print(f"order_id       : {evt.order_id}")
            print(f"flags          : {evt.flags}")
            print(f"ts_event_ns    : {evt.ts_event_ns}")

            print()
            print(f"missing_order  : {missing_order}")
            print(f"negative_after : {negative_after}")
            print(f"crossed_after  : {crossed_after}")

            print()
            print(f"best_bid_before: {best_bid_before}")
            print(f"best_ask_before: {best_ask_before}")
            print(f"best_bid_after : {best_bid_after}")
            print(f"best_ask_after : {best_ask_after}")

            print()
            print(f"order_before   : {order_before}")
            print(f"order_after    : {order_after}")

            print()
            print(f"level_before   : {level_before}")
            print(f"level_after    : {level_after}")

            if failures_found >= args.max_failures:
                print()
                print(f"[done] Reached max_failures={args.max_failures}")
                break

        if idx % 100000 == 0:
            print(f"[progress] processed={idx:,}")

    print()
    print(f"[summary] failures_found={failures_found}")


if __name__ == "__main__":
    main()
