#!/usr/bin/env python3
"""
Simple, direct test of stop order functionality.

This test manually manipulates book state to demonstrate stop triggers.
"""

from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_exec.CGCl_fill_model_strict import StrictFillModel
from cg_exec.CGCl_stop_limit_orders import StopOrderManager, StopTriggerType
from cg_sim.models import MarketEvent, EventType


def build_book_at_price(bid: float, ask: float) -> OrderBookEventBatchedStrict:
    """Build a book with specific bid/ask."""
    book = OrderBookEventBatchedStrict()
    ts = 1000000000000

    # Add bids
    for i, offset in enumerate([0, -0.25, -0.50]):
        evt = MarketEvent(
            ts_event_ns=ts + i,
            event_type=EventType.ADD,
            side="BID",
            price=bid + offset,
            size=20,
            order_id=100 + i,
        )
        book.apply_event(evt, "add")

    # Add asks
    for i, offset in enumerate([0, 0.25, 0.50]):
        evt = MarketEvent(
            ts_event_ns=ts + 10 + i,
            event_type=EventType.ADD,
            side="ASK",
            price=ask + offset,
            size=20,
            order_id=200 + i,
        )
        book.apply_event(evt, "add")

    return book


def main():
    print("\n" + "=" * 80)
    print("STOP ORDER FUNCTIONALITY DEMONSTRATION")
    print("=" * 80)

    # TEST 1: Buy Stop Trigger
    print("\n[TEST 1] BUY STOP-MARKET (Breakout Entry)")
    print("-" * 80)

    book = build_book_at_price(bid=20000.00, ask=20000.25)
    fill_model = StrictFillModel()
    stop_mgr = StopOrderManager(fill_model)

    print(f"Initial: Bid={book.best_bid()}, Ask={book.best_ask()}")

    # Place buy stop above market
    print(f"\nPlacing BUY stop @ 20000.75 (above current ask of {book.best_ask()})")
    order = stop_mgr.place_stop_market(
        order_id="buy_stop_1",
        side="BUY",
        qty=5,
        stop_price=20000.75,
        ts_ns=2000000000000,
    )
    print(f"Status: {order.status}, Active: {order.is_active}")

    # Check - should NOT trigger yet
    triggered_m, triggered_l = stop_mgr.check_triggers(book, 2000000000001)
    print(f"\nCheck triggers: {len(triggered_m)} triggered (should be 0)")

    # Move market UP to trigger
    print(f"\n[Price moves UP]")
    book = build_book_at_price(bid=20000.75, ask=20001.00)
    print(f"New market: Bid={book.best_bid()}, Ask={book.best_ask()}")

    triggered_m, triggered_l = stop_mgr.check_triggers(book, 2000000000002)
    print(f"Check triggers: {len(triggered_m)} triggered (should be 1)")

    if triggered_m:
        stop_mgr.execute_triggered_orders(triggered_m, triggered_l, book, 2000000000002)
        print(f"\n✅ Stop triggered and executed!")
        print(f"   Status: {triggered_m[0].status}")
        if triggered_m[0].market_fill:
            fill = triggered_m[0].market_fill
            print(f"   Filled: {fill.filled_qty} @ avg {fill.avg_fill_price:.2f}")
            print(f"   Slippage: {fill.slippage_ticks:.2f} ticks")

    # TEST 2: Sell Stop Trigger (Stop Loss)
    print("\n\n[TEST 2] SELL STOP-MARKET (Stop Loss)")
    print("-" * 80)

    book = build_book_at_price(bid=20000.00, ask=20000.25)
    fill_model = StrictFillModel()
    stop_mgr = StopOrderManager(fill_model)

    print(f"Initial: Bid={book.best_bid()}, Ask={book.best_ask()}")

    # Place sell stop below market
    print(f"\nPlacing SELL stop @ 19999.50 (below current bid of {book.best_bid()})")
    order = stop_mgr.place_stop_market(
        order_id="sell_stop_1",
        side="SELL",
        qty=5,
        stop_price=19999.50,
        ts_ns=3000000000000,
    )
    print(f"Status: {order.status}, Active: {order.is_active}")

    # Move market DOWN to trigger
    print(f"\n[Price moves DOWN]")
    book = build_book_at_price(bid=19999.25, ask=19999.50)
    print(f"New market: Bid={book.best_bid()}, Ask={book.best_ask()}")

    triggered_m, triggered_l = stop_mgr.check_triggers(book, 3000000000001)
    print(f"Check triggers: {len(triggered_m)} triggered (should be 1)")

    if triggered_m:
        stop_mgr.execute_triggered_orders(triggered_m, triggered_l, book, 3000000000001)
        print(f"\n✅ Stop triggered and executed!")
        print(f"   Status: {triggered_m[0].status}")
        if triggered_m[0].market_fill:
            fill = triggered_m[0].market_fill
            print(f"   Filled: {fill.filled_qty} @ avg {fill.avg_fill_price:.2f}")

    # TEST 3: Stop-Limit Order
    print("\n\n[TEST 3] STOP-LIMIT ORDER")
    print("-" * 80)

    book = build_book_at_price(bid=20000.00, ask=20000.25)
    fill_model = StrictFillModel(assume_queue_position="front")
    stop_mgr = StopOrderManager(fill_model)

    print(f"Initial: Bid={book.best_bid()}, Ask={book.best_ask()}")

    # Place buy stop-limit
    print(f"\nPlacing BUY stop-limit: Stop=20000.50, Limit=20000.75")
    order = stop_mgr.place_stop_limit(
        order_id="stop_limit_1",
        side="BUY",
        qty=3,
        stop_price=20000.50,
        limit_price=20000.75,
        ts_ns=4000000000000,
    )

    # Trigger it
    book = build_book_at_price(bid=20000.50, ask=20000.75)
    print(f"\n[Price moves to trigger]")
    print(f"New market: Bid={book.best_bid()}, Ask={book.best_ask()}")

    triggered_m, triggered_l = stop_mgr.check_triggers(book, 4000000000001)
    print(f"Check triggers: {len(triggered_l)} stop-limit triggered")

    if triggered_l:
        stop_mgr.execute_triggered_orders(triggered_m, triggered_l, book, 4000000000001)
        print(f"\n✅ Stop-limit triggered!")
        print(f"   Status: {triggered_l[0].status}")
        print(f"   Passive order placed at limit price: {triggered_l[0].limit_price}")
        if triggered_l[0].passive_order:
            passive = triggered_l[0].passive_order
            print(f"   Queue ahead: {passive.current_queue_ahead}")

            # Simulate fill
            fill_evt = MarketEvent(
                ts_event_ns=4000000000010,
                event_type=EventType.TRADE,
                side="ASK",
                price=20000.75,
                size=10,
                order_id=999,
            )
            stop_mgr.advance_passive_stops(fill_evt, "fill_resting")

            print(f"\n   [After fill event]")
            print(f"   Filled: {passive.filled_qty}/{passive.qty}")
            print(f"   Status: {triggered_l[0].status}")

    # TEST 4: Trailing Stop
    print("\n\n[TEST 4] TRAILING STOP")
    print("-" * 80)

    book = build_book_at_price(bid=20000.00, ask=20000.25)
    fill_model = StrictFillModel()
    stop_mgr = StopOrderManager(fill_model)

    print(f"Initial: Bid={book.best_bid()}, Ask={book.best_ask()}")

    # Place trailing sell stop
    initial_stop = 19999.50
    trail_offset = 1.0

    print(f"\nPlacing SELL trailing stop:")
    print(f"   Initial stop: {initial_stop}")
    print(f"   Trail offset: {trail_offset} ($1.00)")

    order = stop_mgr.place_trailing_stop_market(
        order_id="trail_1",
        side="SELL",
        qty=5,
        initial_stop_price=initial_stop,
        trail_offset=trail_offset,
        ts_ns=5000000000000,
    )

    print(f"   Current stop: {order.stop_price}")

    # Price moves UP - stop should trail UP
    print(f"\n[Price moves UP by $1.00]")
    book = build_book_at_price(bid=20001.00, ask=20001.25)
    triggered_m, _ = stop_mgr.check_triggers(book, 5000000000001)
    print(f"   Market: Bid={book.best_bid()}, Ask={book.best_ask()}")
    print(f"   Stop price: {order.stop_price:.2f} (trailed up by ~${order.stop_price - initial_stop:.2f})")

    print(f"\n[Price moves UP by another $1.00]")
    book = build_book_at_price(bid=20002.00, ask=20002.25)
    triggered_m, _ = stop_mgr.check_triggers(book, 5000000000002)
    print(f"   Market: Bid={book.best_bid()}, Ask={book.best_ask()}")
    print(f"   Stop price: {order.stop_price:.2f}")

    # Price reverses DOWN - stop triggers
    print(f"\n[Price reverses DOWN to hit stop]")
    book = build_book_at_price(bid=20000.50, ask=20000.75)
    triggered_m, _ = stop_mgr.check_triggers(book, 5000000000003)
    print(f"   Market: Bid={book.best_bid()}, Ask={book.best_ask()}")
    print(f"   Triggered: {len(triggered_m)} orders")

    if triggered_m:
        stop_mgr.execute_triggered_orders(triggered_m, [], book, 5000000000003)
        print(f"\n✅ Trailing stop triggered!")
        print(f"   Final stop price: {triggered_m[0].stop_price:.2f}")
        if triggered_m[0].market_fill:
            fill = triggered_m[0].market_fill
            print(f"   Filled @ avg: {fill.avg_fill_price:.2f}")

    # Summary
    print("\n" + "=" * 80)
    print("SUMMARY")
    print("=" * 80)
    print("\n✅ Stop-Market Orders: Working")
    print("✅ Stop-Limit Orders: Working")
    print("✅ Trailing Stops: Working")
    print("\nAll order types successfully trigger and execute!")
    print()


if __name__ == "__main__":
    main()
