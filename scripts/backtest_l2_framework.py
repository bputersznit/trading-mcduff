#!/usr/bin/env python3
"""
L2 Order Book Backtesting Framework
Uses ClickHouse L2 data to reconstruct order book and generate signals
"""

import pandas as pd
import numpy as np
from datetime import datetime, timedelta
import subprocess
import sys

class L2OrderBook:
    """Reconstructs order book state from L2 events"""
    
    def __init__(self, max_levels=11):
        self.max_levels = max_levels
        self.bids = {}  # {position: (price, size)}
        self.asks = {}
        self.last_update = None
        
    def process_event(self, timestamp, side, operation, position, price, size):
        """Process single L2 event"""
        book = self.bids if side == 'B' else self.asks
        
        if operation == 'A':  # Add
            book[position] = (price, size)
        elif operation == 'U':  # Update
            book[position] = (price, size)
        elif operation == 'R':  # Remove
            book.pop(position, None)
            
        self.last_update = timestamp
        
    def get_imbalance(self, levels=5):
        """Calculate bid/ask imbalance for top N levels"""
        bid_vol = sum(size for pos, (price, size) in self.bids.items() if pos < levels)
        ask_vol = sum(size for pos, (price, size) in self.asks.items() if pos < levels)
        
        if bid_vol + ask_vol == 0:
            return 0
        return (bid_vol - ask_vol) / (bid_vol + ask_vol)
    
    def get_spread(self):
        """Get bid-ask spread"""
        if 0 not in self.bids or 0 not in self.asks:
            return None
        bid_price, _ = self.bids[0]
        ask_price, _ = self.asks[0]
        return ask_price - bid_price
    
    def get_mid_price(self):
        """Get mid price"""
        if 0 not in self.bids or 0 not in self.asks:
            return None
        bid_price, _ = self.bids[0]
        ask_price, _ = self.asks[0]
        return (bid_price + ask_price) / 2
    
    def detect_wall(self, side, threshold_multiplier=3):
        """Detect significant wall (size > threshold * avg)"""
        book = self.bids if side == 'B' else self.asks
        
        if len(book) < 3:
            return None
            
        sizes = [size for pos, (price, size) in book.items()]
        avg_size = np.mean(sizes)
        
        for pos, (price, size) in book.items():
            if size > avg_size * threshold_multiplier:
                return {'position': pos, 'price': price, 'size': size, 'ratio': size/avg_size}
        return None


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


def simple_imbalance_strategy(df, ob, imbalance_threshold=0.3):
    """
    Simple strategy: Long when strong bid imbalance, Short when strong ask imbalance
    """
    signals = []
    current_position = 0
    
    for idx, row in df.iterrows():
        # Update order book
        ob.process_event(
            row['timestamp'], row['side'], row['operation'],
            row['position'], row['price'], row['size']
        )
        
        # Generate signal every N events (to avoid overtrading)
        if idx % 100 == 0:  # Check every 100 events
            imbalance = ob.get_imbalance(levels=5)
            mid = ob.get_mid_price()
            
            if mid is None:
                continue
            
            # Entry signals
            if current_position == 0:
                if imbalance > imbalance_threshold:  # Strong bid imbalance
                    signals.append({
                        'timestamp': row['timestamp'],
                        'action': 'BUY',
                        'price': mid,
                        'imbalance': imbalance
                    })
                    current_position = 1
                elif imbalance < -imbalance_threshold:  # Strong ask imbalance
                    signals.append({
                        'timestamp': row['timestamp'],
                        'action': 'SELL',
                        'price': mid,
                        'imbalance': imbalance
                    })
                    current_position = -1
            
            # Exit signals (simple: opposite imbalance)
            elif current_position == 1 and imbalance < 0:
                signals.append({
                    'timestamp': row['timestamp'],
                    'action': 'CLOSE_LONG',
                    'price': mid,
                    'imbalance': imbalance
                })
                current_position = 0
            elif current_position == -1 and imbalance > 0:
                signals.append({
                    'timestamp': row['timestamp'],
                    'action': 'CLOSE_SHORT',
                    'price': mid,
                    'imbalance': imbalance
                })
                current_position = 0
    
    return pd.DataFrame(signals)


def calculate_performance(signals_df, point_value=0.5):
    """Calculate strategy performance"""
    if len(signals_df) == 0:
        return None
    
    pnl = []
    entry_price = None
    entry_action = None
    
    for idx, row in signals_df.iterrows():
        if row['action'] in ['BUY', 'SELL']:
            entry_price = row['price']
            entry_action = row['action']
        elif row['action'] in ['CLOSE_LONG', 'CLOSE_SHORT']:
            if entry_price is not None:
                if entry_action == 'BUY':
                    pnl.append((row['price'] - entry_price) * point_value)
                else:
                    pnl.append((entry_price - row['price']) * point_value)
                entry_price = None
    
    if len(pnl) == 0:
        return None
    
    return {
        'total_trades': len(pnl),
        'total_pnl': sum(pnl),
        'avg_pnl': np.mean(pnl),
        'win_rate': len([p for p in pnl if p > 0]) / len(pnl),
        'max_win': max(pnl),
        'max_loss': min(pnl)
    }


def main():
    print("=== L2 Order Book Backtest ===\n")
    
    # Configuration
    START_DATE = '2026-03-02'  # Start with full day
    END_DATE = '2026-03-06'    # First week
    
    print(f"Fetching L2 data: {START_DATE} to {END_DATE}")
    print("(This may take a minute...)\n")
    
    # Fetch data
    df = fetch_l2_data(START_DATE, END_DATE, limit=1000000)  # 1M events for testing
    
    print(f"Loaded {len(df):,} L2 events")
    print(f"Period: {df['timestamp'].min()} to {df['timestamp'].max()}\n")
    
    # Initialize order book
    ob = L2OrderBook(max_levels=11)
    
    # Run strategy
    print("Running imbalance strategy...")
    signals = simple_imbalance_strategy(df, ob, imbalance_threshold=0.3)
    
    print(f"\nGenerated {len(signals)} signals:")
    print(signals.head(10))
    print("\nPerformance:")
    
    perf = calculate_performance(signals)
    if perf:
        for key, value in perf.items():
            print(f"  {key}: {value}")
    else:
        print("  No completed trades")
    
    # Save results
    signals.to_csv('results/l2_backtest_signals.csv', index=False)
    print(f"\nSignals saved to: results/l2_backtest_signals.csv")


if __name__ == '__main__':
    main()
