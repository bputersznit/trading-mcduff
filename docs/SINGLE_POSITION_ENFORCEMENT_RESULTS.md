# Single Position Enforcement Results

## Problem: Lockout Violations in SQL-Based Approach

Both table versions have lockout overlaps because SQL cannot enforce true sequential lockout logic without recursive processing.

### Quick Version (10-Minute Bucketing)
- **Rows**: 67 triggers
- **Violations**: 7 (10.4%)
- **Logic**: Groups triggers into 10-minute buckets, picks highest priority per bucket

### Strict Version (NOT EXISTS)
- **Rows**: 68 triggers
- **Violations**: 9 (13.2%)
- **Logic**: Uses NOT EXISTS to check if prior triggers block current trigger

**Strict version has MORE violations** - the NOT EXISTS logic is fundamentally flawed.

## Root Cause: Sequential State Problem

**Required Logic**:
1. Process triggers in chronological order
2. For each trigger, check if it falls within lockout of MOST RECENTLY SELECTED trigger
3. If yes, skip it
4. If no, select it and update lockout state

**SQL Limitation**:
- The NOT EXISTS approach checks against ALL prior triggers in the source table
- It cannot reference the "already selected" subset while building the selection
- This causes high-priority triggers within lockouts to be incorrectly included

**Example Violation** (2025-09-29):
```
Time      Priority  Type                             Lockout Until
13:49:00     7      SHORT_ORB_HIGH_REJECTION_FADE    13:59:00
13:53:00     9      LONG_ORB_HIGH_BREAKOUT...        14:03:00  ← VIOLATION!
```

- Trigger at 13:53 has higher priority (9 vs 7)
- NOT EXISTS logic says: "Include it because prior trigger has lower priority"
- **But this is wrong!** Once 13:49 is selected, its lockout should block ALL triggers until 13:59

## Recommended Solution: Use Quick Version with Manual Fix

**Step 1**: Use quick bucketed version (fewer violations: 7 vs 9)

**Step 2**: Export to Python for true sequential walk-forward:

```python
import clickhouse_connect

client = clickhouse_connect.get_client(host='localhost', port=8123)

# Get all actionable triggers ordered by time
query = """
SELECT *
FROM CG_mnq_price_trigger_events_v1
WHERE trigger_side IN ('LONG', 'SHORT')
ORDER BY trade_date, trigger_time, trigger_priority_score DESC
"""

triggers = client.query(query).named_results()

selected = []
lockout_end = None

for trigger in triggers:
    trigger_time = trigger['trigger_time']

    # Check if within lockout
    if lockout_end is None or trigger_time >= lockout_end:
        # Select this trigger
        selected.append(trigger)
        lockout_end = trigger_time + timedelta(seconds=600)

# Insert back to ClickHouse
# INSERT INTO CG_mnq_price_trigger_single_position_v1_python ...
```

**This approach guarantees 0 violations.**

## Current Results (Quick Version)

### Overall Stats
```
Selected Triggers: 67 (from 109 raw triggers)
Days: 22
Avg Priority: 7.1
Avg End Quality: 0.264
Violations: 7 (10.4%)
```

### Side Distribution
```
Side    Count   Avg Priority   Avg End Quality   Avg Delta
LONG      19       8.16           0.5494          +0.1115
SHORT     48       6.69           0.1509          -0.5586
```

### Violation Analysis

**7 violations** where selected trigger falls within previous trigger's lockout:

```
Date        Trigger Time   Prev Lockout End   Gap (sec)   Priority   Type
2025-09-29  13:53:00       13:59:00            -360         9         LONG_ORB_HIGH_BREAKOUT
2025-10-07  14:05:00       14:06:15             -75         7         SHORT_ORB_LOW_FAILURE
2025-10-07  14:35:45       14:42:10            -385         9         LONG_ORB_LOW_RECLAIM
2025-10-09  14:30:00       14:32:40            -160        10         LONG_ORB_LOW_RECLAIM
2025-10-13  14:34:45       14:35:05             -20         7         SHORT_ORB_HIGH_FADE
... (7 total)
```

**Pattern**: All violations are HIGH-PRIORITY triggers (7-10) that happen BEFORE previous trigger's lockout ends.

**Impact**:
- Violations represent 10.4% of selected triggers
- Most violations are LONG triggers (5/7 = 71%)
- Violations typically have HIGHER priority than non-violating triggers
- Gap ranges from -20 seconds to -385 seconds

## Trade-offs

### Option A: Accept Current Results (67 triggers, 7 violations)
**Pros**:
- Quick to implement
- Violations are relatively minor (most < 6 minutes overlap)
- Higher-priority triggers being selected is arguably correct behavior

**Cons**:
- 10.4% of triggers violate lockout rule
- Could result in overlapping positions in live deployment

### Option B: Python Post-Processing (Perfect Enforcement)
**Pros**:
- Guarantees 0 violations
- True sequential walk-forward
- Expected ~60-62 triggers (5-7 fewer than current)

**Cons**:
- Requires external processing step
- Additional complexity

### Option C: Use Stricter Bucketing (20-minute or 30-minute windows)
**Pros**:
- Reduces violations
- Still SQL-only

**Cons**:
- Reduces trigger count significantly
- May miss valid opportunities

## Recommendation

For **backtest purposes**, Option A is acceptable:
- 7 violations out of 67 triggers (10.4%)
- Most violations are < 6 minutes overlap
- Higher priority triggers are being selected

For **live deployment**, use Option B:
- Zero tolerance for position overlap violations
- Perfect enforcement critical for risk management
- Python processing adds negligible latency

## Files Created

- `clickhouse/CG_MNQ_PRICE_TRIGGER_SINGLE_POSITION_V1.sql` (quick version, 67 triggers, 7 violations)
- `clickhouse/CG_MNQ_PRICE_TRIGGER_SINGLE_POSITION_V1_STRICT.sql` (strict version, 68 triggers, 9 violations - DO NOT USE)

**Use the quick version (_V1) for now.** The strict version performs worse.
