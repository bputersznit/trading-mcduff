# Bookmap-Style Trading Strategies for MNQ Order Flow

## Overview

Using the `mnq_orderflow_1sec` table to implement professional Bookmap strategies:
- Absorption detection
- Iceberg identification
- Thin liquidity breakouts
- Liquidity wall reversals
- Spoofing detection
- Stop run identification
- Velocity + Delta momentum

---

## 1. ABSORPTION DETECTION

**Concept:** Heavy aggression hits a level but price doesn't move = large passive participant absorbing flow

**Signal:** Potential reversal/bounce

### Detection Query (Bid Absorption):
```sql
SELECT
    timestamp_1sec,
    price,
    sell_aggressor_volume as selling_pressure,
    bid_adds as bid_response,
    net_resting_bid as absorption_power,
    total_volume
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQZ5'
  AND sell_aggressor_volume > 80      -- Heavy selling
  AND bid_adds > sell_aggressor_volume * 1.3  -- Strong bid response
  AND net_resting_bid > 50            -- Net absorption
  AND total_volume > 100
ORDER BY sell_aggressor_volume DESC
LIMIT 20;
```

### Real Example (2025-10-10):
```
Time: 10:23:28
Price: 25336
Selling Pressure: 186 contracts
Bid Response: 246 contracts added
Net Absorption: +83 contracts
Total Volume: 367

→ Buyers absorbed 186 contracts of selling with 246 adds
→ Price held at 25336 (potential bounce level)
```

**Trade Setup:**
- Entry: When absorption confirmed (bid_adds > selling * 1.3)
- Direction: LONG (fade the selling)
- Stop: Below absorption level (-5-10 points)
- Target: Previous resistance or +10-15 points

---

## 2. ICEBERG DETECTION

**Concept:** Large hidden orders showing small visible size but absorbing heavy flow

**Signal:** Institutional accumulation/distribution level

### Detection Query:
```sql
SELECT
    timestamp_1sec,
    price,
    total_volume,
    bid_adds + ask_adds as visible_adds,
    round(total_volume / NULLIF(bid_adds + ask_adds, 0), 2) as iceberg_ratio,
    CASE
        WHEN sell_aggressor_volume > buy_aggressor_volume * 1.5 THEN 'BID_ICEBERG'
        WHEN buy_aggressor_volume > sell_aggressor_volume * 1.5 THEN 'ASK_ICEBERG'
        ELSE 'BALANCED'
    END as iceberg_side
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQZ5'
  AND total_volume > 100
  AND total_volume / (bid_adds + ask_adds) > 5.0  -- High ratio = iceberg
ORDER BY iceberg_ratio DESC
LIMIT 20;
```

### Top Icebergs (2025-10-10):
```
1. 16:50:33 @ 24300   - Ratio: 146x  (146 volume, 1 visible add)
2. 10:59:15 @ 25150   - Ratio: 40.67x (244 volume, 6 adds)
3. 11:03:43 @ 25053.75 - Ratio: 37x   (370 volume, 10 adds)
4. 10:58:16 @ 25200   - Ratio: 34.57x (242 volume, 7 adds)
5. 11:01:21 @ 25064   - Ratio: 34.33x (206 volume, 6 adds)
```

**Trade Setup:**
- Entry: When iceberg confirmed (ratio > 10x, price tests level)
- Direction: Trade WITH the iceberg (it's defending/accumulating)
- Stop: Beyond iceberg level (they'll likely pull if broken)
- Target: Mean reversion or continuation in iceberg direction

---

## 3. THIN LIQUIDITY BREAKOUT

**Concept:** Low opposing liquidity + aggressive flow = rapid price movement

**Signal:** Breakout continuation

### Detection Query:
```sql
SELECT
    timestamp_1sec,
    price,
    buy_aggressor_volume,
    sell_aggressor_volume,
    total_volume,
    bid_adds + ask_adds as total_liquidity,
    CASE
        WHEN buy_aggressor_volume > sell_aggressor_volume * 2 THEN 'BULL_BREAKOUT'
        WHEN sell_aggressor_volume > buy_aggressor_volume * 2 THEN 'BEAR_BREAKOUT'
        ELSE 'MIXED'
    END as breakout_direction
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQZ5'
  AND total_volume > 100
  AND (buy_aggressor_volume > 100 OR sell_aggressor_volume > 100)
  AND bid_adds + ask_adds < 50  -- Thin liquidity
ORDER BY total_volume DESC;
```

### Top Breakouts (2025-10-10):
```
1. 15:35:16 @ 24500 - 276 buy agg, 39 liquidity → BULL (Ratio: 7.1x)
2. 14:52:27 @ 24600 - 275 buy agg, 36 liquidity → BULL (Ratio: 7.6x)
3. 11:04:24 @ 25000 - 269 buy agg, 35 liquidity → BULL (Ratio: 7.7x)
4. 11:30:03 @ 24800 - 228 buy agg, 38 liquidity → BULL (Ratio: 6.0x)
5. 11:08:58 @ 25000 - 212 buy agg, 14 liquidity → BULL (Ratio: 15.1x!)
```

**Trade Setup:**
- Entry: On breakout confirmation (aggression >> liquidity)
- Direction: Follow aggression (BULL/BEAR)
- Stop: Back inside breakout level (-3-5 points)
- Target: Next liquidity cluster or +10-20 point extension

---

## 4. SPOOFING DETECTION

**Concept:** Large orders appear then disappear before being hit = fake liquidity

**Signal:** Fade the spoof or trade reversal

### Detection Query:
```sql
SELECT
    timestamp_1sec,
    price,
    bid_adds,
    bid_cancels,
    ask_adds,
    ask_cancels,
    buy_aggressor_volume,
    sell_aggressor_volume,
    CASE
        WHEN bid_adds > 100 AND bid_cancels > bid_adds * 0.7
             AND buy_aggressor_volume < bid_adds * 0.2
        THEN 'BID_SPOOF'
        WHEN ask_adds > 100 AND ask_cancels > ask_adds * 0.7
             AND sell_aggressor_volume < ask_adds * 0.2
        THEN 'ASK_SPOOF'
        ELSE 'LEGIT'
    END as spoof_type,
    round(GREATEST(bid_cancels / NULLIF(bid_adds, 0),
                   ask_cancels / NULLIF(ask_adds, 0)), 2) as cancel_ratio
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQZ5'
  AND (bid_adds > 100 OR ask_adds > 100)
  AND (bid_cancels > bid_adds * 0.7 OR ask_cancels > ask_adds * 0.7)
ORDER BY cancel_ratio DESC;
```

**Trade Setup:**
- Entry: When spoof cancels (large adds disappear)
- Direction: OPPOSITE to spoof (if bid spoof = trade DOWN)
- Stop: Tight (-3-5 points, spoofers won't fight real flow)
- Target: Quick scalp (+5-10 points)

---

## 5. LIQUIDITY WALL REVERSAL

**Concept:** Price can't break through large resting orders = exhaustion & reversal

**Signal:** Failed breakout, fade opportunity

### Detection Query:
```sql
SELECT
    timestamp_1sec,
    price,
    bid_adds,
    ask_adds,
    buy_aggressor_volume,
    sell_aggressor_volume,
    CASE
        WHEN ask_adds > 150 AND buy_aggressor_volume > 80 THEN 'BULL_FAILED_AT_ASK_WALL'
        WHEN bid_adds > 150 AND sell_aggressor_volume > 80 THEN 'BEAR_FAILED_AT_BID_WALL'
        ELSE 'NO_WALL'
    END as wall_failure
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQZ5'
  AND (ask_adds > 150 OR bid_adds > 150)
  AND (buy_aggressor_volume > 80 OR sell_aggressor_volume > 80)
ORDER BY timestamp_1sec DESC;
```

**Trade Setup:**
- Entry: After 2-3 failed attempts at wall
- Direction: Fade the failed breakout (sell if can't break asks)
- Stop: Beyond wall (+5 points past it)
- Target: Return to mean or -10-15 points

---

## 6. VELOCITY + DELTA MOMENTUM (Integration with Velocity Spike Detector)

**Concept:** Combine order flow imbalance with price velocity = momentum continuation

**Signal:** High-probability directional move

### Detection Query:
```sql
SELECT
    o.timestamp_1sec,
    o.price,
    o.aggression_delta,
    o.total_volume,
    o.buy_aggressor_volume,
    o.sell_aggressor_volume,
    o.bid_adds + o.ask_adds as total_liquidity,
    CASE
        WHEN o.aggression_delta > 80 AND o.ask_adds < 30 THEN 'BULL_MOMENTUM'
        WHEN o.aggression_delta < -80 AND o.bid_adds < 30 THEN 'BEAR_MOMENTUM'
        ELSE 'NO_SETUP'
    END as momentum_setup
FROM mnq_orderflow_1sec o
WHERE o.symbol = 'MNQZ5'
  AND o.total_volume > 100
  AND abs(o.aggression_delta) > 80      -- Strong imbalance
  AND o.bid_adds + o.ask_adds < 50     -- Thin opposing liquidity
ORDER BY abs(o.aggression_delta) DESC;
```

**Integration with Velocity Spike Detector:**
- Use velocity spike detector for entry timing
- Use order flow table for confirmation (delta, liquidity)
- Combined signal = highest conviction trade

---

## COMBINED STRATEGY WORKFLOW

### 1. Pre-Market Setup (8:00-9:00)
```sql
-- Identify key liquidity levels
SELECT
    price,
    sum(bid_adds) as total_bid_adds,
    sum(ask_adds) as total_ask_adds,
    sum(total_volume) as total_traded
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQZ5'
  AND timestamp_1sec BETWEEN '2025-10-10 08:00:00' AND '2025-10-10 09:00:00'
GROUP BY price
HAVING total_traded > 500
ORDER BY total_traded DESC
LIMIT 20;
```

### 2. Real-Time Monitoring (9:30-16:00)
Run these queries in sequence:

**A. Absorption check** (every 10 seconds)
**B. Iceberg detection** (every 30 seconds)
**C. Breakout scanner** (every 5 seconds)
**D. Momentum filter** (continuous with velocity detector)

### 3. Post-Market Analysis (16:00+)
```sql
-- Find best setups of the day
SELECT
    'ABSORPTION' as setup_type,
    count(*) as occurrences
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQZ5'
  AND sell_aggressor_volume > 80
  AND bid_adds > sell_aggressor_volume * 1.3

UNION ALL

SELECT
    'ICEBERG',
    count(*)
FROM mnq_orderflow_1sec
WHERE symbol = 'MNQZ5'
  AND total_volume / NULLIF(bid_adds + ask_adds, 0) > 10;
```

---

## RISK MANAGEMENT

**Position Sizing:**
- Absorption: 3-5 contracts (counter-trend)
- Iceberg: 5-10 contracts (institutional backing)
- Breakout: 10-15 contracts (momentum)
- Spoofing fade: 1-3 contracts (quick scalp)

**Stop Placement:**
- Absorption: -5 points below level
- Iceberg: -3 points (tight, institution defending)
- Breakout: -5 points back inside range
- Velocity+Delta: -10 points (wider for momentum)

**Time Limits:**
- Scalps: 30-60 seconds max
- Momentum: 2-5 minutes
- Absorption: 5-15 minutes (reversal needs time)

---

## NEXT STEPS

1. **Backtest each strategy** on historical data
2. **Combine with velocity spike detector** for entries
3. **Create alerting system** for real-time pattern detection
4. **Build visual heatmap** for pattern recognition
5. **Track performance** by setup type

All patterns are now queryable from `mnq_orderflow_1sec` table in real-time!
