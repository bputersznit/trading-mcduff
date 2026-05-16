#!/usr/bin/env python3
"""
rebuild_heatmap_worker_1S_hourly.py

Memory-efficient worker that processes dates in hourly chunks to avoid OOM.
Maintains order book state across chunks for accuracy.
"""

import clickhouse_connect
import pandas as pd
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
            self.book[price][side] += size
            self.orders[order_id] = {
                'price': price,
                'side': side,
                'size': size
            }
            self.add_sizes[price][side] += size
            self.event_counts[price][side] += 1

        elif action == 'C':  # Cancel order
            if order_id in self.orders:
                old_order = self.orders[order_id]
                old_price = old_order['price']
                old_side = old_order['side']
                old_size = old_order['size']

                self.book[old_price][old_side] = max(0, self.book[old_price][old_side] - old_size)
                del self.orders[order_id]

                self.cancel_sizes[old_price][old_side] += old_size
                self.event_counts[old_price][old_side] += 1
            else:
                self.book[price][side] = max(0, self.book[price][side] - size)
                self.cancel_sizes[price][side] += size
                self.event_counts[price][side] += 1

        elif action == 'M':  # Modify order
            if order_id in self.orders:
                old_order = self.orders[order_id]
                old_price = old_order['price']
                old_side = old_order['side']
                old_size = old_order['size']

                self.book[old_price][old_side] = max(0, self.book[old_price][old_side] - old_size)
                self.book[price][side] += size

                self.orders[order_id] = {
                    'price': price,
                    'side': side,
                    'size': size
                }

                self.modify_sizes[old_price][old_side] += old_size
                self.modify_sizes[price][side] += size
                self.event_counts[old_price][old_side] += 1
                if price != old_price:
                    self.event_counts[price][side] += 1
            else:
                self.book[price][side] += size
                self.orders[order_id] = {
                    'price': price,
                    'side': side,
                    'size': size
                }
                self.modify_sizes[price][side] += size
                self.event_counts[price][side] += 1

    def get_snapshot(self):
        """Get current state snapshot"""
        snapshot = []

        for price in self.book:
            bid_size = self.book[price]['B']
            ask_size = self.book[price]['A']
            bid_events = self.event_counts[price]['B']
            ask_events = self.event_counts[price]['A']

            if bid_events > 0 or ask_events > 0:
                snapshot.append({
                    'price': price,
                    'bid_size': bid_size,
                    'ask_size': ask_size,
                    'bid_events': bid_events,
                    'ask_events': ask_events,
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


def fetch_events_for_hour(client, trade_date_str, hour_start, hour_end):
    """Fetch MBO events for a specific hour"""
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
      AND ts_event >= toDateTime64('{hour_start}', 6)
      AND ts_event < toDateTime64('{hour_end}', 6)
      AND action IN ('A', 'C', 'M')
    ORDER BY ts_event, sequence
    """

    return client.query_df(query)


def rebuild_heatmap_for_date_hourly(client, trade_date, worker_id):
    """Rebuild heatmap for a single trading date, processing hourly chunks"""

    # Convert to proper date format
    if hasattr(trade_date, 'strftime'):
        trade_date_str = trade_date.strftime('%Y-%m-%d')
        trade_date_obj = trade_date.date() if hasattr(trade_date, 'date') else trade_date
    else:
        trade_date_str = str(trade_date).split()[0]  # Handle "2025-09-21 00:00:00" format
        trade_date_obj = datetime.strptime(trade_date_str, '%Y-%m-%d').date()

    print(f"[W{worker_id}] Rebuilding {trade_date_str} (hourly chunks)...")

    # Initialize order book (maintains state across hours)
    order_book = OrderBookState()
    total_rows_inserted = 0
    total_events_processed = 0
    BATCH_SIZE = 100000

    # Process each hour of the day
    base_date = datetime.strptime(trade_date_str, '%Y-%m-%d')

    for hour in range(24):
        hour_start = base_date + timedelta(hours=hour)
        hour_end = hour_start + timedelta(hours=1)

        print(f"[W{worker_id}]   Hour {hour:02d}:00 - Fetching events...")
        df = fetch_events_for_hour(client, trade_date_str,
                                   hour_start.strftime('%Y-%m-%d %H:%M:%S'),
                                   hour_end.strftime('%Y-%m-%d %H:%M:%S'))

        if df.empty:
            print(f"[W{worker_id}]   Hour {hour:02d}:00 - No events")
            continue

        print(f"[W{worker_id}]   Hour {hour:02d}:00 - Processing {len(df):,} events...")
        total_events_processed += len(df)

        # Convert timestamp
        df['ts_event'] = pd.to_datetime(df['ts_event'], utc=True)

        # Determine time range (1S buckets)
        start_time = df['ts_event'].min().floor('1s')
        end_time = df['ts_event'].max().ceil('1s')
        time_buckets = pd.date_range(start=start_time, end=end_time, freq='1s')

        # Process events
        heatmap_rows = []
        event_idx = 0
        total_events = len(df)

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

            # Batch insert
            if len(heatmap_rows) >= BATCH_SIZE:
                heatmap_df = pd.DataFrame(heatmap_rows)
                client.insert_df('BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S', heatmap_df)
                total_rows_inserted += len(heatmap_rows)
                heatmap_rows = []

        # Insert remaining rows from this hour
        if heatmap_rows:
            heatmap_df = pd.DataFrame(heatmap_rows)
            client.insert_df('BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S', heatmap_df)
            total_rows_inserted += len(heatmap_rows)

        print(f"[W{worker_id}]   Hour {hour:02d}:00 - Complete ({total_rows_inserted:,} total rows so far)")

        # Free memory
        del df

    print(f"[W{worker_id}]   Complete: {total_rows_inserted:,} heatmap rows, {len(order_book.orders):,} active orders, {total_events_processed:,} events")

    return total_rows_inserted


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dates', required=True, help='Comma-separated list of dates')
    parser.add_argument('--worker-id', required=True, type=int, help='Worker ID')
    args = parser.parse_args()

    dates = args.dates.split(',')
    worker_id = args.worker_id

    print(f"[W{worker_id}] Starting hourly-chunked worker for {len(dates)} dates")

    client = get_client()
    total_rows = 0

    for date_str in dates:
        date_str = date_str.strip().split()[0]  # Handle "2025-10-13 00:00:00" format
        rows = rebuild_heatmap_for_date_hourly(client, date_str, worker_id)
        total_rows += rows

    client.close()
    print(f"[W{worker_id}] Worker complete: {total_rows:,} total rows")


if __name__ == '__main__':
    main()
