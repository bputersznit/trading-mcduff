#!/usr/bin/env python3
"""
Conservative Absorption Strategy Backtest

Position Management:
- 1 position at a time (no overlapping)
- Cooldown: 30 seconds between trades
- Best signal per second only
- Target: 20-40 trades/day

Costs:
- Commission: $0.70 per round-trip
- Slippage: 1 tick ($0.50) per side = $1.00 total
- Total cost: $1.70 per contract per round-trip

MNQ Specs:
- Tick size: 0.25 points
- Tick value: $0.50
- Point value: $2.00

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
import statistics


@dataclass
class Signal:
    """Absorption signal."""
    timestamp: str
    price: float
    side: str  # 'LONG' or 'SHORT'
    strength: float  # Signal quality score


@dataclass
class Trade:
    """Trade execution."""
    entry_time: str
    exit_time: str
    side: str
    entry_price: float
    exit_price: float
    gross_pnl: float
    costs: float
    net_pnl: float
    duration_sec: int
    exit_reason: str


def load_absorption_signals(date_str: str, hour_start: int = 9, hour_end: int = 16) -> list[Signal]:
    """
    Load absorption signals - BEST SIGNAL PER SECOND only.

    Conservative filters:
    - Heavy aggression (>60 contracts)
    - Strong absorption response (>1.5x)
    - Significant net resting (>40)
    """
    print(f"Loading absorption signals for {date_str}...", flush=True)

    query = f"""
    WITH bid_absorption AS (
        SELECT
            timestamp_1sec,
            argMax(price, sell_aggressor_volume) as price,
            max(sell_aggressor_volume) as selling,
            argMax(bid_adds, sell_aggressor_volume) as bid_response,
            argMax(net_resting_bid, sell_aggressor_volume) as net_bid,
            'LONG' as side
        FROM mnq_orderflow_1sec
        WHERE symbol = 'MNQZ5'
          AND toDate(timestamp_1sec) = '{date_str}'
          AND hour(timestamp_1sec) >= {hour_start}
          AND hour(timestamp_1sec) < {hour_end}
          AND sell_aggressor_volume > 40
          AND bid_adds > sell_aggressor_volume * 1.3
          AND net_resting_bid > 30
        GROUP BY timestamp_1sec
        HAVING max(sell_aggressor_volume) > 40
    ),
    ask_absorption AS (
        SELECT
            timestamp_1sec,
            argMax(price, buy_aggressor_volume) as price,
            max(buy_aggressor_volume) as buying,
            argMax(ask_adds, buy_aggressor_volume) as ask_response,
            argMax(net_resting_ask, buy_aggressor_volume) as net_ask,
            'SHORT' as side
        FROM mnq_orderflow_1sec
        WHERE symbol = 'MNQZ5'
          AND toDate(timestamp_1sec) = '{date_str}'
          AND hour(timestamp_1sec) >= {hour_start}
          AND hour(timestamp_1sec) < {hour_end}
          AND buy_aggressor_volume > 40
          AND ask_adds > buy_aggressor_volume * 1.3
          AND net_resting_ask > 30
        GROUP BY timestamp_1sec
        HAVING max(buy_aggressor_volume) > 40
    )
    SELECT
        toString(timestamp_1sec) as ts,
        price,
        side,
        selling as strength
    FROM bid_absorption

    UNION ALL

    SELECT
        toString(timestamp_1sec) as ts,
        price,
        side,
        buying as strength
    FROM ask_absorption

    ORDER BY ts
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

    signals = []
    for row in data["data"]:
        signals.append(Signal(
            timestamp=row["ts"],
            price=float(row["price"]),
            side=row["side"],
            strength=float(row["strength"])
        ))

    print(f"  Found {len(signals):,} absorption signals")
    return signals


def get_price_at_time(date_str: str, timestamp: str) -> float:
    """Get market price at specific timestamp (for exits)."""
    query = f"""
    SELECT avg(price) as price
    FROM mnq_orderflow_1sec
    WHERE symbol = 'MNQZ5'
      AND timestamp_1sec = '{timestamp}'
    FORMAT JSON
    """

    result = subprocess.run(
        ["clickhouse-client", "--query", query],
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        return None

    data = json.loads(result.stdout)
    if data["data"]:
        return float(data["data"][0]["price"])
    return None


def backtest_conservative(date_str: str, signals: list[Signal]) -> list[Trade]:
    """
    Conservative absorption backtest.

    Rules:
    - 1 position at a time
    - 30 second cooldown between trades
    - 5 contracts
    - 12 point target ($24 gross)
    - 6 point stop ($12 gross)
    - 5 minute max hold
    """
    print("\n" + "="*80)
    print(f"CONSERVATIVE ABSORPTION BACKTEST: {date_str}")
    print("="*80)
    print(f"Position Size: 5 contracts")
    print(f"Target: 12 points ($24 gross)")
    print(f"Stop: 6 points ($12 gross)")
    print(f"Max Hold: 5 minutes")
    print(f"Cooldown: 30 seconds")
    print(f"Costs: $0.70 commission + $1.00 slippage = $1.70/contract\n")

    CONTRACTS = 5
    POINT_VALUE = 2.00  # $2 per point for MNQ
    TARGET_POINTS = 12.0
    STOP_POINTS = 6.0
    MAX_HOLD_SEC = 300  # 5 minutes
    COOLDOWN_SEC = 30
    COMMISSION = 0.70
    SLIPPAGE_PER_CONTRACT = 1.00  # 2 ticks total (1 tick entry + 1 tick exit)

    trades = []
    position = None
    last_exit_time = None

    for i, signal in enumerate(signals):
        signal_time = datetime.strptime(signal.timestamp, '%Y-%m-%d %H:%M:%S')

        # Skip if in cooldown
        if last_exit_time:
            last_exit_dt = datetime.strptime(last_exit_time, '%Y-%m-%d %H:%M:%S')
            if (signal_time - last_exit_dt).total_seconds() < COOLDOWN_SEC:
                continue

        # Skip if already in position
        if position:
            # Check for exit
            entry_time = datetime.strptime(position['entry_time'], '%Y-%m-%d %H:%M:%S')
            duration = (signal_time - entry_time).total_seconds()

            # Get current price (use signal price as approximation)
            current_price = signal.price

            if position['side'] == 'LONG':
                pnl_points = current_price - position['entry_price']

                # Check target
                if pnl_points >= TARGET_POINTS:
                    exit_price = position['entry_price'] + TARGET_POINTS
                    gross_pnl = TARGET_POINTS * POINT_VALUE * CONTRACTS
                    costs = (COMMISSION + SLIPPAGE_PER_CONTRACT) * CONTRACTS
                    net_pnl = gross_pnl - costs

                    trades.append(Trade(
                        position['entry_time'],
                        signal.timestamp,
                        'LONG',
                        position['entry_price'],
                        exit_price,
                        gross_pnl,
                        costs,
                        net_pnl,
                        int(duration),
                        'TARGET'
                    ))

                    print(f"  ✅ LONG {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | TARGET")

                    position = None
                    last_exit_time = signal.timestamp
                    continue

                # Check stop
                elif pnl_points <= -STOP_POINTS:
                    exit_price = position['entry_price'] - STOP_POINTS
                    gross_pnl = -STOP_POINTS * POINT_VALUE * CONTRACTS
                    costs = (COMMISSION + SLIPPAGE_PER_CONTRACT) * CONTRACTS
                    net_pnl = gross_pnl - costs

                    trades.append(Trade(
                        position['entry_time'],
                        signal.timestamp,
                        'LONG',
                        position['entry_price'],
                        exit_price,
                        gross_pnl,
                        costs,
                        net_pnl,
                        int(duration),
                        'STOP'
                    ))

                    print(f"  ❌ LONG {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | STOP")

                    position = None
                    last_exit_time = signal.timestamp
                    continue

                # Check time stop
                elif duration >= MAX_HOLD_SEC:
                    exit_price = current_price
                    gross_pnl = pnl_points * POINT_VALUE * CONTRACTS
                    costs = (COMMISSION + SLIPPAGE_PER_CONTRACT) * CONTRACTS
                    net_pnl = gross_pnl - costs

                    trades.append(Trade(
                        position['entry_time'],
                        signal.timestamp,
                        'LONG',
                        position['entry_price'],
                        exit_price,
                        gross_pnl,
                        costs,
                        net_pnl,
                        int(duration),
                        'TIME'
                    ))

                    print(f"  ⏰ LONG {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | TIME")

                    position = None
                    last_exit_time = signal.timestamp
                    continue

            elif position['side'] == 'SHORT':
                pnl_points = position['entry_price'] - current_price

                # Check target
                if pnl_points >= TARGET_POINTS:
                    exit_price = position['entry_price'] - TARGET_POINTS
                    gross_pnl = TARGET_POINTS * POINT_VALUE * CONTRACTS
                    costs = (COMMISSION + SLIPPAGE_PER_CONTRACT) * CONTRACTS
                    net_pnl = gross_pnl - costs

                    trades.append(Trade(
                        position['entry_time'],
                        signal.timestamp,
                        'SHORT',
                        position['entry_price'],
                        exit_price,
                        gross_pnl,
                        costs,
                        net_pnl,
                        int(duration),
                        'TARGET'
                    ))

                    print(f"  ✅ SHORT {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | TARGET")

                    position = None
                    last_exit_time = signal.timestamp
                    continue

                # Check stop
                elif pnl_points <= -STOP_POINTS:
                    exit_price = position['entry_price'] + STOP_POINTS
                    gross_pnl = -STOP_POINTS * POINT_VALUE * CONTRACTS
                    costs = (COMMISSION + SLIPPAGE_PER_CONTRACT) * CONTRACTS
                    net_pnl = gross_pnl - costs

                    trades.append(Trade(
                        position['entry_time'],
                        signal.timestamp,
                        'SHORT',
                        position['entry_price'],
                        exit_price,
                        gross_pnl,
                        costs,
                        net_pnl,
                        int(duration),
                        'STOP'
                    ))

                    print(f"  ❌ SHORT {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | STOP")

                    position = None
                    last_exit_time = signal.timestamp
                    continue

                # Check time stop
                elif duration >= MAX_HOLD_SEC:
                    exit_price = current_price
                    gross_pnl = pnl_points * POINT_VALUE * CONTRACTS
                    costs = (COMMISSION + SLIPPAGE_PER_CONTRACT) * CONTRACTS
                    net_pnl = gross_pnl - costs

                    trades.append(Trade(
                        position['entry_time'],
                        signal.timestamp,
                        'SHORT',
                        position['entry_price'],
                        exit_price,
                        gross_pnl,
                        costs,
                        net_pnl,
                        int(duration),
                        'TIME'
                    ))

                    print(f"  ⏰ SHORT {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | TIME")

                    position = None
                    last_exit_time = signal.timestamp
                    continue

        # Enter new position if flat
        if position is None:
            position = {
                'entry_time': signal.timestamp,
                'entry_price': signal.price,
                'side': signal.side
            }

            print(f"\n{'🔵' if signal.side == 'LONG' else '🔴'} {signal.side} @ {signal.price:.2f} | "
                  f"Strength: {signal.strength:.0f}")

    return trades


def print_results(date_str: str, trades: list[Trade]):
    """Print backtest results."""
    print("\n" + "="*80)
    print(f"RESULTS: {date_str}")
    print("="*80)

    if not trades:
        print("\n❌ No trades executed")
        return

    winners = [t for t in trades if t.net_pnl > 0]
    losers = [t for t in trades if t.net_pnl <= 0]

    print(f"\n💰 Total Trades: {len(trades)}")
    print(f"   Winners: {len(winners)} ({len(winners)/len(trades)*100:.1f}%)")
    print(f"   Losers: {len(losers)} ({len(losers)/len(trades)*100:.1f}%)")

    total_gross = sum(t.gross_pnl for t in trades)
    total_costs = sum(t.costs for t in trades)
    total_net = sum(t.net_pnl for t in trades)

    print(f"\n   Gross P&L: ${total_gross:,.2f}")
    print(f"   Total Costs: ${total_costs:,.2f}")
    print(f"   NET P&L: ${total_net:,.2f}")

    if winners:
        avg_win = statistics.mean(t.net_pnl for t in winners)
        print(f"\n   Avg Win: ${avg_win:.2f}")
        print(f"   Max Win: ${max(t.net_pnl for t in winners):.2f}")

    if losers:
        avg_loss = statistics.mean(t.net_pnl for t in losers)
        print(f"   Avg Loss: ${avg_loss:.2f}")
        print(f"   Max Loss: ${min(t.net_pnl for t in losers):.2f}")

    if winners and losers:
        avg_win = statistics.mean(t.net_pnl for t in winners)
        avg_loss = abs(statistics.mean(t.net_pnl for t in losers))
        print(f"   Win/Loss Ratio: {avg_win/avg_loss:.2f}")

    # Exit reasons
    exit_reasons = {}
    for t in trades:
        exit_reasons[t.exit_reason] = exit_reasons.get(t.exit_reason, 0) + 1

    print(f"\n   Exit Breakdown:")
    for reason, count in sorted(exit_reasons.items()):
        pct = count / len(trades) * 100
        print(f"      {reason}: {count} ({pct:.1f}%)")

    avg_duration = statistics.mean(t.duration_sec for t in trades)
    print(f"\n   Avg Duration: {avg_duration:.0f}s ({avg_duration/60:.1f}min)")

    return {
        'date': date_str,
        'trades': len(trades),
        'win_rate': len(winners)/len(trades)*100 if trades else 0,
        'net_pnl': total_net,
        'avg_duration': avg_duration
    }


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 18 + "CONSERVATIVE ABSORPTION BACKTEST" + " " * 26 + "║")
    print("╚" + "=" * 78 + "╝")

    days = [
        ("2025-10-01", "BULL"),
        ("2025-10-10", "BEAR"),
        ("2025-10-15", "SWING"),
    ]

    all_results = []

    for date_str, regime in days:
        print(f"\n\n{'='*80}")
        print(f"Testing {date_str} ({regime})")
        print(f"{'='*80}")

        signals = load_absorption_signals(date_str, hour_start=9, hour_end=16)
        trades = backtest_conservative(date_str, signals)
        result = print_results(date_str, trades)
        all_results.append(result)

    # Summary
    print("\n\n" + "="*80)
    print("SUMMARY: ALL DAYS")
    print("="*80)

    print(f"\n{'Date':<12} {'Regime':<8} {'Trades':<8} {'Win%':<8} {'Net P&L':<12}")
    print("-" * 80)

    for r in all_results:
        print(f"{r['date']:<12} {'':<8} {r['trades']:<8} {r['win_rate']:<7.1f}% ${r['net_pnl']:<11,.2f}")

    total_trades = sum(r['trades'] for r in all_results)
    total_pnl = sum(r['net_pnl'] for r in all_results)

    print("-" * 80)
    print(f"{'TOTAL':<12} {'':<8} {total_trades:<8} {'':<8} ${total_pnl:,.2f}")

    print(f"\n   Avg per day: {total_trades/3:.1f} trades, ${total_pnl/3:,.2f} P&L")
    print(f"   Target: 20-40 trades/day (Conservative)")

    print("\n" + "="*80)
    print("✅ BACKTEST COMPLETE")
    print("="*80)
    print()


if __name__ == "__main__":
    main()
