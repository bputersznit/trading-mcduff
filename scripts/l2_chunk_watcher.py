#!/usr/bin/env python3
"""
L2 Chunk Watcher - Monitors NT8 output and processes chunks
Pipeline: CSV → Parquet → ClickHouse → Delete CSV
Designed for high-throughput L2 capture
"""

import os
import sys
import time
import pandas as pd
import pyarrow as pa
import pyarrow.parquet as pq
import clickhouse_connect
from pathlib import Path
from datetime import datetime
import logging
from typing import Optional

# Setup logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=[
        logging.FileHandler('l2_watcher.log'),
        logging.StreamHandler(sys.stdout)
    ]
)
logger = logging.getLogger(__name__)


class L2ChunkProcessor:
    """Processes L2 chunks: CSV → Parquet → ClickHouse → Cleanup"""

    def __init__(self, watch_folder: str, parquet_folder: str,
                 ch_host: str = 'localhost', ch_port: int = 8123):
        self.watch_folder = Path(watch_folder)
        self.parquet_folder = Path(parquet_folder)
        self.parquet_folder.mkdir(parents=True, exist_ok=True)

        # ClickHouse connection
        self.ch_client = clickhouse_connect.get_client(
            host=ch_host,
            port=ch_port,
            username='default',
            password='unlucky-strange',
            database='default'
        )

        self.processed_chunks = set()
        self.stats = {
            'chunks_processed': 0,
            'total_rows': 0,
            'total_csv_mb': 0,
            'total_parquet_mb': 0,
            'errors': 0
        }

        logger.info(f"L2 Chunk Processor initialized")
        logger.info(f"Watch folder: {self.watch_folder}")
        logger.info(f"Parquet folder: {self.parquet_folder}")
        logger.info(f"ClickHouse: {ch_host}:{ch_port}")

        self._ensure_clickhouse_table()

    def _ensure_clickhouse_table(self):
        """Create ClickHouse table if not exists"""
        create_table_sql = """
        CREATE TABLE IF NOT EXISTS BM_MNQ_L2_RAW
        (
            timestamp DateTime64(3, 'America/New_York'),
            trade_date Date,
            side FixedString(1),
            operation FixedString(1),
            position UInt8,
            price Float64,
            size UInt64
        )
        ENGINE = MergeTree()
        PARTITION BY trade_date
        ORDER BY (trade_date, timestamp, side, position)
        SETTINGS index_granularity = 8192
        """

        try:
            self.ch_client.command(create_table_sql)
            logger.info("ClickHouse table BM_MNQ_L2_RAW ready")
        except Exception as e:
            logger.error(f"Error creating ClickHouse table: {e}")
            raise

    def find_completed_chunks(self) -> list:
        """Find CSV chunks that are complete (not actively being written)"""
        if not self.watch_folder.exists():
            return []

        completed = []

        # Search all date folders
        for date_folder in self.watch_folder.glob("20*"):
            if not date_folder.is_dir():
                continue

            # Find CSV files
            for csv_file in date_folder.glob("l2_chunk_*.csv"):
                chunk_id = str(csv_file)

                # Skip if already processed
                if chunk_id in self.processed_chunks:
                    continue

                # Check if file is stable (not being written)
                # Compare file size at 1 second interval
                try:
                    size1 = csv_file.stat().st_size
                    time.sleep(1)
                    size2 = csv_file.stat().st_size

                    if size1 == size2 and size1 > 100:  # Stable and non-empty
                        completed.append(csv_file)
                except Exception as e:
                    logger.warning(f"Error checking file stability: {csv_file}: {e}")

        return completed

    def process_chunk(self, csv_path: Path) -> bool:
        """Process a single chunk: CSV → Parquet → ClickHouse → Delete"""
        chunk_name = csv_path.name
        logger.info(f"Processing chunk: {chunk_name}")

        start_time = time.time()

        try:
            # 1. Read CSV
            df = pd.read_csv(csv_path, dtype={
                'side': 'category',
                'operation': 'category',
                'position': 'uint8',
                'price': 'float64',
                'size': 'uint64'
            })

            if len(df) == 0:
                logger.warning(f"Empty chunk: {chunk_name}")
                return False

            # Convert timestamp
            df['timestamp'] = pd.to_datetime(df['timestamp'])
            df['trade_date'] = df['timestamp'].dt.date

            csv_size_mb = csv_path.stat().st_size / (1024 * 1024)
            row_count = len(df)

            logger.info(f"  Loaded {row_count:,} rows ({csv_size_mb:.2f} MB)")

            # 2. Save to Parquet
            parquet_path = self.parquet_folder / csv_path.parent.name / f"{csv_path.stem}.parquet"
            parquet_path.parent.mkdir(parents=True, exist_ok=True)

            df.to_parquet(
                parquet_path,
                engine='pyarrow',
                compression='zstd',
                index=False
            )

            parquet_size_mb = parquet_path.stat().st_size / (1024 * 1024)
            compression_ratio = csv_size_mb / parquet_size_mb if parquet_size_mb > 0 else 0

            logger.info(f"  Parquet saved: {parquet_size_mb:.2f} MB ({compression_ratio:.1f}x compression)")

            # 3. Load to ClickHouse
            self.ch_client.insert_df('BM_MNQ_L2_RAW', df)
            logger.info(f"  Loaded to ClickHouse: {row_count:,} rows")

            # 4. Delete CSV
            csv_path.unlink()
            logger.info(f"  CSV deleted: {chunk_name}")

            # Update stats
            self.stats['chunks_processed'] += 1
            self.stats['total_rows'] += row_count
            self.stats['total_csv_mb'] += csv_size_mb
            self.stats['total_parquet_mb'] += parquet_size_mb

            elapsed = time.time() - start_time
            logger.info(f"  ✓ Chunk processed in {elapsed:.2f}s ({row_count/elapsed:.0f} rows/sec)")

            # Mark as processed
            self.processed_chunks.add(str(csv_path))

            return True

        except Exception as e:
            logger.error(f"Error processing chunk {chunk_name}: {e}", exc_info=True)
            self.stats['errors'] += 1
            return False

    def print_stats(self):
        """Print processing statistics"""
        logger.info("="*70)
        logger.info("L2 CHUNK PROCESSOR STATISTICS")
        logger.info("="*70)
        logger.info(f"  Chunks processed: {self.stats['chunks_processed']}")
        logger.info(f"  Total rows: {self.stats['total_rows']:,}")
        logger.info(f"  CSV total: {self.stats['total_csv_mb']:.1f} MB")
        logger.info(f"  Parquet total: {self.stats['total_parquet_mb']:.1f} MB")

        if self.stats['total_parquet_mb'] > 0:
            ratio = self.stats['total_csv_mb'] / self.stats['total_parquet_mb']
            logger.info(f"  Avg compression: {ratio:.1f}x")

        logger.info(f"  Errors: {self.stats['errors']}")
        logger.info("="*70)

    def watch_and_process(self, poll_interval: int = 5):
        """Main loop: watch for chunks and process them"""
        logger.info("Starting watch loop...")
        logger.info(f"Polling every {poll_interval} seconds")
        logger.info("Press Ctrl+C to stop")

        try:
            while True:
                # Find completed chunks
                chunks = self.find_completed_chunks()

                if chunks:
                    logger.info(f"Found {len(chunks)} completed chunk(s)")

                    for chunk_path in chunks:
                        self.process_chunk(chunk_path)

                    # Print stats after each batch
                    self.print_stats()

                # Wait before next poll
                time.sleep(poll_interval)

        except KeyboardInterrupt:
            logger.info("\nShutdown requested by user")
            self.print_stats()
        except Exception as e:
            logger.error(f"Fatal error in watch loop: {e}", exc_info=True)
            raise


def main():
    """Main entry point"""
    import argparse

    parser = argparse.ArgumentParser(description='L2 Chunk Watcher and Processor')
    parser.add_argument(
        '--watch-folder',
        default=os.path.expanduser('~/Documents/CG_L2_Capture'),
        help='Folder to watch for NT8 output'
    )
    parser.add_argument(
        '--parquet-folder',
        default='./l2_parquet',
        help='Folder to store Parquet files'
    )
    parser.add_argument(
        '--poll-interval',
        type=int,
        default=5,
        help='Seconds between polls'
    )
    parser.add_argument(
        '--ch-host',
        default='localhost',
        help='ClickHouse host'
    )
    parser.add_argument(
        '--ch-port',
        type=int,
        default=8123,
        help='ClickHouse port'
    )

    args = parser.parse_args()

    # Create processor
    processor = L2ChunkProcessor(
        watch_folder=args.watch_folder,
        parquet_folder=args.parquet_folder,
        ch_host=args.ch_host,
        ch_port=args.ch_port
    )

    # Start watching
    processor.watch_and_process(poll_interval=args.poll_interval)


if __name__ == '__main__':
    main()
