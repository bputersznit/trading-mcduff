#!/usr/bin/env python3
"""
Backtest 1000-Tick Bar Momentum Strategy

Strategy:
- Use velocity (bar completion speed) to detect momentum bursts
- Enter on FAST bars (<5 seconds) with strong delta and imbalance
- Exit on momentum fade (slow bars >30s) or trailing stop
- Filter out chop zones and slow periods

Target: 5-15 trades/day on swing, 1-4 on trend days

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
    """A 1000-tick bar with velocity and order flow."""
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


@dataclass
class Trade:
    """A completed trade."""
    entry_bar: int
    exit_bar: int
    entry_time: str
    exit_time: str
    side: str  # LONG or SHORT
    entry_price: float
    exit_price: float
    pnl: float
    duration_bars: int
    duration_seconds: float
    exit_reason: str  # STOP, VELOCITY_FADE, IMBALANCE_REVERSE, TIME_STOP, EOD
    entry_velocity: float  # Seconds to complete entry bar
    exit_velocity: float   # Seconds to complete exit bar


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
    print(f"  Loaded {len(trades):,} trades")
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
    from datetime import datetime

    # Parse timestamps
    start_time = trades[0]["ts"]
    end_time = trades[-1]["ts"]

    # Calculate duration
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
        start_time=start_time[:19],
        end_time=end_time[:19],
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


def backtest_day(date_str: str, expected_regime: str) -> dict:
    """Backtest 1000-tick momentum strategy."""
    print("\n" + "=" * 80)
    print(f"BACKTESTING: {date_str} (Expected: {expected_regime})")
    print("=" * 80)

    # Load and build tick bars
    trades = load_trades_for_tick_bars(date_str)
    bars = build_tick_bars(trades, 1000, date_str)
    print(f"  Built {len(bars)} 1000-tick bars")

    # Trading parameters
    position = 0  # 0 = flat, 5 = long, -5 = short
    entry_price = 0.0
    entry_bar = 0
    entry_time = ""
    stop_price = 0.0
    initial_stop_distance = 10.00  # $10 initial stop
    trail_offset = 8.00  # $8 trailing stop

    # Velocity and momentum tracking
    entry_velocity = 0.0
    bars_since_exit = 999  # Allow immediate first trade
    cooldown_bars = 2  # Wait 2 bars after exit
    max_hold_bars = 10  # 5-minute time stop (10 bars × ~30s avg)

    trades = []

    print(f"\nProcessing {len(bars)} bars...")
    print("Strategy Parameters:")
    print(f"  • Entry: Bar velocity <5s, delta >10, imbalance >1.10/<0.90")
    print(f"  • Exit: Velocity >30s, imbalance reverse, or $10/$8 trailing stop")
    print(f"  • Filters: Skip bars >60s, skip chop zones (imbalance 0.95-1.05)")
    print(f"  • Cooldown: {cooldown_bars} bars, Max hold: {max_hold_bars} bars\n")

    for i, bar in enumerate(bars):
        bars_since_exit += 1

        # Skip if we don't have enough history
        if i < 2:
            continue

        # Check chop filter: If last 5 bars all have imbalance 0.95-1.05, skip
        if i >= 5:
            recent_imbalances = [bars[j].imbalance_ratio for j in range(i-4, i+1)]
            if all(0.95 <= imb <= 1.05 for imb in recent_imbalances):
                if i % 50 == 0:  # Print occasionally
                    print(f"[Bar {i:>4}] CHOP ZONE - skipping (imbalance 0.95-1.05 for 5 bars)")
                continue

        # Manage open position
        if position != 0:
            bars_in_trade = i - entry_bar
            from datetime import datetime
            duration_secs = (datetime.strptime(bar.end_time, '%Y-%m-%d %H:%M:%S') -
                           datetime.strptime(entry_time, '%Y-%m-%d %H:%M:%S')).total_seconds()

            if position > 0:  # Long position
                # Update trailing stop (only move up)
                potential_stop = bar.close - trail_offset
                if potential_stop > stop_price:
                    stop_price = potential_stop

                # Check stop hit
                if bar.low <= stop_price:
                    exit_price = stop_price
                    pnl = (exit_price - entry_price) * position

                    trade = Trade(
                        entry_bar=entry_bar,
                        exit_bar=i,
                        entry_time=entry_time,
                        exit_time=bar.end_time,
                        side="LONG",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        duration_seconds=duration_secs,
                        exit_reason="STOP",
                        entry_velocity=entry_velocity,
                        exit_velocity=bar.duration_seconds,
                    )
                    trades.append(trade)

                    print(f"  EXIT LONG @ {exit_price:.2f} (STOP) | PnL: ${pnl:>7.2f} | "
                          f"{bars_in_trade} bars ({duration_secs:.0f}s) | Entry vel: {entry_velocity:.1f}s")

                    position = 0
                    bars_since_exit = 0

                # Check velocity fade exit
                elif bar.duration_seconds > 30:
                    exit_price = bar.close
                    pnl = (exit_price - entry_price) * position

                    trade = Trade(
                        entry_bar=entry_bar,
                        exit_bar=i,
                        entry_time=entry_time,
                        exit_time=bar.end_time,
                        side="LONG",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        duration_seconds=duration_secs,
                        exit_reason="VELOCITY_FADE",
                        entry_velocity=entry_velocity,
                        exit_velocity=bar.duration_seconds,
                    )
                    trades.append(trade)

                    print(f"  EXIT LONG @ {exit_price:.2f} (VEL FADE: {bar.duration_seconds:.1f}s) | "
                          f"PnL: ${pnl:>7.2f} | {bars_in_trade} bars")

                    position = 0
                    bars_since_exit = 0

                # Check imbalance reversal
                elif bar.imbalance_ratio < 0.95:
                    exit_price = bar.close
                    pnl = (exit_price - entry_price) * position

                    trade = Trade(
                        entry_bar=entry_bar,
                        exit_bar=i,
                        entry_time=entry_time,
                        exit_time=bar.end_time,
                        side="LONG",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        duration_seconds=duration_secs,
                        exit_reason="IMBALANCE_REVERSE",
                        entry_velocity=entry_velocity,
                        exit_velocity=bar.duration_seconds,
                    )
                    trades.append(trade)

                    print(f"  EXIT LONG @ {exit_price:.2f} (IMB REV: {bar.imbalance_ratio:.2f}) | "
                          f"PnL: ${pnl:>7.2f} | {bars_in_trade} bars")

                    position = 0
                    bars_since_exit = 0

                # Check time stop
                elif bars_in_trade >= max_hold_bars:
                    exit_price = bar.close
                    pnl = (exit_price - entry_price) * position

                    trade = Trade(
                        entry_bar=entry_bar,
                        exit_bar=i,
                        entry_time=entry_time,
                        exit_time=bar.end_time,
                        side="LONG",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        duration_seconds=duration_secs,
                        exit_reason="TIME_STOP",
                        entry_velocity=entry_velocity,
                        exit_velocity=bar.duration_seconds,
                    )
                    trades.append(trade)

                    print(f"  EXIT LONG @ {exit_price:.2f} (TIME STOP) | PnL: ${pnl:>7.2f} | {bars_in_trade} bars")

                    position = 0
                    bars_since_exit = 0

            elif position < 0:  # Short position
                # Update trailing stop (only move down)
                potential_stop = bar.close + trail_offset
                if potential_stop < stop_price or stop_price == 0:
                    stop_price = potential_stop

                # Check stop hit
                if bar.high >= stop_price:
                    exit_price = stop_price
                    pnl = (entry_price - exit_price) * abs(position)

                    trade = Trade(
                        entry_bar=entry_bar,
                        exit_bar=i,
                        entry_time=entry_time,
                        exit_time=bar.end_time,
                        side="SHORT",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        duration_seconds=duration_secs,
                        exit_reason="STOP",
                        entry_velocity=entry_velocity,
                        exit_velocity=bar.duration_seconds,
                    )
                    trades.append(trade)

                    print(f"  EXIT SHORT @ {exit_price:.2f} (STOP) | PnL: ${pnl:>7.2f} | "
                          f"{bars_in_trade} bars ({duration_secs:.0f}s) | Entry vel: {entry_velocity:.1f}s")

                    position = 0
                    bars_since_exit = 0

                # Check velocity fade exit
                elif bar.duration_seconds > 30:
                    exit_price = bar.close
                    pnl = (entry_price - exit_price) * abs(position)

                    trade = Trade(
                        entry_bar=entry_bar,
                        exit_bar=i,
                        entry_time=entry_time,
                        exit_time=bar.end_time,
                        side="SHORT",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        duration_seconds=duration_secs,
                        exit_reason="VELOCITY_FADE",
                        entry_velocity=entry_velocity,
                        exit_velocity=bar.duration_seconds,
                    )
                    trades.append(trade)

                    print(f"  EXIT SHORT @ {exit_price:.2f} (VEL FADE: {bar.duration_seconds:.1f}s) | "
                          f"PnL: ${pnl:>7.2f} | {bars_in_trade} bars")

                    position = 0
                    bars_since_exit = 0

                # Check imbalance reversal
                elif bar.imbalance_ratio > 1.05:
                    exit_price = bar.close
                    pnl = (entry_price - exit_price) * abs(position)

                    trade = Trade(
                        entry_bar=entry_bar,
                        exit_bar=i,
                        entry_time=entry_time,
                        exit_time=bar.end_time,
                        side="SHORT",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        duration_seconds=duration_secs,
                        exit_reason="IMBALANCE_REVERSE",
                        entry_velocity=entry_velocity,
                        exit_velocity=bar.duration_seconds,
                    )
                    trades.append(trade)

                    print(f"  EXIT SHORT @ {exit_price:.2f} (IMB REV: {bar.imbalance_ratio:.2f}) | "
                          f"PnL: ${pnl:>7.2f} | {bars_in_trade} bars")

                    position = 0
                    bars_since_exit = 0

                # Check time stop
                elif bars_in_trade >= max_hold_bars:
                    exit_price = bar.close
                    pnl = (entry_price - exit_price) * abs(position)

                    trade = Trade(
                        entry_bar=entry_bar,
                        exit_bar=i,
                        entry_time=entry_time,
                        exit_time=bar.end_time,
                        side="SHORT",
                        entry_price=entry_price,
                        exit_price=exit_price,
                        pnl=pnl,
                        duration_bars=bars_in_trade,
                        duration_seconds=duration_secs,
                        exit_reason="TIME_STOP",
                        entry_velocity=entry_velocity,
                        exit_velocity=bar.duration_seconds,
                    )
                    trades.append(trade)

                    print(f"  EXIT SHORT @ {exit_price:.2f} (TIME STOP) | PnL: ${pnl:>7.2f} | {bars_in_trade} bars")

                    position = 0
                    bars_since_exit = 0

        # Entry logic (only if flat AND cooldown passed)
        if position == 0 and bars_since_exit >= cooldown_bars:
            # Skip if previous bar was slow (>60 seconds)
            if i > 0 and bars[i-1].duration_seconds > 60:
                continue

            # Calculate cumulative delta over last 3 bars
            if i >= 3:
                cum_delta_3bars = sum(bars[j].delta for j in range(i-2, i+1))
            else:
                cum_delta_3bars = 0

            # Check for LONG entry
            if (bar.duration_seconds < 5 and  # Fast bar = momentum
                bar.delta > 10 and              # Strong directional volume
                bar.imbalance_ratio > 1.10 and  # Buy pressure
                cum_delta_3bars > 20):          # Trend forming

                # Confirm last 2 bars also bullish
                if i >= 2 and bars[i-1].delta > 0 and bars[i-2].delta > 0:
                    position = 5
                    entry_price = bar.close
                    entry_bar = i
                    entry_time = bar.end_time
                    entry_velocity = bar.duration_seconds
                    stop_price = entry_price - initial_stop_distance

                    print(f"  ENTER LONG @ {entry_price:.2f} | Vel: {bar.duration_seconds:.2f}s | "
                          f"Delta: {bar.delta} | Imb: {bar.imbalance_ratio:.2f} | "
                          f"CumDelta3: {cum_delta_3bars} | Stop: {stop_price:.2f}")

            # Check for SHORT entry
            elif (bar.duration_seconds < 5 and  # Fast bar = momentum
                  bar.delta < -10 and             # Strong directional volume
                  bar.imbalance_ratio < 0.90 and  # Sell pressure
                  cum_delta_3bars < -20):         # Trend forming

                # Confirm last 2 bars also bearish
                if i >= 2 and bars[i-1].delta < 0 and bars[i-2].delta < 0:
                    position = -5
                    entry_price = bar.close
                    entry_bar = i
                    entry_time = bar.end_time
                    entry_velocity = bar.duration_seconds
                    stop_price = entry_price + initial_stop_distance

                    print(f"  ENTER SHORT @ {entry_price:.2f} | Vel: {bar.duration_seconds:.2f}s | "
                          f"Delta: {bar.delta} | Imb: {bar.imbalance_ratio:.2f} | "
                          f"CumDelta3: {cum_delta_3bars} | Stop: {stop_price:.2f}")

    # Close any remaining position
    if position != 0:
        exit_price = bars[-1].close
        from datetime import datetime
        duration_secs = (datetime.strptime(bars[-1].end_time, '%Y-%m-%d %H:%M:%S') -
                       datetime.strptime(entry_time, '%Y-%m-%d %H:%M:%S')).total_seconds()

        if position > 0:
            pnl = (exit_price - entry_price) * position
            side = "LONG"
        else:
            pnl = (entry_price - exit_price) * abs(position)
            side = "SHORT"

        trade = Trade(
            entry_bar=entry_bar,
            exit_bar=len(bars)-1,
            entry_time=entry_time,
            exit_time=bars[-1].end_time,
            side=side,
            entry_price=entry_price,
            exit_price=exit_price,
            pnl=pnl,
            duration_bars=len(bars) - entry_bar,
            duration_seconds=duration_secs,
            exit_reason="EOD",
            entry_velocity=entry_velocity,
            exit_velocity=bars[-1].duration_seconds,
        )
        trades.append(trade)

        print(f"\n  CLOSE {side} @ {exit_price:.2f} (EOD) | PnL: ${pnl:.2f}")

    # Results
    print("\n" + "-" * 80)
    print(f"RESULTS: {date_str}")
    print("-" * 80)

    print(f"\n💰 Trades: {len(trades)}")
    total_pnl = sum(t.pnl for t in trades)
    winners = [t for t in trades if t.pnl > 0]
    losers = [t for t in trades if t.pnl <= 0]

    # Exit reason breakdown
    exit_reasons = {}
    for t in trades:
        exit_reasons[t.exit_reason] = exit_reasons.get(t.exit_reason, 0) + 1

    for idx, t in enumerate(trades, 1):
        result = "WIN" if t.pnl > 0 else "LOSS"
        print(f"   {idx:>2}. {t.side:5s} {t.entry_price:.2f} → {t.exit_price:.2f} | "
              f"${t.pnl:>7.2f} | {t.duration_bars:>2} bars ({t.duration_seconds:>4.0f}s) | "
              f"EntryVel: {t.entry_velocity:>4.1f}s | {t.exit_reason:16s} | {result}")

    print(f"\n   Total PnL: ${total_pnl:.2f}")
    print(f"   Winners: {len(winners)}, Losers: {len(losers)}")
    if trades:
        win_rate = len(winners) / len(trades) * 100
        avg_duration = sum(t.duration_bars for t in trades) / len(trades)
        avg_duration_sec = sum(t.duration_seconds for t in trades) / len(trades)
        avg_entry_vel = sum(t.entry_velocity for t in trades) / len(trades)

        print(f"   Win Rate: {win_rate:.1f}%")
        print(f"   Avg Duration: {avg_duration:.1f} bars ({avg_duration_sec:.0f} seconds)")
        print(f"   Avg Entry Velocity: {avg_entry_vel:.2f} seconds")

        print(f"\n   Exit Reasons:")
        for reason, count in sorted(exit_reasons.items()):
            print(f"      {reason}: {count}")

        if winners:
            avg_win = sum(t.pnl for t in winners) / len(winners)
            print(f"\n   Avg Win: ${avg_win:.2f}")
        if losers:
            avg_loss = sum(t.pnl for t in losers) / len(losers)
            print(f"   Avg Loss: ${avg_loss:.2f}")

    return {
        "date": date_str,
        "expected_regime": expected_regime,
        "bars": len(bars),
        "trades": len(trades),
        "total_pnl": total_pnl,
        "winners": len(winners),
        "losers": len(losers),
        "win_rate": len(winners) / len(trades) * 100 if trades else 0,
        "avg_duration_bars": sum(t.duration_bars for t in trades) / len(trades) if trades else 0,
        "avg_duration_sec": sum(t.duration_seconds for t in trades) / len(trades) if trades else 0,
        "avg_entry_velocity": sum(t.entry_velocity for t in trades) / len(trades) if trades else 0,
    }


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 18 + "1000-TICK MOMENTUM BACKTEST" + " " * 33 + "║")
    print("╚" + "=" * 78 + "╝")

    print("\nStrategy: Velocity-Based Momentum Trading")
    print("  • Enter: Fast bars (<5s) with delta >10, imbalance >1.10/<0.90")
    print("  • Exit: Velocity fade (>30s), imbalance reverse, stops, or time limit")
    print("  • Filters: Skip slow bars (>60s) and chop zones")
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

    print(f"\n{'Date':<12} {'Expected':<10} {'Bars':<8} {'Trades':<8} {'Win%':<8} "
          f"{'Avg Dur':<12} {'Entry Vel':<12} {'P&L':<12}")
    print("-" * 80)
    for r in results:
        print(f"{r['date']:<12} {r['expected_regime']:<10} {r['bars']:<8} {r['trades']:<8} "
              f"{r['win_rate']:<8.1f}% {r['avg_duration_sec']:<12.0f}s "
              f"{r['avg_entry_velocity']:<12.2f}s ${r['total_pnl']:<11.2f}")

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
    print("✅ 1000-TICK MOMENTUM BACKTEST COMPLETE!")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
