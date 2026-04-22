#!/usr/bin/env python3
"""
Quick diagnostic script to check if trading system is working
Run this to identify why no trades are executing
"""

import os
import sys
from datetime import datetime, timedelta

print("="*80)
print("CG SCALPING STRATEGY - SYSTEM DIAGNOSTIC")
print("="*80)
print()

# Check 1: ClickHouse connection
print("1️⃣  CHECKING CLICKHOUSE CONNECTION...")
try:
    import clickhouse_connect

    client = clickhouse_connect.get_client(
        host='localhost',
        port=8123,
        username='default',
        password='',
        database='marketreplay'
    )

    # Test query
    result = client.query("SELECT 1")
    print("   ✅ ClickHouse connected successfully")

    # Check for recent data
    print("\n2️⃣  CHECKING ORDER FLOW DATA...")
    query = """
    SELECT
        max(timestamp_1sec) as latest,
        count(*) as rows_last_hour
    FROM mnq_orderflow_1sec
    WHERE timestamp_1sec >= now() - INTERVAL 1 HOUR
    """

    result = client.query(query)
    if result.result_rows:
        latest, rows = result.result_rows[0]
        print(f"   ✅ Latest data: {latest}")
        print(f"   ✅ Rows last hour: {rows:,}")

        if rows == 0:
            print("   ⚠️  WARNING: No data in last hour!")
            print("   → Market might be closed")
            print("   → Or data ingestion not running")

        # Check how old the data is
        if latest:
            age_seconds = (datetime.now() - latest).total_seconds()
            if age_seconds > 60:
                print(f"   ⚠️  WARNING: Data is {age_seconds:.0f} seconds old!")
                print("   → Market might be closed")
            else:
                print(f"   ✅ Data is current ({age_seconds:.0f} seconds old)")

except ImportError:
    print("   ❌ clickhouse-connect not installed")
    print("   → Run: pip install clickhouse-connect")
    sys.exit(1)
except Exception as e:
    print(f"   ❌ ClickHouse connection failed: {e}")
    print("   → Is ClickHouse running? sudo systemctl start clickhouse-server")
    sys.exit(1)

# Check 3: Test signal detection
print("\n3️⃣  TESTING SIGNAL DETECTION...")

# Test ABSORPTION signals
query = """
SELECT
    timestamp_1sec,
    price,
    sell_aggressor_volume,
    bid_adds,
    net_resting_bid
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQ'
  AND timestamp_1sec >= now() - INTERVAL 30 MINUTE
  AND sell_aggressor_volume > 30
  AND bid_adds > sell_aggressor_volume * 1.1
  AND net_resting_bid > 15
ORDER BY timestamp_1sec DESC
LIMIT 5
"""

result = client.query(query)
absorption_signals = len(result.result_rows)
print(f"   ABSORPTION signals (last 30min): {absorption_signals}")

# Test ICEBERG signals
query = """
SELECT
    timestamp_1sec,
    price,
    total_volume,
    bid_adds + ask_adds as visible_adds
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQ'
  AND timestamp_1sec >= now() - INTERVAL 30 MINUTE
  AND total_volume > 40
  AND (bid_adds + ask_adds) < total_volume * 0.3
  AND abs(net_resting_bid - net_resting_ask) > 10
ORDER BY timestamp_1sec DESC
LIMIT 5
"""

result = client.query(query)
iceberg_signals = len(result.result_rows)
print(f"   ICEBERG signals (last 30min): {iceberg_signals}")

# Test BREAKOUT signals
query = """
WITH baseline AS (
    SELECT
        timestamp_1sec,
        total_volume,
        abs(aggressor_delta) as abs_aggression,
        aggressor_delta,
        avg(total_volume) OVER (ORDER BY timestamp_1sec ROWS BETWEEN 30 PRECEDING AND 1 PRECEDING) as vol_baseline,
        avg(abs(aggressor_delta)) OVER (ORDER BY timestamp_1sec ROWS BETWEEN 30 PRECEDING AND 1 PRECEDING) as agg_baseline
    FROM mnq_orderflow_1sec
    WHERE symbol = 'MNQ'
      AND timestamp_1sec >= now() - INTERVAL 30 MINUTE
)
SELECT COUNT(*)
FROM baseline
WHERE total_volume > vol_baseline * 2.0
  AND abs_aggression > agg_baseline * 2.5
  AND total_volume > 25
  AND abs_aggression > 12
"""

result = client.query(query)
breakout_signals = result.result_rows[0][0] if result.result_rows else 0
print(f"   BREAKOUT signals (last 30min): {breakout_signals}")

total_signals = absorption_signals + iceberg_signals + breakout_signals
print(f"\n   📊 TOTAL signals last 30min: {total_signals}")

if total_signals == 0:
    print("\n   ⚠️  WARNING: No signals detected!")
    print("   → Market might be slow/choppy")
    print("   → Or thresholds are too strict")
    print("   → Consider lowering detection thresholds")
else:
    print(f"\n   ✅ Signal detection working ({total_signals/30:.1f} signals/min)")

# Check 4: Signal file (if on Windows with VPS connection)
print("\n4️⃣  CHECKING SIGNAL FILE...")
signal_file_linux = "/mnt/c/Trading/Signals/mnq_signals.csv"
signal_file_win = "C:\\Trading\\Signals\\mnq_signals.csv"

signal_file = None
if os.path.exists(signal_file_linux):
    signal_file = signal_file_linux
elif os.path.exists(signal_file_win):
    signal_file = signal_file_win

if signal_file:
    stat = os.stat(signal_file)
    mod_time = datetime.fromtimestamp(stat.st_mtime)
    age_seconds = (datetime.now() - mod_time).total_seconds()

    print(f"   ✅ Signal file exists: {signal_file}")
    print(f"   ✅ Last modified: {mod_time} ({age_seconds:.0f} seconds ago)")

    if age_seconds > 60:
        print(f"   ⚠️  WARNING: Signal file is stale!")
        print("   → Signal generator might not be running")

    # Read last line
    try:
        with open(signal_file, 'r') as f:
            lines = f.readlines()
            if lines:
                print(f"   ✅ Last signal: {lines[-1].strip()}")
            else:
                print("   ⚠️  File is empty")
    except Exception as e:
        print(f"   ⚠️  Could not read file: {e}")
else:
    print("   ❌ Signal file not found!")
    print(f"   → Expected at: C:\\Trading\\Signals\\mnq_signals.csv")
    print("   → Signal generator might not be running")
    print("   → Or directory doesn't exist")

# Check 5: Python packages
print("\n5️⃣  CHECKING PYTHON PACKAGES...")
try:
    import paramiko
    print("   ✅ paramiko installed")
except ImportError:
    print("   ⚠️  paramiko not installed (needed for VPS sync)")

try:
    import clickhouse_connect
    print("   ✅ clickhouse-connect installed")
except ImportError:
    print("   ❌ clickhouse-connect not installed")

print("\n" + "="*80)
print("DIAGNOSTIC SUMMARY")
print("="*80)

issues = []
if rows == 0:
    issues.append("No order flow data in last hour")
if total_signals == 0:
    issues.append("No signals detected (thresholds may be too strict)")
if not signal_file:
    issues.append("Signal file not found (generator not running?)")
elif age_seconds > 60:
    issues.append("Signal file is stale (generator crashed?)")

if not issues:
    print("\n✅ System appears healthy!")
    print("\nIf still no trades:")
    print("  1. Check NT8 strategy is enabled")
    print("  2. Check NT8 signal file path matches")
    print("  3. Check NT8 Output window (F5) for errors")
    print("  4. Verify market is open (CME hours)")
else:
    print("\n⚠️  ISSUES FOUND:")
    for i, issue in enumerate(issues, 1):
        print(f"  {i}. {issue}")

    print("\n📋 NEXT STEPS:")
    if "data" in str(issues):
        print("  → Check if market is open")
        print("  → Verify data ingestion is running")
    if "signals" in str(issues):
        print("  → Lower signal detection thresholds")
        print("  → See docs/DEBUGGING_NO_TRADES.md")
    if "file" in str(issues):
        print("  → Start signal generator:")
        print("    cd scripts && python CGCl_nt8_signal_generator.py")

print("\n" + "="*80)
