#!/usr/bin/env python3
"""
rebuild_heatmap_parallel_1S.py

Parallel orchestrator for state-based 1S heatmap rebuild.
Launches multiple worker processes to rebuild different date ranges simultaneously.
"""

import clickhouse_connect
import subprocess
import sys
import os
from datetime import datetime
import time

def get_client():
    return clickhouse_connect.get_client(
        host='localhost',
        port=8123,
        username='default',
        password='unlucky-strange',
        database='default',
    )

def get_trading_dates(client):
    """Get all trading dates from MBO events"""
    dates_df = client.query_df("""
        SELECT DISTINCT trade_date
        FROM CG_mnq_mbo_events_clean
        ORDER BY trade_date
    """)
    return [str(d) for d in dates_df['trade_date'].tolist()]

def setup_table(client):
    """Setup the 1S table (backup, recreate)"""
    print("Setting up 1S heatmap table...")

    # Backup existing
    client.command("DROP TABLE IF EXISTS BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S_EVENT_BASED_BACKUP")
    client.command("""
        CREATE TABLE BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S_EVENT_BASED_BACKUP
        ENGINE = MergeTree
        PARTITION BY trade_date
        ORDER BY (trade_date, ts_bucket, symbol, price)
        AS SELECT * FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
    """)
    print("✓ Backup created")

    # Recreate target table
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

def launch_worker(worker_id, dates, log_dir):
    """Launch a worker process to rebuild specific dates"""
    dates_str = ','.join(dates)
    log_file = f"{log_dir}/worker_{worker_id}.log"

    cmd = [
        'python3', '-u',
        'rebuild_heatmap_worker_1S.py',
        '--dates', dates_str,
        '--worker-id', str(worker_id)
    ]

    with open(log_file, 'w') as f:
        proc = subprocess.Popen(
            cmd,
            stdout=f,
            stderr=subprocess.STDOUT,
            cwd=os.getcwd()
        )

    return proc, log_file

def main():
    print("=== BM_MNQ Parallel Heatmap Rebuild 1S ===")
    print("(Multi-process state-based rebuild)\n")

    # Get dates
    client = get_client()
    dates = get_trading_dates(client)
    print(f"Found {len(dates)} trading dates\n")

    # Setup table
    setup_table(client)
    client.close()

    # Configure parallelism
    NUM_WORKERS = 14  # Use 14 cores
    dates_per_worker = len(dates) // NUM_WORKERS
    remainder = len(dates) % NUM_WORKERS

    # Split dates among workers
    workers = []
    date_idx = 0
    log_dir = f"parallel_rebuild_logs_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
    os.makedirs(log_dir, exist_ok=True)

    print(f"Launching {NUM_WORKERS} parallel workers...")
    print(f"Logs: {log_dir}/\n")

    for worker_id in range(NUM_WORKERS):
        # Give extra date to first 'remainder' workers
        chunk_size = dates_per_worker + (1 if worker_id < remainder else 0)
        worker_dates = dates[date_idx:date_idx + chunk_size]
        date_idx += chunk_size

        if not worker_dates:
            break

        proc, log_file = launch_worker(worker_id, worker_dates, log_dir)
        workers.append((worker_id, proc, worker_dates, log_file))
        print(f"Worker {worker_id}: {len(worker_dates)} dates ({worker_dates[0]} to {worker_dates[-1]}) -> {log_file}")

    print(f"\n{len(workers)} workers launched\n")
    print("Monitor progress:")
    print(f"  tail -f {log_dir}/worker_*.log")
    print(f"  clickhouse-client --query=\"SELECT count(DISTINCT trade_date) FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S\"")
    print("\nWaiting for completion...")

    # Wait for all workers
    start_time = time.time()
    completed = []

    while len(completed) < len(workers):
        time.sleep(10)
        for worker_id, proc, worker_dates, log_file in workers:
            if worker_id not in completed:
                if proc.poll() is not None:
                    completed.append(worker_id)
                    elapsed = time.time() - start_time
                    print(f"Worker {worker_id} finished ({len(completed)}/{len(workers)}) - {elapsed/60:.1f}m elapsed")

    total_time = time.time() - start_time
    print(f"\n✓ All workers complete in {total_time/60:.1f} minutes")

    # Final stats
    client = get_client()
    result = client.query("""
        SELECT
            count(DISTINCT trade_date) as dates,
            formatReadableQuantity(count()) as rows,
            formatReadableSize(sum(data_compressed_bytes)) as size
        FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S,
             (SELECT sum(data_compressed_bytes) as data_compressed_bytes
              FROM system.parts
              WHERE table = 'BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S' AND active)
    """)

    for row in result.result_rows:
        print(f"\nFinal stats:")
        print(f"  Dates: {row[0]}")
        print(f"  Rows: {row[1]}")
        print(f"  Size: {row[2]}")

    client.close()

if __name__ == '__main__':
    main()
