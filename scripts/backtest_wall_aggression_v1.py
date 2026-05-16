#!/usr/bin/env python3
"""
backtest_wall_aggression_v1.py

Backtest Wall + Aggression strategy directly from ClickHouse data.
Combines heatmap liquidity walls with aggressive order flow.
"""

import clickhouse_connect
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
from collections import defaultdict
import argparse


def get_client():
    return clickhouse_connect.get_client(
        host='localhost',
        port=8123,
        username='default',
        password='unlucky-strange',
        database='default',
    )


class WallAggressionBacktest:
    def __init__(self, params):
        self.params = params
        self.client = get_client()

        # Results tracking
        self.trades = []
        self.equity_curve = []
        self.current_position = None

        # State
        self.cash = params['initial_capital']
        self.equity = params['initial_capital']

    def get_price_bars(self, date_str):
        """Get 1-minute price bars for the date"""
        query = f"""
            SELECT
                toStartOfInterval(ts_bucket, INTERVAL 1 MINUTE) as bar_time,
                argMin(price, ts_bucket) as open,
                max(price) as high,
                min(price) as low,
                argMax(price, ts_bucket) as close,
                sum(total_event_count) as volume
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
            WHERE trade_date = toDate('{date_str}')
              AND symbol = 'MNQ'
            GROUP BY bar_time
            ORDER BY bar_time
        """
        return self.client.query_df(query)

    def get_walls_at_time(self, timestamp, current_price):
        """Get liquidity walls near current price at given timestamp"""
        tick_size = self.params['tick_size']
        wall_distance = self.params['wall_distance_ticks'] * tick_size

        query = f"""
            SELECT
                price,
                sum(bid_liquidity_event_size) as bid_liq,
                sum(ask_liquidity_event_size) as ask_liq
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
            WHERE symbol = 'MNQ'
              AND ts_bucket >= toDateTime64('{timestamp}', 3) - INTERVAL 60 SECOND
              AND ts_bucket <= toDateTime64('{timestamp}', 3)
              AND price >= {current_price - wall_distance}
              AND price <= {current_price + wall_distance}
            GROUP BY price
            HAVING bid_liq >= {self.params['wall_threshold']}
                OR ask_liq >= {self.params['wall_threshold']}
            ORDER BY price
        """

        df = self.client.query_df(query)

        bid_walls = {}
        ask_walls = {}

        for _, row in df.iterrows():
            if row['bid_liq'] >= self.params['wall_threshold']:
                bid_walls[row['price']] = row['bid_liq']
            if row['ask_liq'] >= self.params['wall_threshold']:
                ask_walls[row['price']] = row['ask_liq']

        return bid_walls, ask_walls

    def get_aggression_at_time(self, timestamp):
        """Get recent aggression data"""
        query = f"""
            SELECT
                sum(buy_volume) as buy_vol,
                sum(sell_volume) as sell_vol,
                sum(delta) as net_delta
            FROM CG_mnq_aggression_100ms
            WHERE bucket_time >= toDateTime64('{timestamp}', 3) - INTERVAL 5 SECOND
              AND bucket_time <= toDateTime64('{timestamp}', 3)
        """

        result = self.client.query_df(query)

        if len(result) > 0 and not result.iloc[0].isna().all():
            row = result.iloc[0]
            return {
                'buy_volume': row['buy_vol'] if not pd.isna(row['buy_vol']) else 0,
                'sell_volume': row['sell_vol'] if not pd.isna(row['sell_vol']) else 0,
                'delta': row['net_delta'] if not pd.isna(row['net_delta']) else 0
            }

        return {'buy_volume': 0, 'sell_volume': 0, 'delta': 0}

    def check_exit(self, bar, entry_bar):
        """Check if position should be exited"""
        if self.current_position is None:
            return None

        pos = self.current_position
        direction = pos['direction']
        entry_price = pos['entry_price']
        tick_size = self.params['tick_size']

        profit_target = self.params['profit_target_ticks'] * tick_size
        stop_loss = self.params['stop_loss_ticks'] * tick_size

        # Check profit target
        if direction == 'long':
            if bar['high'] >= entry_price + profit_target:
                return 'profit_target', entry_price + profit_target
            if bar['low'] <= entry_price - stop_loss:
                return 'stop_loss', entry_price - stop_loss
        else:  # short
            if bar['low'] <= entry_price - profit_target:
                return 'profit_target', entry_price - profit_target
            if bar['high'] >= entry_price + stop_loss:
                return 'stop_loss', entry_price + stop_loss

        # Check max bars
        bars_in_trade = len(entry_bar) if isinstance(entry_bar, list) else 1
        if bars_in_trade >= self.params['max_bars_in_position']:
            return 'time_exit', bar['close']

        return None

    def evaluate_signal(self, bar, bid_walls, ask_walls, aggression):
        """Evaluate if conditions met for entry"""
        if self.current_position is not None:
            return None

        current_price = bar['close']
        tick_size = self.params['tick_size']
        wall_distance = self.params['wall_distance_ticks'] * tick_size

        buy_vol = aggression['buy_volume']
        sell_vol = aggression['sell_volume']
        delta = aggression['delta']

        # Need minimum aggression
        if buy_vol + sell_vol < self.params['aggression_threshold']:
            return None

        mode = self.params['mode']
        aggr_ratio = self.params['aggression_ratio']

        # Check bid walls (support)
        for wall_price, wall_size in bid_walls.items():
            if abs(wall_price - current_price) <= wall_distance:

                if mode == 'breakout':
                    # Breakout: aggressive buying through bid wall
                    buy_ratio = buy_vol / sell_vol if sell_vol > 0 else 999
                    if buy_ratio >= aggr_ratio and delta > 0:
                        return {
                            'direction': 'long',
                            'reason': 'breakout_bid_wall',
                            'wall_price': wall_price,
                            'wall_size': wall_size,
                            'aggr_ratio': buy_ratio,
                            'delta': delta
                        }

                elif mode == 'rejection':
                    # Rejection: aggressive selling absorbed by bid wall
                    sell_ratio = sell_vol / buy_vol if buy_vol > 0 else 999
                    if sell_ratio >= aggr_ratio and delta < 0 and current_price >= wall_price:
                        return {
                            'direction': 'long',
                            'reason': 'rejection_bid_wall',
                            'wall_price': wall_price,
                            'wall_size': wall_size,
                            'aggr_ratio': sell_ratio,
                            'delta': delta
                        }

        # Check ask walls (resistance)
        for wall_price, wall_size in ask_walls.items():
            if abs(wall_price - current_price) <= wall_distance:

                if mode == 'breakout':
                    # Breakout: aggressive selling through ask wall
                    sell_ratio = sell_vol / buy_vol if buy_vol > 0 else 999
                    if sell_ratio >= aggr_ratio and delta < 0:
                        return {
                            'direction': 'short',
                            'reason': 'breakout_ask_wall',
                            'wall_price': wall_price,
                            'wall_size': wall_size,
                            'aggr_ratio': sell_ratio,
                            'delta': delta
                        }

                elif mode == 'rejection':
                    # Rejection: aggressive buying absorbed by ask wall
                    buy_ratio = buy_vol / sell_vol if sell_vol > 0 else 999
                    if buy_ratio >= aggr_ratio and delta > 0 and current_price <= wall_price:
                        return {
                            'direction': 'short',
                            'reason': 'rejection_ask_wall',
                            'wall_price': wall_price,
                            'wall_size': wall_size,
                            'aggr_ratio': buy_ratio,
                            'delta': delta
                        }

        return None

    def run_date(self, date_str):
        """Run backtest for a single date"""
        print(f"\nBacktesting {date_str}...")

        bars = self.get_price_bars(date_str)

        if len(bars) == 0:
            print(f"  No data for {date_str}")
            return

        print(f"  Processing {len(bars)} bars...")

        bars_in_position = 0

        for idx, bar in bars.iterrows():
            timestamp = bar['bar_time']

            # Check exit first
            if self.current_position is not None:
                bars_in_position += 1
                exit_result = self.check_exit(bar, bars_in_position)

                if exit_result:
                    exit_reason, exit_price = exit_result
                    pos = self.current_position

                    # Calculate P&L
                    if pos['direction'] == 'long':
                        pnl = (exit_price - pos['entry_price']) / self.params['tick_size'] * self.params['tick_value']
                    else:
                        pnl = (pos['entry_price'] - exit_price) / self.params['tick_size'] * self.params['tick_value']

                    self.equity += pnl

                    trade = {
                        'entry_time': pos['entry_time'],
                        'exit_time': timestamp,
                        'direction': pos['direction'],
                        'entry_price': pos['entry_price'],
                        'exit_price': exit_price,
                        'pnl': pnl,
                        'exit_reason': exit_reason,
                        'bars_held': bars_in_position,
                        'reason': pos['reason']
                    }

                    self.trades.append(trade)
                    self.current_position = None
                    bars_in_position = 0

                    print(f"    {pos['direction'].upper()} exit at {exit_price:.2f} ({exit_reason}): ${pnl:+.2f}")

                continue  # Don't take new position while in trade

            # Look for entry signals
            bid_walls, ask_walls = self.get_walls_at_time(timestamp, bar['close'])
            aggression = self.get_aggression_at_time(timestamp)

            signal = self.evaluate_signal(bar, bid_walls, ask_walls, aggression)

            if signal:
                entry_price = bar['close']

                self.current_position = {
                    'direction': signal['direction'],
                    'entry_price': entry_price,
                    'entry_time': timestamp,
                    'reason': signal['reason'],
                    'wall_price': signal['wall_price'],
                    'aggr_ratio': signal['aggr_ratio']
                }

                bars_in_position = 0

                print(f"    {signal['direction'].upper()} entry at {entry_price:.2f} ({signal['reason']}, wall={signal['wall_price']:.2f}, ratio={signal['aggr_ratio']:.2f})")

            # Track equity
            self.equity_curve.append({
                'timestamp': timestamp,
                'equity': self.equity,
                'position': self.current_position['direction'] if self.current_position else 'flat'
            })

    def generate_report(self):
        """Generate backtest performance report"""
        if len(self.trades) == 0:
            print("\nNo trades executed.")
            return

        trades_df = pd.DataFrame(self.trades)

        # Overall stats
        total_trades = len(trades_df)
        winning_trades = len(trades_df[trades_df['pnl'] > 0])
        losing_trades = len(trades_df[trades_df['pnl'] < 0])
        win_rate = winning_trades / total_trades * 100

        total_pnl = trades_df['pnl'].sum()
        avg_win = trades_df[trades_df['pnl'] > 0]['pnl'].mean() if winning_trades > 0 else 0
        avg_loss = trades_df[trades_df['pnl'] < 0]['pnl'].mean() if losing_trades > 0 else 0

        largest_win = trades_df['pnl'].max()
        largest_loss = trades_df['pnl'].min()

        # Calculate drawdown
        equity_df = pd.DataFrame(self.equity_curve)
        equity_df['cummax'] = equity_df['equity'].cummax()
        equity_df['drawdown'] = equity_df['equity'] - equity_df['cummax']
        max_drawdown = equity_df['drawdown'].min()

        # Profit factor
        gross_profit = trades_df[trades_df['pnl'] > 0]['pnl'].sum()
        gross_loss = abs(trades_df[trades_df['pnl'] < 0]['pnl'].sum())
        profit_factor = gross_profit / gross_loss if gross_loss > 0 else float('inf')

        # Print report
        print("\n" + "="*60)
        print("BACKTEST PERFORMANCE REPORT")
        print("="*60)

        print(f"\nTotal Trades: {total_trades}")
        print(f"Winning Trades: {winning_trades} ({win_rate:.1f}%)")
        print(f"Losing Trades: {losing_trades} ({100-win_rate:.1f}%)")

        print(f"\nNet P&L: ${total_pnl:,.2f}")
        print(f"Avg Win: ${avg_win:.2f}")
        print(f"Avg Loss: ${avg_loss:.2f}")
        print(f"Avg Win/Loss Ratio: {abs(avg_win/avg_loss):.2f}x" if avg_loss != 0 else "N/A")

        print(f"\nLargest Win: ${largest_win:.2f}")
        print(f"Largest Loss: ${largest_loss:.2f}")

        print(f"\nProfit Factor: {profit_factor:.2f}")
        print(f"Max Drawdown: ${max_drawdown:,.2f}")

        print(f"\nFinal Equity: ${self.equity:,.2f}")
        print(f"Return: {((self.equity - self.params['initial_capital']) / self.params['initial_capital'] * 100):.2f}%")

        # Breakdown by direction
        print("\n" + "-"*60)
        print("BREAKDOWN BY DIRECTION")
        print("-"*60)

        for direction in ['long', 'short']:
            dir_trades = trades_df[trades_df['direction'] == direction]
            if len(dir_trades) > 0:
                dir_wins = len(dir_trades[dir_trades['pnl'] > 0])
                dir_pnl = dir_trades['pnl'].sum()
                dir_wr = dir_wins / len(dir_trades) * 100

                print(f"\n{direction.upper()}:")
                print(f"  Trades: {len(dir_trades)}")
                print(f"  Win Rate: {dir_wr:.1f}%")
                print(f"  P&L: ${dir_pnl:,.2f}")

        # Breakdown by reason
        print("\n" + "-"*60)
        print("BREAKDOWN BY SIGNAL TYPE")
        print("-"*60)

        for reason in trades_df['reason'].unique():
            reason_trades = trades_df[trades_df['reason'] == reason]
            reason_wins = len(reason_trades[reason_trades['pnl'] > 0])
            reason_pnl = reason_trades['pnl'].sum()
            reason_wr = reason_wins / len(reason_trades) * 100

            print(f"\n{reason}:")
            print(f"  Trades: {len(reason_trades)}")
            print(f"  Win Rate: {reason_wr:.1f}%")
            print(f"  P&L: ${reason_pnl:,.2f}")

        print("\n" + "="*60)

        # Save trades to CSV
        output_file = f"wall_aggression_trades_{self.params['mode']}_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv"
        trades_df.to_csv(output_file, index=False)
        print(f"\nTrades saved to: {output_file}")


def main():
    parser = argparse.ArgumentParser(description='Backtest Wall + Aggression Strategy')
    parser.add_argument('--start-date', required=True, help='Start date (YYYY-MM-DD)')
    parser.add_argument('--end-date', required=True, help='End date (YYYY-MM-DD)')
    parser.add_argument('--mode', choices=['breakout', 'rejection'], default='breakout', help='Strategy mode')
    parser.add_argument('--wall-threshold', type=float, default=5000, help='Wall threshold')
    parser.add_argument('--aggression-threshold', type=float, default=1000, help='Aggression threshold')
    parser.add_argument('--aggression-ratio', type=float, default=2.0, help='Aggression ratio')
    parser.add_argument('--profit-target', type=int, default=10, help='Profit target in ticks')
    parser.add_argument('--stop-loss', type=int, default=5, help='Stop loss in ticks')

    args = parser.parse_args()

    # Strategy parameters
    params = {
        'initial_capital': 50000,
        'tick_size': 0.25,
        'tick_value': 2.0,  # MNQ = $2 per tick
        'wall_threshold': args.wall_threshold,
        'wall_distance_ticks': 50,  # Increased from 5 to catch more distant walls
        'aggression_threshold': args.aggression_threshold,
        'aggression_ratio': args.aggression_ratio,
        'mode': args.mode,
        'profit_target_ticks': args.profit_target,
        'stop_loss_ticks': args.stop_loss,
        'max_bars_in_position': 50
    }

    print("="*60)
    print("WALL + AGGRESSION BACKTEST")
    print("="*60)
    print(f"\nParameters:")
    for key, value in params.items():
        print(f"  {key}: {value}")

    # Run backtest
    bt = WallAggressionBacktest(params)

    # Get trading dates
    start = datetime.strptime(args.start_date, '%Y-%m-%d')
    end = datetime.strptime(args.end_date, '%Y-%m-%d')

    current = start
    while current <= end:
        date_str = current.strftime('%Y-%m-%d')
        bt.run_date(date_str)
        current += timedelta(days=1)

    # Generate report
    bt.generate_report()


if __name__ == '__main__':
    main()
