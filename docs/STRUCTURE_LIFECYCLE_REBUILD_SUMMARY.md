# Structure Lifecycle Rebuild Summary

## Objective
Expand structure lifecycle analysis from 1-day sample to full 22-day dataset by using `CG_mnq_session_regime_v2` instead of `CG_mnq_wall_outcomes_regime_v1`.

## Tables Created

### 1. CG_mnq_session_regime_v2
**Purpose**: Lightweight 5-second regime context table

**Coverage**:
- 103,224 rows (5-second bars)
- 22 days: 2025-09-23 to 2025-10-22
- Avg ORB range: 100.51 points

**Features**:
- ORB high/low/range (per day)
- Running VWAP (calculated from MBO trades)
- ORB state: BUILDING, ABOVE_OR, BELOW_OR, INSIDE_OR
- VWAP relation: ABOVE, BELOW, AT
- Time bucket: OPENING_RANGE, OPEN, MIDDAY, PM, CLOSE
- Trend bias: LONG_BIAS, SHORT_BIAS, NEUTRAL

**Key Implementation Details**:
- VWAP calculated from `mnq_mbo` trade data (action='T')
- Aggregated to 5-second buckets using `toStartOfInterval`
- Running VWAP computed with cumulative window functions
- ORB built from first 15 minutes (570-585 minutes ET)

### 2. CG_mnq_structure_lifecycle_v1_1 (REBUILT)
**Before**: 3,479 structures, 1 day (2025-09-23 only)
**After**: 908 structures, 22 days (2025-09-23 to 2025-10-22)

**Structure Distribution**:
```
Level Type    Side         Maturity    Count    Avg Touches
─────────────────────────────────────────────────────────────
ORB_HIGH      RESISTANCE   FORMING       26         10.0
ORB_HIGH      RESISTANCE   MATURE        70         33.2
ORB_HIGH      RESISTANCE   IMMATURE      35          1.5
ORB_LOW       SUPPORT      FORMING       25          6.9
ORB_LOW       SUPPORT      MATURE        97         28.2
ORB_LOW       SUPPORT      IMMATURE      41          1.4
VWAP          RESISTANCE   FORMING       57          2.1
VWAP          RESISTANCE   MATURE        73          4.5
VWAP          RESISTANCE   IMMATURE     184          1.1
VWAP          SUPPORT      FORMING       54          2.1
VWAP          SUPPORT      MATURE        76          4.4
VWAP          SUPPORT      IMMATURE     170          1.1
─────────────────────────────────────────────────────────────
TOTAL                                   908
```

**Key Changes**:
- Added ORB_HIGH structures (previously missing)
- VWAP structures split directionally (SUPPORT vs RESISTANCE)
- Event-gated VWAP detection prevents proximity inflation

### 3. CG_mnq_structure_lifecycle_aggression_v1 (REBUILT)
**Before**: 1,037 structures, 1 day
**After**: 478 structures (FORMING + MATURE only), 22 days

**Coverage**: 100% join success (no missing aggression data)

## Alignment Score Results (22-Day Sample)

### Overall Distribution
```
Metric                Value
────────────────────────────
Total Structures      478
Avg Start Align       3.46 / 9 (38.4%)
Avg End Align         3.27 / 9 (36.3%)
Alignment Delta       -0.19 (degrades)

Percentiles:
  P10: start=0, end=0
  P50: start=3, end=3
  P90: start=8, end=7
```

### By Structure Type
```
Level      Side        Maturity   Count   Start   End    Delta   Touches   Failed   Success
────────────────────────────────────────────────────────────────────────────────────────────
ORB_HIGH   RESISTANCE  FORMING      26    3.85   3.54   -0.31     10.0      0.8      9.0
ORB_HIGH   RESISTANCE  MATURE       70    3.51   4.23   +0.71     33.2      6.3     25.9
ORB_LOW    SUPPORT     FORMING      25    4.20   3.40   -0.80      6.9      0.4      6.2
ORB_LOW    SUPPORT     MATURE       97    4.84   3.43   -1.40     28.2      6.2     21.1
VWAP       RESISTANCE  FORMING      57    3.25   2.88   -0.37      2.1      2.0      0.0
VWAP       RESISTANCE  MATURE       73    2.60   2.45   -0.15      4.5      4.3      0.0
VWAP       SUPPORT     FORMING      54    2.76   3.44   +0.69      2.1      2.0      0.0
VWAP       SUPPORT     MATURE       76    2.76   3.01   +0.25      4.4      4.3      0.0
```

### Key Patterns

**ORB Structures**:
- Start with HIGHER alignment (3.5-4.8) vs VWAP (2.6-3.3)
- Mostly BREAK through (25.9 avg successful breaks for ORB_HIGH MATURE)
- ORB_LOW: alignment degrades (-0.8 to -1.4)
- ORB_HIGH MATURE: alignment IMPROVES (+0.71) - unique pattern

**VWAP Structures**:
- Start with LOW alignment (~3 / 9 timeframes)
- NEVER break (0 successful breaks, only failed touches)
- Mixed alignment changes: RESISTANCE degrades, SUPPORT improves slightly
- SUPPORT structures gain alignment over lifecycle (+0.25 to +0.69)

**Alignment Dynamics**:
- Overall trend: alignment DEGRADES (-0.19 avg)
- Exception: ORB_HIGH MATURE and VWAP SUPPORT gain alignment
- Suggests mean reversion: structures that hold tend to gain aggression support

## Technical Implementation

### Bug Fixes
1. **Cartesian Product Join**: Added `AND o.t = r.ts_5s` to VWAP join (previously only joined on trade_date)
2. **Column Name Corrections**: Updated `ts` → `t`, `high/low/close` → `hi_px/lo_px/close_px`
3. **VWAP Calculation**: Built from `mnq_mbo` trades instead of non-existent volume in `mnq_ohlc_5s`
4. **DateTime64 Conversion**: Convert DateTime to DateTime64(3) before millisecond interval operations

### Files Updated
- `clickhouse/CG_MNQ_SESSION_REGIME_V2.sql` (NEW)
- `clickhouse/CG_MNQ_STRUCTURE_LIFECYCLE_V1_1.sql` (MODIFIED)
- `clickhouse/CG_MNQ_STRUCTURE_LIFECYCLE_AGGRESSION_V1.sql` (UNCHANGED, auto-expanded)

## Validation Results

All tables validated successfully:
- **Session Regime v2**: 103,224 rows, 22 days, ORB range 100.51 pts avg
- **Lifecycle v1.1**: 908 structures, 316 MATURE, 162 FORMING, 430 IMMATURE
- **Lifecycle Aggression v1**: 478 structures (FORMING + MATURE), 100% join coverage

## Next Steps

Recommended:
1. Group aggression timeframes into nano/micro/meso scales
2. Test alignment score predictive power for structure breaks
3. Analyze alignment delta as regime shift indicator
4. Correlate alignment patterns with successful ORB breakouts
