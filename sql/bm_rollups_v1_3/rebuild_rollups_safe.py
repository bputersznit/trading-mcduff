#!/usr/bin/env python3
"""
rebuild_rollups_safe.py

Safely rebuild multi-scale rollup tables date-by-date to avoid OOM.
Processes: 100MS -> 1S -> 5S -> 30S -> 1M -> 5M
"""

import clickhouse_connect
from datetime import datetime, timedelta
import sys


def get_client():
    return clickhouse_connect.get_client(
        host='localhost',
        port=8123,
        username='default',
        password='unlucky-strange',
        database='default',
    )


def get_trading_dates(client):
    """Get all trading dates from 1S heatmap"""
    result = client.query("""
        SELECT DISTINCT trade_date
        FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
        ORDER BY trade_date
    """)
    return [row[0] for row in result.result_rows]


def create_canonical_function(client):
    """Create symbol normalization function"""
    print("Creating canonical symbol function...")
    client.command("""
        CREATE OR REPLACE FUNCTION canonicalSymbol AS (s) -> multiIf(
            s = 'MNQ', 'MNQZ5',
            s = 'MNQZ5', 'MNQZ5',
            s
        )
    """)


def setup_heatmap_5s_table(client):
    """Create empty 5S heatmap table"""
    print("Setting up 5S heatmap table...")

    client.command("DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S")

    client.command("""
        CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
        (
            trade_date Date,
            ts_bucket DateTime64(3, 'UTC'),
            ts_et DateTime('America/New_York'),
            symbol String,
            price Float64,
            bid_add_size UInt64,
            ask_add_size UInt64,
            bid_cancel_size UInt64,
            ask_cancel_size UInt64,
            bid_modify_size UInt64,
            ask_modify_size UInt64,
            bid_trade_size UInt64,
            ask_trade_size UInt64,
            bid_event_count UInt64,
            ask_event_count UInt64,
            total_event_count UInt64,
            bid_liquidity_event_size UInt64,
            ask_liquidity_event_size UInt64,
            total_liquidity_event_size UInt64,
            net_liquidity_event_delta Int64,
            heatmap_proxy_value UInt64,
            rth_flag UInt8
        )
        ENGINE = MergeTree
        PARTITION BY trade_date
        ORDER BY (trade_date, ts_bucket, symbol, price)
    """)


def setup_heatmap_30s_table(client):
    """Create empty 30S heatmap table"""
    print("Setting up 30S heatmap table...")

    client.command("DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S")

    client.command("""
        CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S
        (
            trade_date Date,
            ts_bucket DateTime64(3, 'UTC'),
            ts_et DateTime('America/New_York'),
            symbol String,
            price Float64,
            bid_add_size UInt64,
            ask_add_size UInt64,
            bid_cancel_size UInt64,
            ask_cancel_size UInt64,
            bid_modify_size UInt64,
            ask_modify_size UInt64,
            bid_trade_size UInt64,
            ask_trade_size UInt64,
            bid_event_count UInt64,
            ask_event_count UInt64,
            total_event_count UInt64,
            bid_liquidity_event_size UInt64,
            ask_liquidity_event_size UInt64,
            total_liquidity_event_size UInt64,
            net_liquidity_event_delta Int64,
            heatmap_proxy_value UInt64,
            rth_flag UInt8
        )
        ENGINE = MergeTree
        PARTITION BY trade_date
        ORDER BY (trade_date, ts_bucket, symbol, price)
    """)


def setup_heatmap_1m_table(client):
    """Create empty 1M heatmap table"""
    print("Setting up 1M heatmap table...")

    client.command("DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M")

    client.command("""
        CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M
        (
            trade_date Date,
            ts_bucket DateTime64(3, 'UTC'),
            ts_et DateTime('America/New_York'),
            symbol String,
            price Float64,
            bid_add_size UInt64,
            ask_add_size UInt64,
            bid_cancel_size UInt64,
            ask_cancel_size UInt64,
            bid_modify_size UInt64,
            ask_modify_size UInt64,
            bid_trade_size UInt64,
            ask_trade_size UInt64,
            bid_event_count UInt64,
            ask_event_count UInt64,
            total_event_count UInt64,
            bid_liquidity_event_size UInt64,
            ask_liquidity_event_size UInt64,
            total_liquidity_event_size UInt64,
            net_liquidity_event_delta Int64,
            heatmap_proxy_value UInt64,
            rth_flag UInt8
        )
        ENGINE = MergeTree
        PARTITION BY trade_date
        ORDER BY (trade_date, ts_bucket, symbol, price)
    """)


def setup_heatmap_5m_table(client):
    """Create empty 5M heatmap table"""
    print("Setting up 5M heatmap table...")

    client.command("DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M")

    client.command("""
        CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M
        (
            trade_date Date,
            ts_bucket DateTime64(3, 'UTC'),
            ts_et DateTime('America/New_York'),
            symbol String,
            price Float64,
            bid_add_size UInt64,
            ask_add_size UInt64,
            bid_cancel_size UInt64,
            ask_cancel_size UInt64,
            bid_modify_size UInt64,
            ask_modify_size UInt64,
            bid_trade_size UInt64,
            ask_trade_size UInt64,
            bid_event_count UInt64,
            ask_event_count UInt64,
            total_event_count UInt64,
            bid_liquidity_event_size UInt64,
            ask_liquidity_event_size UInt64,
            total_liquidity_event_size UInt64,
            net_liquidity_event_delta Int64,
            heatmap_proxy_value UInt64,
            rth_flag UInt8
        )
        ENGINE = MergeTree
        PARTITION BY trade_date
        ORDER BY (trade_date, ts_bucket, symbol, price)
    """)


def rollup_heatmap_1s_to_5s(client, trade_date):
    """Roll up 1S -> 5S for a single date"""
    print(f"  Rolling up 1S -> 5S for {trade_date}...")

    client.command(f"""
        INSERT INTO BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
        SELECT
            trade_date,
            toStartOfInterval(ts_bucket, toIntervalSecond(5)) AS ts_bucket,
            toStartOfInterval(ts_et, toIntervalSecond(5)) AS ts_et,
            canonicalSymbol(symbol) AS symbol,
            price,
            sum(bid_add_size) AS bid_add_size,
            sum(ask_add_size) AS ask_add_size,
            sum(bid_cancel_size) AS bid_cancel_size,
            sum(ask_cancel_size) AS ask_cancel_size,
            sum(bid_modify_size) AS bid_modify_size,
            sum(ask_modify_size) AS ask_modify_size,
            sum(bid_trade_size) AS bid_trade_size,
            sum(ask_trade_size) AS ask_trade_size,
            sum(bid_event_count) AS bid_event_count,
            sum(ask_event_count) AS ask_event_count,
            sum(total_event_count) AS total_event_count,
            sum(bid_liquidity_event_size) AS bid_liquidity_event_size,
            sum(ask_liquidity_event_size) AS ask_liquidity_event_size,
            sum(total_liquidity_event_size) AS total_liquidity_event_size,
            sum(net_liquidity_event_delta) AS net_liquidity_event_delta,
            sum(heatmap_proxy_value) AS heatmap_proxy_value,
            max(rth_flag) AS rth_flag
        FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
        WHERE trade_date = toDate('{trade_date}')
        GROUP BY trade_date, ts_bucket, ts_et, symbol, price
    """)


def rollup_heatmap_5s_to_30s(client, trade_date):
    """Roll up 5S -> 30S for a single date"""
    print(f"  Rolling up 5S -> 30S for {trade_date}...")

    client.command(f"""
        INSERT INTO BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S
        SELECT
            trade_date,
            toStartOfInterval(ts_bucket, toIntervalSecond(30)) AS ts_bucket,
            toStartOfInterval(ts_et, toIntervalSecond(30)) AS ts_et,
            symbol,
            price,
            sum(bid_add_size) AS bid_add_size,
            sum(ask_add_size) AS ask_add_size,
            sum(bid_cancel_size) AS bid_cancel_size,
            sum(ask_cancel_size) AS ask_cancel_size,
            sum(bid_modify_size) AS bid_modify_size,
            sum(ask_modify_size) AS ask_modify_size,
            sum(bid_trade_size) AS bid_trade_size,
            sum(ask_trade_size) AS ask_trade_size,
            sum(bid_event_count) AS bid_event_count,
            sum(ask_event_count) AS ask_event_count,
            sum(total_event_count) AS total_event_count,
            sum(bid_liquidity_event_size) AS bid_liquidity_event_size,
            sum(ask_liquidity_event_size) AS ask_liquidity_event_size,
            sum(total_liquidity_event_size) AS total_liquidity_event_size,
            sum(net_liquidity_event_delta) AS net_liquidity_event_delta,
            sum(heatmap_proxy_value) AS heatmap_proxy_value,
            max(rth_flag) AS rth_flag
        FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
        WHERE trade_date = toDate('{trade_date}')
        GROUP BY trade_date, ts_bucket, ts_et, symbol, price
    """)


def rollup_heatmap_30s_to_1m(client, trade_date):
    """Roll up 30S -> 1M for a single date"""
    print(f"  Rolling up 30S -> 1M for {trade_date}...")

    client.command(f"""
        INSERT INTO BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M
        SELECT
            trade_date,
            toStartOfInterval(ts_bucket, toIntervalMinute(1)) AS ts_bucket,
            toStartOfInterval(ts_et, toIntervalMinute(1)) AS ts_et,
            symbol,
            price,
            sum(bid_add_size) AS bid_add_size,
            sum(ask_add_size) AS ask_add_size,
            sum(bid_cancel_size) AS bid_cancel_size,
            sum(ask_cancel_size) AS ask_cancel_size,
            sum(bid_modify_size) AS bid_modify_size,
            sum(ask_modify_size) AS ask_modify_size,
            sum(bid_trade_size) AS bid_trade_size,
            sum(ask_trade_size) AS ask_trade_size,
            sum(bid_event_count) AS bid_event_count,
            sum(ask_event_count) AS ask_event_count,
            sum(total_event_count) AS total_event_count,
            sum(bid_liquidity_event_size) AS bid_liquidity_event_size,
            sum(ask_liquidity_event_size) AS ask_liquidity_event_size,
            sum(total_liquidity_event_size) AS total_liquidity_event_size,
            sum(net_liquidity_event_delta) AS net_liquidity_event_delta,
            sum(heatmap_proxy_value) AS heatmap_proxy_value,
            max(rth_flag) AS rth_flag
        FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S
        WHERE trade_date = toDate('{trade_date}')
        GROUP BY trade_date, ts_bucket, ts_et, symbol, price
    """)


def rollup_heatmap_1m_to_5m(client, trade_date):
    """Roll up 1M -> 5M for a single date"""
    print(f"  Rolling up 1M -> 5M for {trade_date}...")

    client.command(f"""
        INSERT INTO BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5M
        SELECT
            trade_date,
            toStartOfInterval(ts_bucket, toIntervalMinute(5)) AS ts_bucket,
            toStartOfInterval(ts_et, toIntervalMinute(5)) AS ts_et,
            symbol,
            price,
            sum(bid_add_size) AS bid_add_size,
            sum(ask_add_size) AS ask_add_size,
            sum(bid_cancel_size) AS bid_cancel_size,
            sum(ask_cancel_size) AS ask_cancel_size,
            sum(bid_modify_size) AS bid_modify_size,
            sum(ask_modify_size) AS ask_modify_size,
            sum(bid_trade_size) AS bid_trade_size,
            sum(ask_trade_size) AS ask_trade_size,
            sum(bid_event_count) AS bid_event_count,
            sum(ask_event_count) AS ask_event_count,
            sum(total_event_count) AS total_event_count,
            sum(bid_liquidity_event_size) AS bid_liquidity_event_size,
            sum(ask_liquidity_event_size) AS ask_liquidity_event_size,
            sum(total_liquidity_event_size) AS total_liquidity_event_size,
            sum(net_liquidity_event_delta) AS net_liquidity_event_delta,
            sum(heatmap_proxy_value) AS heatmap_proxy_value,
            max(rth_flag) AS rth_flag
        FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1M
        WHERE trade_date = toDate('{trade_date}')
        GROUP BY trade_date, ts_bucket, ts_et, symbol, price
    """)


def main():
    print("=== Safe Multi-Scale Rollup Builder ===\n")

    client = get_client()

    # Get dates to process
    print("Fetching trading dates...")
    dates = get_trading_dates(client)
    print(f"Found {len(dates)} trading dates: {dates[0]} to {dates[-1]}\n")

    # Setup
    create_canonical_function(client)
    setup_heatmap_5s_table(client)
    setup_heatmap_30s_table(client)
    setup_heatmap_1m_table(client)
    setup_heatmap_5m_table(client)

    print("\nProcessing dates...")

    # Process each date
    for idx, date in enumerate(dates, 1):
        print(f"\n[{idx}/{len(dates)}] Processing {date}...")

        try:
            rollup_heatmap_1s_to_5s(client, date)
            rollup_heatmap_5s_to_30s(client, date)
            rollup_heatmap_30s_to_1m(client, date)
            rollup_heatmap_1m_to_5m(client, date)
            print(f"  ✓ Complete: {date}")
        except Exception as e:
            print(f"  ✗ Error on {date}: {e}")
            sys.exit(1)

    # Final counts
    print("\n=== Final Row Counts ===")
    for table in ['5S', '30S', '1M', '5M']:
        result = client.query(f"SELECT count(*) FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_{table}")
        count = result.result_rows[0][0]
        print(f"  {table}: {count:,} rows")

    print("\n✓ All rollups complete!")
    client.close()


if __name__ == '__main__':
    main()
