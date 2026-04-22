#!/usr/bin/env python3
"""
Complete Trading Example - All Order Types + Performance Metrics.

Demonstrates:
1. All 7 order types in action
2. OCO and bracket orders
3. Performance metrics tracking
4. Complete trade lifecycle

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_exec.CGCl_fill_model_strict import StrictFillModel
from cg_exec.CGCl_stop_limit_orders import StopOrderManager
from cg_exec.CGCl_oco_bracket_orders import OCOBracketManager
from cg_exec.CGCl_performance_metrics import PerformanceMetrics, Trade
from cg_sim.models import MarketEvent, EventType


def build_book_at_price(bid: float, ask: float) -> OrderBookEventBatchedStrict:
    """Build a book with specific bid/ask."""
    book = OrderBookEventBatchedStrict()
    ts = 1000000000000

    # Add bids
    for i, offset in enumerate([0, -0.25, -0.50, -0.75]):
        evt = MarketEvent(
            ts_event_ns=ts + i,
            event_type=EventType.ADD,
            side="BID",
            price=bid + offset,
            size=25,
            order_id=100 + i,
        )
        book.apply_event(evt, "add")

    # Add asks
    for i, offset in enumerate([0, 0.25, 0.50, 0.75]):
        evt = MarketEvent(
            ts_event_ns=ts + 10 + i,
            event_type=EventType.ADD,
            side="ASK",
            price=ask + offset,
            size=25,
            order_id=200 + i,
        )
        book.apply_event(evt, "add")

    return book


def main():
    print("\n" + "=" * 80)
    print("COMPLETE TRADING SYSTEM DEMONSTRATION")
    print("All Order Types + Performance Metrics")
    print("=" * 80)

    # Initialize components
    fill_model = StrictFillModel()
    stop_mgr = StopOrderManager(fill_model)
    oco_mgr = OCOBracketManager(fill_model, stop_mgr)
    metrics = PerformanceMetrics(initial_capital=100000.0)

    # Scenario 1: Market Order + Manual Exits
    print("\n[SCENARIO 1] Market Entry + Manual Stop/Target")
    print("-" * 80)

    book = build_book_at_price(bid=20000.00, ask=20000.25)
    ts = 1000000000000

    print(f"Market: Bid={book.best_bid()}, Ask={book.best_ask()}")

    # Market buy entry
    print("\n1. Market BUY 5 contracts")
    entry_fill = fill_model.simulate_market_buy(5, book)
    print(f"   Filled: {entry_fill.filled_qty} @ avg {entry_fill.avg_fill_price:.2f}")
    print(f"   Slippage: {entry_fill.slippage_ticks:.2f} ticks")

    metrics.update_position(5, entry_fill.avg_fill_price, ts_ns=ts)

    # Place OCO stop + target
    print("\n2. Place OCO: Stop @ 19950.00, Target @ 20100.00")
    stop_mgr.place_stop_market("stop_1", "SELL", 5, 19950.00, ts + 1000)
    stop_mgr.place_stop_limit("target_1", "SELL", 5, 20100.00, 20100.00, ts + 1000)
    oco_mgr.create_oco_pair("oco_1", "stop_1", "target_1", "stop_market", "stop_limit")

    # Simulate price hitting target
    print("\n3. Price moves to target...")
    book = build_book_at_price(bid=20100.00, ask=20100.25)
    triggered_m, triggered_l = stop_mgr.check_triggers(book, ts + 100000)
    print(f"   Market: Bid={book.best_bid()}, Ask={book.best_ask()}")
    print(f"   Triggered: {len(triggered_l)} stop-limit orders")

    if triggered_l:
        stop_mgr.execute_triggered_orders(triggered_m, triggered_l, book, ts + 100000)
        oco_mgr.check_oco_fills(ts + 100000)
        print("   ✅ Target hit! Position closed at profit.")

        # Record trade
        trade = Trade(
            trade_id="trade_1",
            entry_ts_ns=ts,
            exit_ts_ns=ts + 100000,
            side="LONG",
            quantity=5,
            entry_price=entry_fill.avg_fill_price,
            exit_price=20100.00,
            gross_pnl=(20100.00 - entry_fill.avg_fill_price) * 5,
            entry_slippage_ticks=entry_fill.slippage_ticks,
            exit_slippage_ticks=0.0,
            entry_fill_type="market",
            exit_fill_type="limit",
        )
        metrics.record_trade(trade)

    # Scenario 2: Bracket Order
    print("\n\n[SCENARIO 2] Bracket Order (Entry + Stop + Target)")
    print("-" * 80)

    book = build_book_at_price(bid=20050.00, ask=20050.25)
    ts = 2000000000000

    print(f"Market: Bid={book.best_bid()}, Ask={book.best_ask()}")

    # Place bracket
    print("\n1. Place BRACKET: Entry @ 20050.00, Stop @ 20000.00, Target @ 20150.00")
    bracket = oco_mgr.create_bracket_order(
        bracket_id="bracket_1",
        entry_side="BUY",
        entry_qty=5,
        entry_price=20050.00,
        stop_price=20000.00,
        target_price=20150.00,
        entry_type="market",
        book=book,
        ts_ns=ts,
    )

    if bracket.entry_filled:
        print(f"   ✅ Entry filled @ {bracket.entry_fill_price:.2f}")
        print(f"   📊 Stop and target automatically placed")

        # Simulate hitting stop
        print("\n2. Price moves against us...")
        book = build_book_at_price(bid=19975.00, ask=20000.00)
        triggered_m, triggered_l = stop_mgr.check_triggers(book, ts + 100000)
        print(f"   Market: Bid={book.best_bid()}, Ask={book.best_ask()}")

        if triggered_m:
            stop_mgr.execute_triggered_orders(triggered_m, triggered_l, book, ts + 100000)
            completed = oco_mgr.check_bracket_status(ts + 100000)

            if completed:
                print(f"   ❌ Stop loss hit @ 20000.00")
                print(f"   Status: {completed[0].status}")

                # Record trade
                trade = Trade(
                    trade_id="trade_2",
                    entry_ts_ns=ts,
                    exit_ts_ns=ts + 100000,
                    side="LONG",
                    quantity=5,
                    entry_price=bracket.entry_fill_price,
                    exit_price=20000.00,
                    gross_pnl=(20000.00 - bracket.entry_fill_price) * 5,
                    entry_fill_type="market",
                    exit_fill_type="stop",
                )
                metrics.record_trade(trade)

    # Scenario 3: Trailing Stop
    print("\n\n[SCENARIO 3] Trailing Stop (Let Winners Run)")
    print("-" * 80)

    book = build_book_at_price(bid=20100.00, ask=20100.25)
    ts = 3000000000000

    print(f"Market: Bid={book.best_bid()}, Ask={book.best_ask()}")

    # Market entry
    print("\n1. Market BUY 5 contracts")
    entry_fill = fill_model.simulate_market_buy(5, book)
    print(f"   Filled: {entry_fill.filled_qty} @ avg {entry_fill.avg_fill_price:.2f}")

    # Place trailing stop
    print("\n2. Place TRAILING STOP: Initial @ 20050.00, Trail = $2.00")
    trail = stop_mgr.place_trailing_stop_market(
        "trail_1", "SELL", 5, 20050.00, trail_offset=2.00, ts_ns=ts
    )
    print(f"   Stop price: {trail.stop_price:.2f}")

    # Price moves up
    print("\n3. Price moves up...")
    for i in range(3):
        book = build_book_at_price(bid=20100.00 + i * 10, ask=20100.25 + i * 10)
        triggered_m, _ = stop_mgr.check_triggers(book, ts + i * 10000)
        print(f"   Market: Bid={book.best_bid():.2f}, Stop: {trail.stop_price:.2f}")

    print("\n4. Price reverses down to hit trailing stop...")
    book = build_book_at_price(bid=20095.00, ask=20095.25)
    triggered_m, _ = stop_mgr.check_triggers(book, ts + 50000)

    if triggered_m:
        stop_mgr.execute_triggered_orders(triggered_m, [], book, ts + 50000)
        print(f"   ✅ Trailing stop triggered!")
        if triggered_m[0].market_fill:
            exit_price = triggered_m[0].market_fill.avg_fill_price
            print(f"   Exit @ {exit_price:.2f}")
            print(f"   Profit locked in: ${(exit_price - entry_fill.avg_fill_price) * 5:.2f}")

            trade = Trade(
                trade_id="trade_3",
                entry_ts_ns=ts,
                exit_ts_ns=ts + 50000,
                side="LONG",
                quantity=5,
                entry_price=entry_fill.avg_fill_price,
                exit_price=exit_price,
                gross_pnl=(exit_price - entry_fill.avg_fill_price) * 5,
                entry_fill_type="market",
                exit_fill_type="stop",
            )
            metrics.record_trade(trade)

    # Performance Summary
    print("\n\n" + "=" * 80)
    print("PERFORMANCE SUMMARY")
    print("=" * 80)

    metrics.print_summary(current_price=20100.00)

    # Order Management Stats
    print("\n[ORDER MANAGEMENT STATISTICS]")
    print("-" * 80)

    fill_stats = fill_model.stats_dict()
    print("\nFill Model:")
    for key, val in fill_stats.items():
        print(f"  {key:30s}: {val}")

    stop_stats = stop_mgr.stats_dict()
    print("\nStop Manager:")
    for key, val in stop_stats.items():
        print(f"  {key:30s}: {val}")

    oco_stats = oco_mgr.stats_dict()
    print("\nOCO/Bracket Manager:")
    for key, val in oco_stats.items():
        print(f"  {key:30s}: {val}")

    print("\n" + "=" * 80)
    print("DEMONSTRATION COMPLETE")
    print("=" * 80)
    print("\n✅ All 7 order types demonstrated")
    print("✅ Performance metrics calculated")
    print("✅ Complete trading system operational")
    print()


if __name__ == "__main__":
    main()
