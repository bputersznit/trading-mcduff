#!/usr/bin/env python3
"""
Velocity Spike Detector V2 - MILLISECOND AGGREGATION

Key fix: Aggregate trades to millisecond buckets FIRST, then calculate velocity.
This avoids the divide-by-nanosecond problem where multiple trades at the same
nanosecond timestamp create artificial velocity spikes.

Approach:
1. Load MBO trade events (action='T'/'F', flags=0)
2. Aggregate to 1ms buckets (price, volume, delta per millisecond)
3. Calculate velocity over rolling time windows (100ms, 300ms, 1s, 3s, 10s, 30s, 100s)
4. Use pre-market to establish baselines
5. Detect spikes during RTH

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
from collections import defaultdict, deque
from datetime import datetime
import statistics


@dataclass
class MillisecondBucket:
    """Aggregated trade data for one millisecond."""
    ts_ms: int  # Millisecond timestamp
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
    lookback_ms: int  # Lookback window in milliseconds
    price_change: float  # Absolute price movement
    duration_ms: float  # Actual elapsed milliseconds
    velocity: float  # Ticks per second
    energy: float  # (Δprice)² / Δt
    buy_volume: int
    sell_volume: int
    delta_magnitude: int  # |buy - sell|
    delta_normalized: float  # delta / total volume


def load_and_aggregate_to_milliseconds(date_str: str, hour_start: int = 9, hour_end: int = 16) -> list[MillisecondBucket]:
    """Load MBO events and aggregate to millisecond buckets."""
    print(f"Loading MBO events for {date_str} ({hour_start}:00-{hour_end}:00)...", flush=True)

    query = f"""
    SELECT
        toInt64(toUnixTimestamp64Milli(ts_event)) as ts_ms,
        price,
        size,
        side,
        action,
        flags
    FROM mnq_mbo
    WHERE symbol = 'MNQZ5'
      AND toDate(ts_event) = '{date_str}'
      AND hour(ts_event) >= {hour_start}
      AND hour(ts_event) < {hour_end}
      AND action IN ('T', 'F')
      AND flags = 0
    ORDER BY ts_event
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

    print(f"  Loaded {len(data['data']):,} trade events", flush=True)

    # Aggregate to millisecond buckets
    ms_buckets = defaultdict(lambda: {
        'prices': [],
        'buy_volume': 0,
        'sell_volume': 0,
        'trade_count': 0
    })

    for row in data["data"]:
        ts_ms = int(row["ts_ms"])  # Already in milliseconds from query
        price = float(row["price"])
        size = int(row["size"])
        side = row["side"]

        bucket = ms_buckets[ts_ms]
        bucket['prices'].append(price)
        bucket['trade_count'] += 1

        if side == 'A':  # Ask side = buy aggressor
            bucket['buy_volume'] += size
        elif side == 'B':  # Bid side = sell aggressor
            bucket['sell_volume'] += size

    # Convert to list of MillisecondBucket objects
    buckets = []
    for ts_ms in sorted(ms_buckets.keys()):
        b = ms_buckets[ts_ms]
        buckets.append(MillisecondBucket(
            ts_ms=ts_ms,
            first_price=b['prices'][0],
            last_price=b['prices'][-1],
            high=max(b['prices']),
            low=min(b['prices']),
            buy_volume=b['buy_volume'],
            sell_volume=b['sell_volume'],
            trade_count=b['trade_count'],
        ))

    print(f"  Aggregated to {len(buckets):,} millisecond buckets", flush=True)
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
        return None  # Can't calculate velocity with zero time

    duration_sec = duration_ms / 1000.0

    # Velocity (ticks/second)
    # MNQ tick size = 0.25, so 1 point = 4 ticks
    price_change_ticks = price_change * 4
    velocity = price_change_ticks / duration_sec

    # Energy (emphasizes violent moves)
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

    buckets = load_and_aggregate_to_milliseconds(date_str, hour_start, hour_end)

    # Lookback periods in milliseconds
    # 100ms, 300ms, 1s, 3s, 10s, 30s, 100s
    lookback_periods = [100, 300, 1000, 3000, 10000, 30000, 100000]
    baselines = {}

    # Use deque with enough history for largest lookback
    max_lookback = max(lookback_periods)

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

            # Convert ms to readable format for display
            if lookback < 1000:
                period_str = f"{lookback}ms"
            else:
                period_str = f"{lookback/1000:.0f}s"

            print(f"\n{period_str} Window:")
            print(f"  Velocity (ticks/sec):")
            print(f"    Mean:   {baselines[lookback]['velocity']['mean']:>8.2f}")
            print(f"    Median: {baselines[lookback]['velocity']['median']:>8.2f}")
            print(f"    P90:    {baselines[lookback]['velocity']['p90']:>8.2f}")
            print(f"    P95:    {baselines[lookback]['velocity']['p95']:>8.2f}")
            print(f"    Max:    {baselines[lookback]['velocity']['max']:>8.2f}")

            print(f"  Energy (ticks²/sec):")
            print(f"    Mean:   {baselines[lookback]['energy']['mean']:>8.1f}")
            print(f"    Median: {baselines[lookback]['energy']['median']:>8.1f}")
            print(f"    P90:    {baselines[lookback]['energy']['p90']:>8.1f}")
            print(f"    P95:    {baselines[lookback]['energy']['p95']:>8.1f}")

            print(f"  Delta Magnitude:")
            print(f"    Mean:   {baselines[lookback]['delta_mag']['mean']:>8.0f}")
            print(f"    Median: {baselines[lookback]['delta_mag']['median']:>8.0f}")
            print(f"    P90:    {baselines[lookback]['delta_mag']['p90']:>8.0f}")
            print(f"    P95:    {baselines[lookback]['delta_mag']['p95']:>8.0f}")

            print(f"  Delta Normalized (ratio):")
            print(f"    Mean:   {baselines[lookback]['delta_norm']['mean']:>8.3f}")
            print(f"    Median: {baselines[lookback]['delta_norm']['median']:>8.3f}")
            print(f"    P90:    {baselines[lookback]['delta_norm']['p90']:>8.3f}")
            print(f"    P95:    {baselines[lookback]['delta_norm']['p95']:>8.3f}")

    return baselines


def detect_spikes_live(date_str: str, baselines: dict,
                       hour_start: int = 9, hour_end: int = 16,
                       velocity_multiplier: float = 2.0,
                       energy_multiplier: float = 2.0,
                       delta_norm_threshold: float = 0.03) -> list:
    """
    Detect velocity spikes in RTH using millisecond-aggregated data.

    Spike criteria:
    - Velocity > baseline_p95 * velocity_multiplier
    - Energy > baseline_p95 * energy_multiplier
    - Delta_normalized > delta_norm_threshold
    """
    print(f"\n{'='*80}")
    print(f"LIVE SPIKE DETECTION: {date_str} {hour_start}:00-{hour_end}:00")
    print(f"{'='*80}\n")

    print(f"Spike Criteria:")
    print(f"  Velocity > P95 × {velocity_multiplier}")
    print(f"  Energy > P95 × {energy_multiplier}")
    print(f"  Delta Normalized > {delta_norm_threshold}")
    print()

    buckets = load_and_aggregate_to_milliseconds(date_str, hour_start, hour_end)

    lookback_periods = [100, 300, 1000, 3000, 10000, 30000, 100000]  # milliseconds
    spikes = []

    spike_count = 0
    check_interval_ms = 100  # Check every 100ms

    last_check_ts = 0

    for i, bucket in enumerate(buckets):
        # Only check periodically
        if bucket.ts_ms - last_check_ts < check_interval_ms:
            continue

        last_check_ts = bucket.ts_ms

        if i % 1000 == 0:
            ts = datetime.fromtimestamp(bucket.ts_ms / 1000.0)
            print(f"  Processed through {ts.strftime('%H:%M:%S')}, {spike_count} spikes so far...", flush=True)

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

                # Convert timestamp to readable
                ts = datetime.fromtimestamp(bucket.ts_ms / 1000.0)

                direction = "BUY" if stats.buy_volume > stats.sell_volume else "SELL"

                # Lookback display
                if lookback < 1000:
                    lookback_str = f"{lookback}ms"
                else:
                    lookback_str = f"{lookback/1000:.0f}s"

                spike_info = {
                    'spike_id': spike_count,
                    'bucket_idx': i,
                    'timestamp': ts.strftime('%H:%M:%S.%f')[:-3],
                    'lookback': lookback,
                    'lookback_str': lookback_str,
                    'price': bucket.last_price,
                    'direction': direction,
                    'velocity': stats.velocity,
                    'velocity_threshold': vel_threshold,
                    'energy': stats.energy,
                    'energy_threshold': energy_threshold,
                    'delta_normalized': stats.delta_normalized,
                    'buy_volume': stats.buy_volume,
                    'sell_volume': stats.sell_volume,
                    'price_change': stats.price_change,
                    'duration_ms': stats.duration_ms,
                }

                spikes.append(spike_info)

                print(f"🚀 SPIKE #{spike_count} @ {spike_info['timestamp']}")
                print(f"   Lookback: {lookback_str} | Price: {bucket.last_price:.2f} | Direction: {direction}")
                print(f"   Velocity: {stats.velocity:.1f} ticks/sec (threshold: {vel_threshold:.1f})")
                print(f"   Energy: {stats.energy:.0f} (threshold: {energy_threshold:.0f})")
                print(f"   Delta: {stats.delta_normalized:.3f} ({stats.buy_volume} buy, {stats.sell_volume} sell)")
                print(f"   Price moved: {stats.price_change:.2f} points in {stats.duration_ms:.0f}ms")
                print()

                # Only report each spike once (at shortest lookback that triggers)
                break

    print(f"\n{'='*80}")
    print(f"TOTAL SPIKES DETECTED: {spike_count}")
    print(f"{'='*80}\n")

    return spikes


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 18 + "VELOCITY SPIKE DETECTOR V2" + " " * 34 + "║")
    print("╚" + "=" * 78 + "╝")

    print("\nMillisecond-aggregated tick-level analysis")
    print("Instantaneous measurements:")
    print("  • Velocity: |Δprice| / Δt (ticks/second)")
    print("  • Energy: (Δprice)² / Δt (emphasizes violent moves)")
    print("  • Delta: |V_buy - V_sell| (absolute imbalance)")
    print("\nTime-based lookback windows:")
    print("  • 100ms, 300ms, 1s, 3s, 10s, 30s, 100s")

    # Test on one day first
    date_str = "2025-10-10"  # Bear day

    # Step 1: Analyze pre-market baseline (8:00-9:00)
    baselines = analyze_baseline_period(date_str, hour_start=8, hour_end=9)

    # Step 2: Detect spikes during market hours (9:00-16:00)
    spikes = detect_spikes_live(
        date_str,
        baselines,
        hour_start=9,
        hour_end=16,
        velocity_multiplier=2.0,   # Velocity must be 2x P95
        energy_multiplier=2.0,     # Energy must be 2x P95
        delta_norm_threshold=0.03, # 3% imbalance
    )

    # Summary
    if spikes:
        print("\n📊 SPIKE SUMMARY:")
        print(f"   Total spikes: {len(spikes)}")
        print(f"\n   By lookback period:")

        lookback_counts = {}
        for s in spikes:
            lookback_counts[s['lookback_str']] = lookback_counts.get(s['lookback_str'], 0) + 1

        for lookback_str in sorted(lookback_counts.keys()):
            print(f"     {lookback_str:>6s}: {lookback_counts[lookback_str]} spikes")

        print(f"\n   By direction:")
        buy_spikes = sum(1 for s in spikes if s['direction'] == 'BUY')
        sell_spikes = sum(1 for s in spikes if s['direction'] == 'SELL')
        print(f"     BUY:  {buy_spikes}")
        print(f"     SELL: {sell_spikes}")

        print(f"\n   Average spike velocity: {statistics.mean(s['velocity'] for s in spikes):.1f} ticks/sec")
        print(f"   Max spike velocity: {max(s['velocity'] for s in spikes):.1f} ticks/sec")

    print("\n" + "=" * 80)
    print("✅ ANALYSIS COMPLETE")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
