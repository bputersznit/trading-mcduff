#!/usr/bin/env python3
"""
Import L2 Depth Parquet files to ClickHouse
Handles batch insertion with progress tracking
"""

import pandas as pd
import clickhouse_connect
from pathlib import Path
import logging
import sys
from typing import List

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [%(levelname)s] %(message)s'
)
logger = logging.getLogger(__name__)


def create_table_if_not_exists(client):
    """Create L2 depth table if it doesn't exist"""
    schema_file = Path(__file__).parent.parent / "sql" / "l2_depth_schema.sql"

    if schema_file.exists():
        schema_sql = schema_file.read_text()
        # Execute each statement
        for statement in schema_sql.split(';'):
            statement = statement.strip()
            if statement and not statement.startswith('--'):
                try:
                    client.command(statement)
                except Exception as e:
                    logger.debug(f"Statement execution (may already exist): {e}")
        logger.info("✓ Table schema verified")
    else:
        logger.warning("Schema file not found, table must exist")


def import_parquet_file(client, parquet_file: Path, batch_size: int = 100000):
    """Import a single Parquet file to ClickHouse"""
    logger.info(f"Reading {parquet_file.name}...")

    # Read Parquet
    df = pd.read_parquet(parquet_file)

    # Clean data
    df = df.dropna()  # Remove rows with NaN

    # Convert types for ClickHouse
    df['timestamp'] = pd.to_datetime(df['timestamp'])
    df['side'] = df['side'].astype(str)
    df['operation'] = df['operation'].astype(str)
    df['position'] = df['position'].astype(int)
    df['price'] = df['price'].astype(float)
    df['size'] = df['size'].astype(float)

    total_rows = len(df)
    logger.info(f"  Total rows: {total_rows:,}")

    # Insert in batches
    inserted = 0
    for start_idx in range(0, total_rows, batch_size):
        end_idx = min(start_idx + batch_size, total_rows)
        batch = df.iloc[start_idx:end_idx]

        try:
            client.insert_df(
                'l2_depth_raw',
                batch,
                settings={'async_insert': 1, 'wait_for_async_insert': 0}
            )
            inserted += len(batch)

            if inserted % (batch_size * 5) == 0 or inserted == total_rows:
                logger.info(f"  Inserted: {inserted:,} / {total_rows:,} ({inserted/total_rows*100:.1f}%)")

        except Exception as e:
            logger.error(f"  Failed to insert batch {start_idx}-{end_idx}: {e}")
            raise

    logger.info(f"✓ Imported {inserted:,} rows from {parquet_file.name}")
    return inserted


def main():
    # Configuration
    parquet_dir = Path("/tmp/l2_convert")

    logger.info("=== L2 Depth → ClickHouse Importer ===")

    # Connect to ClickHouse
    try:
        client = clickhouse_connect.get_client(
            host='localhost',
            port=8123,
            username='default',
            password=''
        )
        logger.info("✓ Connected to ClickHouse")
    except Exception as e:
        logger.error(f"Failed to connect to ClickHouse: {e}")
        sys.exit(1)

    # Create table if needed
    create_table_if_not_exists(client)

    # Find all Parquet files
    parquet_files = sorted(parquet_dir.glob("*/l2_depth_*.parquet"))

    if not parquet_files:
        logger.error(f"No Parquet files found in {parquet_dir}")
        sys.exit(1)

    logger.info(f"Found {len(parquet_files)} Parquet files\n")

    # Import each file
    total_imported = 0
    for parquet_file in parquet_files:
        try:
            rows = import_parquet_file(client, parquet_file)
            total_imported += rows
            logger.info("")
        except Exception as e:
            logger.error(f"Failed to import {parquet_file.name}: {e}")
            continue

    # Verify import
    result = client.query("SELECT COUNT(*), MIN(timestamp), MAX(timestamp) FROM l2_depth_raw")
    count, min_ts, max_ts = result.result_rows[0]

    logger.info("=== IMPORT COMPLETE ===")
    logger.info(f"Total rows in database: {count:,}")
    logger.info(f"Date range: {min_ts} → {max_ts}")
    logger.info(f"Rows imported this run: {total_imported:,}")

    # Sample query
    logger.info("\n=== Sample Data ===")
    sample = client.query("SELECT * FROM l2_depth_raw ORDER BY timestamp LIMIT 5")
    for row in sample.result_rows:
        logger.info(f"  {row}")


if __name__ == "__main__":
    main()
