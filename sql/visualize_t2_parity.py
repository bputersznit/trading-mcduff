#!/usr/bin/env python3
"""
T2 ClanMarshal Parity Test - Visualization
===========================================

This script is ONLY for visualization after SQL analysis is complete.
All heavy lifting (signal generation, comparison) is done in SQL.

Usage:
    python3 visualize_t2_parity.py
"""

import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots


def load_data():
    """Load pre-computed results from SQL."""
    print("Loading data from CSV exports...")

    # Load generated signals
    signals = pd.read_csv('/tmp/CG_T2_signals_for_nt8_comparison.csv')
    signals['signal_time_utc'] = pd.to_datetime(signals['signal_time_utc'])

    # Load backtest trades
    trades = pd.read_csv('/tmp/CG_T2_backtest_trades_reference.csv')
    trades['entry_time_utc'] = pd.to_datetime(trades['entry_time_utc'])

    print(f"  Signals: {len(signals)}")
    print(f"  Trades: {len(trades)}")

    return signals, trades


def plot_signal_distribution(signals):
    """Plot distribution of signal features."""
    fig = make_subplots(
        rows=2, cols=2,
        subplot_titles=(
            'Total Event Size',
            'Event Count Delta',
            'Short Momentum',
            'Signals by Hour'
        )
    )

    # Total event size
    fig.add_trace(
        go.Histogram(x=signals['total_event_size'], name='Event Size', nbinsx=50),
        row=1, col=1
    )

    # Event count delta
    fig.add_trace(
        go.Histogram(x=signals['event_count_delta'], name='Delta', nbinsx=50),
        row=1, col=2
    )

    # Short momentum
    fig.add_trace(
        go.Histogram(x=signals['short_momentum'], name='Momentum', nbinsx=50),
        row=2, col=1
    )

    # Signals by hour
    signals['hour'] = signals['signal_time_utc'].dt.hour
    hourly = signals.groupby('hour').size()
    fig.add_trace(
        go.Bar(x=hourly.index, y=hourly.values, name='Count'),
        row=2, col=2
    )

    fig.update_layout(
        title='T2 Signal Feature Distributions',
        height=800,
        showlegend=False
    )

    return fig


def plot_equity_curve(trades):
    """Plot equity curve from backtest trades."""
    trades = trades.sort_values('entry_time_utc')
    trades['cumulative_pnl'] = trades['net_pnl_usd'].cumsum()

    fig = go.Figure()

    fig.add_trace(go.Scatter(
        x=trades['entry_time_utc'],
        y=trades['cumulative_pnl'],
        mode='lines',
        name='Equity Curve',
        line=dict(color='blue', width=2)
    ))

    fig.update_layout(
        title=f'T2 ClanMarshal Equity Curve ({len(trades)} trades, ${trades["cumulative_pnl"].iloc[-1]:,.2f})',
        xaxis_title='Date',
        yaxis_title='Cumulative PnL (USD)',
        hovermode='x unified'
    )

    return fig


def compare_nt8_telemetry(signals_df, nt8_csv_path=None):
    """
    Compare CH signals with NT8 telemetry (if available).

    Args:
        signals_df: DataFrame from SQL export
        nt8_csv_path: Path to NT8 telemetry CSV (optional)
    """
    if nt8_csv_path is None:
        print("\nTo compare with NT8 telemetry:")
        print("  1. Run strategy in NinjaTrader with telemetry enabled")
        print("  2. Load NT8 CSV: compare_nt8_telemetry(signals, 'path/to/nt8_telemetry.csv')")
        return

    # Load NT8 telemetry
    nt8 = pd.read_csv(nt8_csv_path)
    nt8['time'] = pd.to_datetime(nt8['time'])

    # Filter to signal rows only
    nt8_signals = nt8[nt8['record_type'] == 'SIGNAL'].copy()

    # Merge on timestamp and side
    merged = pd.merge(
        signals_df,
        nt8_signals,
        left_on=['signal_time_utc', 'side'],
        right_on=['time', 'side'],
        how='outer',
        indicator=True,
        suffixes=('_ch', '_nt8')
    )

    # Calculate match statistics
    matched = len(merged[merged['_merge'] == 'both'])
    ch_only = len(merged[merged['_merge'] == 'left_only'])
    nt8_only = len(merged[merged['_merge'] == 'right_only'])

    print(f"\n=== NT8 PARITY COMPARISON ===")
    print(f"Matched signals: {matched}")
    print(f"CH only (missing in NT8): {ch_only}")
    print(f"NT8 only (extra in NT8): {nt8_only}")
    print(f"Match rate: {matched / len(signals_df) * 100:.2f}%")

    # Show mismatches
    if ch_only > 0:
        print(f"\nFirst {min(5, ch_only)} CH-only signals:")
        print(merged[merged['_merge'] == 'left_only'][
            ['signal_time_utc', 'side', 'total_event_size', 'event_count_delta']
        ].head())

    if nt8_only > 0:
        print(f"\nFirst {min(5, nt8_only)} NT8-only signals:")
        print(merged[merged['_merge'] == 'right_only'][
            ['time', 'side', 'wall_score', 'delta_proxy']
        ].head())

    return merged


def main():
    """Run visualization."""
    print("\n=== T2 CLANMARSHAL PARITY TEST - VISUALIZATION ===\n")

    # Load data
    signals, trades = load_data()

    # Plot signal distributions
    print("\nGenerating signal distribution plots...")
    fig1 = plot_signal_distribution(signals)
    fig1.write_html('/tmp/t2_signal_distributions.html')
    print("  Saved: /tmp/t2_signal_distributions.html")

    # Plot equity curve
    print("\nGenerating equity curve...")
    fig2 = plot_equity_curve(trades)
    fig2.write_html('/tmp/t2_equity_curve.html')
    print("  Saved: /tmp/t2_equity_curve.html")

    # NT8 comparison instructions
    print("\n" + "="*60)
    compare_nt8_telemetry(signals)
    print("="*60)

    print("\nVisualization complete!")
    print("\nOpen in browser:")
    print("  file:///tmp/t2_signal_distributions.html")
    print("  file:///tmp/t2_equity_curve.html")


if __name__ == '__main__':
    main()
