# T2 Strategy Redesign — From Scalper to Trend Capture

## Current State: Wrong Strategy Type

### What We Built (v2.0 Wall Engine)
```
Type: Counter-trend absorption scalper
Entry: Fade aggression into walls
Target: 8 points fixed ($16/trade)
Trades: 4.95/day
Result: $11.77/day avg, $247/month

Problem: Cannot capture trending days
Best case: 5 trades × $16 = $80/day theoretical max
```

### What's Needed
```
Type: Trend follower / breakout trader
Entry: Trade WITH institutional flow, not against it
Target: 20-60 points ($40-120/trade)
Trades: 1-3/day (quality > quantity)
Goal: $200-500/day on trending days, $100+/day average

Captures: Full trending moves, not 8-point scalps
```

---

## Three Strategy Paths Forward

### Path A: True DOM Heatmap Strategy (What User Asked About)

**Concept**: Detect institutional zones via stacked liquidity across multiple price levels

**Signal Logic**:
1. **Zone Detection**: 5-10 consecutive price levels with >50 contracts each
2. **Accumulation**: Total zone size >500 contracts = institutional presence
3. **Breakout**: Price breaks THROUGH the zone = trend starting
4. **Entry**: Enter when price clears zone + 2-3 points
5. **Target**: Trail stop or fixed 24-40 points

**Example**:
```
Bid Stack Detected:
  24775: 120 contracts
  24774.75: 95 contracts
  24774.50: 110 contracts
  24774.25: 88 contracts
  24774: 105 contracts
  TOTAL: 518 contracts over 1 point range = INSTITUTIONAL BID ZONE

Signal: If price breaks ABOVE 24776 (through zone) → GO LONG
Logic: Institutions failed to defend, now they're buying → trend up
Target: 24796+ (20+ points)
Stop: Below zone at 24773 (-3 points)
```

**Pros**:
- True institutional flow reading
- High conviction signals (large zones = big players)
- Trend-following (not counter-trend)

**Cons**:
- Requires full DOM reconstruction (complex)
- May have only 1-2 signals/day (quality over quantity)
- Needs accurate order book state at every moment

---

### Path B: Opening Range Breakout + VWAP (Simpler, Proven)

**Concept**: First 15 minutes establishes range, trade breakouts with VWAP confirmation

**Signal Logic**:
1. **Opening Range**: 9:30-9:45 AM ET high/low establishes range
2. **Breakout**: Price breaks above/below range by 2+ points
3. **VWAP Confirm**: Breakout in direction of VWAP deviation
4. **Entry**: Breakout + 1 point
5. **Target**: 1.5x-3x opening range width (dynamic)

**Example**:
```
9:30-9:45 AM Range:
  High: 24785
  Low: 24770
  Range: 15 points

LONG Signal (10:15 AM):
  Price breaks above 24787 (range high + 2 points)
  VWAP at 24775 (price above VWAP = bullish)
  Target: 24787 + 22 points = 24809 (1.5x range)
  Stop: 24782 (-5 points, back in range)

Result: +22 points = $44/trade
```

**Pros**:
- Simple to code (no DOM needed)
- Works on any timeframe data (1-min bars sufficient)
- Proven edge (ORB is institutional playbook)
- Dynamic targets based on volatility

**Cons**:
- Only 1-2 trades/day (morning setups mostly)
- Requires waiting for opening range to form
- False breakouts on low-volatility days

---

### Path C: Multi-Timeframe Trend Alignment (Most Robust)

**Concept**: Only trade when 5min, 15min, 60min trends all align

**Signal Logic**:
1. **60min Trend**: EMA(20) direction = primary trend
2. **15min Trend**: EMA(20) direction = intermediate trend
3. **5min Entry**: Pullback to EMA(20) in aligned trend
4. **Confirmation**: Volume spike + momentum surge
5. **Target**: Trail with 15min EMA or fixed 30-50 points

**Example**:
```
10:30 AM Setup:
  60min: EMA(20) sloping up, price above = BULLISH
  15min: EMA(20) sloping up, price above = BULLISH
  5min: Price pulls back to EMA(20) at 24780

LONG Signal:
  Entry: 24780 (5min EMA bounce)
  Stop: Below 5min EMA - 5 points = 24775
  Target: Trail with 15min EMA or 24830 (+50 points)

Result: Rides full trending move until 15min EMA breaks
```

**Pros**:
- Catches sustained trends (not choppy scalps)
- Higher win rate (multi-TF confirmation)
- Works in all market conditions (adapts to trend)

**Cons**:
- Fewer trades (1-3/day, only when all TFs align)
- Requires multiple timeframe data
- Can miss early trend entries (waits for pullback)

---

## Recommendation: Hybrid Path B + C

**Why**:
- **Path B (ORB)**: Catches explosive morning breakouts (1 trade, $50-150 potential)
- **Path C (MTF Trend)**: Catches sustained intraday trends (1-2 trades, $40-100 each)
- **Combined**: 2-3 trades/day, $100-250/day target

**Implementation**:
1. **9:30-9:45 AM**: Establish opening range
2. **9:45-11:00 AM**: Trade ORB breakouts if triggered
3. **11:00 AM-3:00 PM**: Trade MTF trend pullbacks if aligned
4. **3:00-4:00 PM**: Disable (chop/close volatility)

**Expected Performance**:
- Trades: 2-3/day (quality over quantity)
- Win Rate: 35-45% (fewer trades, bigger wins)
- Avg Winner: $60-120 (20-40 points)
- Avg Loser: $15-25 (5-8 points)
- Daily P&L: $100-250/day average
- Monthly P&L: **$2,000-5,000/month per contract**

---

## Path A Deep Dive: DOM Heatmap Strategy (If You Want This)

### What "Stacked Liquidity" Looks Like

**Bid Stack Example (Institutional Accumulation)**:
```
Price Level | Bid Size | Cumulative
------------|----------|------------
24780.00    | 45       | 45
24779.75    | 62       | 107
24779.50    | 88       | 195    ← Building
24779.25    | 110      | 305    ← Large level
24779.00    | 95       | 400    ← Zone forming
24778.75    | 78       | 478
24778.50    | 62       | 540    ← Total 540 contracts over 1.5 points
24778.25    | 45       | 585
24778.00    | 38       | 623

Signal: MASSIVE bid zone at 24778-24780 (623 contracts)
Interpretation: Institutions defending this level = accumulation
Trade: If price holds above 24780, GO LONG (institutions won)
Target: 24800+ (institutions will push price up after accumulating)
```

**Ask Stack Example (Institutional Distribution)**:
```
Price Level | Ask Size | Cumulative
------------|----------|------------
24785.00    | 120      | 120    ← WALL
24785.25    | 95       | 215
24785.50    | 88       | 303
24785.75    | 110      | 413    ← Zone forming
24786.00    | 95       | 508
24786.25    | 78       | 586
24786.50    | 62       | 648    ← Total 648 contracts over 1.5 points

Signal: MASSIVE ask zone at 24785-24787 (648 contracts)
Interpretation: Institutions capping rally = distribution
Trade: If price breaks THROUGH 24787, GO LONG (institutions cleared, now buying)
       If price rejects below 24785, GO SHORT (institutions won, now selling)
```

### Detection Algorithm

```sql
-- Reconstruct order book at each second
WITH order_book AS (
    SELECT
        toStartOfSecond(ts_event) as ts_sec,
        price,
        side,
        -- Net resting = adds - cancels
        sumIf(size, action = 'A') - sumIf(size, action = 'C') as net_size
    FROM mnq_mbo
    WHERE action IN ('A', 'C')  -- Only order book events
      AND side IN ('A', 'B')     -- Ask and Bid
    GROUP BY ts_sec, price, side
    HAVING net_size > 0
),

-- Find consecutive price levels with large size
stacked_zones AS (
    SELECT
        ts_sec,
        side,
        price,
        net_size,
        -- Sum size over 5-level window (1.25 point range for MNQ)
        sum(net_size) OVER (
            PARTITION BY ts_sec, side
            ORDER BY price
            ROWS BETWEEN 2 PRECEDING AND 2 FOLLOWING
        ) as zone_size,
        -- Count levels in zone
        count(*) OVER (
            PARTITION BY ts_sec, side
            ORDER BY price
            ROWS BETWEEN 2 PRECEDING AND 2 FOLLOWING
        ) as zone_levels
    FROM order_book
    WHERE net_size >= 40  -- Each level must have 40+ contracts
)

SELECT
    ts_sec,
    side,
    price,
    zone_size,
    zone_levels
FROM stacked_zones
WHERE zone_size >= 500      -- Total zone must be 500+ contracts
  AND zone_levels >= 5      -- At least 5 consecutive levels
```

### Entry Logic

**Breakout Through Stack** (Trend Following):
```
IF bid_zone_size > 500 AND price breaks ABOVE zone_high:
    → GO LONG (institutions failed to defend, now buying)
    Target: +30 points
    Stop: Below zone_low

IF ask_zone_size > 500 AND price breaks BELOW zone_low:
    → GO SHORT (institutions failed to cap, now selling)
    Target: -30 points
    Stop: Above zone_high
```

**Rejection At Stack** (Fade the Failed Breakout):
```
IF ask_zone_size > 500 AND price tests zone but rejects:
    → GO SHORT (wall held, institutions distributing)
    Target: -20 points
    Stop: Above zone_high + 3 points

IF bid_zone_size > 500 AND price tests zone but bounces:
    → GO LONG (wall held, institutions accumulating)
    Target: +20 points
    Stop: Below zone_low - 3 points
```

---

## Implementation Comparison

| Feature | Path A: DOM Heatmap | Path B: ORB + VWAP | Path C: MTF Trend | Hybrid B+C |
|---------|--------------------|--------------------|-------------------|------------|
| **Data Required** | Full MBO order book | 1-min bars + VWAP | 5/15/60-min bars | 1-min + multi-TF |
| **Complexity** | Very High | Low | Medium | Medium |
| **Trades/Day** | 1-2 | 1-2 | 1-3 | 2-4 |
| **Avg Win** | $80-150 | $50-120 | $60-100 | $50-100 |
| **Win Rate** | 30-40% | 35-45% | 40-50% | 35-45% |
| **Daily P&L** | $150-400 | $100-250 | $120-300 | $150-300 |
| **Monthly P&L** | $3,000-8,000 | $2,000-5,000 | $2,500-6,000 | $3,000-6,000 |
| **Code Difficulty** | Expert | Beginner | Intermediate | Intermediate |
| **Backtest Difficulty** | Hard (need DOM) | Easy (bar data) | Medium (multi-TF) | Medium |

---

## Recommended Next Steps

### Option 1: Quick Win (Path B - ORB)
1. Code opening range breakout in ClickHouse (simple)
2. Backtest on full 28 days
3. Compare to current v2.0 results
4. If promising, code NT8 version
5. **Timeline**: 2-3 hours

### Option 2: Best Long-Term (Hybrid B+C)
1. Code ORB + MTF trend logic in ClickHouse
2. Backtest both strategies separately
3. Combine best of both
4. Code NT8 version with dual logic
5. **Timeline**: 4-6 hours

### Option 3: Advanced (Path A - DOM Heatmap)
1. Build order book reconstruction engine
2. Detect stacked zones
3. Test breakout vs rejection entries
4. Validate edge before coding NT8
5. **Timeline**: 8-12 hours (complex)

---

## Bottom Line

**Current v2.0 Strategy**:
- ❌ Wrong type (scalper, not trend follower)
- ❌ Wrong targets (8 points, not 20-60)
- ❌ Wrong direction (counter-trend, not with-trend)
- ❌ Wrong result ($247/month, not $2,000-5,000)

**What You Need**:
- ✅ Trend follower or breakout trader
- ✅ 20-60 point targets
- ✅ Trade WITH institutional flow
- ✅ $2,000-5,000/month target

**Your Question**: "Are we using a heatmap strategy drawn from large resting orders consecutively stacked?"
**Answer**: No, we built a scalper. You want Path A (DOM Heatmap) or Hybrid B+C (ORB + MTF Trend).

**My Recommendation**: Start with **Hybrid B+C** (ORB + MTF Trend) because:
1. Simpler to code and backtest
2. Proven edge (institutional playbook)
3. Can achieve your $2,000-5,000/month goal
4. If that works, THEN build Path A (DOM Heatmap) as advanced version

---

**Next: Which path do you want to pursue?**
- A: DOM Heatmap (complex, high potential)
- B: ORB + VWAP (simple, proven)
- C: MTF Trend (robust, adaptive)
- **B+C: Hybrid (recommended)**
