#!/usr/bin/env python3
"""
Ubuntu L2 Parquet Puller
Pulls Parquet files from VPS, imports to ClickHouse, cleans up both sides
"""

import time
import pandas as pd
from pathlib import Path
import logging
import subprocess
import tempfile

# Configuration
VPS_REMOTE = "vps:Users/Administrator/Documents/CG_L2_Capture"
LOCAL_STAGING = Path("/tmp/l2_staging")
PARQUET_ARCHIVE = Path("/home/bernard/trading4/CG_MNQ_MarketReplayLab/data/l2_parquet")
CHECK_INTERVAL = 30  # seconds between pulls
BATCH_SIZE = 10  # max files to pull per cycle

# Logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [%(levelname)s] %(message)s',
    handlers=[
        logging.FileHandler('l2_puller.log'),
        logging.StreamHandler()
    ]
)
logger = logging.getLogger(__name__)

# Track imported files
imported_files = set()


def find_vps_parquet_files():
    """Find all Parquet files on VPS"""
    try:
        result = subprocess.run(
            ["rclone", "lsf", VPS_REMOTE, "--recursive", "--include", "*.parquet"],
            capture_output=True,
            text=True,
            timeout=30
        )

        if result.returncode == 0:
            files = [f.strip() for f in result.stdout.split('\n') if f.strip().endswith('.parquet')]
            return files
        else:
            logger.error(f"Failed to list VPS files: {result.stderr[:200]}")
            return []

    except Exception as e:
        logger.error(f"Error listing VPS files: {e}")
        return []


def pull_parquet_from_vps(remote_path: str) -> Path:
    """Pull a Parquet file from VPS to local staging"""
    local_file = LOCAL_STAGING / Path(remote_path).name

    try:
        result = subprocess.run(
            ["rclone", "copy", f"{VPS_REMOTE}/{remote_path}", str(LOCAL_STAGING), "-v"],
            capture_output=True,
            text=True,
            timeout=60
        )

        if result.returncode == 0 and local_file.exists():
            return local_file
        else:
            logger.error(f"Failed to pull {remote_path}: {result.stderr[:200]}")
            return None

    except Exception as e:
        logger.error(f"Error pulling {remote_path}: {e}")
        return None


def check_already_imported(parquet_file: Path) -> bool:
    """Check if this file's data is already in ClickHouse by timestamp range"""
    try:
        # Read first and last timestamp from Parquet
        df = pd.read_parquet(parquet_file)
        if len(df) == 0:
            return True  # Empty file, skip

        df['timestamp'] = pd.to_datetime(df['timestamp'])
        min_ts = df['timestamp'].min()
        max_ts = df['timestamp'].max()
        row_count = len(df)

        # Query ClickHouse for overlapping data
        query = f"""
        SELECT COUNT(*) FROM l2_depth_raw
        WHERE timestamp >= '{min_ts}' AND timestamp <= '{max_ts}'
        """

        result = subprocess.run(
            ["clickhouse-client", "--query", query],
            capture_output=True,
            text=True,
            timeout=10
        )

        if result.returncode == 0:
            existing_count = int(result.stdout.strip())
            # If we already have similar amount of rows in this time range, likely duplicate
            if existing_count >= row_count * 0.9:  # 90% threshold
                logger.warning(f"  ⚠ Skipping {parquet_file.name} - likely already imported ({existing_count} rows exist in time range)")
                return True

        return False

    except Exception as e:
        logger.warning(f"  ⚠ Could not check duplicates: {e}")
        return False  # Continue with import if check fails


def import_to_clickhouse(parquet_file: Path) -> bool:
    """Import Parquet file to ClickHouse with deduplication"""
    try:
        # Check if already imported
        if check_already_imported(parquet_file):
            return True  # Already there, consider it success

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
            logger.info(f"  ✓ Imported {len(df):,} rows from {parquet_file.name}")
            return True
        else:
            logger.error(f"  ✗ Import failed: {proc.stderr[:200]}")
            return False

    except Exception as e:
        logger.error(f"  ✗ Error importing {parquet_file.name}: {e}")
        return False


def archive_parquet(local_file: Path) -> bool:
    """Move Parquet to archive directory"""
    try:
        # Extract date from filename (e.g., l2_chunk_0001.parquet from 2026-03-02/)
        # Actually the file comes from a date folder, let's create the structure
        archive_file = PARQUET_ARCHIVE / local_file.name

        # Move to archive
        local_file.rename(archive_file)
        logger.info(f"  ✓ Archived to {archive_file}")
        return True

    except Exception as e:
        logger.warning(f"  ⚠ Could not archive {local_file.name}: {e}")
        return False


def delete_from_vps(remote_path: str) -> bool:
    """Delete Parquet file from VPS after successful import"""
    try:
        result = subprocess.run(
            ["rclone", "delete", f"{VPS_REMOTE}/{remote_path}"],
            capture_output=True,
            text=True,
            timeout=30
        )

        if result.returncode == 0:
            logger.info(f"  ✓ Deleted from VPS: {remote_path}")
            return True
        else:
            logger.warning(f"  ⚠ Could not delete from VPS: {result.stderr[:100]}")
            return False

    except Exception as e:
        logger.warning(f"  ⚠ Error deleting from VPS: {e}")
        return False


def process_parquet_file(remote_path: str):
    """Complete processing pipeline for one Parquet file"""
    file_id = remote_path

    # Skip if already imported
    if file_id in imported_files:
        return False

    logger.info(f"Processing: {remote_path}")

    try:
        # Pull from VPS
        local_file = pull_parquet_from_vps(remote_path)
        if not local_file:
            return False

        # Import to ClickHouse
        if not import_to_clickhouse(local_file):
            logger.error(f"  ✗ Import failed, keeping file on VPS")
            local_file.unlink()  # Clean up local copy
            return False

        # Archive locally
        archive_parquet(local_file)

        # Delete from VPS
        delete_from_vps(remote_path)

        # Mark as imported
        imported_files.add(file_id)

        logger.info(f"  ✓ Complete: {remote_path}\n")
        return True

    except Exception as e:
        logger.error(f"  ✗ Error processing {remote_path}: {e}\n")
        return False


def get_clickhouse_stats():
    """Get current ClickHouse statistics"""
    try:
        result = subprocess.run(
            ["clickhouse-client", "--query",
             "SELECT COUNT(*) as rows, MAX(timestamp) as latest FROM l2_depth_raw FORMAT TSV"],
            capture_output=True,
            text=True,
            timeout=10
        )

        if result.returncode == 0:
            parts = result.stdout.strip().split('\t')
            return {
                'rows': int(parts[0]),
                'latest': parts[1] if len(parts) > 1 else 'N/A'
            }
    except:
        pass

    return None


def main():
    logger.info("=" * 60)
    logger.info("L2 Parquet Puller Started (PULL from VPS)")
    logger.info(f"VPS source: {VPS_REMOTE}")
    logger.info(f"Local archive: {PARQUET_ARCHIVE}")
    logger.info(f"Check interval: {CHECK_INTERVAL}s")
    logger.info("=" * 60)

    # Create directories
    LOCAL_STAGING.mkdir(parents=True, exist_ok=True)
    PARQUET_ARCHIVE.mkdir(parents=True, exist_ok=True)

    # Load previously imported files
    imported_log = Path("imported_files.txt")
    if imported_log.exists():
        with open(imported_log) as f:
            imported_files.update(line.strip() for line in f)
        logger.info(f"Loaded {len(imported_files)} previously imported files")

    # Show initial ClickHouse stats
    stats = get_clickhouse_stats()
    if stats:
        logger.info(f"Current DB: {stats['rows']:,} rows | Latest: {stats['latest']}")

    logger.info("")

    scan_count = 0

    try:
        while True:
            # Find Parquet files on VPS
            vps_files = find_vps_parquet_files()

            if vps_files:
                logger.info(f"Found {len(vps_files)} Parquet files on VPS")

                # Process up to BATCH_SIZE files
                for remote_file in vps_files[:BATCH_SIZE]:
                    process_parquet_file(remote_file)

            # Save imported files
            with open(imported_log, 'w') as f:
                f.write('\n'.join(imported_files))

            # Show stats every 10 scans
            scan_count += 1
            if scan_count % 10 == 0:
                stats = get_clickhouse_stats()
                if stats:
                    logger.info(f"DB Status: {stats['rows']:,} rows | Latest: {stats['latest']}")

            time.sleep(CHECK_INTERVAL)

    except KeyboardInterrupt:
        logger.info("\nShutdown requested - saving state...")
        with open(imported_log, 'w') as f:
            f.write('\n'.join(imported_files))

        # Final stats
        stats = get_clickhouse_stats()
        if stats:
            logger.info(f"Final: {stats['rows']:,} rows | Latest: {stats['latest']}")

        logger.info("Puller stopped")


if __name__ == "__main__":
    main()
