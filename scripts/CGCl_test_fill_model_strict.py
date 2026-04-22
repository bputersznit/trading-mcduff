#!/usr/bin/env python3
"""
Test script for StrictFillModel with OrderBookEventBatchedStrict.

Tests:
1. Market order sweeping through book levels
2. Passive order placement and queue tracking
3. Queue advancement through cancel/fill events
4. Fill simulation with slippage limits
5. Integration with event-batched strict book

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_exec.CGCl_fill_model_strict import StrictFillModel
from cg_sim.models import MarketEvent, EventType


def print_section(title: str) -> None:
    """Print a section header."""
    print()
    print("=" * 80)
    print(f"  {title}")
    print("=" * 80)


def print_book_snapshot(book: OrderBookEventBatchedStrict, depth: int = 5) -> None:
    """Print a book snapshot."""
    snap = book.snapshot(depth=depth)

    print(f"\n  Best Bid: {snap['best_bid']}")
    print(f"  Best Ask: {snap['best_ask']}")
    print(f"  Spread:   {snap['spread']}")
    print(f"  Active Orders: {snap['active_orders']}")

    print("\n  Top Bids:")
    for level in snap["bids"][:3]:
        print(f"    {level['price']:>10.2f}  |  {level['size']:>6} qty  |  {level['orders']:>3} orders")

    print("\n  Top Asks:")
    for level in snap["asks"][:3]:
        print(f"    {level['price']:>10.2f}  |  {level['size']:>6} qty  |  {level['orders']:>3} orders")


def build_synthetic_book() -> OrderBookEventBatchedStrict:
    """Build a synthetic order book for testing."""
    book = OrderBookEventBatchedStrict()

    # Build bid side
    orders = [
        (1, "BID", 20000.00, 10, 100),
        (2, "BID", 20000.00, 15, 101),
        (3, "BID", 19999.75, 20, 102),
        (4, "BID", 19999.50, 25, 103),
        (5, "BID", 19999.25, 30, 104),
    ]

    # Build ask side
    orders += [
        (6, "ASK", 20000.25, 8, 105),
        (7, "ASK", 20000.25, 12, 106),
        (8, "ASK", 20000.50, 15, 107),
        (9, "ASK", 20000.75, 20, 108),
        (10, "ASK", 20001.00, 25, 109),
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


def test_market_orders():
    """Test 1: Market order sweeping."""
    print_section("TEST 1: Market Order Sweeping")

    book = build_synthetic_book()
    fill_model = StrictFillModel()

    print_book_snapshot(book)

    # Test market buy
    print("\n  [TEST] Market BUY 15 contracts")
    buy_fill = fill_model.simulate_market_buy(15, book)

    print(f"\n  Result:")
    print(f"    Requested:     {buy_fill.requested_qty}")
    print(f"    Filled:        {buy_fill.filled_qty}")
    print(f"    Remaining:     {buy_fill.remaining_qty}")
    print(f"    Avg Price:     {buy_fill.avg_fill_price:.2f}" if buy_fill.avg_fill_price else "    Avg Price:     N/A")
    print(f"    Best Price:    {buy_fill.best_fill_price:.2f}" if buy_fill.best_fill_price else "    Best Price:    N/A")
    print(f"    Worst Price:   {buy_fill.worst_fill_price:.2f}" if buy_fill.worst_fill_price else "    Worst Price:   N/A")
    print(f"    Levels:        {buy_fill.levels_consumed}")
    print(f"    Fully Filled:  {buy_fill.fully_filled}")
    print(f"    Slippage:      {buy_fill.slippage_ticks:.2f} ticks")

    # Test market sell
    print("\n  [TEST] Market SELL 40 contracts")
    sell_fill = fill_model.simulate_market_sell(40, book)

    print(f"\n  Result:")
    print(f"    Requested:     {sell_fill.requested_qty}")
    print(f"    Filled:        {sell_fill.filled_qty}")
    print(f"    Remaining:     {sell_fill.remaining_qty}")
    print(f"    Avg Price:     {sell_fill.avg_fill_price:.2f}" if sell_fill.avg_fill_price else "    Avg Price:     N/A")
    print(f"    Best Price:    {sell_fill.best_fill_price:.2f}" if sell_fill.best_fill_price else "    Best Price:    N/A")
    print(f"    Worst Price:   {sell_fill.worst_fill_price:.2f}" if sell_fill.worst_fill_price else "    Worst Price:   N/A")
    print(f"    Levels:        {sell_fill.levels_consumed}")
    print(f"    Fully Filled:  {sell_fill.fully_filled}")
    print(f"    Slippage:      {sell_fill.slippage_ticks:.2f} ticks")

    # Test with slippage limit
    print("\n  [TEST] Market BUY 50 contracts with 2-tick slippage limit")
    limited_fill = fill_model.simulate_market_buy(50, book, max_slippage_ticks=2.0)

    print(f"\n  Result:")
    print(f"    Requested:     {limited_fill.requested_qty}")
    print(f"    Filled:        {limited_fill.filled_qty}")
    print(f"    Remaining:     {limited_fill.remaining_qty}")
    print(f"    Fully Filled:  {limited_fill.fully_filled}")
    print(f"    Slippage:      {limited_fill.slippage_ticks:.2f} ticks")


def test_passive_orders():
    """Test 2: Passive order placement and queue tracking."""
    print_section("TEST 2: Passive Order Placement")

    book = build_synthetic_book()
    fill_model = StrictFillModel(assume_queue_position="back")

    print_book_snapshot(book)

    # Place passive buy at best bid
    print("\n  [TEST] Place passive BUY limit at 20000.00 (best bid)")
    passive_buy = fill_model.place_passive_limit(
        side="BUY",
        price=20000.00,
        qty=5,
        book=book,
        ts_ns=2000000000000,
        tag="test_buy",
    )

    print(f"\n  Result:")
    print(f"    Side:          {passive_buy.side}")
    print(f"    Price:         {passive_buy.price:.2f}")
    print(f"    Quantity:      {passive_buy.qty}")
    print(f"    Initial Queue: {passive_buy.initial_queue_ahead}")
    print(f"    Current Queue: {passive_buy.current_queue_ahead}")
    print(f"    Active:        {passive_buy.active}")

    # Place passive sell at best ask
    print("\n  [TEST] Place passive SELL limit at 20000.25 (best ask)")
    passive_sell = fill_model.place_passive_limit(
        side="SELL",
        price=20000.25,
        qty=8,
        book=book,
        ts_ns=2000000000001,
        tag="test_sell",
    )

    print(f"\n  Result:")
    print(f"    Side:          {passive_sell.side}")
    print(f"    Price:         {passive_sell.price:.2f}")
    print(f"    Quantity:      {passive_sell.qty}")
    print(f"    Initial Queue: {passive_sell.initial_queue_ahead}")
    print(f"    Current Queue: {passive_sell.current_queue_ahead}")
    print(f"    Active:        {passive_sell.active}")


def test_queue_advancement():
    """Test 3: Queue advancement through book events."""
    print_section("TEST 3: Queue Advancement Through Events")

    book = build_synthetic_book()
    fill_model = StrictFillModel(assume_queue_position="back")

    print_book_snapshot(book)

    # Place passive buy
    passive_order = fill_model.place_passive_limit(
        side="BUY",
        price=20000.00,
        qty=5,
        book=book,
        ts_ns=2000000000000,
        tag="queue_test",
    )

    print(f"\n  [INITIAL] Passive BUY at 20000.00")
    print(f"    Queue Ahead:   {passive_order.current_queue_ahead}")
    print(f"    Filled:        {passive_order.filled_qty}/{passive_order.qty}")

    # Simulate cancel event ahead in queue
    print("\n  [EVENT 1] Cancel 10 contracts at 20000.00 (BID)")
    cancel_evt = MarketEvent(
        ts_event_ns=2000000000001,
        event_type=EventType.CANCEL,
        side="BID",
        price=20000.00,
        size=10,
        order_id=999,
    )
    passive_order = fill_model.advance_passive_order(passive_order, cancel_evt, "cancel")

    print(f"    Queue Ahead:   {passive_order.current_queue_ahead} (reduced by cancel)")
    print(f"    Filled:        {passive_order.filled_qty}/{passive_order.qty}")
    print(f"    Events Seen:   {passive_order.cancel_events_seen} cancels")

    # Simulate another cancel
    print("\n  [EVENT 2] Cancel 8 contracts at 20000.00 (BID)")
    cancel_evt2 = MarketEvent(
        ts_event_ns=2000000000002,
        event_type=EventType.CANCEL,
        side="BID",
        price=20000.00,
        size=8,
        order_id=1000,
    )
    passive_order = fill_model.advance_passive_order(passive_order, cancel_evt2, "cancel")

    print(f"    Queue Ahead:   {passive_order.current_queue_ahead}")
    print(f"    Filled:        {passive_order.filled_qty}/{passive_order.qty}")

    # Simulate fill event that reaches our order
    print("\n  [EVENT 3] Fill (resting) 10 contracts at 20000.00 (BID)")
    fill_evt = MarketEvent(
        ts_event_ns=2000000000003,
        event_type=EventType.TRADE,
        side="BID",
        price=20000.00,
        size=10,
        order_id=1001,
    )
    passive_order = fill_model.advance_passive_order(passive_order, fill_evt, "fill_resting")

    print(f"    Queue Ahead:   {passive_order.current_queue_ahead}")
    print(f"    Filled:        {passive_order.filled_qty}/{passive_order.qty}")
    print(f"    Fill %:        {passive_order.fill_percentage:.1f}%")
    print(f"    Active:        {passive_order.active}")
    print(f"    Is Filled:     {passive_order.is_filled}")


def test_integration():
    """Test 4: Full integration with book and events."""
    print_section("TEST 4: Full Integration Test")

    book = build_synthetic_book()
    fill_model = StrictFillModel(assume_queue_position="middle")

    print_book_snapshot(book)

    # Place multiple passive orders
    print("\n  [SETUP] Placing 3 passive orders")

    orders = [
        fill_model.place_passive_limit("BUY", 20000.00, 3, book, 3000000000000, "order_1"),
        fill_model.place_passive_limit("BUY", 19999.75, 5, book, 3000000000001, "order_2"),
        fill_model.place_passive_limit("SELL", 20000.25, 4, book, 3000000000002, "order_3"),
    ]

    for idx, order in enumerate(orders, 1):
        print(f"    Order {idx}: {order.side} {order.qty} @ {order.price:.2f}, queue={order.current_queue_ahead}")

    # Simulate some market activity
    events = [
        (MarketEvent(ts_event_ns=3000000000010, event_type=EventType.CANCEL, side="BID", price=20000.00, size=5, order_id=9001), "cancel"),
        (MarketEvent(ts_event_ns=3000000000011, event_type=EventType.TRADE, side="BID", price=20000.00, size=8, order_id=9002), "fill_resting"),
        (MarketEvent(ts_event_ns=3000000000012, event_type=EventType.CANCEL, side="ASK", price=20000.25, size=6, order_id=9003), "cancel"),
        (MarketEvent(ts_event_ns=3000000000013, event_type=EventType.TRADE, side="ASK", price=20000.25, size=10, order_id=9004), "fill_resting"),
    ]

    print("\n  [SIMULATION] Processing market events")

    for evt, evt_type in events:
        print(f"\n    Event: {evt_type} {evt.size} @ {evt.price:.2f} ({evt.side})")

        # Advance all orders
        for idx, order in enumerate(orders):
            orders[idx] = fill_model.advance_passive_order(order, evt, evt_type)

        # Show status of each order
        for idx, order in enumerate(orders, 1):
            if order.price == evt.price and ((order.side == "BUY" and evt.side == "BID") or (order.side == "SELL" and evt.side == "ASK")):
                print(f"      Order {idx}: queue={order.current_queue_ahead}, filled={order.filled_qty}/{order.qty}")

    # Final status
    print("\n  [FINAL STATUS]")
    for idx, order in enumerate(orders, 1):
        print(f"    Order {idx}: {order.side} {order.qty} @ {order.price:.2f}")
        print(f"      Filled:      {order.filled_qty}/{order.qty} ({order.fill_percentage:.1f}%)")
        print(f"      Queue:       {order.current_queue_ahead}")
        print(f"      Active:      {order.active}")
        print(f"      Events Seen: {order.cancel_events_seen} cancels, {order.fill_events_seen} fills")

    # Show fill model stats
    print("\n  [FILL MODEL STATS]")
    stats = fill_model.stats_dict()
    for key, value in stats.items():
        print(f"    {key}: {value}")


def main():
    """Run all tests."""
    print()
    print("╔" + "=" * 78 + "╗")
    print("║" + " " * 20 + "STRICT FILL MODEL TEST SUITE" + " " * 30 + "║")
    print("╚" + "=" * 78 + "╝")

    test_market_orders()
    test_passive_orders()
    test_queue_advancement()
    test_integration()

    print()
    print("=" * 80)
    print("  ALL TESTS COMPLETE")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
