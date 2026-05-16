#!/usr/bin/env python3
"""
Wall Break Momentum Strategy - Tuned Parameters
For trending conditions - enters after clearing walls with continuation
"""

import clickhouse_connect
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
import argparse
from collections import defaultdict, deque

def get_client():
    return clickhouse_connect.get_client(
        host='localhost', port=8123, username='default',
        password='unlucky-strange', database='default'
    )

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

class WallBreakoutStrategy:
    def __init__(self):
        self.client = get_client()
        
        # TUNED PARAMETERS
        self.params = {
            'wall_threshold': 80,
            'min_delta_for_breakout': 150,        # Was 100 - need stronger conviction
            'aggression_ratio': 2.0,              # Was 1.5 - need clear dominance
            'clearance_min': 0.5,                 # Was 1.0 - enter sooner after break
            'clearance_max': 3.0,                 # Was 8.0 - too late if price ran too far
            'lookback_distance': 5.0,
            'stop_buffer': 2.0,                   # Was 1.0 - need room for noise
            'profit_target_pts': 12.0,            # Add profit target for trends
            'trailing_activation_pts': 8.0,       # Activate earlier in trends
            'trailing_stop_pct': 0.6,             # Trail tighter in momentum
            'commission': 0.70,
            'slippage_ticks': 1,
            'tick_value': 2.0,
        }
        
        self.position = None
        self.trades = []
        
        self.signal_stats = {
            'walls_detected': 0,
            'clearances_detected': 0,
            'weak_aggression': 0,
            'qualified_entries': 0
        }
        
        self.exit_stats = defaultdict(int)

    def load_data(self, start_date, end_date):
        """Load 5M data"""
        print(f"\nLoading 5M data from {start_date} to {end_date}...")
        
        # Aggression data
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
        df_aggr = self.client.query_df(query_aggr)
        
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

    def detect_breakout_signal(self, current_time, current_price, walls, aggression):
        """Detect wall break with continuation momentum"""
        if self.position:
            return None
        
        lookback = self.params['lookback_distance']
        min_clearance = self.params['clearance_min']
        max_clearance = self.params['clearance_max']
        aggr_ratio = self.params['aggression_ratio']
        min_delta = self.params['min_delta_for_breakout']
        
        # LONG BREAKOUT: Look for resistance walls we just broke above
        ask_walls = []
        for _, wall_row in walls.iterrows():
            ask_liq = wall_row['ask_liquidity_event_size']
            if ask_liq >= self.params['wall_threshold']:
                wall_price = wall_row['price']
                if wall_price < current_price and wall_price >= current_price - lookback:
                    clearance = current_price - wall_price
                    if clearance >= min_clearance and clearance <= max_clearance:
                        ask_walls.append((wall_price, ask_liq))
                        self.signal_stats['walls_detected'] += 1
        
        if ask_walls:
            self.signal_stats['clearances_detected'] += 1
            # Check strong continuation aggression
            buy_ratio = aggression['buy_vol'] / max(aggression['sell_vol'], 1)
            if buy_ratio >= aggr_ratio and aggression['delta'] > min_delta:
                wall_price = max(ask_walls, key=lambda x: x[1])[0]  # Use largest wall
                self.signal_stats['qualified_entries'] += 1
                return {
                    'direction': 'long',
                    'wall_price': wall_price,
                    'entry_price': current_price,
                    'entry_delta': aggression['delta'],
                    'entry_ratio': buy_ratio
                }
            else:
                self.signal_stats['weak_aggression'] += 1
        
        # SHORT BREAKOUT: Look for support walls we just broke below
        bid_walls = []
        for _, wall_row in walls.iterrows():
            bid_liq = wall_row['bid_liquidity_event_size']
            if bid_liq >= self.params['wall_threshold']:
                wall_price = wall_row['price']
                if wall_price > current_price and wall_price <= current_price + lookback:
                    clearance = wall_price - current_price
                    if clearance >= min_clearance and clearance <= max_clearance:
                        bid_walls.append((wall_price, bid_liq))
                        self.signal_stats['walls_detected'] += 1
        
        if bid_walls:
            self.signal_stats['clearances_detected'] += 1
            # Check strong continuation aggression
            sell_ratio = aggression['sell_vol'] / max(aggression['buy_vol'], 1)
            if sell_ratio >= aggr_ratio and aggression['delta'] < -min_delta:
                wall_price = max(bid_walls, key=lambda x: x[1])[0]  # Use largest wall
                self.signal_stats['qualified_entries'] += 1
                return {
                    'direction': 'short',
                    'wall_price': wall_price,
                    'entry_price': current_price,
                    'entry_delta': aggression['delta'],
                    'entry_ratio': sell_ratio
                }
            else:
                self.signal_stats['weak_aggression'] += 1
        
        return None

    def check_exits(self, current_time, current_price, walls, aggression):
        """Check exit conditions for breakout strategy"""
        if not self.position:
            return None, None
        
        # Update max favorable
        if self.position.direction == 'long':
            pts_favorable = current_price - self.position.entry_price
        else:
            pts_favorable = self.position.entry_price - current_price
        self.position.max_favorable_pts = max(self.position.max_favorable_pts, pts_favorable)
        
        # 1. PROFIT TARGET (for trends - lock in gains)
        if pts_favorable >= self.params['profit_target_pts']:
            return 'profit_target', current_price
        
        # 2. STOP LOSS (breakout failure - price reclaimed wall)
        stop_buffer = self.params['stop_buffer']
        if self.position.direction == 'long':
            stop_price = self.position.wall_price - stop_buffer
            if current_price <= stop_price:
                return 'stop_loss', stop_price
        else:
            stop_price = self.position.wall_price + stop_buffer
            if current_price >= stop_price:
                return 'stop_loss', stop_price
        
        # 3. OPPOSING WALL (structural resistance in trend)
        if self.position.direction == 'long':
            for _, wall_row in walls.iterrows():
                ask_liq = wall_row['ask_liquidity_event_size']
                if ask_liq >= self.params['wall_threshold'] * 1.5:  # Larger wall
                    wall_price = wall_row['price']
                    if current_price >= wall_price - 0.5:
                        return 'opposing_wall', wall_price
        else:
            for _, wall_row in walls.iterrows():
                bid_liq = wall_row['bid_liquidity_event_size']
                if bid_liq >= self.params['wall_threshold'] * 1.5:
                    wall_price = wall_row['price']
                    if current_price <= wall_price + 0.5:
                        return 'opposing_wall', wall_price
        
        # 4. AGGRESSION REVERSAL (momentum died)
        if self.position.direction == 'long':
            if aggression['delta'] < -abs(self.position.entry_delta) * 0.6:
                return 'aggression_reversal', current_price
        else:
            if aggression['delta'] > abs(self.position.entry_delta) * 0.6:
                return 'aggression_reversal', current_price
        
        # 5. TRAILING STOP (protect gains in strong trends)
        if self.position.max_favorable_pts >= self.params['trailing_activation_pts']:
            trail_amount = self.position.max_favorable_pts * self.params['trailing_stop_pct']
            if pts_favorable <= trail_amount:
                return 'trailing_stop', current_price
        
        return None, None

    def run_backtest(self, start_date, end_date):
        """Run breakout strategy backtest"""
        df_aggr, df_walls = self.load_data(start_date, end_date)
        
        daily_pnl = defaultdict(float)
        daily_trades = defaultdict(int)
        
        print(f"\n{'='*70}")
        print("WALL BREAK MOMENTUM STRATEGY - TUNED PARAMETERS")
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
            
            # Get walls for this timestamp
            current_walls = df_walls[df_walls['timestamp'] == current_time].copy()
            
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
                    self.exit_stats[exit_reason] += 1
                    
                    self.position = None
            
            # Check entries
            if not self.position:
                signal = self.detect_breakout_signal(current_time, current_price, current_walls, aggression)
                
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
            self.exit_stats['time_exit'] += 1
        
        self.print_results(daily_pnl, daily_trades)

    def print_results(self, daily_pnl, daily_trades):
        """Print results"""
        print("="*70)
        print("WALL BREAK MOMENTUM STRATEGY - TUNED")
        print("="*70)
        print()
        
        for date in sorted(daily_pnl.keys()):
            pnl = daily_pnl[date]
            trades = daily_trades[date]
            print(f"{date}: Trades={trades:3d} | P&L=${pnl:+8,.0f}")
        
        print()
        print("="*70)
        print("SIGNAL STATISTICS")
        print("="*70)
        print(f"  Walls detected: {self.signal_stats['walls_detected']}")
        print(f"  Clearances detected: {self.signal_stats['clearances_detected']}")
        print(f"  Weak aggression (filtered): {self.signal_stats['weak_aggression']}")
        print(f"  Qualified entries: {self.signal_stats['qualified_entries']}")
        print()
        
        if self.trades:
            df_trades = pd.DataFrame(self.trades)
            
            print("="*70)
            print("EXIT BREAKDOWN")
            print("="*70)
            for reason, count in self.exit_stats.items():
                pct = (count / len(df_trades)) * 100
                print(f"  {reason}: {count} ({pct:.1f}%)")
            print()
            
            print("="*70)
            print("TRADE RESULTS")
            print("="*70)
            winners = df_trades[df_trades['gross_pnl'] > 0]
            losers = df_trades[df_trades['gross_pnl'] < 0]
            gross_pnl = df_trades['gross_pnl'].sum()
            net_pnl = df_trades['net_pnl'].sum()
            total_costs = gross_pnl - net_pnl
            
            print(f"  Total Trades: {len(df_trades)} | Winners: {len(winners)} ({len(winners)/len(df_trades)*100:.1f}%)")
            print(f"  Gross P&L: ${gross_pnl:+,.2f}")
            print(f"  Total Costs: ${total_costs:,.2f}")
            print(f"  Net P&L: ${net_pnl:+,.2f}")
            print(f"  Return: {(net_pnl/50000)*100:+.2f}%")
            print()
            print(f"  Avg Gross/Trade: ${gross_pnl/len(df_trades):+,.2f}")
            print(f"  Avg Net/Trade: ${net_pnl/len(df_trades):+,.2f}")
            print()
            if len(winners) > 0:
                print(f"  Avg Winner: ${winners['gross_pnl'].mean():+,.2f}")
            if len(losers) > 0:
                print(f"  Avg Loser: ${losers['gross_pnl'].mean():+,.2f}")
        else:
            print("\nNO TRADES")
        
        print("="*70)
        print()

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--start-date', required=True)
    parser.add_argument('--end-date', required=True)
    args = parser.parse_args()
    
    strategy = WallBreakoutStrategy()
    strategy.run_backtest(args.start_date, args.end_date)
