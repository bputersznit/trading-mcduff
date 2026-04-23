from __future__ import annotations

from dataclasses import dataclass, field
from typing import List

from cg_sim.models import SimOrder, FillEvent, PositionState


@dataclass
class ExecutionEngine:
    position: PositionState = field(default_factory=PositionState)
    fills: List[FillEvent] = field(default_factory=list)

    def submit_market_immediate(self, order: SimOrder, fill_price: float, fee: float = 0.0) -> FillEvent:
        fill = FillEvent(
            ts_fill_ns=order.ts_created_ns,
            side=order.side,
            qty=order.qty,
            price=fill_price,
            tag=order.tag,
            fee=fee,
        )
        self._apply_fill(fill)
        return fill

    def _apply_fill(self, fill: FillEvent) -> None:
        qty_signed = fill.qty if fill.side == "BUY" else -fill.qty

        if self.position.qty == 0:
            self.position.qty = qty_signed
            self.position.avg_price = fill.price
        elif (self.position.qty > 0 and qty_signed > 0) or (self.position.qty < 0 and qty_signed < 0):
            new_qty = self.position.qty + qty_signed
            self.position.avg_price = (
                (abs(self.position.qty) * self.position.avg_price) + (fill.qty * fill.price)
            ) / abs(new_qty)
            self.position.qty = new_qty
        else:
            closing_qty = min(abs(self.position.qty), fill.qty)
            if self.position.qty > 0:
                self.position.realized_pnl += (fill.price - self.position.avg_price) * closing_qty
            else:
                self.position.realized_pnl += (self.position.avg_price - fill.price) * closing_qty

            self.position.qty += qty_signed
            if self.position.qty == 0:
                self.position.avg_price = 0.0
            else:
                self.position.avg_price = fill.price

        self.position.realized_pnl -= fill.fee
        self.fills.append(fill)
