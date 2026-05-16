#!/usr/bin/env python3
"""
L2 Pipeline Monitor
Real-time dashboard for VPS → Ubuntu → ClickHouse pipeline
"""

import subprocess
import time
from datetime import datetime
import sys

def clear_screen():
    """Clear terminal screen"""
    print("\033[2J\033[H", end="")


def get_vps_parquet_count():
    """Count Parquet files on VPS"""
    try:
        result = subprocess.run(
            ["rclone", "lsf", "vps:Users/Administrator/Documents/CG_L2_Capture",
             "--recursive", "--include", "*.parquet"],
            capture_output=True,
            text=True,
            timeout=10
        )
        if result.returncode == 0:
            files = [f for f in result.stdout.split('\n') if f.strip().endswith('.parquet')]
            return len(files)
        return -1
    except:
        return -1


def get_clickhouse_stats():
    """Get ClickHouse statistics"""
    try:
        result = subprocess.run(
            ["clickhouse-client", "--query",
             """SELECT
                    COUNT(*) as rows,
                    MIN(timestamp) as first_ts,
                    MAX(timestamp) as last_ts,
                    COUNT(DISTINCT toDate(timestamp)) as days
                FROM l2_depth_raw
                FORMAT TSV"""],
            capture_output=True,
            text=True,
            timeout=10
        )
        if result.returncode == 0:
            parts = result.stdout.strip().split('\t')
            return {
                'rows': int(parts[0]),
                'first': parts[1] if len(parts) > 1 else 'N/A',
                'last': parts[2] if len(parts) > 2 else 'N/A',
                'days': int(parts[3]) if len(parts) > 3 else 0
            }
    except:
        pass
    return None


def get_local_parquet_count():
    """Count local Parquet files"""
    try:
        result = subprocess.run(
            ["find", "data/l2_parquet", "-name", "*.parquet", "-type", "f"],
            capture_output=True,
            text=True,
            timeout=5
        )
        if result.returncode == 0:
            return len([f for f in result.stdout.split('\n') if f.strip()])
        return 0
    except:
        return 0


def get_puller_status():
    """Check if puller is running"""
    try:
        result = subprocess.run(
            ["pgrep", "-f", "ubuntu_l2_puller"],
            capture_output=True,
            text=True
        )
        return result.returncode == 0
    except:
        return False


def tail_log(log_file, lines=5):
    """Get last N lines from log file"""
    try:
        result = subprocess.run(
            ["tail", "-n", str(lines), log_file],
            capture_output=True,
            text=True,
            timeout=2
        )
        if result.returncode == 0:
            return result.stdout
        return ""
    except:
        return ""


def format_number(n):
    """Format number with commas"""
    if n >= 1000000:
        return f"{n/1000000:.2f}M"
    elif n >= 1000:
        return f"{n/1000:.1f}K"
    return str(n)


def main():
    print("Starting L2 Pipeline Monitor...")
    time.sleep(1)

    prev_rows = None
    prev_time = None

    try:
        while True:
            clear_screen()

            now = datetime.now()
            current_rows = None

            # Header
            print("=" * 80)
            print(f"  L2 PIPELINE MONITOR - {now.strftime('%Y-%m-%d %H:%M:%S')}")
            print("=" * 80)
            print()

            # Pipeline Status
            puller_running = get_puller_status()
            print("┌─ PIPELINE STATUS " + "─" * 60)
            print(f"│  Ubuntu Puller:     {'🟢 RUNNING' if puller_running else '🔴 STOPPED'}")
            print(f"│  VPS Watcher:       (check VPS manually)")
            print("└" + "─" * 78)
            print()

            # VPS Status
            vps_count = get_vps_parquet_count()
            print("┌─ VPS (Windows) " + "─" * 61)
            if vps_count >= 0:
                print(f"│  Parquet files waiting:  {vps_count} files")
                if vps_count > 20:
                    print(f"│  ⚠️  WARNING: High backlog - check VPS watcher")
            else:
                print(f"│  Parquet files waiting:  (connection error)")
            print("└" + "─" * 78)
            print()

            # Ubuntu Status
            local_count = get_local_parquet_count()
            print("┌─ UBUNTU (Local Archive) " + "─" * 51)
            print(f"│  Archived Parquet:       {local_count} files")
            print("└" + "─" * 78)
            print()

            # ClickHouse Status
            stats = get_clickhouse_stats()
            print("┌─ CLICKHOUSE DATABASE " + "─" * 55)
            if stats:
                current_rows = stats['rows']
                print(f"│  Total rows:             {format_number(stats['rows'])} ({stats['rows']:,})")
                print(f"│  Trading days:           {stats['days']}")
                print(f"│  First event:            {stats['first']}")
                print(f"│  Latest event:           {stats['last']}")

                # Calculate import rate
                if prev_rows is not None and prev_time is not None:
                    elapsed = (now - prev_time).total_seconds()
                    if elapsed > 0:
                        rows_added = current_rows - prev_rows
                        rate = rows_added / elapsed
                        if rate > 0:
                            print(f"│  Import rate:            {format_number(int(rate))}/sec ({int(rows_added)} rows in {int(elapsed)}s)")
                        else:
                            print(f"│  Import rate:            0/sec (idle)")

                prev_rows = current_rows
                prev_time = now
            else:
                print(f"│  Status:                 CONNECTION ERROR")
            print("└" + "─" * 78)
            print()

            # Recent Activity
            print("┌─ RECENT ACTIVITY (last 5 log entries) " + "─" * 37)
            recent = tail_log("scripts/l2_puller.log", 5)
            if recent:
                for line in recent.split('\n')[:5]:
                    if line.strip():
                        # Truncate long lines
                        display = line[:76]
                        print(f"│  {display}")
            else:
                print(f"│  (no recent activity)")
            print("└" + "─" * 78)
            print()

            # Instructions
            print("Press Ctrl+C to exit | Refreshes every 10 seconds")
            print()

            time.sleep(10)

    except KeyboardInterrupt:
        print("\n\nMonitor stopped.")
        sys.exit(0)


if __name__ == "__main__":
    main()
