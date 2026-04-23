#!/bin/bash
# Quick ClickHouse Space Optimization Actions
# Remember: CGCl_ prefix for Claude-generated files

set -e

echo "================================================================================"
echo "CLICKHOUSE QUICK SPACE OPTIMIZATION"
echo "================================================================================"
echo ""

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Check if clickhouse-client is available
if ! command -v clickhouse-client &> /dev/null; then
    echo -e "${RED}[ERROR]${NC} clickhouse-client not found. Install ClickHouse client first."
    exit 1
fi

echo "Select an action:"
echo ""
echo "  1. Show space usage summary"
echo "  2. Drop empty tables (dry run)"
echo "  3. Drop empty tables (EXECUTE)"
echo "  4. Drop duplicate regime tables (v2, v3, backup)"
echo "  5. Show all materialized views"
echo "  6. Run full optimization analysis"
echo "  7. Exit"
echo ""

read -p "Enter choice [1-7]: " choice

case $choice in
    1)
        echo ""
        echo "=== SPACE USAGE SUMMARY ==="
        echo ""
        clickhouse-client --query "
        SELECT
            table,
            engine,
            formatReadableSize(total_bytes) as size,
            formatReadableQuantity(total_rows) as rows
        FROM system.tables
        WHERE database = 'default'
          AND engine NOT IN ('View', 'Dictionary', 'Memory')
          AND total_bytes > 0
        ORDER BY total_bytes DESC
        LIMIT 30
        FORMAT PrettyCompact
        "
        ;;

    2)
        echo ""
        echo "=== DRY RUN: Empty Tables ==="
        echo ""
        python3 CGCl_execute_drop_empty_tables.py --dry-run
        ;;

    3)
        echo ""
        echo -e "${YELLOW}[WARNING]${NC} This will DROP empty tables!"
        echo ""
        read -p "Are you sure? Type 'yes' to continue: " confirm

        if [ "$confirm" = "yes" ]; then
            python3 CGCl_execute_drop_empty_tables.py --yes
        else
            echo "Cancelled."
        fi
        ;;

    4)
        echo ""
        echo -e "${YELLOW}[WARNING]${NC} This will DROP duplicate regime tables!"
        echo ""
        echo "Tables to be dropped:"
        echo "  - mnq_regime_5s_v2"
        echo "  - mnq_regime_5s_v3"
        echo "  - mnq_regime_5s_v3_backup"
        echo "  - mnq_regime_5s_hyst"
        echo "  - mnq_regime_5s_sticky"
        echo ""
        echo "These appear to be backup/test versions."
        echo "The main table 'mnq_regime_5s' will be kept."
        echo ""

        read -p "Are you sure? Type 'yes' to continue: " confirm

        if [ "$confirm" = "yes" ]; then
            echo ""
            echo "Dropping tables..."

            tables=(
                "mnq_regime_5s_v2"
                "mnq_regime_5s_v3"
                "mnq_regime_5s_v3_backup"
                "mnq_regime_5s_hyst"
                "mnq_regime_5s_sticky"
            )

            for table in "${tables[@]}"; do
                echo "  Dropping $table..."
                clickhouse-client --query "DROP TABLE IF EXISTS default.$table"
            done

            echo ""
            echo -e "${GREEN}[OK]${NC} Dropped 5 duplicate regime tables"

            # Show space saved
            echo ""
            echo "Estimated space saved: ~90 MiB"
        else
            echo "Cancelled."
        fi
        ;;

    5)
        echo ""
        echo "=== MATERIALIZED VIEWS ==="
        echo ""
        clickhouse-client --query "
        SELECT
            table,
            engine,
            create_table_query
        FROM system.tables
        WHERE database = 'default'
          AND engine LIKE '%Materialized%'
        FORMAT Vertical
        "
        ;;

    6)
        echo ""
        echo "Running full optimization analysis..."
        echo ""
        python3 CGCl_clickhouse_space_optimizer.py

        echo ""
        echo "Review generated files:"
        echo "  - CGCl_drop_empty_tables.sql"
        echo "  - CGCl_view_conversion_candidates.sql"
        echo "  - CGCl_materialized_view_analysis.sql"
        echo "  - CGCl_CLICKHOUSE_SPACE_OPTIMIZATION_GUIDE.md"
        ;;

    7)
        echo "Exiting."
        exit 0
        ;;

    *)
        echo -e "${RED}[ERROR]${NC} Invalid choice"
        exit 1
        ;;
esac

echo ""
echo "Done!"
