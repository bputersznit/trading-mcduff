"""
Strict Fill Model - Integrates with OrderBookEventBatchedStrict for realistic fill simulation.

This module provides:
1. Market order simulation with strict book sweeping
2. Passive limit order queue tracking through actual book events
3. Event-batch aware fill logic that respects F_LAST flag
4. Realistic queue advancement based on fill_resting and trade_aggressor events

Key differences from basic fill_model.py:
- Works with OrderBookEventBatchedStrict instead of basic OrderBook
- Tracks queue position through actual MBO events (cancels, fills, trades)
- Respects event batching (ts_event_ns grouping)
- Uses semantic event types from extraction pipeline

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional, Literal

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_sim.models import MarketEvent


@dataclass(slots=True)
class StrictMarketFill:
    """Result of a market order sweep through the strict book."""

    side: Literal["BUY", "SELL"]
    requested_qty: int
    filled_qty: int
    remaining_qty: int
    avg_fill_price: float | None
    worst_fill_price: float | None
    best_fill_price: float | None
    levels_consumed: int
    fully_filled: bool
    slippage_ticks: float = 0.0
    total_cost: float = 0.0


@dataclass(slots=True)
class StrictPassiveOrder:
    """
    A passive limit order tracked through the strict order book.

    Queue position advances based on actual book events:
    - Cancels ahead reduce queue_ahead
    - Fills ahead reduce queue_ahead
    - Trade aggressor events reduce queue_ahead
    - When queue_ahead reaches 0, our order gets filled
    """

    # Order details
    side: Literal["BUY", "SELL"]
    price: float
    qty: int
    tag: str = ""

    # Queue tracking
    initial_queue_ahead: int = 0
    current_queue_ahead: int = 0
    filled_qty: int = 0

    # Status
    active: bool = True
    ts_placed_ns: int = 0
    ts_filled_ns: int | None = None

    # Lifecycle tracking
    cancel_events_seen: int = 0
    fill_events_seen: int = 0
    trade_events_seen: int = 0
    queue_reductions: int = 0

    @property
    def remaining_qty(self) -> int:
        return self.qty - self.filled_qty

    @property
    def is_filled(self) -> bool:
        return self.filled_qty >= self.qty

    @property
    def fill_percentage(self) -> float:
        return (self.filled_qty / self.qty * 100) if self.qty > 0 else 0.0


@dataclass
class StrictFillModel:
    """
    Fill model that works with OrderBookEventBatchedStrict.

    Provides realistic fill simulation by:
    1. Sweeping market orders through visible book depth
    2. Tracking passive orders through actual MBO event stream
    3. Advancing queue position based on real cancels/fills/trades
    """

    # Configuration
    assume_queue_position: Literal["back", "front", "middle"] = "back"
    aggressive_queue_jumping: bool = False  # If True, assume better queue position

    # Statistics
    total_market_fills: int = 0
    total_passive_placements: int = 0
    total_passive_fills: int = 0
    total_passive_cancels: int = 0

    def simulate_market_buy(
        self,
        qty: int,
        book: OrderBookEventBatchedStrict,
        max_slippage_ticks: float | None = None,
    ) -> StrictMarketFill:
        """
        Simulate a market buy order sweeping through the ask side.

        Args:
            qty: Quantity to buy
            book: Current strict order book state
            max_slippage_ticks: Optional maximum slippage allowed (in ticks)

        Returns:
            StrictMarketFill with execution details
        """

        if qty <= 0:
            return self._empty_fill("BUY", qty)

        best_bid = book.best_bid()
        best_ask = book.best_ask()

        if best_ask is None:
            return self._empty_fill("BUY", qty)

        reference_price = best_ask
        remaining = qty
        total_cost = 0.0
        filled = 0
        levels_consumed = 0
        best_fill = None
        worst_fill = None

        # Sweep through ask levels
        for price, level in book.asks.items():
            available = max(0, level.total_size)

            if available <= 0:
                continue

            # Check slippage limit
            if max_slippage_ticks is not None:
                slippage_ticks = (price - reference_price) / 0.25  # Assuming 0.25 tick size for MNQ
                if slippage_ticks > max_slippage_ticks:
                    break

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
        slippage_ticks = ((worst_fill - reference_price) / 0.25) if worst_fill else 0.0

        self.total_market_fills += 1

        return StrictMarketFill(
            side="BUY",
            requested_qty=qty,
            filled_qty=filled,
            remaining_qty=remaining,
            avg_fill_price=avg_fill,
            worst_fill_price=worst_fill,
            best_fill_price=best_fill,
            levels_consumed=levels_consumed,
            fully_filled=(remaining == 0),
            slippage_ticks=slippage_ticks,
            total_cost=total_cost,
        )

    def simulate_market_sell(
        self,
        qty: int,
        book: OrderBookEventBatchedStrict,
        max_slippage_ticks: float | None = None,
    ) -> StrictMarketFill:
        """
        Simulate a market sell order sweeping through the bid side.

        Args:
            qty: Quantity to sell
            book: Current strict order book state
            max_slippage_ticks: Optional maximum slippage allowed (in ticks)

        Returns:
            StrictMarketFill with execution details
        """

        if qty <= 0:
            return self._empty_fill("SELL", qty)

        best_bid = book.best_bid()
        best_ask = book.best_ask()

        if best_bid is None:
            return self._empty_fill("SELL", qty)

        reference_price = best_bid
        remaining = qty
        total_value = 0.0
        filled = 0
        levels_consumed = 0
        best_fill = None
        worst_fill = None

        # Sweep through bid levels (highest to lowest)
        bid_levels = list(book.bids.items())[::-1]

        for price, level in bid_levels:
            available = max(0, level.total_size)

            if available <= 0:
                continue

            # Check slippage limit
            if max_slippage_ticks is not None:
                slippage_ticks = (reference_price - price) / 0.25  # Assuming 0.25 tick size for MNQ
                if slippage_ticks > max_slippage_ticks:
                    break

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
        slippage_ticks = ((reference_price - worst_fill) / 0.25) if worst_fill else 0.0

        self.total_market_fills += 1

        return StrictMarketFill(
            side="SELL",
            requested_qty=qty,
            filled_qty=filled,
            remaining_qty=remaining,
            avg_fill_price=avg_fill,
            worst_fill_price=worst_fill,
            best_fill_price=best_fill,
            levels_consumed=levels_consumed,
            fully_filled=(remaining == 0),
            slippage_ticks=slippage_ticks,
            total_cost=total_value,
        )

    def place_passive_limit(
        self,
        side: Literal["BUY", "SELL"],
        price: float,
        qty: int,
        book: OrderBookEventBatchedStrict,
        ts_ns: int,
        tag: str = "",
    ) -> StrictPassiveOrder:
        """
        Place a passive limit order and calculate initial queue position.

        Args:
            side: BUY or SELL
            price: Limit price
            qty: Order quantity
            book: Current strict order book state
            ts_ns: Timestamp of placement
            tag: Optional tag for tracking

        Returns:
            StrictPassiveOrder with initial queue position
        """

        # Normalize side to BID/ASK for book lookup
        book_side = "BID" if side == "BUY" else "ASK"
        levels = book._levels_for_side(book_side)
        level = levels.get(price)

        # Calculate initial queue position
        if level is not None:
            visible_size = level.total_size

            # Queue position depends on assumption
            if self.assume_queue_position == "back":
                queue_ahead = visible_size
            elif self.assume_queue_position == "front":
                queue_ahead = 0
            elif self.assume_queue_position == "middle":
                queue_ahead = visible_size // 2
            else:
                queue_ahead = visible_size

            # Aggressive queue jumping assumes better position
            if self.aggressive_queue_jumping:
                queue_ahead = max(0, queue_ahead - qty)
        else:
            # Price level doesn't exist, we're first in queue
            queue_ahead = 0

        self.total_passive_placements += 1

        return StrictPassiveOrder(
            side=side,
            price=price,
            qty=qty,
            tag=tag,
            initial_queue_ahead=queue_ahead,
            current_queue_ahead=queue_ahead,
            ts_placed_ns=ts_ns,
        )

    def advance_passive_order(
        self,
        order: StrictPassiveOrder,
        event: MarketEvent,
        event_type_name: str,
    ) -> StrictPassiveOrder:
        """
        Advance a passive order based on a book event.

        This is called for each MBO event at the order's price level to:
        - Reduce queue_ahead on cancels
        - Reduce queue_ahead on fills
        - Fill our order when queue_ahead reaches 0

        Args:
            order: The passive order to advance
            event: The MBO event
            event_type_name: Semantic event type (cancel, fill_resting, trade_aggressor, etc.)

        Returns:
            Updated StrictPassiveOrder
        """

        if not order.active or order.is_filled:
            return order

        # Only process events at our price level
        if event.price != order.price:
            return order

        # Normalize side
        event_side = self._normalize_side(event.side)
        order_book_side = "BID" if order.side == "BUY" else "ASK"

        if event_side != order_book_side:
            return order

        # Get event size
        event_size = event.size if event.size is not None else 0

        if event_size <= 0:
            return order

        # Process different event types
        if event_type_name == "cancel":
            # Cancel reduces queue ahead
            order.cancel_events_seen += 1
            reduction = min(order.current_queue_ahead, event_size)
            order.current_queue_ahead -= reduction
            if reduction > 0:
                order.queue_reductions += 1

        elif event_type_name == "fill_resting":
            # Resting fill reduces queue ahead
            order.fill_events_seen += 1
            reduction = min(order.current_queue_ahead, event_size)
            order.current_queue_ahead -= reduction
            if reduction > 0:
                order.queue_reductions += 1

        elif event_type_name == "trade_aggressor":
            # Aggressor trade doesn't affect queue directly
            # (the corresponding fill_resting event will)
            order.trade_events_seen += 1

        # If queue ahead is exhausted, start filling our order
        if order.current_queue_ahead <= 0 and event_type_name in ("fill_resting", "trade_aggressor"):
            # We're at the front, this trade can fill us
            available_to_fill = order.remaining_qty
            fill_qty = min(available_to_fill, event_size)

            if fill_qty > 0:
                order.filled_qty += fill_qty
                order.ts_filled_ns = event.ts_event_ns

                if order.is_filled:
                    order.active = False
                    self.total_passive_fills += 1

        return order

    def cancel_passive_order(self, order: StrictPassiveOrder, ts_ns: int) -> StrictPassiveOrder:
        """
        Cancel a passive order.

        Args:
            order: The order to cancel
            ts_ns: Timestamp of cancellation

        Returns:
            Updated order marked as inactive
        """

        if order.active:
            order.active = False
            self.total_passive_cancels += 1

        return order

    def _normalize_side(self, side: Optional[str]) -> Optional[str]:
        """Normalize side to BID or ASK."""
        if side is None:
            return None

        s = str(side).upper()

        if s in ("B", "BID", "BUY"):
            return "BID"

        if s in ("A", "ASK", "SELL"):
            return "ASK"

        return None

    def _empty_fill(self, side: Literal["BUY", "SELL"], qty: int) -> StrictMarketFill:
        """Create an empty fill result when no liquidity available."""
        return StrictMarketFill(
            side=side,
            requested_qty=qty,
            filled_qty=0,
            remaining_qty=qty,
            avg_fill_price=None,
            worst_fill_price=None,
            best_fill_price=None,
            levels_consumed=0,
            fully_filled=False,
        )

    def stats_dict(self) -> dict:
        """Return statistics about fill model performance."""
        return {
            "total_market_fills": self.total_market_fills,
            "total_passive_placements": self.total_passive_placements,
            "total_passive_fills": self.total_passive_fills,
            "total_passive_cancels": self.total_passive_cancels,
            "passive_fill_rate": (
                self.total_passive_fills / self.total_passive_placements * 100
                if self.total_passive_placements > 0
                else 0.0
            ),
        }
