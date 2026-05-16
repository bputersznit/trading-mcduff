#!/usr/bin/env python3
"""
L2 Wall Detection and Trading Strategy
Detects significant walls and trades their absorption, rejection, and breakdown
"""

import pandas as pd
import numpy as np
from datetime import datetime, timedelta
import subprocess
import sys
from collections import defaultdict

class WallTracker:
    """Tracks walls (large orders) in the order book"""
    
    def __init__(self, size_threshold=3.0, min_levels_away=2):
        self.size_threshold = size_threshold  # Multiple of average size
        self.min_levels_away = min_levels_away  # Min distance from best bid/ask
        
        self.bid_walls = {}  # {position: {'price': X, 'size': Y, 'first_seen': ts, 'age': N}}
        self.ask_walls = {}
        
        self.wall_history = []  # Track absorbed/removed walls
        
    def update(self, bids, asks, timestamp):
        """Update wall tracking with current book state"""
        
        # Calculate average sizes
        bid_sizes = [size for pos, (price, size) in bids.items() if pos < 10]
        ask_sizes = [size for pos, (price, size) in asks.items() if pos < 10]
        
        if not bid_sizes or not ask_sizes:
            return
            
        bid_avg = np.mean(bid_sizes)
        ask_avg = np.mean(ask_sizes)
        
        # Detect bid walls
        new_bid_walls = {}
        for pos, (price, size) in bids.items():
            if pos >= self.min_levels_away and size > bid_avg * self.size_threshold:
                wall_id = f"B_{price}"
                if wall_id in self.bid_walls:
                    # Existing wall
                    new_bid_walls[wall_id] = self.bid_walls[wall_id]
                    new_bid_walls[wall_id]['size'] = size
                    new_bid_walls[wall_id]['age'] += 1
                else:
                    # New wall
                    new_bid_walls[wall_id] = {
                        'price': price,
                        'size': size,
                        'position': pos,
                        'first_seen': timestamp,
                        'age': 0,
                        'ratio': size / bid_avg
                    }
        
        # Track removed/absorbed bid walls
        for wall_id in self.bid_walls:
            if wall_id not in new_bid_walls:
                wall = self.bid_walls[wall_id]
                self.wall_history.append({
                    'timestamp': timestamp,
                    'side': 'bid',
                    'price': wall['price'],
                    'size': wall['size'],
                    'age': wall['age'],
                    'event': 'removed'
                })
        
        self.bid_walls = new_bid_walls
        
        # Detect ask walls
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
                        'price': price,
                        'size': size,
                        'position': pos,
                        'first_seen': timestamp,
                        'age': 0,
                        'ratio': size / ask_avg
                    }
        
        # Track removed ask walls
        for wall_id in self.ask_walls:
            if wall_id not in new_ask_walls:
                wall = self.ask_walls[wall_id]
                self.wall_history.append({
                    'timestamp': timestamp,
                    'side': 'ask',
                    'price': wall['price'],
                    'size': wall['size'],
                    'age': wall['age'],
                    'event': 'removed'
                })
        
        self.ask_walls = new_ask_walls
    
    def get_strongest_wall(self, side='bid'):
        """Get strongest wall on specified side"""
        walls = self.bid_walls if side == 'bid' else self.ask_walls
        if not walls:
            return None
        # Return wall with highest size ratio
        return max(walls.values(), key=lambda w: w['ratio'])


class L2OrderBook:
    """Reconstructs order book state from L2 events"""
    
    def __init__(self, max_levels=11):
        self.max_levels = max_levels
        self.bids = {}
        self.asks = {}
        self.last_update = None
        
    def process_event(self, timestamp, side, operation, position, price, size):
        """Process single L2 event"""
        book = self.bids if side == 'B' else self.asks
        
        if operation == 'A':
            book[position] = (price, size)
        elif operation == 'U':
            book[position] = (price, size)
        elif operation == 'R':
            book.pop(position, None)
            
        self.last_update = timestamp
    
    def get_mid_price(self):
        """Get mid price"""
        if 0 not in self.bids or 0 not in self.asks:
            return None
        bid_price, _ = self.bids[0]
        ask_price, _ = self.asks[0]
        return (bid_price + ask_price) / 2
    
    def get_imbalance(self, levels=5):
        """Calculate bid/ask imbalance"""
        bid_vol = sum(size for pos, (price, size) in self.bids.items() if pos < levels)
        ask_vol = sum(size for pos, (price, size) in self.asks.items() if pos < levels)
        
        if bid_vol + ask_vol == 0:
            return 0
        return (bid_vol - ask_vol) / (bid_vol + ask_vol)


def fetch_l2_data(start_date, end_date, limit=None):
    """Fetch L2 data from ClickHouse"""
    query = f"""
    SELECT 
        timestamp,
        side,
        operation,
        position,
        price,
        size
    FROM l2_depth_raw
    WHERE toDate(timestamp) >= '{start_date}'
      AND toDate(timestamp) <= '{end_date}'
      AND hour(timestamp) >= 8 
      AND hour(timestamp) <= 15
    ORDER BY timestamp
    {f'LIMIT {limit}' if limit else ''}
    FORMAT CSVWithNames
    """
    
    result = subprocess.run(
        ['clickhouse-client', '-q', query],
        capture_output=True,
        text=True
    )
    
    if result.returncode != 0:
        print(f"Error: {result.stderr}")
        sys.exit(1)
    
    from io import StringIO
    return pd.read_csv(StringIO(result.stdout))


def wall_strategy(df, ob, wall_tracker):
    """
    Wall Trading Strategy:
    1. Wall Absorption: Price approaches wall + strong buying = Long (wall support)
    2. Wall Breakdown: Wall disappears + momentum = Long/Short (breakout)
    3. Wall Rejection: Price bounces off wall = Counter-trend
    """
    
    signals = []
    current_position = 0
    entry_price = None
    entry_reason = None
    
    check_interval = 200  # Check every 200 events
    
    for idx, row in df.iterrows():
        # Update order book
        ob.process_event(
            row['timestamp'], row['side'], row['operation'],
            row['position'], row['price'], row['size']
        )
        
        # Update wall tracking
        if idx % check_interval == 0:
            wall_tracker.update(ob.bids, ob.asks, row['timestamp'])
            
            mid = ob.get_mid_price()
            if mid is None:
                continue
            
            imbalance = ob.get_imbalance(levels=5)
            
            # Get strongest walls
            bid_wall = wall_tracker.get_strongest_wall('bid')
            ask_wall = wall_tracker.get_strongest_wall('ask')
            
            # STRATEGY 1: Wall Absorption (price near wall + strong imbalance)
            if current_position == 0:
                
                # Bullish: Strong bid wall + buying pressure
                if bid_wall and imbalance > 0.3:
                    # Check if price is near the wall (within 5 ticks)
                    if abs(mid - bid_wall['price']) < 5 * 0.25:  # MNQ tick = 0.25
                        signals.append({
                            'timestamp': row['timestamp'],
                            'action': 'BUY',
                            'price': mid,
                            'reason': 'bid_wall_support',
                            'wall_price': bid_wall['price'],
                            'wall_size': bid_wall['size'],
                            'wall_ratio': bid_wall['ratio'],
                            'imbalance': imbalance
                        })
                        current_position = 1
                        entry_price = mid
                        entry_reason = 'bid_wall_support'
                
                # Bearish: Strong ask wall + selling pressure
                elif ask_wall and imbalance < -0.3:
                    if abs(mid - ask_wall['price']) < 5 * 0.25:
                        signals.append({
                            'timestamp': row['timestamp'],
                            'action': 'SELL',
                            'price': mid,
                            'reason': 'ask_wall_resistance',
                            'wall_price': ask_wall['price'],
                            'wall_size': ask_wall['size'],
                            'wall_ratio': ask_wall['ratio'],
                            'imbalance': imbalance
                        })
                        current_position = -1
                        entry_price = mid
                        entry_reason = 'ask_wall_resistance'
            
            # STRATEGY 2: Exit on opposite imbalance or profit target
            elif current_position == 1:
                # Take profit or stop loss
                pnl_ticks = (mid - entry_price) / 0.25
                
                if pnl_ticks >= 10:  # 10 tick profit target
                    signals.append({
                        'timestamp': row['timestamp'],
                        'action': 'CLOSE_LONG',
                        'price': mid,
                        'reason': 'profit_target',
                        'pnl_ticks': pnl_ticks
                    })
                    current_position = 0
                elif pnl_ticks <= -5:  # 5 tick stop loss
                    signals.append({
                        'timestamp': row['timestamp'],
                        'action': 'CLOSE_LONG',
                        'price': mid,
                        'reason': 'stop_loss',
                        'pnl_ticks': pnl_ticks
                    })
                    current_position = 0
                elif imbalance < -0.2:  # Opposite pressure
                    signals.append({
                        'timestamp': row['timestamp'],
                        'action': 'CLOSE_LONG',
                        'price': mid,
                        'reason': 'imbalance_flip',
                        'pnl_ticks': pnl_ticks
                    })
                    current_position = 0
                    
            elif current_position == -1:
                pnl_ticks = (entry_price - mid) / 0.25
                
                if pnl_ticks >= 10:
                    signals.append({
                        'timestamp': row['timestamp'],
                        'action': 'CLOSE_SHORT',
                        'price': mid,
                        'reason': 'profit_target',
                        'pnl_ticks': pnl_ticks
                    })
                    current_position = 0
                elif pnl_ticks <= -5:
                    signals.append({
                        'timestamp': row['timestamp'],
                        'action': 'CLOSE_SHORT',
                        'price': mid,
                        'reason': 'stop_loss',
                        'pnl_ticks': pnl_ticks
                    })
                    current_position = 0
                elif imbalance > 0.2:
                    signals.append({
                        'timestamp': row['timestamp'],
                        'action': 'CLOSE_SHORT',
                        'price': mid,
                        'reason': 'imbalance_flip',
                        'pnl_ticks': pnl_ticks
                    })
                    current_position = 0
        
        # Progress indicator
        if idx % 100000 == 0:
            print(f"  Processed {idx:,} events...")
    
    return pd.DataFrame(signals)


def calculate_performance(signals_df, point_value=0.5):
    """Calculate strategy performance"""
    if len(signals_df) == 0:
        return None
    
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
                    pnl = (row['price'] - entry_price) * point_value
                else:
                    pnl = (entry_price - row['price']) * point_value
                
                trades.append({
                    'entry_time': entry_time,
                    'exit_time': row['timestamp'],
                    'entry_price': entry_price,
                    'exit_price': row['price'],
                    'pnl': pnl,
                    'exit_reason': row.get('reason', 'unknown')
                })
                entry_price = None
    
    if len(trades) == 0:
        return None
    
    trades_df = pd.DataFrame(trades)
    pnls = trades_df['pnl'].values
    
    return {
        'total_trades': len(trades),
        'winners': len([p for p in pnls if p > 0]),
        'losers': len([p for p in pnls if p <= 0]),
        'win_rate': len([p for p in pnls if p > 0]) / len(pnls),
        'total_pnl': sum(pnls),
        'avg_pnl': np.mean(pnls),
        'avg_win': np.mean([p for p in pnls if p > 0]) if any(p > 0 for p in pnls) else 0,
        'avg_loss': np.mean([p for p in pnls if p <= 0]) if any(p <= 0 for p in pnls) else 0,
        'max_win': max(pnls),
        'max_loss': min(pnls),
        'profit_factor': abs(sum([p for p in pnls if p > 0]) / sum([p for p in pnls if p <= 0])) if sum([p for p in pnls if p <= 0]) != 0 else float('inf'),
        'trades_df': trades_df
    }


def main():
    print("=== L2 Wall Detection & Trading Strategy ===\n")
    
    # Configuration
    START_DATE = '2026-03-02'
    END_DATE = '2026-03-06'
    LIMIT = 3000000  # 3M events for first week
    
    print(f"Fetching RTH L2 data: {START_DATE} to {END_DATE}")
    print(f"Limit: {LIMIT:,} events\n")
    
    # Fetch data
    df = fetch_l2_data(START_DATE, END_DATE, limit=LIMIT)
    
    print(f"Loaded {len(df):,} L2 events")
    print(f"Period: {df['timestamp'].min()} to {df['timestamp'].max()}\n")
    
    # Initialize
    ob = L2OrderBook(max_levels=11)
    wall_tracker = WallTracker(size_threshold=3.0, min_levels_away=2)
    
    # Run strategy
    print("Running wall detection strategy...")
    signals = wall_strategy(df, ob, wall_tracker)
    
    print(f"\nGenerated {len(signals)} signals")
    
    if len(signals) > 0:
        print("\nFirst 10 signals:")
        print(signals.head(10).to_string())
        
        print("\n" + "="*80)
        print("PERFORMANCE METRICS")
        print("="*80)
        
        perf = calculate_performance(signals)
        if perf:
            trades_df = perf.pop('trades_df')
            
            for key, value in perf.items():
                if isinstance(value, float):
                    print(f"  {key:20s}: {value:8.2f}")
                else:
                    print(f"  {key:20s}: {value}")
            
            # Save results
            signals.to_csv('results/l2_wall_signals.csv', index=False)
            trades_df.to_csv('results/l2_wall_trades.csv', index=False)
            
            print(f"\nResults saved:")
            print(f"  - results/l2_wall_signals.csv")
            print(f"  - results/l2_wall_trades.csv")
            
            # Wall history
            if len(wall_tracker.wall_history) > 0:
                wall_hist_df = pd.DataFrame(wall_tracker.wall_history)
                wall_hist_df.to_csv('results/l2_wall_history.csv', index=False)
                print(f"  - results/l2_wall_history.csv ({len(wall_hist_df)} walls tracked)")
        else:
            print("  No completed trades")
    else:
        print("No signals generated")


if __name__ == '__main__':
    main()
