from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, Optional
from sortedcontainers import SortedDict

from cg_sim.models import MarketEvent, EventType, BookLevel


@dataclass(slots=True)
class OrderState:
    order_id: int
    side: str
    price: float
    size: int
    ts_event_ns: int


class OrderBook:
    def __init__(self) -> None:
        self.bids: SortedDict[float, BookLevel] = SortedDict()
        self.asks: SortedDict[float, BookLevel] = SortedDict()
        self.orders: Dict[int, OrderState] = {}

    def _levels_for_side(self, side: str) -> SortedDict:
        return self.bids if side in ("BID", "BUY") else self.asks

    def _get_or_create_level(self, levels: SortedDict, price: float, ts_ns: int) -> BookLevel:
        level = levels.get(price)
        if level is None:
            level = BookLevel(price=price, last_update_ns=ts_ns)
            levels[price] = level
        return level

    def _cleanup_level_if_empty(self, levels: SortedDict, price: float) -> None:
        level = levels.get(price)
        if level is not None and level.total_size <= 0 and level.order_count <= 0:
            del levels[price]

    def apply_event(self, evt: MarketEvent) -> None:
        if evt.event_type == EventType.ADD:
            self._apply_add(evt)
        elif evt.event_type == EventType.CANCEL:
            self._apply_cancel(evt)
        elif evt.event_type == EventType.MODIFY:
            self._apply_modify(evt)
        elif evt.event_type == EventType.TRADE:
            self._apply_trade(evt)

    def _apply_add(self, evt: MarketEvent) -> None:
        if evt.order_id is None or evt.side is None or evt.price is None or evt.size is None:
            return
        levels = self._levels_for_side(evt.side)
        level = self._get_or_create_level(levels, evt.price, evt.ts_event_ns)
        level.total_size += evt.size
        level.order_count += 1
        level.added_size += evt.size
        level.last_update_ns = evt.ts_event_ns
        self.orders[evt.order_id] = OrderState(
            order_id=evt.order_id, side=evt.side, price=evt.price, size=evt.size, ts_event_ns=evt.ts_event_ns
        )

    def _apply_cancel(self, evt: MarketEvent) -> None:
        if evt.order_id is None:
            return
        order = self.orders.get(evt.order_id)
        if order is None:
            return
        cancel_size = evt.size if evt.size is not None else order.size
        cancel_size = min(cancel_size, order.size)
        levels = self._levels_for_side(order.side)
        level = levels.get(order.price)
        if level is not None:
            level.total_size -= cancel_size
            level.canceled_size += cancel_size
            level.last_update_ns = evt.ts_event_ns
        order.size -= cancel_size
        if order.size <= 0:
            if level is not None:
                level.order_count -= 1
            self.orders.pop(evt.order_id, None)
        self._cleanup_level_if_empty(levels, order.price)

    def _apply_modify(self, evt: MarketEvent) -> None:
        if evt.order_id is None:
            return
        order = self.orders.get(evt.order_id)
        if order is None:
            return

        old_levels = self._levels_for_side(order.side)
        old_level = old_levels.get(order.price)
        if old_level is not None:
            old_level.total_size -= order.size
            old_level.order_count -= 1
            old_level.last_update_ns = evt.ts_event_ns
            self._cleanup_level_if_empty(old_levels, order.price)

        new_side = evt.side or order.side
        new_price = evt.price if evt.price is not None else order.price
        new_size = evt.size if evt.size is not None else order.size

        new_levels = self._levels_for_side(new_side)
        new_level = self._get_or_create_level(new_levels, new_price, evt.ts_event_ns)
        new_level.total_size += new_size
        new_level.order_count += 1
        new_level.added_size += new_size
        new_level.last_update_ns = evt.ts_event_ns

        self.orders[evt.order_id] = OrderState(
            order_id=order.order_id, side=new_side, price=new_price, size=new_size, ts_event_ns=evt.ts_event_ns
        )

    def _apply_trade(self, evt: MarketEvent) -> None:
        if evt.price is None or evt.side is None or evt.size is None:
            return
        levels = self._levels_for_side(evt.side)
        level = levels.get(evt.price)
        if level is not None:
            level.traded_size += evt.size
            level.total_size -= evt.size
            level.last_update_ns = evt.ts_event_ns
            self._cleanup_level_if_empty(levels, evt.price)

        if evt.order_id is not None:
            order = self.orders.get(evt.order_id)
            if order is not None:
                order.size -= evt.size
                if order.size <= 0:
                    if level is not None:
                        level.order_count -= 1
                    self.orders.pop(evt.order_id, None)
                self._cleanup_level_if_empty(levels, evt.price)

    def best_bid(self) -> Optional[float]:
        return self.bids.peekitem(-1)[0] if self.bids else None

    def best_ask(self) -> Optional[float]:
        return self.asks.peekitem(0)[0] if self.asks else None

    def spread(self) -> Optional[float]:
        bid = self.best_bid()
        ask = self.best_ask()
        if bid is None or ask is None:
            return None
        return ask - bid

    def snapshot(self, depth: int = 10) -> dict:
        bid_items = list(self.bids.items())[-depth:]
        ask_items = list(self.asks.items())[:depth]
        return {
            "best_bid": self.best_bid(),
            "best_ask": self.best_ask(),
            "spread": self.spread(),
            "bids": [{"price": p, "size": lvl.total_size, "orders": lvl.order_count} for p, lvl in reversed(bid_items)],
            "asks": [{"price": p, "size": lvl.total_size, "orders": lvl.order_count} for p, lvl in ask_items],
        }
