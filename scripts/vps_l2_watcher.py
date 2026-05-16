#!/usr/bin/env python3
"""
VPS L2 CSV Watcher - Runs on Windows VPS
Monitors L2 capture folder for completed CSV chunks
Converts to Parquet, deletes CSV, transfers to Ubuntu, deletes Parquet
"""

import time
import pandas as pd
from pathlib import Path
import logging
import subprocess
import hashlib
from datetime import datetime

# Configuration
L2_CAPTURE_PATH = Path(r"C:\Users\Administrator\Documents\CG_L2_Capture")
RCLONE_REMOTE = "vps"  # Change to your Ubuntu rclone remote name
RCLONE_DEST = f"{RCLONE_REMOTE}:/home/bernard/trading4/CG_MNQ_MarketReplayLab/data/l2_parquet/"
CHECK_INTERVAL = 10  # seconds between scans
MIN_FILE_AGE = 5  # seconds before considering file "complete"

# Logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [%(levelname)s] %(message)s',
    handlers=[
        logging.FileHandler('l2_watcher.log'),
        logging.StreamHandler()
    ]
)
logger = logging.getLogger(__name__)

# Track processed files
processed_files = set()


def is_file_complete(csv_file: Path, min_age_seconds: int = 5) -> bool:
    """Check if file is old enough to be considered complete"""
    try:
        age = time.time() - csv_file.stat().st_mtime
        return age >= min_age_seconds
    except:
        return False


def convert_csv_to_parquet(csv_file: Path) -> Path:
    """Convert CSV chunk to Parquet"""
    logger.info(f"Converting {csv_file.name}...")

    # Read CSV
    df = pd.read_csv(
        csv_file,
        dtype={
            'timestamp': str,
            'side': str,
            'operation': str,
            'position': int,
            'price': float,
            'size': int
        }
    )

    # Clean and validate
    original_rows = len(df)
    df = df.dropna()

    if len(df) < original_rows:
        logger.warning(f"  Dropped {original_rows - len(df)} rows with NaN")

    # Convert timestamp
    df['timestamp'] = pd.to_datetime(df['timestamp'])

    # Create parquet filename
    parquet_file = csv_file.with_suffix('.parquet')

    # Write Parquet
    df.to_parquet(
        parquet_file,
        engine='pyarrow',
        compression='snappy',
        index=False
    )

    csv_size = csv_file.stat().st_size / 1024 / 1024
    parquet_size = parquet_file.stat().st_size / 1024 / 1024

    logger.info(f"  ✓ {len(df):,} rows | CSV: {csv_size:.1f} MB → Parquet: {parquet_size:.1f} MB ({csv_size/parquet_size:.1f}x)")

    return parquet_file


def transfer_to_ubuntu(parquet_file: Path) -> bool:
    """Transfer Parquet to Ubuntu via rclone"""
    date_folder = parquet_file.parent.name
    remote_path = f"{RCLONE_DEST}{date_folder}/"

    logger.info(f"  Transferring to Ubuntu: {remote_path}")

    try:
        result = subprocess.run(
            ["rclone", "copy", str(parquet_file), remote_path, "-v"],
            capture_output=True,
            text=True,
            timeout=300
        )

        if result.returncode == 0:
            logger.info(f"  ✓ Transfer complete")
            return True
        else:
            logger.error(f"  ✗ Transfer failed: {result.stderr[:200]}")
            return False

    except Exception as e:
        logger.error(f"  ✗ Transfer error: {e}")
        return False


def safe_delete(file_path: Path, file_type: str = "file"):
    """Safely delete a file with error handling"""
    try:
        file_path.unlink()
        logger.info(f"  ✓ Deleted {file_type}: {file_path.name}")
        return True
    except Exception as e:
        logger.warning(f"  ⚠ Could not delete {file_type} {file_path.name}: {e}")
        return False


def process_csv_chunk(csv_file: Path):
    """Complete processing pipeline for one CSV chunk"""
    logger.info(f"Processing: {csv_file.relative_to(L2_CAPTURE_PATH)}")

    try:
        # Convert to Parquet
        parquet_file = convert_csv_to_parquet(csv_file)

        # Transfer to Ubuntu
        if not transfer_to_ubuntu(parquet_file):
            logger.error(f"  ✗ Skipping delete - transfer failed")
            return False

        # Delete CSV
        safe_delete(csv_file, "CSV")

        # Delete Parquet (we don't need it on VPS)
        safe_delete(parquet_file, "Parquet")

        logger.info(f"  ✓ Pipeline complete for {csv_file.name}\n")
        return True

    except Exception as e:
        logger.error(f"  ✗ Error processing {csv_file.name}: {e}\n")
        return False


def scan_for_chunks():
    """Scan L2 capture folder for new CSV chunks"""
    if not L2_CAPTURE_PATH.exists():
        logger.error(f"L2 capture path not found: {L2_CAPTURE_PATH}")
        return

    # Find all CSV chunks
    csv_files = list(L2_CAPTURE_PATH.glob("*/l2_chunk_*.csv"))

    # Filter to unprocessed, complete files
    for csv_file in csv_files:
        file_id = str(csv_file)

        # Skip if already processed
        if file_id in processed_files:
            continue

        # Skip if file is still being written
        if not is_file_complete(csv_file, MIN_FILE_AGE):
            continue

        # Process the chunk
        success = process_csv_chunk(csv_file)

        if success:
            processed_files.add(file_id)


def main():
    logger.info("=" * 60)
    logger.info("L2 CSV Watcher Started")
    logger.info(f"Monitoring: {L2_CAPTURE_PATH}")
    logger.info(f"Destination: {RCLONE_DEST}")
    logger.info(f"Check interval: {CHECK_INTERVAL}s")
    logger.info("=" * 60)

    # Load previously processed files
    processed_log = Path("processed_chunks.txt")
    if processed_log.exists():
        with open(processed_log) as f:
            processed_files.update(line.strip() for line in f)
        logger.info(f"Loaded {len(processed_files)} previously processed files")

    try:
        while True:
            scan_for_chunks()

            # Save processed files periodically
            with open(processed_log, 'w') as f:
                f.write('\n'.join(processed_files))

            time.sleep(CHECK_INTERVAL)

    except KeyboardInterrupt:
        logger.info("\nShutdown requested - saving state...")
        with open(processed_log, 'w') as f:
            f.write('\n'.join(processed_files))
        logger.info("Watcher stopped")


if __name__ == "__main__":
    main()
