#!/usr/bin/env python3
"""
Compare Latest Test Results to Baseline
April 13, 2026: Baseline vs New Test
"""

import pandas as pd
import numpy as np
from pathlib import Path

def parse_nt_trades(csv_path):
    """Parse NinjaTrader trade export CSV"""
    df = pd.read_csv(csv_path)
    df['Time'] = pd.to_datetime(df['Time'])

    # Separate entries and exits
    entries = df[df['E/X'] == 'Entry'].copy()
    exits = df[df['E/X'] == 'Exit'].copy()

    trades = []

    for _, entry in entries.iterrows():
        # Find matching exit
        if entry['Action'] == 'Buy':
            matching_exits = exits[
                (exits['Action'] == 'Sell') &
                (exits['Time'] > entry['Time'])
            ].sort_values('Time')
        else:
            matching_exits = exits[
                (exits['Action'] == 'Buy') &
                (exits['Time'] > entry['Time'])
            ].sort_values('Time')

        if len(matching_exits) > 0:
            exit_row = matching_exits.iloc[0]

            if entry['Action'] == 'Buy':
                pnl = (exit_row['Price'] - entry['Price']) * 4
                direction = 'LONG'
            else:
                pnl = (entry['Price'] - exit_row['Price']) * 4
                direction = 'SHORT'

            trades.append({
                'entry_time': entry['Time'],
                'exit_time': exit_row['Time'],
                'direction': direction,
                'entry_price': entry['Price'],
                'exit_price': exit_row['Price'],
                'exit_type': exit_row['Name'],
                'pnl': pnl,
                'signal': entry['Name'],
                'is_winner': pnl > 0
            })

            exits = exits[exits.index != exit_row.name]

    return pd.DataFrame(trades)

def analyze(df, label):
    """Calculate performance metrics"""
    if len(df) == 0:
        return {
            'label': label,
            'trades': 0,
            'longs': 0,
            'shorts': 0,
            'winners': 0,
            'losers': 0,
            'win_rate': 0,
            'total_pnl': 0,
            'avg_win': 0,
            'avg_loss': 0,
            'profit_factor': 0,
            'expectancy': 0
        }

    wins = df[df['pnl'] > 0]
    losses = df[df['pnl'] <= 0]

    longs = df[df['direction'] == 'LONG']
    shorts = df[df['direction'] == 'SHORT']

    total_pnl = df['pnl'].sum()
    win_rate = len(wins) / len(df) * 100
    avg_win = wins['pnl'].mean() if len(wins) > 0 else 0
    avg_loss = losses['pnl'].mean() if len(losses) > 0 else 0

    gross_profit = wins['pnl'].sum() if len(wins) > 0 else 0
    gross_loss = abs(losses['pnl'].sum()) if len(losses) > 0 else 0
    profit_factor = gross_profit / gross_loss if gross_loss > 0 else float('inf')

    return {
        'label': label,
        'trades': len(df),
        'longs': len(longs),
        'shorts': len(shorts),
        'winners': len(wins),
        'losers': len(losses),
        'win_rate': win_rate,
        'total_pnl': total_pnl,
        'avg_win': avg_win,
        'avg_loss': avg_loss,
        'gross_profit': gross_profit,
        'gross_loss': gross_loss,
        'profit_factor': profit_factor,
        'expectancy': df['pnl'].mean()
    }

def main():
    print("="*80)
    print("APRIL 13, 2026: BASELINE VS NEW TEST COMPARISON")
    print("="*80)
    print()

    # Load both files
    baseline_file = "Instrument Action Quantity.txt"
    new_test_file = "NinjaTrader Grid 2026-04-22 08-02 PM.csv"

    # Parse baseline (filter for April 13 only)
    print("Loading baseline data...")
    baseline_all = parse_nt_trades(baseline_file)
    baseline = baseline_all[baseline_all['entry_time'].dt.date == pd.to_datetime('2026-04-13').date()]

    print(f"  Baseline April 13: {len(baseline)} trades")

    # Parse new test
    print("Loading new test data...")
    new_test = parse_nt_trades(new_test_file)
    print(f"  New test April 13: {len(new_test)} trades")
    print()

    # Analyze both
    baseline_stats = analyze(baseline, "BASELINE (April 13)")
    new_test_stats = analyze(new_test, "NEW TEST (April 13)")

    # Display comparison
    print("-"*80)
    print("OVERALL PERFORMANCE COMPARISON")
    print("-"*80)
    print()

    print(f"{'Metric':<25} {'Baseline':<20} {'New Test':<20} {'Change':<15}")
    print("-"*80)

    metrics = [
        ('Total Trades', 'trades', ''),
        ('Longs', 'longs', ''),
        ('Shorts', 'shorts', ''),
        ('Winners', 'winners', ''),
        ('Losers', 'losers', ''),
        ('Win Rate', 'win_rate', '%'),
        ('Total P&L', 'total_pnl', '$'),
        ('Avg Win', 'avg_win', '$'),
        ('Avg Loss', 'avg_loss', '$'),
        ('Profit Factor', 'profit_factor', ''),
        ('Expectancy', 'expectancy', '$'),
    ]

    for label, key, suffix in metrics:
        base_val = baseline_stats[key]
        new_val = new_test_stats[key]

        if suffix == '$':
            base_str = f"${base_val:.2f}"
            new_str = f"${new_val:.2f}"
            change = new_val - base_val
            if change != 0:
                change_str = f"${change:+.2f}"
                if change > 0:
                    change_str += " ✅"
                else:
                    change_str += " ❌"
            else:
                change_str = "—"
        elif suffix == '%':
            base_str = f"{base_val:.1f}%"
            new_str = f"{new_val:.1f}%"
            change = new_val - base_val
            if abs(change) > 0.1:
                change_str = f"{change:+.1f}%"
                if change > 0:
                    change_str += " ✅"
                else:
                    change_str += " ❌"
            else:
                change_str = "—"
        else:
            base_str = f"{base_val:.2f}" if isinstance(base_val, float) else str(base_val)
            new_str = f"{new_val:.2f}" if isinstance(new_val, float) else str(new_val)
            change = new_val - base_val if isinstance(new_val, (int, float)) else 0
            if abs(change) > 0:
                change_str = f"{change:+.0f}"
            else:
                change_str = "—"

        print(f"{label:<25} {base_str:<20} {new_str:<20} {change_str:<15}")

    print()
    print("-"*80)
    print("DIRECTIONAL BREAKDOWN")
    print("-"*80)
    print()

    # Baseline directional
    baseline_longs = baseline[baseline['direction'] == 'LONG']
    baseline_shorts = baseline[baseline['direction'] == 'SHORT']

    baseline_long_stats = analyze(baseline_longs, "Baseline LONGS")
    baseline_short_stats = analyze(baseline_shorts, "Baseline SHORTS")

    # New test directional
    new_longs = new_test[new_test['direction'] == 'LONG']
    new_shorts = new_test[new_test['direction'] == 'SHORT']

    new_long_stats = analyze(new_longs, "New Test LONGS")
    new_short_stats = analyze(new_shorts, "New Test SHORTS")

    print("BASELINE:")
    print(f"  LONGS:  {baseline_long_stats['trades']} trades | "
          f"{baseline_long_stats['win_rate']:.1f}% win | "
          f"${baseline_long_stats['total_pnl']:.2f}")
    print(f"  SHORTS: {baseline_short_stats['trades']} trades | "
          f"{baseline_short_stats['win_rate']:.1f}% win | "
          f"${baseline_short_stats['total_pnl']:.2f}")
    print()

    print("NEW TEST:")
    print(f"  LONGS:  {new_long_stats['trades']} trades | "
          f"{new_long_stats['win_rate']:.1f}% win | "
          f"${new_long_stats['total_pnl']:.2f}")
    print(f"  SHORTS: {new_short_stats['trades']} trades | "
          f"{new_short_stats['win_rate']:.1f}% win | "
          f"${new_short_stats['total_pnl']:.2f}")
    print()

    # Key insights
    print("="*80)
    print("KEY INSIGHTS")
    print("="*80)
    print()

    shorts_filtered = baseline_short_stats['trades'] - new_short_stats['trades']

    if shorts_filtered > 0:
        print(f"✅ SHORT GATE ACTIVE: {shorts_filtered} short trades filtered")
        print(f"   Baseline had {baseline_short_stats['trades']} shorts losing "
              f"${baseline_short_stats['total_pnl']:.2f}")
        print(f"   New test has {new_short_stats['trades']} shorts")
        print()

    pnl_improvement = new_test_stats['total_pnl'] - baseline_stats['total_pnl']
    win_rate_improvement = new_test_stats['win_rate'] - baseline_stats['win_rate']

    print(f"P&L CHANGE:      ${pnl_improvement:+.2f}")
    print(f"Win Rate CHANGE: {win_rate_improvement:+.1f}%")
    print()

    if pnl_improvement > 0:
        pct_improvement = (pnl_improvement / abs(baseline_stats['total_pnl'])) * 100 if baseline_stats['total_pnl'] != 0 else 0
        print(f"✅ IMPROVED: {pct_improvement:.1f}% better P&L")
    elif pnl_improvement < 0:
        print(f"❌ WORSE: P&L declined")
    else:
        print(f"⚠️  NEUTRAL: Same P&L")

    print()
    print("="*80)

    # Save detailed comparison
    comparison = pd.DataFrame([baseline_stats, new_test_stats])
    comparison.to_csv('test_comparison_april13.csv', index=False)
    print("Detailed comparison saved to: test_comparison_april13.csv")
    print()

if __name__ == "__main__":
    main()
