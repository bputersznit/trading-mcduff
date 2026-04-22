#!/usr/bin/env python3
"""
Find Bull, Bear, and Swing days from historical data.

Analyzes MNQ data to identify:
1. Bull day (strong uptrend)
2. Bear day (strong downtrend)
3. Swing/Choppy day (range-bound)

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

import subprocess
import json
from dataclasses import dataclass
from datetime import datetime


@dataclass
class DayAnalysis:
    """Analysis of a trading day."""
    date: str
    open_price: float
    close_price: float
    high: float
    low: float
    range_points: float
    net_change: float
    net_change_pct: float
    events: int
    regime: str
    score: float


def query_clickhouse(query: str) -> str:
    """Execute ClickHouse query and return result."""
    result = subprocess.run(
        ["clickhouse-client", "--query", query, "--format", "JSON"],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise Exception(f"Query failed: {result.stderr}")
    return result.stdout


def analyze_day(date_str: str) -> DayAnalysis | None:
    """Analyze a single trading day."""
    print(f"Analyzing {date_str}...", end=" ")

    # Get RTH (regular trading hours) data: 9:30 AM - 4:00 PM ET
    # MNQ trades on CME, so use proper trading hours
    # Filter to single contract (MNQZ5 = December 2025)
    # Use only actual trades (T, F) for price analysis
    query = f"""
    SELECT
        toDate(ts_event) as date,
        minIf(price, action IN ('T', 'F')) as low,
        maxIf(price, action IN ('T', 'F')) as high,
        argMinIf(price, ts_event, action IN ('T', 'F')) as first_price,
        argMaxIf(price, ts_event, action IN ('T', 'F')) as last_price,
        count(*) as events
    FROM mnq_mbo
    WHERE toDate(ts_event) = '{date_str}'
      AND symbol = 'MNQZ5'
      AND hour(ts_event) >= 9
      AND hour(ts_event) < 16
    GROUP BY date
    """

    try:
        result = query_clickhouse(query)
        data = json.loads(result)

        if not data.get("data"):
            print("No data")
            return None

        row = data["data"][0]

        open_price = float(row["first_price"])
        close_price = float(row["last_price"])
        high = float(row["high"])
        low = float(row["low"])
        events = int(row["events"])

        range_points = high - low
        net_change = close_price - open_price
        net_change_pct = (net_change / open_price) * 100

        # Classify regime
        if net_change > 50 and net_change / range_points > 0.6:
            regime = "BULL"
            score = net_change / range_points * 100
        elif net_change < -50 and abs(net_change) / range_points > 0.6:
            regime = "BEAR"
            score = abs(net_change) / range_points * 100
        elif range_points > 100 and abs(net_change) < range_points * 0.3:
            regime = "SWING"
            score = range_points / abs(net_change + 1)
        else:
            regime = "MIXED"
            score = 50

        analysis = DayAnalysis(
            date=date_str,
            open_price=open_price,
            close_price=close_price,
            high=high,
            low=low,
            range_points=range_points,
            net_change=net_change,
            net_change_pct=net_change_pct,
            events=events,
            regime=regime,
            score=score,
        )

        print(f"{regime} (O:{open_price:.1f} C:{close_price:.1f} Δ:{net_change:+.1f})")
        return analysis

    except Exception as e:
        print(f"Error: {e}")
        return None


def main():
    print("\n" + "=" * 80)
    print("FINDING BULL, BEAR, AND SWING DAYS")
    print("=" * 80)

    # Get recent high-volume trading days
    print("\nQuerying available dates...")
    dates_query = """
    SELECT
        toDate(ts_event) as date,
        count(*) as events
    FROM mnq_mbo
    WHERE toDate(ts_event) >= '2025-10-01'
      AND toDate(ts_event) <= '2025-10-31'
      AND symbol = 'MNQZ5'
      AND hour(ts_event) >= 9
      AND hour(ts_event) < 16
    GROUP BY date
    HAVING events > 5000000
    ORDER BY date DESC
    """

    result = query_clickhouse(dates_query)
    data = json.loads(result)
    available_dates = [row["date"] for row in data["data"]]

    print(f"Found {len(available_dates)} trading days with sufficient data")

    # Analyze each day
    print("\n" + "-" * 80)
    print("Analyzing days...")
    print("-" * 80)

    analyses = []
    for date_str in available_dates:
        analysis = analyze_day(date_str)
        if analysis:
            analyses.append(analysis)

    # Find best examples of each regime
    print("\n" + "=" * 80)
    print("REGIME CLASSIFICATION")
    print("=" * 80)

    bull_days = [a for a in analyses if a.regime == "BULL"]
    bear_days = [a for a in analyses if a.regime == "BEAR"]
    swing_days = [a for a in analyses if a.regime == "SWING"]
    mixed_days = [a for a in analyses if a.regime == "MIXED"]

    print(f"\n📊 Results:")
    print(f"   Bull days:  {len(bull_days)}")
    print(f"   Bear days:  {len(bear_days)}")
    print(f"   Swing days: {len(swing_days)}")
    print(f"   Mixed days: {len(mixed_days)}")

    # Select best examples
    best_bull = max(bull_days, key=lambda x: x.score) if bull_days else None
    best_bear = max(bear_days, key=lambda x: x.score) if bear_days else None
    best_swing = max(swing_days, key=lambda x: x.score) if swing_days else None

    print("\n" + "=" * 80)
    print("SELECTED DAYS FOR BACKTESTING")
    print("=" * 80)

    if best_bull:
        print(f"\n🟢 BULL DAY: {best_bull.date}")
        print(f"   Open:        {best_bull.open_price:.2f}")
        print(f"   Close:       {best_bull.close_price:.2f}")
        print(f"   High:        {best_bull.high:.2f}")
        print(f"   Low:         {best_bull.low:.2f}")
        print(f"   Net Change:  +{best_bull.net_change:.2f} points (+{best_bull.net_change_pct:.2f}%)")
        print(f"   Range:       {best_bull.range_points:.2f} points")
        print(f"   Events:      {best_bull.events:,}")
        print(f"   Score:       {best_bull.score:.1f}")

    if best_bear:
        print(f"\n🔴 BEAR DAY: {best_bear.date}")
        print(f"   Open:        {best_bear.open_price:.2f}")
        print(f"   Close:       {best_bear.close_price:.2f}")
        print(f"   High:        {best_bear.high:.2f}")
        print(f"   Low:         {best_bear.low:.2f}")
        print(f"   Net Change:  {best_bear.net_change:.2f} points ({best_bear.net_change_pct:.2f}%)")
        print(f"   Range:       {best_bear.range_points:.2f} points")
        print(f"   Events:      {best_bear.events:,}")
        print(f"   Score:       {best_bear.score:.1f}")

    if best_swing:
        print(f"\n🔵 SWING DAY: {best_swing.date}")
        print(f"   Open:        {best_swing.open_price:.2f}")
        print(f"   Close:       {best_swing.close_price:.2f}")
        print(f"   High:        {best_swing.high:.2f}")
        print(f"   Low:         {best_swing.low:.2f}")
        print(f"   Net Change:  {best_swing.net_change:+.2f} points ({best_swing.net_change_pct:+.2f}%)")
        print(f"   Range:       {best_swing.range_points:.2f} points")
        print(f"   Events:      {best_swing.events:,}")
        print(f"   Score:       {best_swing.score:.1f}")

    # Save results to file
    output_file = PROJECT_ROOT / "backtest_days.txt"
    with open(output_file, "w") as f:
        if best_bull:
            f.write(f"BULL_DAY={best_bull.date}\n")
        if best_bear:
            f.write(f"BEAR_DAY={best_bear.date}\n")
        if best_swing:
            f.write(f"SWING_DAY={best_swing.date}\n")

    print(f"\n✅ Days saved to: {output_file}")

    # Show all days for reference
    print("\n" + "=" * 80)
    print("ALL DAYS ANALYZED")
    print("=" * 80)

    print(f"\n{'Date':<12} {'Regime':<8} {'Open':<10} {'Close':<10} {'Change':<10} {'Range':<10}")
    print("-" * 80)
    for a in sorted(analyses, key=lambda x: x.date, reverse=True):
        print(f"{a.date:<12} {a.regime:<8} {a.open_price:<10.2f} {a.close_price:<10.2f} "
              f"{a.net_change:>+9.2f} {a.range_points:<10.2f}")

    print("\n" + "=" * 80)
    print("NEXT STEPS")
    print("=" * 80)
    print("\nRun the backtest on these days:")
    if best_bull:
        print(f"  python scripts/CGCl_backtest_regime_day.py --date {best_bull.date} --regime bull")
    if best_bear:
        print(f"  python scripts/CGCl_backtest_regime_day.py --date {best_bear.date} --regime bear")
    if best_swing:
        print(f"  python scripts/CGCl_backtest_regime_day.py --date {best_swing.date} --regime swing")
    print()


if __name__ == "__main__":
    import sys
    from pathlib import Path
    PROJECT_ROOT = Path(__file__).resolve().parents[1]
    main()
