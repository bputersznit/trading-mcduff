# Bar Aggregation Alternatives

## Overview

Instead of processing 12-22M individual MBO events, aggregate into tradeable bars.

---

## ✅ **1-Minute Time Bars** (IMPLEMENTED)

**SQL:** `sql/CGCl_create_1min_bars_view.sql`

**Specs:**
- **420 bars per day** (7 hours × 60 minutes)
- **~30K-50K events per bar**
- Updates every 60 seconds

**Pros:**
- ✅ Standard, well-understood timeframe
- ✅ Perfect for intraday swing (1-15 trades/day)
- ✅ Captures trends while filtering noise
- ✅ Fast to process

**Cons:**
- ❌ Might miss very fast scalps (< 1 minute)
- ❌ Fixed time = variable volume per bar

**Use For:**
- Intraday swing trading (hold minutes-hours)
- Trend following
- Most strategies

---

## 2. **5-Minute Time Bars**

**SQL:**
```sql
CREATE VIEW mnq_5min_bars_orderflow AS
SELECT
    toStartOfFiveMinutes(ts_event) as bar_time,
    symbol,
    -- OHLC
    argMinIf(price, ts_event, action IN ('T', 'F')) as open,
    maxIf(price, action IN ('T', 'F')) as high,
    minIf(price, action IN ('T', 'F')) as low,
    argMaxIf(price, ts_event, action IN ('T', 'F')) as close,
    -- Order flow
    sumIf(size, action = 'T' AND side = 'A') as buy_volume,
    sumIf(size, action = 'T' AND side = 'B') as sell_volume,
    sumIf(size, action = 'T' AND side = 'A') - sumIf(size, action = 'T' AND side = 'B') as delta,
    count(*) as event_count
FROM mnq_mbo
WHERE symbol = 'MNQZ5'
  AND hour(ts_event) >= 9
  AND hour(ts_event) < 16
GROUP BY toStartOfFiveMinutes(ts_event), symbol
ORDER BY bar_time;
```

**Specs:**
- **84 bars per day** (7 hours × 12 per hour)
- **~150K-250K events per bar**

**Pros:**
- ✅ Smoother price action
- ✅ Better trend clarity
- ✅ Even faster to process

**Cons:**
- ❌ Less granular
- ❌ Slower entry/exit timing

**Use For:**
- Longer-term swing trades (hold hours)
- Clearer trend identification
- When 1-min is too noisy

---

## 3. **Volume Bars**

**Concept:** New bar every N contracts traded (e.g., every 1000 contracts)

**SQL:**
```sql
-- This requires cumulative sum and is more complex
-- Pseudo-code:
SELECT
    -- Group events into chunks of 1000 volume
    floor(sum(size) / 1000) as bar_id,
    min(ts_event) as bar_start,
    max(ts_event) as bar_end,
    -- OHLC, delta, etc.
    ...
FROM mnq_mbo
WHERE symbol = 'MNQZ5'
GROUP BY bar_id;
```

**Specs:**
- **Variable bars per day** (more during high volume)
- **Consistent volume per bar** (e.g., 1000 contracts each)

**Pros:**
- ✅ Adapts to market activity
- ✅ More bars during volatility (when you want to trade)
- ✅ Fewer bars during quiet periods
- ✅ Better for volume-based strategies

**Cons:**
- ❌ More complex to implement
- ❌ Variable time duration (hard to visualize)
- ❌ Harder to backtest (timing issues)

**Use For:**
- Volume profile analysis
- High-frequency strategies
- When volume matters more than time

---

## 4. **Dollar Bars**

**Concept:** New bar every $X million traded (price × volume)

Similar to volume bars but accounts for price changes.

**SQL:**
```sql
-- Group by cumulative dollar volume
floor(sum(price * size) / 1000000) as bar_id
```

**Specs:**
- **Variable bars per day**
- **Consistent dollar volume**

**Pros:**
- ✅ Better than volume bars (accounts for price)
- ✅ More bars at high prices/volume

**Cons:**
- ❌ Complex
- ❌ Variable timing

**Use For:**
- Professional quant strategies
- Research

---

## 5. **Tick Bars**

**Concept:** New bar every N ticks (price changes)

**SQL:**
```sql
-- Count distinct price changes
-- Group every 100 price changes
floor(count(DISTINCT price) / 100) as bar_id
```

**Specs:**
- **Variable bars per day**
- **Consistent number of price changes**

**Pros:**
- ✅ Adapts to volatility
- ✅ More bars during fast markets
- ✅ Good for scalping

**Cons:**
- ❌ Complex
- ❌ Hard to interpret

**Use For:**
- High-frequency scalping
- Tick-based strategies

---

## 6. **Renko Bars**

**Concept:** New bar only when price moves N points (ignores time)

**Specs:**
- **Variable bars per day**
- **Fixed price movement** (e.g., 10 points per bar)

**Pros:**
- ✅ Pure price action
- ✅ Filters noise
- ✅ Clear trends

**Cons:**
- ❌ Doesn't capture time/volume
- ❌ Can miss reversals
- ❌ Hard to implement with MBO data

**Use For:**
- Pure trend following
- Noise filtration

---

## 7. **Imbalance Bars**

**Concept:** New bar when cumulative buy/sell imbalance exceeds threshold

**SQL:**
```sql
-- Group when abs(cumulative_delta) > 500
-- This requires window functions
```

**Specs:**
- **Variable bars**
- **Triggered by order flow imbalance**

**Pros:**
- ✅ Captures regime changes
- ✅ More bars during trending markets
- ✅ Fewer during choppy

**Cons:**
- ❌ Very complex
- ❌ Requires stateful processing

**Use For:**
- Advanced order flow strategies
- Regime detection

---

## **RECOMMENDATION FOR YOUR SYSTEM:**

### For Starting Out:
**Use 1-Minute Bars** (already implemented)
- Simple, standard, fast
- 420 bars per day is perfect
- Works for swing trading (your goal: 5-15 trades/day on swing, 1-4 on trend)

### Later Enhancements:
1. **5-Minute Bars** - for longer-term swing trades
2. **Volume Bars** - if you want volume-weighted strategies

---

## Implementation Priority:

**Phase 1 (NOW):** ✅
- ✅ 1-minute time bars
- ✅ Backtest on 1-min bars
- ✅ Prove strategy works

**Phase 2 (LATER):**
- 5-minute bars for comparison
- Volume bars for advanced strategies

**Phase 3 (ADVANCED):**
- Dollar bars
- Imbalance bars
- Custom aggregations

---

## Bar Count Comparison:

| Bar Type | Bars/Day | Events/Bar | Processing Time | Complexity |
|----------|----------|------------|-----------------|------------|
| **Raw MBO** | 12-22M | 1 | Hours | High |
| **1-Min** | **420** | **30K-50K** | **Seconds** | **Low** ✅ |
| **5-Min** | 84 | 150K-250K | < 1 second | Low |
| **Volume** | ~200-400 | Variable | Seconds | Medium |
| **Dollar** | ~200-400 | Variable | Seconds | Medium |
| **Tick** | ~500-1000 | Variable | Seconds | Medium |

---

## Next Step:

Create backtest script that uses **1-minute bars** instead of raw MBO events.

This will:
- Run in **seconds** instead of minutes
- Process 420 bars instead of 12M events
- Still capture trends, delta, imbalances
- Enable realistic 5-15 trades per day on swing, 1-4 on trend days

Ready to create the 1-min bar backtest?
