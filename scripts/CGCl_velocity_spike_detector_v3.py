#!/usr/bin/env python3
"""
Velocity Spike Detector V3 - CLICKHOUSE MATERIALIZED VIEW

Uses pre-aggregated millisecond buckets from mnq_mbo_ms_buckets table.
Massive performance improvement over Python aggregation.

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

import subprocess
import json
from dataclasses import dataclass
from collections import deque
from datetime import datetime
import statistics


@dataclass
class MillisecondBucket:
    """Pre-aggregated millisecond bucket from ClickHouse."""
    ts_ms: int
    first_price: float
    last_price: float
    high: float
    low: float
    buy_volume: int
    sell_volume: int
    trade_count: int

    @property
    def delta(self) -> int:
        return self.buy_volume - self.sell_volume

    @property
    def price_change(self) -> float:
        return abs(self.last_price - self.first_price)


@dataclass
class VelocityStats:
    """Velocity statistics over a time window."""
    lookback_ms: int
    price_change: float
    duration_ms: float
    velocity: float
    energy: float
    buy_volume: int
    sell_volume: int
    delta_magnitude: int
    delta_normalized: float


def load_millisecond_buckets(date_str: str, hour_start: int = 9, hour_end: int = 16) -> list[MillisecondBucket]:
    """Load pre-aggregated millisecond buckets from ClickHouse materialized view."""
    print(f"Loading millisecond buckets for {date_str} ({hour_start}:00-{hour_end}:00)...", flush=True)

    # Convert hour range to millisecond timestamps
    # Note: ts_ms in the table is UTC epoch milliseconds
    query = f"""
    SELECT
        ts_ms,
        first_price,
        last_price,
        high,
        low,
        buy_volume,
        sell_volume,
        trade_count
    FROM mnq_mbo_ms_buckets
    WHERE symbol = 'MNQZ5'
      AND date = '{date_str}'
      AND hour(toDateTime(ts_ms / 1000)) >= {hour_start}
      AND hour(toDateTime(ts_ms / 1000)) < {hour_end}
    ORDER BY ts_ms
    FORMAT JSON
    """

    result = subprocess.run(
        ["clickhouse-client", "--query", query],
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        raise Exception(f"Query failed: {result.stderr}")

    data = json.loads(result.stdout)

    buckets = []
    for row in data["data"]:
        buckets.append(MillisecondBucket(
            ts_ms=int(row["ts_ms"]),
            first_price=float(row["first_price"]),
            last_price=float(row["last_price"]),
            high=float(row["high"]),
            low=float(row["low"]),
            buy_volume=int(row["buy_volume"]),
            sell_volume=int(row["sell_volume"]),
            trade_count=int(row["trade_count"]),
        ))

    print(f"  Loaded {len(buckets):,} millisecond buckets", flush=True)
    return buckets


def calculate_velocity_stats(buckets: deque, lookback_ms: int) -> VelocityStats:
    """Calculate velocity statistics over last N milliseconds."""
    if len(buckets) < 2:
        return None

    # Get time window
    last_ts = buckets[-1].ts_ms
    first_ts = buckets[0].ts_ms

    # Filter buckets within lookback window
    window_start_ts = last_ts - lookback_ms
    window = [b for b in buckets if b.ts_ms >= window_start_ts]

    if len(window) < 2:
        return None

    # Price movement
    first_price = window[0].first_price
    last_price = window[-1].last_price
    price_change = abs(last_price - first_price)

    # Time elapsed in milliseconds
    duration_ms = window[-1].ts_ms - window[0].ts_ms

    if duration_ms == 0:
        return None

    duration_sec = duration_ms / 1000.0

    # Velocity (ticks/second)
    price_change_ticks = price_change * 4  # MNQ: 1 point = 4 ticks
    velocity = price_change_ticks / duration_sec

    # Energy
    energy = (price_change_ticks ** 2) / duration_sec

    # Delta magnitude
    buy_volume = sum(b.buy_volume for b in window)
    sell_volume = sum(b.sell_volume for b in window)
    delta_magnitude = abs(buy_volume - sell_volume)
    total_volume = buy_volume + sell_volume
    delta_normalized = delta_magnitude / total_volume if total_volume > 0 else 0

    return VelocityStats(
        lookback_ms=lookback_ms,
        price_change=price_change,
        duration_ms=duration_ms,
        velocity=velocity,
        energy=energy,
        buy_volume=buy_volume,
        sell_volume=sell_volume,
        delta_magnitude=delta_magnitude,
        delta_normalized=delta_normalized,
    )


def analyze_baseline_period(date_str: str, hour_start: int = 8, hour_end: int = 9) -> dict:
    """Analyze pre-market period to establish baseline statistics."""
    print(f"\n{'='*80}")
    print(f"BASELINE ANALYSIS: {date_str} {hour_start}:00-{hour_end}:00 (Pre-Market)")
    print(f"{'='*80}\n")

    buckets = load_millisecond_buckets(date_str, hour_start, hour_end)

    if len(buckets) == 0:
        print("  WARNING: No buckets in pre-market period!")
        return {}

    # Lookback periods in milliseconds
    lookback_periods = [100, 300, 1000, 3000, 10000, 30000, 100000]
    baselines = {}

    # Collect statistics for each lookback period
    for lookback in lookback_periods:
        velocities = []
        energies = []
        delta_mags = []
        delta_norms = []

        # Sliding window through buckets
        for i in range(len(buckets)):
            # Get buckets within lookback window
            window_start_ts = buckets[i].ts_ms - lookback
            window_buckets = [b for b in buckets[:i+1] if b.ts_ms >= window_start_ts]

            if len(window_buckets) < 2:
                continue

            stats = calculate_velocity_stats(deque(window_buckets), lookback)
            if stats and stats.velocity > 0:
                velocities.append(stats.velocity)
                energies.append(stats.energy)
                delta_mags.append(stats.delta_magnitude)
                delta_norms.append(stats.delta_normalized)

        if velocities:
            baselines[lookback] = {
                'velocity': {
                    'mean': statistics.mean(velocities),
                    'median': statistics.median(velocities),
                    'p90': statistics.quantiles(velocities, n=10)[8] if len(velocities) > 10 else max(velocities),
                    'p95': statistics.quantiles(velocities, n=20)[18] if len(velocities) > 20 else max(velocities),
                    'max': max(velocities),
                },
                'energy': {
                    'mean': statistics.mean(energies),
                    'median': statistics.median(energies),
                    'p90': statistics.quantiles(energies, n=10)[8] if len(energies) > 10 else max(energies),
                    'p95': statistics.quantiles(energies, n=20)[18] if len(energies) > 20 else max(energies),
                    'max': max(energies),
                },
                'delta_mag': {
                    'mean': statistics.mean(delta_mags),
                    'median': statistics.median(delta_mags),
                    'p90': statistics.quantiles(delta_mags, n=10)[8] if len(delta_mags) > 10 else max(delta_mags),
                    'p95': statistics.quantiles(delta_mags, n=20)[18] if len(delta_mags) > 20 else max(delta_mags),
                },
                'delta_norm': {
                    'mean': statistics.mean(delta_norms),
                    'median': statistics.median(delta_norms),
                    'p90': statistics.quantiles(delta_norms, n=10)[8] if len(delta_norms) > 10 else max(delta_norms),
                    'p95': statistics.quantiles(delta_norms, n=20)[18] if len(delta_norms) > 20 else max(delta_norms),
                },
            }

            # Convert ms to readable format
            if lookback < 1000:
                period_str = f"{lookback}ms"
            else:
                period_str = f"{lookback/1000:.0f}s"

            print(f"\n{period_str} Window:")
            print(f"  Velocity (ticks/sec): Mean={baselines[lookback]['velocity']['mean']:.2f}, "
                  f"P95={baselines[lookback]['velocity']['p95']:.2f}")
            print(f"  Energy: Mean={baselines[lookback]['energy']['mean']:.1f}, "
                  f"P95={baselines[lookback]['energy']['p95']:.1f}")
            print(f"  Delta Normalized: Mean={baselines[lookback]['delta_norm']['mean']:.3f}, "
                  f"P95={baselines[lookback]['delta_norm']['p95']:.3f}")

    return baselines


def detect_spikes_live(date_str: str, baselines: dict,
                       hour_start: int = 9, hour_end: int = 16,
                       velocity_multiplier: float = 2.0,
                       energy_multiplier: float = 2.0,
                       delta_norm_threshold: float = 0.03,
                       check_interval_ms: int = 1000) -> list:
    """Detect velocity spikes using pre-aggregated millisecond buckets."""
    print(f"\n{'='*80}")
    print(f"LIVE SPIKE DETECTION: {date_str} {hour_start}:00-{hour_end}:00")
    print(f"{'='*80}\n")

    print(f"Spike Criteria:")
    print(f"  Velocity > P95 × {velocity_multiplier}")
    print(f"  Energy > P95 × {energy_multiplier}")
    print(f"  Delta Normalized > {delta_norm_threshold}")
    print(f"  Check interval: {check_interval_ms}ms\n")

    buckets = load_millisecond_buckets(date_str, hour_start, hour_end)

    if len(buckets) == 0:
        print("  No buckets to analyze!")
        return []

    lookback_periods = [100, 300, 1000, 3000, 10000, 30000, 100000]
    spikes = []
    spike_count = 0
    last_check_ts = 0

    print(f"Processing {len(buckets):,} buckets...")

    for i, bucket in enumerate(buckets):
        # Check periodically
        if bucket.ts_ms - last_check_ts < check_interval_ms:
            continue

        last_check_ts = bucket.ts_ms

        if i % 10000 == 0 and i > 0:
            ts = datetime.fromtimestamp(bucket.ts_ms / 1000.0)
            print(f"  [{i:,}/{len(buckets):,}] {ts.strftime('%H:%M:%S')} - {spike_count} spikes", flush=True)

        # Check each lookback period
        for lookback in lookback_periods:
            if lookback not in baselines:
                continue

            # Get buckets within lookback window
            window_start_ts = bucket.ts_ms - lookback
            window_buckets = [b for b in buckets[:i+1] if b.ts_ms >= window_start_ts]

            if len(window_buckets) < 2:
                continue

            stats = calculate_velocity_stats(deque(window_buckets), lookback)
            if not stats:
                continue

            # Get thresholds
            vel_threshold = baselines[lookback]['velocity']['p95'] * velocity_multiplier
            energy_threshold = baselines[lookback]['energy']['p95'] * energy_multiplier

            # Check if spike
            is_velocity_spike = stats.velocity > vel_threshold
            is_energy_spike = stats.energy > energy_threshold
            is_delta_spike = stats.delta_normalized > delta_norm_threshold

            # All three must trigger
            if is_velocity_spike and is_energy_spike and is_delta_spike:
                spike_count += 1
                ts = datetime.fromtimestamp(bucket.ts_ms / 1000.0)
                direction = "BUY" if stats.buy_volume > stats.sell_volume else "SELL"

                lookback_str = f"{lookback}ms" if lookback < 1000 else f"{lookback/1000:.0f}s"

                spike_info = {
                    'spike_id': spike_count,
                    'timestamp': ts.strftime('%H:%M:%S.%f')[:-3],
                    'lookback': lookback,
                    'lookback_str': lookback_str,
                    'price': bucket.last_price,
                    'direction': direction,
                    'velocity': stats.velocity,
                    'energy': stats.energy,
                    'delta_normalized': stats.delta_normalized,
                    'buy_volume': stats.buy_volume,
                    'sell_volume': stats.sell_volume,
                    'price_change': stats.price_change,
                    'duration_ms': stats.duration_ms,
                }

                spikes.append(spike_info)

                print(f"\n🚀 SPIKE #{spike_count} @ {spike_info['timestamp']}")
                print(f"   {lookback_str} | Price: {bucket.last_price:.2f} | {direction}")
                print(f"   Vel: {stats.velocity:.1f} (>{vel_threshold:.1f}) | "
                      f"Energy: {stats.energy:.0f} (>{energy_threshold:.0f}) | "
                      f"Delta: {stats.delta_normalized:.3f}")

                # Only report shortest lookback that triggers
                break

    print(f"\n{'='*80}")
    print(f"TOTAL SPIKES: {spike_count}")
    print(f"{'='*80}\n")

    return spikes


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 18 + "VELOCITY SPIKE DETECTOR V3" + " " * 34 + "║")
    print("║" + " " * 21 + "(ClickHouse Optimized)" + " " * 36 + "║")
    print("╚" + "=" * 78 + "╝")

    date_str = "2025-10-10"

    # Step 1: Baseline from pre-market
    baselines = analyze_baseline_period(date_str, hour_start=8, hour_end=9)

    if not baselines:
        print("\n❌ No baseline data available")
        return

    # Step 2: Detect spikes during RTH
    spikes = detect_spikes_live(
        date_str,
        baselines,
        hour_start=9,
        hour_end=16,
        velocity_multiplier=2.0,
        energy_multiplier=2.0,
        delta_norm_threshold=0.03,
        check_interval_ms=1000,  # Check every second
    )

    # Summary
    if spikes:
        print(f"\n📊 SPIKE SUMMARY:")
        print(f"   Total: {len(spikes)}")

        by_lookback = {}
        for s in spikes:
            by_lookback[s['lookback_str']] = by_lookback.get(s['lookback_str'], 0) + 1

        print(f"\n   By lookback:")
        for lb in sorted(by_lookback.keys()):
            print(f"     {lb:>6s}: {by_lookback[lb]:>3d} spikes")

        buy_count = sum(1 for s in spikes if s['direction'] == 'BUY')
        sell_count = sum(1 for s in spikes if s['direction'] == 'SELL')
        print(f"\n   Direction: BUY={buy_count}, SELL={sell_count}")
        print(f"   Avg velocity: {statistics.mean(s['velocity'] for s in spikes):.1f} ticks/sec")

    print("\n" + "=" * 80)
    print("✅ COMPLETE")
    print("=" * 80)


if __name__ == "__main__":
    main()
