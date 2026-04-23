#!/usr/bin/env python3
"""
Diagnose why longs performed differently in v4.1 vs v4 baseline
"""
import pandas as pd

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
                'pnl': pnl,
                'signal': entry['Name'],
                'exit_type': exit_row['Name']
            })

            exits = exits[exits.index != exit_row.name]

    return pd.DataFrame(trades)

def main():
    print("="*80)
    print("DIAGNOSTIC: Why did longs perform differently?")
    print("="*80)
    print()

    # Load both files
    baseline_file = "Instrument Action Quantity.txt"
    new_test_file = "NinjaTrader Grid 2026-04-22 08-02 PM.csv"

    # Parse baseline (filter for April 13 only)
    baseline_all = parse_nt_trades(baseline_file)
    baseline = baseline_all[baseline_all['entry_time'].dt.date == pd.to_datetime('2026-04-13').date()]

    # Parse new test
    new_test = parse_nt_trades(new_test_file)

    # Filter longs only
    baseline_longs = baseline[baseline['direction'] == 'LONG'].copy()
    new_test_longs = new_test[new_test['direction'] == 'LONG'].copy()

    print(f"BASELINE LONGS: {len(baseline_longs)} trades")
    print(f"NEW TEST LONGS: {len(new_test_longs)} trades")
    print()

    # Check if entry times overlap (same replay session?)
    baseline_times = set(baseline_longs['entry_time'].dt.floor('min'))
    new_test_times = set(new_test_longs['entry_time'].dt.floor('min'))

    overlap = baseline_times & new_test_times
    print(f"Overlapping entry times (rounded to minute): {len(overlap)}/{max(len(baseline_times), len(new_test_times))}")
    print()

    if len(overlap) < len(baseline_times) * 0.5:
        print("❌ WARNING: Less than 50% overlap in entry times!")
        print("   This suggests you're comparing DIFFERENT replay sessions")
        print("   or different market conditions, not the same data with/without Short Gate.")
        print()

    # Compare signal types
    print("SIGNAL TYPE BREAKDOWN:")
    print()
    print("Baseline Longs:")
    print(baseline_longs['signal'].value_counts())
    print()
    print("New Test Longs:")
    print(new_test_longs['signal'].value_counts())
    print()

    # Compare exit types
    print("EXIT TYPE BREAKDOWN:")
    print()
    print("Baseline Longs:")
    print(baseline_longs['exit_type'].value_counts())
    print()
    print("New Test Longs:")
    print(new_test_longs['exit_type'].value_counts())
    print()

    # Time distribution
    baseline_longs['hour'] = baseline_longs['entry_time'].dt.hour
    new_test_longs['hour'] = new_test_longs['entry_time'].dt.hour

    print("HOURLY DISTRIBUTION:")
    print()
    print("Baseline Longs by hour:")
    print(baseline_longs['hour'].value_counts().sort_index())
    print()
    print("New Test Longs by hour:")
    print(new_test_longs['hour'].value_counts().sort_index())
    print()

    # Price level analysis
    print("PRICE ANALYSIS:")
    print(f"Baseline avg entry: {baseline_longs['entry_price'].mean():.2f}")
    print(f"New test avg entry: {new_test_longs['entry_price'].mean():.2f}")
    print()

    # P&L stats
    print("P&L STATS:")
    print(f"Baseline: {baseline_longs['pnl'].sum():.2f} total, {baseline_longs['pnl'].mean():.2f} avg")
    print(f"New test: {new_test_longs['pnl'].sum():.2f} total, {new_test_longs['pnl'].mean():.2f} avg")
    print()

    print("="*80)
    print("CONCLUSION:")
    print("="*80)
    print()

    if len(overlap) < len(baseline_times) * 0.5:
        print("🔍 ROOT CAUSE: Different replay sessions")
        print()
        print("The poor long performance in the new test is likely because:")
        print("1. Different market conditions (different replay start time/data)")
        print("2. Not an apples-to-apples comparison of v4 vs v4.1")
        print()
        print("RECOMMENDATION:")
        print("To properly test Short Gate, you need to run v4.1 on the EXACT same")
        print("replay session as the baseline (start from same time, same settings).")
    else:
        print("🔍 ROOT CAUSE: Unknown - needs further investigation")
        print("Entry times overlap, suggesting same session but different results.")
        print("Check strategy parameter settings in NT8.")

if __name__ == "__main__":
    main()
