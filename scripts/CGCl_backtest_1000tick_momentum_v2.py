#!/usr/bin/env python3
"""
Backtest 1000-Tick Bar Momentum Strategy - RELAXED THRESHOLDS

Adjusted based on actual statistics:
- Avg abs delta: 4-5 (so >5 is meaningful, not >10)
- Imbalance range: 0.89-1.14 (so >1.05/<0.95 is meaningful, not >1.10/<0.90)
- Chop zone: 0.98-1.02 (tighter, since natural range is near 1.0)

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

# Import from the original script
from CGCl_backtest_1000tick_momentum import (
    TickBar, Trade, load_trades_for_tick_bars, build_tick_bars
)

def backtest_day_v2(date_str: str, expected_regime: str) -> dict:
    """Backtest with RELAXED thresholds."""
    print("\n" + "=" * 80)
    print(f"BACKTESTING: {date_str} (Expected: {expected_regime})")
    print("=" * 80)

    # Load and build tick bars
    trades = load_trades_for_tick_bars(date_str)
    bars = build_tick_bars(trades, 1000, date_str)
    print(f"  Built {len(bars)} 1000-tick bars")

    # Trading parameters
    position = 0
    entry_price = 0.0
    entry_bar = 0
    entry_time = ""
    stop_price = 0.0
    initial_stop_distance = 10.00
    trail_offset = 8.00

    entry_velocity = 0.0
    bars_since_exit = 999
    cooldown_bars = 2
    max_hold_bars = 15  # Longer holds

    trades_list = []

    print(f"\nProcessing {len(bars)} bars...")
    print("RELAXED Strategy Parameters:")
    print(f"  • Entry: Velocity <5s, delta >5 (was >10), imbalance >1.05/<0.95 (was >1.10/<0.90)")
    print(f"  • Entry: Cumulative 3-bar delta >10/<-10 (was >20/<-20)")
    print(f"  • Exit: Velocity >30s, imbalance reverse, or $10/$8 trailing stop")
    print(f"  • Chop filter: 0.98-1.02 (tighter, was 0.95-1.05)")
    print(f"  • Max hold: {max_hold_bars} bars\n")

    for i, bar in enumerate(bars):
        bars_since_exit += 1

        if i < 2:
            continue

        # Chop filter: TIGHTER range (0.98-1.02)
        if i >= 5:
            recent_imbalances = [bars[j].imbalance_ratio for j in range(i-4, i+1)]
            if all(0.98 <= imb <= 1.02 for imb in recent_imbalances):
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
                    print(f"  EXIT LONG @ {exit_price:.2f} (STOP) | PnL: ${pnl:>7.2f} | {bars_in_trade} bars ({duration_secs:.0f}s)")
                    position = 0
                    bars_since_exit = 0

                elif bar.duration_seconds > 30:
                    exit_price = bar.close
                    pnl = (exit_price - entry_price) * position
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "LONG",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "VELOCITY_FADE", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT LONG @ {exit_price:.2f} (VEL FADE: {bar.duration_seconds:.1f}s) | PnL: ${pnl:>7.2f}")
                    position = 0
                    bars_since_exit = 0

                elif bar.imbalance_ratio < 0.98:  # Relaxed from 0.95
                    exit_price = bar.close
                    pnl = (exit_price - entry_price) * position
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "LONG",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "IMBALANCE_REVERSE", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT LONG @ {exit_price:.2f} (IMB REV: {bar.imbalance_ratio:.2f}) | PnL: ${pnl:>7.2f}")
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
                    print(f"  EXIT SHORT @ {exit_price:.2f} (STOP) | PnL: ${pnl:>7.2f} | {bars_in_trade} bars ({duration_secs:.0f}s)")
                    position = 0
                    bars_since_exit = 0

                elif bar.duration_seconds > 30:
                    exit_price = bar.close
                    pnl = (entry_price - exit_price) * abs(position)
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "SHORT",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "VELOCITY_FADE", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT SHORT @ {exit_price:.2f} (VEL FADE: {bar.duration_seconds:.1f}s) | PnL: ${pnl:>7.2f}")
                    position = 0
                    bars_since_exit = 0

                elif bar.imbalance_ratio > 1.02:  # Relaxed from 1.05
                    exit_price = bar.close
                    pnl = (entry_price - exit_price) * abs(position)
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "SHORT",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "IMBALANCE_REVERSE", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT SHORT @ {exit_price:.2f} (IMB REV: {bar.imbalance_ratio:.2f}) | PnL: ${pnl:>7.2f}")
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

        # Entry logic - RELAXED THRESHOLDS
        if position == 0 and bars_since_exit >= cooldown_bars:
            if i > 0 and bars[i-1].duration_seconds > 60:
                continue

            cum_delta_3bars = sum(bars[j].delta for j in range(i-2, i+1)) if i >= 3 else 0

            # LONG entry - RELAXED
            if (bar.duration_seconds < 5 and
                bar.delta > 5 and  # Relaxed from >10
                bar.imbalance_ratio > 1.05 and  # Relaxed from >1.10
                cum_delta_3bars > 10):  # Relaxed from >20

                if i >= 2 and bars[i-1].delta > 0 and bars[i-2].delta > 0:
                    position = 5
                    entry_price = bar.close
                    entry_bar = i
                    entry_time = bar.end_time
                    entry_velocity = bar.duration_seconds
                    stop_price = entry_price - initial_stop_distance

                    print(f"  ENTER LONG @ {entry_price:.2f} | Vel: {bar.duration_seconds:.2f}s | "
                          f"Delta: {bar.delta} | Imb: {bar.imbalance_ratio:.2f} | CumDelta3: {cum_delta_3bars}")

            # SHORT entry - RELAXED
            elif (bar.duration_seconds < 5 and
                  bar.delta < -5 and  # Relaxed from <-10
                  bar.imbalance_ratio < 0.95 and  # Relaxed from <0.90
                  cum_delta_3bars < -10):  # Relaxed from <-20

                if i >= 2 and bars[i-1].delta < 0 and bars[i-2].delta < 0:
                    position = -5
                    entry_price = bar.close
                    entry_bar = i
                    entry_time = bar.end_time
                    entry_velocity = bar.duration_seconds
                    stop_price = entry_price + initial_stop_distance

                    print(f"  ENTER SHORT @ {entry_price:.2f} | Vel: {bar.duration_seconds:.2f}s | "
                          f"Delta: {bar.delta} | Imb: {bar.imbalance_ratio:.2f} | CumDelta3: {cum_delta_3bars}")

    # Close remaining position
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
              f"EntryVel: {t.entry_velocity:>4.1f}s | {t.exit_reason:16s} | {result}")

    print(f"\n   Total PnL: ${total_pnl:.2f}")
    print(f"   Winners: {len(winners)}, Losers: {len(losers)}")
    if trades_list:
        win_rate = len(winners) / len(trades_list) * 100
        avg_duration_sec = sum(t.duration_seconds for t in trades_list) / len(trades_list)
        avg_entry_vel = sum(t.entry_velocity for t in trades_list) / len(trades_list)

        print(f"   Win Rate: {win_rate:.1f}%")
        print(f"   Avg Duration: {avg_duration_sec:.0f} seconds")
        print(f"   Avg Entry Velocity: {avg_entry_vel:.2f} seconds")

        print(f"\n   Exit Reasons:")
        for reason, count in sorted(exit_reasons.items()):
            print(f"      {reason}: {count}")

        if winners:
            print(f"\n   Avg Win: ${sum(t.pnl for t in winners) / len(winners):.2f}")
        if losers:
            print(f"   Avg Loss: ${sum(t.pnl for t in losers) / len(losers):.2f}")

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
        "avg_entry_velocity": sum(t.entry_velocity for t in trades_list) / len(trades_list) if trades_list else 0,
    }


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 14 + "1000-TICK MOMENTUM V2 (RELAXED THRESHOLDS)" + " " * 21 + "║")
    print("╚" + "=" * 78 + "╝")

    print("\nAdjusted based on actual statistics:")
    print("  • Delta: >5 (was >10) - avg abs delta is 4-5")
    print("  • Imbalance: >1.05/<0.95 (was >1.10/<0.90)")
    print("  • Cumulative delta: >10/<-10 (was >20/<-20)")
    print("  • Chop filter: 0.98-1.02 (tighter, was 0.95-1.05)")

    days = [
        ("2025-10-01", "BULL"),
        ("2025-10-10", "BEAR"),
        ("2025-10-15", "SWING"),
    ]

    results = []
    for date_str, expected_regime in days:
        result = backtest_day_v2(date_str, expected_regime)
        results.append(result)

    # Summary
    print("\n\n" + "=" * 80)
    print("SUMMARY: ALL DAYS")
    print("=" * 80)

    print(f"\n{'Date':<12} {'Expected':<10} {'Bars':<8} {'Trades':<8} {'Win%':<8} "
          f"{'Avg Dur (s)':<12} {'P&L':<12}")
    print("-" * 80)
    for r in results:
        print(f"{r['date']:<12} {r['expected_regime']:<10} {r['bars']:<8} {r['trades']:<8} "
              f"{r['win_rate']:<8.1f}% {r['avg_duration_sec']:<12.0f} ${r['total_pnl']:<11.2f}")

    total_pnl = sum(r['total_pnl'] for r in results)
    total_trades = sum(r['trades'] for r in results)

    print(f"\n💰 TOTAL: {total_trades} trades, ${total_pnl:.2f} P&L across 3 days")
    print(f"   Average per day: {total_trades / 3:.1f} trades, ${total_pnl / 3:.2f} P&L")

    print("\n📈 COMPARISON TO TARGETS:")
    print(f"   Bull day: {results[0]['trades']} trades (target: 1-4)")
    print(f"   Bear day: {results[1]['trades']} trades (target: 1-4)")
    print(f"   Swing day: {results[2]['trades']} trades (target: 5-15)")

    print("\n" + "=" * 80)
    print("✅ 1000-TICK MOMENTUM V2 COMPLETE!")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
