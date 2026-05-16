#!/usr/bin/env python3
"""
Analyze v5 CSV and simulate what would have happened with safety checks
"""

import pandas as pd
import sys

# ================================================================
# SAFETY CONSTANTS
# ================================================================
MIN_SECONDS_BETWEEN_ENTRIES = 10

print("=" * 80)
print("V5 SAFETY FIX SIMULATION")
print("=" * 80)
print()

# ================================================================
# Load Original v5 Data
# ================================================================
csv_file = 'CG_mnq_hybrid_v5_clanmarshal_trades.csv'
print(f"Loading: {csv_file}")

try:
    df = pd.read_csv(csv_file)
    df['entry_et'] = pd.to_datetime(df['entry_et'])
    df['effective_fill_time'] = pd.to_datetime(df['effective_fill_time'])
except Exception as e:
    print(f"❌ Error loading CSV: {e}")
    sys.exit(1)

print(f"✅ Loaded {len(df)} trades from v5 original")
print()

# ================================================================
# Analyze Original Performance
# ================================================================
print("ORIGINAL V5 PERFORMANCE (WITH VIOLATIONS)")
print("-" * 80)

winners_orig = len(df[df['outcome'] == 'TARGET'])
losers_orig = len(df[df['outcome'] == 'STOP'])
wr_orig = winners_orig / len(df) * 100
total_pnl_orig = df['net_pnl_usd'].sum()

print(f"Total Trades:        {len(df)}")
print(f"Winners:             {winners_orig}")
print(f"Losers:              {losers_orig}")
print(f"Win Rate:            {wr_orig:.2f}%")
print(f"Total P&L:           ${total_pnl_orig:,.2f}")
print(f"Avg P&L:             ${total_pnl_orig / len(df):.2f}")
print()

# ================================================================
# Apply Safety Checks
# ================================================================
print("APPLYING SAFETY CHECKS...")
print("-" * 80)

# Sort by entry time to ensure chronological processing
df = df.sort_values('effective_fill_time').reset_index(drop=True)

# Track which trades to keep
keep_trades = []
last_entry_time = None
rapid_fire_blocks = 0

for idx, row in df.iterrows():
    current_time = row['effective_fill_time']

    # Check #1: Rapid-fire prevention
    if last_entry_time is not None:
        seconds_since_last = (current_time - last_entry_time).total_seconds()
        if seconds_since_last < MIN_SECONDS_BETWEEN_ENTRIES:
            rapid_fire_blocks += 1
            continue

    # Trade passes safety checks
    keep_trades.append(idx)
    last_entry_time = current_time

# ================================================================
# Create Fixed Dataset
# ================================================================
df_fixed = df.iloc[keep_trades].copy()

print(f"✅ Safety checks applied")
print(f"   Rapid-fire blocks:    {rapid_fire_blocks}")
print(f"   Trades kept:          {len(df_fixed)}")
print(f"   Trades removed:       {len(df) - len(df_fixed)} ({(len(df) - len(df_fixed)) / len(df) * 100:.1f}%)")
print()

# ================================================================
# Analyze Fixed Performance
# ================================================================
print("FIXED V5 PERFORMANCE (WITH SAFETY CHECKS)")
print("-" * 80)

if len(df_fixed) > 0:
    winners_fixed = len(df_fixed[df_fixed['outcome'] == 'TARGET'])
    losers_fixed = len(df_fixed[df_fixed['outcome'] == 'STOP'])
    wr_fixed = winners_fixed / len(df_fixed) * 100
    total_pnl_fixed = df_fixed['net_pnl_usd'].sum()

    print(f"Total Trades:        {len(df_fixed)}")
    print(f"Winners:             {winners_fixed}")
    print(f"Losers:              {losers_fixed}")
    print(f"Win Rate:            {wr_fixed:.2f}%")
    print(f"Total P&L:           ${total_pnl_fixed:,.2f}")
    print(f"Avg P&L:             ${total_pnl_fixed / len(df_fixed):.2f}")
    print()

    # ================================================================
    # Comparison
    # ================================================================
    print("COMPARISON: ORIGINAL vs FIXED")
    print("=" * 80)
    print()

    trade_delta = len(df_fixed) - len(df)
    wr_delta = wr_fixed - wr_orig
    pnl_delta = total_pnl_fixed - total_pnl_orig

    print(f"{'Metric':<25} {'Original':>15} {'Fixed':>15} {'Delta':>15}")
    print("-" * 80)
    print(f"{'Total Trades':<25} {len(df):>15,} {len(df_fixed):>15,} {trade_delta:>15,}")
    print(f"{'Win Rate':<25} {wr_orig:>14.2f}% {wr_fixed:>14.2f}% {wr_delta:>+14.2f}%")
    print(f"{'Total P&L':<25} ${total_pnl_orig:>14,.2f} ${total_pnl_fixed:>14,.2f} ${pnl_delta:>+14,.2f}")
    print(f"{'Avg P&L/Trade':<25} ${total_pnl_orig/len(df):>14,.2f} ${total_pnl_fixed/len(df_fixed):>14,.2f} ${(total_pnl_fixed/len(df_fixed) - total_pnl_orig/len(df)):>+14,.2f}")
    print()

    # Analyze what was removed
    df_removed = df[~df.index.isin(keep_trades)].copy()

    if len(df_removed) > 0:
        removed_winners = len(df_removed[df_removed['outcome'] == 'TARGET'])
        removed_losers = len(df_removed[df_removed['outcome'] == 'STOP'])
        removed_pnl = df_removed['net_pnl_usd'].sum()

        print("REMOVED TRADES ANALYSIS")
        print("-" * 80)
        print(f"Removed Trades:      {len(df_removed)}")
        print(f"  Winners:           {removed_winners}")
        print(f"  Losers:            {removed_losers}")
        print(f"  Win Rate:          {removed_winners/len(df_removed)*100:.2f}%")
        print(f"  Total P&L:         ${removed_pnl:,.2f}")
        print(f"  Avg P&L:           ${removed_pnl/len(df_removed):.2f}")
        print()

        if removed_pnl > 0:
            print("⚠️  WARNING: Removed trades were NET PROFITABLE!")
            print(f"   By enforcing safety, we gave up ${removed_pnl:,.2f}")
            print(f"   BUT we eliminated {len(df_removed)} violation risks")
        else:
            print("✅ GOOD: Removed trades were NET LOSING")
            print(f"   Safety checks improved P&L by ${-removed_pnl:,.2f}")
        print()

    # ================================================================
    # Violations Analysis
    # ================================================================
    print("SIMULTANEOUS ENTRY VIOLATIONS (ORIGINAL)")
    print("-" * 80)

    # Find all timestamps with multiple entries
    duplicate_times = df.groupby('effective_fill_time').size()
    simultaneous = duplicate_times[duplicate_times > 1]

    if len(simultaneous) > 0:
        print(f"Found {len(simultaneous)} timestamps with multiple entries:")
        print()

        total_violation_trades = 0
        for timestamp, count in simultaneous.items():
            trades_at_time = df[df['effective_fill_time'] == timestamp]
            total_violation_trades += len(trades_at_time)

            print(f"{timestamp}: {count} contracts")
            for _, trade in trades_at_time.iterrows():
                print(f"  {trade['side']:5} @ {trade['entry_price']:.2f} → {trade['outcome']:6} ${trade['net_pnl_usd']:>8.2f}")
            print()

        print(f"Total violation trades:  {total_violation_trades}")
        print(f"All eliminated in FIXED version ✅")
    else:
        print("No simultaneous entries found")

    print()

    # ================================================================
    # Save Fixed Dataset
    # ================================================================
    output_file = 'CG_mnq_hybrid_v5_FIXED_trades.csv'
    df_fixed.to_csv(output_file, index=False)
    print(f"✅ Saved fixed dataset to: {output_file}")

else:
    print("❌ No trades survived safety checks!")

print()
print("=" * 80)
print("CONCLUSION")
print("=" * 80)
print()

if len(df_fixed) > 0:
    pnl_pct_change = ((total_pnl_fixed - total_pnl_orig) / total_pnl_orig * 100) if total_pnl_orig != 0 else 0

    if total_pnl_fixed > total_pnl_orig:
        print(f"✅ SAFETY CHECKS IMPROVED PERFORMANCE")
        print(f"   P&L increased by ${total_pnl_fixed - total_pnl_orig:,.2f} ({pnl_pct_change:+.1f}%)")
        print(f"   Removed {len(df) - len(df_fixed)} low-quality trades")
    elif total_pnl_fixed < total_pnl_orig:
        print(f"⚠️  SAFETY CHECKS REDUCED P&L")
        print(f"   P&L decreased by ${total_pnl_orig - total_pnl_fixed:,.2f} ({pnl_pct_change:.1f}%)")
        print(f"   BUT eliminated {len(simultaneous)} violation events")
        print(f"   Trade-off: Lower profit for guaranteed safety")
    else:
        print(f"➡️  SAFETY CHECKS HAD NO P&L IMPACT")
        print(f"   P&L unchanged, violations eliminated")

    print()
    print("RECOMMENDATION:")
    if pnl_pct_change >= -5:  # Less than 5% loss is acceptable for safety
        print("  ✅ Deploy v1.2 SafetyPatched - safety worth the cost")
    else:
        print("  ⚠️  Significant P&L impact - review trade-offs")
else:
    print("❌ Safety checks too restrictive - all trades removed")

print()
