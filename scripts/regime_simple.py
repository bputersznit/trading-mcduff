#!/usr/bin/env python3
"""
Simple 2-State Regime Detector
ROTATION (default) → Use Wall Rejection
TREND (strong signals) → Use Wall Breakout
"""

import numpy as np
from collections import deque
from datetime import datetime, time


class RegimeSimple:
    """
    Simple regime classifier

    ROTATION (default):
    - Price oscillating
    - Hugging VWAP
    - Failed breakouts
    - Symmetric walls

    TREND (requires multiple confirmations):
    - Price making HH/LL
    - Staying away from VWAP
    - Successful breakouts
    - Persistent delta direction
    """

    def __init__(self):
        self.params = {
            # Trend qualification (need 2+ signals)
            'trend_vwap_distance': 8.0,        # Must be > 8pts from VWAP
            'trend_consistency_bars': 10,      # Look back N bars
            'trend_min_hh_ll': 3,              # Require 3+ HH or LL
            'trend_delta_persistence': 0.6,    # 60% of bars same delta sign

            # Session tracking
            'or_start': time(9, 30),
            'or_end': time(9, 45),
        }

        self.is_trend = False
        self.in_opening = False

        # Price structure tracking
        self.recent_prices = deque(maxlen=20)
        self.recent_deltas = deque(maxlen=20)
        self.recent_vwap_distances = deque(maxlen=20)

        # Session tracking
        self.session_date = None

    def update(self, timestamp, price, delta, vwap=None):
        """
        Update regime state

        Returns:
            'opening' - No trading (9:30-9:45)
            'trend' - Use breakout strategy
            'rotation' - Use rejection strategy (default)
        """
        current_time = timestamp.time()
        current_date = timestamp.date()

        # Reset on new session
        if current_date != self.session_date:
            self._reset_session(current_date)

        # Check if in opening range
        if self.params['or_start'] <= current_time < self.params['or_end']:
            self.in_opening = True
            return 'opening'
        else:
            self.in_opening = False

        # Track metrics
        self.recent_prices.append(price)
        self.recent_deltas.append(delta)

        if vwap is not None and vwap > 0:
            distance = abs(price - vwap)
            self.recent_vwap_distances.append(distance)

        # Need enough data to classify
        if len(self.recent_prices) < self.params['trend_consistency_bars']:
            return 'rotation'  # Default to rotation

        # Evaluate trend signals
        trend_score = 0

        # 1. VWAP Distance (strong signal)
        if len(self.recent_vwap_distances) >= 5:
            avg_distance = np.mean(list(self.recent_vwap_distances))
            if avg_distance > self.params['trend_vwap_distance']:
                trend_score += 2  # Strong signal

        # 2. Price Structure (Higher Highs / Lower Lows)
        hh_ll_score = self._count_hh_ll()
        if hh_ll_score >= self.params['trend_min_hh_ll']:
            trend_score += 2  # Strong signal

        # 3. Delta Persistence (sustained buying or selling)
        delta_persistence = self._calc_delta_persistence()
        if delta_persistence >= self.params['trend_delta_persistence']:
            trend_score += 1

        # Require 2+ trend signals to enter trend mode
        # Require 0-1 to exit trend mode (hysteresis)
        if trend_score >= 2:
            self.is_trend = True
        elif trend_score <= 1:
            self.is_trend = False

        return 'trend' if self.is_trend else 'rotation'

    def _reset_session(self, date):
        """Reset for new session"""
        self.session_date = date
        self.is_trend = False
        self.in_opening = False
        self.recent_prices.clear()
        self.recent_deltas.clear()
        self.recent_vwap_distances.clear()

    def _count_hh_ll(self):
        """
        Count higher highs or lower lows in recent prices

        Returns:
            Positive = higher highs (uptrend)
            Negative = lower lows (downtrend)
            Near zero = choppy
        """
        prices = list(self.recent_prices)

        if len(prices) < 6:
            return 0

        # Split into 3 segments and compare
        segment_size = len(prices) // 3

        seg1 = prices[:segment_size]
        seg2 = prices[segment_size:2*segment_size]
        seg3 = prices[2*segment_size:]

        high1, low1 = max(seg1), min(seg1)
        high2, low2 = max(seg2), min(seg2)
        high3, low3 = max(seg3), min(seg3)

        # Count higher highs
        hh_count = 0
        if high2 > high1:
            hh_count += 1
        if high3 > high2:
            hh_count += 1

        # Count lower lows
        ll_count = 0
        if low2 < low1:
            ll_count += 1
        if low3 < low2:
            ll_count += 1

        # Return net (positive = uptrend, negative = downtrend)
        return max(hh_count, ll_count)

    def _calc_delta_persistence(self):
        """
        Calculate what % of recent bars have same delta direction

        Returns:
            0.0-1.0 (1.0 = all same direction, 0.5 = random)
        """
        if len(self.recent_deltas) < 5:
            return 0.5

        deltas = list(self.recent_deltas)

        # Count positive vs negative
        positive = sum(1 for d in deltas if d > 0)
        negative = sum(1 for d in deltas if d < 0)

        # Return max persistence
        total = len(deltas)
        return max(positive, negative) / total if total > 0 else 0.5

    def get_state(self):
        """Get current state for logging"""
        return {
            'regime': 'trend' if self.is_trend else 'rotation',
            'in_opening': self.in_opening,
            'avg_vwap_distance': np.mean(list(self.recent_vwap_distances))
                                if self.recent_vwap_distances else 0,
            'hh_ll_count': self._count_hh_ll(),
            'delta_persistence': self._calc_delta_persistence(),
        }
