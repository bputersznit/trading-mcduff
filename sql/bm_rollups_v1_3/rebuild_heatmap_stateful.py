#!/usr/bin/env python3
"""
rebuild_heatmap_stateful.py

Rebuild BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS with proper ORDER BOOK STATE tracking.

Key difference from current:
- Current: Only records when liquidity EVENTS occur (gaps when orders rest)
- New: Maintains continuous ORDER BOOK STATE (shows resting orders continuously)

Approach:
1. Process MBO events chronologically
2. Maintain order book state: {price_level: {side: total_size}}
3. Every 100ms: snapshot the state → heatmap row
4. Fills gaps with continuous presence of resting orders
"""

import clickhouse_connect
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
from collections import defaultdict
import os

def get_client():
    return clickhouse_connect.get_client(
        host=os.getenv('CH_HOST', 'localhost'),
        port=int(os.getenv('CH_PORT', '8123')),
        username=os.getenv('CH_USER', 'default'),
        password=os.getenv('CH_PASSWORD', ''),
        database=os.getenv('CH_DATABASE', 'default'),
    )


class OrderBookState:
    """Maintains order book state from MBO events"""

    def __init__(self):
        # {price: {'B': size, 'A': size}}
        self.book = defaultdict(lambda: {'B': 0, 'A': 0})
        self.event_counts = defaultdict(lambda: {'B': 0, 'A': 0})
        self.add_sizes = defaultdict(lambda: {'B': 0, 'A': 0})
        self.cancel_sizes = defaultdict(lambda: {'B': 0, 'A': 0})

    def process_event(self, action, side, price, size):
        """Process a single MBO event"""
        if action == 'A':  # Add
            self.book[price][side] += size
            self.add_sizes[price][side] += size
            self.event_counts[price][side] += 1

        elif action == 'C':  # Cancel
            self.book[price][side] = max(0, self.book[price][side] - size)
            self.cancel_sizes[price][side] += size
            self.event_counts[price][side] += 1

        elif action == 'M':  # Modify
            # Modify typically means size change
            # For simplicity, treat as cancel + add
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
                })

        return snapshot

    def reset_event_counters(self):
        """Reset per-bucket event counters (keep state)"""
        self.event_counts = defaultdict(lambda: {'B': 0, 'A': 0})
        self.add_sizes = defaultdict(lambda: {'B': 0, 'A': 0})
        self.cancel_sizes = defaultdict(lambda: {'B': 0, 'A': 0})


def rebuild_heatmap_for_date(client, trade_date):
    """Rebuild heatmap for a single trading date with state tracking"""

    print(f"Rebuilding heatmap for {trade_date}...")

    # Fetch all MBO events for the date, ordered chronologically
    query = f"""
    SELECT
        ts_event,
        action,
        side,
        price,
        size
    FROM CG_mnq_mbo_events_clean
    WHERE trade_date = '{trade_date}'
      AND action IN ('A', 'C', 'M')
    ORDER BY ts_event, sequence
    """

    print("Fetching MBO events...")
    df = client.query_df(query)
    print(f"  Loaded {len(df):,} events")

    if df.empty:
        print("  No events found, skipping")
        return

    # Convert timestamp to datetime
    df['ts_event'] = pd.to_datetime(df['ts_event'], utc=True)

    # Determine 100ms buckets
    start_time = df['ts_event'].min().floor('100ms')
    end_time = df['ts_event'].max().ceil('100ms')

    # Create 100ms time grid
    time_buckets = pd.date_range(start=start_time, end=end_time, freq='100ms')
    print(f"  Processing {len(time_buckets):,} time buckets (100ms each)")

    # Initialize order book
    order_book = OrderBookState()

    # Output rows
    heatmap_rows = []

    # Process events bucket by bucket
    event_idx = 0
    total_events = len(df)

    for bucket_start in time_buckets:
        bucket_end = bucket_start + pd.Timedelta(milliseconds=100)

        # Reset per-bucket counters
        order_book.reset_event_counters()

        # Process all events in this bucket
        while event_idx < total_events:
            event_time = df.iloc[event_idx]['ts_event']

            if event_time >= bucket_end:
                break  # Move to next bucket

            # Process event
            order_book.process_event(
                action=df.iloc[event_idx]['action'],
                side=df.iloc[event_idx]['side'],
                price=df.iloc[event_idx]['price'],
                size=df.iloc[event_idx]['size']
            )
            event_idx += 1

        # Snapshot the order book state for this bucket
        snapshot = order_book.get_snapshot()

        # Convert to heatmap rows
        for level in snapshot:
            heatmap_rows.append({
                'trade_date': trade_date,
                'ts_bucket': bucket_start,
                'ts_et': bucket_start.tz_convert('America/New_York'),
                'symbol': 'MNQ',
                'price': level['price'],
                'bid_add_size': level['bid_adds'],
                'ask_add_size': level['ask_adds'],
                'bid_cancel_size': level['bid_cancels'],
                'ask_cancel_size': level['ask_cancels'],
                'bid_modify_size': 0,  # Simplification
                'ask_modify_size': 0,
                'bid_trade_size': 0,  # Tracked separately
                'ask_trade_size': 0,
                'bid_event_count': level['bid_events'],
                'ask_event_count': level['ask_events'],
                'total_event_count': level['bid_events'] + level['ask_events'],
                'bid_liquidity_event_size': level['bid_size'],  # Current resting size
                'ask_liquidity_event_size': level['ask_size'],
                'total_liquidity_event_size': level['bid_size'] + level['ask_size'],
                'net_liquidity_event_delta': level['bid_size'] - level['ask_size'],
                'heatmap_proxy_value': level['bid_size'] + level['ask_size'],  # Total depth
                'rth_flag': 1,
            })

        if len(heatmap_rows) % 100000 == 0 and len(heatmap_rows) > 0:
            print(f"  Processed {event_idx:,}/{total_events:,} events, {len(heatmap_rows):,} heatmap rows")

    print(f"  Generated {len(heatmap_rows):,} heatmap rows (state-based)")

    # Insert into ClickHouse
    if heatmap_rows:
        print("  Inserting into ClickHouse...")
        heatmap_df = pd.DataFrame(heatmap_rows)

        client.insert_df(
            'BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS',
            heatmap_df
        )
        print("  ✓ Insert complete")

    return len(heatmap_rows)


def main():
    print("=== BM_MNQ Heatmap State-Based Rebuild ===\n")

    client = get_client()

    # Backup existing table
    print("Creating backup of existing heatmap...")
    client.command("""
        CREATE TABLE IF NOT EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS_EVENT_BASED_BACKUP
        ENGINE = MergeTree
        PARTITION BY trade_date
        ORDER BY (trade_date, ts_bucket, symbol, price)
        AS SELECT * FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS
    """)
    print("✓ Backup created\n")

    # Drop and recreate target table
    print("Recreating target table...")
    client.command("DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS")
    client.command("""
        CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS
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

    # Get list of trading dates to rebuild
    dates_df = client.query_df("""
        SELECT DISTINCT trade_date
        FROM CG_mnq_mbo_events_clean
        ORDER BY trade_date
    """)

    trading_dates = dates_df['trade_date'].tolist()
    print(f"Found {len(trading_dates)} trading dates to rebuild\n")

    # Rebuild each date
    total_rows = 0
    for i, trade_date in enumerate(trading_dates, 1):
        print(f"\n[{i}/{len(trading_dates)}] {trade_date}")
        rows = rebuild_heatmap_for_date(client, trade_date)
        total_rows += rows

    # Final validation
    print(f"\n\n=== Rebuild Complete ===")
    print(f"Total heatmap rows: {total_rows:,}")

    # Compare density
    result = client.query("""
        WITH
        old AS (
            SELECT count() AS rows
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS_EVENT_BASED_BACKUP
            WHERE trade_date = '2025-10-07'
              AND ts_et >= '2025-10-07 09:30:00'
              AND ts_et < '2025-10-07 09:35:00'
              AND price = 25230.0
        ),
        new AS (
            SELECT count() AS rows
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_100MS
            WHERE trade_date = '2025-10-07'
              AND ts_et >= '2025-10-07 09:30:00'
              AND ts_et < '2025-10-07 09:35:00'
              AND price = 25230.0
        )
        SELECT
            old.rows AS old_event_based,
            new.rows AS new_state_based,
            3000 AS expected_buckets,
            new.rows / 3000.0 AS coverage_ratio
        FROM old, new
    """)

    print("\nDensity comparison (price 25230.0, 09:30-09:35):")
    for row in result.result_rows:
        print(f"  Old (event-based): {row[0]} buckets")
        print(f"  New (state-based): {row[1]} buckets")
        print(f"  Expected: {row[2]} buckets")
        print(f"  Coverage: {row[3]:.1%}")

    client.close()
    print("\n✓ State-based heatmap rebuild complete!")


if __name__ == '__main__':
    main()
