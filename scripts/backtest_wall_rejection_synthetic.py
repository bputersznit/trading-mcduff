#!/usr/bin/env python3
"""
Wall Rejection Reversal Strategy - Synthetic Timeframes
Aggregates 5M data into 10M, 15M, 30M bars
"""

import clickhouse_connect
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
import argparse
from collections import defaultdict

class WallState:
    """Track wall persistence and price reaction"""
    def __init__(self, price, size, side, timestamp):
        self.price = price
        self.size = size
        self.side = side
        self.timestamp = timestamp
        self.bars_present = 1
        self.absorption_detected = False
        self.price_low = None
        self.price_high = None

class Position:
    """Track open position"""
    def __init__(self, direction, entry_price, entry_time, wall_price, entry_delta, entry_ratio):
        self.direction = direction
        self.entry_price = entry_price
        self.entry_time = entry_time
        self.wall_price = wall_price
        self.entry_delta = entry_delta
        self.entry_ratio = entry_ratio
        self.max_favorable_pts = 0
        self.trailing_stop_active = False

class WallRejectionBacktest:
    def __init__(self, timeframe_minutes=10):
        self.client = clickhouse_connect.get_client(
            host='localhost', port=8123, username='default',
            password='unlucky-strange', database='default'
        )
        self.timeframe_minutes = timeframe_minutes
        self.bars_per_aggregate = timeframe_minutes // 5  # How many 5M bars to combine
        
        self.params = {
            'wall_threshold': 80,
            'min_bars_present': 2,
            'min_delta_for_absorption': 80,
            'invalidation_buffer': 2.0,
            'trailing_activation_pts': 10.0,
            'trailing_stop_pct': 0.5,
            'commission': 0.70,
            'slippage_ticks': 1,
            'tick_value': 2.0,
        }
        
        self.position = None
        self.trades = []
        self.active_walls = {}
        self.signal_stats = defaultdict(int)
        self.recent_prices = []

    def aggregate_bars(self, df_5m, n_bars):
        """Aggregate 5M bars into larger timeframes"""
        if df_5m.empty:
            return pd.DataFrame()
        
        # Group by chunks of n_bars
        df_5m = df_5m.sort_values('timestamp').reset_index(drop=True)
        df_5m['group'] = df_5m.index // n_bars
        
        # Aggregate
        agg_dict = {
            'timestamp': 'last',
            'price': 'last',
            'buy_vol': 'sum',
            'sell_vol': 'sum',
            'delta': 'sum',
            'trade_date': 'first'
        }
        
        aggregated = df_5m.groupby('group').agg(agg_dict).reset_index(drop=True)
        return aggregated

    def aggregate_walls(self, df_walls_5m, n_bars):
        """Aggregate wall data - take max size at each price level"""
        if df_walls_5m.empty:
            return pd.DataFrame()

        df_walls_5m = df_walls_5m.sort_values('timestamp').reset_index(drop=True)
        df_walls_5m['group'] = df_walls_5m.index // n_bars

        # For each group, aggregate bid/ask liquidity by price
        aggregated_walls = []
        for group_id, group_df in df_walls_5m.groupby('group'):
            timestamp = group_df['timestamp'].iloc[-1]

            for price, price_group in group_df.groupby('price'):
                bid_liq = price_group['bid_liquidity_event_size'].sum()
                ask_liq = price_group['ask_liquidity_event_size'].sum()

                # Create entries for each side if above threshold
                if bid_liq >= self.params['wall_threshold']:
                    aggregated_walls.append({
                        'timestamp': timestamp,
                        'price': price,
                        'size': bid_liq,
                        'side': 'bid',
                        'trade_date': price_group['trade_date'].iloc[0]
                    })
                if ask_liq >= self.params['wall_threshold']:
                    aggregated_walls.append({
                        'timestamp': timestamp,
                        'price': price,
                        'size': ask_liq,
                        'side': 'ask',
                        'trade_date': price_group['trade_date'].iloc[0]
                    })

        return pd.DataFrame(aggregated_walls) if aggregated_walls else pd.DataFrame()

    def load_data(self, start_date, end_date):
        """Load 5M data and aggregate to target timeframe"""
        print(f"\nLoading 5M data for aggregation into {self.timeframe_minutes}M bars...")
        
        # Load aggression data
        query_aggr = f"""
        SELECT 
            toDateTime(ts_bucket) as timestamp,
            argMax(price, ts_bucket) as price,
            sum(buy_exec_size) as buy_vol,
            sum(sell_exec_size) as sell_vol,
            sum(exec_delta) as delta,
            trade_date
        FROM BM_MNQ_AGGRESSION_EXECUTIONS_5M
        WHERE trade_date >= '{start_date}'
          AND trade_date <= '{end_date}'
          AND symbol = 'MNQZ5'
        GROUP BY ts_bucket, trade_date
        ORDER BY ts_bucket
        """
        
        df_aggr_5m = self.client.query_df(query_aggr)
        
        # Load wall data
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
        
        df_walls_5m = self.client.query_df(query_walls)
        
        print(f"Loaded {len(df_aggr_5m)} 5M aggression bars")
        print(f"Loaded {len(df_walls_5m)} 5M wall events")
        
        # Aggregate
        print(f"Aggregating {self.bars_per_aggregate} bars into {self.timeframe_minutes}M timeframe...")
        df_aggr = self.aggregate_bars(df_aggr_5m, self.bars_per_aggregate)
        df_walls = self.aggregate_walls(df_walls_5m, self.bars_per_aggregate)
        
        print(f"Result: {len(df_aggr)} {self.timeframe_minutes}M aggression bars")
        print(f"Result: {len(df_walls)} {self.timeframe_minutes}M wall events")
        
        return df_aggr, df_walls

    def detect_absorption_and_reversal(self, current_time, current_price, aggression):
        """Detect wall absorption with reversal confirmation per Bookmap framework"""
        signals = []
        
        for wall_id, wall in list(self.active_walls.items()):
            wall.bars_present += 1
            
            # Update price extremes
            if wall.price_low is None:
                wall.price_low = current_price
                wall.price_high = current_price
            wall.price_low = min(wall.price_low, current_price)
            wall.price_high = max(wall.price_high, current_price)
            
            # LONG: Bid wall absorption (support holding under selling pressure)
            if wall.side == 'bid' and wall.bars_present >= self.params['min_bars_present']:
                if current_price > wall.price - 3 and current_price < wall.price + 2:
                    if aggression['delta'] < -self.params['min_delta_for_absorption']:
                        if wall.price_low >= wall.price - 1.0:
                            wall.absorption_detected = True
                            if current_price > wall.price and current_price > wall.price_low:
                                signals.append({
                                    'direction': 'long',
                                    'wall_price': wall.price,
                                    'entry_price': current_price,
                                    'entry_delta': aggression['delta'],
                                    'entry_ratio': aggression['sell_vol'] / max(aggression['buy_vol'], 1)
                                })
                                self.signal_stats['qualified_entries'] += 1
            
            # SHORT: Ask wall absorption (resistance holding under buying pressure)
            elif wall.side == 'ask' and wall.bars_present >= self.params['min_bars_present']:
                if current_price < wall.price + 3 and current_price > wall.price - 2:
                    if aggression['delta'] > self.params['min_delta_for_absorption']:
                        if wall.price_high <= wall.price + 1.0:
                            wall.absorption_detected = True
                            if current_price < wall.price and current_price < wall.price_high:
                                signals.append({
                                    'direction': 'short',
                                    'wall_price': wall.price,
                                    'entry_price': current_price,
                                    'entry_delta': aggression['delta'],
                                    'entry_ratio': aggression['buy_vol'] / max(aggression['sell_vol'], 1)
                                })
                                self.signal_stats['qualified_entries'] += 1
        
        return signals[0] if signals else None

    def check_exits(self, current_time, current_price, walls, aggression):
        """Check all exit conditions in priority order"""
        if not self.position:
            return None, None
        
        invalidation_buffer = self.params['invalidation_buffer']
        
        # Update max favorable
        if self.position.direction == 'long':
            pts_favorable = current_price - self.position.entry_price
        else:
            pts_favorable = self.position.entry_price - current_price
        self.position.max_favorable_pts = max(self.position.max_favorable_pts, pts_favorable)
        
        # 1. THESIS INVALIDATION (stop loss)
        if self.position.direction == 'long':
            stop_price = self.position.wall_price - invalidation_buffer
            if current_price <= stop_price:
                return 'thesis_invalidation', stop_price
        else:
            stop_price = self.position.wall_price + invalidation_buffer
            if current_price >= stop_price:
                return 'thesis_invalidation', stop_price
        
        # 2. OPPOSING WALL
        if self.position.direction == 'long':
            ask_walls = walls[walls['side'] == 'ask']
            for _, wall in ask_walls.iterrows():
                if wall['size'] >= self.params['wall_threshold']:
                    if current_price >= wall['price'] - 0.5:
                        return 'opposing_wall', wall['price']
        else:
            bid_walls = walls[walls['side'] == 'bid']
            for _, wall in bid_walls.iterrows():
                if wall['size'] >= self.params['wall_threshold']:
                    if current_price <= wall['price'] + 0.5:
                        return 'opposing_wall', wall['price']
        
        # 3. AGGRESSION EXHAUSTION
        if self.position.direction == 'long':
            if aggression['delta'] < -abs(self.position.entry_delta) * 0.5:
                return 'aggression_exhaustion', current_price
        else:
            if aggression['delta'] > abs(self.position.entry_delta) * 0.5:
                return 'aggression_exhaustion', current_price
        
        # 4. TRAILING STOP
        if self.position.max_favorable_pts >= self.params['trailing_activation_pts']:
            trail_amount = self.position.max_favorable_pts * self.params['trailing_stop_pct']
            if pts_favorable <= trail_amount:
                return 'trailing_stop', current_price
        
        return None, None

    def run_backtest(self, start_date, end_date):
        """Run the backtest"""
        df_aggr, df_walls = self.load_data(start_date, end_date)
        
        daily_pnl = defaultdict(float)
        daily_trades = defaultdict(int)
        
        print(f"\n{'='*70}")
        print(f"Running {self.timeframe_minutes}M backtest...")
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
            
            self.recent_prices.append(current_price)
            if len(self.recent_prices) > 5:
                self.recent_prices.pop(0)
            
            # Get walls for this timestamp
            current_walls = df_walls[df_walls['timestamp'] == current_time].copy()
            
            # Update wall tracking
            for _, wall_row in current_walls.iterrows():
                wall_key = (wall_row['price'], wall_row['side'])
                if wall_key not in self.active_walls:
                    self.active_walls[wall_key] = WallState(
                        wall_row['price'],
                        wall_row['size'],
                        wall_row['side'],
                        current_time
                    )
                    self.signal_stats['walls_detected'] += 1
            
            # Check exits first
            if self.position:
                exit_reason, exit_price = self.check_exits(current_time, current_price, current_walls, aggression)
                if exit_reason:
                    costs = self.params['commission'] + (self.params['slippage_ticks'] * self.params['tick_value'])
                    
                    if self.position.direction == 'long':
                        gross_pnl = (exit_price - self.position.entry_price) * (self.params['tick_value'] / 0.25)
                    else:
                        gross_pnl = (self.position.entry_price - exit_price) * (self.params['tick_value'] / 0.25)
                    
                    net_pnl = gross_pnl - costs
                    
                    self.trades.append({
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
                    
                    self.position = None
                    self.active_walls.clear()
            
            # Check entries
            if not self.position:
                for wall_key, wall in list(self.active_walls.items()):
                    if wall.absorption_detected:
                        self.signal_stats['absorption_detected'] += 1
                
                signal = self.detect_absorption_and_reversal(current_time, current_price, aggression)
                
                if signal:
                    self.position = Position(
                        signal['direction'],
                        signal['entry_price'],
                        current_time,
                        signal['wall_price'],
                        signal['entry_delta'],
                        signal['entry_ratio']
                    )
        
        # Close any open position at end
        if self.position:
            exit_price = df_aggr.iloc[-1]['price']
            costs = self.params['commission'] + (self.params['slippage_ticks'] * self.params['tick_value'])
            
            if self.position.direction == 'long':
                gross_pnl = (exit_price - self.position.entry_price) * (self.params['tick_value'] / 0.25)
            else:
                gross_pnl = (self.position.entry_price - exit_price) * (self.params['tick_value'] / 0.25)
            
            net_pnl = gross_pnl - costs
            
            self.trades.append({
                'entry_time': self.position.entry_time,
                'exit_time': df_aggr.iloc[-1]['timestamp'],
                'direction': self.position.direction,
                'entry_price': self.position.entry_price,
                'exit_price': exit_price,
                'gross_pnl': gross_pnl,
                'net_pnl': net_pnl,
                'exit_reason': 'time_exit',
                'trade_date': df_aggr.iloc[-1]['trade_date']
            })
            
            trade_date = df_aggr.iloc[-1]['trade_date']
            daily_pnl[trade_date] += net_pnl
            daily_trades[trade_date] += 1
        
        self.print_results(daily_pnl, daily_trades)

    def print_results(self, daily_pnl, daily_trades):
        """Print backtest results"""
        print("="*70)
        print("WALL REJECTION REVERSAL STRATEGY")
        print("Per Bookmap Framework Document")
        print("="*70)
        print("Entry: Wall absorption + reversal confirmation")
        print("Stop: Thesis invalidation (2.0 pts beyond wall)")
        print("Exit: Opposing wall, aggression exhaustion, or trailing")
        print("="*70)
        print()
        
        for date in sorted(daily_pnl.keys()):
            pnl = daily_pnl[date]
            trades = daily_trades[date]
            print(f"{date}: Trades={trades:3d} | P&L=${pnl:+8,.0f}")
        
        print()
        print("="*70)
        print(f"WALL REJECTION REVERSAL STRATEGY - {self.timeframe_minutes}M TIMEFRAME")
        print("="*70)
        print()
        print("SIGNAL STATS:")
        print(f"  walls_detected: {self.signal_stats['walls_detected']}")
        print(f"  absorption_detected: {self.signal_stats['absorption_detected']}")
        print(f"  confirmation_failed: {self.signal_stats['confirmation_failed']}")
        print(f"  qualified_entries: {self.signal_stats['qualified_entries']}")
        print()
        
        if self.trades:
            df_trades = pd.DataFrame(self.trades)
            
            exit_counts = df_trades['exit_reason'].value_counts()
            print("EXIT BREAKDOWN:")
            for reason, count in exit_counts.items():
                pct = (count / len(df_trades)) * 100
                print(f"  {reason}: {count} ({pct:.1f}%)")
            print()
            
            winners = df_trades[df_trades['gross_pnl'] > 0]
            gross_pnl = df_trades['gross_pnl'].sum()
            net_pnl = df_trades['net_pnl'].sum()
            total_costs = gross_pnl - net_pnl
            
            print("TRADE RESULTS:")
            print(f"  Trades: {len(df_trades)} | Winners: {len(winners)} ({len(winners)/len(df_trades)*100:.1f}%)")
            print(f"  Gross P&L: ${gross_pnl:+,.2f}")
            print(f"  Total Costs: ${total_costs:,.2f}")
            print(f"  Net P&L: ${net_pnl:+,.2f}")
            print(f"  Return: {(net_pnl/50000)*100:+.2f}%")
            print()
            print(f"  Avg Gross/Trade: ${gross_pnl/len(df_trades):+,.2f}")
            print(f"  Avg Net/Trade: ${net_pnl/len(df_trades):+,.2f}")
        else:
            print("NO TRADES")
        
        print("="*70)
        print()

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--start-date', required=True)
    parser.add_argument('--end-date', required=True)
    parser.add_argument('--timeframe', type=int, required=True, choices=[10, 15, 30],
                       help='Timeframe in minutes (10, 15, or 30)')
    args = parser.parse_args()
    
    bt = WallRejectionBacktest(timeframe_minutes=args.timeframe)
    bt.run_backtest(args.start_date, args.end_date)
