#!/usr/bin/env python3
"""
Analyze different bar aggregation types:
- 1-second bars
- 1000-tick bars
- 5000-tick bars

Report statistics on:
- Bars per day
- Duration (velocity)
- Delta distribution
- Imbalance patterns
- Volume characteristics

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
from typing import List
import statistics

@dataclass
class TickBar:
    """A tick-based bar."""
    bar_id: int
    date: str
    start_time: str
    end_time: str
    duration_seconds: float
    open: float
    high: float
    low: float
    close: float
    buy_volume: int
    sell_volume: int
    total_volume: int
    delta: int
    imbalance_ratio: float
    net_change: float
    trades: int


def load_trades_for_tick_bars(date_str: str) -> list[dict]:
    """Load all trades for a day to build tick bars."""
    print(f"Loading trades for {date_str}...")

    query = f"""
    SELECT
        toString(ts_event) as ts,
        price,
        size,
        side,
        action
    FROM mnq_mbo
    WHERE symbol = 'MNQZ5'
      AND toDate(ts_event) = '{date_str}'
      AND hour(ts_event) >= 9
      AND hour(ts_event) < 16
      AND action IN ('T', 'F')
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
    trades = data["data"]
    print(f"  Loaded {len(trades)} trades")
    return trades


def build_tick_bars(trades: list[dict], tick_size: int, date_str: str) -> list[TickBar]:
    """Build tick bars from trade data."""
    bars = []
    current_bar_trades = []
    bar_id = 0

    for trade in trades:
        current_bar_trades.append(trade)

        if len(current_bar_trades) >= tick_size:
            # Complete bar
            bar = create_bar_from_trades(current_bar_trades, bar_id, date_str)
            bars.append(bar)

            current_bar_trades = []
            bar_id += 1

    # Handle remaining trades (incomplete final bar)
    if current_bar_trades:
        bar = create_bar_from_trades(current_bar_trades, bar_id, date_str)
        bars.append(bar)

    return bars


def create_bar_from_trades(trades: list[dict], bar_id: int, date_str: str) -> TickBar:
    """Create a single tick bar from a list of trades."""
    # Parse timestamps
    start_time = trades[0]["ts"]
    end_time = trades[-1]["ts"]

    # Calculate duration
    from datetime import datetime
    # ClickHouse timestamp format: 2025-10-01 09:30:00.123456
    start_dt = datetime.strptime(start_time[:26], '%Y-%m-%d %H:%M:%S.%f')
    end_dt = datetime.strptime(end_time[:26], '%Y-%m-%d %H:%M:%S.%f')
    duration = (end_dt - start_dt).total_seconds()

    # OHLC
    prices = [float(t["price"]) for t in trades]
    open_price = prices[0]
    high_price = max(prices)
    low_price = min(prices)
    close_price = prices[-1]

    # Volume and delta
    buy_volume = sum(int(t["size"]) for t in trades if t["side"] == "A")
    sell_volume = sum(int(t["size"]) for t in trades if t["side"] == "B")
    total_volume = buy_volume + sell_volume
    delta = buy_volume - sell_volume

    # Imbalance
    imbalance_ratio = buy_volume / sell_volume if sell_volume > 0 else 0

    # Net change
    net_change = close_price - open_price

    return TickBar(
        bar_id=bar_id,
        date=date_str,
        start_time=start_time,
        end_time=end_time,
        duration_seconds=duration,
        open=open_price,
        high=high_price,
        low=low_price,
        close=close_price,
        buy_volume=buy_volume,
        sell_volume=sell_volume,
        total_volume=total_volume,
        delta=delta,
        imbalance_ratio=imbalance_ratio,
        net_change=net_change,
        trades=len(trades),
    )


def analyze_bars(bars: list[TickBar], bar_type: str) -> dict:
    """Analyze statistics for a set of bars."""
    if not bars:
        return {}

    durations = [b.duration_seconds for b in bars]
    deltas = [b.delta for b in bars]
    imbalances = [b.imbalance_ratio for b in bars if b.imbalance_ratio > 0]
    volumes = [b.total_volume for b in bars]
    price_moves = [abs(b.net_change) for b in bars]

    return {
        "bar_type": bar_type,
        "bars_per_day": len(bars),
        "duration": {
            "avg": round(statistics.mean(durations), 2) if durations else 0,
            "min": round(min(durations), 2) if durations else 0,
            "max": round(max(durations), 2) if durations else 0,
            "median": round(statistics.median(durations), 2) if durations else 0,
        },
        "delta": {
            "avg": round(statistics.mean(deltas), 2) if deltas else 0,
            "min": min(deltas) if deltas else 0,
            "max": max(deltas) if deltas else 0,
            "std": round(statistics.stdev(deltas), 2) if len(deltas) > 1 else 0,
            "abs_avg": round(statistics.mean([abs(d) for d in deltas]), 2) if deltas else 0,
        },
        "imbalance": {
            "avg": round(statistics.mean(imbalances), 3) if imbalances else 0,
            "min": round(min(imbalances), 3) if imbalances else 0,
            "max": round(max(imbalances), 3) if imbalances else 0,
            "median": round(statistics.median(imbalances), 3) if imbalances else 0,
        },
        "volume": {
            "avg": round(statistics.mean(volumes), 0) if volumes else 0,
            "min": min(volumes) if volumes else 0,
            "max": max(volumes) if volumes else 0,
        },
        "price_move": {
            "avg": round(statistics.mean(price_moves), 2) if price_moves else 0,
            "max": round(max(price_moves), 2) if price_moves else 0,
        },
    }


def get_1sec_bar_stats(date_str: str) -> dict:
    """Get statistics for 1-second bars from ClickHouse view."""
    print(f"\nAnalyzing 1-second bars for {date_str}...")

    query = f"""
    SELECT
        date,
        count(*) as bars_per_day,
        round(avg(delta), 2) as avg_delta,
        min(delta) as min_delta,
        max(delta) as max_delta,
        round(stddevPop(delta), 2) as std_delta,
        round(avg(abs(delta)), 2) as avg_abs_delta,
        round(avg(imbalance_ratio), 3) as avg_imbalance,
        round(quantile(0.5)(imbalance_ratio), 3) as median_imbalance,
        round(avg(total_volume), 0) as avg_volume,
        round(avg(abs(net_change)), 2) as avg_price_move,
        max(abs(net_change)) as max_price_move
    FROM mnq_1sec_bars_orderflow
    WHERE date = '{date_str}'
    GROUP BY date
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
    row = data["data"][0]

    return {
        "bar_type": "1-SECOND BARS",
        "bars_per_day": row["bars_per_day"],
        "duration": {
            "avg": 1.0,  # Always 1 second
            "min": 1.0,
            "max": 1.0,
            "median": 1.0,
        },
        "delta": {
            "avg": float(row["avg_delta"]),
            "min": row["min_delta"],
            "max": row["max_delta"],
            "std": float(row["std_delta"]),
            "abs_avg": float(row["avg_abs_delta"]),
        },
        "imbalance": {
            "avg": float(row["avg_imbalance"]),
            "median": float(row["median_imbalance"]),
        },
        "volume": {
            "avg": float(row["avg_volume"]),
        },
        "price_move": {
            "avg": float(row["avg_price_move"]),
            "max": float(row["max_price_move"]),
        },
    }


def print_stats(stats: dict):
    """Print statistics in a nice format."""
    print(f"\n{'='*80}")
    print(f"{stats['bar_type']}")
    print(f"{'='*80}")
    print(f"\n📊 Bars per day: {stats['bars_per_day']}")

    if "duration" in stats:
        d = stats["duration"]
        print(f"\n⏱️  Duration (seconds):")
        print(f"   Average: {d['avg']}s")
        print(f"   Median:  {d['median']}s")
        print(f"   Range:   {d['min']}s - {d['max']}s")

    if "delta" in stats:
        d = stats["delta"]
        print(f"\n📈 Delta (buy - sell volume):")
        print(f"   Average:     {d['avg']}")
        print(f"   Abs Average: {d['abs_avg']}")
        print(f"   Std Dev:     {d['std']}")
        print(f"   Range:       {d['min']} to {d['max']}")

    if "imbalance" in stats:
        i = stats["imbalance"]
        print(f"\n⚖️  Imbalance Ratio (buy/sell):")
        if "avg" in i:
            print(f"   Average: {i['avg']}")
        if "median" in i:
            print(f"   Median:  {i['median']}")
        if "min" in i and "max" in i:
            print(f"   Range:   {i['min']} - {i['max']}")

    if "volume" in stats:
        v = stats["volume"]
        print(f"\n📦 Volume:")
        print(f"   Average: {v['avg']}")
        if "min" in v and "max" in v:
            print(f"   Range:   {v['min']} - {v['max']}")

    if "price_move" in stats:
        p = stats["price_move"]
        print(f"\n💵 Price Movement (absolute):")
        print(f"   Average: ${p['avg']}")
        print(f"   Max:     ${p['max']}")


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 20 + "BAR TYPE ANALYSIS & STATISTICS" + " " * 28 + "║")
    print("╚" + "=" * 78 + "╝")

    print("\nAnalyzing: 1-second, 1000-tick, and 5000-tick bars")
    print("Days: 2025-10-01 (BULL), 2025-10-10 (BEAR), 2025-10-15 (SWING)")

    days = [
        ("2025-10-01", "BULL"),
        ("2025-10-10", "BEAR"),
        ("2025-10-15", "SWING"),
    ]

    for date_str, regime in days:
        print(f"\n\n{'#'*80}")
        print(f"# DATE: {date_str} ({regime})")
        print(f"{'#'*80}")

        # 1-second bars (from ClickHouse view)
        stats_1sec = get_1sec_bar_stats(date_str)
        print_stats(stats_1sec)

        # Load trades for tick bars
        trades = load_trades_for_tick_bars(date_str)

        # 1000-tick bars
        print(f"\nBuilding 1000-tick bars...")
        bars_1000 = build_tick_bars(trades, 1000, date_str)
        stats_1000 = analyze_bars(bars_1000, "1000-TICK BARS")
        print_stats(stats_1000)

        # 5000-tick bars
        print(f"\nBuilding 5000-tick bars...")
        bars_5000 = build_tick_bars(trades, 5000, date_str)
        stats_5000 = analyze_bars(bars_5000, "5000-TICK BARS")
        print_stats(stats_5000)

    print(f"\n\n{'='*80}")
    print("✅ ANALYSIS COMPLETE")
    print(f"{'='*80}\n")


if __name__ == "__main__":
    main()
