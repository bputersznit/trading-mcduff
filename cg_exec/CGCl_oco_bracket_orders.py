"""
OCO (One-Cancels-Other) and Bracket Order System.

Provides:
1. OCO pairs - Two orders where filling one cancels the other
2. Bracket orders - Entry + stop loss + profit target as a group
3. Order groups - Manage related orders together
4. Automatic cancellation logic

Common use cases:
- Stop + Limit target (exit long position)
- Buy stop + Buy limit (breakout or pullback entry)
- Full bracket: Entry + Stop + Target

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Literal, Optional, Union

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_exec.CGCl_fill_model_strict import StrictFillModel, StrictPassiveOrder
from cg_exec.CGCl_stop_limit_orders import StopOrderManager, StopMarketOrder, StopLimitOrder


class OCOStatus(str, Enum):
    """Status of OCO group."""
    ACTIVE = "active"           # Both orders active
    FILLED = "filled"           # One order filled, other cancelled
    CANCELLED = "cancelled"     # Both orders cancelled
    PARTIAL = "partial"         # One order partially filled


class BracketStatus(str, Enum):
    """Status of bracket order."""
    PENDING = "pending"         # Entry not filled yet
    ACTIVE = "active"           # Entry filled, exits active
    STOPPED = "stopped"         # Stop loss hit
    TARGET_HIT = "target_hit"   # Profit target hit
    PARTIAL = "partial"         # Partially closed
    CANCELLED = "cancelled"     # Bracket cancelled


@dataclass(slots=True)
class OCOPair:
    """
    One-Cancels-Other order pair.

    When one order fills, the other is automatically cancelled.
    Common for exit management: stop loss + profit target.
    """

    # Identification
    oco_id: str
    tag: str = ""

    # Orders (can be any combination)
    order_a_id: str = ""
    order_b_id: str = ""

    order_a_type: str = "unknown"  # "stop_market", "stop_limit", "passive_limit"
    order_b_type: str = "unknown"

    # Status
    status: OCOStatus = OCOStatus.ACTIVE
    filled_order_id: Optional[str] = None
    cancelled_order_id: Optional[str] = None
    ts_filled_ns: Optional[int] = None

    @property
    def is_active(self) -> bool:
        return self.status == OCOStatus.ACTIVE


@dataclass(slots=True)
class BracketOrder:
    """
    Bracket order = Entry + Stop Loss + Profit Target.

    Automatically places stop and target when entry fills.
    When stop or target fills, cancels the other.
    """

    # Identification
    bracket_id: str
    tag: str = ""

    # Entry order
    entry_order_id: str = ""
    entry_type: str = "unknown"  # "market", "limit", "stop_market", "stop_limit"
    entry_side: Literal["BUY", "SELL"] = "BUY"
    entry_qty: int = 0
    entry_filled: bool = False
    entry_fill_price: Optional[float] = None

    # Stop loss
    stop_order_id: str = ""
    stop_price: float = 0.0
    stop_filled: bool = False

    # Profit target
    target_order_id: str = ""
    target_price: float = 0.0
    target_filled: bool = False

    # OCO for exits
    exit_oco_id: Optional[str] = None

    # Status
    status: BracketStatus = BracketStatus.PENDING
    ts_entry_filled_ns: Optional[int] = None
    ts_exit_filled_ns: Optional[int] = None

    @property
    def is_active(self) -> bool:
        return self.status == BracketStatus.ACTIVE

    @property
    def exit_side(self) -> Literal["BUY", "SELL"]:
        """Opposite of entry side."""
        return "SELL" if self.entry_side == "BUY" else "BUY"


@dataclass
class OCOBracketManager:
    """
    Manages OCO pairs and bracket orders.

    Responsibilities:
    - Track OCO relationships
    - Auto-cancel when one leg fills
    - Manage bracket order lifecycle
    - Coordinate with stop and fill managers
    """

    fill_model: StrictFillModel
    stop_manager: StopOrderManager

    # Order tracking
    oco_pairs: list[OCOPair] = field(default_factory=list)
    brackets: list[BracketOrder] = field(default_factory=list)

    # Statistics
    total_oco_created: int = 0
    total_oco_filled: int = 0
    total_oco_cancelled: int = 0
    total_brackets_created: int = 0
    total_brackets_stopped: int = 0
    total_brackets_target_hit: int = 0

    def create_oco_pair(
        self,
        oco_id: str,
        order_a_id: str,
        order_b_id: str,
        order_a_type: str,
        order_b_type: str,
        tag: str = "",
    ) -> OCOPair:
        """Create an OCO pair between two orders."""
        oco = OCOPair(
            oco_id=oco_id,
            order_a_id=order_a_id,
            order_b_id=order_b_id,
            order_a_type=order_a_type,
            order_b_type=order_b_type,
            tag=tag,
        )

        self.oco_pairs.append(oco)
        self.total_oco_created += 1

        return oco

    def create_bracket_order(
        self,
        bracket_id: str,
        entry_side: Literal["BUY", "SELL"],
        entry_qty: int,
        entry_price: float,
        stop_price: float,
        target_price: float,
        entry_type: str = "limit",
        book: OrderBookEventBatchedStrict = None,
        ts_ns: int = 0,
        tag: str = "",
    ) -> BracketOrder:
        """
        Create a bracket order with entry, stop, and target.

        The entry order is placed immediately.
        Stop and target are placed when entry fills.
        """

        bracket = BracketOrder(
            bracket_id=bracket_id,
            entry_side=entry_side,
            entry_qty=entry_qty,
            entry_type=entry_type,
            stop_price=stop_price,
            target_price=target_price,
            tag=tag,
        )

        # Place entry order based on type
        if entry_type == "market" and book is not None:
            # Execute immediately
            if entry_side == "BUY":
                fill = self.fill_model.simulate_market_buy(entry_qty, book)
            else:
                fill = self.fill_model.simulate_market_sell(entry_qty, book)

            if fill.fully_filled:
                bracket.entry_filled = True
                bracket.entry_fill_price = fill.avg_fill_price
                bracket.ts_entry_filled_ns = ts_ns
                bracket.status = BracketStatus.ACTIVE

                # Place exit orders
                self._place_bracket_exits(bracket, ts_ns)

        elif entry_type == "limit" and book is not None:
            # Place passive limit
            passive = self.fill_model.place_passive_limit(
                side=entry_side,
                price=entry_price,
                qty=entry_qty,
                book=book,
                ts_ns=ts_ns,
                tag=f"bracket_{bracket_id}_entry",
            )
            bracket.entry_order_id = f"passive_{bracket_id}_entry"

        self.brackets.append(bracket)
        self.total_brackets_created += 1

        return bracket

    def _place_bracket_exits(self, bracket: BracketOrder, ts_ns: int) -> None:
        """Place stop loss and profit target for filled bracket entry."""

        # Place stop loss
        bracket.stop_order_id = f"stop_{bracket.bracket_id}"
        self.stop_manager.place_stop_market(
            order_id=bracket.stop_order_id,
            side=bracket.exit_side,
            qty=bracket.entry_qty,
            stop_price=bracket.stop_price,
            ts_ns=ts_ns,
            tag=f"bracket_{bracket.bracket_id}_stop",
        )

        # Place target (as stop-limit that triggers immediately on favorable price)
        bracket.target_order_id = f"target_{bracket.bracket_id}"

        # Target is placed as a limit order that should trigger when price reaches target
        # For simplicity, we'll use stop-limit where stop = limit = target
        self.stop_manager.place_stop_limit(
            order_id=bracket.target_order_id,
            side=bracket.exit_side,
            qty=bracket.entry_qty,
            stop_price=bracket.target_price,
            limit_price=bracket.target_price,
            ts_ns=ts_ns,
            tag=f"bracket_{bracket.bracket_id}_target",
        )

        # Create OCO between stop and target
        bracket.exit_oco_id = f"oco_{bracket.bracket_id}_exits"
        self.create_oco_pair(
            oco_id=bracket.exit_oco_id,
            order_a_id=bracket.stop_order_id,
            order_b_id=bracket.target_order_id,
            order_a_type="stop_market",
            order_b_type="stop_limit",
            tag=f"bracket_{bracket.bracket_id}",
        )

    def check_oco_fills(self, ts_ns: int) -> list[OCOPair]:
        """
        Check if any OCO orders have filled and cancel the other leg.

        Returns:
            List of OCO pairs that were resolved (one filled, other cancelled)
        """

        resolved = []

        for oco in self.oco_pairs:
            if not oco.is_active:
                continue

            # Check if either order filled
            order_a_filled = self._is_order_filled(oco.order_a_id, oco.order_a_type)
            order_b_filled = self._is_order_filled(oco.order_b_id, oco.order_b_type)

            if order_a_filled:
                # Cancel order B
                self._cancel_order(oco.order_b_id, oco.order_b_type)
                oco.status = OCOStatus.FILLED
                oco.filled_order_id = oco.order_a_id
                oco.cancelled_order_id = oco.order_b_id
                oco.ts_filled_ns = ts_ns
                resolved.append(oco)
                self.total_oco_filled += 1

            elif order_b_filled:
                # Cancel order A
                self._cancel_order(oco.order_a_id, oco.order_a_type)
                oco.status = OCOStatus.FILLED
                oco.filled_order_id = oco.order_b_id
                oco.cancelled_order_id = oco.order_a_id
                oco.ts_filled_ns = ts_ns
                resolved.append(oco)
                self.total_oco_filled += 1

        return resolved

    def check_bracket_status(self, ts_ns: int) -> list[BracketOrder]:
        """
        Check bracket order status and update.

        Returns:
            List of brackets that completed (hit stop or target)
        """

        completed = []

        for bracket in self.brackets:
            if bracket.status != BracketStatus.ACTIVE:
                continue

            # Check if stop hit
            stop_filled = self._is_order_filled(bracket.stop_order_id, "stop_market")
            if stop_filled:
                bracket.stop_filled = True
                bracket.status = BracketStatus.STOPPED
                bracket.ts_exit_filled_ns = ts_ns
                completed.append(bracket)
                self.total_brackets_stopped += 1

            # Check if target hit
            target_filled = self._is_order_filled(bracket.target_order_id, "stop_limit")
            if target_filled:
                bracket.target_filled = True
                bracket.status = BracketStatus.TARGET_HIT
                bracket.ts_exit_filled_ns = ts_ns
                completed.append(bracket)
                self.total_brackets_target_hit += 1

        return completed

    def _is_order_filled(self, order_id: str, order_type: str) -> bool:
        """Check if an order has filled."""

        if order_type == "stop_market":
            for order in self.stop_manager.stop_market_orders:
                if order.order_id == order_id:
                    return order.status.value == "filled"

        elif order_type == "stop_limit":
            for order in self.stop_manager.stop_limit_orders:
                if order.order_id == order_id:
                    return order.status.value == "filled"

        elif order_type == "passive_limit":
            for order in self.stop_manager.triggered_passive_orders:
                if hasattr(order, 'tag') and order_id in order.tag:
                    return order.is_filled

        return False

    def _cancel_order(self, order_id: str, order_type: str) -> bool:
        """Cancel an order."""
        return self.stop_manager.cancel_stop_order(order_id)

    def cancel_oco(self, oco_id: str) -> bool:
        """Cancel an OCO pair (both orders)."""
        for oco in self.oco_pairs:
            if oco.oco_id == oco_id and oco.is_active:
                self._cancel_order(oco.order_a_id, oco.order_a_type)
                self._cancel_order(oco.order_b_id, oco.order_b_type)
                oco.status = OCOStatus.CANCELLED
                self.total_oco_cancelled += 1
                return True

        return False

    def cancel_bracket(self, bracket_id: str) -> bool:
        """Cancel a bracket order."""
        for bracket in self.brackets:
            if bracket.bracket_id == bracket_id:
                if bracket.status == BracketStatus.PENDING:
                    # Cancel entry
                    bracket.status = BracketStatus.CANCELLED
                    return True

                elif bracket.status == BracketStatus.ACTIVE:
                    # Cancel exit OCO
                    if bracket.exit_oco_id:
                        self.cancel_oco(bracket.exit_oco_id)
                    bracket.status = BracketStatus.CANCELLED
                    return True

        return False

    def stats_dict(self) -> dict:
        """Get statistics."""
        active_oco = sum(1 for o in self.oco_pairs if o.is_active)
        active_brackets = sum(1 for b in self.brackets if b.is_active)
        pending_brackets = sum(1 for b in self.brackets if b.status == BracketStatus.PENDING)

        return {
            "total_oco_created": self.total_oco_created,
            "total_oco_filled": self.total_oco_filled,
            "total_oco_cancelled": self.total_oco_cancelled,
            "active_oco_pairs": active_oco,
            "total_brackets_created": self.total_brackets_created,
            "total_brackets_stopped": self.total_brackets_stopped,
            "total_brackets_target_hit": self.total_brackets_target_hit,
            "active_brackets": active_brackets,
            "pending_brackets": pending_brackets,
            "bracket_target_hit_rate": (
                self.total_brackets_target_hit / (self.total_brackets_stopped + self.total_brackets_target_hit) * 100
                if (self.total_brackets_stopped + self.total_brackets_target_hit) > 0
                else 0.0
            ),
        }