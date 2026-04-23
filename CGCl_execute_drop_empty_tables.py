#!/usr/bin/env python3
"""
Execute drop commands for empty tables safely.

This script will:
1. List all empty tables
2. Ask for confirmation
3. Drop them to save space

Remember: CGCl_ prefix for Claude-generated files
"""

import os
import sys
from pathlib import Path

import clickhouse_connect
from dotenv import load_dotenv


def get_client():
    """Connect to ClickHouse using .env credentials."""
    load_dotenv()

    host = os.getenv("CLICKHOUSE_HOST", "localhost")
    port = int(os.getenv("CLICKHOUSE_PORT", "8123"))
    user = os.getenv("CLICKHOUSE_USER", "default")
    password = os.getenv("CLICKHOUSE_PASSWORD", "")
    database = os.getenv("CLICKHOUSE_DATABASE", "default")

    return clickhouse_connect.get_client(
        host=host,
        port=port,
        username=user,
        password=password,
        database=database,
    )


def find_empty_tables(client, database="default"):
    """Find all empty tables."""

    query = """
    SELECT
        table,
        engine,
        total_bytes,
        total_rows
    FROM system.tables
    WHERE database = %(database)s
      AND engine NOT IN ('View', 'Dictionary', 'Memory')
      AND (total_bytes = 0 OR total_rows = 0 OR total_bytes IS NULL OR total_rows IS NULL)
    ORDER BY table
    """

    result = client.query(query, parameters={"database": database})

    return [row[0] for row in result.result_rows]


def main():
    import argparse

    parser = argparse.ArgumentParser(description="Drop empty ClickHouse tables")
    parser.add_argument("--database", default="default", help="Database to clean")
    parser.add_argument("--dry-run", action="store_true", help="Show what would be dropped without actually dropping")
    parser.add_argument("--yes", action="store_true", help="Skip confirmation prompt")
    args = parser.parse_args()

    try:
        client = get_client()
        empty_tables = find_empty_tables(client, args.database)

        if not empty_tables:
            print("[INFO] No empty tables found!")
            return

        print("=" * 80)
        print("EMPTY TABLES TO DROP")
        print("=" * 80)
        print()

        for table in empty_tables:
            print("  - {}".format(table))

        print()
        print("Total: {} tables".format(len(empty_tables)))
        print()

        if args.dry_run:
            print("[DRY RUN] No tables were dropped.")
            return

        if not args.yes:
            response = input("Are you sure you want to drop these tables? (yes/no): ")
            if response.lower() not in ("yes", "y"):
                print("[CANCELLED] No tables were dropped.")
                return

        print()
        print("[INFO] Dropping tables...")
        print()

        dropped_count = 0
        failed_count = 0

        for table in empty_tables:
            try:
                drop_query = "DROP TABLE IF EXISTS {}.{}".format(args.database, table)
                client.command(drop_query)
                print("[OK] Dropped: {}".format(table))
                dropped_count += 1
            except Exception as e:
                print("[ERROR] Failed to drop {}: {}".format(table, e))
                failed_count += 1

        print()
        print("=" * 80)
        print("SUMMARY")
        print("=" * 80)
        print("Dropped: {} tables".format(dropped_count))
        print("Failed:  {} tables".format(failed_count))
        print()

    except Exception as e:
        print("[ERROR] {}".format(e), file=sys.stderr)
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
