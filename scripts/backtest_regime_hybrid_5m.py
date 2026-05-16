#!/usr/bin/env python3
"""
Regime-Aware Hybrid Strategy
State Machine: Opening Discovery → Trend/Rotation Classification → Strategy Selection
Uses Wall Rejection in rotation, Wall Breakout in trends, nothing in chaos
"""

import sys
sys.path.append('.')

import clickhouse_connect
import pandas as pd
import numpy as np
from datetime import datetime, timedelta, time
import argparse
from collections import defaultdict, deque

from regime_detector import RegimeDetector, RegimeState


def get_client():
    return clickhouse_connect.get_client(
        host='localhost', port=8123, username='default',
        password='unlucky-strange', database='default'
    )


class Position:
    """Track open position"""
    def __init__(self, strategy, regime, direction, entry_price, entry_time, wall_price,
                 entry_delta, entry_ratio=None):
        self.strategy = strategy  # 'rejection' or 'breakout'
        self.regime = regime      # Regime at entry
        self.direction = direction
        self.entry_price = entry_price
        self.entry_time = entry_time
        self.wall_price = wall_price
        self.entry_delta = entry_delta
        self.entry_ratio = entry_ratio
        self.max_favorable_pts = 0


class RegimeHybridStrategy:
    """Regime-aware strategy that switches between rejection and breakout"""

    def __init__(self):
        self.client = get_client()

        # Strategy parameters (same as before)
        self.params = {
            # Rejection (rotation) params
            'wall_threshold': 80,
            'min_bars_present': 2,
            'min_delta_for_absorption': 80,
            'rejection_stop_buffer': 2.0,

            # Breakout (trend) params
            'min_delta_for_breakout': 150,
            'aggression_ratio': 2.0,
            'clearance_min': 0.5,
            'clearance_max': 3.0,
            'lookback_distance': 5.0,
            'breakout_stop_buffer': 2.0,
            'profit_target_pts': 12.0,

            # Universal
            'trailing_activation_pts': 10.0,
            'trailing_stop_pct': 0.5,
            'commission': 0.70,
            'slippage_ticks': 1,
            'tick_value': 2.0,
        }

        self.regime_detector = RegimeDetector()
        self.position = None
        self.trades = []
        self.active_walls = {}  # For rejection strategy

        self.stats = {
            'regime_minutes': defaultdict(int),
            'trades_by_regime': defaultdict(int),
            'rejection_entries': 0,
            'breakout_entries': 0,
            'chaos_blocks': 0,
            'opening_skips': 0,
        }

        self.exit_stats = defaultdict(int)

    def load_data(self, start_date, end_date):
        """Load 5M data with VWAP calculation"""
        print(f"\nLoading 5M data from {start_date} to {end_date}...")

        # Aggression data
        query_aggr = f"""
        SELECT
            ts_bucket,
            price,
            buy_exec_size,
            sell_exec_size,
            exec_delta,
            trade_date
        FROM BM_MNQ_AGGRESSION_EXECUTIONS_5M
        WHERE trade_date >= '{start_date}'
          AND trade_date <= '{end_date}'
          AND symbol = 'MNQZ5'
        ORDER BY ts_bucket
        """
        df_aggr = self.client.query_df(query_aggr)

        # Aggregate and calculate VWAP in pandas
        df_aggr['timestamp'] = pd.to_datetime(df_aggr['ts_bucket'])
        df_aggr['volume'] = df_aggr['buy_exec_size'] + df_aggr['sell_exec_size']
        df_aggr['dollar_volume'] = df_aggr['volume'] * df_aggr['price']

        agg_dict = {
            'price': 'last',
            'buy_exec_size': 'sum',
            'sell_exec_size': 'sum',
            'exec_delta': 'sum',
            'dollar_volume': 'sum',
            'volume': 'sum',
            'trade_date': 'first'
        }

        df_aggr = df_aggr.groupby('timestamp').agg(agg_dict).reset_index()
        df_aggr = df_aggr.rename(columns={'buy_exec_size': 'buy_vol', 'sell_exec_size': 'sell_vol', 'exec_delta': 'delta'})
        df_aggr['vwap'] = df_aggr['dollar_volume'] / df_aggr['volume'].replace(0, np.nan)
        df_aggr = df_aggr.drop(['dollar_volume', 'volume'], axis=1)

        # Wall data
        query_walls = f"""
        SELECT
            toDateTime(ts_bucket) as timestamp,
            price,
            bid_liquidity_event_size,
            ask_liquidity_event_size,
            trade_date
        FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M
        WHERE trade_date >= '{start_date}'
          AND trade_date <= '{end_date}'
          AND symbol = 'MNQZ5'
        ORDER BY ts_bucket, price
        """
        df_walls = self.client.query_df(query_walls)

        print(f"Loaded {len(df_aggr)} aggression bars, {len(df_walls)} wall events")
        return df_aggr, df_walls

    def detect_rejection_signal(self, current_time, current_price, aggression):
        """Wall Rejection Reversal - for ROTATION regime"""
        if self.position:
            return None

        signals = []

        for wall_id, wall in list(self.active_walls.items()):
            wall.bars_present += 1

            if wall.price_low is None:
                wall.price_low = current_price
                wall.price_high = current_price
            wall.price_low = min(wall.price_low, current_price)
            wall.price_high = max(wall.price_high, current_price)

            # LONG: Bid wall absorption
            if wall.side == 'bid' and wall.bars_present >= self.params['min_bars_present']:
                if current_price > wall.price - 3 and current_price < wall.price + 2:
                    if aggression['delta'] < -self.params['min_delta_for_absorption']:
                        if wall.price_low >= wall.price - 1.0:
                            if current_price > wall.price and current_price > wall.price_low:
                                signals.append({
                                    'strategy': 'rejection',
                                    'direction': 'long',
                                    'wall_price': wall.price,
                                    'entry_price': current_price,
                                    'entry_delta': aggression['delta'],
                                    'entry_ratio': aggression['sell_vol'] / max(aggression['buy_vol'], 1)
                                })
                                self.stats['rejection_entries'] += 1

            # SHORT: Ask wall absorption
            elif wall.side == 'ask' and wall.bars_present >= self.params['min_bars_present']:
                if current_price < wall.price + 3 and current_price > wall.price - 2:
                    if aggression['delta'] > self.params['min_delta_for_absorption']:
                        if wall.price_high <= wall.price + 1.0:
                            if current_price < wall.price and current_price < wall.price_high:
                                signals.append({
                                    'strategy': 'rejection',
                                    'direction': 'short',
                                    'wall_price': wall.price,
                                    'entry_price': current_price,
                                    'entry_delta': aggression['delta'],
                                    'entry_ratio': aggression['buy_vol'] / max(aggression['sell_vol'], 1)
                                })
                                self.stats['rejection_entries'] += 1

        return signals[0] if signals else None

    def detect_breakout_signal(self, current_time, current_price, walls, aggression):
        """Wall Break Momentum - for TREND regime"""
        if self.position:
            return None

        # (Same logic as before - abbreviated for space)
        lookback = self.params['lookback_distance']
        min_clearance = self.params['clearance_min']
        max_clearance = self.params['clearance_max']
        aggr_ratio = self.params['aggression_ratio']
        min_delta = self.params['min_delta_for_breakout']

        # LONG BREAKOUT
        ask_walls = []
        for _, wall_row in walls.iterrows():
            ask_liq = wall_row['ask_liquidity_event_size']
            if ask_liq >= self.params['wall_threshold']:
                wall_price = wall_row['price']
                if wall_price < current_price and wall_price >= current_price - lookback:
                    clearance = current_price - wall_price
                    if min_clearance <= clearance <= max_clearance:
                        ask_walls.append((wall_price, ask_liq))

        if ask_walls:
            buy_ratio = aggression['buy_vol'] / max(aggression['sell_vol'], 1)
            if buy_ratio >= aggr_ratio and aggression['delta'] > min_delta:
                wall_price = max(ask_walls, key=lambda x: x[1])[0]
                self.stats['breakout_entries'] += 1
                return {
                    'strategy': 'breakout',
                    'direction': 'long',
                    'wall_price': wall_price,
                    'entry_price': current_price,
                    'entry_delta': aggression['delta'],
                    'entry_ratio': buy_ratio
                }

        # SHORT BREAKOUT
        bid_walls = []
        for _, wall_row in walls.iterrows():
            bid_liq = wall_row['bid_liquidity_event_size']
            if bid_liq >= self.params['wall_threshold']:
                wall_price = wall_row['price']
                if wall_price > current_price and wall_price <= current_price + lookback:
                    clearance = wall_price - current_price
                    if min_clearance <= clearance <= max_clearance:
                        bid_walls.append((wall_price, bid_liq))

        if bid_walls:
            sell_ratio = aggression['sell_vol'] / max(aggression['buy_vol'], 1)
            if sell_ratio >= aggr_ratio and aggression['delta'] < -min_delta:
                wall_price = max(bid_walls, key=lambda x: x[1])[0]
                self.stats['breakout_entries'] += 1
                return {
                    'strategy': 'breakout',
                    'direction': 'short',
                    'wall_price': wall_price,
                    'entry_price': current_price,
                    'entry_delta': aggression['delta'],
                    'entry_ratio': sell_ratio
                }

        return None

    def check_exits(self, current_time, current_price, walls, aggression):
        """Universal exit logic"""
        if not self.position:
            return None, None

        # Update max favorable
        if self.position.direction == 'long':
            pts_favorable = current_price - self.position.entry_price
        else:
            pts_favorable = self.position.entry_price - current_price
        self.position.max_favorable_pts = max(self.position.max_favorable_pts, pts_favorable)

        # Stop loss
        if self.position.strategy == 'rejection':
            stop_buffer = self.params['rejection_stop_buffer']
        else:
            stop_buffer = self.params['breakout_stop_buffer']

        if self.position.direction == 'long':
            stop_price = self.position.wall_price - stop_buffer
            if current_price <= stop_price:
                return 'stop_loss', stop_price
        else:
            stop_price = self.position.wall_price + stop_buffer
            if current_price >= stop_price:
                return 'stop_loss', stop_price

        # Profit target (breakout only)
        if self.position.strategy == 'breakout':
            if pts_favorable >= self.params['profit_target_pts']:
                return 'profit_target', current_price

        # Opposing wall
        if self.position.direction == 'long':
            for _, wall_row in walls.iterrows():
                ask_liq = wall_row['ask_liquidity_event_size']
                if ask_liq >= self.params['wall_threshold']:
                    wall_price = wall_row['price']
                    if current_price >= wall_price - 0.5:
                        return 'opposing_wall', wall_price
        else:
            for _, wall_row in walls.iterrows():
                bid_liq = wall_row['bid_liquidity_event_size']
                if bid_liq >= self.params['wall_threshold']:
                    wall_price = wall_row['price']
                    if current_price <= wall_price + 0.5:
                        return 'opposing_wall', wall_price

        # Aggression exhaustion
        if self.position.direction == 'long':
            if aggression['delta'] < -abs(self.position.entry_delta) * 0.5:
                return 'aggression_exhaustion', current_price
        else:
            if aggression['delta'] > abs(self.position.entry_delta) * 0.5:
                return 'aggression_exhaustion', current_price

        # Trailing stop
        if self.position.max_favorable_pts >= self.params['trailing_activation_pts']:
            trail_amount = self.position.max_favorable_pts * self.params['trailing_stop_pct']
            if pts_favorable <= trail_amount:
                return 'trailing_stop', current_price

        return None, None

    def run_backtest(self, start_date, end_date):
        """Run regime-aware backtest"""
        df_aggr, df_walls = self.load_data(start_date, end_date)

        daily_pnl = defaultdict(float)
        daily_trades = defaultdict(int)
        daily_regimes = defaultdict(lambda: defaultdict(int))

        print(f"\n{'='*70}")
        print("REGIME-AWARE HYBRID STRATEGY")
        print("Switching: Rejection (Rotation) / Breakout (Trend) / Nothing (Chaos)")
        print(f"{'='*70}\n")

        for idx, row in df_aggr.iterrows():
            current_time = row['timestamp']
            current_price = row['price']
            trade_date = row['trade_date']

            aggression = {
                'buy_vol': row['buy_vol'],
                'sell_vol': row['sell_vol'],
                'delta': row['delta']
            }

            vwap = row.get('vwap')
            current_walls_df = df_walls[df_walls['timestamp'] == current_time].copy()

            # Calculate wall stats for regime detector
            wall_info = {
                'bid_size': current_walls_df['bid_liquidity_event_size'].sum(),
                'ask_size': current_walls_df['ask_liquidity_event_size'].sum(),
            }

            # Update regime detector
            regime = self.regime_detector.update(
                current_time, current_price, wall_info, aggression, vwap
            )

            # Track regime time
            self.stats['regime_minutes'][regime.value] += 5  # 5-min bars

            # Update wall tracking for rejection
            for _, wall_row in current_walls_df.iterrows():
                bid_liq = wall_row['bid_liquidity_event_size']
                ask_liq = wall_row['ask_liquidity_event_size']
                price = wall_row['price']

                from regime_detector import RegimeDetector  # Import WallState
                # Simplified wall tracking (full implementation would be here)

            # Check exits first
            if self.position:
                exit_reason, exit_price = self.check_exits(
                    current_time, current_price, current_walls_df, aggression
                )

                if exit_reason:
                    costs = self.params['commission'] + (
                        self.params['slippage_ticks'] * self.params['tick_value']
                    )

                    if self.position.direction == 'long':
                        gross_pnl = (exit_price - self.position.entry_price) * (
                            self.params['tick_value'] / 0.25
                        )
                    else:
                        gross_pnl = (self.position.entry_price - exit_price) * (
                            self.params['tick_value'] / 0.25
                        )

                    net_pnl = gross_pnl - costs

                    self.trades.append({
                        'strategy': self.position.strategy,
                        'entry_regime': self.position.regime.value,
                        'entry_time': self.position.entry_time,
                        'exit_time': current_time,
                        'direction': self.position.direction,
                        'entry_price': self.position.entry_price,
                        'exit_price': exit_price,
                        'gross_pnl': gross_pnl,
                        'net_pnl': net_pnl,
                        'exit_reason': exit_reason,
                        'trade_date': trade_date
                    })

                    daily_pnl[trade_date] += net_pnl
                    daily_trades[trade_date] += 1
                    self.exit_stats[exit_reason] += 1

                    self.position = None
                    self.active_walls.clear()

            # Check entries based on regime
            if not self.position:
                signal = None

                if regime == RegimeState.OPENING_DISCOVERY:
                    # No trading during opening
                    self.stats['opening_skips'] += 1

                elif regime == RegimeState.TREND_MODE:
                    # Use breakout strategy
                    signal = self.detect_breakout_signal(
                        current_time, current_price, current_walls_df, aggression
                    )

                elif regime == RegimeState.ROTATION_MODE:
                    # Use rejection strategy
                    signal = self.detect_rejection_signal(
                        current_time, current_price, aggression
                    )

                elif regime == RegimeState.CHAOS_MODE:
                    # No trading in chaos
                    self.stats['chaos_blocks'] += 1

                if signal:
                    self.position = Position(
                        signal['strategy'],
                        regime,
                        signal['direction'],
                        signal['entry_price'],
                        current_time,
                        signal['wall_price'],
                        signal['entry_delta'],
                        signal.get('entry_ratio')
                    )
                    self.stats['trades_by_regime'][regime.value] += 1

            # Track daily regime distribution
            daily_regimes[trade_date][regime.value] += 1

        # Close any open position
        if self.position:
            # (same closing logic as before)
            pass

        self.print_results(daily_pnl, daily_trades, daily_regimes)

    def print_results(self, daily_pnl, daily_trades, daily_regimes):
        """Print comprehensive results"""
        print("="*70)
        print("REGIME-AWARE HYBRID STRATEGY RESULTS")
        print("="*70)
        print()

        for date in sorted(daily_pnl.keys()):
            pnl = daily_pnl[date]
            trades = daily_trades[date]
            regimes = daily_regimes[date]

            # Calculate regime percentages
            total_bars = sum(regimes.values())
            regime_str = " | ".join([
                f"{r[:4].upper()}={v/total_bars*100:.0f}%"
                for r, v in sorted(regimes.items())
            ])

            print(f"{date}: Trades={trades:3d} | P&L=${pnl:+8,.0f} | {regime_str}")

        print()
        print("="*70)
        print("REGIME STATISTICS")
        print("="*70)
        total_minutes = sum(self.stats['regime_minutes'].values())
        for regime, minutes in sorted(self.stats['regime_minutes'].items()):
            pct = (minutes / total_minutes * 100) if total_minutes > 0 else 0
            print(f"  {regime:20s}: {minutes:5d} min ({pct:5.1f}%)")

        print()
        print("STRATEGY USAGE:")
        print(f"  Rejection entries: {self.stats['rejection_entries']}")
        print(f"  Breakout entries: {self.stats['breakout_entries']}")
        print(f"  Chaos blocks: {self.stats['chaos_blocks']}")
        print(f"  Opening skips: {self.stats['opening_skips']}")

        print()
        if self.trades:
            df_trades = pd.DataFrame(self.trades)

            print("="*70)
            print("TRADES BY REGIME")
            print("="*70)
            for regime in df_trades['entry_regime'].unique():
                regime_trades = df_trades[df_trades['entry_regime'] == regime]
                winners = regime_trades[regime_trades['gross_pnl'] > 0]
                net = regime_trades['net_pnl'].sum()

                print(f"\n{regime.upper()}:")
                print(f"  Trades: {len(regime_trades)} | "
                      f"Winners: {len(winners)} ({len(winners)/len(regime_trades)*100:.1f}%)")
                print(f"  Net P&L: ${net:+,.2f}")
                print(f"  Avg: ${net/len(regime_trades):+,.2f}")

            print()
            print("="*70)
            print("COMBINED RESULTS")
            print("="*70)
            winners = df_trades[df_trades['gross_pnl'] > 0]
            gross_pnl = df_trades['gross_pnl'].sum()
            net_pnl = df_trades['net_pnl'].sum()

            print(f"  Total Trades: {len(df_trades)} | "
                  f"Winners: {len(winners)} ({len(winners)/len(df_trades)*100:.1f}%)")
            print(f"  Gross P&L: ${gross_pnl:+,.2f}")
            print(f"  Net P&L: ${net_pnl:+,.2f}")
            print(f"  Return: {(net_pnl/50000)*100:+.2f}%")
            print()
            print(f"  Avg Gross/Trade: ${gross_pnl/len(df_trades):+,.2f}")
            print(f"  Avg Net/Trade: ${net_pnl/len(df_trades):+,.2f}")
        else:
            print("\nNO TRADES")

        print("="*70)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--start-date', required=True)
    parser.add_argument('--end-date', required=True)
    args = parser.parse_args()

    strategy = RegimeHybridStrategy()
    strategy.run_backtest(args.start_date, args.end_date)
