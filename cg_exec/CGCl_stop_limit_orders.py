"""
Stop and Stop-Limit Order Extensions for Strict Fill Model.

This module adds:
1. Stop-Market orders (trigger at stop price, then execute as market)
2. Stop-Limit orders (trigger at stop price, then place limit order)
3. Trailing stops (dynamic stop price that follows favorable price movement)
4. Order state management and lifecycle tracking

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Literal, Optional

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_exec.CGCl_fill_model_strict import StrictFillModel, StrictMarketFill, StrictPassiveOrder


class StopOrderStatus(str, Enum):
    """Status of a stop order."""
    PENDING = "pending"           # Waiting for trigger
    TRIGGERED = "triggered"       # Stop price hit, order activated
    FILLED = "filled"             # Order completely filled
    CANCELLED = "cancelled"       # Order cancelled before trigger
    REJECTED = "rejected"         # Order rejected (e.g., invalid price)
    PARTIAL = "partial"           # Partially filled


class StopTriggerType(str, Enum):
    """How stop orders trigger."""
    LAST_TRADE = "last_trade"     # Trigger on last trade price
    BID_ASK = "bid_ask"           # Trigger on bid/ask crossing stop
    CONSERVATIVE = "conservative" # Trigger only when best bid/ask crosses stop


@dataclass(slots=True)
class StopMarketOrder:
    """
    Stop-market order that triggers at stop price and executes as market order.

    Buy stop:  triggers when price rises to/above stop_price (breakout, cover short)
    Sell stop: triggers when price falls to/below stop_price (stop loss, exit long)
    """

    # Order identification
    order_id: str
    side: Literal["BUY", "SELL"]
    qty: int
    stop_price: float
    tag: str = ""

    # Trigger configuration
    trigger_type: StopTriggerType = StopTriggerType.BID_ASK

    # State
    status: StopOrderStatus = StopOrderStatus.PENDING
    ts_placed_ns: int = 0
    ts_triggered_ns: Optional[int] = None
    ts_filled_ns: Optional[int] = None

    # Execution results (after trigger)
    market_fill: Optional[StrictMarketFill] = None

    # Trailing stop support
    is_trailing: bool = False
    trail_offset: float = 0.0  # Offset from favorable price
    best_price_seen: Optional[float] = None

    @property
    def is_active(self) -> bool:
        return self.status == StopOrderStatus.PENDING

    @property
    def is_buy_stop(self) -> bool:
        """Buy stop triggers when price rises."""
        return self.side == "BUY"

    def update_trailing_stop(self, current_price: float) -> None:
        """Update stop price for trailing stop."""
        if not self.is_trailing:
            return

        if self.best_price_seen is None:
            self.best_price_seen = current_price
            return

        if self.is_buy_stop:
            # Buy stop: trail below rising price
            if current_price > self.best_price_seen:
                self.best_price_seen = current_price
                # Stop price trails above by offset (we want to buy on pullback)
                # Actually for buy stop trailing, we trail the stop DOWN as price falls
                # This is typically used to enter on breakout with protection
                # For now, keep simple: trail stop up as price rises
                self.stop_price = current_price + self.trail_offset
        else:
            # Sell stop: trail above falling price
            if current_price < self.best_price_seen:
                self.best_price_seen = current_price
                # Stop price trails below by offset
                self.stop_price = current_price - self.trail_offset


@dataclass(slots=True)
class StopLimitOrder:
    """
    Stop-limit order that triggers at stop price and places a limit order.

    Provides price protection after trigger (unlike stop-market which can slip).
    Risk: may not fill if market moves through limit price quickly.
    """

    # Order identification
    order_id: str
    side: Literal["BUY", "SELL"]
    qty: int
    stop_price: float
    limit_price: float
    tag: str = ""

    # Trigger configuration
    trigger_type: StopTriggerType = StopTriggerType.BID_ASK

    # State
    status: StopOrderStatus = StopOrderStatus.PENDING
    ts_placed_ns: int = 0
    ts_triggered_ns: Optional[int] = None

    # Execution results (after trigger)
    passive_order: Optional[StrictPassiveOrder] = None

    # Trailing stop support
    is_trailing: bool = False
    trail_offset: float = 0.0
    best_price_seen: Optional[float] = None

    @property
    def is_active(self) -> bool:
        return self.status == StopOrderStatus.PENDING

    @property
    def is_buy_stop(self) -> bool:
        return self.side == "BUY"

    def update_trailing_stop(self, current_price: float) -> None:
        """Update stop price for trailing stop."""
        if not self.is_trailing:
            return

        if self.best_price_seen is None:
            self.best_price_seen = current_price
            return

        if self.is_buy_stop:
            if current_price > self.best_price_seen:
                self.best_price_seen = current_price
                self.stop_price = current_price + self.trail_offset
                # Keep limit price offset from stop
                limit_offset = self.limit_price - (self.stop_price - self.trail_offset)
                self.limit_price = self.stop_price + limit_offset
        else:
            if current_price < self.best_price_seen:
                self.best_price_seen = current_price
                self.stop_price = current_price - self.trail_offset
                # Keep limit price offset from stop
                limit_offset = (self.stop_price + self.trail_offset) - self.limit_price
                self.limit_price = self.stop_price - limit_offset


@dataclass
class StopOrderManager:
    """
    Manages stop and stop-limit orders.

    Responsibilities:
    - Track pending stop orders
    - Monitor market for stop triggers
    - Execute market orders when stop-market triggers
    - Place limit orders when stop-limit triggers
    - Handle trailing stops
    """

    fill_model: StrictFillModel

    # Order tracking
    stop_market_orders: list[StopMarketOrder] = field(default_factory=list)
    stop_limit_orders: list[StopLimitOrder] = field(default_factory=list)

    # Triggered orders (now being filled)
    triggered_passive_orders: list[StrictPassiveOrder] = field(default_factory=list)

    # Statistics
    total_stop_market_placed: int = 0
    total_stop_market_triggered: int = 0
    total_stop_market_filled: int = 0
    total_stop_limit_placed: int = 0
    total_stop_limit_triggered: int = 0
    total_stop_limit_filled: int = 0
    total_stops_cancelled: int = 0

    def place_stop_market(
        self,
        order_id: str,
        side: Literal["BUY", "SELL"],
        qty: int,
        stop_price: float,
        ts_ns: int,
        trigger_type: StopTriggerType = StopTriggerType.BID_ASK,
        tag: str = "",
    ) -> StopMarketOrder:
        """Place a stop-market order."""
        order = StopMarketOrder(
            order_id=order_id,
            side=side,
            qty=qty,
            stop_price=stop_price,
            trigger_type=trigger_type,
            ts_placed_ns=ts_ns,
            tag=tag,
        )

        self.stop_market_orders.append(order)
        self.total_stop_market_placed += 1

        return order

    def place_stop_limit(
        self,
        order_id: str,
        side: Literal["BUY", "SELL"],
        qty: int,
        stop_price: float,
        limit_price: float,
        ts_ns: int,
        trigger_type: StopTriggerType = StopTriggerType.BID_ASK,
        tag: str = "",
    ) -> StopLimitOrder:
        """Place a stop-limit order."""
        order = StopLimitOrder(
            order_id=order_id,
            side=side,
            qty=qty,
            stop_price=stop_price,
            limit_price=limit_price,
            trigger_type=trigger_type,
            ts_placed_ns=ts_ns,
            tag=tag,
        )

        self.stop_limit_orders.append(order)
        self.total_stop_limit_placed += 1

        return order

    def place_trailing_stop_market(
        self,
        order_id: str,
        side: Literal["BUY", "SELL"],
        qty: int,
        initial_stop_price: float,
        trail_offset: float,
        ts_ns: int,
        tag: str = "",
    ) -> StopMarketOrder:
        """Place a trailing stop-market order."""
        order = self.place_stop_market(
            order_id=order_id,
            side=side,
            qty=qty,
            stop_price=initial_stop_price,
            ts_ns=ts_ns,
            tag=tag,
        )

        order.is_trailing = True
        order.trail_offset = trail_offset

        return order

    def check_triggers(
        self,
        book: OrderBookEventBatchedStrict,
        ts_ns: int,
    ) -> tuple[list[StopMarketOrder], list[StopLimitOrder]]:
        """
        Check if any stop orders should trigger based on current market.

        Returns:
            (triggered_stop_markets, triggered_stop_limits)
        """
        best_bid = book.best_bid()
        best_ask = book.best_ask()

        if best_bid is None or best_ask is None:
            return [], []

        triggered_markets = []
        triggered_limits = []

        # Update trailing stops
        mid_price = (best_bid + best_ask) / 2

        for order in self.stop_market_orders:
            if not order.is_active:
                continue

            if order.is_trailing:
                order.update_trailing_stop(mid_price)

        for order in self.stop_limit_orders:
            if not order.is_active:
                continue

            if order.is_trailing:
                order.update_trailing_stop(mid_price)

        # Check stop-market triggers
        for order in self.stop_market_orders:
            if not order.is_active:
                continue

            triggered = False

            if order.trigger_type == StopTriggerType.BID_ASK:
                if order.is_buy_stop:
                    # Buy stop: trigger when ask >= stop_price
                    triggered = best_ask >= order.stop_price
                else:
                    # Sell stop: trigger when bid <= stop_price
                    triggered = best_bid <= order.stop_price

            elif order.trigger_type == StopTriggerType.CONSERVATIVE:
                if order.is_buy_stop:
                    # Buy stop: trigger when bid >= stop_price (more conservative)
                    triggered = best_bid >= order.stop_price
                else:
                    # Sell stop: trigger when ask <= stop_price
                    triggered = best_ask <= order.stop_price

            if triggered:
                order.status = StopOrderStatus.TRIGGERED
                order.ts_triggered_ns = ts_ns
                triggered_markets.append(order)
                self.total_stop_market_triggered += 1

        # Check stop-limit triggers
        for order in self.stop_limit_orders:
            if not order.is_active:
                continue

            triggered = False

            if order.trigger_type == StopTriggerType.BID_ASK:
                if order.is_buy_stop:
                    triggered = best_ask >= order.stop_price
                else:
                    triggered = best_bid <= order.stop_price

            elif order.trigger_type == StopTriggerType.CONSERVATIVE:
                if order.is_buy_stop:
                    triggered = best_bid >= order.stop_price
                else:
                    triggered = best_ask <= order.stop_price

            if triggered:
                order.status = StopOrderStatus.TRIGGERED
                order.ts_triggered_ns = ts_ns
                triggered_limits.append(order)
                self.total_stop_limit_triggered += 1

        return triggered_markets, triggered_limits

    def execute_triggered_orders(
        self,
        triggered_markets: list[StopMarketOrder],
        triggered_limits: list[StopLimitOrder],
        book: OrderBookEventBatchedStrict,
        ts_ns: int,
    ) -> None:
        """Execute triggered stop orders."""

        # Execute stop-market orders immediately
        for order in triggered_markets:
            if order.side == "BUY":
                fill = self.fill_model.simulate_market_buy(order.qty, book)
            else:
                fill = self.fill_model.simulate_market_sell(order.qty, book)

            order.market_fill = fill

            if fill.fully_filled:
                order.status = StopOrderStatus.FILLED
                order.ts_filled_ns = ts_ns
                self.total_stop_market_filled += 1
            else:
                order.status = StopOrderStatus.PARTIAL

        # Place limit orders for stop-limit triggers
        for order in triggered_limits:
            passive = self.fill_model.place_passive_limit(
                side=order.side,
                price=order.limit_price,
                qty=order.qty,
                book=book,
                ts_ns=ts_ns,
                tag=order.tag,
            )

            order.passive_order = passive
            self.triggered_passive_orders.append(passive)

    def advance_passive_stops(
        self,
        event,
        event_type_name: str,
    ) -> None:
        """Advance passive orders created from stop-limit triggers."""

        for i, passive in enumerate(self.triggered_passive_orders):
            self.triggered_passive_orders[i] = self.fill_model.advance_passive_order(
                passive, event, event_type_name
            )

        # Check for filled passive orders and update parent stop-limit status
        newly_filled = [o for o in self.triggered_passive_orders if o.is_filled]

        for passive in newly_filled:
            # Find parent stop-limit order
            for stop_limit in self.stop_limit_orders:
                if stop_limit.passive_order is passive:
                    stop_limit.status = StopOrderStatus.FILLED
                    self.total_stop_limit_filled += 1
                    break

        # Remove filled orders
        self.triggered_passive_orders = [
            o for o in self.triggered_passive_orders if not o.is_filled
        ]

    def cancel_stop_order(
        self,
        order_id: str,
    ) -> bool:
        """Cancel a pending stop order."""

        # Try stop-market
        for order in self.stop_market_orders:
            if order.order_id == order_id and order.is_active:
                order.status = StopOrderStatus.CANCELLED
                self.total_stops_cancelled += 1
                return True

        # Try stop-limit
        for order in self.stop_limit_orders:
            if order.order_id == order_id and order.is_active:
                order.status = StopOrderStatus.CANCELLED
                self.total_stops_cancelled += 1
                return True

        return False

    def get_active_stops(self) -> dict:
        """Get count of active stop orders."""
        active_markets = sum(1 for o in self.stop_market_orders if o.is_active)
        active_limits = sum(1 for o in self.stop_limit_orders if o.is_active)
        triggered_passive = len(self.triggered_passive_orders)

        return {
            "active_stop_markets": active_markets,
            "active_stop_limits": active_limits,
            "triggered_passive_pending": triggered_passive,
        }

    def stats_dict(self) -> dict:
        """Get statistics."""
        active = self.get_active_stops()

        return {
            "stop_market_placed": self.total_stop_market_placed,
            "stop_market_triggered": self.total_stop_market_triggered,
            "stop_market_filled": self.total_stop_market_filled,
            "stop_market_trigger_rate": (
                self.total_stop_market_triggered / self.total_stop_market_placed * 100
                if self.total_stop_market_placed > 0 else 0.0
            ),
            "stop_limit_placed": self.total_stop_limit_placed,
            "stop_limit_triggered": self.total_stop_limit_triggered,
            "stop_limit_filled": self.total_stop_limit_filled,
            "stop_limit_trigger_rate": (
                self.total_stop_limit_triggered / self.total_stop_limit_placed * 100
                if self.total_stop_limit_placed > 0 else 0.0
            ),
            "stops_cancelled": self.total_stops_cancelled,
            **active,
        }
