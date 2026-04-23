from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

from cg_book.order_book import OrderBook


@dataclass(slots=True)
class SimulatedFill:
    side: str
    requested_qty: int
    filled_qty: int
    remaining_qty: int
    avg_fill_price: float | None
    worst_fill_price: float | None
    best_fill_price: float | None
    levels_consumed: int
    fully_filled: bool


@dataclass(slots=True)
class PassiveLimitOrder:
    side: str
    price: float
    qty: int
    queue_ahead: int = 0
    filled_qty: int = 0
    active: bool = True
    tag: str = ""


class FillModel:
    """
    First-pass fill model.

    Assumptions:
    - Market orders sweep visible ladder depth.
    - Passive orders rest at a price level with queue position.
    - Queue position advances only from traded volume.
    """

    def simulate_market_buy(self, qty: int, book: OrderBook) -> SimulatedFill:
        remaining = qty
        total_cost = 0.0
        filled = 0
        levels_consumed = 0
        best_fill = None
        worst_fill = None

        for price, level in book.asks.items():
            available = max(0, level.total_size)

            if available <= 0:
                continue

            take = min(remaining, available)

            if take > 0:
                total_cost += take * price
                remaining -= take
                filled += take
                levels_consumed += 1

                if best_fill is None:
                    best_fill = price

                worst_fill = price

            if remaining <= 0:
                break

        avg_fill = total_cost / filled if filled > 0 else None

        return SimulatedFill(
            side="BUY",
            requested_qty=qty,
            filled_qty=filled,
            remaining_qty=remaining,
            avg_fill_price=avg_fill,
            worst_fill_price=worst_fill,
            best_fill_price=best_fill,
            levels_consumed=levels_consumed,
            fully_filled=(remaining == 0),
        )

    def simulate_market_sell(self, qty: int, book: OrderBook) -> SimulatedFill:
        remaining = qty
        total_value = 0.0
        filled = 0
        levels_consumed = 0
        best_fill = None
        worst_fill = None

        bid_levels = list(book.bids.items())[::-1]

        for price, level in bid_levels:
            available = max(0, level.total_size)

            if available <= 0:
                continue

            take = min(remaining, available)

            if take > 0:
                total_value += take * price
                remaining -= take
                filled += take
                levels_consumed += 1

                if best_fill is None:
                    best_fill = price

                worst_fill = price

            if remaining <= 0:
                break

        avg_fill = total_value / filled if filled > 0 else None

        return SimulatedFill(
            side="SELL",
            requested_qty=qty,
            filled_qty=filled,
            remaining_qty=remaining,
            avg_fill_price=avg_fill,
            worst_fill_price=worst_fill,
            best_fill_price=best_fill,
            levels_consumed=levels_consumed,
            fully_filled=(remaining == 0),
        )

    def place_limit_order(
        self,
        side: str,
        price: float,
        qty: int,
        book: OrderBook,
        tag: str = "",
    ) -> PassiveLimitOrder:
        if side.upper() == "BUY":
            level = book.bids.get(price)
        else:
            level = book.asks.get(price)

        queue_ahead = level.total_size if level is not None else 0

        return PassiveLimitOrder(
            side=side.upper(),
            price=price,
            qty=qty,
            queue_ahead=queue_ahead,
            tag=tag,
        )

    def advance_limit_order_queue(
        self,
        order: PassiveLimitOrder,
        traded_qty_at_level: int,
    ) -> PassiveLimitOrder:
        if not order.active:
            return order

        remaining_trade = traded_qty_at_level

        if order.queue_ahead > 0:
            reduction = min(order.queue_ahead, remaining_trade)
            order.queue_ahead -= reduction
            remaining_trade -= reduction

        if remaining_trade > 0:
            available_for_fill = order.qty - order.filled_qty
            fill_now = min(available_for_fill, remaining_trade)
            order.filled_qty += fill_now

        if order.filled_qty >= order.qty:
            order.active = False

        return order
