#!/usr/bin/env python3
"""
Bookmap Strategy Backtester

Tests professional order flow patterns:
1. Absorption Reversal
2. Iceberg Defense
3. Thin Liquidity Breakout
4. Velocity + Delta Momentum

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
from typing import List
import statistics


@dataclass
class OrderFlowEvent:
    """1-second order flow bucket."""
    timestamp: str
    price: float
    bid_adds: int
    ask_adds: int
    bid_cancels: int
    ask_cancels: int
    buy_aggressor: int
    sell_aggressor: int
    aggression_delta: int
    total_volume: int
    net_resting_bid: int
    net_resting_ask: int


@dataclass
class Trade:
    """Trade result."""
    setup_type: str
    entry_time: str
    exit_time: str
    side: str
    entry_price: float
    exit_price: float
    pnl: float
    duration_sec: int
    exit_reason: str


def load_orderflow_data(date_str: str, hour_start: int = 9, hour_end: int = 16) -> List[OrderFlowEvent]:
    """Load 1-second order flow data from ClickHouse."""
    print(f"Loading order flow for {date_str} ({hour_start}:00-{hour_end}:00)...", flush=True)

    query = f"""
    SELECT
        toString(timestamp_1sec) as ts,
        price,
        bid_adds,
        ask_adds,
        bid_cancels,
        ask_cancels,
        buy_aggressor_volume,
        sell_aggressor_volume,
        aggression_delta,
        total_volume,
        net_resting_bid,
        net_resting_ask
    FROM mnq_orderflow_1sec
    WHERE symbol = 'MNQZ5'
      AND toDate(timestamp_1sec) = '{date_str}'
      AND hour(timestamp_1sec) >= {hour_start}
      AND hour(timestamp_1sec) < {hour_end}
    ORDER BY timestamp_1sec, price
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
        events.append(OrderFlowEvent(
            timestamp=row["ts"],
            price=float(row["price"]),
            bid_adds=int(row["bid_adds"]),
            ask_adds=int(row["ask_adds"]),
            bid_cancels=int(row["bid_cancels"]),
            ask_cancels=int(row["ask_cancels"]),
            buy_aggressor=int(row["buy_aggressor_volume"]),
            sell_aggressor=int(row["sell_aggressor_volume"]),
            aggression_delta=int(row["aggression_delta"]),
            total_volume=int(row["total_volume"]),
            net_resting_bid=int(row["net_resting_bid"]),
            net_resting_ask=int(row["net_resting_ask"]),
        ))

    print(f"  Loaded {len(events):,} order flow events")
    return events


def backtest_absorption(events: List[OrderFlowEvent], contracts: int = 5) -> List[Trade]:
    """
    Absorption Reversal Strategy

    Setup: Heavy aggression absorbed by resting liquidity
    Entry: When absorption confirmed
    Exit: Stop loss or target
    """
    trades = []
    position = 0
    entry_price = 0.0
    entry_time = ""
    entry_idx = 0
    setup_type = ""

    stop_loss = 5.0    # $5 stop
    target = 10.0      # $10 target
    max_hold_bars = 300  # 5 minutes max

    print("\n" + "="*80)
    print("ABSORPTION REVERSAL Strategy")
    print("="*80)
    print(f"Entry: Heavy aggression absorbed by resting liquidity")
    print(f"Stop: ${stop_loss} | Target: ${target} | Max hold: {max_hold_bars}s\n")

    for i, event in enumerate(events):
        # Manage position
        if position != 0:
            bars_in_trade = i - entry_idx
            duration_sec = bars_in_trade  # 1 bar = 1 second

            if position > 0:  # Long
                # Check target
                if event.price >= entry_price + target:
                    pnl = target * contracts * 5  # $5 per point per contract
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "LONG",
                        entry_price, entry_price + target, pnl, duration_sec, "TARGET"
                    ))
                    print(f"  ✅ LONG exit @ {entry_price + target:.2f} (TARGET) | PnL: ${pnl:.2f} | {duration_sec}s")
                    position = 0

                # Check stop
                elif event.price <= entry_price - stop_loss:
                    pnl = -stop_loss * contracts * 5
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "LONG",
                        entry_price, entry_price - stop_loss, pnl, duration_sec, "STOP"
                    ))
                    print(f"  ❌ LONG exit @ {entry_price - stop_loss:.2f} (STOP) | PnL: ${pnl:.2f}")
                    position = 0

                # Time stop
                elif bars_in_trade >= max_hold_bars:
                    exit_price = event.price
                    pnl = (exit_price - entry_price) * contracts * 5
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "LONG",
                        entry_price, exit_price, pnl, duration_sec, "TIME"
                    ))
                    print(f"  ⏰ LONG exit @ {exit_price:.2f} (TIME) | PnL: ${pnl:.2f}")
                    position = 0

            elif position < 0:  # Short
                # Check target
                if event.price <= entry_price - target:
                    pnl = target * contracts * 5
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "SHORT",
                        entry_price, entry_price - target, pnl, duration_sec, "TARGET"
                    ))
                    print(f"  ✅ SHORT exit @ {entry_price - target:.2f} (TARGET) | PnL: ${pnl:.2f} | {duration_sec}s")
                    position = 0

                # Check stop
                elif event.price >= entry_price + stop_loss:
                    pnl = -stop_loss * contracts * 5
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "SHORT",
                        entry_price, entry_price + stop_loss, pnl, duration_sec, "STOP"
                    ))
                    print(f"  ❌ SHORT exit @ {entry_price + stop_loss:.2f} (STOP) | PnL: ${pnl:.2f}")
                    position = 0

                # Time stop
                elif bars_in_trade >= max_hold_bars:
                    exit_price = event.price
                    pnl = (entry_price - exit_price) * contracts * 5
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "SHORT",
                        entry_price, exit_price, pnl, duration_sec, "TIME"
                    ))
                    print(f"  ⏰ SHORT exit @ {exit_price:.2f} (TIME) | PnL: ${pnl:.2f}")
                    position = 0

        # Entry logic
        if position == 0:
            # BID ABSORPTION (go long)
            if (event.sell_aggressor > 80 and
                event.bid_adds > event.sell_aggressor * 1.3 and
                event.net_resting_bid > 50 and
                event.total_volume > 100):

                position = contracts
                entry_price = event.price
                entry_time = event.timestamp
                entry_idx = i
                setup_type = "BID_ABSORPTION"

                print(f"🔵 BID ABSORPTION @ {entry_price:.2f}")
                print(f"   Selling: {event.sell_aggressor} | Bid adds: {event.bid_adds} | Net: +{event.net_resting_bid}")

            # ASK ABSORPTION (go short)
            elif (event.buy_aggressor > 80 and
                  event.ask_adds > event.buy_aggressor * 1.3 and
                  event.net_resting_ask > 50 and
                  event.total_volume > 100):

                position = -contracts
                entry_price = event.price
                entry_time = event.timestamp
                entry_idx = i
                setup_type = "ASK_ABSORPTION"

                print(f"🔴 ASK ABSORPTION @ {entry_price:.2f}")
                print(f"   Buying: {event.buy_aggressor} | Ask adds: {event.ask_adds} | Net: +{event.net_resting_ask}")

    return trades


def backtest_icebergs(events: List[OrderFlowEvent], contracts: int = 7) -> List[Trade]:
    """
    Iceberg Defense Strategy

    Setup: Hidden large order detected (high volume, low visible adds)
    Entry: Trade with the iceberg (it's defending level)
    Exit: Stop if iceberg broken, target on reversion
    """
    trades = []
    position = 0
    entry_price = 0.0
    entry_time = ""
    entry_idx = 0

    stop_loss = 3.0    # Tight stop (iceberg defending)
    target = 8.0
    max_hold_bars = 180  # 3 minutes

    print("\n" + "="*80)
    print("ICEBERG DEFENSE Strategy")
    print("="*80)
    print(f"Entry: Trade with hidden institutional order")
    print(f"Stop: ${stop_loss} | Target: ${target} | Max hold: {max_hold_bars}s\n")

    for i, event in enumerate(events):
        # Manage position (same as absorption)
        if position != 0:
            bars_in_trade = i - entry_idx
            duration_sec = bars_in_trade

            if position > 0:
                if event.price >= entry_price + target:
                    pnl = target * contracts * 5
                    trades.append(Trade(
                        "ICEBERG_BID", entry_time, event.timestamp, "LONG",
                        entry_price, entry_price + target, pnl, duration_sec, "TARGET"
                    ))
                    print(f"  ✅ LONG exit @ {entry_price + target:.2f} (TARGET) | PnL: ${pnl:.2f}")
                    position = 0
                elif event.price <= entry_price - stop_loss:
                    pnl = -stop_loss * contracts * 5
                    trades.append(Trade(
                        "ICEBERG_BID", entry_time, event.timestamp, "LONG",
                        entry_price, entry_price - stop_loss, pnl, duration_sec, "STOP"
                    ))
                    print(f"  ❌ LONG exit @ {entry_price - stop_loss:.2f} (STOP)")
                    position = 0
                elif bars_in_trade >= max_hold_bars:
                    exit_price = event.price
                    pnl = (exit_price - entry_price) * contracts * 5
                    trades.append(Trade(
                        "ICEBERG_BID", entry_time, event.timestamp, "LONG",
                        entry_price, exit_price, pnl, duration_sec, "TIME"
                    ))
                    print(f"  ⏰ LONG exit @ {exit_price:.2f} (TIME) | PnL: ${pnl:.2f}")
                    position = 0

            elif position < 0:
                if event.price <= entry_price - target:
                    pnl = target * contracts * 5
                    trades.append(Trade(
                        "ICEBERG_ASK", entry_time, event.timestamp, "SHORT",
                        entry_price, entry_price - target, pnl, duration_sec, "TARGET"
                    ))
                    print(f"  ✅ SHORT exit @ {entry_price - target:.2f} (TARGET) | PnL: ${pnl:.2f}")
                    position = 0
                elif event.price >= entry_price + stop_loss:
                    pnl = -stop_loss * contracts * 5
                    trades.append(Trade(
                        "ICEBERG_ASK", entry_time, event.timestamp, "SHORT",
                        entry_price, entry_price + stop_loss, pnl, duration_sec, "STOP"
                    ))
                    print(f"  ❌ SHORT exit @ {entry_price + stop_loss:.2f} (STOP)")
                    position = 0
                elif bars_in_trade >= max_hold_bars:
                    exit_price = event.price
                    pnl = (entry_price - exit_price) * contracts * 5
                    trades.append(Trade(
                        "ICEBERG_ASK", entry_time, event.timestamp, "SHORT",
                        entry_price, exit_price, pnl, duration_sec, "TIME"
                    ))
                    print(f"  ⏰ SHORT exit @ {exit_price:.2f} (TIME) | PnL: ${pnl:.2f}")
                    position = 0

        # Entry logic - detect icebergs
        if position == 0 and event.total_volume > 100:
            visible_adds = event.bid_adds + event.ask_adds

            if visible_adds > 0:
                iceberg_ratio = event.total_volume / visible_adds

                # BID ICEBERG (go long with it)
                if (iceberg_ratio > 10.0 and
                    event.sell_aggressor > event.buy_aggressor * 1.5 and
                    event.bid_adds < event.sell_aggressor * 0.3):

                    position = contracts
                    entry_price = event.price
                    entry_time = event.timestamp
                    entry_idx = i

                    print(f"🧊 BID ICEBERG @ {entry_price:.2f}")
                    print(f"   Ratio: {iceberg_ratio:.1f}x | Volume: {event.total_volume} | Visible: {visible_adds}")

                # ASK ICEBERG (go short with it)
                elif (iceberg_ratio > 10.0 and
                      event.buy_aggressor > event.sell_aggressor * 1.5 and
                      event.ask_adds < event.buy_aggressor * 0.3):

                    position = -contracts
                    entry_price = event.price
                    entry_time = event.timestamp
                    entry_idx = i

                    print(f"🧊 ASK ICEBERG @ {entry_price:.2f}")
                    print(f"   Ratio: {iceberg_ratio:.1f}x | Volume: {event.total_volume} | Visible: {visible_adds}")

    return trades


def backtest_thin_breakouts(events: List[OrderFlowEvent], contracts: int = 10) -> List[Trade]:
    """
    Thin Liquidity Breakout Strategy

    Setup: Heavy aggression through thin opposing liquidity
    Entry: On breakout confirmation
    Exit: Stop if fails, target on continuation
    """
    trades = []
    position = 0
    entry_price = 0.0
    entry_time = ""
    entry_idx = 0
    setup_type = ""

    stop_loss = 5.0
    target = 15.0      # Wider target for momentum
    max_hold_bars = 120  # 2 minutes (momentum fades fast)

    print("\n" + "="*80)
    print("THIN LIQUIDITY BREAKOUT Strategy")
    print("="*80)
    print(f"Entry: Aggression through vacuum")
    print(f"Stop: ${stop_loss} | Target: ${target} | Max hold: {max_hold_bars}s\n")

    for i, event in enumerate(events):
        # Manage position
        if position != 0:
            bars_in_trade = i - entry_idx
            duration_sec = bars_in_trade

            if position > 0:
                if event.price >= entry_price + target:
                    pnl = target * contracts * 5
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "LONG",
                        entry_price, entry_price + target, pnl, duration_sec, "TARGET"
                    ))
                    print(f"  ✅ LONG exit @ {entry_price + target:.2f} (TARGET) | PnL: ${pnl:.2f}")
                    position = 0
                elif event.price <= entry_price - stop_loss:
                    pnl = -stop_loss * contracts * 5
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "LONG",
                        entry_price, entry_price - stop_loss, pnl, duration_sec, "STOP"
                    ))
                    print(f"  ❌ LONG exit @ {entry_price - stop_loss:.2f} (STOP)")
                    position = 0
                elif bars_in_trade >= max_hold_bars:
                    exit_price = event.price
                    pnl = (exit_price - entry_price) * contracts * 5
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "LONG",
                        entry_price, exit_price, pnl, duration_sec, "TIME"
                    ))
                    print(f"  ⏰ LONG exit @ {exit_price:.2f} (TIME) | PnL: ${pnl:.2f}")
                    position = 0

            elif position < 0:
                if event.price <= entry_price - target:
                    pnl = target * contracts * 5
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "SHORT",
                        entry_price, entry_price - target, pnl, duration_sec, "TARGET"
                    ))
                    print(f"  ✅ SHORT exit @ {entry_price - target:.2f} (TARGET) | PnL: ${pnl:.2f}")
                    position = 0
                elif event.price >= entry_price + stop_loss:
                    pnl = -stop_loss * contracts * 5
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "SHORT",
                        entry_price, entry_price + stop_loss, pnl, duration_sec, "STOP"
                    ))
                    print(f"  ❌ SHORT exit @ {entry_price + stop_loss:.2f} (STOP)")
                    position = 0
                elif bars_in_trade >= max_hold_bars:
                    exit_price = event.price
                    pnl = (entry_price - exit_price) * contracts * 5
                    trades.append(Trade(
                        setup_type, entry_time, event.timestamp, "SHORT",
                        entry_price, exit_price, pnl, duration_sec, "TIME"
                    ))
                    print(f"  ⏰ SHORT exit @ {exit_price:.2f} (TIME) | PnL: ${pnl:.2f}")
                    position = 0

        # Entry logic
        if position == 0 and event.total_volume > 100:
            total_liquidity = event.bid_adds + event.ask_adds

            # BULL BREAKOUT
            if (event.buy_aggressor > 100 and
                event.buy_aggressor > event.sell_aggressor * 2 and
                total_liquidity < 50):

                position = contracts
                entry_price = event.price
                entry_time = event.timestamp
                entry_idx = i
                setup_type = "BULL_BREAKOUT"

                aggression_ratio = event.buy_aggressor / max(total_liquidity, 1)
                print(f"⚡ BULL BREAKOUT @ {entry_price:.2f}")
                print(f"   Buy agg: {event.buy_aggressor} | Liquidity: {total_liquidity} | Ratio: {aggression_ratio:.1f}x")

            # BEAR BREAKOUT
            elif (event.sell_aggressor > 100 and
                  event.sell_aggressor > event.buy_aggressor * 2 and
                  total_liquidity < 50):

                position = -contracts
                entry_price = event.price
                entry_time = event.timestamp
                entry_idx = i
                setup_type = "BEAR_BREAKOUT"

                aggression_ratio = event.sell_aggressor / max(total_liquidity, 1)
                print(f"⚡ BEAR BREAKOUT @ {entry_price:.2f}")
                print(f"   Sell agg: {event.sell_aggressor} | Liquidity: {total_liquidity} | Ratio: {aggression_ratio:.1f}x")

    return trades


def print_results(strategy_name: str, trades: List[Trade]):
    """Print backtest results."""
    print("\n" + "="*80)
    print(f"RESULTS: {strategy_name}")
    print("="*80)

    if not trades:
        print("\n❌ No trades executed")
        return

    total_pnl = sum(t.pnl for t in trades)
    winners = [t for t in trades if t.pnl > 0]
    losers = [t for t in trades if t.pnl <= 0]

    print(f"\n💰 Total Trades: {len(trades)}")
    print(f"   Winners: {len(winners)} | Losers: {len(losers)}")
    print(f"   Win Rate: {len(winners)/len(trades)*100:.1f}%")
    print(f"   Total PnL: ${total_pnl:,.2f}")

    if winners:
        print(f"\n   Avg Win: ${statistics.mean(t.pnl for t in winners):.2f}")
        print(f"   Max Win: ${max(t.pnl for t in winners):.2f}")

    if losers:
        print(f"\n   Avg Loss: ${statistics.mean(t.pnl for t in losers):.2f}")
        print(f"   Max Loss: ${min(t.pnl for t in losers):.2f}")

    if winners and losers:
        avg_win = statistics.mean(t.pnl for t in winners)
        avg_loss = abs(statistics.mean(t.pnl for t in losers))
        print(f"   Win/Loss Ratio: {avg_win/avg_loss:.2f}")

    # Exit reasons
    exit_reasons = {}
    for t in trades:
        exit_reasons[t.exit_reason] = exit_reasons.get(t.exit_reason, 0) + 1

    print(f"\n   Exit Reasons:")
    for reason, count in sorted(exit_reasons.items()):
        print(f"      {reason}: {count}")

    # Average duration
    avg_duration = statistics.mean(t.duration_sec for t in trades)
    print(f"\n   Avg Duration: {avg_duration:.0f} seconds ({avg_duration/60:.1f} minutes)")


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 22 + "BOOKMAP STRATEGY BACKTEST" + " " * 31 + "║")
    print("╚" + "=" * 78 + "╝")

    date_str = "2025-10-10"

    # Load data
    events = load_orderflow_data(date_str, hour_start=9, hour_end=16)

    # Test each strategy
    all_results = {}

    # 1. Absorption
    absorption_trades = backtest_absorption(events, contracts=5)
    all_results["ABSORPTION"] = absorption_trades
    print_results("ABSORPTION REVERSAL", absorption_trades)

    # 2. Icebergs
    iceberg_trades = backtest_icebergs(events, contracts=7)
    all_results["ICEBERG"] = iceberg_trades
    print_results("ICEBERG DEFENSE", iceberg_trades)

    # 3. Thin Breakouts
    breakout_trades = backtest_thin_breakouts(events, contracts=10)
    all_results["BREAKOUT"] = breakout_trades
    print_results("THIN LIQUIDITY BREAKOUT", breakout_trades)

    # Combined summary
    print("\n\n" + "="*80)
    print("COMBINED SUMMARY")
    print("="*80)

    total_trades = sum(len(trades) for trades in all_results.values())
    total_pnl = sum(sum(t.pnl for t in trades) for trades in all_results.values())

    print(f"\nStrategy          Trades    Win%     Total P&L")
    print("-" * 80)

    for strategy, trades in all_results.items():
        if trades:
            winners = len([t for t in trades if t.pnl > 0])
            win_rate = winners / len(trades) * 100
            strategy_pnl = sum(t.pnl for t in trades)
            print(f"{strategy:<16s}  {len(trades):>3d}     {win_rate:>5.1f}%    ${strategy_pnl:>10,.2f}")

    print("-" * 80)
    print(f"{'TOTAL':<16s}  {total_trades:>3d}              ${total_pnl:>10,.2f}")

    print("\n" + "="*80)
    print("✅ BACKTEST COMPLETE")
    print("="*80)
    print()


if __name__ == "__main__":
    main()
