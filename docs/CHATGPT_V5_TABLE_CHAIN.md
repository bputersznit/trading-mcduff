# ChatGPT v5 Table Dependency Chain

Complete trace from `mnq_mbo` to `CG_mnq_hybrid_v5_clanmarshal` (908 trades)

## Table Flow Diagram

```
mnq_mbo (source: 789M MBO events)
    │
    ├─> (unknown processing: CG_mnq_mbo_events?)
    │
    ├─> CG_mnq_book_proxy_100ms
    │       └─ Aggregates to 100ms buckets
    │       └─ Calculates: bid_event_size, ask_event_size, bid_events, ask_events
    │       └─ SQL: sumIf(size, side = 'B/A'), countIf(side = 'B/A')
    │
    ├─> CG_mnq_features_100ms
    │       └─ Calculates event features per 100ms bucket
    │       └─ event_delta = bid_event_size - ask_event_size
    │       └─ total_event_size = bid_event_size + ask_event_size
    │       └─ event_imbalance = event_delta / total_event_size
    │       └─ event_count_delta = bid_events - ask_events
    │
    ├─> CG_mnq_features_100ms_clean
    │       └─ Filters: best_bid > 0, best_ask > 0, spread <= 2.0
    │
    ├─> CG_mnq_signals_100ms
    │       └─ Signal logic: WHEN event_delta > 50 AND event_imbalance > 0.60 THEN 'LONG'
    │       └─                WHEN event_delta < -50 AND event_imbalance < -0.60 THEN 'SHORT'
    │
    ├─> CG_mnq_signal_events_100ms
    │       └─ Deduplicates: WHERE signal != prev_signal
    │
    ├─> CG_mnq_signal_events_100ms_clean
    │       └─ (additional cleaning - unknown filters)
    │
    ├─> CG_mnq_entries_100ms
    │       └─ Adds entry prices:
    │       └─   LONG: entry_price = best_ask
    │       └─   SHORT: entry_price = best_bid
    │
    ├─> CG_mnq_trade_candidates_100ms
    │       └─ Adds targets/stops:
    │       └─   target_price = entry_price ± (40 * 0.25)
    │       └─   stop_price = entry_price ∓ (20 * 0.25)
    │
    ├─> CG_mnq_trade_results_100ms
    │       └─ Exit simulation: joins with features_100ms_clean
    │       └─ Finds first 100ms bucket where:
    │       └─   LONG: best_bid >= target OR best_bid <= stop
    │       └─   SHORT: best_ask <= target OR best_ask >= stop
    │       └─ Returns: target_hit_time, stop_hit_time
    │
    ├─> CG_mnq_trades_100ms
    │       └─ Determines outcome:
    │       └─   IF target_hit_time < stop_hit_time THEN 'TARGET' ELSE 'STOP'
    │
    ├─> CG_mnq_trade_results_queue_q10_w5
    │       └─ Limit order fill simulation (queue position model)
    │       └─ Parameters: q=10 (queue depth?), w=5 (window?)
    │       └─ Returns: fill_time (when limit order would fill)
    │
    ├─> CG_mnq_hybrid_model_rth
    │       └─ Joins: trades_100ms + features_100ms_clean + queue results
    │       └─ Adds execution_type: IF fill_time IS NOT NULL THEN 'LIMIT' ELSE 'MARKET'
    │       └─ Adds effective_fill_time: fill_time OR entry_time + 1 second
    │       └─ Calculates slippage_ticks_rt:
    │       └─   LIMIT: 2 ticks
    │       └─   MARKET (total_event_size > 400): 8 ticks
    │       └─   MARKET (total_event_size > 200): 6 ticks
    │       └─   MARKET (total_event_size > 100): 4 ticks
    │       └─   MARKET (else): 3 ticks
    │       └─ Calculates net_pnl_usd:
    │       └─   TARGET: (40 * 5) - (slippage_ticks * 5) - 0.70
    │       └─   STOP: -(20 * 5) - (slippage_ticks * 5) - 0.70
    │       └─ Filters: RTH only (9:30-16:00 ET)
    │
    ├─> CG_mnq_hybrid_model_rth_resolved
    │       └─ Joins with trades_100ms to get exit times
    │       └─ Adds: target_hit_time, stop_hit_time, exit_time
    │
    ├─> CG_mnq_hybrid_model_rth_single_position
    │       └─ Enforces single position using arrayFold
    │       └─ Only allows entry if previous trade has exited
    │       └─ (This is the key to preventing overlaps!)
    │
    ├─> CG_mnq_hybrid_v4_institutional_manipaware
    │       └─ Adds Opening Range (9:30-9:45 AM) labels:
    │       └─   or_high, or_low, or_location (ABOVE_OR/BELOW_OR/INSIDE_OR)
    │       └─ Adds time_zone labels:
    │       └─   OPEN_15 (9:00-9:45), POST_OPEN (9:45-10:30), NORMAL, CLOSE_30 (15:30-16:00)
    │       └─ Applies manipulation awareness filters:
    │       └─   Removes: OPEN_15 + SHORT
    │       └─   Removes: POST_OPEN + ABOVE_OR + SHORT + LIMIT
    │       └─   Removes: POST_OPEN + INSIDE_OR + SHORT
    │       └─   Removes: NORMAL + INSIDE_OR + LONG + MARKET
    │       └─   Removes: CLOSE_30 + ABOVE_OR + SHORT
    │       └─   Removes: CLOSE_30 + BELOW_OR + LONG + MARKET
    │       └─ Adds loss governance:
    │       └─   running_daily_pnl, consecutive_losses
    │       └─   Filters: running_daily_pnl > -60 AND consecutive_losses < 4
    │
    └─> CG_mnq_hybrid_v5_clanmarshal (FINAL: 908 trades)
            └─ Adds profit lock filter:
            └─   Removes trades where:
            └─     v5_running_daily_peak >= 3000
            └─     AND v5_drawdown_from_peak <= -500
            └─ Result: 908 trades, $71,429.40, 64.43% WR
```

## Key SQL Snippets

### 1. Book Proxy (100ms aggregation)
```sql
CREATE TABLE CG_mnq_book_proxy_100ms AS
SELECT
    toStartOfInterval(ts_event, toIntervalMillisecond(100)) AS ts_bucket,
    maxIf(price, side = 'B') AS best_bid,
    minIf(price, side = 'A') AS best_ask,
    sumIf(size, side = 'B') AS bid_event_size,   -- KEY: SUM of volumes
    sumIf(size, side = 'A') AS ask_event_size,
    countIf(side = 'B') AS bid_events,           -- COUNT of events
    countIf(side = 'A') AS ask_events
FROM CG_mnq_mbo_events
GROUP BY ts_bucket;
```

### 2. Features Calculation
```sql
CREATE TABLE CG_mnq_features_100ms AS
SELECT
    ts_bucket,
    best_bid,
    best_ask,
    best_ask - best_bid AS spread,
    bid_event_size,
    ask_event_size,
    bid_event_size - ask_event_size AS event_delta,        -- Volume delta
    bid_event_size + ask_event_size AS total_event_size,
    (bid_event_size - ask_event_size) / nullIf(bid_event_size + ask_event_size, 0) AS event_imbalance,
    bid_events,
    ask_events,
    bid_events - ask_events AS event_count_delta            -- Count delta
FROM CG_mnq_book_proxy_100ms;
```

### 3. Signal Generation
```sql
CREATE TABLE CG_mnq_signals_100ms AS
SELECT
    *,
    CASE
        WHEN event_delta > 50 AND event_imbalance > 0.60 THEN 'LONG'
        WHEN event_delta < -50 AND event_imbalance < -0.60 THEN 'SHORT'
        ELSE 'NONE'
    END AS signal
FROM CG_mnq_features_100ms;
```

### 4. Single Position Enforcement (ArrayFold)
```sql
CREATE TABLE CG_mnq_hybrid_model_rth_single_position AS
WITH
ordered AS (
    SELECT groupArray(x) AS xs
    FROM (
        SELECT tuple(entry_time, side, entry_price, ..., exit_time, net_pnl_usd) AS x
        FROM CG_mnq_hybrid_model_rth_resolved
        WHERE exit_time IS NOT NULL
        ORDER BY entry_time
    )
),
folded AS (
    SELECT
        tupleElement(
            arrayFold(
                (acc, x) ->
                    if(
                        tupleElement(acc, 2) IS NULL              -- First trade
                        OR tupleElement(x, 1) > tupleElement(acc, 2),  -- New entry > prev exit
                        tuple(
                            arrayConcat(tupleElement(acc, 1), [x]),
                            tupleElement(x, 15)  -- exit_time of current trade
                        ),
                        acc
                    ),
                xs,
                tuple(arraySlice(xs, 1, 0), CAST(NULL, 'Nullable(DateTime64)'))
            ),
            1
        ) AS picked
    FROM ordered
)
SELECT tupleElement(x, 1) AS entry_time, ...
FROM folded
ARRAY JOIN picked AS x;
```

### 5. Manipulation Awareness Filters (v4)
```sql
CREATE TABLE CG_mnq_hybrid_v4_institutional_manipaware AS
...
WHERE NOT (
    (time_zone = 'OPEN_15' AND side = 'SHORT')
    OR (time_zone = 'POST_OPEN' AND or_location = 'ABOVE_OR' AND side = 'SHORT' AND execution_type = 'LIMIT')
    OR (time_zone = 'POST_OPEN' AND or_location = 'INSIDE_OR' AND side = 'SHORT')
    OR (time_zone = 'NORMAL' AND or_location = 'INSIDE_OR' AND side = 'LONG' AND execution_type = 'MARKET')
    OR (time_zone = 'CLOSE_30' AND or_location = 'ABOVE_OR' AND side = 'SHORT')
    OR (time_zone = 'CLOSE_30' AND or_location = 'BELOW_OR' AND side = 'LONG' AND execution_type = 'MARKET')
)
AND running_daily_pnl > -60
AND consecutive_losses < 4;
```

### 6. Profit Lock Filter (v5)
```sql
CREATE TABLE CG_mnq_hybrid_v5_clanmarshal AS
...
WHERE NOT (
    v5_running_daily_peak >= 3000
    AND v5_drawdown_from_peak <= -500
);
```

## What My v6.2 Replicated

✅ **Steps 1-9**: MBO → 100ms aggregation → features → signals → entries
✅ **Step 10**: Trade candidates with targets/stops
✅ **Step 11**: Exit simulation (accurate tick-by-tick)
✅ **Step 13**: Single position enforcement (different method but same result)
✅ **Step 14 (partial)**: Opening Range labels
✅ **Slippage & Commission**: ChatGPT's exact model

## What My v6.2 MISSED

❌ **Step 12**: `CG_mnq_trade_results_queue_q10_w5` - Limit order fill simulation
❌ **Step 14 (filters)**: Manipulation awareness filters (6 filter rules)
❌ **Step 14 (governance)**: Loss governance (daily PnL > -60, consecutive losses < 4)
❌ **Step 15**: Profit lock filter (removes trades after $3K peak with $500 DD)
❌ **Unknown**: What is `CG_mnq_mbo_events`? (Used as source for book_proxy)

## Trade Count Impact

| Stage | Trades | Notes |
|-------|--------|-------|
| **After signals** | ~5,000+ | Raw 100ms signals |
| **After dedup** | ~2,000+ | Signal changes only |
| **After exits** | ~1,500+ | With valid target/stop hits |
| **After single position** | ~1,200+ | No overlaps |
| **After manipulation filters** | ~950+ | v4 filters applied |
| **After loss governance** | ~920+ | Daily loss limit, streak limit |
| **After profit lock** | **908** | v5 final (or 785 with 10s gap) |

My v6.2: **202 trades** because I'm missing the queue fill simulation and manipulation filters that likely ADD trades back in.

## Next Steps to Match v5

1. Find or recreate `CG_mnq_mbo_events` table
2. Implement limit order fill simulation (queue position model)
3. Add all 6 manipulation awareness filters from v4
4. Add loss governance rules (daily PnL limit, consecutive loss limit)
5. Add profit lock filter from v5

The core signal logic is correct, but the additional filters and queue simulation are critical to reaching 785-908 trades.
