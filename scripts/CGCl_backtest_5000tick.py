#!/usr/bin/env python3
"""
5000-Tick Bar Strategy - Clean Price Action

Based on statistics:
- 188-338 bars per day (vs 900-1,700 for 1000-tick)
- Avg duration: 75-134 seconds (vs 15-27s for 1000-tick)
- Avg price move: $7-12 per bar (vs $4-6 for 1000-tick)
- Delta std dev: 13-16 (vs 6-8 for 1000-tick)
- Imbalance: 0.98-1.03 (IGNORE - too tight)

Strategy:
- PRIMARY: Delta >15 or <-15 (stronger directional volume)
- CONFIRM: Velocity <60s (fast bar = momentum)
- CONFIRM: Price moved >$5 in bar (real price action)
- IGNORE: Imbalance ratio (useless at this scale)

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

from scripts.CGCl_backtest_1000tick_momentum import (
    TickBar, Trade, load_trades_for_tick_bars, build_tick_bars
)


def backtest_5000tick(date_str: str, expected_regime: str) -> dict:
    """5000-tick bar backtest."""
    print("\n" + "=" * 80)
    print(f"BACKTESTING: {date_str} (Expected: {expected_regime})")
    print("=" * 80)

    trades_data = load_trades_for_tick_bars(date_str)
    bars = build_tick_bars(trades_data, 5000, date_str)  # 5000-tick bars
    print(f"  Built {len(bars)} 5000-tick bars")

    # Trading parameters - WIDER for larger bars
    position = 0
    entry_price = 0.0
    entry_bar = 0
    entry_time = ""
    stop_price = 0.0
    initial_stop = 15.00  # Wider stop for larger bars
    trail_offset = 12.00   # Wider trail

    entry_velocity = 0.0
    bars_since_exit = 999
    cooldown_bars = 2
    max_hold_bars = 10  # 10 bars × ~90s avg = ~15 minutes max

    trades_list = []

    print(f"\nProcessing {len(bars)} bars...")
    print("5000-TICK Strategy (Clean Price Action):")
    print(f"  • PRIMARY: Delta >15 or <-15 (strong with 5000 trades)")
    print(f"  • VELOCITY: <60s (fast = momentum burst)")
    print(f"  • PRICE ACTION: Bar moved >$5 (real price discovery)")
    print(f"  • STOPS: ${initial_stop} initial, ${trail_offset} trail")
    print(f"  • IGNORE: Imbalance ratio (0.98-1.03 = useless)")
    print(f"  • Cooldown: {cooldown_bars} bars, Max hold: {max_hold_bars} bars\n")

    for i, bar in enumerate(bars):
        bars_since_exit += 1

        if i < 1:
            continue

        # Manage open position
        if position != 0:
            bars_in_trade = i - entry_bar
            from datetime import datetime
            duration_secs = (datetime.strptime(bar.end_time, '%Y-%m-%d %H:%M:%S') -
                           datetime.strptime(entry_time, '%Y-%m-%d %H:%M:%S')).total_seconds()

            if position > 0:  # Long
                potential_stop = bar.close - trail_offset
                if potential_stop > stop_price:
                    stop_price = potential_stop

                if bar.low <= stop_price:
                    exit_price = stop_price
                    pnl = (exit_price - entry_price) * position
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "LONG",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "STOP", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT LONG @ {exit_price:.2f} (STOP) | PnL: ${pnl:>7.2f} | "
                          f"{bars_in_trade} bars ({duration_secs:.0f}s)")
                    position = 0
                    bars_since_exit = 0

                elif bar.duration_seconds > 120:  # Very slow = momentum fade
                    exit_price = bar.close
                    pnl = (exit_price - entry_price) * position
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "LONG",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "VELOCITY_FADE", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT LONG @ {exit_price:.2f} (VEL FADE: {bar.duration_seconds:.0f}s) | "
                          f"PnL: ${pnl:>7.2f}")
                    position = 0
                    bars_since_exit = 0

                elif bars_in_trade >= max_hold_bars:
                    exit_price = bar.close
                    pnl = (exit_price - entry_price) * position
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "LONG",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "TIME_STOP", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT LONG @ {exit_price:.2f} (TIME STOP) | PnL: ${pnl:>7.2f}")
                    position = 0
                    bars_since_exit = 0

            elif position < 0:  # Short
                potential_stop = bar.close + trail_offset
                if potential_stop < stop_price or stop_price == 0:
                    stop_price = potential_stop

                if bar.high >= stop_price:
                    exit_price = stop_price
                    pnl = (entry_price - exit_price) * abs(position)
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "SHORT",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "STOP", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT SHORT @ {exit_price:.2f} (STOP) | PnL: ${pnl:>7.2f} | "
                          f"{bars_in_trade} bars ({duration_secs:.0f}s)")
                    position = 0
                    bars_since_exit = 0

                elif bar.duration_seconds > 120:
                    exit_price = bar.close
                    pnl = (entry_price - exit_price) * abs(position)
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "SHORT",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "VELOCITY_FADE", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT SHORT @ {exit_price:.2f} (VEL FADE: {bar.duration_seconds:.0f}s) | "
                          f"PnL: ${pnl:>7.2f}")
                    position = 0
                    bars_since_exit = 0

                elif bars_in_trade >= max_hold_bars:
                    exit_price = bar.close
                    pnl = (entry_price - exit_price) * abs(position)
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "SHORT",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "TIME_STOP", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT SHORT @ {exit_price:.2f} (TIME STOP) | PnL: ${pnl:>7.2f}")
                    position = 0
                    bars_since_exit = 0

        # Entry logic - CLEAN & SIMPLE
        if position == 0 and bars_since_exit >= cooldown_bars:
            # Calculate price movement in this bar
            price_move = abs(bar.close - bar.open)

            # LONG entry: Strong delta + Fast velocity + Real price move
            if (bar.delta > 15 and                    # Strong buying (5000 trades)
                bar.duration_seconds < 60 and         # Fast = momentum
                price_move > 5):                      # Real price action

                position = 5
                entry_price = bar.close
                entry_bar = i
                entry_time = bar.end_time
                entry_velocity = bar.duration_seconds
                stop_price = entry_price - initial_stop

                print(f"  ENTER LONG @ {entry_price:.2f} | Vel: {bar.duration_seconds:.0f}s | "
                      f"Delta: {bar.delta} | Move: ${price_move:.2f}")

            # SHORT entry: Strong negative delta + Fast velocity + Real price move
            elif (bar.delta < -15 and
                  bar.duration_seconds < 60 and
                  price_move > 5):

                position = -5
                entry_price = bar.close
                entry_bar = i
                entry_time = bar.end_time
                entry_velocity = bar.duration_seconds
                stop_price = entry_price + initial_stop

                print(f"  ENTER SHORT @ {entry_price:.2f} | Vel: {bar.duration_seconds:.0f}s | "
                      f"Delta: {bar.delta} | Move: ${price_move:.2f}")

    # Close remaining
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

        trade = Trade(entry_bar, len(bars)-1, entry_time, bars[-1].end_time, side,
                    entry_price, exit_price, pnl, len(bars) - entry_bar,
                    duration_secs, "EOD", entry_velocity, bars[-1].duration_seconds)
        trades_list.append(trade)
        print(f"\n  CLOSE {side} @ {exit_price:.2f} (EOD) | PnL: ${pnl:.2f}")

    # Results
    print("\n" + "-" * 80)
    print(f"RESULTS: {date_str}")
    print("-" * 80)

    print(f"\n💰 Trades: {len(trades_list)}")
    total_pnl = sum(t.pnl for t in trades_list)
    winners = [t for t in trades_list if t.pnl > 0]
    losers = [t for t in trades_list if t.pnl <= 0]

    exit_reasons = {}
    for t in trades_list:
        exit_reasons[t.exit_reason] = exit_reasons.get(t.exit_reason, 0) + 1

    for idx, t in enumerate(trades_list, 1):
        result = "WIN" if t.pnl > 0 else "LOSS"
        print(f"   {idx:>2}. {t.side:5s} {t.entry_price:.2f} → {t.exit_price:.2f} | "
              f"${t.pnl:>7.2f} | {t.duration_bars:>2} bars ({t.duration_seconds:>4.0f}s) | "
              f"EntryVel: {t.entry_velocity:>4.0f}s | {t.exit_reason:16s} | {result}")

    print(f"\n   Total PnL: ${total_pnl:.2f}")
    print(f"   Winners: {len(winners)}, Losers: {len(losers)}")
    if trades_list:
        win_rate = len(winners) / len(trades_list) * 100
        avg_duration_sec = sum(t.duration_seconds for t in trades_list) / len(trades_list)

        print(f"   Win Rate: {win_rate:.1f}%")
        print(f"   Avg Duration: {avg_duration_sec:.0f} seconds ({avg_duration_sec/60:.1f} minutes)")

        print(f"\n   Exit Reasons:")
        for reason, count in sorted(exit_reasons.items()):
            print(f"      {reason}: {count}")

        if winners:
            avg_win = sum(t.pnl for t in winners) / len(winners)
            print(f"\n   Avg Win: ${avg_win:.2f}")
        if losers:
            avg_loss = sum(t.pnl for t in losers) / len(losers)
            print(f"   Avg Loss: ${avg_loss:.2f}")
            if winners:
                print(f"   Win/Loss Ratio: {avg_win / abs(avg_loss):.2f}")

    return {
        "date": date_str,
        "expected_regime": expected_regime,
        "bars": len(bars),
        "trades": len(trades_list),
        "total_pnl": total_pnl,
        "winners": len(winners),
        "losers": len(losers),
        "win_rate": len(winners) / len(trades_list) * 100 if trades_list else 0,
        "avg_duration_sec": sum(t.duration_seconds for t in trades_list) / len(trades_list) if trades_list else 0,
    }


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 22 + "5000-TICK BAR STRATEGY" + " " * 33 + "║")
    print("╚" + "=" * 78 + "╝")

    print("\nCleaner signals with 5x more volume per bar:")
    print("  • 188-338 bars/day (vs 900-1,700 for 1000-tick)")
    print("  • Avg $7-12 price moves (vs $4-6)")
    print("  • Focus: Delta + Velocity + Price Action")
    print("  • Target: 10-30 trades/day")

    days = [
        ("2025-10-01", "BULL"),
        ("2025-10-10", "BEAR"),
        ("2025-10-15", "SWING"),
    ]

    results = []
    for date_str, expected_regime in days:
        result = backtest_5000tick(date_str, expected_regime)
        results.append(result)

    # Summary
    print("\n\n" + "=" * 80)
    print("SUMMARY: ALL DAYS")
    print("=" * 80)

    print(f"\n{'Date':<12} {'Expected':<10} {'Bars':<8} {'Trades':<8} {'Win%':<8} "
          f"{'Avg Dur':<12} {'P&L':<12}")
    print("-" * 80)
    for r in results:
        avg_min = r['avg_duration_sec'] / 60 if r['avg_duration_sec'] else 0
        print(f"{r['date']:<12} {r['expected_regime']:<10} {r['bars']:<8} {r['trades']:<8} "
              f"{r['win_rate']:<8.1f}% {avg_min:<12.1f}min ${r['total_pnl']:<11.2f}")

    total_pnl = sum(r['total_pnl'] for r in results)
    total_trades = sum(r['trades'] for r in results)

    print(f"\n💰 TOTAL: {total_trades} trades, ${total_pnl:.2f} P&L across 3 days")
    print(f"   Average per day: {total_trades / 3:.1f} trades, ${total_pnl / 3:.2f} P&L")

    print("\n📈 COMPARISON TO TARGETS:")
    print(f"   Bull day: {results[0]['trades']} trades (target: 1-4)")
    print(f"   Bear day: {results[1]['trades']} trades (target: 1-4)")
    print(f"   Swing day: {results[2]['trades']} trades (target: 5-15)")

    print("\n" + "=" * 80)
    print("✅ 5000-TICK BACKTEST COMPLETE!")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
