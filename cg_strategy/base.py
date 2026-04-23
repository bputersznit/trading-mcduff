from __future__ import annotations

from abc import ABC, abstractmethod

from cg_sim.models import FillEvent, MarketEvent


class StrategyContext(ABC):
    @abstractmethod
    def best_bid(self) -> float | None:
        pass

    @abstractmethod
    def best_ask(self) -> float | None:
        pass

    @abstractmethod
    def position_qty(self) -> int:
        pass

    @abstractmethod
    def submit_market(self, side: str, qty: int, tag: str = "") -> None:
        pass


class BaseStrategy(ABC):
    @abstractmethod
    def on_market_event(self, evt: MarketEvent, ctx: StrategyContext) -> None:
        pass

    def on_fill(self, fill: FillEvent, ctx: StrategyContext) -> None:
        pass

    def on_timer(self, ts_ns: int, ctx: StrategyContext) -> None:
        pass
