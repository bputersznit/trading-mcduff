#!/usr/bin/env python3
"""
Adaptive Wall + Aggression Backtest
Detects trending vs choppy regimes and adjusts strategy
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
    def __init__(self, direction, entry_time, entry_price, entry_reason, regime):
        self.direction = direction
        self.entry_time = entry_time
        self.entry_price = entry_price
        self.entry_reason = entry_reason
        self.regime = regime
        self.exit_time = None
        self.exit_price = None
        self.exit_reason = None
        self.pnl = 0
        self.bars_held = 0


class AdaptiveWallAggressionBacktest:
    def __init__(self, params):
        self.params = params
        self.client = get_client()
        self.trades = []
        self.position = None
        self.equity = params['initial_capital']
        self.equity_curve = []
        self.price_history = []  # For regime detection
        
    def detect_regime(self, current_price):
        """
        Detect if market is trending or choppy
        Returns: ('trending_up', 'trending_down', 'choppy')
        """
        if len(self.price_history) < self.params['regime_lookback']:
            return 'choppy'  # Default to choppy until we have enough data
        
        recent_prices = self.price_history[-self.params['regime_lookback']:]
        
        # Calculate trend metrics
        x = np.arange(len(recent_prices))
        slope, _ = np.polyfit(x, recent_prices, 1)
        
        # Calculate volatility (std dev)
        volatility = np.std(recent_prices)
        
        # Calculate price range
        price_range = max(recent_prices) - min(recent_prices)
        avg_price = np.mean(recent_prices)
        range_pct = price_range / avg_price * 100
        
        # ADX-like metric: how strong is the trend?
        # Slope relative to volatility
        trend_strength = abs(slope) / volatility if volatility > 0 else 0
        
        # Classification logic
        slope_threshold = self.params['trend_slope_threshold']
        strength_threshold = self.params['trend_strength_threshold']
        
        if trend_strength > strength_threshold:
            if slope > slope_threshold:
                return 'trending_up'
            elif slope < -slope_threshold:
                return 'trending_down'
        
        return 'choppy'
    
    def get_regime_params(self, regime):
        """Get strategy parameters based on regime"""
        if regime == 'trending_up':
            return {
                'profit_target': self.params['trend_profit_target'],
                'stop_loss': self.params['trend_stop_loss'],
                'trade_with_trend': True,
                'allowed_direction': 'long'  # Only LONG in uptrend
            }
        elif regime == 'trending_down':
            return {
                'profit_target': self.params['trend_profit_target'],
                'stop_loss': self.params['trend_stop_loss'],
                'trade_with_trend': True,
                'allowed_direction': 'short'  # Only SHORT in downtrend
            }
        else:  # choppy
            return {
                'profit_target': self.params['chop_profit_target'],
                'stop_loss': self.params['chop_stop_loss'],
                'trade_with_trend': False,
                'allowed_direction': 'both'
            }

    def get_market_snapshot(self, timestamp, current_price):
        """Get walls and aggression for this timestamp"""
        ts_str = str(timestamp)[:19]
        
        # Get walls
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

        # Get aggression
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

        aggression = {'buy_volume': 0, 'sell_volume': 0, 'delta': 0}
        if len(aggr_df) > 0 and not aggr_df.iloc[0].isna().all():
            row = aggr_df.iloc[0]
            aggression = {
                'buy_volume': float(row['buy_vol']) if not pd.isna(row['buy_vol']) else 0,
                'sell_volume': float(row['sell_vol']) if not pd.isna(row['sell_vol']) else 0,
                'delta': float(row['delta']) if not pd.isna(row['delta']) else 0
            }

        return walls, aggression

    def check_exit(self, current_time, current_price, regime_params):
        """Check if position should exit"""
        if not self.position:
            return None

        pos = self.position
        tick_size = self.params['tick_size']
        
        # Use regime-specific targets
        profit_target_ticks = regime_params['profit_target']
        stop_loss_ticks = regime_params['stop_loss']

        if pos.direction == 'long':
            profit_price = pos.entry_price + (profit_target_ticks * tick_size)
            stop_price = pos.entry_price - (stop_loss_ticks * tick_size)

            if current_price >= profit_price:
                return 'profit_target', profit_price
            if current_price <= stop_price:
                return 'stop_loss', stop_price
        else:  # short
            profit_price = pos.entry_price - (profit_target_ticks * tick_size)
            stop_price = pos.entry_price + (stop_loss_ticks * tick_size)

            if current_price <= profit_price:
                return 'profit_target', profit_price
            if current_price >= stop_price:
                return 'stop_loss', stop_price

        # Time-based exit
        if pos.bars_held >= self.params['max_bars_in_position']:
            return 'time_exit', current_price

        return None

    def evaluate_signal(self, walls, aggression, current_price, regime, regime_params):
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
        allowed_direction = regime_params['allowed_direction']

        # Find nearest walls
        bid_walls = walls[walls['bid_liq'] >= self.params['wall_threshold']].to_dict('records')
        ask_walls = walls[walls['ask_liq'] >= self.params['wall_threshold']].to_dict('records')

        # Check bid wall signals (LONG)
        if allowed_direction in ['long', 'both']:
            for wall in bid_walls:
                wall_price = wall['price']
                distance = abs(current_price - wall_price)

                if distance <= self.params['wall_distance']:
                    if mode == 'breakout':
                        buy_ratio = buy_vol / sell_vol if sell_vol > 0 else 999
                        if buy_ratio >= ratio_threshold and delta > 0:
                            return {
                                'direction': 'long',
                                'reason': f'{regime}_breakout_bid@{wall_price:.2f}',
                                'wall_size': wall['bid_liq'],
                                'ratio': buy_ratio
                            }

        # Check ask wall signals (SHORT)
        if allowed_direction in ['short', 'both']:
            for wall in ask_walls:
                wall_price = wall['price']
                distance = abs(current_price - wall_price)

                if distance <= self.params['wall_distance']:
                    if mode == 'breakout':
                        sell_ratio = sell_vol / buy_vol if buy_vol > 0 else 999
                        if sell_ratio >= ratio_threshold and delta < 0:
                            return {
                                'direction': 'short',
                                'reason': f'{regime}_breakout_ask@{wall_price:.2f}',
                                'wall_size': wall['ask_liq'],
                                'ratio': sell_ratio
                            }

        return None

    def run_date(self, date_str):
        """Run backtest for single date"""
        print(f"\n{'='*60}")
        print(f"Backtesting {date_str}...")
        print(f"{'='*60}")

        # Get bars
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
            HAVING volume > 5
            ORDER BY ts_bucket
        """
        bars = self.client.query_df(bars_query)

        if len(bars) == 0:
            print(f"  No data for {date_str}")
            return

        print(f"  Processing {len(bars)} bars...\n")

        regime_counts = {'trending_up': 0, 'trending_down': 0, 'choppy': 0}
        signals_checked = 0

        for idx, bar in bars.iterrows():
            timestamp = bar['ts_bucket']
            price = bar['price']
            
            # Update price history for regime detection
            self.price_history.append(price)
            if len(self.price_history) > self.params['regime_lookback']:
                self.price_history.pop(0)
            
            # Detect current regime
            regime = self.detect_regime(price)
            regime_counts[regime] += 1
            regime_params = self.get_regime_params(regime)

            # Update position tracking
            if self.position:
                self.position.bars_held += 1

                # Check exit with regime-specific params
                exit_result = self.check_exit(timestamp, price, regime_params)
                if exit_result:
                    exit_reason, exit_price = exit_result

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

                    print(f"  [{self.position.regime:13s}] EXIT {self.position.direction.upper()}: {exit_reason} @ {exit_price:.2f} | P&L: ${pnl:+.2f}")

                    self.position = None

                continue

            # Look for entry signals (sample every 5 seconds)
            if idx % 5 != 0:
                continue

            signals_checked += 1

            # Get market snapshot
            walls, aggression = self.get_market_snapshot(timestamp, price)

            # Evaluate signal with regime awareness
            signal = self.evaluate_signal(walls, aggression, price, regime, regime_params)

            if signal:
                self.position = Trade(
                    direction=signal['direction'],
                    entry_time=timestamp,
                    entry_price=price,
                    entry_reason=signal['reason'],
                    regime=regime
                )

                print(f"  [{regime:13s}] ENTER {signal['direction'].upper()}: {signal['reason']} @ {price:.2f} | PT/SL: {regime_params['profit_target']}/{regime_params['stop_loss']}")

            self.equity_curve.append({
                'timestamp': timestamp,
                'equity': self.equity,
                'regime': regime
            })

        print(f"\n  Bars: {len(bars)} | Signals checked: {signals_checked}")
        print(f"  Regime distribution:")
        total_bars = sum(regime_counts.values())
        for regime, count in regime_counts.items():
            pct = count / total_bars * 100 if total_bars > 0 else 0
            print(f"    {regime:13s}: {count:5d} bars ({pct:.1f}%)")

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
            'regime': t.regime,
            'entry_price': t.entry_price,
            'exit_price': t.exit_price,
            'pnl': t.pnl,
            'bars_held': t.bars_held,
            'reason': t.entry_reason,
            'exit_reason': t.exit_reason
        } for t in self.trades])

        # Overall stats
        total_trades = len(trades_df)
        winners = trades_df[trades_df['pnl'] > 0]
        losers = trades_df[trades_df['pnl'] < 0]
        win_count = len(winners)
        loss_count = len(losers)
        win_rate = win_count / total_trades * 100 if total_trades > 0 else 0
        total_pnl = trades_df['pnl'].sum()

        print("\n" + "="*60)
        print("ADAPTIVE BACKTEST PERFORMANCE REPORT")
        print("="*60)
        print(f"\nTotal Trades: {total_trades}")
        print(f"Winners: {win_count} ({win_rate:.1f}%)")
        print(f"Losers: {loss_count}")
        print(f"Net P&L: ${total_pnl:,.2f}")
        print(f"Return: {((self.equity - self.params['initial_capital']) / self.params['initial_capital'] * 100):.2f}%")

        # By regime
        print(f"\n{'-'*60}")
        print("BY REGIME")
        print(f"{'-'*60}")
        for regime in ['trending_up', 'trending_down', 'choppy']:
            regime_trades = trades_df[trades_df['regime'] == regime]
            if len(regime_trades) > 0:
                regime_pnl = regime_trades['pnl'].sum()
                regime_wins = len(regime_trades[regime_trades['pnl'] > 0])
                regime_wr = regime_wins / len(regime_trades) * 100
                print(f"\n{regime.upper():13s}: {len(regime_trades):3d} trades | WR: {regime_wr:.1f}% | P&L: ${regime_pnl:+,.2f}")

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
                print(f"{direction.upper()}: {len(dir_trades)} trades | Win Rate: {dir_wr:.1f}% | P&L: ${dir_pnl:,.2f}")

        print("\n" + "="*60)

        # Save trades
        output_file = f"adaptive_trades_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv"
        trades_df.to_csv(output_file, index=False)
        print(f"\nTrades saved to: {output_file}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--start-date', required=True)
    parser.add_argument('--end-date', required=True)
    parser.add_argument('--mode', choices=['breakout', 'rejection'], default='breakout')
    args = parser.parse_args()

    params = {
        'initial_capital': 50000,
        'tick_size': 0.25,
        'tick_value': 2.0,
        'wall_threshold': 80,
        'wall_distance': 12.5,
        'aggression_threshold': 80,
        'aggression_ratio': 1.4,
        'mode': args.mode,
        
        # Regime detection
        'regime_lookback': 300,  # 5 minutes of 1S bars
        'trend_slope_threshold': 0.5,  # Points per bar
        'trend_strength_threshold': 0.3,  # Slope/volatility ratio
        
        # Choppy regime params (original)
        'chop_profit_target': 10,  # ticks
        'chop_stop_loss': 5,  # ticks
        
        # Trending regime params (wider targets)
        'trend_profit_target': 20,  # ticks - let winners run
        'trend_stop_loss': 8,  # ticks - wider stop
        
        'max_bars_in_position': 300
    }

    print("="*60)
    print("ADAPTIVE WALL + AGGRESSION BACKTEST")
    print("="*60)
    print(f"\nRegime Detection: {params['regime_lookback']} bar lookback")
    print(f"Choppy Mode: PT/SL = {params['chop_profit_target']}/{params['chop_stop_loss']} ticks")
    print(f"Trend Mode:  PT/SL = {params['trend_profit_target']}/{params['trend_stop_loss']} ticks")
    print(f"Trend filters: Only trade WITH the trend")

    bt = AdaptiveWallAggressionBacktest(params)

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
