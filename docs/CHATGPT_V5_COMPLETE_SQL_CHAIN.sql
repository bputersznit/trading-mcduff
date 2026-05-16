-- ============================================================================
-- ChatGPT v5 Complete SQL Chain
-- ============================================================================
-- Source: Extracted from system.query_log
-- Created by: ChatGPT between April 25-28, 2026
-- Purpose: Complete recreation pipeline from mnq_mbo → v5 (908 trades)
--
-- Flow:
--   789M MBO events → 12.6M 100ms buckets → 7,621 signals → 908 final trades
--
-- Runtime: ~750 seconds total (12.5 minutes)
-- Result: $71,429.40 profit, 64.43% win rate, 908 trades
-- ============================================================================

-- ============================================================================
-- STEP 1: Filter MBO Data to MNQZ5 Contract
-- ============================================================================
-- Input:  mnq_mbo (all contracts, all symbols)
-- Output: CG_mnq_mbo_events (789M rows - MNQZ5 only)
-- Runtime: 383 seconds
-- Purpose: Isolate September 2025 MNQ contract for backtesting
-- ============================================================================

CREATE TABLE CG_mnq_mbo_events
ENGINE = MergeTree
ORDER BY (ts_event, sequence)
AS
SELECT
    ts_event,
    sequence,
    action,
    side,
    price,
    size,
    order_id
FROM mnq_mbo
WHERE symbol = 'MNQZ5';


-- ============================================================================
-- STEP 2: Aggregate to 100ms Buckets
-- ============================================================================
-- Input:  CG_mnq_mbo_events (789M MBO events)
-- Output: CG_mnq_book_proxy_100ms (12.6M 100ms buckets)
-- Runtime: 22 seconds
-- Purpose: Group tick-level data into 100ms intervals for signal generation
--
-- Key Calculations:
--   - bid_event_size: SUM of all bid-side event sizes (VOLUME)
--   - ask_event_size: SUM of all ask-side event sizes (VOLUME)
--   - bid_events: COUNT of bid-side events
--   - ask_events: COUNT of ask-side events
--   - best_bid: Highest bid price in bucket
--   - best_ask: Lowest ask price in bucket
-- ============================================================================

CREATE TABLE CG_mnq_book_proxy_100ms
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT
    toStartOfInterval(ts_event, toIntervalMillisecond(100)) AS ts_bucket,
    maxIf(price, side = 'B') AS best_bid,
    minIf(price, side = 'A') AS best_ask,
    sumIf(size, side = 'B') AS bid_event_size,
    sumIf(size, side = 'A') AS ask_event_size,
    countIf(side = 'B') AS bid_events,
    countIf(side = 'A') AS ask_events
FROM CG_mnq_mbo_events
GROUP BY ts_bucket;


-- ============================================================================
-- STEP 3: Calculate Event Features
-- ============================================================================
-- Input:  CG_mnq_book_proxy_100ms (12.6M buckets)
-- Output: CG_mnq_features_100ms (12.6M buckets with features)
-- Runtime: 5 seconds
-- Purpose: Calculate per-bucket event metrics for signal generation
--
-- Key Features:
--   - event_delta: bid_event_size - ask_event_size (volume imbalance)
--   - total_event_size: bid_event_size + ask_event_size (total activity)
--   - event_imbalance: event_delta / total_event_size (normalized -1 to +1)
--   - event_count_delta: bid_events - ask_events (count imbalance)
--   - spread: best_ask - best_bid (for filtering)
-- ============================================================================

CREATE TABLE CG_mnq_features_100ms
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT
    ts_bucket,
    best_bid,
    best_ask,
    best_ask - best_bid AS spread,
    bid_event_size,
    ask_event_size,
    bid_event_size - ask_event_size AS event_delta,
    bid_event_size + ask_event_size AS total_event_size,
    (bid_event_size - ask_event_size) / nullIf(bid_event_size + ask_event_size, 0) AS event_imbalance,
    bid_events,
    ask_events,
    bid_events - ask_events AS event_count_delta
FROM CG_mnq_book_proxy_100ms;


-- ============================================================================
-- STEP 4: Clean Features (Filter Valid Spreads)
-- ============================================================================
-- Input:  CG_mnq_features_100ms (12.6M buckets)
-- Output: CG_mnq_features_100ms_clean (6.07M buckets - 48% kept)
-- Runtime: 6 seconds
-- Purpose: Remove invalid/wide spread buckets
--
-- Filters:
--   - best_bid > 0 (valid bid)
--   - best_ask > 0 (valid ask)
--   - best_ask >= best_bid (valid book)
--   - spread <= 2.0 (tight spread only - 2 points = 8 ticks)
-- ============================================================================

CREATE TABLE CG_mnq_features_100ms_clean
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT *
FROM CG_mnq_features_100ms
WHERE
    best_bid > 0
    AND best_ask > 0
    AND best_ask >= best_bid
    AND (best_ask - best_bid) <= 2.0;


-- ============================================================================
-- STEP 5: Generate Signals (ChatGPT's Exact Thresholds)
-- ============================================================================
-- Input:  CG_mnq_features_100ms (12.6M buckets)
-- Output: CG_mnq_signals_100ms (12.6M buckets with signal labels)
-- Runtime: 12 seconds
-- Purpose: Label each bucket with LONG/SHORT/NONE signal
--
-- Signal Logic:
--   LONG:  event_delta > 50 AND event_imbalance > 0.60
--          (Strong bid pressure: 50+ contract volume edge, 60%+ bid-biased)
--
--   SHORT: event_delta < -50 AND event_imbalance < -0.60
--          (Strong ask pressure: 50+ contract volume edge, 60%+ ask-biased)
--
--   NONE:  Everything else
--
-- Result: Most buckets are NONE, only extreme imbalances trigger signals
-- ============================================================================

CREATE TABLE CG_mnq_signals_100ms
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT
    *,
    CASE
        WHEN event_delta > 50 AND event_imbalance > 0.60 THEN 'LONG'
        WHEN event_delta < -50 AND event_imbalance < -0.60 THEN 'SHORT'
        ELSE 'NONE'
    END AS signal
FROM CG_mnq_features_100ms;


-- ============================================================================
-- STEP 6: Deduplicate Signals (Signal Changes Only)
-- ============================================================================
-- Input:  CG_mnq_signals_100ms (12.6M buckets)
-- Output: CG_mnq_signal_events_100ms (7,621 signal changes)
-- Runtime: 4 seconds
-- Purpose: Only keep buckets where signal changes from previous bucket
--
-- Logic:
--   - Use lag() to get previous signal
--   - Keep if signal != NONE AND signal != prev_signal
--   - This deduplicates consecutive same-signals
--
-- Result: 12.6M buckets → 7,621 signal events (0.06% kept)
-- ============================================================================

CREATE TABLE CG_mnq_signal_events_100ms
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT *
FROM
(
    SELECT
        *,
        lagInFrame(signal, 1, 'NONE') OVER (ORDER BY ts_bucket) AS prev_signal
    FROM CG_mnq_signals_100ms
)
WHERE signal != 'NONE'
  AND signal != prev_signal;


-- ============================================================================
-- STEP 7: Add Entry Prices
-- ============================================================================
-- Input:  CG_mnq_signal_events_100ms_clean (cleaned signal events)
-- Output: CG_mnq_entries_100ms (entry times + prices)
-- Runtime: 0.1 seconds
-- Purpose: Assign realistic entry prices based on market side
--
-- Entry Price Logic:
--   LONG:  entry_price = best_ask (pay the ask to go long)
--   SHORT: entry_price = best_bid (hit the bid to go short)
--
-- Note: This assumes we cross the spread, realistic for market orders
-- ============================================================================

CREATE TABLE CG_mnq_entries_100ms
ENGINE = MergeTree
ORDER BY entry_time
AS
SELECT
    ts_bucket AS entry_time,
    signal AS side,
    best_bid,
    best_ask,
    multiIf(
        signal = 'LONG', best_ask,
        signal = 'SHORT', best_bid,
        CAST(NULL, 'Nullable(Float64)')
    ) AS entry_price
FROM CG_mnq_signal_events_100ms_clean;


-- ============================================================================
-- STEP 8: Add Targets and Stops
-- ============================================================================
-- Input:  CG_mnq_entries_100ms
-- Output: CG_mnq_trade_candidates_100ms
-- Runtime: 0.1 seconds
-- Purpose: Define target and stop prices for each trade
--
-- Target: 40 ticks = 40 * 0.25 = 10 points
-- Stop:   20 ticks = 20 * 0.25 = 5 points
--
-- Risk/Reward: 1:2 (risk 5 points to make 10 points)
--
-- LONG:
--   target_price = entry_price + 10.0
--   stop_price   = entry_price - 5.0
--
-- SHORT:
--   target_price = entry_price - 10.0
--   stop_price   = entry_price + 5.0
-- ============================================================================

CREATE TABLE CG_mnq_trade_candidates_100ms
ENGINE = MergeTree
ORDER BY entry_time
AS
SELECT
    entry_time,
    side,
    entry_price,

    multiIf(
        side = 'LONG',  entry_price + 40 * 0.25,
        side = 'SHORT', entry_price - 40 * 0.25,
        CAST(NULL, 'Nullable(Float64)')
    ) AS target_price,

    multiIf(
        side = 'LONG',  entry_price - 20 * 0.25,
        side = 'SHORT', entry_price + 20 * 0.25,
        CAST(NULL, 'Nullable(Float64)')
    ) AS stop_price
FROM CG_mnq_entries_100ms
WHERE entry_price IS NOT NULL;


-- ============================================================================
-- STEP 9: Exit Simulation (Tick-by-Tick at 100ms Granularity)
-- ============================================================================
-- Input:  CG_mnq_trade_candidates_100ms + CG_mnq_features_100ms_clean
-- Output: CG_mnq_trade_results_100ms (with exit times)
-- Runtime: 351 seconds (5.8 minutes) - LONGEST STEP
-- Purpose: Find exact 100ms bucket where target or stop was hit
--
-- Logic:
--   - For each trade candidate, scan forward through 100ms buckets
--   - Stop searching after 10 minutes (600 seconds)
--   - For LONG: check if best_bid >= target OR best_bid <= stop
--   - For SHORT: check if best_ask <= target OR best_ask >= stop
--   - Record FIRST bucket where either condition is met
--
-- Returns:
--   - target_hit_time: First bucket where target was reached
--   - stop_hit_time: First bucket where stop was reached
--   - (Later step determines which came first)
--
-- This is the most expensive operation (351s) due to the join with
-- 6.07M clean features for each candidate trade.
-- ============================================================================

CREATE TABLE CG_mnq_trade_results_100ms
ENGINE = MergeTree
ORDER BY entry_time
AS
SELECT
    t.entry_time,
    t.side,
    t.entry_price,
    t.target_price,
    t.stop_price,

    minIf(
        f.ts_bucket,
        (t.side = 'LONG'  AND f.best_bid >= t.target_price)
        OR
        (t.side = 'SHORT' AND f.best_ask <= t.target_price)
    ) AS target_hit_time,

    minIf(
        f.ts_bucket,
        (t.side = 'LONG'  AND f.best_bid <= t.stop_price)
        OR
        (t.side = 'SHORT' AND f.best_ask >= t.stop_price)
    ) AS stop_hit_time

FROM CG_mnq_trade_candidates_100ms AS t
INNER JOIN CG_mnq_features_100ms_clean AS f
    ON f.ts_bucket > t.entry_time
   AND f.ts_bucket <= t.entry_time + INTERVAL 10 MINUTE

GROUP BY
    t.entry_time,
    t.side,
    t.entry_price,
    t.target_price,
    t.stop_price;


-- ============================================================================
-- STEP 10: Determine Outcomes
-- ============================================================================
-- Input:  CG_mnq_trade_results_100ms
-- Output: CG_mnq_trades_100ms (4,104 trades with outcomes)
-- Runtime: 0.1 seconds
-- Purpose: Decide if trade hit target or stop based on timing
--
-- Outcome Logic:
--   TIMEOUT: Neither target nor stop hit within 10 minutes
--   TARGET:  Target hit and (no stop OR target hit first)
--   STOP:    Stop hit and (no target OR stop hit first)
--
-- Conservative: If both hit in same bucket, assume STOP
-- ============================================================================

CREATE TABLE CG_mnq_trades_100ms
ENGINE = MergeTree
ORDER BY entry_time
AS
SELECT
    *,
    multiIf(
        target_hit_time IS NULL AND stop_hit_time IS NULL, 'TIMEOUT',
        stop_hit_time IS NULL, 'TARGET',
        target_hit_time IS NULL, 'STOP',
        target_hit_time < stop_hit_time, 'TARGET',
        'STOP'
    ) AS outcome
FROM CG_mnq_trade_results_100ms;


-- ============================================================================
-- STEP 11: Add Slippage, P&L, and RTH Filter
-- ============================================================================
-- Input:  CG_mnq_trades_100ms + CG_mnq_features_100ms_clean + queue results
-- Output: CG_mnq_hybrid_model_rth
-- Runtime: 0.3 seconds
-- Purpose: Add execution details, slippage model, P&L calculation
--
-- Joins:
--   - trades_100ms: Base trade data
--   - features: For total_event_size (slippage model input)
--   - queue_q10_w5: Limit order fill simulation (if filled → 'LIMIT')
--
-- Execution Type:
--   LIMIT:  If queue simulation shows limit order filled
--   MARKET: Otherwise (instant fill, more slippage)
--
-- Slippage Model (based on total_event_size):
--   LIMIT fills:                  2 ticks
--   MARKET (size > 400):          8 ticks (low liquidity)
--   MARKET (size > 200):          6 ticks
--   MARKET (size > 100):          4 ticks
--   MARKET (else):                3 ticks (high liquidity)
--
-- P&L Calculation (MNQ: $5 per tick):
--   TARGET: (40 ticks * $5) - (slippage_ticks * $5) - $0.70 commission
--         = $200 - slippage - $0.70
--   STOP:   -(20 ticks * $5) - (slippage_ticks * $5) - $0.70 commission
--         = -$100 - slippage - $0.70
--
-- RTH Filter: Only 9:30 AM - 4:00 PM ET
-- ============================================================================

CREATE TABLE CG_mnq_hybrid_model_rth
ENGINE = MergeTree
ORDER BY entry_time
AS
SELECT
    h_entry_time AS entry_time,
    side,
    entry_price,
    target_price,
    stop_price,
    limit_fill_time,
    execution_type,
    effective_fill_time,
    total_event_size,
    event_count_delta,
    slippage_ticks_rt,
    outcome,
    net_pnl_usd
FROM
(
    SELECT
        m.entry_time AS h_entry_time,
        m.side AS side,
        m.entry_price AS entry_price,
        m.target_price AS target_price,
        m.stop_price AS stop_price,
        q.fill_time AS limit_fill_time,

        if(q.fill_time IS NOT NULL, 'LIMIT', 'MARKET') AS execution_type,

        if(
            q.fill_time IS NOT NULL,
            q.fill_time,
            m.entry_time + INTERVAL 1 SECOND
        ) AS effective_fill_time,

        f.total_event_size AS total_event_size,
        f.event_count_delta AS event_count_delta,

        multiIf(
            q.fill_time IS NOT NULL, 2,
            f.total_event_size > 400, 8,
            f.total_event_size > 200, 6,
            f.total_event_size > 100, 4,
            3
        ) AS slippage_ticks_rt,

        m.outcome AS outcome,

        multiIf(
            m.outcome = 'TARGET', 40 * 5,
            m.outcome = 'STOP',  -20 * 5,
            0
        )
        - (
            multiIf(
                q.fill_time IS NOT NULL, 2,
                f.total_event_size > 400, 8,
                f.total_event_size > 200, 6,
                f.total_event_size > 100, 4,
                3
            ) * 5
        )
        - 0.70 AS net_pnl_usd

    FROM CG_mnq_trades_100ms AS m
    LEFT JOIN CG_mnq_trade_results_queue_q10_w5 AS q
        ON q.entry_time = m.entry_time
    INNER JOIN CG_mnq_features_100ms_clean AS f
        ON f.ts_bucket = m.entry_time
    WHERE
        toTimeZone(m.entry_time, 'America/New_York') >= toDateTime64(concat(toString(toDate(m.entry_time, 'America/New_York')), ' 09:30:00'), 3, 'America/New_York')
        AND toTimeZone(m.entry_time, 'America/New_York') < toDateTime64(concat(toString(toDate(m.entry_time, 'America/New_York')), ' 16:00:00'), 3, 'America/New_York')
);


-- ============================================================================
-- STEP 12: Add Exit Times
-- ============================================================================
-- Input:  CG_mnq_hybrid_model_rth + CG_mnq_trades_100ms
-- Output: CG_mnq_hybrid_model_rth_resolved
-- Runtime: 0 seconds
-- Purpose: Add target_hit_time, stop_hit_time, and calculate exit_time
--
-- Exit Time Logic:
--   TARGET: exit_time = target_hit_time
--   STOP:   exit_time = stop_hit_time
--   TIMEOUT: exit_time = effective_fill_time + 10 minutes
--
-- This allows subsequent steps to check for position overlaps
-- ============================================================================

CREATE TABLE CG_mnq_hybrid_model_rth_resolved
ENGINE = MergeTree
ORDER BY entry_time
AS
SELECT
    h.*,
    m.target_hit_time,
    m.stop_hit_time,
    multiIf(
        h.outcome = 'TARGET', m.target_hit_time,
        h.outcome = 'STOP',   m.stop_hit_time,
        h.effective_fill_time + INTERVAL 10 MINUTE
    ) AS exit_time
FROM CG_mnq_hybrid_model_rth h
INNER JOIN CG_mnq_trades_100ms m
    ON m.entry_time = h.entry_time;


-- ============================================================================
-- STEP 13: Enforce Single Position (ArrayFold - ChatGPT's Approach)
-- ============================================================================
-- Input:  CG_mnq_hybrid_model_rth_resolved
-- Output: CG_mnq_hybrid_model_rth_single_position
-- Runtime: 0.4 seconds
-- Purpose: Remove overlapping trades to ensure only 1 position at a time
--
-- Strategy: ArrayFold
--   1. Convert all trades to array of tuples (ordered by entry_time)
--   2. Fold over array with accumulator: (kept_trades[], last_exit_time)
--   3. For each trade:
--      - If entry_time > last_exit_time: KEEP (add to array, update exit_time)
--      - Else: SKIP (overlaps with previous position)
--   4. Unnest the kept_trades array back to rows
--
-- This is ChatGPT's elegant functional approach to position filtering
-- Alternative: Window function with WHERE entry_time >= MAX(prev_exit_times)
--
-- Result: Removes any trades that would have entered before previous exit
-- ============================================================================

CREATE TABLE CG_mnq_hybrid_model_rth_single_position
ENGINE = MergeTree
ORDER BY entry_time
AS
WITH
ordered AS (
    SELECT groupArray(x) AS xs
    FROM
    (
        SELECT
            tuple(
                entry_time,
                side,
                entry_price,
                target_price,
                stop_price,
                limit_fill_time,
                execution_type,
                effective_fill_time,
                total_event_size,
                event_count_delta,
                slippage_ticks_rt,
                outcome,
                target_hit_time,
                stop_hit_time,
                exit_time,
                net_pnl_usd
            ) AS x
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
                        tupleElement(acc, 2) IS NULL
                        OR tupleElement(x, 1) > tupleElement(acc, 2),
                        tuple(
                            arrayConcat(tupleElement(acc, 1), [x]),
                            tupleElement(x, 15)
                        ),
                        acc
                    ),
                xs,
                tuple(
                    arraySlice(xs, 1, 0),
                    CAST(NULL, 'Nullable(DateTime64(3, ''UTC''))')
                )
            ),
            1
        ) AS picked
    FROM ordered
)
SELECT
    tupleElement(x, 1) AS entry_time,
    tupleElement(x, 2) AS side,
    tupleElement(x, 3) AS entry_price,
    tupleElement(x, 4) AS target_price,
    tupleElement(x, 5) AS stop_price,
    tupleElement(x, 6) AS limit_fill_time,
    tupleElement(x, 7) AS execution_type,
    tupleElement(x, 8) AS effective_fill_time,
    tupleElement(x, 9) AS total_event_size,
    tupleElement(x, 10) AS event_count_delta,
    tupleElement(x, 11) AS slippage_ticks_rt,
    tupleElement(x, 12) AS outcome,
    tupleElement(x, 13) AS target_hit_time,
    tupleElement(x, 14) AS stop_hit_time,
    tupleElement(x, 15) AS exit_time,
    tupleElement(x, 16) AS net_pnl_usd
FROM folded
ARRAY JOIN picked AS x;


-- ============================================================================
-- STEP 14: Add Opening Range + Manipulation Filters + Loss Governance
-- ============================================================================
-- Input:  CG_mnq_hybrid_model_rth_single_position
-- Output: CG_mnq_hybrid_v4_institutional_manipaware
-- Runtime: 0.1 seconds
-- Purpose: Add market context and filter out manipulation-prone setups
--
-- Opening Range (OR):
--   - Calculated from 9:30-9:45 AM ET (first 15 minutes)
--   - or_high: MAX(entry_price) during OR period
--   - or_low:  MIN(entry_price) during OR period
--   - or_location: ABOVE_OR / BELOW_OR / INSIDE_OR (where price is vs OR)
--
-- Time Zones:
--   - OPEN_15:   9:00-9:45 (includes pre-market + OR)
--   - POST_OPEN: 9:45-10:30 (post-OR, still volatile)
--   - NORMAL:    10:30-15:00 (mid-day)
--   - CLOSE_30:  15:30-16:00 (last 30 min)
--   - CLOSED:    Outside RTH
--
-- Manipulation Filters (removes 6 specific setups):
--   1. OPEN_15 + SHORT
--      (No shorts during opening - tends to squeeze up)
--
--   2. POST_OPEN + ABOVE_OR + SHORT + LIMIT
--      (After breaking above OR, limit shorts often don't fill or get run over)
--
--   3. POST_OPEN + INSIDE_OR + SHORT
--      (Inside OR post-open, shorts are against trend)
--
--   4. NORMAL + INSIDE_OR + LONG + MARKET
--      (Mid-day chop inside OR, market longs get stopped out)
--
--   5. CLOSE_30 + ABOVE_OR + SHORT
--      (End of day above OR, shorts fight closing auction)
--
--   6. CLOSE_30 + BELOW_OR + LONG + MARKET
--      (End of day below OR, market longs are weak)
--
-- Loss Governance:
--   - running_daily_pnl > -60: Stop trading if down $60 on the day
--   - consecutive_losses < 4: Stop after 3 losses in a row
--
-- Result: Filters out trades in manipulation-prone contexts
-- ============================================================================

CREATE TABLE CG_mnq_hybrid_v4_institutional_manipaware
ENGINE = MergeTree
ORDER BY (trade_date, entry_time)
AS
WITH base AS
(
    SELECT
        *,
        toDate(toTimeZone(entry_time, 'America/New_York')) AS trade_date,
        toTimeZone(entry_time, 'America/New_York') AS entry_et
    FROM CG_mnq_hybrid_model_rth_single_position
    WHERE exit_time IS NOT NULL
      AND entry_price IS NOT NULL
),

opening_range AS
(
    SELECT
        toDate(toTimeZone(entry_time, 'America/New_York')) AS trade_date,
        max(entry_price) AS or_high,
        min(entry_price) AS or_low
    FROM CG_mnq_hybrid_model_rth_single_position
    WHERE entry_price IS NOT NULL
      AND toHour(toTimeZone(entry_time, 'America/New_York')) = 9
      AND toMinute(toTimeZone(entry_time, 'America/New_York')) >= 30
      AND toMinute(toTimeZone(entry_time, 'America/New_York')) < 45
    GROUP BY trade_date
),

labeled AS
(
    SELECT
        b.entry_time AS entry_time,
        b.side AS side,
        b.entry_price AS entry_price,
        b.target_price AS target_price,
        b.stop_price AS stop_price,
        b.limit_fill_time AS limit_fill_time,
        b.execution_type AS execution_type,
        b.effective_fill_time AS effective_fill_time,
        b.total_event_size AS total_event_size,
        b.event_count_delta AS event_count_delta,
        b.slippage_ticks_rt AS slippage_ticks_rt,
        b.outcome AS outcome,
        b.target_hit_time AS target_hit_time,
        b.stop_hit_time AS stop_hit_time,
        b.exit_time AS exit_time,
        b.net_pnl_usd AS net_pnl_usd,
        b.trade_date AS trade_date,
        b.entry_et AS entry_et,
        o.or_high AS or_high,
        o.or_low AS or_low,

        multiIf(
            b.entry_price > o.or_high, 'ABOVE_OR',
            b.entry_price < o.or_low,  'BELOW_OR',
            'INSIDE_OR'
        ) AS or_location,

        multiIf(
            toHour(b.entry_et) = 9 AND toMinute(b.entry_et) < 45, 'OPEN_15',
            (
                toHour(b.entry_et) = 9 AND toMinute(b.entry_et) >= 45
            ) OR (
                toHour(b.entry_et) = 10 AND toMinute(b.entry_et) < 30
            ), 'POST_OPEN',
            toHour(b.entry_et) = 15 AND toMinute(b.entry_et) >= 30, 'CLOSE_30',
            'NORMAL'
        ) AS time_zone
    FROM base AS b
    INNER JOIN opening_range AS o
        ON b.trade_date = o.trade_date
),

manipulation_gated AS
(
    SELECT *
    FROM labeled
    WHERE NOT
    (
        (time_zone = 'OPEN_15' AND side = 'SHORT')
        OR (time_zone = 'POST_OPEN' AND or_location = 'ABOVE_OR' AND side = 'SHORT' AND execution_type = 'LIMIT')
        OR (time_zone = 'POST_OPEN' AND or_location = 'INSIDE_OR' AND side = 'SHORT')
        OR (time_zone = 'NORMAL' AND or_location = 'INSIDE_OR' AND side = 'LONG' AND execution_type = 'MARKET')
        OR (time_zone = 'CLOSE_30' AND or_location = 'ABOVE_OR' AND side = 'SHORT')
        OR (time_zone = 'CLOSE_30' AND or_location = 'BELOW_OR' AND side = 'LONG' AND execution_type = 'MARKET')
    )
),

ordered AS
(
    SELECT
        *,
        sum(net_pnl_usd) OVER (
            PARTITION BY trade_date
            ORDER BY entry_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS running_daily_pnl,
        if(net_pnl_usd < 0, 1, 0) AS loss_flag
    FROM manipulation_gated
),

loss_groups AS
(
    SELECT
        *,
        sum(if(loss_flag = 0, 1, 0)) OVER (
            PARTITION BY trade_date
            ORDER BY entry_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS reset_group
    FROM ordered
),

loss_streaks AS
(
    SELECT
        *,
        sum(loss_flag) OVER (
            PARTITION BY trade_date, reset_group
            ORDER BY entry_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS consecutive_losses
    FROM loss_groups
)

SELECT
    entry_time AS entry_time,
    trade_date AS trade_date,
    entry_et AS entry_et,
    side AS side,
    entry_price AS entry_price,
    target_price AS target_price,
    stop_price AS stop_price,
    limit_fill_time AS limit_fill_time,
    execution_type AS execution_type,
    effective_fill_time AS effective_fill_time,
    total_event_size AS total_event_size,
    event_count_delta AS event_count_delta,
    slippage_ticks_rt AS slippage_ticks_rt,
    outcome AS outcome,
    target_hit_time AS target_hit_time,
    stop_hit_time AS stop_hit_time,
    exit_time AS exit_time,
    net_pnl_usd AS net_pnl_usd,
    or_high AS or_high,
    or_low AS or_low,
    or_location AS or_location,
    time_zone AS time_zone,
    running_daily_pnl AS running_daily_pnl,
    consecutive_losses AS consecutive_losses
FROM loss_streaks
WHERE running_daily_pnl > -60
  AND consecutive_losses < 4;


-- ============================================================================
-- STEP 15: Apply Profit Lock Filter (FINAL: 908 trades)
-- ============================================================================
-- Input:  CG_mnq_hybrid_v4_institutional_manipaware
-- Output: CG_mnq_hybrid_v5_clanmarshal (908 trades - FINAL)
-- Runtime: 0 seconds
-- Purpose: Prevent giving back big winners
--
-- Profit Lock Logic:
--   - Track cumulative daily P&L: v5_running_daily_pnl
--   - Track peak daily P&L: v5_running_daily_peak
--   - Calculate drawdown from peak: v5_drawdown_from_peak
--   - REMOVE trades where:
--       - Peak >= $3,000 (had a big winner)
--       - AND Drawdown <= -$500 (giving back $500+ from peak)
--
-- Rationale: If we're up $3K+ and draw down $500, stop trading to lock gains
--
-- Result: 908 trades, $71,429.40, 64.43% win rate
--
-- THIS IS THE FINAL v5 TABLE USED FOR ANALYSIS
-- ============================================================================

CREATE TABLE CG_mnq_hybrid_v5_clanmarshal
ENGINE = MergeTree
ORDER BY (trade_date, entry_time)
AS
WITH equity AS
(
    SELECT
        *,
        sum(net_pnl_usd) OVER (
            PARTITION BY trade_date
            ORDER BY entry_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS v5_running_daily_pnl
    FROM CG_mnq_hybrid_v4_institutional_manipaware
),

peaks AS
(
    SELECT
        *,
        max(v5_running_daily_pnl) OVER (
            PARTITION BY trade_date
            ORDER BY entry_time
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS v5_running_daily_peak
    FROM equity
),

profit_lock AS
(
    SELECT
        *,
        v5_running_daily_pnl - v5_running_daily_peak AS v5_drawdown_from_peak
    FROM peaks
)

SELECT *
FROM profit_lock
WHERE NOT
(
    v5_running_daily_peak >= 3000
    AND v5_drawdown_from_peak <= -500
);


-- ============================================================================
-- END OF CHATGPT v5 SQL CHAIN
-- ============================================================================
--
-- Final Result: 908 trades
-- Performance: $71,429.40 profit, 64.43% win rate
-- Period: September 24 - October 22, 2025
-- Total Runtime: ~750 seconds (12.5 minutes)
--
-- Data Flow:
--   789M MBO events
--   → 12.6M 100ms buckets
--   → 7,621 signal events
--   → 4,104 trades (after exit simulation)
--   → 908 final trades (after filters)
--
-- Key Innovation: ArrayFold for single position enforcement
-- Critical Filter: 6 manipulation awareness rules
-- ============================================================================
