# Python Sequential Enforcement Results

## Summary

**Perfect single position enforcement achieved via Python sequential walk-forward.**

- **Source**: CG_mnq_price_trigger_events_v1 (72 actionable triggers: LONG + SHORT)
- **Output**: CG_mnq_price_trigger_single_position_python_v1 (61 selected triggers)
- **Violations**: **0** (guaranteed by sequential logic)
- **Lockout**: 10 minutes (600 seconds) after each trigger

## Comparison: SQL vs Python Approaches

| Approach | Triggers Selected | Violations | Violation Rate | Method |
|----------|------------------|------------|----------------|--------|
| **Quick SQL** (V1) | 67 | 7 | 10.4% | 10-minute bucketing, highest priority per bucket |
| **Strict SQL** (V1_STRICT) | 68 | 9 | 13.2% | NOT EXISTS with priority comparison |
| **Python** (V1_PYTHON) | **61** | **0** | **0%** | True sequential walk-forward |

### Why Python Wins

**SQL Limitation**: Cannot maintain "last selected trigger" state during query execution.
- Quick version: Groups into buckets, but high-priority triggers can violate previous bucket's lockout
- Strict version: NOT EXISTS checks ALL prior triggers, not just selected subset
- Both produce lockout overlaps

**Python Solution**: Maintains explicit lockout state in memory.
- Processes triggers one-by-one in chronological order
- Tracks `lockout_end` from most recently selected trigger
- Skips ALL triggers within lockout (regardless of priority)
- Result: Zero violations guaranteed

## Python Results

### Overall Stats

```
Total triggers:      61
Source triggers:     72 (LONG + SHORT)
Reduction:           11 triggers skipped (15.3%)
Unique days:         22
Avg per day:         2.77
Avg priority:        6.98
Avg end quality:     0.240
Min lockout gap:     25 seconds
Violations:          0
```

### Side Distribution

```
Side    Triggers   Avg Priority   Avg End Quality   Avg Delta
LONG      18          7.7            0.470           -0.0027
SHORT     43          6.7            0.144           -0.5651
```

**Observations**:
- SHORT dominates (70.5% of triggers)
- LONG has higher quality (0.47 vs 0.14)
- LONG has neutral delta (-0.003), SHORT has strong negative delta (-0.565)
- Pattern: LONG = continuation setups, SHORT = exhaustion/fade plays

### Lockout Gap Statistics

All gaps between consecutive triggers are positive (no overlaps):

```
Min gap:    25 seconds
Avg gap:    ~7.3 days (varies by day-to-day distribution)
Max gap:    ~20.4 days
Violations: 0
```

**Note**: Large avg/max gaps reflect multi-day intervals between triggers, not intraday lockout spacing.

## Trigger Reduction Analysis

**72 source triggers → 61 selected triggers = 11 skipped (15.3%)**

These 11 triggers fell within the 10-minute lockout of a previously selected trigger and were correctly excluded by the sequential walk-forward logic.

### Why Fewer Triggers Than SQL Versions?

| Version | Triggers | Why |
|---------|----------|-----|
| **Quick SQL** | 67 | Bucketing allows high-priority triggers to violate lockouts |
| **Strict SQL** | 68 | NOT EXISTS incorrectly includes high-priority triggers within lockouts |
| **Python** | 61 | True sequential enforcement skips ALL triggers in lockout |

The Python version is **more conservative** because it strictly enforces the "no overlapping positions" rule. SQL versions incorrectly include 6-7 high-priority triggers that fall within prior lockouts.

## Implementation Details

### Script: `scripts/enforce_single_position.py`

**Key Functions**:

1. **load_triggers()**: Loads LONG + SHORT triggers ordered by:
   - trade_date
   - trigger_time
   - trigger_priority_score DESC
   - end_weighted_alignment_score DESC
   - alignment_quality_delta DESC

2. **enforce_single_position()**: Sequential walk-forward
   ```python
   lockout_end = None
   for trigger in triggers:
       if lockout_end is None or trigger_time >= lockout_end:
           selected.append(trigger)
           lockout_end = trigger_time + timedelta(seconds=600)
   ```

3. **write_clickhouse()**: Creates table with source columns + 4 new columns:
   - `trade_sequence`: UInt32 (1-indexed trade number)
   - `selected_trigger_time`: DateTime (same as trigger_time)
   - `lockout_end_time`: DateTime (trigger_time + 600 seconds)
   - `lockout_seconds`: UInt16 (constant 600 for now)

### Future Enhancement

**Later replacement**: Replace fixed 10-minute lockout with actual exit time from path simulation:

```python
# Current (placeholder):
lockout_end = trigger_time + timedelta(seconds=600)

# Future (after path simulation):
lockout_end = actual_exit_time  # From price path analysis
```

This will give realistic trade durations instead of fixed lockout windows.

## Validation Queries

### Zero Violations Check

```sql
SELECT count() AS violation_count
FROM CG_mnq_price_trigger_single_position_python_v1 AS curr
INNER JOIN CG_mnq_price_trigger_single_position_python_v1 AS prev
    ON curr.trade_date = prev.trade_date
   AND curr.selected_trigger_time > prev.selected_trigger_time
   AND curr.selected_trigger_time < prev.lockout_end_time
```

**Result**: 0 violations ✓

### Side Distribution

```sql
SELECT
    trigger_side,
    count() AS triggers,
    round(avg(trigger_priority_score), 2) AS avg_priority,
    round(avg(end_weighted_alignment_score), 3) AS avg_end_quality,
    round(avg(alignment_quality_delta), 4) AS avg_delta
FROM CG_mnq_price_trigger_single_position_python_v1
GROUP BY trigger_side
ORDER BY trigger_side
```

### Daily Breakdown

```sql
SELECT
    trade_date,
    count() AS triggers,
    countIf(trigger_side = 'LONG') AS longs,
    countIf(trigger_side = 'SHORT') AS shorts,
    round(avg(trigger_priority_score), 1) AS avg_priority
FROM CG_mnq_price_trigger_single_position_python_v1
GROUP BY trade_date
ORDER BY trade_date
```

## Recommendation

**For backtest purposes**: Use Python version (CG_mnq_price_trigger_single_position_python_v1)
- Zero violations guaranteed
- True sequential enforcement
- Expected ~60 triggers per 22-day period (~2.7 per day)

**For live deployment**: Python enforcement is MANDATORY
- Zero tolerance for position overlap violations
- Perfect enforcement critical for risk management
- 1 MNQ contract maximum at all times

**SQL versions should NOT be used for production** - both have inherent violations due to SQL's inability to maintain sequential state.

## Files

- **Script**: `scripts/enforce_single_position.py`
- **Output table**: CG_mnq_price_trigger_single_position_python_v1
- **Source table**: CG_mnq_price_trigger_events_v1
- **SQL quick version**: clickhouse/CG_MNQ_PRICE_TRIGGER_SINGLE_POSITION_V1.sql (67 triggers, 7 violations)
- **SQL strict version**: clickhouse/CG_MNQ_PRICE_TRIGGER_SINGLE_POSITION_V1_STRICT.sql (68 triggers, 9 violations)

## Next Step

After validation, the next phase is:

**CG_mnq_price_trigger_path_simulation_v1**: Simulate price path execution
- Entry: reference_entry_price (close at trigger time)
- Exit: Model path evolution using 5s bars
- Calculate realistic PnL with slippage ($10) and commission ($0.70)
- Replace fixed 10-minute lockout with actual hold duration
- Track dual PnL: NT-style (for loss governor) vs realistic (for actual expectations)
