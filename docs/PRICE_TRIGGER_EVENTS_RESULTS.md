# Price Trigger Events Results

## Table: CG_mnq_price_trigger_events_v1

**Coverage**: 109 trigger events from 109 qualified structures (non-WEAK, 22 days)

## Trigger Generation Logic

**Source**: Structures where `aggression_quality_state != 'WEAK_OR_OPPOSED'`
- Filtered out: 369 structures (77%)
- Generated triggers: 109 structures (23%)

**Trigger Time**: Structure end time (when pattern completes)

**Classification**:
1. **Trigger Type**: 12 specific patterns (8 actionable + 4 watch-only)
2. **Trigger Side**: LONG (21), SHORT (51), WATCH (37)
3. **Trigger Family**:
   - CONTINUATION_OR_REVERSION_WITH_SUPPORT (14 triggers)
   - FAILURE_OR_FADE (58 triggers)
   - WATCH_ONLY (37 triggers)

## Priority Scoring (0-11 scale)

**Components**:
- Aggression quality: 2-5 (NEUTRAL=2, EXHAUSTING=3, MODERATE=4, HIGH=5)
- Maturity: 1-2 (FORMING=1, MATURE=2)
- Structure type: 1-2 (VWAP=1, ORB=2)
- Quality delta bonus: +1 if positive

**Distribution**:
- **Highest priority (10)**: 4 triggers (STRENGTHENING_HIGH + MATURE + ORB)
- **High priority (9)**: 5 triggers (STRENGTHENING_MODERATE + MATURE + ORB)
- **Medium priority (7-8)**: 28 triggers
- **Low priority (5-6)**: 72 triggers

## Trigger Breakdown by Priority

### Highest Priority (Score 10) - 4 Triggers

```
Trigger Type                        Side   Level    Maturity   Quality State         Count   End Quality   Delta
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────
LONG_ORB_LOW_RECLAIM_SUPPORT        LONG   ORB_LOW  MATURE     STRENGTHENING_HIGH      3      0.8805       +0.2528
LONG_ORB_HIGH_BREAKOUT_CONTINUATION LONG   ORB_HIGH MATURE     STRENGTHENING_HIGH      1      0.7182       +0.4342
```

**Characteristics**:
- All LONG side
- All ORB structures (3 ORB_LOW, 1 ORB_HIGH)
- All MATURE
- All STRENGTHENING_HIGH (end quality 0.72-0.88)
- Positive quality delta (+0.25 to +0.43)

### High Priority (Score 9) - 5 Triggers

```
Trigger Type                        Side   Level    Maturity   Quality State            Count   End Quality   Delta
────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
LONG_ORB_HIGH_BREAKOUT_CONTINUATION LONG   ORB_HIGH MATURE     STRENGTHENING_MODERATE     4      0.5666       +0.5651
LONG_ORB_LOW_RECLAIM_SUPPORT        LONG   ORB_LOW  FORMING    STRENGTHENING_HIGH         2      0.8278       +0.2110
LONG_ORB_LOW_RECLAIM_SUPPORT        LONG   ORB_LOW  MATURE     STRENGTHENING_MODERATE     1      0.6940       +0.0647
```

**Characteristics**:
- All LONG side
- All ORB structures
- Mix of MATURE (5) and FORMING (2)
- STRENGTHENING_MODERATE or HIGH
- High end quality (0.57-0.83)
- Positive delta

### Medium Priority (Score 7-8) - 28 Triggers

**FAILURE_OR_FADE Family** (25 triggers):
```
SHORT_ORB_LOW_SUPPORT_FAILURE       SHORT  ORB_LOW  MATURE     EXHAUSTING               25      0.1077       -0.5762
SHORT_ORB_HIGH_REJECTION_FADE       SHORT  ORB_HIGH MATURE     EXHAUSTING               13      0.1145       -0.6616
```

**CONTINUATION_OR_REVERSION_WITH_SUPPORT** (3 triggers):
```
LONG_VWAP_SUPPORT_REVERSION         LONG   VWAP     MATURE     STRENGTHENING_MODERATE    3      0.5914       +0.1540
```

**Characteristics**:
- Dominated by SHORT fade/failure plays (38 total SHORT)
- EXHAUSTING quality (started strong, collapsed)
- Low end quality (0.11-0.24)
- Large negative delta (-0.48 to -0.66)

### WATCH_ONLY (Score 4-7) - 37 Triggers

```
Quality State   Level    Side   Count   End Quality   Delta
────────────────────────────────────────────────────────────
NEUTRAL         ORB_HIGH WATCH    12      0.48        +0.18
NEUTRAL         ORB_LOW  WATCH     9      0.39        +0.10
NEUTRAL         VWAP     WATCH    16      0.39        +0.20
```

**Characteristics**:
- Mixed directional conviction
- Moderate end quality (0.39-0.48)
- Positive delta (quality improving)
- For monitoring, not immediate action

## Side Distribution Analysis

### LONG Triggers (21 total, 19.3%)

```
Avg Priority: 7.95
Avg End Quality: 0.5145
Avg Delta: +0.26

Breakdown:
  STRENGTHENING_HIGH:     6 triggers (end quality 0.72-0.88)
  STRENGTHENING_MODERATE: 8 triggers (end quality 0.57-0.69)
  EXHAUSTING:             7 triggers (end quality 0.06-0.24)
```

**Pattern**: Dominated by high-conviction continuations (14/21 = 67%)

### SHORT Triggers (51 total, 46.8%)

```
Avg Priority: 6.71
Avg End Quality: 0.1428
Avg Delta: -0.54

Breakdown:
  EXHAUSTING:            51 triggers (all are fade/failure plays)
    - ORB_LOW Support Failure:  29
    - ORB_HIGH Rejection Fade:  19
    - VWAP Support Failure:      3
```

**Pattern**: Exclusively EXHAUSTING (quality collapse signals)

### WATCH Triggers (37 total, 33.9%)

```
Avg Priority: 5.81
Avg End Quality: 0.4163
Avg Delta: +0.17

All NEUTRAL quality state
```

## Trigger Type Frequency

### Actionable Triggers (72 total)

**LONG Side** (21):
```
LONG_ORB_LOW_RECLAIM_SUPPORT         6
LONG_ORB_HIGH_BREAKOUT_CONTINUATION  5
LONG_VWAP_SUPPORT_REVERSION          3
LONG_VWAP_RESISTANCE_RECLAIM         7
```

**SHORT Side** (51):
```
SHORT_ORB_LOW_SUPPORT_FAILURE       29
SHORT_ORB_HIGH_REJECTION_FADE       19
SHORT_VWAP_SUPPORT_FAILURE           3
```

### WATCH_ONLY Triggers (37)

```
WATCH_ORB_HIGH_NEUTRAL              12
WATCH_ORB_LOW_NEUTRAL                9
WATCH_VWAP_SUPPORT_NEUTRAL          10
WATCH_VWAP_RESISTANCE_NEUTRAL        6
```

## Key Insights

### 1. SHORT Dominates via Exhaustion

- 51/72 actionable triggers (71%) are SHORT
- All SHORT triggers are EXHAUSTING (quality collapse)
- Pattern: Strong buying/selling support → Fades → Failure expected
- **Implication**: Market shows more exhaustion setups than continuation

### 2. LONG Quality is Much Higher

- LONG avg end quality: 0.51 vs SHORT: 0.14 (3.6x higher)
- LONG avg priority: 7.95 vs SHORT: 6.71
- LONG shows quality GAIN (+0.26), SHORT shows COLLAPSE (-0.54)
- **Implication**: LONG setups have stronger conviction

### 3. ORB Structures Dominate High Priority

- Top 9 priority triggers: 100% ORB structures
- ORB_LOW Support plays: 6 LONG triggers (highest conviction)
- ORB_HIGH Breakout: 5 LONG triggers
- **Implication**: ORB structures more actionable than VWAP

### 4. VWAP Triggers are Lower Priority

- Only 3 VWAP triggers in top priority tier
- Most VWAP triggers are WATCH or EXHAUSTING
- VWAP SUPPORT REVERSION: 3 triggers (all MODERATE quality)
- **Implication**: VWAP shows weaker conviction overall

### 5. Two Distinct Trigger Families

**Family A: CONTINUATION_OR_REVERSION_WITH_SUPPORT** (14 triggers)
- Pattern: Quality STRENGTHENING at structure end
- Side: Mostly LONG (11 LONG, 3 SHORT)
- Priority: High (avg 8.6)
- Logic: Aggression supports the structure, expect continuation/reversion

**Family B: FAILURE_OR_FADE** (58 triggers)
- Pattern: Quality EXHAUSTING (started strong, collapsed)
- Side: Mostly SHORT (51 SHORT, 7 LONG)
- Priority: Medium (avg 6.5)
- Logic: Aggression exhausted, expect failure/reversal

### 6. WATCH_ONLY Characteristics

- 37 triggers (33.9% of total)
- All NEUTRAL quality
- Positive quality delta (improving but not strong enough)
- For monitoring market structure, not immediate trades
- Could become actionable if quality continues improving

## Priority Filtering Recommendations

**Tier 1 (Score 9-10)**: 9 triggers
- All LONG side
- All ORB structures
- All STRENGTHENING (HIGH or MODERATE)
- Highest conviction plays

**Tier 2 (Score 7-8)**: 28 triggers
- Mostly SHORT (25/28)
- Mix of ORB (38) and VWAP (3)
- EXHAUSTING or STRENGTHENING
- Medium conviction

**Tier 3 (Score 5-6)**: 72 triggers
- Includes all WATCH triggers (37)
- Mixed conviction
- Lower priority

## Next Step: CG_mnq_price_trigger_single_position_v1

Enforce deployment rules:
- **1 MNQ contract maximum** (no overlapping positions)
- **Highest priority wins** when multiple triggers at same time
- **Sequential walk-forward** through trigger times
- Track position state (flat vs in-position)
- Calculate actual PnL with slippage and commissions

Expected row count: ≤109 (de-duplicated by time, highest priority wins)
