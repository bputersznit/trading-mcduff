#!/usr/bin/env python3
"""
Single Position Enforcement with Sequential Lockout

Purpose:
- Enforce 1 MNQ contract maximum (no overlapping positions)
- 10-minute lockout after each trigger (600 seconds)
- True sequential walk-forward (guaranteed 0 violations)
- Highest priority wins when lockouts conflict

Source: CG_mnq_price_trigger_events_v1
Output: CG_mnq_price_trigger_single_position_python_v1

Selection Logic:
1. Process triggers in chronological order
2. For each trigger, check if it falls within lockout of most recently selected trigger
3. If yes, skip it
4. If no, select it and update lockout state

Later replacement: Replace lockout_end = trigger_time + lockout_seconds
with lockout_end = actual_exit_time from path simulation.
"""

import os
from pathlib import Path
import pandas as pd
import clickhouse_connect
from datetime import timedelta


CH_HOST = os.getenv("CH_HOST", os.getenv("CLICKHOUSE_HOST", "localhost"))
CH_PORT = int(os.getenv("CH_PORT", os.getenv("CLICKHOUSE_PORT", "8123")))
CH_USER = os.getenv("CH_USER", os.getenv("CLICKHOUSE_USER", "default"))
CH_PASSWORD = os.getenv("CH_PASSWORD", os.getenv("CLICKHOUSE_PASSWORD", ""))
CH_DATABASE = os.getenv("CH_DATABASE", os.getenv("CLICKHOUSE_DATABASE", "default"))

SOURCE_TABLE = "CG_mnq_price_trigger_events_v1"
OUTPUT_TABLE = "CG_mnq_price_trigger_single_position_python_v1"

LOCKOUT_SECONDS = 600  # 10 minutes


def get_client():
    return clickhouse_connect.get_client(
        host=CH_HOST,
        port=CH_PORT,
        username=CH_USER,
        password=CH_PASSWORD,
        database=CH_DATABASE,
    )


def load_triggers(client) -> pd.DataFrame:
    """Load all actionable triggers ordered by time."""
    sql = f"""
    SELECT *
    FROM {SOURCE_TABLE}
    WHERE trigger_side IN ('LONG', 'SHORT')
    ORDER BY
        trade_date,
        trigger_time,
        trigger_priority_score DESC,
        end_weighted_alignment_score DESC,
        alignment_quality_delta DESC
    """
    return client.query_df(sql)


def enforce_single_position(df: pd.DataFrame) -> pd.DataFrame:
    """
    True sequential walk-forward with lockout enforcement.

    Returns DataFrame with only selected triggers (no overlaps).
    """
    if df.empty:
        return df

    selected = []
    lockout_end = None

    for _, trigger in df.iterrows():
        trigger_time = trigger['trigger_time']

        # Check if within lockout
        if lockout_end is None or trigger_time >= lockout_end:
            # Select this trigger
            selected.append(trigger)
            lockout_end = trigger_time + timedelta(seconds=LOCKOUT_SECONDS)

    if not selected:
        return pd.DataFrame(columns=df.columns)

    # Build output DataFrame
    out = pd.DataFrame(selected).reset_index(drop=True)

    # Add new columns
    out.insert(0, 'trade_sequence', range(1, len(out) + 1))
    out['selected_trigger_time'] = out['trigger_time']
    out['lockout_end_time'] = out['trigger_time'] + pd.Timedelta(seconds=LOCKOUT_SECONDS)
    out['lockout_seconds'] = LOCKOUT_SECONDS

    return out


def write_clickhouse(client, df: pd.DataFrame):
    """
    Write selected triggers to ClickHouse.

    Strategy:
    1. Create table structure matching source table
    2. Add 4 new columns: trade_sequence, selected_trigger_time, lockout_end_time, lockout_seconds
    3. Insert the DataFrame
    """
    client.command(f"DROP TABLE IF EXISTS {OUTPUT_TABLE}")

    # Create table with same structure as source, plus new columns
    # Use explicit type casting to ensure proper data types
    client.command(f"""
    CREATE TABLE {OUTPUT_TABLE}
    ENGINE = MergeTree
    PARTITION BY trade_date
    ORDER BY (trade_date, selected_trigger_time, trigger_side)
    AS SELECT
        CAST(0 AS UInt32) AS trade_sequence,
        CAST(toDateTime('1970-01-01 00:00:00', 'UTC') AS DateTime) AS selected_trigger_time,
        CAST(toDateTime('1970-01-01 00:00:00', 'UTC') AS DateTime) AS lockout_end_time,
        CAST(0 AS UInt16) AS lockout_seconds,
        *
    FROM {SOURCE_TABLE}
    WHERE 0
    """)

    if not df.empty:
        client.insert_df(OUTPUT_TABLE, df)


def main():
    client = get_client()

    print(f"Loading triggers from {SOURCE_TABLE}...")
    df = load_triggers(client)
    print(f"Loaded {len(df)} actionable triggers (LONG + SHORT)")

    print(f"\nEnforcing single position with {LOCKOUT_SECONDS}s lockout...")
    selected = enforce_single_position(df)
    print(f"Selected {len(selected)} triggers (0 violations guaranteed)")

    print(f"\nWriting to ClickHouse table: {OUTPUT_TABLE}...")
    write_clickhouse(client, selected)
    print(f"✓ Wrote {len(selected)} rows to {OUTPUT_TABLE}")

    # Validation query
    print("\nValidation - Side distribution:")
    result = client.query(f"""
    SELECT
        trigger_side,
        count() AS triggers,
        round(avg(trigger_priority_score), 2) AS avg_priority,
        round(avg(end_weighted_alignment_score), 3) AS avg_end_quality,
        round(avg(alignment_quality_delta), 4) AS avg_delta
    FROM {OUTPUT_TABLE}
    GROUP BY trigger_side
    ORDER BY trigger_side
    """)
    for row in result.named_results():
        print(f"  {row['trigger_side']:6} {row['triggers']:3} triggers  "
              f"priority={row['avg_priority']:4.1f}  "
              f"end_quality={row['avg_end_quality']:5.3f}  "
              f"delta={row['avg_delta']:+7.4f}")

    # Lockout overlap sanity check
    print("\nSanity check - Lockout overlaps:")
    violations = client.query(f"""
    SELECT count() AS violation_count
    FROM {OUTPUT_TABLE} AS curr
    INNER JOIN {OUTPUT_TABLE} AS prev
        ON curr.trade_date = prev.trade_date
       AND curr.selected_trigger_time > prev.selected_trigger_time
       AND curr.selected_trigger_time < prev.lockout_end_time
    """)
    violation_count = violations.result_rows[0][0]

    if violation_count == 0:
        print(f"  ✓ Zero violations (perfect sequential enforcement)")
    else:
        print(f"  ✗ {violation_count} violations detected (unexpected!)")


if __name__ == "__main__":
    main()
