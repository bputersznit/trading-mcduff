# L2 Market Depth Analysis - March 2, 2026

## Dataset Summary
- **Total Events**: 820,869
- **Time Range**: 07:40:00 → 09:01:30 (1h 21m)
- **Instrument**: MNQ June 2026
- **Depth Levels**: 0-9 (10 levels)

## Event Distribution
| Operation | Events | % | Description |
|-----------|--------|---|-------------|
| Add (A) | 527,702 | 64.3% | New orders entering book |
| Remove (R) | 264,303 | 32.2% | Orders cancelled/filled |
| Update (U) | 28,864 | 3.5% | Order size modifications |

**Key Insight**: 2:1 Add/Remove ratio indicates active order placement with frequent cancellations.

## Bid/Ask Balance
| Side | Events | % | Avg Size | Max Size |
|------|--------|---|----------|----------|
| Bid | 293,953 | 52.8% | 3.58 | 165 |
| Ask | 262,613 | 47.2% | 3.40 | 95 |

**Key Insight**: Slight bid dominance (5% more events), suggesting mild buying pressure.

## Depth Level Activity
| Level | Updates | Avg Size | Notes |
|-------|---------|----------|-------|
| 0 (TOB) | 200,687 | 1.15 | Top of book - most liquid |
| 1 | 40,766 | 1.73 | |
| 2 | 21,345 | 2.86 | |
| 3-8 | ~28,000 | 4-9 | Deeper liquidity, larger sizes |
| 9 | 266,111 | 5.27 | **Heavy activity at max depth** |

**Key Insight**: Level 9 shows unusually high activity (266K events) - likely refresh/restack behavior at visible depth limit.

## Top of Book (Level 0-1)
| Side | Events | Avg Size | Price Range |
|------|--------|----------|-------------|
| Bid | 118,328 | 1.25 | 24,808.00 → 25,112.50 (304.5 pts) |
| Ask | 123,125 | 1.24 | 24,814.50 → 25,116.00 (301.5 pts) |

**Key Insight**:
- TOB sizes are small (~1.2 contracts avg)
- ~300 point range suggests volatile pre-market into RTH open

## Market Microstructure - Time Series

### Pre-Market (07:40 - 08:29)
- **Avg ops/sec**: 30-130
- **Pattern**: Gradual increase approaching RTH open
- **Peak**: 08:03 @ 128.6 ops/sec (early morning spike)

### RTH Open (08:30+)
- **Immediate spike**: 364.8 ops/sec @ 08:30
- **Sustained rate**: 300-400 ops/sec
- **Peak**: 433.6 ops/sec @ 08:52

### Activity Breakdown
```
Pre-RTH  (07:40-08:29): ~50 ops/sec avg
RTH Open (08:30-09:01): ~350 ops/sec avg

** 7x increase in order book activity at market open **
```

## Order Book Churn Patterns

**2:1 Add/Remove Ratio**: For every 100 orders added, ~50 are removed
- Indicates quote stuffing / HFT market-making activity
- High churn = low "resting" time for orders

**Update Rate (3.5%)**: Low update frequency suggests most changes via cancel/replace rather than size modifications

## Data Quality
✓ Clean capture - no gaps in timestamp sequence
✓ All depth levels represented (0-9)
✓ Both bid/ask sides captured
✓ Pre-market + RTH open covered

## Next Steps
1. **Reconstruct order book state** - Build full L2 ladder from events
2. **Measure spread dynamics** - Track bid-ask spread tick-by-tick
3. **Liquidity imbalance** - Calculate cumulative bid/ask depth over time
4. **Price impact** - Correlate book changes with price moves
5. **HFT signature detection** - Identify quote stuffing patterns

## Storage Efficiency
- CSV: 34.5 MB (before compression)
- Parquet: 3.9 MB
- **Compression: 8.8x**
- ClickHouse: Indexed, queryable at ~10M rows/sec

## Recommended Capture Strategy
Based on this analysis:
- **Chunk size**: 50K rows is optimal (~1-2 min of RTH data)
- **Depth levels**: Keep all 10 levels (L9 has unique signal)
- **Conversion cadence**: Convert every 30 min during replay to keep CSV size manageable
- **Target**: Full RTH session = ~1.5M events = ~75 chunks = ~15 MB Parquet
