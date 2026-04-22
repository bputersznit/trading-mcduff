#!/usr/bin/env python3
"""
Test script for Stop and Stop-Limit order functionality.

Tests:
1. Stop-market buy/sell orders
2. Stop-limit buy/sell orders
3. Trailing stops
4. Order triggering and execution
5. Integration with strict fill model

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_exec.CGCl_fill_model_strict import StrictFillModel
from cg_exec.CGCl_stop_limit_orders import (
    StopOrderManager,
    StopTriggerType,
    StopOrderStatus,
)
from cg_sim.models import MarketEvent, EventType


def print_section(title: str) -> None:
    print()
    print("=" * 80)
    print(f"  {title}")
    print("=" * 80)


def build_book() -> OrderBookEventBatchedStrict:
    """Build a synthetic book for testing."""
    book = OrderBookEventBatchedStrict()

    orders = [
        (1, "BID", 20000.00, 25, 100),
        (2, "BID", 19999.75, 20, 101),
        (3, "BID", 19999.50, 25, 102),
        (4, "ASK", 20000.25, 20, 103),
        (5, "ASK", 20000.50, 15, 104),
        (6, "ASK", 20000.75, 20, 105),
    ]

    ts = 1000000000000

    for idx, (order_id, side, price, size, event_id) in enumerate(orders):
        evt = MarketEvent(
            ts_event_ns=ts + idx,
            event_type=EventType.ADD,
            side=side,
            price=price,
            size=size,
            order_id=order_id,
        )
        book.apply_event(evt, "add")

    return book


def simulate_price_movement(
    book: OrderBookEventBatchedStrict,
    direction: str,
    ticks: int,
    ts_start: int,
) -> None:
    """Simulate price movement by adding/removing levels."""
    tick_size = 0.25
    current_best_bid = book.best_bid()
    current_best_ask = book.best_ask()

    if direction == "up":
        # Price moving up: add higher asks, remove lower bids
        for i in range(ticks):
            new_ask_price = current_best_ask + (i + 1) * tick_size
            evt = MarketEvent(
                ts_event_ns=ts_start + i * 1000,
                event_type=EventType.ADD,
                side="ASK",
                price=new_ask_price,
                size=10,
                order_id=10000 + i,
            )
            book.apply_event(evt, "add")

    elif direction == "down":
        # Price moving down: add lower bids, remove higher asks
        for i in range(ticks):
            new_bid_price = current_best_bid - (i + 1) * tick_size
            evt = MarketEvent(
                ts_event_ns=ts_start + i * 1000,
                event_type=EventType.ADD,
                side="BID",
                price=new_bid_price,
                size=10,
                order_id=10000 + i,
            )
            book.apply_event(evt, "add")


def test_stop_market_buy():
    """Test 1: Stop-market buy order."""
    print_section("TEST 1: Stop-Market BUY Order")

    book = build_book()
    fill_model = StrictFillModel()
    stop_mgr = StopOrderManager(fill_model)

    print(f"\n  Initial Book:")
    print(f"    Best Bid: {book.best_bid()}")
    print(f"    Best Ask: {book.best_ask()}")

    # Place buy stop above current market (breakout entry)
    stop_price = 20000.50
    print(f"\n  [ACTION] Place BUY stop-market @ {stop_price}")
    print(f"           (Triggers when price rises to/above {stop_price})")

    order = stop_mgr.place_stop_market(
        order_id="stop_buy_1",
        side="BUY",
        qty=5,
        stop_price=stop_price,
        ts_ns=2000000000000,
        tag="breakout_entry",
    )

    print(f"\n  Order Status: {order.status}")
    print(f"  Is Active:    {order.is_active}")

    # Check triggers (should not trigger yet)
    print(f"\n  [CHECK] Checking triggers...")
    triggered_markets, triggered_limits = stop_mgr.check_triggers(book, 2000000000001)
    print(f"  Triggered:    {len(triggered_markets)} stop-market orders")

    # Simulate price moving up to trigger
    print(f"\n  [SIMULATE] Price moving UP to trigger stop...")
    simulate_price_movement(book, "up", 2, 2000000000002)

    print(f"    New Best Bid: {book.best_bid()}")
    print(f"    New Best Ask: {book.best_ask()}")

    # Check triggers again
    print(f"\n  [CHECK] Checking triggers again...")
    triggered_markets, triggered_limits = stop_mgr.check_triggers(book, 2000000000010)
    print(f"  Triggered:    {len(triggered_markets)} stop-market orders")

    if triggered_markets:
        print(f"\n  [EXECUTE] Executing triggered orders...")
        stop_mgr.execute_triggered_orders(
            triggered_markets, triggered_limits, book, 2000000000010
        )

        order = triggered_markets[0]
        print(f"\n  Order Status:   {order.status}")
        print(f"  Trigger Time:   {order.ts_triggered_ns}")

        if order.market_fill:
            print(f"\n  Fill Details:")
            print(f"    Filled Qty:     {order.market_fill.filled_qty}")
            print(f"    Avg Price:      {order.market_fill.avg_fill_price:.2f}")
            print(f"    Slippage:       {order.market_fill.slippage_ticks:.2f} ticks")
            print(f"    Levels:         {order.market_fill.levels_consumed}")


def test_stop_market_sell():
    """Test 2: Stop-market sell order (stop loss)."""
    print_section("TEST 2: Stop-Market SELL Order (Stop Loss)")

    book = build_book()
    fill_model = StrictFillModel()
    stop_mgr = StopOrderManager(fill_model)

    print(f"\n  Initial Book:")
    print(f"    Best Bid: {book.best_bid()}")
    print(f"    Best Ask: {book.best_ask()}")

    # Place sell stop below current market (stop loss on long)
    stop_price = 19999.75
    print(f"\n  [ACTION] Place SELL stop-market @ {stop_price}")
    print(f"           (Triggers when price falls to/below {stop_price})")

    order = stop_mgr.place_stop_market(
        order_id="stop_sell_1",
        side="SELL",
        qty=5,
        stop_price=stop_price,
        ts_ns=2000000000000,
        tag="stop_loss",
    )

    print(f"\n  Order Status: {order.status}")

    # Simulate price moving down to trigger
    print(f"\n  [SIMULATE] Price moving DOWN to trigger stop...")
    simulate_price_movement(book, "down", 3, 2000000000002)

    print(f"    New Best Bid: {book.best_bid()}")
    print(f"    New Best Ask: {book.best_ask()}")

    # Check and execute triggers
    triggered_markets, triggered_limits = stop_mgr.check_triggers(book, 2000000000010)
    print(f"\n  [CHECK] Triggered: {len(triggered_markets)} stop-market orders")

    if triggered_markets:
        stop_mgr.execute_triggered_orders(
            triggered_markets, triggered_limits, book, 2000000000010
        )

        order = triggered_markets[0]
        print(f"\n  Order Status: {order.status}")

        if order.market_fill:
            print(f"\n  Fill Details:")
            print(f"    Filled Qty:     {order.market_fill.filled_qty}")
            print(f"    Avg Price:      {order.market_fill.avg_fill_price:.2f}")
            print(f"    Slippage:       {order.market_fill.slippage_ticks:.2f} ticks")


def test_stop_limit():
    """Test 3: Stop-limit order."""
    print_section("TEST 3: Stop-Limit Order")

    book = build_book()
    fill_model = StrictFillModel()
    stop_mgr = StopOrderManager(fill_model)

    print(f"\n  Initial Book:")
    print(f"    Best Bid: {book.best_bid()}")
    print(f"    Best Ask: {book.best_ask()}")

    # Place buy stop-limit (trigger at 20000.50, limit at 20000.75)
    stop_price = 20000.50
    limit_price = 20000.75

    print(f"\n  [ACTION] Place BUY stop-limit")
    print(f"           Stop Price:  {stop_price}")
    print(f"           Limit Price: {limit_price}")

    order = stop_mgr.place_stop_limit(
        order_id="stop_limit_1",
        side="BUY",
        qty=3,
        stop_price=stop_price,
        limit_price=limit_price,
        ts_ns=2000000000000,
        tag="stop_limit_entry",
    )

    print(f"\n  Order Status: {order.status}")

    # Simulate price moving up to trigger
    print(f"\n  [SIMULATE] Price moving UP to trigger stop...")
    simulate_price_movement(book, "up", 2, 2000000000002)

    print(f"    New Best Bid: {book.best_bid()}")
    print(f"    New Best Ask: {book.best_ask()}")

    # Check and execute triggers
    triggered_markets, triggered_limits = stop_mgr.check_triggers(book, 2000000000010)
    print(f"\n  [CHECK] Triggered: {len(triggered_limits)} stop-limit orders")

    if triggered_limits:
        stop_mgr.execute_triggered_orders(
            triggered_markets, triggered_limits, book, 2000000000010
        )

        order = triggered_limits[0]
        print(f"\n  Order Status:     {order.status}")
        print(f"  Passive Order:    Placed at {order.limit_price}")

        if order.passive_order:
            print(f"\n  Passive Order Details:")
            print(f"    Queue Ahead:      {order.passive_order.current_queue_ahead}")
            print(f"    Active:           {order.passive_order.active}")

    # Simulate some fills to advance the passive order
    print(f"\n  [SIMULATE] Simulating fills at limit price...")
    fill_evt = MarketEvent(
        ts_event_ns=2000000000020,
        event_type=EventType.TRADE,
        side="ASK",  # This should be ASK for buy orders
        price=limit_price,
        size=10,
        order_id=99999,
    )

    stop_mgr.advance_passive_stops(fill_evt, "fill_resting")

    if order.passive_order:
        print(f"    Queue Ahead:      {order.passive_order.current_queue_ahead}")
        print(f"    Filled:           {order.passive_order.filled_qty}/{order.passive_order.qty}")
        print(f"    Status:           {order.status}")


def test_trailing_stop():
    """Test 4: Trailing stop."""
    print_section("TEST 4: Trailing Stop")

    book = build_book()
    fill_model = StrictFillModel()
    stop_mgr = StopOrderManager(fill_model)

    print(f"\n  Initial Book:")
    print(f"    Best Bid: {book.best_bid()}")
    print(f"    Best Ask: {book.best_ask()}")

    # Place trailing sell stop (protect long position)
    initial_stop = 19999.50
    trail_offset = 1.0  # 1 point = 4 ticks

    print(f"\n  [ACTION] Place SELL trailing stop")
    print(f"           Initial Stop: {initial_stop}")
    print(f"           Trail Offset: {trail_offset}")

    order = stop_mgr.place_trailing_stop_market(
        order_id="trail_stop_1",
        side="SELL",
        qty=5,
        initial_stop_price=initial_stop,
        trail_offset=trail_offset,
        ts_ns=2000000000000,
        tag="trailing_stop_loss",
    )

    print(f"\n  Order Status:     {order.status}")
    print(f"  Is Trailing:      {order.is_trailing}")
    print(f"  Current Stop:     {order.stop_price}")

    # Simulate price moving up (stop should trail up)
    print(f"\n  [SIMULATE] Price moving UP (stop should trail up)...")

    for i in range(5):
        simulate_price_movement(book, "up", 1, 2000000000000 + i * 10000)
        triggered_markets, _ = stop_mgr.check_triggers(book, 2000000000000 + i * 10000)

        print(f"\n    Step {i+1}:")
        print(f"      Best Bid:       {book.best_bid()}")
        print(f"      Best Ask:       {book.best_ask()}")
        print(f"      Stop Price:     {order.stop_price:.2f}")
        print(f"      Triggered:      {len(triggered_markets) > 0}")

    # Now simulate price falling to trigger the stop
    print(f"\n  [SIMULATE] Price moving DOWN to trigger stop...")
    simulate_price_movement(book, "down", 8, 2000000000100)

    print(f"    New Best Bid: {book.best_bid()}")
    print(f"    New Best Ask: {book.best_ask()}")

    triggered_markets, _ = stop_mgr.check_triggers(book, 2000000000110)
    print(f"\n  [CHECK] Triggered: {len(triggered_markets)} trailing stops")

    if triggered_markets:
        stop_mgr.execute_triggered_orders(triggered_markets, [], book, 2000000000110)
        order = triggered_markets[0]
        print(f"\n  Order Status:     {order.status}")
        print(f"  Final Stop Price: {order.stop_price:.2f}")
        if order.market_fill:
            print(f"  Filled At:        {order.market_fill.avg_fill_price:.2f}")


def test_integration():
    """Test 5: Full integration."""
    print_section("TEST 5: Full Integration")

    book = build_book()
    fill_model = StrictFillModel()
    stop_mgr = StopOrderManager(fill_model)

    print(f"\n  Initial Book:")
    print(f"    Best Bid: {book.best_bid()}")
    print(f"    Best Ask: {book.best_ask()}")

    # Place multiple stop orders
    print(f"\n  [SETUP] Placing multiple stop orders...")

    orders = [
        stop_mgr.place_stop_market("sm1", "BUY", 3, 20000.75, 3000000000000, tag="breakout"),
        stop_mgr.place_stop_market("sm2", "SELL", 5, 19999.50, 3000000000001, tag="stop_loss"),
        stop_mgr.place_stop_limit("sl1", "BUY", 2, 20000.50, 20000.75, 3000000000002, tag="limit_entry"),
        stop_mgr.place_trailing_stop_market("tm1", "SELL", 4, 19999.75, 0.75, 3000000000003, tag="trail"),
    ]

    print(f"    Placed {len(orders)} stop orders")

    active = stop_mgr.get_active_stops()
    print(f"\n  Active stops:")
    for key, val in active.items():
        print(f"    {key}: {val}")

    # Simulate price volatility
    print(f"\n  [SIMULATE] Price volatility...")

    # Move up
    simulate_price_movement(book, "up", 3, 3000000000010)
    triggered_m, triggered_l = stop_mgr.check_triggers(book, 3000000000020)
    stop_mgr.execute_triggered_orders(triggered_m, triggered_l, book, 3000000000020)

    print(f"    Price up:   triggered {len(triggered_m)} markets, {len(triggered_l)} limits")

    # Move down
    simulate_price_movement(book, "down", 5, 3000000000030)
    triggered_m, triggered_l = stop_mgr.check_triggers(book, 3000000000040)
    stop_mgr.execute_triggered_orders(triggered_m, triggered_l, book, 3000000000040)

    print(f"    Price down: triggered {len(triggered_m)} markets, {len(triggered_l)} limits")

    # Show final stats
    print(f"\n  [FINAL STATS]")
    stats = stop_mgr.stats_dict()
    for key, val in stats.items():
        print(f"    {key:30s}: {val}")


def main():
    print()
    print("╔" + "=" * 78 + "╗")
    print("║" + " " * 18 + "STOP & STOP-LIMIT ORDER TEST SUITE" + " " * 26 + "║")
    print("╚" + "=" * 78 + "╝")

    test_stop_market_buy()
    test_stop_market_sell()
    test_stop_limit()
    test_trailing_stop()
    test_integration()

    print()
    print("=" * 80)
    print("  ALL TESTS COMPLETE")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
