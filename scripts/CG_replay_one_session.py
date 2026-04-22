#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

import argparse

import polars as pl

from cg_sim.models import MarketEvent, EventType
from cg_sim.replay import ReplayEngine
from cg_strategy.base import BaseStrategy, StrategyContext


class SnapshotOnlyStrategy(BaseStrategy):
    def on_market_event(self, evt: MarketEvent, ctx: StrategyContext) -> None:
        return


def parse_event_type(value: str) -> EventType:
    value = str(value).lower()

    mapping = {
        "add": EventType.ADD,
        "cancel": EventType.CANCEL,
        "modify": EventType.MODIFY,
        "trade": EventType.TRADE,
        "depth": EventType.DEPTH,
        "bar_close": EventType.BAR_CLOSE,
        "marker": EventType.MARKER,
        "a": EventType.ADD,
        "c": EventType.CANCEL,
        "m": EventType.MODIFY,
        "t": EventType.TRADE,
        "f": EventType.TRADE,
    }

    if value not in mapping:
        raise ValueError(f"Unknown event_type: {value}")

    return mapping[value]


def load_events(parquet_path: Path) -> list[MarketEvent]:
    df = pl.read_parquet(parquet_path)

    events: list[MarketEvent] = []

    for row in df.iter_rows(named=True):
        events.append(
            MarketEvent(
                ts_event_ns=int(row["ts_event_ns"]),
                ts_recv_ns=int(row["ts_recv_ns"]) if row.get("ts_recv_ns") is not None else None,
                event_type=parse_event_type(row["event_type"]),
                side=row.get("side"),
                price=float(row["price"]) if row.get("price") is not None else None,
                size=int(row["size"]) if row.get("size") is not None else None,
                order_id=int(row["order_id"]) if row.get("order_id") is not None else None,
                flags=int(row["flags"]) if row.get("flags") is not None else None,
                source="parquet_replay",
                symbol="MNQ",
            )
        )

    return events


def print_snapshot(snapshot: dict, event_idx: int) -> None:
    print(
        f"[snap] "
        f"event_idx={event_idx:,} "
        f"best_bid={snapshot['best_bid']} "
        f"best_ask={snapshot['best_ask']} "
        f"spread={snapshot['spread']}"
    )

    print("       top bids:")
    for row in snapshot["bids"][:5]:
        print(
            f"         bid "
            f"px={row['price']} "
            f"size={row['size']} "
            f"orders={row['orders']}"
        )

    print("       top asks:")
    for row in snapshot["asks"][:5]:
        print(
            f"         ask "
            f"px={row['price']} "
            f"size={row['size']} "
            f"orders={row['orders']}"
        )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--parquet", required=True)
    parser.add_argument("--snapshot-every", type=int, default=50000)
    args = parser.parse_args()

    parquet_path = Path(args.parquet)

    if not parquet_path.exists():
        raise FileNotFoundError(f"Parquet not found: {parquet_path}")

    print(f"[info] Loading parquet: {parquet_path}")
    events = load_events(parquet_path)
    print(f"[info] Loaded events: {len(events):,}")

    strategy = SnapshotOnlyStrategy()
    engine = ReplayEngine(events, strategy)

    print("[info] Starting replay...")

    last_ts = None

    for i, evt in enumerate(engine.events, start=1):
        delta_ns = 0 if last_ts is None else max(0, evt.ts_event_ns - last_ts)

        target_time = engine.env.now + delta_ns + engine.latency.feed_latency_ns

        if target_time > engine.env.now:
            engine.env.run(until=target_time)

        engine.current_ts_ns = evt.ts_event_ns
        engine.book.apply_event(evt)
        engine.strategy.on_market_event(evt, engine.ctx)

        last_ts = evt.ts_event_ns

        if i % args.snapshot_every == 0:
            snap = engine.book.snapshot(depth=5)
            print_snapshot(snap, i)

    final_snap = engine.book.snapshot(depth=5)

    print("[done] Replay complete.")
    print_snapshot(final_snap, len(engine.events))
    print(f"[done] Final position: {engine.exec_engine.position}")
    print(f"[done] Total fills: {len(engine.exec_engine.fills)}")


if __name__ == "__main__":
    main()