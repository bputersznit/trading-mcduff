#!/usr/bin/env python3
"""
Complete replay script with strict fill simulation.

This script:
1. Loads real MBO data from parquet
2. Builds strict event-batched order book
3. Simulates realistic fills (market + passive)
4. Tracks PnL and execution metrics
5. Outputs detailed fill statistics

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

import sys
from pathlib import Path
from collections import defaultdict

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

import argparse
import polars as pl

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_exec.CGCl_fill_model_strict import StrictFillModel, StrictPassiveOrder
from cg_sim.models import MarketEvent, EventType

F_LAST = 128


def load_events(
    parquet_path: Path,
    max_events: int | None = None,
) -> list[tuple[MarketEvent, str]]:
    """Load events from parquet file."""
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


class SimpleStrategy:
    """
    Simple test strategy that:
    1. Places passive buy/sell orders periodically
    2. Simulates occasional market orders
    3. Tracks fills and PnL
    """

    def __init__(self, fill_model: StrictFillModel):
        self.fill_model = fill_model
        self.active_orders: list[StrictPassiveOrder] = []
        self.filled_orders: list[StrictPassiveOrder] = []

        # Strategy state
        self.last_action_ns = 0
        self.action_interval_ns = 60_000_000_000  # 60 seconds

        # Market orders
        self.market_orders_placed = 0
        self.market_fills: list = []

        # PnL tracking
        self.position = 0
        self.avg_price = 0.0
        self.realized_pnl = 0.0

    def on_event_batch(
        self,
        batch: list[tuple[MarketEvent, str]],
        book: OrderBookEventBatchedStrict,
    ) -> None:
        """Process an event batch."""
        if not batch:
            return

        ts_ns = batch[0][0].ts_event_ns

        # Advance all active passive orders
        for evt, evt_name in batch:
            for i, order in enumerate(self.active_orders):
                self.active_orders[i] = self.fill_model.advance_passive_order(
                    order, evt, evt_name
                )

        # Move filled orders to filled list
        newly_filled = [o for o in self.active_orders if o.is_filled]
        self.filled_orders.extend(newly_filled)
        self.active_orders = [o for o in self.active_orders if not o.is_filled]

        # Update PnL for filled orders
        for order in newly_filled:
            qty_signed = order.qty if order.side == "BUY" else -order.qty
            self._update_position(qty_signed, order.price)

        # Periodically place new orders or execute market orders
        if ts_ns - self.last_action_ns > self.action_interval_ns:
            self._take_action(book, ts_ns)
            self.last_action_ns = ts_ns

    def _take_action(self, book: OrderBookEventBatchedStrict, ts_ns: int) -> None:
        """Place passive orders or execute market orders."""
        best_bid = book.best_bid()
        best_ask = book.best_ask()

        if best_bid is None or best_ask is None:
            return

        # 70% chance to place passive, 30% chance market
        import random
        if random.random() < 0.7:
            # Place passive orders
            if random.random() < 0.5:
                # Buy limit at best bid
                order = self.fill_model.place_passive_limit(
                    side="BUY",
                    price=best_bid,
                    qty=random.randint(1, 5),
                    book=book,
                    ts_ns=ts_ns,
                    tag="passive_buy",
                )
                self.active_orders.append(order)
            else:
                # Sell limit at best ask
                order = self.fill_model.place_passive_limit(
                    side="SELL",
                    price=best_ask,
                    qty=random.randint(1, 5),
                    book=book,
                    ts_ns=ts_ns,
                    tag="passive_sell",
                )
                self.active_orders.append(order)
        else:
            # Execute market order
            if random.random() < 0.5:
                fill = self.fill_model.simulate_market_buy(
                    qty=random.randint(2, 8),
                    book=book,
                )
                if fill.fully_filled:
                    self.market_orders_placed += 1
                    self.market_fills.append(fill)
                    self._update_position(fill.filled_qty, fill.avg_fill_price)
            else:
                fill = self.fill_model.simulate_market_sell(
                    qty=random.randint(2, 8),
                    book=book,
                )
                if fill.fully_filled:
                    self.market_orders_placed += 1
                    self.market_fills.append(fill)
                    self._update_position(-fill.filled_qty, fill.avg_fill_price)

    def _update_position(self, qty_signed: int, price: float) -> None:
        """Update position and PnL."""
        if self.position == 0:
            self.position = qty_signed
            self.avg_price = price
        elif (self.position > 0 and qty_signed > 0) or (self.position < 0 and qty_signed < 0):
            # Adding to position
            new_qty = self.position + qty_signed
            self.avg_price = (
                (abs(self.position) * self.avg_price) + (abs(qty_signed) * price)
            ) / abs(new_qty)
            self.position = new_qty
        else:
            # Closing or flipping position
            closing_qty = min(abs(self.position), abs(qty_signed))
            if self.position > 0:
                self.realized_pnl += (price - self.avg_price) * closing_qty
            else:
                self.realized_pnl += (self.avg_price - price) * closing_qty

            self.position += qty_signed
            if self.position == 0:
                self.avg_price = 0.0
            else:
                self.avg_price = price

    def get_stats(self) -> dict:
        """Get strategy statistics."""
        total_passive = len(self.filled_orders) + len(self.active_orders)
        filled_passive = len(self.filled_orders)

        return {
            "market_orders": self.market_orders_placed,
            "market_fills": len(self.market_fills),
            "passive_placed": total_passive,
            "passive_filled": filled_passive,
            "passive_active": len(self.active_orders),
            "passive_fill_rate": (filled_passive / total_passive * 100) if total_passive > 0 else 0.0,
            "position": self.position,
            "avg_price": self.avg_price,
            "realized_pnl": self.realized_pnl,
        }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--parquet", required=True, help="Path to semantic parquet file")
    parser.add_argument("--max-events", type=int, default=500000, help="Max events to process")
    parser.add_argument("--progress-every", type=int, default=50000, help="Progress update interval")
    parser.add_argument("--queue-position", choices=["front", "middle", "back"], default="back", help="Queue position assumption")
    args = parser.parse_args()

    parquet_path = Path(args.parquet)

    print("[INFO] Loading events from:", parquet_path)
    events = load_events(parquet_path, args.max_events)
    print(f"[INFO] Loaded {len(events):,} events")
    print()

    # Initialize
    book = OrderBookEventBatchedStrict()
    fill_model = StrictFillModel(assume_queue_position=args.queue_position)
    strategy = SimpleStrategy(fill_model)

    stats = defaultdict(int)
    current_ts = None
    batch: list[tuple[MarketEvent, str]] = []

    def process_batch(batch_rows: list[tuple[MarketEvent, str]]) -> None:
        if not batch_rows:
            return

        stats["batches_seen"] += 1

        # Apply to book
        for evt, evt_name in batch_rows:
            book.apply_event(evt, evt_name)

        # Let strategy process
        strategy.on_event_batch(batch_rows, book)

        # Check for crossed book
        bid = book.best_bid()
        ask = book.best_ask()
        if bid is not None and ask is not None:
            stats["stable_snapshots"] += 1
            if bid >= ask:
                stats["crossed_after_batch"] += 1

    print("[INFO] Starting replay with fill simulation...")
    print()

    for idx, (evt, evt_name) in enumerate(events, start=1):
        if current_ts is None:
            current_ts = evt.ts_event_ns

        if evt.ts_event_ns != current_ts:
            process_batch(batch)
            batch = []
            current_ts = evt.ts_event_ns

            # Progress update
            if stats["batches_seen"] > 0 and stats["batches_seen"] % args.progress_every == 0:
                snap = book.snapshot(depth=3)
                strat_stats = strategy.get_stats()

                print(
                    f"[progress] "
                    f"rows={idx:,} "
                    f"batches={stats['batches_seen']:,} "
                    f"bid={snap['best_bid']} "
                    f"ask={snap['best_ask']} "
                    f"passive_filled={strat_stats['passive_filled']} "
                    f"pos={strat_stats['position']} "
                    f"rpnl=${strat_stats['realized_pnl']:.2f}"
                )

        batch.append((evt, evt_name))

    # Process final batch
    process_batch(batch)

    # Final results
    print()
    print("=" * 80)
    print("REPLAY COMPLETE - EXECUTION SUMMARY")
    print("=" * 80)
    print()

    book_stats = book.stats_dict()
    fill_stats = fill_model.stats_dict()
    strat_stats = strategy.get_stats()

    print("[BOOK STATS]")
    print(f"  Events Applied:      {book_stats['events_applied']:,}")
    print(f"  Batches Processed:   {stats['batches_seen']:,}")
    print(f"  Crossed Books:       {stats['crossed_after_batch']:,}")
    print(f"  Best Bid:            {book_stats['best_bid']}")
    print(f"  Best Ask:            {book_stats['best_ask']}")
    print(f"  Spread:              {book_stats['spread']}")
    print()

    print("[FILL MODEL STATS]")
    for key, val in fill_stats.items():
        print(f"  {key:25s}: {val}")
    print()

    print("[STRATEGY STATS]")
    for key, val in strat_stats.items():
        print(f"  {key:25s}: {val}")
    print()

    print("[MARKET FILL ANALYSIS]")
    if strategy.market_fills:
        total_slippage = sum(f.slippage_ticks for f in strategy.market_fills)
        avg_slippage = total_slippage / len(strategy.market_fills)
        max_slippage = max(f.slippage_ticks for f in strategy.market_fills)

        print(f"  Total Market Fills:  {len(strategy.market_fills)}")
        print(f"  Avg Slippage:        {avg_slippage:.2f} ticks")
        print(f"  Max Slippage:        {max_slippage:.2f} ticks")
        print(f"  Avg Levels Swept:    {sum(f.levels_consumed for f in strategy.market_fills) / len(strategy.market_fills):.1f}")
    else:
        print("  No market fills executed")
    print()

    print("[PASSIVE FILL ANALYSIS]")
    if strategy.filled_orders:
        avg_queue = sum(o.initial_queue_ahead for o in strategy.filled_orders) / len(strategy.filled_orders)
        avg_events = sum(o.cancel_events_seen + o.fill_events_seen for o in strategy.filled_orders) / len(strategy.filled_orders)

        print(f"  Total Filled:        {len(strategy.filled_orders)}")
        print(f"  Avg Initial Queue:   {avg_queue:.1f}")
        print(f"  Avg Events to Fill:  {avg_events:.1f}")
        print(f"  Fill Rate:           {strat_stats['passive_fill_rate']:.1f}%")
    else:
        print("  No passive fills yet")
    print()

    print("=" * 80)


if __name__ == "__main__":
    main()
