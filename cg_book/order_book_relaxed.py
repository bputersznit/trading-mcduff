from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

from sortedcontainers import SortedDict

from cg_sim.models import BookLevel, EventType, MarketEvent


@dataclass(slots=True)
class RelaxedBookStats:
    events_applied: int = 0
    add_events: int = 0
    cancel_events: int = 0
    modify_events: int = 0
    fill_events: int = 0
    aggressor_trade_events: int = 0
    clear_events: int = 0
    none_events: int = 0

    missing_level_refs: int = 0
    capped_decrements: int = 0
    ignored_trade_events: int = 0
    ignored_unknown_events: int = 0

    crossed_book_count: int = 0
    zero_removed_levels: int = 0


class OrderBookRelaxed:
    VALID_MODES = {"add_cancel_only", "add_cancel_modify", "full"}

    def __init__(self, mode: str = "full") -> None:
        if mode not in self.VALID_MODES:
            raise ValueError(f"Invalid mode: {mode}. Valid modes: {sorted(self.VALID_MODES)}")

        self.mode = mode
        self.bids: SortedDict[float, BookLevel] = SortedDict()
        self.asks: SortedDict[float, BookLevel] = SortedDict()
        self.stats = RelaxedBookStats()

    def _levels_for_side(self, side: str) -> SortedDict[float, BookLevel]:
        return self.bids if side in ("BID", "BUY") else self.asks

    def _normalize_side(self, side: Optional[str]) -> Optional[str]:
        if side is None:
            return None

        s = str(side).upper()
        if s in ("B", "BID", "BUY"):
            return "BID"
        if s in ("A", "ASK", "SELL"):
            return "ASK"
        return None

    def _get_level(self, side: str, price: float) -> Optional[BookLevel]:
        return self._levels_for_side(side).get(price)

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

    def _decrement_level_capped(
        self,
        side: str,
        price: float,
        dec_size: int,
        ts_ns: int,
        bucket: str,
    ) -> None:
        level = self._get_level(side, price)
        if level is None:
            self.stats.missing_level_refs += 1
            return

        before = level.total_size
        applied = min(max(dec_size, 0), max(before, 0))

        if dec_size > before:
            self.stats.capped_decrements += 1

        level.total_size -= applied
        level.last_update_ns = ts_ns

        if bucket == "cancel":
            level.canceled_size += applied
        elif bucket == "fill":
            level.traded_size += applied

        if level.total_size == 0:
            level.order_count = 0
        else:
            level.order_count = max(0, min(level.order_count, level.total_size))

        self._cleanup_level_if_empty(side, price)

    def _set_level_size_relaxed(self, side: str, price: float, size: int, ts_ns: int) -> None:
        level = self._get_or_create_level(side, price, ts_ns)
        new_size = max(0, size)

        if new_size > level.total_size:
            delta = new_size - level.total_size
            level.added_size += delta
        elif new_size < level.total_size:
            delta = level.total_size - new_size
            level.canceled_size += delta

        level.total_size = new_size
        level.order_count = 0 if new_size == 0 else max(1, min(level.order_count or 1, new_size))
        level.last_update_ns = ts_ns

        self._cleanup_level_if_empty(side, price)

    def _update_crossed_book_stat(self) -> None:
        bid = self.best_bid()
        ask = self.best_ask()
        if bid is not None and ask is not None and bid >= ask:
            self.stats.crossed_book_count += 1

    def clear_book(self) -> None:
        self.bids.clear()
        self.asks.clear()

    def apply_event(self, evt: MarketEvent) -> None:
        self.stats.events_applied += 1

        side = self._normalize_side(evt.side)
        price = evt.price
        size = evt.size if evt.size is not None else 0
        evt_name = evt.event_type.value.lower()

        if evt_name == "none":
            self.stats.none_events += 1
            return

        if evt_name == "clear":
            self.stats.clear_events += 1
            self.clear_book()
            return

        if side is None or price is None:
            self.stats.ignored_unknown_events += 1
            return

        if evt_name == "add":
            self.stats.add_events += 1
            self._apply_add(side, price, size, evt.ts_event_ns)

        elif evt_name == "cancel":
            self.stats.cancel_events += 1
            self._apply_cancel(side, price, size, evt.ts_event_ns)

        elif evt_name == "modify":
            self.stats.modify_events += 1
            if self.mode in ("add_cancel_modify", "full"):
                self._apply_modify(side, price, size, evt.ts_event_ns)

        elif evt_name == "fill_resting":
            self.stats.fill_events += 1
            if self.mode == "full":
                self._apply_fill(side, price, size, evt.ts_event_ns)
            else:
                self.stats.ignored_trade_events += 1

        elif evt_name == "trade_aggressor":
            self.stats.aggressor_trade_events += 1
            self.stats.ignored_trade_events += 1

        elif evt_name == "trade":
            # backward compatibility with old parquet files
            if self.mode == "full":
                self._apply_fill(side, price, size, evt.ts_event_ns)
            else:
                self.stats.ignored_trade_events += 1

        else:
            self.stats.ignored_unknown_events += 1

        self._update_crossed_book_stat()

    def _apply_add(self, side: str, price: float, size: int, ts_ns: int) -> None:
        add_size = max(0, size)
        if add_size == 0:
            return

        level = self._get_or_create_level(side, price, ts_ns)
        level.total_size += add_size
        level.added_size += add_size
        level.order_count = max(1, level.order_count + 1)
        level.last_update_ns = ts_ns

    def _apply_cancel(self, side: str, price: float, size: int, ts_ns: int) -> None:
        self._decrement_level_capped(side, price, max(0, size), ts_ns, "cancel")

    def _apply_modify(self, side: str, price: float, size: int, ts_ns: int) -> None:
        self._set_level_size_relaxed(side, price, size, ts_ns)

    def _apply_fill(self, side: str, price: float, size: int, ts_ns: int) -> None:
        self._decrement_level_capped(side, price, max(0, size), ts_ns, "fill")

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
            "mode": self.mode,
            "best_bid": self.best_bid(),
            "best_ask": self.best_ask(),
            "spread": self.spread(),
            "bids": [
                {
                    "price": p,
                    "size": lvl.total_size,
                    "orders": lvl.order_count,
                    "added": lvl.added_size,
                    "canceled": lvl.canceled_size,
                    "traded": lvl.traded_size,
                }
                for p, lvl in reversed(bid_items)
            ],
            "asks": [
                {
                    "price": p,
                    "size": lvl.total_size,
                    "orders": lvl.order_count,
                    "added": lvl.added_size,
                    "canceled": lvl.canceled_size,
                    "traded": lvl.traded_size,
                }
                for p, lvl in ask_items
            ],
        }

    def stats_dict(self) -> dict:
        return {
            "events_applied": self.stats.events_applied,
            "add_events": self.stats.add_events,
            "cancel_events": self.stats.cancel_events,
            "modify_events": self.stats.modify_events,
            "fill_events": self.stats.fill_events,
            "aggressor_trade_events": self.stats.aggressor_trade_events,
            "clear_events": self.stats.clear_events,
            "none_events": self.stats.none_events,
            "missing_level_refs": self.stats.missing_level_refs,
            "capped_decrements": self.stats.capped_decrements,
            "ignored_trade_events": self.stats.ignored_trade_events,
            "ignored_unknown_events": self.stats.ignored_unknown_events,
            "crossed_book_count": self.stats.crossed_book_count,
            "zero_removed_levels": self.stats.zero_removed_levels,
            "bid_levels": len(self.bids),
            "ask_levels": len(self.asks),
            "best_bid": self.best_bid(),
            "best_ask": self.best_ask(),
            "spread": self.spread(),
        }