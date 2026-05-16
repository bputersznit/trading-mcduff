#!/usr/bin/env python3
"""
L2 Wall Strategy - Complete 24-Day Backtest
"""

import pandas as pd
import numpy as np
import subprocess
import sys
from io import StringIO
from datetime import datetime

# Import wall strategy components from backtest_l2_walls.py
from collections import defaultdict

class WallTracker:
    """Tracks walls in order book"""
    def __init__(self, size_threshold=3.0, min_levels_away=2):
        self.size_threshold = size_threshold
        self.min_levels_away = min_levels_away
        self.bid_walls = {}
        self.ask_walls = {}
        self.wall_history = []
        
    def update(self, bids, asks, timestamp):
        bid_sizes = [size for pos, (price, size) in bids.items() if pos < 10]
        ask_sizes = [size for pos, (price, size) in asks.items() if pos < 10]
        
        if not bid_sizes or not ask_sizes:
            return
            
        bid_avg = np.mean(bid_sizes)
        ask_avg = np.mean(ask_sizes)
        
        new_bid_walls = {}
        for pos, (price, size) in bids.items():
            if pos >= self.min_levels_away and size > bid_avg * self.size_threshold:
                wall_id = f"B_{price}"
                if wall_id in self.bid_walls:
                    new_bid_walls[wall_id] = self.bid_walls[wall_id]
                    new_bid_walls[wall_id]['size'] = size
                    new_bid_walls[wall_id]['age'] += 1
                else:
                    new_bid_walls[wall_id] = {
                        'price': price, 'size': size, 'position': pos,
                        'first_seen': timestamp, 'age': 0, 'ratio': size / bid_avg
                    }
        
        for wall_id in self.bid_walls:
            if wall_id not in new_bid_walls:
                self.wall_history.append({'timestamp': timestamp, 'side': 'bid', 
                    'price': self.bid_walls[wall_id]['price'], 'event': 'removed'})
        
        self.bid_walls = new_bid_walls
        
        new_ask_walls = {}
        for pos, (price, size) in asks.items():
            if pos >= self.min_levels_away and size > ask_avg * self.size_threshold:
                wall_id = f"A_{price}"
                if wall_id in self.ask_walls:
                    new_ask_walls[wall_id] = self.ask_walls[wall_id]
                    new_ask_walls[wall_id]['size'] = size
                    new_ask_walls[wall_id]['age'] += 1
                else:
                    new_ask_walls[wall_id] = {
                        'price': price, 'size': size, 'position': pos,
                        'first_seen': timestamp, 'age': 0, 'ratio': size / ask_avg
                    }
        
        for wall_id in self.ask_walls:
            if wall_id not in new_ask_walls:
                self.wall_history.append({'timestamp': timestamp, 'side': 'ask',
                    'price': self.ask_walls[wall_id]['price'], 'event': 'removed'})
        
        self.ask_walls = new_ask_walls
    
    def get_strongest_wall(self, side='bid'):
        walls = self.bid_walls if side == 'bid' else self.ask_walls
        if not walls:
            return None
        return max(walls.values(), key=lambda w: w['ratio'])


class L2OrderBook:
    def __init__(self, max_levels=11):
        self.max_levels = max_levels
        self.bids = {}
        self.asks = {}
        
    def process_event(self, timestamp, side, operation, position, price, size):
        book = self.bids if side == 'B' else self.asks
        if operation in ['A', 'U']:
            book[position] = (price, size)
        elif operation == 'R':
            book.pop(position, None)
    
    def get_mid_price(self):
        if 0 not in self.bids or 0 not in self.asks:
            return None
        return (self.bids[0][0] + self.asks[0][0]) / 2
    
    def get_imbalance(self, levels=5):
        bid_vol = sum(size for pos, (price, size) in self.bids.items() if pos < levels)
        ask_vol = sum(size for pos, (price, size) in self.asks.items() if pos < levels)
        if bid_vol + ask_vol == 0:
            return 0
        return (bid_vol - ask_vol) / (bid_vol + ask_vol)


def get_available_dates():
    """Get all RTH trading days"""
    query = """
    SELECT DISTINCT toDate(timestamp) as date
    FROM l2_depth_raw
    WHERE hour(timestamp) >= 8 AND hour(timestamp) <= 15
    ORDER BY date
    FORMAT CSVWithNames
    """
    result = subprocess.run(['clickhouse-client', '-q', query], 
                          capture_output=True, text=True)
    if result.returncode != 0:
        return []
    return pd.read_csv(StringIO(result.stdout))['date'].tolist()


def fetch_day_data(date):
    """Fetch RTH data for single day"""
    query = f"""
    SELECT timestamp, side, operation, position, price, size
    FROM l2_depth_raw
    WHERE toDate(timestamp) = '{date}'
      AND hour(timestamp) >= 8 AND hour(timestamp) <= 15
    ORDER BY timestamp
    FORMAT CSVWithNames
    """
    result = subprocess.run(['clickhouse-client', '-q', query],
                          capture_output=True, text=True)
    if result.returncode != 0:
        return None
    return pd.read_csv(StringIO(result.stdout))


def run_wall_strategy(df, check_interval=200):
    """Run wall strategy on data"""
    ob = L2OrderBook()
    wall_tracker = WallTracker(size_threshold=3.0, min_levels_away=2)
    
    signals = []
    current_position = 0
    entry_price = None
    
    for idx, row in df.iterrows():
        ob.process_event(row['timestamp'], row['side'], row['operation'],
                        row['position'], row['price'], row['size'])
        
        if idx % check_interval == 0:
            wall_tracker.update(ob.bids, ob.asks, row['timestamp'])
            mid = ob.get_mid_price()
            if mid is None:
                continue
            
            imbalance = ob.get_imbalance(levels=5)
            bid_wall = wall_tracker.get_strongest_wall('bid')
            ask_wall = wall_tracker.get_strongest_wall('ask')
            
            # Entry logic
            if current_position == 0:
                if bid_wall and imbalance > 0.3:
                    if abs(mid - bid_wall['price']) < 5 * 0.25:
                        signals.append({'timestamp': row['timestamp'], 'action': 'BUY',
                                      'price': mid, 'reason': 'bid_wall_support'})
                        current_position = 1
                        entry_price = mid
                
                elif ask_wall and imbalance < -0.3:
                    if abs(mid - ask_wall['price']) < 5 * 0.25:
                        signals.append({'timestamp': row['timestamp'], 'action': 'SELL',
                                      'price': mid, 'reason': 'ask_wall_resistance'})
                        current_position = -1
                        entry_price = mid
            
            # Exit logic
            elif current_position == 1:
                pnl_ticks = (mid - entry_price) / 0.25
                if pnl_ticks >= 10:
                    signals.append({'timestamp': row['timestamp'], 'action': 'CLOSE_LONG',
                                  'price': mid, 'reason': 'profit_target', 'pnl_ticks': pnl_ticks})
                    current_position = 0
                elif pnl_ticks <= -5:
                    signals.append({'timestamp': row['timestamp'], 'action': 'CLOSE_LONG',
                                  'price': mid, 'reason': 'stop_loss', 'pnl_ticks': pnl_ticks})
                    current_position = 0
                elif imbalance < -0.2:
                    signals.append({'timestamp': row['timestamp'], 'action': 'CLOSE_LONG',
                                  'price': mid, 'reason': 'imbalance_flip', 'pnl_ticks': pnl_ticks})
                    current_position = 0
                    
            elif current_position == -1:
                pnl_ticks = (entry_price - mid) / 0.25
                if pnl_ticks >= 10:
                    signals.append({'timestamp': row['timestamp'], 'action': 'CLOSE_SHORT',
                                  'price': mid, 'reason': 'profit_target', 'pnl_ticks': pnl_ticks})
                    current_position = 0
                elif pnl_ticks <= -5:
                    signals.append({'timestamp': row['timestamp'], 'action': 'CLOSE_SHORT',
                                  'price': mid, 'reason': 'stop_loss', 'pnl_ticks': pnl_ticks})
                    current_position = 0
                elif imbalance > 0.2:
                    signals.append({'timestamp': row['timestamp'], 'action': 'CLOSE_SHORT',
                                  'price': mid, 'reason': 'imbalance_flip', 'pnl_ticks': pnl_ticks})
                    current_position = 0
    
    return pd.DataFrame(signals)


def calculate_trades(signals_df):
    """Calculate trades from signals"""
    trades = []
    entry_price = None
    entry_action = None
    entry_time = None
    
    for idx, row in signals_df.iterrows():
        if row['action'] in ['BUY', 'SELL']:
            entry_price = row['price']
            entry_action = row['action']
            entry_time = row['timestamp']
        elif row['action'] in ['CLOSE_LONG', 'CLOSE_SHORT']:
            if entry_price is not None:
                if entry_action == 'BUY':
                    pnl = (row['price'] - entry_price) * 0.5
                else:
                    pnl = (entry_price - row['price']) * 0.5
                
                trades.append({
                    'date': pd.to_datetime(entry_time).date(),
                    'entry_time': entry_time,
                    'exit_time': row['timestamp'],
                    'side': 'long' if entry_action == 'BUY' else 'short',
                    'entry_price': entry_price,
                    'exit_price': row['price'],
                    'pnl': pnl,
                    'exit_reason': row.get('reason', 'unknown')
                })
                entry_price = None
    
    return pd.DataFrame(trades) if trades else pd.DataFrame()


def main():
    print("="*80)
    print("L2 WALL STRATEGY - 24-DAY BACKTEST")
    print("="*80)
    print()
    
    dates = get_available_dates()
    print(f"Found {len(dates)} trading days\n")
    
    all_trades = []
    
    for i, date in enumerate(dates, 1):
        print(f"[{i}/{len(dates)}] {date}...", end=" ")
        
        df = fetch_day_data(date)
        if df is None or len(df) == 0:
            print("No data")
            continue
        
        print(f"{len(df):,} events...", end=" ")
        
        signals = run_wall_strategy(df)
        trades = calculate_trades(signals)
        
        if len(trades) > 0:
            all_trades.append(trades)
            print(f"{len(trades)} trades")
        else:
            print("No trades")
    
    if not all_trades:
        print("\nNo trades generated!")
        return
    
    # Combine all trades
    all_trades_df = pd.concat(all_trades, ignore_index=True)
    
    print("\n" + "="*80)
    print("RESULTS")
    print("="*80)
    
    # Performance metrics
    pnls = all_trades_df['pnl'].values
    
    print(f"\nTotal trades:      {len(all_trades_df)}")
    print(f"Winners:           {len([p for p in pnls if p > 0])}")
    print(f"Losers:            {len([p for p in pnls if p <= 0])}")
    print(f"Win rate:          {100 * len([p for p in pnls if p > 0]) / len(pnls):.1f}%")
    print(f"\nTotal P&L:         ${sum(pnls):.2f}")
    print(f"Average P&L:       ${np.mean(pnls):.2f}")
    print(f"Average winner:    ${np.mean([p for p in pnls if p > 0]):.2f}")
    print(f"Average loser:     ${np.mean([p for p in pnls if p <= 0]):.2f}")
    print(f"Max win:           ${max(pnls):.2f}")
    print(f"Max loss:          ${min(pnls):.2f}")
    
    if sum([p for p in pnls if p <= 0]) != 0:
        pf = abs(sum([p for p in pnls if p > 0]) / sum([p for p in pnls if p <= 0]))
        print(f"Profit factor:     {pf:.2f}")
    
    # Daily breakdown
    print("\n" + "="*80)
    print("DAILY BREAKDOWN")
    print("="*80)
    
    daily = all_trades_df.groupby('date').agg({
        'pnl': ['count', 'sum', lambda x: 100 * len([p for p in x if p > 0]) / len(x)]
    }).round(2)
    daily.columns = ['Trades', 'P&L', 'Win%']
    print(daily.to_string())
    
    # Save results
    all_trades_df.to_csv('results/l2_wall_24day_trades.csv', index=False)
    print(f"\n✅ Results saved to: results/l2_wall_24day_trades.csv")


if __name__ == '__main__':
    main()
