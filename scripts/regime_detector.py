#!/usr/bin/env python3
"""
Regime Detection Engine
Determines: Directional Trend vs Rotational vs Chaos
Based on opening range, delta efficiency, VWAP behavior, wall structure
"""

import pandas as pd
import numpy as np
from collections import deque
from datetime import datetime, time
from enum import Enum


class RegimeState(Enum):
    """Market regime states"""
    OPENING_DISCOVERY = "opening_discovery"      # 9:30-9:45 observe only
    TREND_MODE = "trend"                         # Directional auction
    ROTATION_MODE = "rotation"                   # Two-way facilitation
    CHAOS_MODE = "chaos"                         # Manipulation/unstable


class RegimeDetector:
    """
    Detects market regime and signals strategy switches

    Trend Signals:
    - Clean OR breakouts that hold
    - Delta producing displacement
    - Price stays away from VWAP
    - Asymmetric wall consumption

    Rotation Signals:
    - OR breaks fail to hold
    - Delta without displacement (absorption)
    - VWAP constantly reclaimed
    - Symmetric wall structure
    - Failed breakouts
    """

    def __init__(self, params=None):
        self.params = params or {
            # Opening range
            'or_start_time': time(9, 30),
            'or_end_time': time(9, 45),
            'or_qualification_time': time(10, 0),  # By when to classify

            # Breakout qualification
            'min_breakout_hold_minutes': 10,       # Must hold outside OR
            'failed_breakout_threshold': 3,        # 3+ failures → rotation

            # Delta efficiency
            'min_displacement_per_delta': 0.5,     # Points per 100 delta
            'absorption_count_threshold': 3,       # Failed delta pushes

            # VWAP behavior
            'vwap_reclaim_threshold': 3,           # Crosses per hour → rotation
            'trend_vwap_distance_pts': 10.0,       # Trending stays > 10pts from VWAP

            # Wall structure
            'wall_asymmetry_ratio': 2.0,           # Bid/Ask ratio for trend
            'wall_balance_range': 0.5,             # Within 0.5 = balanced

            # Chaos detection
            'max_reversal_rate': 5,                # Reversals per hour
            'max_volatility_z': 3.0,               # Z-score volatility spike
        }

        self.state = RegimeState.OPENING_DISCOVERY
        self.state_history = []

        # Opening range tracking
        self.or_high = None
        self.or_low = None
        self.or_established = False

        # Breakout tracking
        self.breakout_attempts = []
        self.failed_breakouts = 0
        self.successful_breakouts = 0

        # Delta efficiency tracking
        self.delta_displacement_samples = deque(maxlen=20)
        self.absorption_count = 0

        # VWAP tracking
        self.vwap = None
        self.vwap_crosses = []
        self.vwap_distance_samples = deque(maxlen=20)

        # Wall structure tracking
        self.wall_bid_sizes = deque(maxlen=10)
        self.wall_ask_sizes = deque(maxlen=10)

        # Chaos tracking
        self.recent_reversals = []
        self.volatility_samples = deque(maxlen=20)

        # Session tracking
        self.session_start = None
        self.last_price = None
        self.last_direction = None  # 1=up, -1=down

    def update(self, timestamp, price, walls, aggression, vwap=None):
        """
        Main update - processes new bar and updates regime state

        Args:
            timestamp: Current bar timestamp
            price: Current price
            walls: Dict with 'bid_size', 'ask_size', 'bid_prices', 'ask_prices'
            aggression: Dict with 'delta', 'buy_vol', 'sell_vol'
            vwap: Optional VWAP value

        Returns:
            RegimeState
        """
        current_time = timestamp.time()

        # Initialize session
        if self.session_start is None or timestamp.date() != self.session_start.date():
            self._reset_session(timestamp)

        # Update VWAP
        if vwap is not None:
            self.vwap = vwap
            self._track_vwap_behavior(price)

        # STATE 1: OPENING DISCOVERY (9:30-9:45)
        if self.params['or_start_time'] <= current_time < self.params['or_end_time']:
            self.state = RegimeState.OPENING_DISCOVERY
            self._update_opening_range(price)
            return self.state

        # Establish OR once period ends
        if current_time >= self.params['or_end_time'] and not self.or_established:
            self._finalize_opening_range()

        # Update tracking metrics
        self._track_delta_efficiency(aggression, price)
        self._track_wall_structure(walls)
        self._track_chaos_signals(price)

        # STATE 2: DIRECTIONAL QUALIFICATION (9:45-10:00)
        if current_time < self.params['or_qualification_time']:
            self._evaluate_breakout_attempts(price)
            return self.state

        # STATE 3+: REGIME CLASSIFICATION
        self.state = self._classify_regime()

        self.last_price = price
        return self.state

    def _reset_session(self, timestamp):
        """Reset for new trading session"""
        self.session_start = timestamp
        self.or_high = None
        self.or_low = None
        self.or_established = False
        self.breakout_attempts.clear()
        self.failed_breakouts = 0
        self.successful_breakouts = 0
        self.absorption_count = 0
        self.vwap_crosses.clear()
        self.recent_reversals.clear()
        self.last_price = None
        self.last_direction = None
        self.state = RegimeState.OPENING_DISCOVERY

    def _update_opening_range(self, price):
        """Track OR high/low during opening period"""
        if self.or_high is None:
            self.or_high = price
            self.or_low = price
        else:
            self.or_high = max(self.or_high, price)
            self.or_low = min(self.or_low, price)

    def _finalize_opening_range(self):
        """Lock in OR after period ends"""
        if self.or_high is not None and self.or_low is not None:
            self.or_established = True
            or_size = self.or_high - self.or_low
            # Ignore if OR is unreasonably small (< 2 pts) or large (> 50 pts)
            if or_size < 2.0 or or_size > 50.0:
                self.or_high = None
                self.or_low = None
                self.or_established = False

    def _evaluate_breakout_attempts(self, price):
        """Track breakout attempts during qualification period"""
        if not self.or_established:
            return

        # Check if breaking OR
        breaking_high = price > self.or_high
        breaking_low = price < self.or_low

        if breaking_high or breaking_low:
            direction = 'up' if breaking_high else 'down'

            # Record breakout attempt
            if not self.breakout_attempts or self.breakout_attempts[-1]['direction'] != direction:
                self.breakout_attempts.append({
                    'direction': direction,
                    'start_price': price,
                    'max_displacement': 0,
                    'held': False
                })

        # Update displacement for active breakout
        if self.breakout_attempts:
            current = self.breakout_attempts[-1]
            if current['direction'] == 'up':
                current['max_displacement'] = max(current['max_displacement'],
                                                 price - self.or_high)
            else:
                current['max_displacement'] = max(current['max_displacement'],
                                                 self.or_low - price)

            # Check if returned inside OR (failed)
            if current['direction'] == 'up' and price < self.or_high:
                if not current['held']:
                    self.failed_breakouts += 1
                    current['held'] = False
            elif current['direction'] == 'down' and price > self.or_low:
                if not current['held']:
                    self.failed_breakouts += 1
                    current['held'] = False
            else:
                # Still outside OR
                if current['max_displacement'] >= 3.0:  # Held at least 3 pts
                    current['held'] = True
                    self.successful_breakouts += 1

    def _track_delta_efficiency(self, aggression, price):
        """Track if delta produces displacement or gets absorbed"""
        if self.last_price is None or abs(aggression['delta']) < 50:
            return

        displacement = price - self.last_price
        delta = aggression['delta']

        # Efficiency = displacement per 100 delta
        efficiency = (displacement / (abs(delta) / 100.0)) if delta != 0 else 0

        self.delta_displacement_samples.append(efficiency)

        # Absorption = large delta but small/opposite displacement
        if abs(delta) > 100:
            expected_direction = 1 if delta > 0 else -1
            actual_direction = 1 if displacement > 0 else -1 if displacement < 0 else 0

            if actual_direction != expected_direction or abs(displacement) < 1.0:
                self.absorption_count += 1

    def _track_vwap_behavior(self, price):
        """Track VWAP crosses and distance"""
        if self.vwap is None or self.last_price is None:
            return

        # Detect cross
        last_above = self.last_price > self.vwap
        now_above = price > self.vwap

        if last_above != now_above:
            self.vwap_crosses.append(datetime.now())

        # Track distance
        distance = abs(price - self.vwap)
        self.vwap_distance_samples.append(distance)

    def _track_wall_structure(self, walls):
        """Track wall asymmetry (trend) vs balance (rotation)"""
        bid_size = walls.get('bid_size', 0)
        ask_size = walls.get('ask_size', 0)

        if bid_size > 0:
            self.wall_bid_sizes.append(bid_size)
        if ask_size > 0:
            self.wall_ask_sizes.append(ask_size)

    def _track_chaos_signals(self, price):
        """Track reversal rate and volatility spikes"""
        if self.last_price is None:
            return

        # Track direction changes (reversals)
        current_direction = 1 if price > self.last_price else -1 if price < self.last_price else 0

        if self.last_direction is not None and current_direction != 0:
            if current_direction != self.last_direction:
                self.recent_reversals.append(datetime.now())

        if current_direction != 0:
            self.last_direction = current_direction

        # Track volatility
        if self.last_price != 0:
            pct_change = abs((price - self.last_price) / self.last_price) * 100
            self.volatility_samples.append(pct_change)

    def _classify_regime(self):
        """
        Classify current regime based on accumulated evidence

        Priority:
        1. CHAOS (highest risk)
        2. TREND (directional)
        3. ROTATION (mean reversion)
        """

        # Check for CHAOS first
        if self._is_chaos():
            return RegimeState.CHAOS_MODE

        # Count evidence for TREND vs ROTATION
        trend_score = 0
        rotation_score = 0

        # 1. OR Breakout behavior
        if self.failed_breakouts >= self.params['failed_breakout_threshold']:
            rotation_score += 2  # Strong signal
        elif self.successful_breakouts > self.failed_breakouts:
            trend_score += 2

        # 2. Delta efficiency
        if len(self.delta_displacement_samples) >= 5:
            avg_efficiency = np.mean(list(self.delta_displacement_samples))
            if avg_efficiency < self.params['min_displacement_per_delta']:
                rotation_score += 1  # Poor efficiency = absorption
            else:
                trend_score += 1

        if self.absorption_count >= self.params['absorption_count_threshold']:
            rotation_score += 1

        # 3. VWAP behavior
        # Count recent crosses (last 60 min)
        recent_crosses = [c for c in self.vwap_crosses
                         if (datetime.now() - c).total_seconds() < 3600]
        crosses_per_hour = len(recent_crosses)

        if crosses_per_hour >= self.params['vwap_reclaim_threshold']:
            rotation_score += 2  # Strong signal

        # Check VWAP distance
        if len(self.vwap_distance_samples) >= 5:
            avg_distance = np.mean(list(self.vwap_distance_samples))
            if avg_distance > self.params['trend_vwap_distance_pts']:
                trend_score += 1  # Staying away from VWAP = trend
            else:
                rotation_score += 1  # Hugging VWAP = rotation

        # 4. Wall structure
        if len(self.wall_bid_sizes) >= 3 and len(self.wall_ask_sizes) >= 3:
            avg_bid = np.mean(list(self.wall_bid_sizes))
            avg_ask = np.mean(list(self.wall_ask_sizes))

            if avg_bid > 0 and avg_ask > 0:
                ratio = max(avg_bid, avg_ask) / min(avg_bid, avg_ask)

                if ratio >= self.params['wall_asymmetry_ratio']:
                    trend_score += 1  # Asymmetric = one side dominating
                elif ratio <= (1.0 + self.params['wall_balance_range']):
                    rotation_score += 1  # Balanced = rotation

        # Classify based on scores
        if trend_score > rotation_score:
            return RegimeState.TREND_MODE
        else:
            return RegimeState.ROTATION_MODE

    def _is_chaos(self):
        """Detect chaos/manipulation regime"""
        # Check reversal rate
        recent_reversals = [r for r in self.recent_reversals
                           if (datetime.now() - r).total_seconds() < 3600]
        reversal_rate = len(recent_reversals)

        if reversal_rate >= self.params['max_reversal_rate']:
            return True

        # Check volatility spikes
        if len(self.volatility_samples) >= 10:
            mean_vol = np.mean(list(self.volatility_samples))
            std_vol = np.std(list(self.volatility_samples))

            if std_vol > 0:
                recent_vols = list(self.volatility_samples)[-5:]
                max_recent = max(recent_vols)
                z_score = (max_recent - mean_vol) / std_vol

                if z_score >= self.params['max_volatility_z']:
                    return True

        return False

    def get_state_summary(self):
        """Return detailed regime state for logging"""
        return {
            'regime': self.state.value,
            'or_high': self.or_high,
            'or_low': self.or_low,
            'failed_breakouts': self.failed_breakouts,
            'successful_breakouts': self.successful_breakouts,
            'absorption_count': self.absorption_count,
            'vwap_crosses_1h': len([c for c in self.vwap_crosses
                                   if (datetime.now() - c).total_seconds() < 3600]),
            'avg_vwap_distance': np.mean(list(self.vwap_distance_samples))
                                if self.vwap_distance_samples else 0,
            'wall_asymmetry': (max(list(self.wall_bid_sizes) + list(self.wall_ask_sizes)) /
                              min(list(self.wall_bid_sizes) + list(self.wall_ask_sizes)))
                             if self.wall_bid_sizes and self.wall_ask_sizes else 1.0,
        }
