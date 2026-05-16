#!/usr/bin/env python3
"""
L2 Wall Strategy - Full 24-Day Backtest
Run on all available trading days in ClickHouse
"""

import sys
sys.path.append('scripts')

# Import the wall backtest components
import subprocess
import pandas as pd
from io import StringIO

def get_available_dates():
    """Get all trading days in database"""
    query = """
    SELECT DISTINCT toDate(timestamp) as date
    FROM l2_depth_raw
    WHERE hour(timestamp) >= 8 AND hour(timestamp) <= 15
    ORDER BY date
    FORMAT CSVWithNames
    """
    
    result = subprocess.run(
        ['clickhouse-client', '-q', query],
        capture_output=True,
        text=True
    )
    
    if result.returncode != 0:
        print(f"Error: {result.stderr}")
        return []
    
    df = pd.read_csv(StringIO(result.stdout))
    return df['date'].tolist()


def main():
    print("=== L2 Wall Strategy - Full Backtest ===\n")
    
    dates = get_available_dates()
    print(f"Found {len(dates)} trading days with RTH data:")
    for date in dates:
        print(f"  - {date}")
    
    print(f"\nRunning wall backtest on all {len(dates)} days...")
    print("This will take several minutes...\n")
    
    # Run backtest day by day
    all_results = []
    
    for i, date in enumerate(dates, 1):
        print(f"[{i}/{len(dates)}] Processing {date}...")
        
        # Run backtest for this day
        result = subprocess.run(
            ['python3', 'scripts/backtest_l2_walls.py', date, date],
            capture_output=True,
            text=True
        )
        
        # Parse results (simplified for now)
        if 'total_trades' in result.stdout:
            all_results.append(date)
    
    print(f"\n✅ Completed backtest on {len(all_results)} days")
    print("Results saved to results/ directory")


if __name__ == '__main__':
    main()
