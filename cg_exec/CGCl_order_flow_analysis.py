"""
Order Flow Analysis and Market Microstructure Detection.

Analyzes order book events to detect:
1. Order flow imbalance (buy vs sell pressure)
2. Absorption (large passive orders absorbing aggression)
3. Iceberg detection (repeated fills at same price)
4. Spoofing patterns (orders that cancel before fill)
5. Sweep detection (aggressive taker sweeping multiple levels)
6. Delta analysis (buy volume vs sell volume)

Provides real-time market insights for execution decisions.

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

from dataclasses import dataclass, field
from collections import defaultdict, deque
from typing import Literal, Optional

from cg_sim.models import MarketEvent


@dataclass(slots=True)
class OrderFlowSignal:
    """A detected order flow event/pattern."""

    signal_type: str  # "imbalance", "absorption", "iceberg", "sweep", etc.
    side: str  # "BID" or "ASK"
    price: Optional[float]
    strength: float  # 0-100 score
    ts_ns: int
    details: dict = field(default_factory=dict)


@dataclass(slots=True)
class LevelSnapshot:
    """Snapshot of a price level at a point in time."""

    price: float
    bid_size: int = 0
    ask_size: int = 0
    bid_orders: int = 0
    ask_orders: int = 0
    ts_ns: int = 0


@dataclass
class OrderFlowAnalyzer:
    """
    Analyzes order flow in real-time.

    Tracks market microstructure and detects actionable patterns.
    """

    # Configuration
    lookback_events: int = 1000
    imbalance_threshold: float = 2.0  # Ratio for significant imbalance
    absorption_threshold: int = 50  # Min size for absorption detection
    iceberg_threshold: int = 5  # Min fills to detect iceberg

    # Event history
    recent_events: deque = field(default_factory=lambda: deque(maxlen=1000))

    # Price level tracking
    level_history: dict[float, list[LevelSnapshot]] = field(default_factory=dict)

    # Order flow metrics
    bid_volume: int = 0
    ask_volume: int = 0
    bid_aggressive: int = 0  # Buy market orders
    ask_aggressive: int = 0  # Sell market orders

    # Delta tracking
    cumulative_delta: int = 0
    delta_history: deque = field(default_factory=lambda: deque(maxlen=100))

    # Level-specific tracking
    fills_by_level: dict[float, list[dict]] = field(default_factory=lambda: defaultdict(list))
    cancels_by_level: dict[float, int] = field(default_factory=lambda: defaultdict(int))
    adds_by_level: dict[float, int] = field(default_factory=lambda: defaultdict(int))

    # Detected signals
    signals: list[OrderFlowSignal] = field(default_factory=list)

    # Statistics
    total_events_processed: int = 0
    imbalances_detected: int = 0
    absorptions_detected: int = 0
    icebergs_detected: int = 0
    sweeps_detected: int = 0

    def process_event(
        self,
        event: MarketEvent,
        event_type_name: str,
        best_bid: Optional[float],
        best_ask: Optional[float],
    ) -> list[OrderFlowSignal]:
        """
        Process an order book event and detect patterns.

        Returns:
            List of detected signals
        """
        self.recent_events.append((event, event_type_name))
        self.total_events_processed += 1

        new_signals = []

        # Update volume tracking
        if event.size and event.size > 0:
            if event.side == "BID":
                self.bid_volume += event.size
            elif event.side == "ASK":
                self.ask_volume += event.size

        # Track aggressive trades (takers)
        if event_type_name == "trade_aggressor":
            if event.side == "BID":
                self.bid_aggressive += event.size or 0
            elif event.side == "ASK":
                self.ask_aggressive += event.size or 0

            # Update delta
            if event.side == "BID":
                self.cumulative_delta += event.size or 0
            else:
                self.cumulative_delta -= event.size or 0

            self.delta_history.append(self.cumulative_delta)

        # Track fills by level (for iceberg detection)
        if event_type_name == "fill_resting" and event.price:
            self.fills_by_level[event.price].append({
                'ts_ns': event.ts_event_ns,
                'size': event.size or 0,
                'side': event.side,
            })

            # Check for iceberg pattern
            iceberg_signal = self._detect_iceberg(event.price, event.ts_event_ns)
            if iceberg_signal:
                new_signals.append(iceberg_signal)

        # Track cancels (for spoofing detection)
        if event_type_name == "cancel" and event.price:
            self.cancels_by_level[event.price] += event.size or 0

        # Track adds
        if event_type_name == "add" and event.price:
            self.adds_by_level[event.price] += event.size or 0

        # Detect order flow imbalance
        if best_bid and best_ask:
            imbalance_signal = self._detect_imbalance(best_bid, best_ask, event.ts_event_ns)
            if imbalance_signal:
                new_signals.append(imbalance_signal)

        # Detect absorption
        if event_type_name == "fill_resting" and event.price and event.size:
            absorption_signal = self._detect_absorption(event, best_bid, best_ask)
            if absorption_signal:
                new_signals.append(absorption_signal)

        # Detect sweep
        if event_type_name == "trade_aggressor":
            sweep_signal = self._detect_sweep(event.ts_event_ns)
            if sweep_signal:
                new_signals.append(sweep_signal)

        # Store signals
        self.signals.extend(new_signals)

        return new_signals

    def _detect_imbalance(
        self,
        best_bid: float,
        best_ask: float,
        ts_ns: int,
    ) -> Optional[OrderFlowSignal]:
        """Detect order flow imbalance at best bid/ask."""

        if self.ask_volume == 0 or self.bid_volume == 0:
            return None

        bid_ask_ratio = self.bid_volume / self.ask_volume
        ask_bid_ratio = self.ask_volume / self.bid_volume

        if bid_ask_ratio >= self.imbalance_threshold:
            # Strong buying pressure
            self.imbalances_detected += 1
            return OrderFlowSignal(
                signal_type="imbalance",
                side="BID",
                price=best_bid,
                strength=min(100, bid_ask_ratio * 20),
                ts_ns=ts_ns,
                details={
                    'bid_volume': self.bid_volume,
                    'ask_volume': self.ask_volume,
                    'ratio': bid_ask_ratio,
                },
            )

        elif ask_bid_ratio >= self.imbalance_threshold:
            # Strong selling pressure
            self.imbalances_detected += 1
            return OrderFlowSignal(
                signal_type="imbalance",
                side="ASK",
                price=best_ask,
                strength=min(100, ask_bid_ratio * 20),
                ts_ns=ts_ns,
                details={
                    'bid_volume': self.bid_volume,
                    'ask_volume': self.ask_volume,
                    'ratio': ask_bid_ratio,
                },
            )

        return None

    def _detect_absorption(
        self,
        event: MarketEvent,
        best_bid: Optional[float],
        best_ask: Optional[float],
    ) -> Optional[OrderFlowSignal]:
        """
        Detect absorption - large passive orders absorbing aggression.

        Indicates strong support/resistance.
        """

        if not event.size or event.size < self.absorption_threshold:
            return None

        if not event.price:
            return None

        # Check if at best bid/ask (key levels)
        at_key_level = (event.price == best_bid) or (event.price == best_ask)

        if at_key_level:
            self.absorptions_detected += 1
            return OrderFlowSignal(
                signal_type="absorption",
                side=event.side or "UNKNOWN",
                price=event.price,
                strength=min(100, (event.size / self.absorption_threshold) * 50),
                ts_ns=event.ts_event_ns,
                details={
                    'size': event.size,
                    'at_best': True,
                },
            )

        return None

    def _detect_iceberg(self, price: float, ts_ns: int) -> Optional[OrderFlowSignal]:
        """
        Detect iceberg orders - repeated fills at same price.

        Indicates hidden liquidity.
        """

        fills = self.fills_by_level[price]

        if len(fills) < self.iceberg_threshold:
            return None

        # Check recent fills (last 10 seconds)
        recent_fills = [f for f in fills if ts_ns - f['ts_ns'] < 10_000_000_000]

        if len(recent_fills) >= self.iceberg_threshold:
            total_filled = sum(f['size'] for f in recent_fills)
            self.icebergs_detected += 1

            return OrderFlowSignal(
                signal_type="iceberg",
                side=recent_fills[0]['side'],
                price=price,
                strength=min(100, len(recent_fills) * 10),
                ts_ns=ts_ns,
                details={
                    'num_fills': len(recent_fills),
                    'total_filled': total_filled,
                },
            )

        return None

    def _detect_sweep(self, ts_ns: int) -> Optional[OrderFlowSignal]:
        """
        Detect aggressive sweep through multiple levels.

        Indicates strong urgency/conviction.
        """

        # Look at recent aggressor trades
        recent_window = 1_000_000_000  # 1 second

        recent_aggressors = [
            (evt, evt_type)
            for evt, evt_type in self.recent_events
            if evt_type == "trade_aggressor" and ts_ns - evt.ts_event_ns < recent_window
        ]

        if len(recent_aggressors) >= 3:
            # Multiple aggressive trades in short time = sweep
            total_volume = sum(evt.size or 0 for evt, _ in recent_aggressors)

            # Determine dominant side
            bid_vol = sum(evt.size or 0 for evt, _ in recent_aggressors if evt.side == "BID")
            ask_vol = sum(evt.size or 0 for evt, _ in recent_aggressors if evt.side == "ASK")

            dominant_side = "BID" if bid_vol > ask_vol else "ASK"

            self.sweeps_detected += 1

            return OrderFlowSignal(
                signal_type="sweep",
                side=dominant_side,
                price=None,
                strength=min(100, len(recent_aggressors) * 15),
                ts_ns=ts_ns,
                details={
                    'num_trades': len(recent_aggressors),
                    'total_volume': total_volume,
                    'bid_volume': bid_vol,
                    'ask_volume': ask_vol,
                },
            )

        return None

    def get_delta(self) -> int:
        """Get current cumulative delta (buy volume - sell volume)."""
        return self.cumulative_delta

    def get_delta_trend(self, periods: int = 10) -> str:
        """
        Get delta trend over recent periods.

        Returns:
            "bullish", "bearish", or "neutral"
        """
        if len(self.delta_history) < periods:
            return "neutral"

        recent_deltas = list(self.delta_history)[-periods:]

        # Linear regression slope
        n = len(recent_deltas)
        x_mean = (n - 1) / 2
        y_mean = sum(recent_deltas) / n

        numerator = sum((i - x_mean) * (recent_deltas[i] - y_mean) for i in range(n))
        denominator = sum((i - x_mean) ** 2 for i in range(n))

        if denominator == 0:
            return "neutral"

        slope = numerator / denominator

        if slope > 5:
            return "bullish"
        elif slope < -5:
            return "bearish"
        else:
            return "neutral"

    def get_recent_signals(self, signal_type: Optional[str] = None, last_n: int = 10) -> list[OrderFlowSignal]:
        """Get recent signals, optionally filtered by type."""
        if signal_type:
            filtered = [s for s in self.signals if s.signal_type == signal_type]
            return filtered[-last_n:]
        else:
            return self.signals[-last_n:]

    def stats_dict(self) -> dict:
        """Get analysis statistics."""
        return {
            "events_processed": self.total_events_processed,
            "imbalances_detected": self.imbalances_detected,
            "absorptions_detected": self.absorptions_detected,
            "icebergs_detected": self.icebergs_detected,
            "sweeps_detected": self.sweeps_detected,
            "total_signals": len(self.signals),
            "cumulative_delta": self.cumulative_delta,
            "delta_trend": self.get_delta_trend(),
            "bid_volume": self.bid_volume,
            "ask_volume": self.ask_volume,
            "bid_aggressive": self.bid_aggressive,
            "ask_aggressive": self.ask_aggressive,
        }
