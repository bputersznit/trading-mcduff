#!/usr/bin/env python3
"""
ClickHouse Space Optimizer - Identifies tables that can be converted to views to save disk space.

This script analyzes your ClickHouse database and suggests:
1. Which MergeTree tables could be replaced with regular views
2. Which materialized views could be replaced with regular views
3. Empty or near-empty tables that can be dropped
4. Generates DDL statements for optimization

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


def analyze_tables(client, database="default"):
    """Analyze all tables in the database."""

    query = """
    SELECT
        table,
        engine,
        total_bytes,
        formatReadableSize(total_bytes) as size_readable,
        total_rows,
        create_table_query
    FROM system.tables
    WHERE database = %(database)s
      AND engine NOT IN ('View', 'Dictionary', 'Memory')
    ORDER BY total_bytes DESC
    """

    result = client.query(query, parameters={"database": database})

    return [
        {
            "table": row[0],
            "engine": row[1],
            "total_bytes": row[2],
            "size_readable": row[3],
            "total_rows": row[4],
            "create_ddl": row[5],
        }
        for row in result.result_rows
    ]


def find_views(client, database="default"):
    """Find all existing views."""

    query = """
    SELECT
        table,
        engine,
        create_table_query
    FROM system.tables
    WHERE database = %(database)s
      AND engine = 'View'
    ORDER BY table
    """

    result = client.query(query, parameters={"database": database})

    return [
        {
            "table": row[0],
            "engine": row[1],
            "create_ddl": row[2],
        }
        for row in result.result_rows
    ]


def find_materialized_views(client, database="default"):
    """Find all materialized views."""

    query = """
    SELECT
        table,
        engine,
        create_table_query
    FROM system.tables
    WHERE database = %(database)s
      AND engine LIKE '%%Materialized%%'
    ORDER BY table
    """

    result = client.query(query, parameters={"database": database})

    return [
        {
            "table": row[0],
            "engine": row[1],
            "create_ddl": row[2],
        }
        for row in result.result_rows
    ]


def categorize_tables(tables):
    """Categorize tables by potential for space optimization."""

    # Tables that are good candidates for conversion to views
    view_candidates = []

    # Empty or near-empty tables that could be dropped
    empty_tables = []

    # Large tables that should stay as MergeTree
    keep_as_mergetree = []

    # Source data tables (should never be views)
    SOURCE_TABLES = {
        "mnq_mbo",
        "bento_mbo",
        "futures_continuous_mbo",
        "nq_continuous_mbo",
        "nq_tape_trades",
        "nt8_mnq_trades",
    }

    # Tables with frequent writes (should stay as MergeTree)
    WRITE_HEAVY_PATTERNS = [
        "_trades",
        "_entries",
        "_signals",
        "_bars",
        "ohlcv_",
    ]

    for table_info in tables:
        table = table_info["table"]
        total_bytes = table_info["total_bytes"] or 0
        total_rows = table_info["total_rows"] or 0

        # Empty or near-empty tables
        if total_bytes == 0 or total_rows == 0:
            empty_tables.append(table_info)
            continue

        # Source tables must stay as MergeTree
        if table in SOURCE_TABLES:
            keep_as_mergetree.append(table_info)
            continue

        # Check if it's a write-heavy table
        is_write_heavy = any(pattern in table for pattern in WRITE_HEAVY_PATTERNS)

        # Small read-only analytical tables are good view candidates
        # Threshold: under 10MB and doesn't match write-heavy patterns
        if total_bytes < 10 * 1024 * 1024 and not is_write_heavy:
            view_candidates.append(table_info)
        # Medium-sized tables (10MB - 100MB) that could potentially be views
        elif 10 * 1024 * 1024 <= total_bytes < 100 * 1024 * 1024 and not is_write_heavy:
            # Check if table name suggests it's derived/analytical
            if any(keyword in table for keyword in ["_features", "_rolling", "_candidates", "regime", "params", "decision"]):
                view_candidates.append(table_info)
            else:
                keep_as_mergetree.append(table_info)
        else:
            keep_as_mergetree.append(table_info)

    return {
        "view_candidates": view_candidates,
        "empty_tables": empty_tables,
        "keep_as_mergetree": keep_as_mergetree,
    }


def generate_optimization_report(client, database="default", output_dir="."):
    """Generate comprehensive optimization report and DDL scripts."""

    print(f"[INFO] Analyzing database: {database}")
    print()

    # Analyze tables
    tables = analyze_tables(client, database)
    views = find_views(client, database)
    mat_views = find_materialized_views(client, database)

    categorized = categorize_tables(tables)

    # Calculate potential space savings
    view_candidate_bytes = sum(t["total_bytes"] or 0 for t in categorized["view_candidates"])
    empty_table_bytes = sum(t["total_bytes"] or 0 for t in categorized["empty_tables"])
    total_space_used = sum(t["total_bytes"] or 0 for t in tables)

    print("=" * 80)
    print("CLICKHOUSE SPACE OPTIMIZATION REPORT")
    print("=" * 80)
    print()

    print("Total tables analyzed: {}".format(len(tables)))
    print("Existing regular views: {}".format(len(views)))
    print("Existing materialized views: {}".format(len(mat_views)))
    print("Total space used: {:.2f} GiB".format(total_space_used / (1024**3)))
    print()

    print("-" * 80)
    print("POTENTIAL SPACE SAVINGS")
    print("-" * 80)
    print(f"View candidates: {len(categorized['view_candidates'])} tables")
    print(f"  Potential savings: {view_candidate_bytes / (1024**3):.2f} GiB")
    print(f"Empty/near-empty tables: {len(categorized['empty_tables'])} tables")
    print(f"  Potential savings: {empty_table_bytes / (1024**3):.2f} GiB")
    print(f"Total potential savings: {(view_candidate_bytes + empty_table_bytes) / (1024**3):.2f} GiB")
    print()

    # Output directory
    output_path = Path(output_dir)
    output_path.mkdir(exist_ok=True)

    # Generate DROP statements for empty tables
    drop_script = output_path / "CGCl_drop_empty_tables.sql"
    with open(drop_script, "w") as f:
        f.write("-- Drop empty or near-empty tables to save space\n")
        f.write("-- Review carefully before executing!\n\n")

        for table_info in categorized["empty_tables"]:
            f.write(f"-- {table_info['table']}: {table_info['size_readable']}, {table_info['total_rows']} rows\n")
            f.write(f"-- DROP TABLE IF EXISTS {database}.{table_info['table']};\n\n")

    print(f"[GENERATED] {drop_script}")

    # Generate view conversion suggestions
    view_conversion_script = output_path / "CGCl_view_conversion_candidates.sql"
    with open(view_conversion_script, "w") as f:
        f.write("-- Tables that could potentially be converted to views\n")
        f.write("-- IMPORTANT: Only convert if the table is rarely queried or query performance is acceptable\n")
        f.write("-- BACKUP YOUR DATA BEFORE CONVERSION!\n\n")

        for table_info in categorized["view_candidates"]:
            f.write(f"-- {table_info['table']}: {table_info['size_readable']}, {table_info['total_rows']:,} rows\n")
            f.write(f"-- Engine: {table_info['engine']}\n")
            f.write(f"-- To convert:\n")
            f.write(f"--   1. Create a view with the same logic\n")
            f.write(f"--   2. Test the view performance\n")
            f.write(f"--   3. If acceptable, drop the table and use the view\n")
            f.write(f"-- DROP TABLE IF EXISTS {database}.{table_info['table']};\n\n")

    print(f"[GENERATED] {view_conversion_script}")

    # Generate materialized view analysis
    mat_view_script = output_path / "CGCl_materialized_view_analysis.sql"
    with open(mat_view_script, "w") as f:
        f.write("-- Materialized views analysis\n")
        f.write("-- Consider replacing with regular views if:\n")
        f.write("--   1. The source data doesn't change frequently\n")
        f.write("--   2. Query performance is acceptable without pre-aggregation\n")
        f.write("--   3. The target table is large\n\n")

        for mv_info in mat_views:
            f.write(f"-- {mv_info['table']}\n")
            f.write(f"-- Engine: {mv_info['engine']}\n")
            f.write(f"-- CREATE DDL:\n")
            for line in mv_info['create_ddl'].split("\n"):
                f.write(f"--   {line}\n")
            f.write(f"\n")

    print(f"[GENERATED] {mat_view_script}")

    # Print detailed breakdown
    print()
    print("-" * 80)
    print("VIEW CANDIDATES (convert to save space)")
    print("-" * 80)

    for table_info in categorized["view_candidates"]:
        print(f"  {table_info['table']:50s} {table_info['size_readable']:>15s} {table_info['total_rows']:>12,} rows")

    print()
    print("-" * 80)
    print("EMPTY TABLES (consider dropping)")
    print("-" * 80)

    for table_info in categorized["empty_tables"]:
        size_str = table_info['size_readable'] or '0.00 B'
        print("  {:50s} {:>15s}".format(table_info['table'], size_str))

    print()
    print("-" * 80)
    print("KEEP AS MERGETREE (essential or write-heavy)")
    print("-" * 80)

    for table_info in sorted(categorized["keep_as_mergetree"], key=lambda x: x["total_bytes"], reverse=True)[:10]:
        print(f"  {table_info['table']:50s} {table_info['size_readable']:>15s} {table_info['total_rows']:>12,} rows")

    if len(categorized["keep_as_mergetree"]) > 10:
        print(f"  ... and {len(categorized['keep_as_mergetree']) - 10} more tables")

    print()
    print("=" * 80)
    print("RECOMMENDATIONS")
    print("=" * 80)
    print()
    print("1. Review and drop empty tables using CGCl_drop_empty_tables.sql")
    print("2. For view candidates, create views and test performance before dropping tables")
    print("3. Consider replacing materialized views with regular views if query perf is OK")
    print("4. Keep source tables (mnq_mbo, etc.) as MergeTree - never convert to views")
    print()


def main():
    import argparse
    import traceback

    parser = argparse.ArgumentParser(description="ClickHouse Space Optimizer")
    parser.add_argument("--database", default="default", help="Database to analyze")
    parser.add_argument("--output-dir", default=".", help="Output directory for SQL scripts")
    args = parser.parse_args()

    try:
        client = get_client()
        generate_optimization_report(client, args.database, args.output_dir)
    except Exception as e:
        print("[ERROR] {}".format(e), file=sys.stderr)
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
