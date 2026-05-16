#!/usr/bin/env python3
"""
CG L2 CSV to Parquet Converter
Converts chunked L2 market depth CSV files to compressed Parquet format
Validates conversion and optionally deletes source CSVs
"""

import pandas as pd
import pyarrow as pa
import pyarrow.parquet as pq
from pathlib import Path
import logging
from typing import List, Tuple
import sys

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [%(levelname)s] %(message)s'
)
logger = logging.getLogger(__name__)


def find_l2_date_folders(base_path: Path) -> List[Path]:
    """Find all date folders containing L2 chunks"""
    if not base_path.exists():
        logger.error(f"Base path does not exist: {base_path}")
        return []

    date_folders = [d for d in base_path.iterdir() if d.is_dir() and d.name.startswith('2026-')]
    logger.info(f"Found {len(date_folders)} date folders")
    return sorted(date_folders)


def find_csv_chunks(date_folder: Path) -> List[Path]:
    """Find all CSV chunks in a date folder"""
    chunks = sorted(date_folder.glob('l2_chunk_*.csv'))
    return chunks


def convert_date_folder(date_folder: Path, delete_csv: bool = False) -> Tuple[bool, int, int]:
    """
    Convert all CSV chunks in a date folder to single Parquet file

    Returns:
        (success, total_rows, csv_size_mb)
    """
    date_name = date_folder.name
    logger.info(f"Processing {date_name}...")

    # Find all chunks
    chunks = find_csv_chunks(date_folder)
    if not chunks:
        logger.warning(f"  No CSV chunks found in {date_name}")
        return False, 0, 0

    logger.info(f"  Found {len(chunks)} CSV chunks")

    # Read all chunks into single DataFrame
    dfs = []
    total_csv_size = 0

    for chunk_file in chunks:
        try:
            df = pd.read_csv(
                chunk_file,
                dtype={
                    'timestamp': str,
                    'side': str,
                    'operation': str,
                    'position': int,
                    'price': float,
                    'size': int
                }
            )
            dfs.append(df)
            total_csv_size += chunk_file.stat().st_size

        except Exception as e:
            logger.error(f"  Failed to read {chunk_file.name}: {e}")
            return False, 0, 0

    # Combine all chunks
    combined_df = pd.concat(dfs, ignore_index=True)
    total_rows = len(combined_df)

    logger.info(f"  Combined {total_rows:,} rows from {len(chunks)} chunks")

    # Convert timestamp to datetime
    combined_df['timestamp'] = pd.to_datetime(combined_df['timestamp'])

    # Write to Parquet with compression
    parquet_file = date_folder / f"l2_depth_{date_name}.parquet"

    try:
        combined_df.to_parquet(
            parquet_file,
            engine='pyarrow',
            compression='snappy',
            index=False
        )

        parquet_size = parquet_file.stat().st_size
        compression_ratio = total_csv_size / parquet_size if parquet_size > 0 else 0

        logger.info(f"  ✓ Written: {parquet_file.name}")
        logger.info(f"  CSV size: {total_csv_size / 1024 / 1024:.1f} MB")
        logger.info(f"  Parquet size: {parquet_size / 1024 / 1024:.1f} MB")
        logger.info(f"  Compression ratio: {compression_ratio:.1f}x")

        # Verify row count
        verify_df = pd.read_parquet(parquet_file)
        if len(verify_df) != total_rows:
            logger.error(f"  ✗ Row count mismatch! CSV: {total_rows}, Parquet: {len(verify_df)}")
            return False, total_rows, total_csv_size // (1024 * 1024)

        logger.info(f"  ✓ Verified: {len(verify_df):,} rows")

        # Delete CSV chunks if requested
        if delete_csv:
            for chunk_file in chunks:
                try:
                    chunk_file.unlink()
                    logger.debug(f"  Deleted: {chunk_file.name}")
                except Exception as e:
                    logger.warning(f"  Failed to delete {chunk_file.name}: {e}")

            logger.info(f"  ✓ Deleted {len(chunks)} CSV chunks")

        return True, total_rows, total_csv_size // (1024 * 1024)

    except Exception as e:
        logger.error(f"  Failed to write Parquet: {e}")
        return False, total_rows, total_csv_size // (1024 * 1024)


def main():
    # Configuration
    base_path = Path(r"C:\Users\Administrator\Documents\CG_L2_Capture")
    delete_csv = True  # Set to False to keep CSV files

    logger.info("=== CG L2 CSV → Parquet Converter ===")
    logger.info(f"Base path: {base_path}")
    logger.info(f"Delete CSV after conversion: {delete_csv}")
    logger.info("")

    # Find all date folders
    date_folders = find_l2_date_folders(base_path)

    if not date_folders:
        logger.error("No date folders found")
        sys.exit(1)

    # Process each date folder
    total_rows = 0
    total_csv_mb = 0
    success_count = 0

    for date_folder in date_folders:
        success, rows, csv_mb = convert_date_folder(date_folder, delete_csv)
        if success:
            success_count += 1
            total_rows += rows
            total_csv_mb += csv_mb
        logger.info("")

    # Summary
    logger.info("=== CONVERSION COMPLETE ===")
    logger.info(f"Processed: {len(date_folders)} date folders")
    logger.info(f"Successful: {success_count}")
    logger.info(f"Total rows: {total_rows:,}")
    logger.info(f"Total CSV size: {total_csv_mb} MB")

    if success_count < len(date_folders):
        logger.warning(f"Failed: {len(date_folders) - success_count} folders")
        sys.exit(1)


if __name__ == "__main__":
    main()
