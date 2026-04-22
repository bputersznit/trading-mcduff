#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

from cg_book.order_book import OrderBook
from cg_exec.fill_model import FillModel
from cg_sim.models import EventType, MarketEvent


def add_level(book: OrderBook, side: str, price: float, size: int, order_id: int):
    evt = MarketEvent(
        ts_event_ns=0,
        event_type=EventType.ADD,
        side=side,
        price=price,
        size=size,
        order_id=order_id,
    )
    book.apply_event(evt)


def main():
    book = OrderBook()

    # Build a small synthetic book
    add_level(book, "BID", 20000.00, 10, 1)
    add_level(book, "BID", 19999.75, 15, 2)
    add_level(book, "BID", 19999.50, 25, 3)

    add_level(book, "ASK", 20000.25, 8, 4)
    add_level(book, "ASK", 20000.50, 12, 5)
    add_level(book, "ASK", 20000.75, 20, 6)

    print("[info] Initial book snapshot")
    print(book.snapshot(depth=5))
    print()

    fill_model = FillModel()

    buy_fill = fill_model.simulate_market_buy(qty=18, book=book)
    print("[info] Market buy result")
    print(buy_fill)
    print()

    sell_fill = fill_model.simulate_market_sell(qty=22, book=book)
    print("[info] Market sell result")
    print(sell_fill)
    print()

    passive_buy = fill_model.place_limit_order(
        side="BUY",
        price=20000.00,
        qty=5,
        book=book,
        tag="test_buy_limit",
    )

    print("[info] Passive order after placement")
    print(passive_buy)
    print()

    passive_buy = fill_model.advance_limit_order_queue(
        order=passive_buy,
        traded_qty_at_level=7,
    )

    print("[info] Passive order after 7 traded ahead")
    print(passive_buy)
    print()

    passive_buy = fill_model.advance_limit_order_queue(
        order=passive_buy,
        traded_qty_at_level=10,
    )

    print("[info] Passive order after another 10 traded")
    print(passive_buy)
    print()


if __name__ == "__main__":
    main()
