#!/usr/bin/env python3
"""
rebuild_heatmap_stateful_1S.py

Rebuild heatmap with proper order tracking for Modify events.

Handles:
- Add: Create order
- Modify: Update order price/size (requires tracking previous state)
- Cancel: Remove order
"""

import clickhouse_connect
import pandas as pd
from datetime import datetime
from collections import defaultdict
import os

def get_client():
    return clickhouse_connect.get_client(
        host='localhost',
        port=8123,
        username='default',
        password='unlucky-strange',
        database='default',
    )


class OrderBookState:
    """Maintains order book state with individual order tracking"""

    def __init__(self):
        # Order book: {price: {side: total_size}}
        self.book = defaultdict(lambda: {'B': 0, 'A': 0})
        
        # Individual orders: {order_id: {'price': p, 'side': s, 'size': sz}}
        self.orders = {}
        
        # Per-bucket event counters (reset each bucket)
        self.event_counts = defaultdict(lambda: {'B': 0, 'A': 0})
        self.add_sizes = defaultdict(lambda: {'B': 0, 'A': 0})
        self.cancel_sizes = defaultdict(lambda: {'B': 0, 'A': 0})
        self.modify_sizes = defaultdict(lambda: {'B': 0, 'A': 0})

    def process_event(self, action, side, price, size, order_id):
        """Process a single MBO event with order tracking"""
        
        if action == 'A':  # Add new order
            # Add to book
            self.book[price][side] += size
            
            # Track order
            self.orders[order_id] = {
                'price': price,
                'side': side,
                'size': size
            }
            
            # Count event
            self.add_sizes[price][side] += size
            self.event_counts[price][side] += 1

        elif action == 'C':  # Cancel order
            # Remove from book if order exists
            if order_id in self.orders:
                old_order = self.orders[order_id]
                old_price = old_order['price']
                old_side = old_order['side']
                old_size = old_order['size']
                
                # Subtract from book
                self.book[old_price][old_side] = max(0, self.book[old_price][old_side] - old_size)
                
                # Remove order
                del self.orders[order_id]
                
                # Count event at old price
                self.cancel_sizes[old_price][old_side] += old_size
                self.event_counts[old_price][old_side] += 1
            else:
                # Order not tracked (might be from before our window)
                # Just subtract from book
                self.book[price][side] = max(0, self.book[price][side] - size)
                self.cancel_sizes[price][side] += size
                self.event_counts[price][side] += 1

        elif action == 'M':  # Modify order (change price/size)
            if order_id in self.orders:
                # Get old state
                old_order = self.orders[order_id]
                old_price = old_order['price']
                old_side = old_order['side']
                old_size = old_order['size']
                
                # Remove old state from book
                self.book[old_price][old_side] = max(0, self.book[old_price][old_side] - old_size)
                
                # Add new state to book
                self.book[price][side] += size
                
                # Update order tracking
                self.orders[order_id] = {
                    'price': price,
                    'side': side,
                    'size': size
                }
                
                # Count event at both prices
                self.modify_sizes[old_price][old_side] += old_size
                self.modify_sizes[price][side] += size
                self.event_counts[old_price][old_side] += 1
                if price != old_price:
                    self.event_counts[price][side] += 1
            else:
                # Order not tracked - treat as simple add
                self.book[price][side] += size
                self.orders[order_id] = {
                    'price': price,
                    'side': side,
                    'size': size
                }
                self.modify_sizes[price][side] += size
                self.event_counts[price][side] += 1

    def get_snapshot(self):
        """Get current order book state snapshot"""
        snapshot = []

        for price in self.book:
            bid_size = self.book[price]['B']
            ask_size = self.book[price]['A']

            # Only output if there's resting liquidity
            if bid_size > 0 or ask_size > 0:
                snapshot.append({
                    'price': float(price),
                    'bid_size': bid_size,
                    'ask_size': ask_size,
                    'bid_events': self.event_counts[price]['B'],
                    'ask_events': self.event_counts[price]['A'],
                    'bid_adds': self.add_sizes[price]['B'],
                    'ask_adds': self.add_sizes[price]['A'],
                    'bid_cancels': self.cancel_sizes[price]['B'],
                    'ask_cancels': self.cancel_sizes[price]['A'],
                    'bid_modifies': self.modify_sizes[price]['B'],
                    'ask_modifies': self.modify_sizes[price]['A'],
                })

        return snapshot

    def reset_event_counters(self):
        """Reset per-bucket event counters (keep state)"""
        self.event_counts = defaultdict(lambda: {'B': 0, 'A': 0})
        self.add_sizes = defaultdict(lambda: {'B': 0, 'A': 0})
        self.cancel_sizes = defaultdict(lambda: {'B': 0, 'A': 0})
        self.modify_sizes = defaultdict(lambda: {'B': 0, 'A': 0})


def rebuild_heatmap_for_date(client, trade_date):
    """Rebuild heatmap for a single trading date with full order tracking"""

    # Convert to proper date format
    if hasattr(trade_date, 'strftime'):
        trade_date_str = trade_date.strftime('%Y-%m-%d')
        trade_date_obj = trade_date.date() if hasattr(trade_date, 'date') else trade_date
    else:
        trade_date_str = str(trade_date)
        from datetime import datetime
        trade_date_obj = datetime.strptime(trade_date_str, '%Y-%m-%d').date()

    print(f"\nRebuilding {trade_date_str}...")

    # Fetch all MBO events for the date, ordered chronologically
    query = f"""
    SELECT
        ts_event,
        action,
        side,
        price,
        size,
        order_id
    FROM CG_mnq_mbo_events_clean
    WHERE trade_date = toDate('{trade_date_str}')
      AND action IN ('A', 'C', 'M')
    ORDER BY ts_event, sequence
    """

    print("  Fetching MBO events...")
    df = client.query_df(query)
    print(f"  Loaded {len(df):,} events")

    if df.empty:
        print("  No events found, skipping")
        return 0

    # Convert timestamp
    df['ts_event'] = pd.to_datetime(df['ts_event'], utc=True)

    # Determine time range
    start_time = df['ts_event'].min().floor('1S')
    end_time = df['ts_event'].max().ceil('1S')
    time_buckets = pd.date_range(start=start_time, end=end_time, freq='1S')
    
    print(f"  Processing {len(time_buckets):,} time buckets")

    # Initialize order book
    order_book = OrderBookState()
    heatmap_rows = []
    event_idx = 0
    total_events = len(df)
    total_rows_inserted = 0
    BATCH_SIZE = 100000  # Insert every 100K rows to avoid memory issues

    # Process bucket by bucket
    for bucket_idx, bucket_start in enumerate(time_buckets):
        bucket_end = bucket_start + pd.Timedelta(seconds=1)

        # Reset per-bucket counters
        order_book.reset_event_counters()

        # Process all events in this bucket
        while event_idx < total_events:
            event = df.iloc[event_idx]
            event_time = event['ts_event']

            if event_time >= bucket_end:
                break

            # Process event with order tracking
            order_book.process_event(
                action=event['action'],
                side=event['side'],
                price=event['price'],
                size=event['size'],
                order_id=event['order_id']
            )
            event_idx += 1

        # Snapshot state
        snapshot = order_book.get_snapshot()

        # Convert to heatmap rows
        for level in snapshot:
            heatmap_rows.append({
                'trade_date': trade_date_obj,
                'ts_bucket': bucket_start,
                'ts_et': bucket_start.tz_convert('America/New_York'),
                'symbol': 'MNQ',
                'price': level['price'],
                'bid_add_size': level['bid_adds'],
                'ask_add_size': level['ask_adds'],
                'bid_cancel_size': level['bid_cancels'],
                'ask_cancel_size': level['ask_cancels'],
                'bid_modify_size': level['bid_modifies'],
                'ask_modify_size': level['ask_modifies'],
                'bid_trade_size': 0,
                'ask_trade_size': 0,
                'bid_event_count': level['bid_events'],
                'ask_event_count': level['ask_events'],
                'total_event_count': level['bid_events'] + level['ask_events'],
                'bid_liquidity_event_size': level['bid_size'],
                'ask_liquidity_event_size': level['ask_size'],
                'total_liquidity_event_size': level['bid_size'] + level['ask_size'],
                'net_liquidity_event_delta': level['bid_size'] - level['ask_size'],
                'heatmap_proxy_value': level['bid_size'] + level['ask_size'],
                'rth_flag': 1,
            })

        # Batch insert to avoid memory overflow
        if len(heatmap_rows) >= BATCH_SIZE:
            heatmap_df = pd.DataFrame(heatmap_rows)
            client.insert_df('BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S', heatmap_df)
            total_rows_inserted += len(heatmap_rows)
            print(f"  → Inserted {total_rows_inserted:,} rows ({bucket_idx:,}/{len(time_buckets):,} buckets)")
            heatmap_rows = []  # Clear memory

        # Progress
        if bucket_idx % 10000 == 0 and bucket_idx > 0:
            print(f"  {bucket_idx:,}/{len(time_buckets):,} buckets, {event_idx:,}/{total_events:,} events, {total_rows_inserted + len(heatmap_rows):,} rows")

    # Insert remaining rows
    if heatmap_rows:
        heatmap_df = pd.DataFrame(heatmap_rows)
        client.insert_df('BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S', heatmap_df)
        total_rows_inserted += len(heatmap_rows)

    print(f"  Complete: {total_rows_inserted:,} heatmap rows, {len(order_book.orders):,} active orders")

    return total_rows_inserted


def main():
    print("=== BM_MNQ Heatmap State-Based Rebuild 1S (Analytical) ===")
    print("(With full order tracking for Modify events)\n")

    client = get_client()

    # Backup
    print("Backing up existing table...")
    client.command("DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S_EVENT_BASED_BACKUP")
    client.command("""
        CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S_EVENT_BASED_BACKUP
        ENGINE = MergeTree
        PARTITION BY trade_date
        ORDER BY (trade_date, ts_bucket, symbol, price)
        AS SELECT * FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
    """)
    print("✓ Backup created\n")

    # Recreate table
    print("Recreating target table...")
    client.command("DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S")
    client.command("""
        CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
        (
            trade_date Date,
            ts_bucket DateTime64(3, 'UTC'),
            ts_et DateTime64(3, 'America/New_York'),
            symbol LowCardinality(String),
            price Float64,
            bid_add_size Float64,
            ask_add_size Float64,
            bid_cancel_size Float64,
            ask_cancel_size Float64,
            bid_modify_size Float64,
            ask_modify_size Float64,
            bid_trade_size Float64,
            ask_trade_size Float64,
            bid_event_count UInt64,
            ask_event_count UInt64,
            total_event_count UInt64,
            bid_liquidity_event_size Float64,
            ask_liquidity_event_size Float64,
            total_liquidity_event_size Float64,
            net_liquidity_event_delta Float64,
            heatmap_proxy_value Float64,
            rth_flag UInt8,
            created_at DateTime DEFAULT now()
        )
        ENGINE = MergeTree
        PARTITION BY trade_date
        ORDER BY (trade_date, ts_bucket, symbol, price)
    """)
    print("✓ Table created\n")

    # Get trading dates
    dates_df = client.query_df("""
        SELECT DISTINCT trade_date
        FROM CG_mnq_mbo_events_clean
        ORDER BY trade_date
    """)
    trading_dates = dates_df['trade_date'].tolist()
    print(f"Found {len(trading_dates)} trading dates\n")

    # Rebuild
    total_rows = 0
    for i, trade_date in enumerate(trading_dates, 1):
        print(f"[{i}/{len(trading_dates)}] {trade_date}")
        rows = rebuild_heatmap_for_date(client, trade_date)
        total_rows += rows

    print(f"\n\n=== Rebuild Complete ===")
    print(f"Total heatmap rows: {total_rows:,}\n")

    # Validation
    print("Density comparison (price 25230.0, 5min window):")
    result = client.query("""
        WITH
        old AS (
            SELECT count() AS rows
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S_EVENT_BASED_BACKUP
            WHERE trade_date = '2025-10-07'
              AND ts_et >= '2025-10-07 09:30:00'
              AND ts_et < '2025-10-07 09:35:00'
              AND price = 25230.0
        ),
        new AS (
            SELECT count() AS rows
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
            WHERE trade_date = '2025-10-07'
              AND ts_et >= '2025-10-07 09:30:00'
              AND ts_et < '2025-10-07 09:35:00'
              AND price = 25230.0
        )
        SELECT
            old.rows AS old_buckets,
            new.rows AS new_buckets,
            3000 AS expected_buckets,
            new.rows / 3000.0 AS coverage
        FROM old, new
    """)

    for row in result.result_rows:
        print(f"  Old (event): {row[0]} buckets ({row[0]/3000:.1%} coverage)")
        print(f"  New (state): {row[1]} buckets ({row[3]:.1%} coverage)")
        print(f"  Expected:    {row[2]} buckets (100%)")

    client.close()
    print("\n✓ State-based heatmap rebuild complete!")


if __name__ == '__main__':
    main()
