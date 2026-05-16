#!/usr/bin/env python3
"""
Fixed Wall + Aggression Backtest
Uses 1S heatmap data directly as bars, proper time alignment
"""

import clickhouse_connect
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
import argparse


def get_client():
    return clickhouse_connect.get_client(
        host='localhost',
        port=8123,
        username='default',
        password='unlucky-strange',
        database='default',
    )


class Trade:
    def __init__(self, direction, entry_time, entry_price, entry_reason):
        self.direction = direction
        self.entry_time = entry_time
        self.entry_price = entry_price
        self.entry_reason = entry_reason
        self.exit_time = None
        self.exit_price = None
        self.exit_reason = None
        self.pnl = 0
        self.bars_held = 0


class WallAggressionBacktest:
    def __init__(self, params):
        self.params = params
        self.client = get_client()
        self.trades = []
        self.position = None
        self.equity = params['initial_capital']
        self.equity_curve = []

    def get_market_snapshot(self, timestamp, current_price):
        """Get walls and aggression for this timestamp"""

        # Get walls from 1S data (better resolution than 5S)
        # Look back 30 seconds for liquidity buildup
        ts_str = str(timestamp)[:19]  # Remove fractional seconds if present
        wall_query = f"""
            SELECT
                price,
                sum(bid_liquidity_event_size) as bid_liq,
                sum(ask_liquidity_event_size) as ask_liq
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
            WHERE symbol = 'MNQ'
              AND toDateTime(ts_bucket) >= toDateTime('{ts_str}') - INTERVAL 30 SECOND
              AND toDateTime(ts_bucket) <= toDateTime('{ts_str}')
              AND price >= {current_price - self.params['wall_distance']}
              AND price <= {current_price + self.params['wall_distance']}
            GROUP BY price
            HAVING bid_liq >= {self.params['wall_threshold']}
                OR ask_liq >= {self.params['wall_threshold']}
            ORDER BY (bid_liq + ask_liq) DESC
            LIMIT 20
        """

        walls = self.client.query_df(wall_query)

        # Get aggression (5 second window)
        # Convert timestamp to string format that matches aggression bucket_time
        ts_str = str(timestamp)[:19]  # Remove fractional seconds if present
        aggr_query = f"""
            SELECT
                sum(buy_volume) as buy_vol,
                sum(sell_volume) as sell_vol,
                sum(delta) as delta
            FROM CG_mnq_aggression_100ms
            WHERE toDateTime(bucket_time) >= toDateTime('{ts_str}') - INTERVAL 5 SECOND
              AND toDateTime(bucket_time) <= toDateTime('{ts_str}')
        """

        aggr_df = self.client.query_df(aggr_query)

        aggression = {
            'buy_volume': 0,
            'sell_volume': 0,
            'delta': 0
        }

        if len(aggr_df) > 0 and not aggr_df.iloc[0].isna().all():
            row = aggr_df.iloc[0]
            aggression = {
                'buy_volume': float(row['buy_vol']) if not pd.isna(row['buy_vol']) else 0,
                'sell_volume': float(row['sell_vol']) if not pd.isna(row['sell_vol']) else 0,
                'delta': float(row['delta']) if not pd.isna(row['delta']) else 0
            }

        return walls, aggression

    def check_exit(self, current_time, current_price):
        """Check if position should exit"""
        if not self.position:
            return None

        pos = self.position
        tick_size = self.params['tick_size']

        # Price-based exits
        if pos.direction == 'long':
            profit_price = pos.entry_price + (self.params['profit_target_ticks'] * tick_size)
            stop_price = pos.entry_price - (self.params['stop_loss_ticks'] * tick_size)

            if current_price >= profit_price:
                return 'profit_target', profit_price
            if current_price <= stop_price:
                return 'stop_loss', stop_price
        else:  # short
            profit_price = pos.entry_price - (self.params['profit_target_ticks'] * tick_size)
            stop_price = pos.entry_price + (self.params['stop_loss_ticks'] * tick_size)

            if current_price <= profit_price:
                return 'profit_target', profit_price
            if current_price >= stop_price:
                return 'stop_loss', stop_price

        # Time-based exit
        if pos.bars_held >= self.params['max_bars_in_position']:
            return 'time_exit', current_price

        return None

    def evaluate_signal(self, walls, aggression, current_price):
        """Evaluate if entry signal present"""

        if len(walls) == 0:
            return None

        buy_vol = aggression['buy_volume']
        sell_vol = aggression['sell_volume']
        delta = aggression['delta']

        # Need minimum aggression
        total_vol = buy_vol + sell_vol
        if total_vol < self.params['aggression_threshold']:
            return None

        mode = self.params['mode']
        ratio_threshold = self.params['aggression_ratio']

        # Find nearest walls
        bid_walls = walls[walls['bid_liq'] >= self.params['wall_threshold']].to_dict('records')
        ask_walls = walls[walls['ask_liq'] >= self.params['wall_threshold']].to_dict('records')

        # Check bid wall signals
        for wall in bid_walls:
            wall_price = wall['price']
            distance = abs(current_price - wall_price)

            if distance <= self.params['wall_distance']:
                if mode == 'breakout':
                    # Aggressive buying through bid wall
                    buy_ratio = buy_vol / sell_vol if sell_vol > 0 else 999
                    if buy_ratio >= ratio_threshold and delta > 0:
                        return {
                            'direction': 'long',
                            'reason': f'breakout_bid_wall@{wall_price:.2f}',
                            'wall_size': wall['bid_liq'],
                            'ratio': buy_ratio
                        }
                elif mode == 'rejection':
                    # Selling absorbed by bid wall
                    sell_ratio = sell_vol / buy_vol if buy_vol > 0 else 999
                    if sell_ratio >= ratio_threshold and delta < 0:
                        return {
                            'direction': 'long',
                            'reason': f'rejection_bid_wall@{wall_price:.2f}',
                            'wall_size': wall['bid_liq'],
                            'ratio': sell_ratio
                        }

        # Check ask wall signals
        for wall in ask_walls:
            wall_price = wall['price']
            distance = abs(current_price - wall_price)

            if distance <= self.params['wall_distance']:
                if mode == 'breakout':
                    # Aggressive selling through ask wall
                    sell_ratio = sell_vol / buy_vol if buy_vol > 0 else 999
                    if sell_ratio >= ratio_threshold and delta < 0:
                        return {
                            'direction': 'short',
                            'reason': f'breakout_ask_wall@{wall_price:.2f}',
                            'wall_size': wall['ask_liq'],
                            'ratio': sell_ratio
                        }
                elif mode == 'rejection':
                    # Buying absorbed by ask wall
                    buy_ratio = buy_vol / sell_vol if sell_vol > 0 else 999
                    if buy_ratio >= ratio_threshold and delta > 0:
                        return {
                            'direction': 'short',
                            'reason': f'rejection_ask_wall@{wall_price:.2f}',
                            'wall_size': wall['ask_liq'],
                            'ratio': buy_ratio
                        }

        return None

    def run_date(self, date_str):
        """Run backtest for single date"""
        print(f"\n{'='*60}")
        print(f"Backtesting {date_str}...")
        print(f"{'='*60}")

        # Get all 1S timestamps and prices for the date (use as bars)
        bars_query = f"""
            SELECT
                ts_bucket,
                argMax(price, total_event_count) as price,
                sum(total_event_count) as volume
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
            WHERE trade_date = '{date_str}'
              AND symbol = 'MNQ'
              AND total_event_count > 0
            GROUP BY ts_bucket
            HAVING volume > 5  -- Filter very quiet periods
            ORDER BY ts_bucket
        """

        bars = self.client.query_df(bars_query)

        if len(bars) == 0:
            print(f"  No data for {date_str}")
            return

        print(f"  Processing {len(bars)} bars...\n")

        walls_detected = 0
        aggression_detected = 0
        signals_checked = 0

        for idx, bar in bars.iterrows():
            timestamp = bar['ts_bucket']
            price = bar['price']

            # Update position tracking
            if self.position:
                self.position.bars_held += 1

                # Check exit
                exit_result = self.check_exit(timestamp, price)
                if exit_result:
                    exit_reason, exit_price = exit_result

                    # Calculate P&L
                    if self.position.direction == 'long':
                        pnl = (exit_price - self.position.entry_price) / self.params['tick_size'] * self.params['tick_value']
                    else:
                        pnl = (self.position.entry_price - exit_price) / self.params['tick_size'] * self.params['tick_value']

                    self.position.exit_time = timestamp
                    self.position.exit_price = exit_price
                    self.position.exit_reason = exit_reason
                    self.position.pnl = pnl

                    self.equity += pnl
                    self.trades.append(self.position)

                    print(f"  EXIT {self.position.direction.upper()}: {exit_reason} @ {exit_price:.2f} | P&L: ${pnl:+.2f} | Bars: {self.position.bars_held}")

                    self.position = None

                continue  # Don't take new position while in trade

            # Look for entry signals (sample every 5 seconds to reduce queries)
            if idx % 5 != 0:
                continue

            signals_checked += 1

            # Get market snapshot
            walls, aggression = self.get_market_snapshot(timestamp, price)

            if len(walls) > 0:
                walls_detected += 1
            if aggression['buy_volume'] + aggression['sell_volume'] > 50:
                aggression_detected += 1

            # Evaluate signal
            signal = self.evaluate_signal(walls, aggression, price)

            if signal:
                self.position = Trade(
                    direction=signal['direction'],
                    entry_time=timestamp,
                    entry_price=price,
                    entry_reason=signal['reason']
                )

                print(f"  ENTER {signal['direction'].upper()}: {signal['reason']} @ {price:.2f} | Wall: {signal['wall_size']:.0f} | Ratio: {signal['ratio']:.2f}")

            # Track equity
            self.equity_curve.append({
                'timestamp': timestamp,
                'equity': self.equity
            })

        print(f"\n  Bars processed: {len(bars)}")
        print(f"  Signals checked: {signals_checked}")
        print(f"  Walls detected: {walls_detected} ({walls_detected/signals_checked*100:.1f}%)")
        print(f"  Aggression detected: {aggression_detected} ({aggression_detected/signals_checked*100:.1f}%)")

    def generate_report(self):
        """Generate performance report"""
        if len(self.trades) == 0:
            print("\n" + "="*60)
            print("NO TRADES EXECUTED")
            print("="*60)
            return

        trades_df = pd.DataFrame([{
            'entry_time': t.entry_time,
            'exit_time': t.exit_time,
            'direction': t.direction,
            'entry_price': t.entry_price,
            'exit_price': t.exit_price,
            'pnl': t.pnl,
            'bars_held': t.bars_held,
            'reason': t.entry_reason,
            'exit_reason': t.exit_reason
        } for t in self.trades])

        # Stats
        total_trades = len(trades_df)
        winners = trades_df[trades_df['pnl'] > 0]
        losers = trades_df[trades_df['pnl'] < 0]

        win_count = len(winners)
        loss_count = len(losers)
        win_rate = win_count / total_trades * 100 if total_trades > 0 else 0

        total_pnl = trades_df['pnl'].sum()
        avg_win = winners['pnl'].mean() if len(winners) > 0 else 0
        avg_loss = losers['pnl'].mean() if len(losers) > 0 else 0

        gross_profit = winners['pnl'].sum() if len(winners) > 0 else 0
        gross_loss = abs(losers['pnl'].sum()) if len(losers) > 0 else 0
        profit_factor = gross_profit / gross_loss if gross_loss > 0 else float('inf')

        print("\n" + "="*60)
        print("BACKTEST PERFORMANCE REPORT")
        print("="*60)

        print(f"\nTotal Trades: {total_trades}")
        print(f"Winners: {win_count} ({win_rate:.1f}%)")
        print(f"Losers: {loss_count} ({100-win_rate:.1f}%)")

        print(f"\nNet P&L: ${total_pnl:,.2f}")
        print(f"Gross Profit: ${gross_profit:,.2f}")
        print(f"Gross Loss: ${gross_loss:,.2f}")
        print(f"Profit Factor: {profit_factor:.2f}")

        print(f"\nAvg Win: ${avg_win:.2f}")
        print(f"Avg Loss: ${avg_loss:.2f}")
        print(f"Win/Loss Ratio: {abs(avg_win/avg_loss):.2f}x" if avg_loss != 0 else "N/A")

        print(f"\nLargest Win: ${trades_df['pnl'].max():.2f}")
        print(f"Largest Loss: ${trades_df['pnl'].min():.2f}")

        print(f"\nFinal Equity: ${self.equity:,.2f}")
        print(f"Return: {((self.equity - self.params['initial_capital']) / self.params['initial_capital'] * 100):.2f}%")

        # By direction
        print(f"\n{'-'*60}")
        print("BY DIRECTION")
        print(f"{'-'*60}")
        for direction in ['long', 'short']:
            dir_trades = trades_df[trades_df['direction'] == direction]
            if len(dir_trades) > 0:
                dir_pnl = dir_trades['pnl'].sum()
                dir_wins = len(dir_trades[dir_trades['pnl'] > 0])
                dir_wr = dir_wins / len(dir_trades) * 100
                print(f"\n{direction.upper()}: {len(dir_trades)} trades | Win Rate: {dir_wr:.1f}% | P&L: ${dir_pnl:,.2f}")

        print("\n" + "="*60)

        # Save trades
        output_file = f"wall_aggression_trades_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv"
        trades_df.to_csv(output_file, index=False)
        print(f"\nTrades saved to: {output_file}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--start-date', required=True)
    parser.add_argument('--end-date', required=True)
    parser.add_argument('--mode', choices=['breakout', 'rejection'], default='breakout')
    parser.add_argument('--wall-threshold', type=float, default=100)
    parser.add_argument('--aggression-threshold', type=float, default=100)
    parser.add_argument('--aggression-ratio', type=float, default=1.5)
    parser.add_argument('--profit-target', type=int, default=10)
    parser.add_argument('--stop-loss', type=int, default=5)
    args = parser.parse_args()

    params = {
        'initial_capital': 50000,
        'tick_size': 0.25,
        'tick_value': 2.0,
        'wall_threshold': args.wall_threshold,
        'wall_distance': 12.5,  # 50 ticks = 12.5 points
        'aggression_threshold': args.aggression_threshold,
        'aggression_ratio': args.aggression_ratio,
        'mode': args.mode,
        'profit_target_ticks': args.profit_target,
        'stop_loss_ticks': args.stop_loss,
        'max_bars_in_position': 300  # 5 minutes at 1S bars
    }

    print("="*60)
    print("WALL + AGGRESSION BACKTEST (FIXED)")
    print("="*60)
    print(f"\nMode: {params['mode']}")
    print(f"Wall Threshold: {params['wall_threshold']}")
    print(f"Aggression Threshold: {params['aggression_threshold']}")
    print(f"Aggression Ratio: {params['aggression_ratio']}")
    print(f"PT/SL: {params['profit_target_ticks']}/{params['stop_loss_ticks']} ticks")

    bt = WallAggressionBacktest(params)

    # Run dates
    start = datetime.strptime(args.start_date, '%Y-%m-%d')
    end = datetime.strptime(args.end_date, '%Y-%m-%d')

    current = start
    while current <= end:
        bt.run_date(current.strftime('%Y-%m-%d'))
        current += timedelta(days=1)

    bt.generate_report()


if __name__ == '__main__':
    main()
