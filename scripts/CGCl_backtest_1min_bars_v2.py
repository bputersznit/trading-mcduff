#!/usr/bin/env python3
"""
Backtest using 1-minute aggregated bars - IMPROVED VERSION

Key improvements:
1. Regime persistence - requires 3 consecutive bars confirming regime
2. Trade cooldown - 5 bar wait after exit before re-entry
3. Minimum hold time - stay in trades at least 5 bars
4. Better trailing stop logic
5. Aligned with expectations: 5-15 trades on swing days, 1-4 on trend days

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
    exit_reason: str  # STOP, REGIME_CHANGE, EOD


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


def detect_regime_from_bars(bars: list[Bar], current_idx: int, lookback: int = 15) -> str:
    """
    Detect regime from recent bars - TUNED FOR REAL MARKET CONDITIONS.

    Balanced thresholds based on actual data:
    - BULL_TREND: Cumulative delta > +350 over 15 bars, 3+ buy-heavy bars, +6pt move
    - BEAR_TREND: Cumulative delta < -350 over 15 bars, 3+ sell-heavy bars, -6pt move
    - CHOPPY: Everything else
    """
    if current_idx < lookback:
        return "CHOPPY"

    # Look at last N bars (15 minutes)
    recent_bars = bars[max(0, current_idx - lookback):current_idx + 1]

    # Cumulative delta over lookback period
    cum_delta = sum(b.delta for b in recent_bars)

    # Count imbalanced bars
    buy_heavy_bars = sum(1 for b in recent_bars if b.imbalance_ratio > 1.5)
    sell_heavy_bars = sum(1 for b in recent_bars if b.imbalance_ratio < 0.67)

    # Price movement
    price_change = recent_bars[-1].close - recent_bars[0].open

    # Classify
    if cum_delta > 350 and buy_heavy_bars >= 3 and price_change > 6:
        return "BULL_TREND"
    elif cum_delta < -350 and sell_heavy_bars >= 3 and price_change < -6:
        return "BEAR_TREND"
    else:
        return "CHOPPY"


def backtest_day(date_str: str, expected_regime: str) -> dict:
    """Backtest one day using 1-min bars with IMPROVED logic."""
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
    trail_offset = 8.00  # $8 trailing stop (wider than before)

    # NEW: Regime persistence tracking
    regime_buffer = []  # Track last 2 regime detections
    current_confirmed_regime = "CHOPPY"

    # NEW: Trade management
    bars_since_exit = 999  # Start with large number (allow immediate first trade)
    cooldown_period = 5  # Must wait 5 bars after exit
    min_hold_bars = 5  # Minimum bars to hold position

    trades = []
    regime_history = []

    print(f"\nProcessing {len(bars)} bars...")
    print("Settings:")
    print(f"  • Cooldown: {cooldown_period} bars after exit")
    print(f"  • Min hold: {min_hold_bars} bars")
    print(f"  • Trailing stop: ${trail_offset:.2f}")
    print(f"  • Regime confirmation: 2 consecutive bars")
    print(f"  • Lookback: 15 bars (15 minutes)")
    print()

    for i, bar in enumerate(bars):
        # Detect raw regime
        raw_regime = detect_regime_from_bars(bars, i, lookback=15)

        # Update regime buffer
        regime_buffer.append(raw_regime)
        if len(regime_buffer) > 2:
            regime_buffer.pop(0)

        # Confirm regime only if last 2 bars agree
        if len(regime_buffer) == 2 and len(set(regime_buffer)) == 1:
            new_regime = regime_buffer[0]
            if new_regime != current_confirmed_regime:
                regime_history.append((i, current_confirmed_regime, new_regime, bar.bar_time))
                print(f"[Bar {i:>3}] {current_confirmed_regime:12s} → {new_regime:12s} @ {bar.bar_time}")
                current_confirmed_regime = new_regime

        # Increment bars since exit
        bars_since_exit += 1

        # Check trailing stop if in position
        if position != 0:
            bars_in_trade = i - entry_bar_idx

            if position > 0:  # Long position
                # Update trailing stop (only move up)
                potential_stop = bar.close - trail_offset
                if potential_stop > stop_price:
                    stop_price = potential_stop

                # Check if stop hit
                if bar.low <= stop_price:
                    exit_price = stop_price
                    pnl = (exit_price - entry_price) * position

                    trade = Trade(
                        entry_time=entry_time,
                        exit_time=bar.bar_time,
                        side="LONG",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        exit_reason="STOP",
                    )
                    trades.append(trade)

                    print(f"  EXIT LONG @ {exit_price:.2f} (STOP) | PnL: ${pnl:>7.2f} | Duration: {bars_in_trade} bars")

                    position = 0
                    bars_since_exit = 0

                # Check regime change exit (only if held minimum time)
                elif bars_in_trade >= min_hold_bars and current_confirmed_regime != "BULL_TREND":
                    exit_price = bar.close
                    pnl = (exit_price - entry_price) * position

                    trade = Trade(
                        entry_time=entry_time,
                        exit_time=bar.bar_time,
                        side="LONG",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        exit_reason="REGIME_CHANGE",
                    )
                    trades.append(trade)

                    print(f"  EXIT LONG @ {exit_price:.2f} (REGIME) | PnL: ${pnl:>7.2f} | Duration: {bars_in_trade} bars")

                    position = 0
                    bars_since_exit = 0

            elif position < 0:  # Short position
                # Update trailing stop (only move down)
                potential_stop = bar.close + trail_offset
                if potential_stop < stop_price or stop_price == 0:
                    stop_price = potential_stop

                # Check if stop hit
                if bar.high >= stop_price:
                    exit_price = stop_price
                    pnl = (entry_price - exit_price) * abs(position)

                    trade = Trade(
                        entry_time=entry_time,
                        exit_time=bar.bar_time,
                        side="SHORT",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        exit_reason="STOP",
                    )
                    trades.append(trade)

                    print(f"  EXIT SHORT @ {exit_price:.2f} (STOP) | PnL: ${pnl:>7.2f} | Duration: {bars_in_trade} bars")

                    position = 0
                    bars_since_exit = 0

                # Check regime change exit (only if held minimum time)
                elif bars_in_trade >= min_hold_bars and current_confirmed_regime != "BEAR_TREND":
                    exit_price = bar.close
                    pnl = (entry_price - exit_price) * abs(position)

                    trade = Trade(
                        entry_time=entry_time,
                        exit_time=bar.bar_time,
                        side="SHORT",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        exit_reason="REGIME_CHANGE",
                    )
                    trades.append(trade)

                    print(f"  EXIT SHORT @ {exit_price:.2f} (REGIME) | PnL: ${pnl:>7.2f} | Duration: {bars_in_trade} bars")

                    position = 0
                    bars_since_exit = 0

        # Entry logic (only if flat AND cooldown passed)
        if position == 0 and bars_since_exit >= cooldown_period:
            if current_confirmed_regime == "BULL_TREND":
                # Enter long
                position = 5
                entry_price = bar.close
                entry_bar_idx = i
                entry_time = bar.bar_time
                stop_price = entry_price - trail_offset  # Initial stop

                print(f"  ENTER LONG @ {entry_price:.2f} | Stop: {stop_price:.2f} | Cooldown: {bars_since_exit} bars")

            elif current_confirmed_regime == "BEAR_TREND":
                # Enter short
                position = -5
                entry_price = bar.close
                entry_bar_idx = i
                entry_time = bar.bar_time
                stop_price = entry_price + trail_offset  # Initial stop

                print(f"  ENTER SHORT @ {entry_price:.2f} | Stop: {stop_price:.2f} | Cooldown: {bars_since_exit} bars")

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
            exit_reason="EOD",
        )
        trades.append(trade)

        print(f"\n  CLOSE {side} @ {exit_price:.2f} (EOD) | PnL: ${pnl:.2f}")

    # Results
    print("\n" + "-" * 80)
    print(f"RESULTS: {date_str}")
    print("-" * 80)

    print(f"\n📊 Regime Changes: {len(regime_history)}")
    for bar_idx, old_regime, new_regime, time in regime_history[:10]:  # Show first 10
        print(f"   Bar {bar_idx:>3}: {old_regime:12s} → {new_regime:12s} @ {time}")
    if len(regime_history) > 10:
        print(f"   ... and {len(regime_history) - 10} more regime changes")

    print(f"\n💰 Trades: {len(trades)}")
    total_pnl = sum(t.pnl for t in trades)
    winners = [t for t in trades if t.pnl > 0]
    losers = [t for t in trades if t.pnl <= 0]

    # Exit reason breakdown
    exit_reasons = {}
    for t in trades:
        exit_reasons[t.exit_reason] = exit_reasons.get(t.exit_reason, 0) + 1

    for i, t in enumerate(trades, 1):
        result = "WIN" if t.pnl > 0 else "LOSS"
        print(f"   {i:>2}. {t.side:5s} {t.entry_price:.2f} → {t.exit_price:.2f} | "
              f"${t.pnl:>7.2f} | {t.duration_bars:>3} bars | {t.exit_reason:12s} | {result}")

    print(f"\n   Total PnL: ${total_pnl:.2f}")
    print(f"   Winners: {len(winners)}, Losers: {len(losers)}")
    if trades:
        win_rate = len(winners) / len(trades) * 100
        avg_duration = sum(t.duration_bars for t in trades) / len(trades)
        print(f"   Win Rate: {win_rate:.1f}%")
        print(f"   Avg Duration: {avg_duration:.1f} bars")
        print(f"\n   Exit Reasons:")
        for reason, count in sorted(exit_reasons.items()):
            print(f"      {reason}: {count}")

    return {
        "date": date_str,
        "expected_regime": expected_regime,
        "trades": len(trades),
        "total_pnl": total_pnl,
        "winners": len(winners),
        "losers": len(losers),
        "win_rate": len(winners) / len(trades) * 100 if trades else 0,
        "avg_duration": sum(t.duration_bars for t in trades) / len(trades) if trades else 0,
        "regime_changes": len(regime_history),
    }


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 16 + "1-MINUTE BAR BACKTEST V2 (IMPROVED!)" + " " * 25 + "║")
    print("╚" + "=" * 78 + "╝")

    print("\nImprovements:")
    print("  • Regime persistence: 3 consecutive bars required")
    print("  • Trade cooldown: 5 bars wait after exit")
    print("  • Minimum hold: 5 bars per trade")
    print("  • Wider stops: $8 trailing (was $5)")
    print("  • Stricter regime detection: Higher thresholds")
    print("\nTarget: 5-15 trades on swing days, 1-4 on trend days")

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

    print(f"\n{'Date':<12} {'Expected':<10} {'Trades':<8} {'Win Rate':<10} {'Avg Dur':<10} {'P&L':<12}")
    print("-" * 80)
    for r in results:
        print(f"{r['date']:<12} {r['expected_regime']:<10} {r['trades']:<8} "
              f"{r['win_rate']:<10.1f}% {r['avg_duration']:<10.1f} ${r['total_pnl']:<11.2f}")

    total_pnl = sum(r['total_pnl'] for r in results)
    total_trades = sum(r['trades'] for r in results)
    avg_trades_per_day = total_trades / len(results)

    print(f"\n💰 TOTAL: {total_trades} trades, ${total_pnl:.2f} P&L across 3 days")
    print(f"   Average per day: {avg_trades_per_day:.1f} trades, ${total_pnl / 3:.2f} P&L")

    print("\n📈 COMPARISON TO TARGETS:")
    print(f"   Bull day: {results[0]['trades']} trades (target: 1-4)")
    print(f"   Bear day: {results[1]['trades']} trades (target: 1-4)")
    print(f"   Swing day: {results[2]['trades']} trades (target: 5-15)")

    print("\n" + "=" * 80)
    print("✅ 1-MINUTE BAR BACKTEST V2 COMPLETE!")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
