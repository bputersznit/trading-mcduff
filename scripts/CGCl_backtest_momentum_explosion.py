#!/usr/bin/env python3
"""
Momentum Explosion Strategy - NO WINDOWS

Combines two objective signals:
1. VELOCITY SPIKE: 5000-tick bar completes in <20 seconds (vs avg 75-134s)
2. DELTA SPIKE: Same bar has delta >40 (vs avg abs delta ~13)

Logic: When BOTH occur together = Explosive directional move starting

NO arbitrary windows, NO lagging indicators
Just detect ACCELERATION as it happens

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


def backtest_momentum_explosion(date_str: str, expected_regime: str) -> dict:
    """Detect and trade explosive momentum moves."""
    print("\n" + "=" * 80)
    print(f"BACKTESTING: {date_str} (Expected: {expected_regime})")
    print("=" * 80)

    trades_data = load_trades_for_tick_bars(date_str)
    bars = build_tick_bars(trades_data, 5000, date_str)
    print(f"  Built {len(bars)} 5000-tick bars")

    # Calculate baseline statistics for this day
    velocities = [b.duration_seconds for b in bars]
    deltas = [abs(b.delta) for b in bars]
    avg_velocity = sum(velocities) / len(velocities)
    avg_abs_delta = sum(deltas) / len(deltas)

    print(f"  Day Statistics:")
    print(f"    Avg bar duration: {avg_velocity:.1f}s")
    print(f"    Avg abs delta: {avg_abs_delta:.1f}")

    # Dynamic thresholds based on this day's characteristics
    velocity_threshold = min(20, avg_velocity * 0.3)  # 30% of average, max 20s
    delta_threshold = max(40, avg_abs_delta * 3)      # 3x average, min 40

    print(f"  Thresholds for today:")
    print(f"    Velocity spike: <{velocity_threshold:.0f}s (FAST)")
    print(f"    Delta spike: >{delta_threshold:.0f} (STRONG)")

    # Trading parameters
    position = 0
    entry_price = 0.0
    entry_bar = 0
    entry_time = ""
    stop_price = 0.0
    initial_stop = 20.00  # Wide stop for explosive moves
    trail_offset = 15.00   # Aggressive trail

    entry_velocity = 0.0
    entry_delta = 0
    bars_since_exit = 999
    cooldown_bars = 3  # Short cooldown - don't want to miss follow-through
    max_hold_bars = 12  # ~15 minutes max

    trades_list = []
    explosion_count = 0  # Track how many explosion signals

    print(f"\nProcessing {len(bars)} bars...")
    print("MOMENTUM EXPLOSION Strategy:")
    print(f"  • SIGNAL: Velocity <{velocity_threshold:.0f}s AND Delta >{delta_threshold:.0f}")
    print(f"  • NO WINDOWS - Just detect explosions")
    print(f"  • STOPS: ${initial_stop} initial, ${trail_offset} trail")
    print(f"  • EXIT: Momentum fade (next bar >60s)")
    print(f"  • Cooldown: {cooldown_bars} bars\n")

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
                # Trail stop
                potential_stop = bar.close - trail_offset
                if potential_stop > stop_price:
                    stop_price = potential_stop

                # Check stop hit
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

                # Momentum fade - next bar is slow
                elif bar.duration_seconds > 60:
                    exit_price = bar.close
                    pnl = (exit_price - entry_price) * position
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "LONG",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "VELOCITY_FADE", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT LONG @ {exit_price:.2f} (MOMENTUM FADE: {bar.duration_seconds:.0f}s) | "
                          f"PnL: ${pnl:>7.2f}")
                    position = 0
                    bars_since_exit = 0

                # Time stop
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
                # Trail stop
                potential_stop = bar.close + trail_offset
                if potential_stop < stop_price or stop_price == 0:
                    stop_price = potential_stop

                # Check stop hit
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

                # Momentum fade
                elif bar.duration_seconds > 60:
                    exit_price = bar.close
                    pnl = (entry_price - exit_price) * abs(position)
                    trade = Trade(entry_bar, i, entry_time, bar.end_time, "SHORT",
                                entry_price, exit_price, pnl, bars_in_trade,
                                duration_secs, "VELOCITY_FADE", entry_velocity, bar.duration_seconds)
                    trades_list.append(trade)
                    print(f"  EXIT SHORT @ {exit_price:.2f} (MOMENTUM FADE: {bar.duration_seconds:.0f}s) | "
                          f"PnL: ${pnl:>7.2f}")
                    position = 0
                    bars_since_exit = 0

                # Time stop
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

        # Entry logic - EXPLOSION DETECTION
        if position == 0 and bars_since_exit >= cooldown_bars:

            # Check for MOMENTUM EXPLOSION
            is_velocity_spike = bar.duration_seconds < velocity_threshold
            is_delta_spike = abs(bar.delta) > delta_threshold

            if is_velocity_spike and is_delta_spike:
                explosion_count += 1

                # Direction determined by delta
                if bar.delta > 0:  # Bullish explosion
                    position = 5
                    entry_price = bar.close
                    entry_bar = i
                    entry_time = bar.end_time
                    entry_velocity = bar.duration_seconds
                    entry_delta = bar.delta
                    stop_price = entry_price - initial_stop

                    print(f"  🚀 EXPLOSION #{explosion_count} - ENTER LONG @ {entry_price:.2f}")
                    print(f"      Velocity: {bar.duration_seconds:.1f}s (threshold: <{velocity_threshold:.0f}s)")
                    print(f"      Delta: +{bar.delta} (threshold: >{delta_threshold:.0f})")
                    print(f"      Stop: {stop_price:.2f}")

                elif bar.delta < 0:  # Bearish explosion
                    position = -5
                    entry_price = bar.close
                    entry_bar = i
                    entry_time = bar.end_time
                    entry_velocity = bar.duration_seconds
                    entry_delta = bar.delta
                    stop_price = entry_price + initial_stop

                    print(f"  💥 EXPLOSION #{explosion_count} - ENTER SHORT @ {entry_price:.2f}")
                    print(f"      Velocity: {bar.duration_seconds:.1f}s (threshold: <{velocity_threshold:.0f}s)")
                    print(f"      Delta: {bar.delta} (threshold: <-{delta_threshold:.0f})")
                    print(f"      Stop: {stop_price:.2f}")

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

    print(f"\n🚀 Explosion Signals Detected: {explosion_count}")
    print(f"💰 Trades Executed: {len(trades_list)}")

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
        "explosions": explosion_count,
        "trades": len(trades_list),
        "total_pnl": total_pnl,
        "winners": len(winners),
        "losers": len(losers),
        "win_rate": len(winners) / len(trades_list) * 100 if trades_list else 0,
        "avg_duration_sec": sum(t.duration_seconds for t in trades_list) / len(trades_list) if trades_list else 0,
    }


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 20 + "MOMENTUM EXPLOSION STRATEGY" + " " * 31 + "║")
    print("╚" + "=" * 78 + "╝")

    print("\nNO WINDOWS - Just detect explosive momentum:")
    print("  • Velocity spike: Bar completes 3x+ faster than average")
    print("  • Delta spike: Delta 3x+ higher than average")
    print("  • BOTH together = Explosion starting")

    days = [
        ("2025-10-01", "BULL"),
        ("2025-10-10", "BEAR"),
        ("2025-10-15", "SWING"),
    ]

    results = []
    for date_str, expected_regime in days:
        result = backtest_momentum_explosion(date_str, expected_regime)
        results.append(result)

    # Summary
    print("\n\n" + "=" * 80)
    print("SUMMARY: ALL DAYS")
    print("=" * 80)

    print(f"\n{'Date':<12} {'Expected':<10} {'Bars':<8} {'Explosions':<12} {'Trades':<8} "
          f"{'Win%':<8} {'Avg Dur':<12} {'P&L':<12}")
    print("-" * 80)
    for r in results:
        avg_min = r['avg_duration_sec'] / 60 if r['avg_duration_sec'] else 0
        print(f"{r['date']:<12} {r['expected_regime']:<10} {r['bars']:<8} {r['explosions']:<12} "
              f"{r['trades']:<8} {r['win_rate']:<8.1f}% {avg_min:<12.1f}min ${r['total_pnl']:<11.2f}")

    total_pnl = sum(r['total_pnl'] for r in results)
    total_trades = sum(r['trades'] for r in results)
    total_explosions = sum(r['explosions'] for r in results)

    print(f"\n💰 TOTAL: {total_trades} trades from {total_explosions} explosions, "
          f"${total_pnl:.2f} P&L across 3 days")
    print(f"   Average per day: {total_trades / 3:.1f} trades, ${total_pnl / 3:.2f} P&L")
    print(f"   Signal efficiency: {total_trades}/{total_explosions} = "
          f"{total_trades/total_explosions*100:.0f}% of signals traded")

    print("\n📈 COMPARISON TO TARGETS:")
    print(f"   Bull day: {results[0]['trades']} trades (target: 1-4)")
    print(f"   Bear day: {results[1]['trades']} trades (target: 1-4)")
    print(f"   Swing day: {results[2]['trades']} trades (target: 5-15)")

    print("\n" + "=" * 80)
    print("✅ MOMENTUM EXPLOSION BACKTEST COMPLETE!")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
