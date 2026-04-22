#!/usr/bin/env python3
"""
Backtest using 1-minute aggregated bars (FAST!)

Instead of 12-22M MBO events, processes 420 bars per day.
Runtime: Seconds instead of minutes.

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
from datetime import datetime

@dataclass
class Bar:
    """1-minute bar with order flow data."""
    bar_time: str
    open: float
    high: float
    low: float
    close: float
    buy_volume: int
    sell_volume: int
    delta: int
    imbalance_ratio: float
    trades: int


@dataclass
class Trade:
    """A completed trade."""
    entry_time: str
    exit_time: str
    side: str  # LONG or SHORT
    entry_price: float
    exit_price: float
    pnl: float
    duration_bars: int


def load_bars_from_clickhouse(date_str: str) -> list[Bar]:
    """Load 1-minute bars for a day."""
    print(f"Loading 1-min bars for {date_str}...")

    query = f"""
    SELECT
        formatDateTime(bar_time, '%Y-%m-%d %H:%M:%S') as bar_time,
        open,
        high,
        low,
        close,
        buy_volume,
        sell_volume,
        delta,
        imbalance_ratio,
        trades
    FROM mnq_1min_bars_orderflow
    WHERE date = '{date_str}'
    ORDER BY bar_time
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

    bars = []
    for row in data["data"]:
        bar = Bar(
            bar_time=row["bar_time"],
            open=float(row["open"]),
            high=float(row["high"]),
            low=float(row["low"]),
            close=float(row["close"]),
            buy_volume=int(row["buy_volume"]),
            sell_volume=int(row["sell_volume"]),
            delta=int(row["delta"]),
            imbalance_ratio=float(row["imbalance_ratio"]),
            trades=int(row["trades"]),
        )
        bars.append(bar)

    print(f"  Loaded {len(bars)} bars")
    return bars


def detect_regime_from_bars(bars: list[Bar], current_idx: int, lookback: int = 10) -> str:
    """
    Detect regime from recent bars.

    Uses rolling delta sum and imbalance to classify:
    - BULL_TREND: Cumulative delta > +300, multiple buy-heavy bars
    - BEAR_TREND: Cumulative delta < -300, multiple sell-heavy bars
    - CHOPPY: Everything else
    """
    if current_idx < lookback:
        return "CHOPPY"

    # Look at last N bars
    recent_bars = bars[max(0, current_idx - lookback):current_idx + 1]

    # Cumulative delta over lookback period
    cum_delta = sum(b.delta for b in recent_bars)

    # Count imbalanced bars
    buy_heavy_bars = sum(1 for b in recent_bars if b.imbalance_ratio > 1.5)
    sell_heavy_bars = sum(1 for b in recent_bars if b.imbalance_ratio < 0.67)

    # Price movement
    price_change = recent_bars[-1].close - recent_bars[0].open

    # Classify
    if cum_delta > 300 and buy_heavy_bars >= 3 and price_change > 5:
        return "BULL_TREND"
    elif cum_delta < -300 and sell_heavy_bars >= 3 and price_change < -5:
        return "BEAR_TREND"
    else:
        return "CHOPPY"


def backtest_day(date_str: str, expected_regime: str) -> dict:
    """Backtest one day using 1-min bars."""
    print("\n" + "=" * 80)
    print(f"BACKTESTING: {date_str} (Expected: {expected_regime})")
    print("=" * 80)

    bars = load_bars_from_clickhouse(date_str)

    # Trading state
    position = 0  # 0 = flat, 5 = long, -5 = short
    entry_price = 0.0
    entry_bar_idx = 0
    entry_time = ""
    stop_price = 0.0
    trail_offset = 5.00  # $5 trailing stop

    trades = []
    regime_history = []

    print(f"\nProcessing {len(bars)} bars...")

    for i, bar in enumerate(bars):
        # Detect regime
        regime = detect_regime_from_bars(bars, i, lookback=10)

        if i == 0 or regime != regime_history[-1][1] if regime_history else "UNKNOWN":
            regime_history.append((i, regime, bar.bar_time))
            if i > 0:
                print(f"[Bar {i:>3}] {regime_history[-2][1]:12s} → {regime:12s} @ {bar.bar_time}")

        # Check trailing stop if in position
        if position != 0:
            if position > 0:  # Long position
                # Update trailing stop
                potential_stop = bar.close - trail_offset
                if potential_stop > stop_price:
                    stop_price = potential_stop

                # Check if stop hit
                if bar.low <= stop_price:
                    # Exit long
                    exit_price = stop_price
                    pnl = (exit_price - entry_price) * position

                    trade = Trade(
                        entry_time=entry_time,
                        exit_time=bar.bar_time,
                        side="LONG",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=i - entry_bar_idx,
                    )
                    trades.append(trade)

                    print(f"  EXIT LONG @ {exit_price:.2f} | PnL: ${pnl:>7.2f} | Duration: {trade.duration_bars} bars")

                    position = 0

            elif position < 0:  # Short position
                # Update trailing stop
                potential_stop = bar.close + trail_offset
                if potential_stop < stop_price or stop_price == 0:
                    stop_price = potential_stop

                # Check if stop hit
                if bar.high >= stop_price:
                    # Exit short
                    exit_price = stop_price
                    pnl = (entry_price - exit_price) * abs(position)

                    trade = Trade(
                        entry_time=entry_time,
                        exit_time=bar.bar_time,
                        side="SHORT",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=i - entry_bar_idx,
                    )
                    trades.append(trade)

                    print(f"  EXIT SHORT @ {exit_price:.2f} | PnL: ${pnl:>7.2f} | Duration: {trade.duration_bars} bars")

                    position = 0

        # Entry logic (only if flat)
        if position == 0:
            if regime == "BULL_TREND":
                # Enter long
                position = 5
                entry_price = bar.close
                entry_bar_idx = i
                entry_time = bar.bar_time
                stop_price = entry_price - 5.00  # Initial stop $5 below

                print(f"  ENTER LONG @ {entry_price:.2f} | Stop: {stop_price:.2f}")

            elif regime == "BEAR_TREND":
                # Enter short
                position = -5
                entry_price = bar.close
                entry_bar_idx = i
                entry_time = bar.bar_time
                stop_price = entry_price + 5.00  # Initial stop $5 above

                print(f"  ENTER SHORT @ {entry_price:.2f} | Stop: {stop_price:.2f}")

    # Close any remaining position
    if position != 0:
        exit_price = bars[-1].close
        if position > 0:
            pnl = (exit_price - entry_price) * position
            side = "LONG"
        else:
            pnl = (entry_price - exit_price) * abs(position)
            side = "SHORT"

        trade = Trade(
            entry_time=entry_time,
            exit_time=bars[-1].bar_time,
            side=side,
            entry_price=entry_price,
            exit_price=exit_price,
            pnl=pnl,
            duration_bars=len(bars) - entry_bar_idx,
        )
        trades.append(trade)

        print(f"\n  CLOSE {side} @ {exit_price:.2f} | PnL: ${pnl:.2f}")

    # Results
    print("\n" + "-" * 80)
    print(f"RESULTS: {date_str}")
    print("-" * 80)

    print(f"\n📊 Regime Detections: {len(regime_history)} changes")
    regime_counts = {}
    for _, regime, _ in regime_history:
        regime_counts[regime] = regime_counts.get(regime, 0) + 1
    for regime, count in sorted(regime_counts.items()):
        print(f"   {regime}: {count}")

    print(f"\n💰 Trades: {len(trades)}")
    total_pnl = sum(t.pnl for t in trades)
    winners = [t for t in trades if t.pnl > 0]
    losers = [t for t in trades if t.pnl <= 0]

    for i, t in enumerate(trades, 1):
        result = "WIN" if t.pnl > 0 else "LOSS"
        print(f"   {i}. {t.side:5s} {t.entry_price:.2f} → {t.exit_price:.2f} | ${t.pnl:>7.2f} | {t.duration_bars:>3} bars | {result}")

    print(f"\n   Total PnL: ${total_pnl:.2f}")
    print(f"   Winners: {len(winners)}, Losers: {len(losers)}")
    if trades:
        win_rate = len(winners) / len(trades) * 100
        print(f"   Win Rate: {win_rate:.1f}%")

    return {
        "date": date_str,
        "expected_regime": expected_regime,
        "trades": len(trades),
        "total_pnl": total_pnl,
        "winners": len(winners),
        "losers": len(losers),
        "win_rate": len(winners) / len(trades) * 100 if trades else 0,
    }


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 18 + "1-MINUTE BAR BACKTEST (FAST!)" + " " * 30 + "║")
    print("╚" + "=" * 78 + "╝")

    print("\nUsing 1-minute aggregated bars with order flow statistics")
    print("  • 420 bars per day (vs 12-22M events)")
    print("  • Delta and imbalance pre-calculated")
    print("  • Runtime: Seconds instead of minutes")

    days = [
        ("2025-10-01", "BULL"),
        ("2025-10-10", "BEAR"),
        ("2025-10-15", "SWING"),
    ]

    results = []

    for date_str, expected_regime in days:
        result = backtest_day(date_str, expected_regime)
        results.append(result)

    # Summary
    print("\n\n" + "=" * 80)
    print("SUMMARY: ALL DAYS")
    print("=" * 80)

    print(f"\n{'Date':<12} {'Expected':<10} {'Trades':<8} {'Win Rate':<10} {'P&L':<12}")
    print("-" * 80)
    for r in results:
        print(f"{r['date']:<12} {r['expected_regime']:<10} {r['trades']:<8} "
              f"{r['win_rate']:<10.1f}% ${r['total_pnl']:<11.2f}")

    total_pnl = sum(r['total_pnl'] for r in results)
    total_trades = sum(r['trades'] for r in results)

    print(f"\n💰 TOTAL: {total_trades} trades, ${total_pnl:.2f} P&L across 3 days")
    print(f"   Average per day: ${total_pnl / 3:.2f}")

    print("\n" + "=" * 80)
    print("✅ 1-MINUTE BAR BACKTEST COMPLETE!")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
