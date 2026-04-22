#!/usr/bin/env python3
"""
Multi-Strategy Bookmap Backtest

Combines multiple Bookmap patterns:
- Absorption Reversal (bid/ask absorption)
- Iceberg Defense (hidden institutional orders)
- Thin Breakout (aggression through vacuum)

Position Management:
- 1 position at a time (no overlapping)
- NO cooldown between trades
- HIGH FREQUENCY thresholds (relaxed for more signals)
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
    """Trading signal from any strategy."""
    timestamp: str
    price: float
    side: str  # 'LONG' or 'SHORT'
    strategy: str  # 'ABSORPTION', 'ICEBERG', 'BREAKOUT'
    strength: float


@dataclass
class Trade:
    """Trade execution."""
    entry_time: str
    exit_time: str
    side: str
    strategy: str
    entry_price: float
    exit_price: float
    gross_pnl: float
    costs: float
    net_pnl: float
    duration_sec: int
    exit_reason: str


def load_absorption_signals(date_str: str) -> list[Signal]:
    """
    Load absorption signals with HIGH FREQUENCY thresholds.

    Filters (RELAXED for more signals):
    - Light aggression (>30) - was 50
    - Light absorption (>1.1x) - was 1.2x
    - Light net resting (>15) - was 30
    """
    query = f"""
    WITH bid_absorption AS (
        SELECT
            timestamp_1sec,
            argMax(price, sell_aggressor_volume) as price,
            max(sell_aggressor_volume) as strength
        FROM mnq_orderflow_1sec
        WHERE symbol = 'MNQZ5'
          AND toDate(timestamp_1sec) = '{date_str}'
          AND hour(timestamp_1sec) >= 9
          AND hour(timestamp_1sec) < 16
          AND sell_aggressor_volume > 30
          AND bid_adds > sell_aggressor_volume * 1.1
          AND net_resting_bid > 15
        GROUP BY timestamp_1sec
    ),
    ask_absorption AS (
        SELECT
            timestamp_1sec,
            argMax(price, buy_aggressor_volume) as price,
            max(buy_aggressor_volume) as strength
        FROM mnq_orderflow_1sec
        WHERE symbol = 'MNQZ5'
          AND toDate(timestamp_1sec) = '{date_str}'
          AND hour(timestamp_1sec) >= 9
          AND hour(timestamp_1sec) < 16
          AND buy_aggressor_volume > 30
          AND ask_adds > buy_aggressor_volume * 1.1
          AND net_resting_ask > 15
        GROUP BY timestamp_1sec
    )
    SELECT
        toString(timestamp_1sec) as ts,
        price,
        'LONG' as side,
        'ABSORPTION' as strategy,
        strength
    FROM bid_absorption

    UNION ALL

    SELECT
        toString(timestamp_1sec) as ts,
        price,
        'SHORT' as side,
        'ABSORPTION' as strategy,
        strength
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
        return []

    data = json.loads(result.stdout)

    signals = []
    for row in data["data"]:
        signals.append(Signal(
            timestamp=row["ts"],
            price=float(row["price"]),
            side=row["side"],
            strategy=row["strategy"],
            strength=float(row["strength"])
        ))

    return signals


def load_iceberg_signals(date_str: str) -> list[Signal]:
    """
    Load iceberg detection signals.

    Iceberg = High volume with very low visible adds/cancels
    Suggests hidden institutional order absorbing flow.

    Filters (RELAXED for more signals):
    - Total volume > 60 contracts (was 80)
    - Volume/Adds ratio > 6.0 (was 8.0)
    """
    query = f"""
    WITH iceberg_bid AS (
        SELECT
            timestamp_1sec,
            argMax(price, total_volume) as price,
            max(total_volume) as volume,
            min(bid_adds) as adds
        FROM mnq_orderflow_1sec
        WHERE symbol = 'MNQZ5'
          AND toDate(timestamp_1sec) = '{date_str}'
          AND hour(timestamp_1sec) >= 9
          AND hour(timestamp_1sec) < 16
          AND total_volume > 60
          AND bid_adds > 0
          AND total_volume / bid_adds > 6.0
          AND sell_aggressor_volume > buy_aggressor_volume
        GROUP BY timestamp_1sec
    ),
    iceberg_ask AS (
        SELECT
            timestamp_1sec,
            argMax(price, total_volume) as price,
            max(total_volume) as volume,
            min(ask_adds) as adds
        FROM mnq_orderflow_1sec
        WHERE symbol = 'MNQZ5'
          AND toDate(timestamp_1sec) = '{date_str}'
          AND hour(timestamp_1sec) >= 9
          AND hour(timestamp_1sec) < 16
          AND total_volume > 60
          AND ask_adds > 0
          AND total_volume / ask_adds > 6.0
          AND buy_aggressor_volume > sell_aggressor_volume
        GROUP BY timestamp_1sec
    )
    SELECT
        toString(timestamp_1sec) as ts,
        price,
        'LONG' as side,
        'ICEBERG' as strategy,
        volume as strength
    FROM iceberg_bid

    UNION ALL

    SELECT
        toString(timestamp_1sec) as ts,
        price,
        'SHORT' as side,
        'ICEBERG' as strategy,
        volume as strength
    FROM iceberg_ask

    ORDER BY ts
    FORMAT JSON
    """

    result = subprocess.run(
        ["clickhouse-client", "--query", query],
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        return []

    data = json.loads(result.stdout)

    signals = []
    for row in data["data"]:
        signals.append(Signal(
            timestamp=row["ts"],
            price=float(row["price"]),
            side=row["side"],
            strategy=row["strategy"],
            strength=float(row["strength"])
        ))

    return signals


def load_breakout_signals(date_str: str) -> list[Signal]:
    """
    Load thin liquidity breakout signals - REDESIGNED.

    NEW Breakout = Quiet market → Sudden one-sided aggression spike
    Catches vacuum scenarios where sleepy market gets hit.

    Detection (RELAXED for more signals):
    - Rolling 30-second baseline for volume/aggression
    - Current volume > 2.0x baseline (was 2.5x)
    - Current aggression > 2.5x baseline (was 3.0x)
    - Liquidity thin relative to aggression
    """
    query = f"""
    WITH second_agg AS (
        -- Aggregate to second level first
        SELECT
            timestamp_1sec,
            sum(buy_aggressor_volume) as total_buy_agg,
            sum(sell_aggressor_volume) as total_sell_agg,
            sum(buy_aggressor_volume) - sum(sell_aggressor_volume) as agg_delta,
            sum(total_volume) as total_vol,
            sum(bid_adds + ask_adds) as total_adds,
            argMax(price, CAST(total_volume AS Float64)) as price
        FROM mnq_orderflow_1sec
        WHERE symbol = 'MNQZ5'
          AND toDate(timestamp_1sec) = '{date_str}'
          AND hour(timestamp_1sec) >= 9
          AND hour(timestamp_1sec) < 16
        GROUP BY timestamp_1sec
    ),
    with_baseline AS (
        -- Calculate rolling baseline (last 30 seconds)
        SELECT
            *,
            avg(total_vol) OVER (ORDER BY timestamp_1sec ROWS BETWEEN 30 PRECEDING AND 1 PRECEDING) as vol_baseline,
            avg(abs(agg_delta)) OVER (ORDER BY timestamp_1sec ROWS BETWEEN 30 PRECEDING AND 1 PRECEDING) as agg_baseline
        FROM second_agg
    )
    SELECT
        toString(timestamp_1sec) as ts,
        price,
        if(agg_delta > 0, 'LONG', 'SHORT') as side,
        'BREAKOUT' as strategy,
        abs(agg_delta) as strength
    FROM with_baseline
    WHERE total_vol > vol_baseline * 2.0           -- Sudden volume spike (relaxed from 2.5x)
      AND abs(agg_delta) > agg_baseline * 2.5      -- Strong aggression spike (relaxed from 3.0x)
      AND total_vol > 25                           -- Minimum volume threshold (relaxed from 30)
      AND abs(agg_delta) > 12                      -- Minimum aggression threshold (relaxed from 15)
      AND total_adds < abs(agg_delta) * 20         -- Book thin relative to aggression (relaxed from 15x)
      AND vol_baseline > 0                         -- Valid baseline
      AND agg_baseline > 0
    ORDER BY ts
    FORMAT JSON
    """

    result = subprocess.run(
        ["clickhouse-client", "--query", query],
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        return []

    data = json.loads(result.stdout)

    signals = []
    for row in data["data"]:
        signals.append(Signal(
            timestamp=row["ts"],
            price=float(row["price"]),
            side=row["side"],
            strategy=row["strategy"],
            strength=float(row["strength"])
        ))

    return signals


def backtest_multi_strategy(date_str: str, signals: list[Signal]) -> list[Trade]:
    """
    Multi-strategy backtest with conservative position management.

    Strategy-specific parameters:
    - ABSORPTION: 12pt target, 6pt stop, 5min hold
    - ICEBERG: 10pt target, 5pt stop, 3min hold
    - BREAKOUT: 15pt target, 7pt stop, 2min hold

    Conservative rules:
    - 1 position at a time
    - No cooldown
    - 5 contracts

    RISK MANAGEMENT STOPS:
    - Rolling trades/hour gate (continuous monitoring)
    - NO daily P&L limit
    """
    print("\n" + "="*80)
    print(f"SCALPING BACKTEST: {date_str}")
    print("="*80)
    print(f"Position Size: 1 CONTRACT (account survival mode)")
    print(f"Cooldown: NONE (immediate re-entry)")
    print(f"Risk Limits:")
    print(f"  • Rolling trades/hour < 4.0 (60-min window)")
    print(f"  • Weekly cumulative loss: -$250 (5-day rolling)")
    print(f"  • HARD STOP: -$500 total (NT8 survival)")
    print(f"Costs: $0.70 commission + $1.00 slippage = $1.70/contract\n")

    CONTRACTS = 1  # SINGLE CONTRACT ONLY
    POINT_VALUE = 2.00
    COOLDOWN_SEC = 0  # No cooldown
    COMMISSION = 0.70
    SLIPPAGE_PER_CONTRACT = 1.00

    # RISK MANAGEMENT LIMITS
    ROLLING_TRADES_PER_HOUR_MIN = 4.0  # Stop if < 4 trades/hour in last 60 minutes
    ROLLING_WINDOW_SECONDS = 3600  # 60 minutes
    MIN_TRADES_BEFORE_CHECK = 5  # Need at least 5 trades before checking rate
    WEEKLY_LOSS_LIMIT = -250.0  # Stop if lose $250 in rolling 5-day window
    HARD_LOSS_LIMIT = -500.0  # Absolute maximum loss (NT8 survival)

    # SCALPING: Tighter targets/stops, faster exits
    PARAMS = {
        'ABSORPTION': {'target': 6.0, 'stop': 3.0, 'max_hold': 120},  # 2min hold, 2:1 R/R
        'ICEBERG': {'target': 5.0, 'stop': 2.5, 'max_hold': 90},      # 1.5min hold, 2:1 R/R
        'BREAKOUT': {'target': 8.0, 'stop': 4.0, 'max_hold': 60},     # 1min hold, 2:1 R/R
    }

    trades = []
    position = None
    last_exit_time = None
    consecutive_losses = 0
    daily_pnl = 0.0
    risk_stop_triggered = False
    risk_stop_reason = None

    for signal in signals:
        # CHECK RISK MANAGEMENT STOP
        if risk_stop_triggered:
            break

        # Rolling trades/hour check (after minimum trades)
        if len(trades) >= MIN_TRADES_BEFORE_CHECK:
            # Calculate rolling window start time
            signal_time = datetime.strptime(signal.timestamp, '%Y-%m-%d %H:%M:%S')
            window_start = signal_time.timestamp() - ROLLING_WINDOW_SECONDS

            # Count trades in the last 60 minutes
            recent_trades = [
                t for t in trades
                if datetime.strptime(t.entry_time, '%Y-%m-%d %H:%M:%S').timestamp() >= window_start
            ]

            if len(recent_trades) > 0:
                # Calculate time span of recent trades
                earliest_trade_time = datetime.strptime(recent_trades[0].entry_time, '%Y-%m-%d %H:%M:%S')
                time_span_hours = (signal_time - earliest_trade_time).total_seconds() / 3600

                if time_span_hours > 0:
                    trades_per_hour = len(recent_trades) / time_span_hours

                    # Check if below threshold
                    if trades_per_hour < ROLLING_TRADES_PER_HOUR_MIN:
                        risk_stop_triggered = True
                        risk_stop_reason = f"ROLLING TRADES/HR ({trades_per_hour:.1f}/hr in last {time_span_hours*60:.0f}min)"
                        print(f"\n🛑 {risk_stop_reason} - Stopping for the day")
                        break
        signal_time = datetime.strptime(signal.timestamp, '%Y-%m-%d %H:%M:%S')

        # Skip if in cooldown
        if last_exit_time:
            last_exit_dt = datetime.strptime(last_exit_time, '%Y-%m-%d %H:%M:%S')
            if (signal_time - last_exit_dt).total_seconds() < COOLDOWN_SEC:
                continue

        # Check existing position for exit
        if position:
            entry_time = datetime.strptime(position['entry_time'], '%Y-%m-%d %H:%M:%S')
            duration = (signal_time - entry_time).total_seconds()
            current_price = signal.price

            params = PARAMS[position['strategy']]
            TARGET_POINTS = params['target']
            STOP_POINTS = params['stop']
            MAX_HOLD_SEC = params['max_hold']

            if position['side'] == 'LONG':
                pnl_points = current_price - position['entry_price']

                # Check target
                if pnl_points >= TARGET_POINTS:
                    exit_price = position['entry_price'] + TARGET_POINTS
                    gross_pnl = TARGET_POINTS * POINT_VALUE * CONTRACTS
                    costs = (COMMISSION + SLIPPAGE_PER_CONTRACT) * CONTRACTS
                    net_pnl = gross_pnl - costs

                    trades.append(Trade(
                        position['entry_time'], signal.timestamp, 'LONG',
                        position['strategy'], position['entry_price'], exit_price,
                        gross_pnl, costs, net_pnl, int(duration), 'TARGET'
                    ))

                    print(f"  ✅ {position['strategy']} LONG {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | TARGET")

                    # Update risk tracking
                    daily_pnl += net_pnl
                    consecutive_losses = 0

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
                        position['entry_time'], signal.timestamp, 'LONG',
                        position['strategy'], position['entry_price'], exit_price,
                        gross_pnl, costs, net_pnl, int(duration), 'STOP'
                    ))

                    print(f"  ❌ {position['strategy']} LONG {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | STOP")

                    # Update risk tracking
                    daily_pnl += net_pnl
                    consecutive_losses += 1

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
                        position['entry_time'], signal.timestamp, 'LONG',
                        position['strategy'], position['entry_price'], exit_price,
                        gross_pnl, costs, net_pnl, int(duration), 'TIME'
                    ))

                    print(f"  ⏰ {position['strategy']} LONG {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | TIME")

                    # Update risk tracking
                    daily_pnl += net_pnl
                    if net_pnl <= 0:
                        consecutive_losses += 1
                    else:
                        consecutive_losses = 0

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
                        position['entry_time'], signal.timestamp, 'SHORT',
                        position['strategy'], position['entry_price'], exit_price,
                        gross_pnl, costs, net_pnl, int(duration), 'TARGET'
                    ))

                    print(f"  ✅ {position['strategy']} SHORT {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | TARGET")

                    # Update risk tracking
                    daily_pnl += net_pnl
                    consecutive_losses = 0

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
                        position['entry_time'], signal.timestamp, 'SHORT',
                        position['strategy'], position['entry_price'], exit_price,
                        gross_pnl, costs, net_pnl, int(duration), 'STOP'
                    ))

                    print(f"  ❌ {position['strategy']} SHORT {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | STOP")

                    # Update risk tracking
                    daily_pnl += net_pnl
                    consecutive_losses += 1

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
                        position['entry_time'], signal.timestamp, 'SHORT',
                        position['strategy'], position['entry_price'], exit_price,
                        gross_pnl, costs, net_pnl, int(duration), 'TIME'
                    ))

                    print(f"  ⏰ {position['strategy']} SHORT {position['entry_price']:.2f} → {exit_price:.2f} | "
                          f"${net_pnl:.2f} | {int(duration)}s | TIME")

                    # Update risk tracking
                    daily_pnl += net_pnl
                    if net_pnl <= 0:
                        consecutive_losses += 1
                    else:
                        consecutive_losses = 0

                    position = None
                    last_exit_time = signal.timestamp
                    continue

        # Enter new position if flat
        if position is None:
            position = {
                'entry_time': signal.timestamp,
                'entry_price': signal.price,
                'side': signal.side,
                'strategy': signal.strategy
            }

            params = PARAMS[signal.strategy]
            print(f"\n{'🔵' if signal.side == 'LONG' else '🔴'} {signal.strategy} {signal.side} @ {signal.price:.2f} | "
                  f"Target: {params['target']:.0f}pt | Stop: {params['stop']:.0f}pt | Strength: {signal.strength:.0f}")

    # Add risk stop info to return
    if risk_stop_triggered:
        print(f"\n⚠️  Risk management stopped trading: {risk_stop_reason}")

    return trades


def print_results(date_str: str, trades: list[Trade]):
    """Print detailed backtest results."""
    print("\n" + "="*80)
    print(f"RESULTS: {date_str}")
    print("="*80)

    if not trades:
        print("\n❌ No trades executed")
        return {
            'date': date_str,
            'trades': 0,
            'win_rate': 0,
            'net_pnl': 0,
            'avg_duration': 0
        }

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

    # Strategy breakdown
    strategies = {}
    for t in trades:
        if t.strategy not in strategies:
            strategies[t.strategy] = {'trades': 0, 'wins': 0, 'pnl': 0}
        strategies[t.strategy]['trades'] += 1
        if t.net_pnl > 0:
            strategies[t.strategy]['wins'] += 1
        strategies[t.strategy]['pnl'] += t.net_pnl

    print(f"\n   Strategy Breakdown:")
    for strat, stats in sorted(strategies.items()):
        win_rate = stats['wins'] / stats['trades'] * 100 if stats['trades'] > 0 else 0
        print(f"      {strat}: {stats['trades']} trades, {win_rate:.1f}% win, ${stats['pnl']:.2f}")

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
    print("║" + " " * 20 + "SCALPING STRATEGY BACKTEST" + " " * 32 + "║")
    print("║" + " " * 18 + "(1 Contract, $250 Weekly Limit)" + " " * 27 + "║")
    print("╚" + "=" * 78 + "╝")

    # Get all available dates
    import subprocess
    result = subprocess.run(
        ["clickhouse-client", "--query",
         "SELECT DISTINCT toDate(timestamp_1sec) as date FROM mnq_orderflow_1sec WHERE symbol = 'MNQZ5' ORDER BY date FORMAT TSV"],
        capture_output=True,
        text=True
    )

    days = [(date.strip(), "") for date in result.stdout.strip().split('\n')]

    print(f"\n📊 Testing {len(days)} days: {days[0][0]} to {days[-1][0]}")
    print(f"🛡️  Risk Management:")
    print(f"   • 1 contract only")
    print(f"   • Rolling 5-day cumulative loss limit: -$250")
    print(f"   • Hard stop: -$500 (NT8 survival)")

    all_results = []
    cumulative_pnl = 0.0
    weekly_stop_triggered = False

    for date_str, regime in days:
        # Check weekly cumulative loss (rolling 5-day window)
        if len(all_results) >= 5:
            last_5_days_pnl = sum([r['net_pnl'] for r in all_results[-5:]])
            if last_5_days_pnl <= -250.0:
                print(f"\n\n{'='*80}")
                print(f"🛑 WEEKLY CUMULATIVE LOSS LIMIT HIT!")
                print(f"   Last 5 days P&L: ${last_5_days_pnl:.2f}")
                print(f"   Skipping {date_str} (weekly stop)")
                print(f"{'='*80}")
                # Record as skipped day
                all_results.append({
                    'date': date_str,
                    'trades': 0,
                    'win_rate': 0,
                    'net_pnl': 0,
                    'avg_duration': 0,
                    'skipped': True
                })
                continue

        # Check hard limit
        if cumulative_pnl <= -500.0:
            print(f"\n\n{'='*80}")
            print(f"🚨 HARD LOSS LIMIT HIT: ${cumulative_pnl:.2f}")
            print(f"   NT8 SURVIVAL MODE - STOPPING ALL TRADING")
            print(f"{'='*80}")
            break
        print(f"\n\n{'='*80}")
        print(f"Testing {date_str}")
        print(f"{'='*80}")

        # Load all strategies
        print(f"Loading signals for {date_str}...")
        absorption = load_absorption_signals(date_str)
        iceberg = load_iceberg_signals(date_str)
        breakout = load_breakout_signals(date_str)

        print(f"  ABSORPTION: {len(absorption)} signals")
        print(f"  ICEBERG: {len(iceberg)} signals")
        print(f"  BREAKOUT: {len(breakout)} signals")

        # Combine and sort by time
        all_signals = absorption + iceberg + breakout
        all_signals.sort(key=lambda s: s.timestamp)

        print(f"  TOTAL: {len(all_signals)} signals")

        # Backtest
        trades = backtest_multi_strategy(date_str, all_signals)
        result = print_results(date_str, trades)

        # Update cumulative P&L tracking
        cumulative_pnl += result['net_pnl']
        result['cumulative_pnl'] = cumulative_pnl

        all_results.append(result)

    # Summary
    print("\n\n" + "="*80)
    print("SUMMARY: ALL DAYS (1 CONTRACT)")
    print("="*80)

    # Show daily results in compact format
    print(f"\n{'Date':<12} {'Trades':<8} {'Win%':<8} {'Net P&L':<12} {'Cumulative':<12} {'Status':<15}")
    print("-" * 95)

    for r in all_results:
        status = "🛑 SKIPPED" if r.get('skipped') else ""
        cum_pnl = r.get('cumulative_pnl', 0)
        print(f"{r['date']:<12} {r['trades']:<8} {r['win_rate']:<7.1f}% ${r['net_pnl']:<11,.2f} ${cum_pnl:<11,.2f} {status:<15}")

    # Calculate aggregate statistics
    total_trades = sum(r['trades'] for r in all_results)
    total_pnl = sum(r['net_pnl'] for r in all_results)
    num_days = len(all_results)
    winning_days = len([r for r in all_results if r['net_pnl'] > 0])
    losing_days = len([r for r in all_results if r['net_pnl'] < 0])
    breakeven_days = len([r for r in all_results if r['net_pnl'] == 0])

    print("-" * 80)
    print(f"{'TOTAL':<12} {total_trades:<8} {'':<8} ${total_pnl:,.2f}")

    print("\n" + "="*80)
    print("AGGREGATE STATISTICS (1 CONTRACT)")
    print("="*80)

    skipped_days = len([r for r in all_results if r.get('skipped')])

    print(f"\n📅 Days Tested: {num_days}")
    print(f"   Winning days: {winning_days} ({winning_days/num_days*100:.1f}%)")
    print(f"   Losing days: {losing_days} ({losing_days/num_days*100:.1f}%)")
    print(f"   Breakeven days: {breakeven_days}")
    if skipped_days > 0:
        print(f"   🛑 Skipped (weekly limit): {skipped_days}")

    print(f"\n📊 Trading Activity:")
    print(f"   Total trades: {total_trades}")
    print(f"   Avg per day: {total_trades/num_days:.1f} trades")
    print(f"   Target: 20-40 trades/day (Conservative)")

    print(f"\n💰 Profitability (1 CONTRACT):")
    print(f"   Total P&L: ${total_pnl:,.2f}")
    print(f"   Avg per day: ${total_pnl/num_days:,.2f}")
    print(f"   Per week avg: ${total_pnl/(num_days/7):,.2f}")
    print(f"   Per month projection: ${total_pnl/num_days*22:,.2f}")
    print(f"   Best day: ${max(r['net_pnl'] for r in all_results):,.2f}")
    print(f"   Worst day: ${min(r['net_pnl'] for r in all_results):,.2f}")

    print(f"\n🛡️  Risk Management:")
    print(f"   Worst day vs $500 limit: {abs(min(r['net_pnl'] for r in all_results))/500*100:.1f}%")
    print(f"   Final cumulative P&L: ${cumulative_pnl:,.2f}")
    print(f"   Distance from -$500: ${500 + cumulative_pnl:,.2f} buffer")
    print(f"   Weekly stops triggered: {skipped_days} days")

    if winning_days > 0:
        avg_win_day = statistics.mean([r['net_pnl'] for r in all_results if r['net_pnl'] > 0])
        print(f"   Avg winning day: ${avg_win_day:,.2f}")
    if losing_days > 0:
        avg_loss_day = statistics.mean([r['net_pnl'] for r in all_results if r['net_pnl'] < 0])
        print(f"   Avg losing day: ${avg_loss_day:,.2f}")

    # Calculate overall win rate
    all_trades_data = []
    for r in all_results:
        if r.get('trades', 0) > 0:
            all_trades_data.append(r)

    if all_trades_data:
        total_wins = sum([int(r['trades'] * r['win_rate'] / 100) for r in all_trades_data])
        overall_win_rate = (total_wins / total_trades * 100) if total_trades > 0 else 0
        print(f"\n   Overall win rate: {overall_win_rate:.1f}%")

    print("\n" + "="*80)
    print("✅ BACKTEST COMPLETE")
    print("="*80)
    print()


if __name__ == "__main__":
    main()
