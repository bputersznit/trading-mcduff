from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable, Optional

import simpy

from cg_book.order_book import OrderBook
from cg_exec.execution import ExecutionEngine
from cg_sim.models import MarketEvent, SimOrder
from cg_strategy.base import StrategyContext, BaseStrategy


@dataclass
class LatencyModel:
    feed_latency_ns: int = 0
    strategy_latency_ns: int = 0
    order_send_latency_ns: int = 0


class ReplayContext(StrategyContext):
    def __init__(self, book: OrderBook, exec_engine: ExecutionEngine, now_ns_fn):
        self.book = book
        self.exec_engine = exec_engine
        self._now_ns_fn = now_ns_fn

    def best_bid(self) -> float | None:
        return self.book.best_bid()

    def best_ask(self) -> float | None:
        return self.book.best_ask()

    def position_qty(self) -> int:
        return self.exec_engine.position.qty

    def submit_market(self, side: str, qty: int, tag: str = "") -> None:
        px = self.book.best_ask() if side == "BUY" else self.book.best_bid()
        if px is None:
            return

        order = SimOrder(
            ts_created_ns=self._now_ns_fn(),
            side=side,
            order_type="MARKET",
            qty=qty,
            tag=tag,
        )
        self.exec_engine.submit_market_immediate(order, fill_price=px, fee=0.0)


class ReplayEngine:
    def __init__(
        self,
        events: Iterable[MarketEvent],
        strategy: BaseStrategy,
        latency: Optional[LatencyModel] = None,
    ) -> None:
        self.events = list(events)
        self.strategy = strategy
        self.latency = latency or LatencyModel()
        self.env = simpy.Environment()
        self.book = OrderBook()
        self.exec_engine = ExecutionEngine()
        self.current_ts_ns = 0
        self.ctx = ReplayContext(self.book, self.exec_engine, self.now_ns)

    def now_ns(self) -> int:
        return self.current_ts_ns

    def run(self) -> None:
        self.env.process(self._feed())
        self.env.run()

    def _feed(self):
        last_ts = None

        for evt in self.events:
            delta_ns = 0 if last_ts is None else max(0, evt.ts_event_ns - last_ts)
            yield self.env.timeout(delta_ns + self.latency.feed_latency_ns)

            self.current_ts_ns = evt.ts_event_ns
            self.book.apply_event(evt)
            self.strategy.on_market_event(evt, self.ctx)

            last_ts = evt.ts_event_ns
