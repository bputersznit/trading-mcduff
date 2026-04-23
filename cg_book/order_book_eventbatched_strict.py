from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

from sortedcontainers import SortedDict

from cg_sim.models import BookLevel, MarketEvent


@dataclass(slots=True)
class RestingOrder:
    order_id: int
    side: str
    price: float
    size: int
    ts_last_ns: int


@dataclass(slots=True)
class StrictBookStats:
    events_applied: int = 0

    add_events: int = 0
    modify_events: int = 0
    cancel_events: int = 0
    fill_events: int = 0
    aggressor_trade_events: int = 0
    clear_events: int = 0
    none_events: int = 0

    unknown_modify_refs: int = 0
    unknown_cancel_refs: int = 0
    unknown_fill_refs: int = 0
    missing_order_id: int = 0

    capped_order_decrements: int = 0
    crossed_book_count: int = 0
    zero_removed_levels: int = 0
    deleted_orders: int = 0


class OrderBookEventBatchedStrict:
    def __init__(self) -> None:
        self.bids: SortedDict[float, BookLevel] = SortedDict()
        self.asks: SortedDict[float, BookLevel] = SortedDict()
        self.orders: dict[int, RestingOrder] = {}
        self.stats = StrictBookStats()

    def _normalize_side(self, side: Optional[str]) -> Optional[str]:
        if side is None:
            return None

        s = str(side).upper()

        if s in ("B", "BID", "BUY"):
            return "BID"

        if s in ("A", "ASK", "SELL"):
            return "ASK"

        return None

    def _levels_for_side(self, side: str) -> SortedDict:
        return self.bids if side == "BID" else self.asks

    def _get_or_create_level(self, side: str, price: float, ts_ns: int) -> BookLevel:
        levels = self._levels_for_side(side)
        level = levels.get(price)

        if level is None:
            level = BookLevel(price=price, last_update_ns=ts_ns)
            levels[price] = level

        return level

    def _cleanup_level_if_empty(self, side: str, price: float) -> None:
        levels = self._levels_for_side(side)
        level = levels.get(price)

        if level is not None and level.total_size <= 0:
            del levels[price]
            self.stats.zero_removed_levels += 1

    def _increment_level(self, side: str, price: float, qty: int, ts_ns: int) -> None:
        if qty <= 0:
            return

        level = self._get_or_create_level(side, price, ts_ns)
        level.total_size += qty
        level.added_size += qty
        level.order_count += 1
        level.last_update_ns = ts_ns

    def _decrement_level(
        self,
        side: str,
        price: float,
        qty: int,
        ts_ns: int,
        traded: bool = False,
    ) -> None:
        if qty <= 0:
            return

        levels = self._levels_for_side(side)
        level = levels.get(price)

        if level is None:
            return

        applied = min(qty, max(level.total_size, 0))

        if qty > level.total_size:
            self.stats.capped_order_decrements += 1

        level.total_size -= applied

        if traded:
            level.traded_size += applied
        else:
            level.canceled_size += applied

        if level.total_size <= 0:
            level.total_size = 0
            level.order_count = 0
        else:
            level.order_count = max(0, level.order_count - 1)

        level.last_update_ns = ts_ns
        self._cleanup_level_if_empty(side, price)

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

    def clear(self) -> None:
        self.bids.clear()
        self.asks.clear()
        self.orders.clear()

    def _update_crossed_stat(self) -> None:
        bid = self.best_bid()
        ask = self.best_ask()

        if bid is not None and ask is not None and bid >= ask:
            self.stats.crossed_book_count += 1

    def apply_event(self, evt: MarketEvent, event_type_name: str) -> None:
        self.stats.events_applied += 1

        side = self._normalize_side(evt.side)
        order_id = evt.order_id
        price = evt.price
        size = evt.size if evt.size is not None else 0
        ts_ns = evt.ts_event_ns

        if event_type_name == "none":
            self.stats.none_events += 1
            return

        if event_type_name == "clear":
            self.stats.clear_events += 1
            self.clear()
            return

        if order_id is None:
            self.stats.missing_order_id += 1
            return

        if event_type_name == "trade_aggressor":
            self.stats.aggressor_trade_events += 1
            return

        if side is None or price is None:
            return

        if event_type_name == "add":
            self.stats.add_events += 1
            self._apply_add(order_id, side, price, size, ts_ns)

        elif event_type_name == "modify":
            self.stats.modify_events += 1
            self._apply_modify(order_id, side, price, size, ts_ns)

        elif event_type_name == "cancel":
            self.stats.cancel_events += 1
            self._apply_cancel(order_id, size, ts_ns)

        elif event_type_name == "fill_resting":
            self.stats.fill_events += 1
            self._apply_fill(order_id, size, ts_ns)

        self._update_crossed_stat()

    def _apply_add(
        self,
        order_id: int,
        side: str,
        price: float,
        size: int,
        ts_ns: int,
    ) -> None:
        if size <= 0:
            return

        if order_id in self.orders:
            self._apply_modify(order_id, side, price, size, ts_ns)
            return

        self.orders[order_id] = RestingOrder(
            order_id=order_id,
            side=side,
            price=price,
            size=size,
            ts_last_ns=ts_ns,
        )

        self._increment_level(side, price, size, ts_ns)

    def _apply_modify(
        self,
        order_id: int,
        side: str,
        price: float,
        size: int,
        ts_ns: int,
    ) -> None:
        old = self.orders.get(order_id)

        if old is None:
            self.stats.unknown_modify_refs += 1
            self._apply_add(order_id, side, price, size, ts_ns)
            return

        self._decrement_level(old.side, old.price, old.size, ts_ns, traded=False)

        new_size = max(size, 0)

        if new_size <= 0:
            del self.orders[order_id]
            self.stats.deleted_orders += 1
            return

        self.orders[order_id] = RestingOrder(
            order_id=order_id,
            side=side,
            price=price,
            size=new_size,
            ts_last_ns=ts_ns,
        )

        self._increment_level(side, price, new_size, ts_ns)

    def _apply_cancel(self, order_id: int, cancel_size: int, ts_ns: int) -> None:
        old = self.orders.get(order_id)

        if old is None:
            self.stats.unknown_cancel_refs += 1
            return

        applied = min(cancel_size, old.size)

        self._decrement_level(old.side, old.price, applied, ts_ns, traded=False)

        remaining = old.size - applied

        if remaining <= 0:
            del self.orders[order_id]
            self.stats.deleted_orders += 1
        else:
            old.size = remaining
            old.ts_last_ns = ts_ns
            self.orders[order_id] = old

    def _apply_fill(self, order_id: int, fill_size: int, ts_ns: int) -> None:
        old = self.orders.get(order_id)

        if old is None:
            self.stats.unknown_fill_refs += 1
            return

        applied = min(fill_size, old.size)

        self._decrement_level(old.side, old.price, applied, ts_ns, traded=True)

        remaining = old.size - applied

        if remaining <= 0:
            del self.orders[order_id]
            self.stats.deleted_orders += 1
        else:
            old.size = remaining
            old.ts_last_ns = ts_ns
            self.orders[order_id] = old

    def snapshot(self, depth: int = 10) -> dict:
        bid_items = list(self.bids.items())[-depth:]
        ask_items = list(self.asks.items())[:depth]

        return {
            "best_bid": self.best_bid(),
            "best_ask": self.best_ask(),
            "spread": self.spread(),
            "active_orders": len(self.orders),
            "bids": [
                {
                    "price": p,
                    "size": lvl.total_size,
                    "orders": lvl.order_count,
                }
                for p, lvl in reversed(bid_items)
            ],
            "asks": [
                {
                    "price": p,
                    "size": lvl.total_size,
                    "orders": lvl.order_count,
                }
                for p, lvl in ask_items
            ],
        }

    def stats_dict(self) -> dict:
        return {
            "events_applied": self.stats.events_applied,
            "add_events": self.stats.add_events,
            "modify_events": self.stats.modify_events,
            "cancel_events": self.stats.cancel_events,
            "fill_events": self.stats.fill_events,
            "aggressor_trade_events": self.stats.aggressor_trade_events,
            "clear_events": self.stats.clear_events,
            "none_events": self.stats.none_events,
            "unknown_modify_refs": self.stats.unknown_modify_refs,
            "unknown_cancel_refs": self.stats.unknown_cancel_refs,
            "unknown_fill_refs": self.stats.unknown_fill_refs,
            "missing_order_id": self.stats.missing_order_id,
            "capped_order_decrements": self.stats.capped_order_decrements,
            "crossed_book_count": self.stats.crossed_book_count,
            "zero_removed_levels": self.stats.zero_removed_levels,
            "deleted_orders": self.stats.deleted_orders,
            "active_orders": len(self.orders),
            "bid_levels": len(self.bids),
            "ask_levels": len(self.asks),
            "best_bid": self.best_bid(),
            "best_ask": self.best_ask(),
            "spread": self.spread(),
        }