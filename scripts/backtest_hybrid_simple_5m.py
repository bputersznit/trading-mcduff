#!/usr/bin/env python3
"""
Simplified 2-State Hybrid Strategy
ROTATION (default) → Wall Rejection
TREND (strong) → Wall Breakout
"""

import sys
sys.path.append('.')

import clickhouse_connect
import pandas as pd
import numpy as np
from datetime import datetime
import argparse
from collections import defaultdict, deque

from regime_simple import RegimeSimple


def get_client():
    return clickhouse_connect.get_client(
        host='localhost', port=8123, username='default',
        password='unlucky-strange', database='default'
    )


class WallState:
    """Track wall for rejection strategy"""
    def __init__(self, price, size, side, timestamp):
        self.price = price
        self.size = size
        self.side = side
        self.timestamp = timestamp
        self.bars_present = 1
        self.price_low = None
        self.price_high = None


class Position:
    """Track open position"""
    def __init__(self, strategy, direction, entry_price, entry_time, wall_price, entry_delta):
        self.strategy = strategy
        self.direction = direction
        self.entry_price = entry_price
        self.entry_time = entry_time
        self.wall_price = wall_price
        self.entry_delta = entry_delta
        self.max_favorable_pts = 0


class HybridSimpleStrategy:
    """2-state hybrid: Rejection (rotation) + Breakout (trend)"""

    def __init__(self):
        self.client = get_client()

        self.params = {
            # Rejection params
            'wall_threshold': 80,
            'min_bars_present': 2,
            'min_delta_for_absorption': 80,
            'rejection_stop_buffer': 2.0,

            # Breakout params
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

        self.regime = RegimeSimple()
        self.position = None
        self.trades = []
        self.active_walls = {}

        self.stats = {
            'regime_bars': defaultdict(int),
            'rejection_signals': 0,
            'breakout_signals': 0,
            'opening_skips': 0,
        }

        self.exit_stats = defaultdict(int)

    def load_data(self, start_date, end_date):
        """Load 5M data"""
        print(f"\nLoading 5M data...")

        # Aggression
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

        # Aggregate
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
        df_aggr = df_aggr.rename(columns={
            'buy_exec_size': 'buy_vol',
            'sell_exec_size': 'sell_vol',
            'exec_delta': 'delta'
        })
        df_aggr['vwap'] = df_aggr['dollar_volume'] / df_aggr['volume'].replace(0, np.nan)
        df_aggr = df_aggr.drop(['dollar_volume', 'volume'], axis=1)

        # Walls
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

        print(f"Loaded {len(df_aggr)} bars, {len(df_walls)} wall events")
        return df_aggr, df_walls

    def detect_rejection_signal(self, price, aggression):
        """Wall Rejection - for rotation mode"""
        if self.position:
            return None

        for wall_id, wall in list(self.active_walls.items()):
            wall.bars_present += 1

            if wall.price_low is None:
                wall.price_low = price
                wall.price_high = price
            wall.price_low = min(wall.price_low, price)
            wall.price_high = max(wall.price_high, price)

            # LONG: Bid wall absorption
            if wall.side == 'bid' and wall.bars_present >= self.params['min_bars_present']:
                if price > wall.price - 3 and price < wall.price + 2:
                    if aggression['delta'] < -self.params['min_delta_for_absorption']:
                        if wall.price_low >= wall.price - 1.0:
                            if price > wall.price and price > wall.price_low:
                                self.stats['rejection_signals'] += 1
                                return {
                                    'strategy': 'rejection',
                                    'direction': 'long',
                                    'wall_price': wall.price,
                                    'entry_price': price,
                                    'entry_delta': aggression['delta']
                                }

            # SHORT: Ask wall absorption
            elif wall.side == 'ask' and wall.bars_present >= self.params['min_bars_present']:
                if price < wall.price + 3 and price > wall.price - 2:
                    if aggression['delta'] > self.params['min_delta_for_absorption']:
                        if wall.price_high <= wall.price + 1.0:
                            if price < wall.price and price < wall.price_high:
                                self.stats['rejection_signals'] += 1
                                return {
                                    'strategy': 'rejection',
                                    'direction': 'short',
                                    'wall_price': wall.price,
                                    'entry_price': price,
                                    'entry_delta': aggression['delta']
                                }

        return None

    def detect_breakout_signal(self, price, walls_df, aggression):
        """Wall Breakout - for trend mode"""
        if self.position:
            return None

        lookback = self.params['lookback_distance']
        min_clear = self.params['clearance_min']
        max_clear = self.params['clearance_max']
        ratio = self.params['aggression_ratio']
        min_delta = self.params['min_delta_for_breakout']

        # LONG
        ask_walls = []
        for _, wall_row in walls_df.iterrows():
            ask_liq = wall_row['ask_liquidity_event_size']
            if ask_liq >= self.params['wall_threshold']:
                wp = wall_row['price']
                if wp < price and wp >= price - lookback:
                    clear = price - wp
                    if min_clear <= clear <= max_clear:
                        ask_walls.append((wp, ask_liq))

        if ask_walls:
            buy_ratio = aggression['buy_vol'] / max(aggression['sell_vol'], 1)
            if buy_ratio >= ratio and aggression['delta'] > min_delta:
                wp = max(ask_walls, key=lambda x: x[1])[0]
                self.stats['breakout_signals'] += 1
                return {
                    'strategy': 'breakout',
                    'direction': 'long',
                    'wall_price': wp,
                    'entry_price': price,
                    'entry_delta': aggression['delta']
                }

        # SHORT
        bid_walls = []
        for _, wall_row in walls_df.iterrows():
            bid_liq = wall_row['bid_liquidity_event_size']
            if bid_liq >= self.params['wall_threshold']:
                wp = wall_row['price']
                if wp > price and wp <= price + lookback:
                    clear = wp - price
                    if min_clear <= clear <= max_clear:
                        bid_walls.append((wp, bid_liq))

        if bid_walls:
            sell_ratio = aggression['sell_vol'] / max(aggression['buy_vol'], 1)
            if sell_ratio >= ratio and aggression['delta'] < -min_delta:
                wp = max(bid_walls, key=lambda x: x[1])[0]
                self.stats['breakout_signals'] += 1
                return {
                    'strategy': 'breakout',
                    'direction': 'short',
                    'wall_price': wp,
                    'entry_price': price,
                    'entry_delta': aggression['delta']
                }

        return None

    def check_exits(self, price, walls_df, aggression):
        """Exit logic"""
        if not self.position:
            return None, None

        # Update max favorable
        if self.position.direction == 'long':
            pts_fav = price - self.position.entry_price
        else:
            pts_fav = self.position.entry_price - price
        self.position.max_favorable_pts = max(self.position.max_favorable_pts, pts_fav)

        # Stop loss
        stop_buf = (self.params['rejection_stop_buffer'] if self.position.strategy == 'rejection'
                   else self.params['breakout_stop_buffer'])

        if self.position.direction == 'long':
            if price <= self.position.wall_price - stop_buf:
                return 'stop_loss', self.position.wall_price - stop_buf
        else:
            if price >= self.position.wall_price + stop_buf:
                return 'stop_loss', self.position.wall_price + stop_buf

        # Profit target (breakout only)
        if self.position.strategy == 'breakout':
            if pts_fav >= self.params['profit_target_pts']:
                return 'profit_target', price

        # Opposing wall
        if self.position.direction == 'long':
            for _, wr in walls_df.iterrows():
                if wr['ask_liquidity_event_size'] >= self.params['wall_threshold']:
                    if price >= wr['price'] - 0.5:
                        return 'opposing_wall', wr['price']
        else:
            for _, wr in walls_df.iterrows():
                if wr['bid_liquidity_event_size'] >= self.params['wall_threshold']:
                    if price <= wr['price'] + 0.5:
                        return 'opposing_wall', wr['price']

        # Aggression exhaustion
        if self.position.direction == 'long':
            if aggression['delta'] < -abs(self.position.entry_delta) * 0.5:
                return 'aggression_exhaustion', price
        else:
            if aggression['delta'] > abs(self.position.entry_delta) * 0.5:
                return 'aggression_exhaustion', price

        # Trailing
        if self.position.max_favorable_pts >= self.params['trailing_activation_pts']:
            trail = self.position.max_favorable_pts * self.params['trailing_stop_pct']
            if pts_fav <= trail:
                return 'trailing_stop', price

        return None, None

    def run_backtest(self, start_date, end_date):
        """Run backtest"""
        df_aggr, df_walls = self.load_data(start_date, end_date)

        daily_pnl = defaultdict(float)
        daily_trades = defaultdict(int)
        daily_regimes = defaultdict(lambda: defaultdict(int))

        print(f"\n{'='*70}")
        print("SIMPLIFIED 2-STATE HYBRID STRATEGY")
        print("ROTATION → Rejection | TREND → Breakout")
        print(f"{'='*70}\n")

        for idx, row in df_aggr.iterrows():
            ts = row['timestamp']
            price = row['price']
            date = row['trade_date']

            aggr = {
                'buy_vol': row['buy_vol'],
                'sell_vol': row['sell_vol'],
                'delta': row['delta']
            }

            vwap = row.get('vwap')
            walls_df = df_walls[df_walls['timestamp'] == ts].copy()

            # Update regime
            regime_state = self.regime.update(ts, price, aggr['delta'], vwap)
            self.stats['regime_bars'][regime_state] += 1
            daily_regimes[date][regime_state] += 1

            # Update wall tracking for rejection
            for _, wr in walls_df.iterrows():
                bid_liq = wr['bid_liquidity_event_size']
                ask_liq = wr['ask_liquidity_event_size']
                wp = wr['price']

                if bid_liq >= self.params['wall_threshold']:
                    key = (wp, 'bid')
                    if key not in self.active_walls:
                        self.active_walls[key] = WallState(wp, bid_liq, 'bid', ts)

                if ask_liq >= self.params['wall_threshold']:
                    key = (wp, 'ask')
                    if key not in self.active_walls:
                        self.active_walls[key] = WallState(wp, ask_liq, 'ask', ts)

            # Check exits
            if self.position:
                exit_reason, exit_price = self.check_exits(price, walls_df, aggr)

                if exit_reason:
                    costs = self.params['commission'] + (
                        self.params['slippage_ticks'] * self.params['tick_value']
                    )

                    if self.position.direction == 'long':
                        gross = (exit_price - self.position.entry_price) * (
                            self.params['tick_value'] / 0.25
                        )
                    else:
                        gross = (self.position.entry_price - exit_price) * (
                            self.params['tick_value'] / 0.25
                        )

                    net = gross - costs

                    self.trades.append({
                        'strategy': self.position.strategy,
                        'entry_time': self.position.entry_time,
                        'exit_time': ts,
                        'direction': self.position.direction,
                        'entry_price': self.position.entry_price,
                        'exit_price': exit_price,
                        'gross_pnl': gross,
                        'net_pnl': net,
                        'exit_reason': exit_reason,
                        'trade_date': date
                    })

                    daily_pnl[date] += net
                    daily_trades[date] += 1
                    self.exit_stats[exit_reason] += 1

                    self.position = None
                    self.active_walls.clear()

            # Check entries
            if not self.position:
                signal = None

                if regime_state == 'opening':
                    self.stats['opening_skips'] += 1

                elif regime_state == 'rotation':
                    signal = self.detect_rejection_signal(price, aggr)

                elif regime_state == 'trend':
                    signal = self.detect_breakout_signal(price, walls_df, aggr)

                if signal:
                    self.position = Position(
                        signal['strategy'],
                        signal['direction'],
                        signal['entry_price'],
                        ts,
                        signal['wall_price'],
                        signal['entry_delta']
                    )

        # Close open position
        if self.position:
            # (Close at last price)
            pass

        self.print_results(daily_pnl, daily_trades, daily_regimes)

    def print_results(self, daily_pnl, daily_trades, daily_regimes):
        """Print results"""
        print("="*70)
        print("2-STATE HYBRID RESULTS")
        print("="*70)
        print()

        for date in sorted(daily_pnl.keys()):
            pnl = daily_pnl[date]
            trades = daily_trades[date]
            regimes = daily_regimes[date]

            total = sum(regimes.values())
            regime_str = " | ".join([
                f"{r[:3].upper()}={v/total*100:.0f}%"
                for r, v in sorted(regimes.items())
            ])

            print(f"{date}: Trades={trades:3d} | P&L=${pnl:+8,.0f} | {regime_str}")

        print()
        print("="*70)
        print("REGIME DISTRIBUTION")
        print("="*70)
        total_bars = sum(self.stats['regime_bars'].values())
        for regime, bars in sorted(self.stats['regime_bars'].items()):
            pct = (bars / total_bars * 100) if total_bars > 0 else 0
            print(f"  {regime:15s}: {bars:4d} bars ({pct:5.1f}%)")

        print()
        print(f"  Rejection signals: {self.stats['rejection_signals']}")
        print(f"  Breakout signals: {self.stats['breakout_signals']}")
        print(f"  Opening skips: {self.stats['opening_skips']}")

        if self.trades:
            df = pd.DataFrame(self.trades)

            print()
            print("="*70)
            print("BY STRATEGY")
            print("="*70)
            for strat in df['strategy'].unique():
                st = df[df['strategy'] == strat]
                winners = st[st['gross_pnl'] > 0]
                print(f"\n{strat.upper()}:")
                print(f"  Trades: {len(st)} | Winners: {len(winners)} "
                      f"({len(winners)/len(st)*100:.1f}%)")
                print(f"  Net: ${st['net_pnl'].sum():+,.2f} | "
                      f"Avg: ${st['net_pnl'].mean():+,.2f}")

            print()
            print("="*70)
            print("COMBINED")
            print("="*70)
            winners = df[df['gross_pnl'] > 0]
            gross = df['gross_pnl'].sum()
            net = df['net_pnl'].sum()

            print(f"  Trades: {len(df)} | Winners: {len(winners)} "
                  f"({len(winners)/len(df)*100:.1f}%)")
            print(f"  Gross: ${gross:+,.2f}")
            print(f"  Net: ${net:+,.2f}")
            print(f"  Return: {(net/50000)*100:+.2f}%")
            print(f"  Avg Net: ${net/len(df):+,.2f}")
        else:
            print("\nNO TRADES")

        print("="*70)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--start-date', required=True)
    parser.add_argument('--end-date', required=True)
    args = parser.parse_args()

    strategy = HybridSimpleStrategy()
    strategy.run_backtest(args.start_date, args.end_date)
