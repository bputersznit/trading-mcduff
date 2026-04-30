#!/usr/bin/env python3
"""
T2 ClanMarshal Statistical Parity Comparison
============================================

Compares statistical distributions between:
- Baseline: ClickHouse October 2025 (validated backtest)
- Test: NinjaTrader Playback March-April 2026 (your test runs)

Method: Distribution parity, not exact signal matching
"""

import pandas as pd
import numpy as np
from pathlib import Path
from scipy import stats
import plotly.graph_objects as go
from plotly.subplots import make_subplots


def load_ch_baseline():
    """Load ClickHouse Oct 2025 baseline."""
    print("Loading CH baseline (Oct 2025)...")

    baseline_path = '/tmp/ch_baseline_oct2025.csv'
    if not Path(baseline_path).exists():
        print(f"ERROR: Baseline not found. Run this first:")
        print("  clickhouse-client --multiquery < sql/CG_T2_Statistical_Baseline.sql")
        return None

    df = pd.read_csv(baseline_path)
    df['entry_time_utc'] = pd.to_datetime(df['entry_time_utc'])

    print(f"  Loaded: {len(df)} trades")
    print(f"  Date range: {df['trade_date'].min()} to {df['trade_date'].max()}")

    return df


def load_nt8_telemetry(telemetry_dir='~/Documents/NinjaTrader 8/trace'):
    """
    Load NT8 telemetry CSV files from March-April 2026.

    Args:
        telemetry_dir: Directory containing CG_T2_ClanMarshal_v1_1_*.csv files
    """
    print("\nLoading NT8 telemetry (Mar-Apr 2026)...")

    telemetry_path = Path(telemetry_dir).expanduser()
    if not telemetry_path.exists():
        print(f"ERROR: Telemetry directory not found: {telemetry_path}")
        print("\nPlease specify correct path:")
        print("  nt8 = load_nt8_telemetry('/path/to/telemetry/files')")
        return None

    # Find all telemetry files
    csv_files = list(telemetry_path.glob('CG_T2_ClanMarshal_*.csv'))

    if not csv_files:
        print(f"ERROR: No telemetry files found in {telemetry_path}")
        print("\nExpected files like: CG_T2_ClanMarshal_v1_1_20260301_*.csv")
        return None

    print(f"  Found {len(csv_files)} telemetry files")

    # Load and combine all files
    dfs = []
    for csv_file in csv_files:
        try:
            df = pd.read_csv(csv_file)
            df['source_file'] = csv_file.name
            dfs.append(df)
        except Exception as e:
            print(f"  Warning: Could not read {csv_file.name}: {e}")

    if not dfs:
        print("ERROR: No valid telemetry files loaded")
        return None

    combined = pd.concat(dfs, ignore_index=True)
    combined['time'] = pd.to_datetime(combined['time'])

    # Filter to EXIT records only (completed trades)
    trades = combined[combined['record_type'] == 'EXIT'].copy()

    print(f"  Loaded: {len(trades)} completed trades from {len(csv_files)} files")
    print(f"  Date range: {trades['time'].min()} to {trades['time'].max()}")

    return trades


def calculate_statistics(df, name):
    """Calculate comprehensive statistics for a dataset."""

    stats_dict = {
        'name': name,
        'total_trades': len(df),
        'total_pnl': df['pnl_usd'].sum() if 'pnl_usd' in df.columns else df['net_pnl_usd'].sum(),
        'win_rate': (df['pnl_usd'] > 0).mean() * 100 if 'pnl_usd' in df.columns else (df['net_pnl_usd'] > 0).mean() * 100,
        'avg_winner': df[df['pnl_usd' if 'pnl_usd' in df.columns else 'net_pnl_usd'] > 0]['pnl_usd' if 'pnl_usd' in df.columns else 'net_pnl_usd'].mean(),
        'avg_loser': df[df['pnl_usd' if 'pnl_usd' in df.columns else 'net_pnl_usd'] < 0]['pnl_usd' if 'pnl_usd' in df.columns else 'net_pnl_usd'].mean(),
    }

    # Profit factor
    winners = df[df['pnl_usd' if 'pnl_usd' in df.columns else 'net_pnl_usd'] > 0]['pnl_usd' if 'pnl_usd' in df.columns else 'net_pnl_usd'].sum()
    losers = -df[df['pnl_usd' if 'pnl_usd' in df.columns else 'net_pnl_usd'] < 0]['pnl_usd' if 'pnl_usd' in df.columns else 'net_pnl_usd'].sum()
    stats_dict['profit_factor'] = winners / losers if losers > 0 else np.inf

    # Daily statistics
    if 'trade_date' in df.columns:
        date_col = 'trade_date'
    else:
        date_col = df['time'].dt.date

    daily = df.groupby(date_col).size()
    stats_dict['trades_per_day_mean'] = daily.mean()
    stats_dict['trades_per_day_median'] = daily.median()
    stats_dict['trades_per_day_std'] = daily.std()

    # Side distribution
    stats_dict['long_pct'] = (df['side'] == 'LONG').mean() * 100
    stats_dict['short_pct'] = (df['side'] == 'SHORT').mean() * 100

    return stats_dict


def compare_distributions(ch_df, nt8_df):
    """Compare statistical distributions between CH and NT8."""

    print("\n" + "="*70)
    print("STATISTICAL PARITY COMPARISON")
    print("="*70)

    ch_stats = calculate_statistics(ch_df, "CH Oct 2025")
    nt8_stats = calculate_statistics(nt8_df, "NT8 Mar-Apr 2026")

    # Print comparison table
    print(f"\n{'Metric':<30} {'CH Baseline':<20} {'NT8 Test':<20} {'Delta':<15}")
    print("-" * 85)

    metrics = [
        ('Total Trades', 'total_trades', '{:.0f}'),
        ('Total PnL', 'total_pnl', '${:,.2f}'),
        ('Win Rate', 'win_rate', '{:.1f}%'),
        ('Profit Factor', 'profit_factor', '{:.2f}'),
        ('Avg Winner', 'avg_winner', '${:.2f}'),
        ('Avg Loser', 'avg_loser', '${:.2f}'),
        ('Trades/Day (mean)', 'trades_per_day_mean', '{:.1f}'),
        ('Trades/Day (median)', 'trades_per_day_median', '{:.1f}'),
        ('Long %', 'long_pct', '{:.1f}%'),
        ('Short %', 'short_pct', '{:.1f}%'),
    ]

    for label, key, fmt in metrics:
        ch_val = ch_stats[key]
        nt8_val = nt8_stats[key]

        # Calculate delta
        if 'pct' in key.lower() or key == 'win_rate':
            delta = nt8_val - ch_val
            delta_str = f"{delta:+.1f}pp"
        elif key in ['profit_factor']:
            delta = ((nt8_val / ch_val) - 1) * 100
            delta_str = f"{delta:+.1f}%"
        elif 'total' in key or 'avg' in key:
            if ch_val != 0:
                delta = ((nt8_val / ch_val) - 1) * 100
                delta_str = f"{delta:+.1f}%"
            else:
                delta_str = "N/A"
        else:
            delta = nt8_val - ch_val
            delta_str = f"{delta:+.1f}"

        print(f"{label:<30} {fmt.format(ch_val):<20} {fmt.format(nt8_val):<20} {delta_str:<15}")

    # Statistical tests
    print("\n" + "="*70)
    print("STATISTICAL TESTS")
    print("="*70)

    # Chi-square test for side distribution
    ch_long = (ch_df['side'] == 'LONG').sum()
    ch_short = (ch_df['side'] == 'SHORT').sum()
    nt8_long = (nt8_df['side'] == 'LONG').sum()
    nt8_short = (nt8_df['side'] == 'SHORT').sum()

    chi2, p_value = stats.chi2_contingency([[ch_long, ch_short], [nt8_long, nt8_short]])[:2]
    print(f"\nSide Distribution (Chi-Square Test):")
    print(f"  CH:  {ch_long} LONG ({ch_long/(ch_long+ch_short)*100:.1f}%), {ch_short} SHORT ({ch_short/(ch_long+ch_short)*100:.1f}%)")
    print(f"  NT8: {nt8_long} LONG ({nt8_long/(nt8_long+nt8_short)*100:.1f}%), {nt8_short} SHORT ({nt8_short/(nt8_long+nt8_short)*100:.1f}%)")
    print(f"  p-value: {p_value:.4f} {'✅ Similar' if p_value > 0.05 else '❌ Different'}")

    # Kolmogorov-Smirnov test for PnL distribution
    ch_pnl = ch_df['net_pnl_usd'].values
    nt8_pnl = nt8_df['pnl_usd'].values if 'pnl_usd' in nt8_df.columns else nt8_df['net_pnl_usd'].values

    ks_stat, ks_pvalue = stats.ks_2samp(ch_pnl, nt8_pnl)
    print(f"\nPnL Distribution (Kolmogorov-Smirnov Test):")
    print(f"  KS statistic: {ks_stat:.4f}")
    print(f"  p-value: {ks_pvalue:.4f} {'✅ Similar' if ks_pvalue > 0.05 else '❌ Different'}")

    # T-test for mean PnL
    t_stat, t_pvalue = stats.ttest_ind(ch_pnl, nt8_pnl)
    print(f"\nMean PnL (T-Test):")
    print(f"  CH mean: ${ch_pnl.mean():.2f}")
    print(f"  NT8 mean: ${nt8_pnl.mean():.2f}")
    print(f"  p-value: {t_pvalue:.4f} {'✅ Similar' if t_pvalue > 0.05 else '❌ Different'}")

    # Overall parity score
    print("\n" + "="*70)
    print("PARITY ASSESSMENT")
    print("="*70)

    score = 0
    max_score = 0

    # Win rate similarity (within 5%)
    max_score += 20
    win_rate_diff = abs(ch_stats['win_rate'] - nt8_stats['win_rate'])
    if win_rate_diff < 2:
        score += 20
    elif win_rate_diff < 5:
        score += 15
    elif win_rate_diff < 10:
        score += 10
    print(f"Win Rate Similarity: {win_rate_diff:.1f}pp difference {'✅' if win_rate_diff < 5 else '⚠️' if win_rate_diff < 10 else '❌'}")

    # Profit factor similarity (within 20%)
    max_score += 20
    pf_ratio = min(ch_stats['profit_factor'], nt8_stats['profit_factor']) / max(ch_stats['profit_factor'], nt8_stats['profit_factor'])
    if pf_ratio > 0.9:
        score += 20
    elif pf_ratio > 0.8:
        score += 15
    elif pf_ratio > 0.7:
        score += 10
    print(f"Profit Factor Similarity: {pf_ratio*100:.1f}% match {'✅' if pf_ratio > 0.8 else '⚠️' if pf_ratio > 0.7 else '❌'}")

    # Side distribution (p > 0.05)
    max_score += 15
    if p_value > 0.05:
        score += 15
    elif p_value > 0.01:
        score += 10
    print(f"Side Distribution Match: {'✅' if p_value > 0.05 else '⚠️' if p_value > 0.01 else '❌'}")

    # PnL distribution (KS p > 0.05)
    max_score += 15
    if ks_pvalue > 0.05:
        score += 15
    elif ks_pvalue > 0.01:
        score += 10
    print(f"PnL Distribution Match: {'✅' if ks_pvalue > 0.05 else '⚠️' if ks_pvalue > 0.01 else '❌'}")

    # Trade frequency (within 30%)
    max_score += 15
    freq_ratio = min(ch_stats['trades_per_day_mean'], nt8_stats['trades_per_day_mean']) / max(ch_stats['trades_per_day_mean'], nt8_stats['trades_per_day_mean'])
    if freq_ratio > 0.8:
        score += 15
    elif freq_ratio > 0.7:
        score += 10
    elif freq_ratio > 0.6:
        score += 5
    print(f"Trade Frequency Match: {freq_ratio*100:.1f}% {'✅' if freq_ratio > 0.7 else '⚠️' if freq_ratio > 0.6 else '❌'}")

    # Expectancy similarity (within 20%)
    max_score += 15
    ch_exp = ch_pnl.mean()
    nt8_exp = nt8_pnl.mean()
    exp_ratio = min(abs(ch_exp), abs(nt8_exp)) / max(abs(ch_exp), abs(nt8_exp)) if max(abs(ch_exp), abs(nt8_exp)) > 0 else 0
    if exp_ratio > 0.8:
        score += 15
    elif exp_ratio > 0.7:
        score += 10
    print(f"Expectancy Match: {exp_ratio*100:.1f}% {'✅' if exp_ratio > 0.8 else '⚠️' if exp_ratio > 0.7 else '❌'}")

    # Final score
    pct_score = (score / max_score) * 100
    print(f"\n{'='*70}")
    print(f"OVERALL PARITY SCORE: {score}/{max_score} ({pct_score:.0f}%)")

    if pct_score >= 85:
        grade = "✅ EXCELLENT - NT8 is statistically equivalent to CH baseline"
    elif pct_score >= 70:
        grade = "✅ GOOD - NT8 shows strong parity with CH baseline"
    elif pct_score >= 60:
        grade = "⚠️  ACCEPTABLE - NT8 has moderate parity, investigate differences"
    else:
        grade = "❌ POOR - NT8 does not match CH baseline, DO NOT TRADE LIVE"

    print(f"{grade}")
    print(f"{'='*70}\n")

    return ch_stats, nt8_stats


def plot_distributions(ch_df, nt8_df):
    """Plot comparative distributions."""

    fig = make_subplots(
        rows=2, cols=2,
        subplot_titles=(
            'PnL Distribution',
            'Cumulative PnL',
            'Hourly Trade Distribution',
            'Win/Loss Distribution'
        )
    )

    # PnL histogram
    fig.add_trace(
        go.Histogram(x=ch_df['net_pnl_usd'], name='CH Oct 2025', opacity=0.6, nbinsx=50),
        row=1, col=1
    )
    nt8_pnl_col = 'pnl_usd' if 'pnl_usd' in nt8_df.columns else 'net_pnl_usd'
    fig.add_trace(
        go.Histogram(x=nt8_df[nt8_pnl_col], name='NT8 Mar-Apr 2026', opacity=0.6, nbinsx=50),
        row=1, col=1
    )

    # Cumulative PnL
    ch_sorted = ch_df.sort_values('entry_time_utc')
    nt8_sorted = nt8_df.sort_values('time')

    fig.add_trace(
        go.Scatter(x=list(range(len(ch_sorted))), y=ch_sorted['net_pnl_usd'].cumsum(),
                   name='CH', mode='lines'),
        row=1, col=2
    )
    fig.add_trace(
        go.Scatter(x=list(range(len(nt8_sorted))), y=nt8_sorted[nt8_pnl_col].cumsum(),
                   name='NT8', mode='lines'),
        row=1, col=2
    )

    # Hourly distribution
    ch_hourly = ch_df['hour_et'].value_counts().sort_index()
    nt8_df['hour'] = pd.to_datetime(nt8_df['time']).dt.hour
    nt8_hourly = nt8_df['hour'].value_counts().sort_index()

    fig.add_trace(
        go.Bar(x=ch_hourly.index, y=ch_hourly.values, name='CH', opacity=0.6),
        row=2, col=1
    )
    fig.add_trace(
        go.Bar(x=nt8_hourly.index, y=nt8_hourly.values, name='NT8', opacity=0.6),
        row=2, col=1
    )

    # Win/Loss counts
    ch_wins = (ch_df['net_pnl_usd'] > 0).sum()
    ch_losses = (ch_df['net_pnl_usd'] < 0).sum()
    nt8_wins = (nt8_df[nt8_pnl_col] > 0).sum()
    nt8_losses = (nt8_df[nt8_pnl_col] < 0).sum()

    fig.add_trace(
        go.Bar(x=['Wins', 'Losses'], y=[ch_wins, ch_losses], name='CH', opacity=0.6),
        row=2, col=2
    )
    fig.add_trace(
        go.Bar(x=['Wins', 'Losses'], y=[nt8_wins, nt8_losses], name='NT8', opacity=0.6),
        row=2, col=2
    )

    fig.update_layout(
        title='T2 ClanMarshal: CH Baseline vs NT8 Playback',
        height=800,
        showlegend=True
    )

    return fig


def main():
    """Run statistical parity comparison."""

    print("\n" + "="*70)
    print("T2 CLANMARSHAL STATISTICAL PARITY TEST")
    print("="*70)
    print("\nComparing:")
    print("  Baseline: ClickHouse October 2025")
    print("  Test:     NinjaTrader Playback March-April 2026")
    print()

    # Load data
    ch_df = load_ch_baseline()
    if ch_df is None:
        return

    nt8_df = load_nt8_telemetry()
    if nt8_df is None:
        print("\nTo load NT8 data, specify the telemetry directory:")
        print("  nt8_df = load_nt8_telemetry('/path/to/NinjaTrader 8/trace')")
        return

    # Compare
    ch_stats, nt8_stats = compare_distributions(ch_df, nt8_df)

    # Plot
    print("\nGenerating comparison plots...")
    fig = plot_distributions(ch_df, nt8_df)
    fig.write_html('/tmp/t2_statistical_parity.html')
    print("  Saved: /tmp/t2_statistical_parity.html")

    print("\nComparison complete!")
    print("\nOpen in browser:")
    print("  file:///tmp/t2_statistical_parity.html")


if __name__ == '__main__':
    main()
