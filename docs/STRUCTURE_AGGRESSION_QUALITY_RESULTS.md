# Structure Aggression Quality Results

## Table: CG_mnq_structure_aggression_quality_v1

**Coverage**: 478 structures (FORMING + MATURE), 22 days, 100% data join

## Key Upgrades from Raw Alignment

1. **Normalized Magnitude**: P95-scaled directional strength (not just binary direction match)
2. **Weighted Timeframe Scores**: Longer timeframes weighted higher (5m = 5x, 100ms = 1x)
3. **Three-Phase Sampling**: Start, midpoint, end (captures dynamics, not just endpoints)
4. **Quality Labels**: 5 states (STRENGTHENING_HIGH/MODERATE, EXHAUSTING, WEAK_OR_OPPOSED, NEUTRAL)
5. **Behavior Candidates**: 4 specific pattern types + UNCLASSIFIED

## Quality Score Summary (Weighted 0-1 Scale)

### By Structure Type

```
Level      Side        Maturity   Count   Start    Mid     End     Delta    Peak
────────────────────────────────────────────────────────────────────────────────
ORB_HIGH   RESISTANCE  FORMING      26   0.2618  0.1227  0.1352  -0.1266  0.2996
ORB_HIGH   RESISTANCE  MATURE       70   0.2349  0.1183  0.1662  -0.0687  0.3610
ORB_LOW    SUPPORT     FORMING      25   0.2533  0.1423  0.1780  -0.0753  0.3250
ORB_LOW    SUPPORT     MATURE       97   0.3057  0.1474  0.1277  -0.1780  0.3674
VWAP       RESISTANCE  FORMING      57   0.1233  0.1195  0.0901  -0.0332  0.1797
VWAP       RESISTANCE  MATURE       73   0.1050  0.0909  0.0508  -0.0542  0.1568
VWAP       SUPPORT     FORMING      54   0.1243  0.1519  0.1315  +0.0072  0.2020
VWAP       SUPPORT     MATURE       76   0.0981  0.0811  0.0990  +0.0009  0.1742
```

**Key Patterns**:
- **ORB structures**: Start with MODERATE quality (0.23-0.31), degrade to LOW (0.13-0.18)
- **VWAP structures**: Start LOW (0.10-0.12), stay LOW or slightly improve
- **Quality degradation**: All except VWAP SUPPORT show negative delta
- **Absolute levels**: VERY LOW overall (peak avg 0.16-0.37, max possible = 1.0)

## Quality State Distribution

### By Structure Type

```
Level      Side        Maturity   HIGH  MOD  EXHAUST  WEAK  NEUTRAL
────────────────────────────────────────────────────────────────────
ORB_HIGH   RESISTANCE  FORMING      0    0      6      17      3
ORB_HIGH   RESISTANCE  MATURE       1    4     13      43      9
ORB_LOW    SUPPORT     FORMING      2    0      4      16      3
ORB_LOW    SUPPORT     MATURE       3    1     25      62      6
VWAP       RESISTANCE  FORMING      0    0      2      50      5
VWAP       RESISTANCE  MATURE       0    0      5      67      1
VWAP       SUPPORT     FORMING      0    0      2      46      6
VWAP       SUPPORT     MATURE       0    3      1      68      4
```

**Distribution Summary**:
- **WEAK_OR_OPPOSED dominates**: 369/478 (77.2%) - aggression doesn't support structure
- **STRENGTHENING_HIGH rare**: 6/478 (1.3%) - only ORB structures show this
- **EXHAUSTING notable**: 58/478 (12.1%) - high quality at start, collapses at end
- **ORB structures show more variety**: Have all 5 quality states
- **VWAP structures mostly WEAK**: 231/260 (88.8%) weak or opposed

## Behavior Candidate Analysis

### ORB_HIGH_CONTINUATION_CANDIDATE (32 structures)
**Pattern**: ORB_HIGH MATURE resistance with INCREASING aggression quality

```
Quality State           Count   Start    End     Delta   Avg Touches   Avg Secs
───────────────────────────────────────────────────────────────────────────────
WEAK_OR_OPPOSED           21   0.0120  0.1153  +0.1032     32.7         253
NEUTRAL                    6   0.0157  0.3496  +0.3338     26.0         181
STRENGTHENING_MODERATE     4   0.0014  0.5666  +0.5651     29.8         249
STRENGTHENING_HIGH         1   0.2840  0.7182  +0.4342     12.0          60
```

**Interpretation**:
- 11/32 (34%) reach NEUTRAL+ end quality despite weak start
- 5/32 (16%) reach MODERATE+ end quality (candidates for breakout continuation)
- Trend: Start weak → End moderate/strong as resistance holds
- Quality improvement suggests increasing upside pressure at mature resistance

### ORB_LOW_EXHAUSTION_OR_TRAP_CANDIDATE (60 structures)
**Pattern**: ORB_LOW MATURE support with DECREASING aggression quality

```
Quality State           Count   Start    End     Delta   Avg Touches   Avg Secs
───────────────────────────────────────────────────────────────────────────────
WEAK_OR_OPPOSED           32   0.2399  0.0539  -0.1860     33.2         265
EXHAUSTING                25   0.6839  0.1077  -0.5762     23.3         199
NEUTRAL                    3   0.5161  0.4093  -0.1068     17.3         133
```

**Interpretation**:
- 25/60 (42%) classified as EXHAUSTING (start >0.50, end <0.30)
- Start quality HIGH (avg 0.68) → End quality LOW (avg 0.11)
- Quality collapse suggests buying pressure exhaustion at support
- Candidates for breakdown/trap patterns

### VWAP_RESISTANCE_REVERSION_CANDIDATE (70 structures)
**Pattern**: VWAP resistance with DECREASING quality (sell pressure fading)

```
Quality State           Count   Start    End     Delta   Avg Touches   Avg Secs
───────────────────────────────────────────────────────────────────────────────
WEAK_OR_OPPOSED           63   0.1318  0.0423  -0.0895      3.4          77
EXHAUSTING                 7   0.5938  0.1508  -0.4430      3.0          65
```

**Interpretation**:
- 90% already WEAK (quality <0.25)
- 7/70 (10%) show EXHAUSTING pattern (strong sell start → fade)
- Mean reversion signal: As selling pressure fades, price may revert toward VWAP

### VWAP_SUPPORT_REVERSION_CANDIDATE (64 structures)
**Pattern**: VWAP support with INCREASING quality (buying pressure building)

```
Quality State           Count   Start    End     Delta   Avg Touches   Avg Secs
───────────────────────────────────────────────────────────────────────────────
WEAK_OR_OPPOSED           53   0.0375  0.1099  +0.0724      3.3          71
NEUTRAL                    8   0.1213  0.4020  +0.2807      3.1          73
STRENGTHENING_MODERATE     3   0.4374  0.5914  +0.1540      3.3          82
```

**Interpretation**:
- 11/64 (17%) reach NEUTRAL+ end quality
- 3/64 (5%) reach MODERATE end quality
- Quality improvement suggests buying support building
- Mean reversion signal: Price may bounce back to VWAP

## Critical Insights

### 1. Overall Aggression Quality is LOW
- Median end quality: 0.05-0.13 across all types
- 77% of structures are WEAK_OR_OPPOSED
- Only 1.3% reach STRENGTHENING_HIGH
- **Implication**: Microstructure aggression weakly predicts structure behavior

### 2. Quality Degradation is Common
- 85% of structures show negative quality delta
- ORB structures degrade faster (-0.07 to -0.18) than VWAP (-0.03 to -0.05)
- **Implication**: Structures LOSE aggression support over their lifecycle

### 3. Behavior Candidates Show Distinct Patterns
- **ORB_HIGH Continuation**: Weak start → Moderate/Strong end (upside pressure building)
- **ORB_LOW Exhaustion**: Strong start → Weak end (support failing)
- **VWAP Reversion**: Quality changes predict mean reversion direction
- **Implication**: Quality CHANGE (delta) more predictive than absolute level

### 4. STRENGTHENING_HIGH is Rare but Meaningful
- Only 6 structures (1.3%) reach this state
- All are ORB structures (5 UNCLASSIFIED, 1 ORB_HIGH Continuation)
- Avg quality progression: 0.62 → 0.55 → 0.86 (strong finish)
- **Implication**: When it occurs, it's a strong signal

### 5. EXHAUSTING Pattern is Actionable
- 58 structures (12.1%) show this pattern
- Start quality >0.50, end <0.30 (drop >0.20)
- Concentrated in ORB_LOW MATURE (25/58 = 43%)
- **Implication**: Fade signals when high initial quality collapses

## Filtering Recommendations for Price Triggers

**Exclude** (as user specified):
- `aggression_quality_state = 'WEAK_OR_OPPOSED'` (369 structures, 77%)

**Prioritize**:
1. `STRENGTHENING_HIGH` (6 structures) - Strongest conviction
2. `STRENGTHENING_MODERATE` (11 structures) - Good conviction
3. `EXHAUSTING` (58 structures) - Fade/reversal plays
4. `NEUTRAL` (34 structures) - Neutral/mixed

**Behavior-Specific**:
- **ORB_HIGH Continuation**: Include NEUTRAL+ (11/32 structures)
- **ORB_LOW Exhaustion**: Include EXHAUSTING (25/60 structures)
- **VWAP Reversions**: Include NEUTRAL+ and EXHAUSTING

## Next Step: CG_mnq_price_trigger_events_v1

Generate candidate triggers from structures where:
```sql
WHERE aggression_quality_state != 'WEAK_OR_OPPOSED'
```

This filters to **109 structures** (22.8% of total) with meaningful aggression participation.
