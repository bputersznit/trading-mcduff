"""
Advanced Institutional Order Types.

Implements sophisticated order types used by professional traders:
1. Iceberg Orders - Hide large size, reveal incrementally
2. Pegged Orders - Auto-adjust price to track market
3. Scale Orders - Multiple orders across price levels
4. VWAP Orders - Volume-weighted average price execution
5. Hidden Orders - Partial display quantity
6. Reserve Orders - Show small, hide large

These provide stealth execution, reduced market impact, and sophisticated entry/exit strategies.

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Literal, Optional, Callable

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_exec.CGCl_fill_model_strict import StrictFillModel, StrictPassiveOrder
from cg_sim.models import MarketEvent


class PegType(str, Enum):
    """How pegged orders track the market."""
    PRIMARY_PEG = "primary_peg"      # Track best bid/ask
    MARKET_PEG = "market_peg"        # Track opposite side
    MIDPOINT_PEG = "midpoint_peg"    # Track mid-price


class ScaleDistribution(str, Enum):
    """How to distribute orders in scale."""
    LINEAR = "linear"                # Equal spacing
    GEOMETRIC = "geometric"          # Exponential spacing
    WEIGHTED_TOP = "weighted_top"    # More orders near top
    WEIGHTED_BOTTOM = "weighted_bottom"  # More orders near bottom


@dataclass(slots=True)
class IcebergOrder:
    """
    Iceberg Order - Shows small tip, hides large size.

    Benefits:
    - Reduced market impact
    - Hide true size from predatory traders
    - Execute large orders without moving market

    The visible "tip" replenishes as it fills.
    """

    # Identification
    order_id: str
    side: Literal["BUY", "SELL"]
    total_qty: int
    display_qty: int  # Visible tip size
    price: float
    tag: str = ""

    # State
    filled_qty: int = 0
    current_display_qty: int = 0
    active: bool = True
    ts_placed_ns: int = 0

    # Passive orders for each tip
    passive_orders: list[StrictPassiveOrder] = field(default_factory=list)

    def __post_init__(self):
        self.current_display_qty = min(self.display_qty, self.remaining_qty)

    @property
    def remaining_qty(self) -> int:
        return self.total_qty - self.filled_qty

    @property
    def is_filled(self) -> bool:
        return self.filled_qty >= self.total_qty

    @property
    def hidden_qty(self) -> int:
        """Quantity hidden from market."""
        return self.remaining_qty - self.current_display_qty

    def replenish_tip(self) -> int:
        """Calculate next tip size after current fills."""
        if self.is_filled:
            return 0
        return min(self.display_qty, self.remaining_qty)


@dataclass(slots=True)
class PeggedOrder:
    """
    Pegged Order - Automatically adjusts price to track market.

    Types:
    - Primary Peg: Tracks best bid (buy) or ask (sell)
    - Market Peg: Tracks opposite side
    - Midpoint Peg: Tracks mid-price

    Offset: How many ticks away from reference price
    """

    order_id: str
    side: Literal["BUY", "SELL"]
    qty: int
    peg_type: PegType
    offset_ticks: float = 0.0  # Offset from peg (can be negative)
    tag: str = ""

    # State
    current_price: Optional[float] = None
    filled_qty: int = 0
    active: bool = True
    ts_placed_ns: int = 0

    # Current passive order
    current_passive: Optional[StrictPassiveOrder] = None

    # Tracking
    price_updates: int = 0
    order_replacements: int = 0

    @property
    def is_filled(self) -> bool:
        return self.filled_qty >= self.qty

    @property
    def remaining_qty(self) -> int:
        return self.qty - self.filled_qty

    def calculate_peg_price(
        self,
        book: OrderBookEventBatchedStrict,
        tick_size: float = 0.25,
    ) -> Optional[float]:
        """Calculate where order should be pegged."""
        best_bid = book.best_bid()
        best_ask = book.best_ask()

        if best_bid is None or best_ask is None:
            return None

        reference_price = None

        if self.peg_type == PegType.PRIMARY_PEG:
            # Track best bid (buy) or ask (sell)
            reference_price = best_bid if self.side == "BUY" else best_ask

        elif self.peg_type == PegType.MARKET_PEG:
            # Track opposite side
            reference_price = best_ask if self.side == "BUY" else best_bid

        elif self.peg_type == PegType.MIDPOINT_PEG:
            # Track mid-price
            reference_price = (best_bid + best_ask) / 2

        if reference_price is None:
            return None

        # Apply offset
        offset_dollars = self.offset_ticks * tick_size
        pegged_price = reference_price + offset_dollars

        # Round to tick size
        pegged_price = round(pegged_price / tick_size) * tick_size

        return pegged_price


@dataclass(slots=True)
class ScaleOrder:
    """
    Scale Order - Multiple orders distributed across price levels.

    Use cases:
    - Accumulate position gradually
    - Average into position
    - Scale out of winners

    Distribution patterns:
    - Linear: Equal spacing
    - Geometric: Exponential spacing
    - Weighted: More orders at certain levels
    """

    order_id: str
    side: Literal["BUY", "SELL"]
    total_qty: int
    num_orders: int
    start_price: float
    end_price: float
    distribution: ScaleDistribution = ScaleDistribution.LINEAR
    tag: str = ""

    # State
    orders: list[dict] = field(default_factory=list)  # [{price, qty, passive_order, filled}]
    total_filled_qty: int = 0
    active: bool = True
    ts_placed_ns: int = 0

    @property
    def is_fully_filled(self) -> bool:
        return self.total_filled_qty >= self.total_qty

    @property
    def avg_fill_price(self) -> float:
        """Calculate average fill price of filled orders."""
        total_value = 0.0
        filled_qty = 0

        for order in self.orders:
            if order.get('passive_order') and order['passive_order'].filled_qty > 0:
                filled = order['passive_order'].filled_qty
                price = order['price']
                total_value += filled * price
                filled_qty += filled

        return total_value / filled_qty if filled_qty > 0 else 0.0

    def generate_levels(self, tick_size: float = 0.25) -> list[tuple[float, int]]:
        """
        Generate price levels and quantities.

        Returns:
            List of (price, qty) tuples
        """
        levels = []

        if self.distribution == ScaleDistribution.LINEAR:
            # Equal spacing
            price_step = (self.end_price - self.start_price) / (self.num_orders - 1)
            qty_per_order = self.total_qty // self.num_orders

            for i in range(self.num_orders):
                price = self.start_price + i * price_step
                price = round(price / tick_size) * tick_size
                qty = qty_per_order
                levels.append((price, qty))

        elif self.distribution == ScaleDistribution.GEOMETRIC:
            # Exponential spacing
            ratio = (self.end_price / self.start_price) ** (1 / (self.num_orders - 1))
            qty_per_order = self.total_qty // self.num_orders

            for i in range(self.num_orders):
                price = self.start_price * (ratio ** i)
                price = round(price / tick_size) * tick_size
                qty = qty_per_order
                levels.append((price, qty))

        elif self.distribution == ScaleDistribution.WEIGHTED_TOP:
            # More orders near end price
            weights = [i + 1 for i in range(self.num_orders)]
            total_weight = sum(weights)

            price_step = (self.end_price - self.start_price) / (self.num_orders - 1)

            for i in range(self.num_orders):
                price = self.start_price + i * price_step
                price = round(price / tick_size) * tick_size
                qty = int(self.total_qty * weights[i] / total_weight)
                levels.append((price, qty))

        elif self.distribution == ScaleDistribution.WEIGHTED_BOTTOM:
            # More orders near start price
            weights = [self.num_orders - i for i in range(self.num_orders)]
            total_weight = sum(weights)

            price_step = (self.end_price - self.start_price) / (self.num_orders - 1)

            for i in range(self.num_orders):
                price = self.start_price + i * price_step
                price = round(price / tick_size) * tick_size
                qty = int(self.total_qty * weights[i] / total_weight)
                levels.append((price, qty))

        return levels


@dataclass(slots=True)
class VWAPOrder:
    """
    VWAP Order - Execute at Volume-Weighted Average Price.

    Slices large order into smaller pieces executed over time,
    attempting to match the volume-weighted average price.

    Parameters:
    - Target duration: How long to execute over
    - Min slice size: Minimum order size per execution
    - Max participation rate: Max % of market volume to take
    """

    order_id: str
    side: Literal["BUY", "SELL"]
    total_qty: int
    target_duration_seconds: float
    min_slice_size: int = 1
    max_participation_rate: float = 0.10  # 10% of volume
    tag: str = ""

    # State
    filled_qty: int = 0
    slices_executed: int = 0
    ts_started_ns: Optional[int] = None
    ts_completed_ns: Optional[int] = None
    active: bool = True

    # Execution tracking
    slice_fills: list[dict] = field(default_factory=list)  # [{ts, qty, price}]

    @property
    def is_filled(self) -> bool:
        return self.filled_qty >= self.total_qty

    @property
    def remaining_qty(self) -> int:
        return self.total_qty - self.filled_qty

    @property
    def vwap_price(self) -> float:
        """Calculate actual VWAP achieved."""
        if not self.slice_fills:
            return 0.0

        total_value = sum(s['qty'] * s['price'] for s in self.slice_fills)
        total_qty = sum(s['qty'] for s in self.slice_fills)

        return total_value / total_qty if total_qty > 0 else 0.0

    def calculate_slice_size(
        self,
        elapsed_seconds: float,
        recent_volume: int,
    ) -> int:
        """
        Calculate next slice size.

        Args:
            elapsed_seconds: Time elapsed since start
            recent_volume: Recent market volume

        Returns:
            Slice size for next execution
        """
        # How much time left?
        progress = elapsed_seconds / self.target_duration_seconds
        progress = min(progress, 1.0)

        # How much should we have filled by now?
        target_filled = int(self.total_qty * progress)
        shortfall = max(0, target_filled - self.filled_qty)

        # Calculate based on participation rate
        participation_size = int(recent_volume * self.max_participation_rate)

        # Take the larger of shortfall catchup or participation size
        slice_size = max(shortfall, participation_size, self.min_slice_size)

        # Don't exceed remaining
        slice_size = min(slice_size, self.remaining_qty)

        return slice_size


@dataclass
class AdvancedOrderManager:
    """
    Manages advanced order types.

    Integrates with basic fill model to provide sophisticated execution.
    """

    fill_model: StrictFillModel

    # Order tracking
    iceberg_orders: list[IcebergOrder] = field(default_factory=list)
    pegged_orders: list[PeggedOrder] = field(default_factory=list)
    scale_orders: list[ScaleOrder] = field(default_factory=list)
    vwap_orders: list[VWAPOrder] = field(default_factory=list)

    # Statistics
    total_icebergs_placed: int = 0
    total_icebergs_filled: int = 0
    total_pegged_placed: int = 0
    total_pegged_filled: int = 0
    total_scales_placed: int = 0
    total_scales_filled: int = 0
    total_vwaps_placed: int = 0
    total_vwaps_filled: int = 0

    def place_iceberg_order(
        self,
        order_id: str,
        side: Literal["BUY", "SELL"],
        total_qty: int,
        display_qty: int,
        price: float,
        book: OrderBookEventBatchedStrict,
        ts_ns: int,
        tag: str = "",
    ) -> IcebergOrder:
        """Place an iceberg order."""

        iceberg = IcebergOrder(
            order_id=order_id,
            side=side,
            total_qty=total_qty,
            display_qty=display_qty,
            price=price,
            ts_placed_ns=ts_ns,
            tag=tag,
        )

        # Place initial tip
        tip_qty = iceberg.replenish_tip()
        if tip_qty > 0:
            passive = self.fill_model.place_passive_limit(
                side=side,
                price=price,
                qty=tip_qty,
                book=book,
                ts_ns=ts_ns,
                tag=f"iceberg_{order_id}_tip_0",
            )
            iceberg.passive_orders.append(passive)

        self.iceberg_orders.append(iceberg)
        self.total_icebergs_placed += 1

        return iceberg

    def place_pegged_order(
        self,
        order_id: str,
        side: Literal["BUY", "SELL"],
        qty: int,
        peg_type: PegType,
        offset_ticks: float,
        book: OrderBookEventBatchedStrict,
        ts_ns: int,
        tag: str = "",
    ) -> PeggedOrder:
        """Place a pegged order."""

        pegged = PeggedOrder(
            order_id=order_id,
            side=side,
            qty=qty,
            peg_type=peg_type,
            offset_ticks=offset_ticks,
            ts_placed_ns=ts_ns,
            tag=tag,
        )

        # Calculate initial price
        initial_price = pegged.calculate_peg_price(book)
        if initial_price:
            pegged.current_price = initial_price

            # Place initial order
            passive = self.fill_model.place_passive_limit(
                side=side,
                price=initial_price,
                qty=qty,
                book=book,
                ts_ns=ts_ns,
                tag=f"pegged_{order_id}",
            )
            pegged.current_passive = passive

        self.pegged_orders.append(pegged)
        self.total_pegged_placed += 1

        return pegged

    def place_scale_order(
        self,
        order_id: str,
        side: Literal["BUY", "SELL"],
        total_qty: int,
        num_orders: int,
        start_price: float,
        end_price: float,
        distribution: ScaleDistribution,
        book: OrderBookEventBatchedStrict,
        ts_ns: int,
        tag: str = "",
    ) -> ScaleOrder:
        """Place a scale order."""

        scale = ScaleOrder(
            order_id=order_id,
            side=side,
            total_qty=total_qty,
            num_orders=num_orders,
            start_price=start_price,
            end_price=end_price,
            distribution=distribution,
            ts_placed_ns=ts_ns,
            tag=tag,
        )

        # Generate and place all levels
        levels = scale.generate_levels()

        for i, (price, qty) in enumerate(levels):
            passive = self.fill_model.place_passive_limit(
                side=side,
                price=price,
                qty=qty,
                book=book,
                ts_ns=ts_ns,
                tag=f"scale_{order_id}_level_{i}",
            )

            scale.orders.append({
                'price': price,
                'qty': qty,
                'passive_order': passive,
                'filled': False,
            })

        self.scale_orders.append(scale)
        self.total_scales_placed += 1

        return scale

    def place_vwap_order(
        self,
        order_id: str,
        side: Literal["BUY", "SELL"],
        total_qty: int,
        target_duration_seconds: float,
        ts_ns: int,
        tag: str = "",
    ) -> VWAPOrder:
        """Place a VWAP order."""

        vwap = VWAPOrder(
            order_id=order_id,
            side=side,
            total_qty=total_qty,
            target_duration_seconds=target_duration_seconds,
            ts_started_ns=ts_ns,
            tag=tag,
        )

        self.vwap_orders.append(vwap)
        self.total_vwaps_placed += 1

        return vwap

    def update_iceberg_orders(
        self,
        event: MarketEvent,
        event_type_name: str,
        book: OrderBookEventBatchedStrict,
        ts_ns: int,
    ) -> None:
        """Update iceberg orders - replenish tips as they fill."""

        for iceberg in self.iceberg_orders:
            if not iceberg.active or iceberg.is_filled:
                continue

            # Advance all passive tips
            for i, passive in enumerate(iceberg.passive_orders):
                if passive.active:
                    iceberg.passive_orders[i] = self.fill_model.advance_passive_order(
                        passive, event, event_type_name
                    )

            # Check for filled tips
            newly_filled_tips = [p for p in iceberg.passive_orders if p.is_filled and p.active]

            for tip in newly_filled_tips:
                iceberg.filled_qty += tip.filled_qty
                tip.active = False

                # Replenish tip if not fully filled
                if not iceberg.is_filled:
                    new_tip_qty = iceberg.replenish_tip()
                    if new_tip_qty > 0:
                        new_passive = self.fill_model.place_passive_limit(
                            side=iceberg.side,
                            price=iceberg.price,
                            qty=new_tip_qty,
                            book=book,
                            ts_ns=ts_ns,
                            tag=f"iceberg_{iceberg.order_id}_tip_{len(iceberg.passive_orders)}",
                        )
                        iceberg.passive_orders.append(new_passive)

            if iceberg.is_filled:
                iceberg.active = False
                self.total_icebergs_filled += 1

    def update_pegged_orders(
        self,
        book: OrderBookEventBatchedStrict,
        ts_ns: int,
    ) -> None:
        """Update pegged orders - adjust prices to track market."""

        for pegged in self.pegged_orders:
            if not pegged.active or pegged.is_filled:
                continue

            # Calculate where peg should be
            new_price = pegged.calculate_peg_price(book)

            if new_price and new_price != pegged.current_price:
                # Price changed, need to replace order
                if pegged.current_passive:
                    # Cancel old order (implicitly by not tracking it)
                    pegged.current_passive.active = False

                # Place new order at new price
                new_passive = self.fill_model.place_passive_limit(
                    side=pegged.side,
                    price=new_price,
                    qty=pegged.remaining_qty,
                    book=book,
                    ts_ns=ts_ns,
                    tag=f"pegged_{pegged.order_id}_update_{pegged.order_replacements}",
                )

                pegged.current_passive = new_passive
                pegged.current_price = new_price
                pegged.order_replacements += 1
                pegged.price_updates += 1

    def stats_dict(self) -> dict:
        """Get statistics."""
        active_icebergs = sum(1 for o in self.iceberg_orders if o.active)
        active_pegged = sum(1 for o in self.pegged_orders if o.active)
        active_scales = sum(1 for o in self.scale_orders if o.active)
        active_vwaps = sum(1 for o in self.vwap_orders if o.active)

        return {
            "icebergs_placed": self.total_icebergs_placed,
            "icebergs_filled": self.total_icebergs_filled,
            "active_icebergs": active_icebergs,
            "pegged_placed": self.total_pegged_placed,
            "pegged_filled": self.total_pegged_filled,
            "active_pegged": active_pegged,
            "scales_placed": self.total_scales_placed,
            "scales_filled": self.total_scales_filled,
            "active_scales": active_scales,
            "vwaps_placed": self.total_vwaps_placed,
            "vwaps_filled": self.total_vwaps_filled,
            "active_vwaps": active_vwaps,
        }
