#!/usr/bin/env python3
"""
Velocity Spike Detector - RAW TICK LEVEL

No bars, no windows - pure instantaneous measurements:

1. VELOCITY: v = |Δprice| / Δt (ticks per second)
2. ENERGY: E = (Δprice)² / Δt (emphasizes violent moves)
3. DELTA MAGNITUDE: |V_buy - V_sell| (absolute imbalance)
4. NORMALIZED DELTA: |V_buy - V_sell| / (V_buy + V_sell)

Measured over rolling lookback periods:
- 100, 300, 1000, 3000, 10000, 30000, 100000 ticks

First: Analyze pre-open period to establish baselines
Then: Detect spikes in real-time event stream

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
class MBOEvent:
    """Single MBO event."""
    ts_ns: int  # Nanosecond timestamp
    price: float
    size: int
    side: str  # 'A' = ask/buy aggressor, 'B' = bid/sell aggressor
    action: str  # 'T' = trade, 'A' = add, 'C' = cancel, etc.


@dataclass
class VelocityStats:
    """Rolling velocity statistics."""
    lookback: int  # Number of ticks
    price_change: float  # Absolute price movement
    duration_ms: float  # Elapsed milliseconds
    velocity: float  # Ticks per second
    energy: float  # (Δprice)² / Δt
    buy_volume: int
    sell_volume: int
    delta_magnitude: int  # |buy - sell|
    delta_normalized: float  # delta / total volume


def load_mbo_events(date_str: str, hour_start: int = 9, hour_end: int = 16) -> list[MBOEvent]:
    """Load raw MBO events for a day."""
    print(f"Loading MBO events for {date_str} ({hour_start}:00-{hour_end}:00)...", flush=True)

    query = f"""
    SELECT
        toInt64(ts_event) as ts_ns,
        price,
        size,
        side,
        action
    FROM mnq_mbo
    WHERE symbol = 'MNQZ5'
      AND toDate(ts_event) = '{date_str}'
      AND hour(ts_event) >= {hour_start}
      AND hour(ts_event) < {hour_end}
      AND action IN ('T', 'F')  -- Trades only for now
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

    events = []
    for row in data["data"]:
        event = MBOEvent(
            ts_ns=int(row["ts_ns"]),
            price=float(row["price"]),
            size=int(row["size"]),
            side=row["side"],
            action=row["action"],
        )
        events.append(event)

    print(f"  Loaded {len(events):,} trade events")
    return events


def calculate_velocity_stats(events: deque, lookback: int) -> VelocityStats:
    """Calculate velocity statistics over last N events."""
    if len(events) < lookback:
        return None

    # Get lookback window
    window = list(events)[-lookback:]

    # Price movement
    first_price = window[0].price
    last_price = window[-1].price
    price_change = abs(last_price - first_price)

    # Time elapsed in milliseconds
    first_ts = window[0].ts_ns
    last_ts = window[-1].ts_ns
    duration_ns = last_ts - first_ts
    duration_ms = duration_ns / 1_000_000  # Convert to milliseconds
    duration_sec = duration_ns / 1_000_000_000  # Convert to seconds

    # Velocity (ticks/second)
    # MNQ tick size = 0.25, so 1 point = 4 ticks
    price_change_ticks = price_change * 4  # Convert points to ticks
    velocity = price_change_ticks / duration_sec if duration_sec > 0 else 0

    # Energy (emphasizes violent moves)
    energy = (price_change_ticks ** 2) / duration_sec if duration_sec > 0 else 0

    # Delta magnitude
    buy_volume = sum(e.size for e in window if e.side == 'A')
    sell_volume = sum(e.size for e in window if e.side == 'B')
    delta_magnitude = abs(buy_volume - sell_volume)
    total_volume = buy_volume + sell_volume
    delta_normalized = delta_magnitude / total_volume if total_volume > 0 else 0

    return VelocityStats(
        lookback=lookback,
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

    events = load_mbo_events(date_str, hour_start, hour_end)

    if len(events) < 100000:
        print(f"  Warning: Only {len(events):,} events in pre-market (might not be enough)")

    lookback_periods = [100, 300, 1000, 3000, 10000, 30000, 100000]
    baselines = {}

    # Use deque for efficient rolling window
    event_buffer = deque(maxlen=100000)

    # Collect statistics for each lookback period
    for lookback in lookback_periods:
        velocities = []
        energies = []
        delta_mags = []
        delta_norms = []

        # Process events
        for event in events:
            event_buffer.append(event)

            if len(event_buffer) >= lookback:
                stats = calculate_velocity_stats(event_buffer, lookback)
                if stats and stats.velocity > 0:  # Only count when there's movement
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

            print(f"\n{lookback:,} Tick Window:")
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
                       delta_norm_threshold: float = 0.6,
                       sample_interval: int = 100) -> list:
    """
    Detect velocity spikes in real-time event stream.

    Spike criteria:
    - Velocity > baseline_p95 * velocity_multiplier
    - Energy > baseline_p95 * energy_multiplier
    - Delta_normalized > delta_norm_threshold

    sample_interval: Check for spikes every N events (default 100) for efficiency
    """
    print(f"\n{'='*80}")
    print(f"LIVE SPIKE DETECTION: {date_str} {hour_start}:00-{hour_end}:00")
    print(f"{'='*80}\n")

    print(f"Spike Criteria:")
    print(f"  Velocity > P95 × {velocity_multiplier}")
    print(f"  Energy > P95 × {energy_multiplier}")
    print(f"  Delta Normalized > {delta_norm_threshold}")
    print(f"  Sampling every {sample_interval} events (for efficiency)")
    print()

    events = load_mbo_events(date_str, hour_start, hour_end)

    lookback_periods = [100, 300, 1000, 3000, 10000, 30000, 100000]  # All 7 lookback periods
    spikes = []

    event_buffer = deque(maxlen=100000)

    spike_count = 0
    checks_performed = 0

    for i, event in enumerate(events):
        event_buffer.append(event)

        # Only check every sample_interval events for efficiency
        if i % sample_interval != 0:
            continue

        checks_performed += 1
        if checks_performed % 1000 == 0:
            print(f"  Processed {i:,} events, performed {checks_performed:,} spike checks...")

        # Check each lookback period
        for lookback in lookback_periods:
            if len(event_buffer) < lookback:
                continue

            stats = calculate_velocity_stats(event_buffer, lookback)
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
                ts = datetime.fromtimestamp(event.ts_ns / 1_000_000_000)

                direction = "BUY" if stats.buy_volume > stats.sell_volume else "SELL"

                spike_info = {
                    'spike_id': spike_count,
                    'event_idx': i,
                    'timestamp': ts.strftime('%H:%M:%S.%f')[:-3],
                    'lookback': lookback,
                    'price': event.price,
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
                print(f"   Lookback: {lookback:,} ticks | Price: {event.price:.2f} | Direction: {direction}")
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
    print("║" + " " * 20 + "VELOCITY SPIKE DETECTOR" + " " * 36 + "║")
    print("╚" + "=" * 78 + "╝")

    print("\nRaw tick-level analysis - NO BARS")
    print("Instantaneous measurements:")
    print("  • Velocity: |Δprice| / Δt (ticks/second)")
    print("  • Energy: (Δprice)² / Δt (emphasizes violent moves)")
    print("  • Delta: |V_buy - V_sell| (absolute imbalance)")

    # Test on one day first
    date_str = "2025-10-10"  # Bear day - should have spikes

    # Step 1: Analyze pre-market baseline (8:00-9:00)
    baselines = analyze_baseline_period(date_str, hour_start=8, hour_end=9)

    # Step 2: Detect spikes during market hours (9:30-16:00)
    # Note: Baselines show delta_normalized P95 ranges from 0.009 (1000-tick) to 0.050 (100-tick)
    # Using 0.6 threshold is unrealistic - adjust to 3x P95 instead
    spikes = detect_spikes_live(
        date_str,
        baselines,
        hour_start=9,
        hour_end=16,
        velocity_multiplier=2.0,   # Velocity must be 2x P95
        energy_multiplier=2.0,     # Energy must be 2x P95
        delta_norm_threshold=0.03, # 3% imbalance (realistic for these lookbacks)
        sample_interval=100,       # Check every 100 events for efficiency
    )

    # Summary
    if spikes:
        print("\n📊 SPIKE SUMMARY:")
        print(f"   Total spikes: {len(spikes)}")
        print(f"\n   By lookback period:")
        for lookback in [100, 300, 1000, 3000, 10000, 30000, 100000]:
            count = sum(1 for s in spikes if s['lookback'] == lookback)
            if count > 0:
                print(f"     {lookback:,} ticks: {count} spikes")

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
