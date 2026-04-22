#!/usr/bin/env python3
"""
Analyze MNQ trading performance from execution data
"""

import csv
from datetime import datetime

# MNQ point value: $2 per point
POINT_VALUE = 2.0

def parse_trades(csv_file):
    """Parse CSV data and match entries with exits"""
    trades = []

    with open(csv_file, 'r') as f:
        reader = csv.DictReader(f)
        rows = list(reader)

    # Reverse to get chronological order (data is in reverse time order)
    rows.reverse()

    # Track open positions
    open_positions = []

    for row in rows:
        ex_type = row['E/X'].strip()

        if ex_type == 'Entry':
            # Store entry for later matching
            open_positions.append(row)

        elif ex_type == 'Exit':
            # Match with most recent entry
            if open_positions:
                entry = open_positions.pop()

                # Determine direction
                entry_action = entry['Action'].strip()
                quantity = int(entry['Quantity'])
                entry_price = float(entry['Price'])
                exit_price = float(row['Price'])

                # Calculate P&L
                if entry_action == 'Buy':
                    # Long trade
                    pnl = (exit_price - entry_price) * quantity * POINT_VALUE
                    direction = 'Long'
                else:
                    # Short trade
                    pnl = (entry_price - exit_price) * quantity * POINT_VALUE
                    direction = 'Short'

                # Extract strategy name
                strategy_name = entry['Name'].strip()
                if '_' in strategy_name:
                    strategy = strategy_name.split('_')[0]
                else:
                    strategy = strategy_name

                # Parse times
                entry_time = datetime.strptime(entry['Time'], '%m/%d/%Y %I:%M:%S %p')
                exit_time = datetime.strptime(row['Time'], '%m/%d/%Y %I:%M:%S %p')
                duration = (exit_time - entry_time).total_seconds() / 60  # minutes

                trades.append({
                    'strategy': strategy,
                    'direction': direction,
                    'entry_price': entry_price,
                    'exit_price': exit_price,
                    'entry_time': entry_time,
                    'exit_time': exit_time,
                    'duration_min': duration,
                    'pnl': pnl,
                    'exit_reason': row['Name'].strip()
                })

    return trades

def analyze_performance(trades):
    """Calculate performance metrics"""
    if not trades:
        print("No trades found")
        return

    # Overall metrics
    total_trades = len(trades)
    winning_trades = [t for t in trades if t['pnl'] > 0]
    losing_trades = [t for t in trades if t['pnl'] < 0]
    breakeven_trades = [t for t in trades if t['pnl'] == 0]

    num_wins = len(winning_trades)
    num_losses = len(losing_trades)
    num_breakeven = len(breakeven_trades)

    total_pnl = sum(t['pnl'] for t in trades)
    gross_profit = sum(t['pnl'] for t in winning_trades)
    gross_loss = sum(t['pnl'] for t in losing_trades)

    win_rate = (num_wins / total_trades * 100) if total_trades > 0 else 0
    avg_win = gross_profit / num_wins if num_wins > 0 else 0
    avg_loss = gross_loss / num_losses if num_losses > 0 else 0
    avg_trade = total_pnl / total_trades if total_trades > 0 else 0

    # Profit factor
    profit_factor = abs(gross_profit / gross_loss) if gross_loss != 0 else float('inf')

    # Average duration
    avg_duration = sum(t['duration_min'] for t in trades) / total_trades if total_trades > 0 else 0

    # Max consecutive wins/losses
    consecutive_wins = 0
    consecutive_losses = 0
    max_consecutive_wins = 0
    max_consecutive_losses = 0

    for trade in trades:
        if trade['pnl'] > 0:
            consecutive_wins += 1
            consecutive_losses = 0
            max_consecutive_wins = max(max_consecutive_wins, consecutive_wins)
        elif trade['pnl'] < 0:
            consecutive_losses += 1
            consecutive_wins = 0
            max_consecutive_losses = max(max_consecutive_losses, consecutive_losses)

    print("=" * 70)
    print("OVERALL PERFORMANCE SUMMARY")
    print("=" * 70)
    print(f"Total Trades:        {total_trades}")
    print(f"Winning Trades:      {num_wins} ({num_wins/total_trades*100:.1f}%)")
    print(f"Losing Trades:       {num_losses} ({num_losses/total_trades*100:.1f}%)")
    print(f"Breakeven Trades:    {num_breakeven}")
    print()
    print(f"Win Rate:            {win_rate:.2f}%")
    print(f"Total P&L:           ${total_pnl:,.2f}")
    print(f"Gross Profit:        ${gross_profit:,.2f}")
    print(f"Gross Loss:          ${gross_loss:,.2f}")
    print(f"Profit Factor:       {profit_factor:.2f}")
    print()
    print(f"Average Win:         ${avg_win:.2f}")
    print(f"Average Loss:        ${avg_loss:.2f}")
    print(f"Average Trade:       ${avg_trade:.2f}")
    print(f"Avg Win/Avg Loss:    {abs(avg_win/avg_loss) if avg_loss != 0 else 'N/A':.2f}")
    print()
    print(f"Average Duration:    {avg_duration:.1f} minutes")
    print(f"Max Consecutive Wins:  {max_consecutive_wins}")
    print(f"Max Consecutive Losses: {max_consecutive_losses}")
    print()

    # Largest win/loss
    if winning_trades:
        largest_win = max(winning_trades, key=lambda x: x['pnl'])
        print(f"Largest Win:         ${largest_win['pnl']:.2f} ({largest_win['strategy']} {largest_win['direction']})")

    if losing_trades:
        largest_loss = min(losing_trades, key=lambda x: x['pnl'])
        print(f"Largest Loss:        ${largest_loss['pnl']:.2f} ({largest_loss['strategy']} {largest_loss['direction']})")

    print()

    # Strategy breakdown
    strategies = set(t['strategy'] for t in trades)

    print("=" * 70)
    print("PERFORMANCE BY STRATEGY")
    print("=" * 70)

    for strategy in sorted(strategies):
        strategy_trades = [t for t in trades if t['strategy'] == strategy]
        s_total = len(strategy_trades)
        s_wins = len([t for t in strategy_trades if t['pnl'] > 0])
        s_losses = len([t for t in strategy_trades if t['pnl'] < 0])
        s_pnl = sum(t['pnl'] for t in strategy_trades)
        s_win_rate = (s_wins / s_total * 100) if s_total > 0 else 0
        s_avg_pnl = s_pnl / s_total if s_total > 0 else 0

        print(f"\n{strategy}:")
        print(f"  Trades: {s_total} | Wins: {s_wins} | Losses: {s_losses} | Win Rate: {s_win_rate:.1f}%")
        print(f"  Total P&L: ${s_pnl:,.2f} | Avg P&L: ${s_avg_pnl:.2f}")

    print()

    # Direction breakdown
    print("=" * 70)
    print("PERFORMANCE BY DIRECTION")
    print("=" * 70)

    for direction in ['Long', 'Short']:
        dir_trades = [t for t in trades if t['direction'] == direction]
        if not dir_trades:
            continue

        d_total = len(dir_trades)
        d_wins = len([t for t in dir_trades if t['pnl'] > 0])
        d_losses = len([t for t in dir_trades if t['pnl'] < 0])
        d_pnl = sum(t['pnl'] for t in dir_trades)
        d_win_rate = (d_wins / d_total * 100) if d_total > 0 else 0
        d_avg_pnl = d_pnl / d_total if d_total > 0 else 0

        print(f"\n{direction}:")
        print(f"  Trades: {d_total} | Wins: {d_wins} | Losses: {d_losses} | Win Rate: {d_win_rate:.1f}%")
        print(f"  Total P&L: ${d_pnl:,.2f} | Avg P&L: ${d_avg_pnl:.2f}")

    print()

    # Exit reason analysis
    print("=" * 70)
    print("EXIT REASONS")
    print("=" * 70)

    exit_reasons = {}
    for trade in trades:
        reason = trade['exit_reason']
        if reason not in exit_reasons:
            exit_reasons[reason] = {'count': 0, 'pnl': 0}
        exit_reasons[reason]['count'] += 1
        exit_reasons[reason]['pnl'] += trade['pnl']

    for reason, stats in sorted(exit_reasons.items(), key=lambda x: x[1]['count'], reverse=True):
        count = stats['count']
        pnl = stats['pnl']
        pct = (count / total_trades * 100) if total_trades > 0 else 0
        print(f"\n{reason}:")
        print(f"  Count: {count} ({pct:.1f}%) | Total P&L: ${pnl:,.2f}")

    print()
    print("=" * 70)

if __name__ == '__main__':
    import sys
    if len(sys.argv) > 1:
        csv_file = sys.argv[1]
    else:
        csv_file = '/home/bernard/trading4/CG_MNQ_MarketReplayLab/NinjaTrader Grid 2026-04-22 03-36 AM.csv'

    trades = parse_trades(csv_file)
    analyze_performance(trades)
