#!/usr/bin/env python3
"""
Ubuntu L2 Parquet Importer
Monitors incoming Parquet directory and imports to ClickHouse
"""

import time
import pandas as pd
from pathlib import Path
import logging
import subprocess
from datetime import datetime

# Configuration
PARQUET_PATH = Path("/home/bernard/trading4/CG_MNQ_MarketReplayLab/data/l2_parquet")
CHECK_INTERVAL = 30  # seconds between scans
MIN_FILE_AGE = 10  # seconds before considering file "complete"

# Logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [%(levelname)s] %(message)s',
    handlers=[
        logging.FileHandler('l2_importer.log'),
        logging.StreamHandler()
    ]
)
logger = logging.getLogger(__name__)

# Track imported files
imported_files = set()


def is_file_complete(parquet_file: Path, min_age_seconds: int = 10) -> bool:
    """Check if file is old enough to be considered complete"""
    try:
        age = time.time() - parquet_file.stat().st_mtime
        return age >= min_age_seconds
    except:
        return False


def import_to_clickhouse(parquet_file: Path) -> bool:
    """Import Parquet file to ClickHouse"""
    logger.info(f"Importing {parquet_file.name}...")

    try:
        # Read Parquet
        df = pd.read_parquet(parquet_file)
        df = df.dropna()

        # Fix types
        df['position'] = df['position'].astype(int)
        df['size'] = df['size'].astype(int)

        # Convert to CSV for pipe
        csv_data = df.to_csv(index=False, header=False)

        # Pipe to ClickHouse
        proc = subprocess.run(
            ["clickhouse-client", "--query", "INSERT INTO l2_depth_raw FORMAT CSV"],
            input=csv_data,
            text=True,
            capture_output=True,
            timeout=300
        )

        if proc.returncode == 0:
            logger.info(f"  ✓ Imported {len(df):,} rows")
            return True
        else:
            logger.error(f"  ✗ Import failed: {proc.stderr[:200]}")
            return False

    except Exception as e:
        logger.error(f"  ✗ Error importing {parquet_file.name}: {e}")
        return False


def scan_for_parquet():
    """Scan for new Parquet files"""
    if not PARQUET_PATH.exists():
        logger.warning(f"Parquet path not found: {PARQUET_PATH}")
        PARQUET_PATH.mkdir(parents=True, exist_ok=True)
        return

    # Find all Parquet files
    parquet_files = list(PARQUET_PATH.glob("*/*.parquet"))

    # Filter to unprocessed, complete files
    for parquet_file in parquet_files:
        file_id = str(parquet_file)

        # Skip if already imported
        if file_id in imported_files:
            continue

        # Skip if file is still being transferred
        if not is_file_complete(parquet_file, MIN_FILE_AGE):
            continue

        # Import the file
        success = import_to_clickhouse(parquet_file)

        if success:
            imported_files.add(file_id)


def get_clickhouse_stats():
    """Get current ClickHouse statistics"""
    try:
        result = subprocess.run(
            ["clickhouse-client", "--query",
             "SELECT COUNT(*) as rows, MIN(timestamp), MAX(timestamp) FROM l2_depth_raw FORMAT TSV"],
            capture_output=True,
            text=True,
            timeout=10
        )

        if result.returncode == 0:
            parts = result.stdout.strip().split('\t')
            return {
                'rows': int(parts[0]),
                'min_ts': parts[1] if len(parts) > 1 else 'N/A',
                'max_ts': parts[2] if len(parts) > 2 else 'N/A'
            }
    except:
        pass

    return None


def main():
    logger.info("=" * 60)
    logger.info("L2 Parquet Importer Started")
    logger.info(f"Monitoring: {PARQUET_PATH}")
    logger.info(f"Check interval: {CHECK_INTERVAL}s")
    logger.info("=" * 60)

    # Load previously imported files
    imported_log = Path("imported_files.txt")
    if imported_log.exists():
        with open(imported_log) as f:
            imported_files.update(line.strip() for line in f)
        logger.info(f"Loaded {len(imported_files)} previously imported files")

    # Show initial ClickHouse stats
    stats = get_clickhouse_stats()
    if stats:
        logger.info(f"Current DB: {stats['rows']:,} rows | {stats['min_ts']} → {stats['max_ts']}")

    logger.info("")

    scan_count = 0

    try:
        while True:
            scan_for_parquet()

            # Save imported files periodically
            with open(imported_log, 'w') as f:
                f.write('\n'.join(imported_files))

            # Show stats every 10 scans
            scan_count += 1
            if scan_count % 10 == 0:
                stats = get_clickhouse_stats()
                if stats:
                    logger.info(f"DB Status: {stats['rows']:,} rows | Latest: {stats['max_ts']}")

            time.sleep(CHECK_INTERVAL)

    except KeyboardInterrupt:
        logger.info("\nShutdown requested - saving state...")
        with open(imported_log, 'w') as f:
            f.write('\n'.join(imported_files))

        # Final stats
        stats = get_clickhouse_stats()
        if stats:
            logger.info(f"Final: {stats['rows']:,} rows | {stats['min_ts']} → {stats['max_ts']}")

        logger.info("Importer stopped")


if __name__ == "__main__":
    main()
