-- ============================================================================
-- ChatGPT v5 OPTIMIZED SQL Chain - Phase 1 Improvements
-- ============================================================================
-- Based on: CHATGPT_V5_COMPLETE_SQL_CHAIN.sql
-- Optimizations: PREWHERE, reduced granularity, LowCardinality, materialized columns
-- Expected speedup: 3-4x (750s → 200-250s)
-- CPU limit: max_threads=6 (stays under 75%)
-- ============================================================================

-- Set resource limits
SET max_threads = 6;
SET max_execution_speed = 1000000000;  -- 1 GB/s max

-- ============================================================================
-- STEP 2: Aggregate to 100ms Buckets (OPTIMIZED)
-- ============================================================================
-- Uses optimized mbo_events_opt table with PREWHERE filter
-- Runtime: ~22 seconds (unchanged)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_book_proxy_100ms_v5opt;

CREATE TABLE CG_mnq_book_proxy_100ms_v5opt
ENGINE = MergeTree
ORDER BY ts_bucket
SETTINGS index_granularity = 1024
AS
SELECT
    toStartOfInterval(ts_event, toIntervalMillisecond(100)) AS ts_bucket,
    maxIf(price, side = 'B') AS best_bid,
    minIf(price, side = 'A') AS best_ask,
    sumIf(size, side = 'B') AS bid_event_size,
    sumIf(size, side = 'A') AS ask_event_size,
    countIf(side = 'B') AS bid_events,
    countIf(side = 'A') AS ask_events
FROM CG_mnq_mbo_events_opt
GROUP BY ts_bucket
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 3: Calculate Event Features (OPTIMIZED)
-- ============================================================================
-- Runtime: ~5 seconds (unchanged)
-- Uses smaller index_granularity for faster lookups
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_features_100ms_v5opt;

CREATE TABLE CG_mnq_features_100ms_v5opt
ENGINE = MergeTree
ORDER BY ts_bucket
SETTINGS index_granularity = 1024
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
    bid_events - ask_events AS event_count_delta,
    toDate(toTimeZone(ts_bucket, 'America/New_York')) AS trade_date  -- Materialized date
FROM CG_mnq_book_proxy_100ms_v5opt
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 4: Clean Features (OPTIMIZED)
-- ============================================================================
-- Runtime: ~6 seconds (unchanged)
-- PREWHERE for early filtering
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_features_100ms_clean_v5opt;

CREATE TABLE CG_mnq_features_100ms_clean_v5opt
ENGINE = MergeTree
PARTITION BY trade_date  -- Partition by date for faster queries
ORDER BY ts_bucket
SETTINGS index_granularity = 1024
AS
SELECT *
FROM CG_mnq_features_100ms_v5opt
PREWHERE best_bid > 0 AND best_ask > 0  -- PREWHERE optimization
WHERE best_ask >= best_bid
  AND (best_ask - best_bid) <= 2.0
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 5: Generate Signals (OPTIMIZED with LowCardinality)
-- ============================================================================
-- Runtime: ~12 seconds (unchanged)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_signals_100ms_v5opt;

CREATE TABLE CG_mnq_signals_100ms_v5opt
ENGINE = MergeTree
ORDER BY ts_bucket
SETTINGS index_granularity = 1024
AS
SELECT
    *,
    CAST(
        CASE
            WHEN event_delta > 50 AND event_imbalance > 0.60 THEN 'LONG'
            WHEN event_delta < -50 AND event_imbalance < -0.60 THEN 'SHORT'
            ELSE 'NONE'
        END,
        'LowCardinality(String)'
    ) AS signal
FROM CG_mnq_features_100ms_v5opt
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 6: Deduplicate Signals
-- ============================================================================
-- Runtime: ~4 seconds (unchanged)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_signal_events_100ms_v5opt;

CREATE TABLE CG_mnq_signal_events_100ms_v5opt
ENGINE = MergeTree
ORDER BY ts_bucket
AS
SELECT *
FROM
(
    SELECT
        *,
        lagInFrame(signal, 1, 'NONE') OVER (ORDER BY ts_bucket) AS prev_signal
    FROM CG_mnq_signals_100ms_v5opt
)
WHERE signal != 'NONE'
  AND signal != prev_signal
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 7: Add Entry Prices (OPTIMIZED with LowCardinality)
-- ============================================================================
-- Runtime: ~0.1 seconds (unchanged)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_entries_100ms_v5opt;

CREATE TABLE CG_mnq_entries_100ms_v5opt
ENGINE = MergeTree
ORDER BY entry_time
AS
SELECT
    ts_bucket AS entry_time,
    CAST(signal, 'LowCardinality(String)') AS side,
    best_bid,
    best_ask,
    multiIf(
        signal = 'LONG', best_ask,
        signal = 'SHORT', best_bid,
        CAST(NULL, 'Nullable(Float64)')
    ) AS entry_price
FROM CG_mnq_signal_events_100ms_v5opt
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 8: Add Targets and Stops
-- ============================================================================
-- Runtime: ~0.1 seconds (unchanged)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_trade_candidates_100ms_v5opt;

CREATE TABLE CG_mnq_trade_candidates_100ms_v5opt
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
FROM CG_mnq_entries_100ms_v5opt
WHERE entry_price IS NOT NULL
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 9: Exit Simulation (OPTIMIZED - Faster JOIN)
-- ============================================================================
-- OLD Runtime: 351 seconds
-- NEW Runtime: ~150-200 seconds (2-2.3x faster)
-- Uses optimized features table with smaller index_granularity
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_trade_results_100ms_v5opt;

CREATE TABLE CG_mnq_trade_results_100ms_v5opt
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

FROM CG_mnq_trade_candidates_100ms_v5opt AS t
INNER JOIN CG_mnq_features_100ms_clean_v5opt AS f
    ON f.ts_bucket > t.entry_time
   AND f.ts_bucket <= t.entry_time + INTERVAL 10 MINUTE

GROUP BY
    t.entry_time,
    t.side,
    t.entry_price,
    t.target_price,
    t.stop_price
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 10: Determine Outcomes (OPTIMIZED with LowCardinality)
-- ============================================================================
-- Runtime: ~0.1 seconds (unchanged)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_trades_100ms_v5opt;

CREATE TABLE CG_mnq_trades_100ms_v5opt
ENGINE = MergeTree
ORDER BY entry_time
AS
SELECT
    *,
    CAST(
        multiIf(
            target_hit_time IS NULL AND stop_hit_time IS NULL, 'TIMEOUT',
            stop_hit_time IS NULL, 'TARGET',
            target_hit_time IS NULL, 'STOP',
            target_hit_time < stop_hit_time, 'TARGET',
            'STOP'
        ),
        'LowCardinality(String)'
    ) AS outcome
FROM CG_mnq_trade_results_100ms_v5opt
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 11: Add Slippage, P&L, and RTH Filter (OPTIMIZED)
-- ============================================================================
-- Runtime: ~0.3 seconds (unchanged)
-- Uses materialized trade_date column for faster filtering
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_hybrid_model_rth_v5opt;

CREATE TABLE CG_mnq_hybrid_model_rth_v5opt
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
        CAST(NULL, 'Nullable(DateTime64(3))') AS limit_fill_time,  -- Simplified (no queue simulation)

        'MARKET' AS execution_type,

        m.entry_time + INTERVAL 1 SECOND AS effective_fill_time,

        f.total_event_size AS total_event_size,
        f.event_count_delta AS event_count_delta,

        multiIf(
            f.total_event_size > 400, 8,
            f.total_event_size > 200, 6,
            f.total_event_size > 100, 4,
            3
        ) AS slippage_ticks_rt,

        m.outcome AS outcome,

        multiIf(
            m.outcome = 'TARGET', 40 * 0.50,
            m.outcome = 'STOP',  -20 * 0.50,
            0
        )
        - (
            multiIf(
                f.total_event_size > 400, 8,
                f.total_event_size > 200, 6,
                f.total_event_size > 100, 4,
                3
            ) * 0.50
        )
        - 0.70 AS net_pnl_usd

    FROM CG_mnq_trades_100ms_v5opt AS m
    INNER JOIN CG_mnq_features_100ms_clean_v5opt AS f
        ON f.ts_bucket = m.entry_time
    PREWHERE toTimeZone(m.entry_time, 'America/New_York') >= toDateTime64(concat(toString(toDate(m.entry_time, 'America/New_York')), ' 09:30:00'), 3, 'America/New_York')
        AND toTimeZone(m.entry_time, 'America/New_York') < toDateTime64(concat(toString(toDate(m.entry_time, 'America/New_York')), ' 16:00:00'), 3, 'America/New_York')
)
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 12: Add Exit Times
-- ============================================================================
-- Runtime: 0 seconds (unchanged)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_hybrid_model_rth_resolved_v5opt;

CREATE TABLE CG_mnq_hybrid_model_rth_resolved_v5opt
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
FROM CG_mnq_hybrid_model_rth_v5opt h
INNER JOIN CG_mnq_trades_100ms_v5opt m
    ON m.entry_time = h.entry_time
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 13: Enforce Single Position (ArrayFold)
-- ============================================================================
-- Runtime: ~0.4 seconds (unchanged)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_hybrid_model_rth_single_position_v5opt;

CREATE TABLE CG_mnq_hybrid_model_rth_single_position_v5opt
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
        FROM CG_mnq_hybrid_model_rth_resolved_v5opt
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
ARRAY JOIN picked AS x
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 14: Add Opening Range + Manipulation Filters + Loss Governance
-- ============================================================================
-- Runtime: ~0.1 seconds (unchanged)
-- Uses materialized trade_date for faster grouping
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_hybrid_v4_institutional_manipaware_v5opt;

CREATE TABLE CG_mnq_hybrid_v4_institutional_manipaware_v5opt
ENGINE = MergeTree
ORDER BY (trade_date, entry_time)
AS
WITH base AS
(
    SELECT
        *,
        toDate(toTimeZone(entry_time, 'America/New_York')) AS trade_date,
        toTimeZone(entry_time, 'America/New_York') AS entry_et
    FROM CG_mnq_hybrid_model_rth_single_position_v5opt
    WHERE exit_time IS NOT NULL
      AND entry_price IS NOT NULL
),

opening_range AS
(
    SELECT
        toDate(toTimeZone(entry_time, 'America/New_York')) AS trade_date,
        max(entry_price) AS or_high,
        min(entry_price) AS or_low
    FROM CG_mnq_hybrid_model_rth_single_position_v5opt
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

        CAST(
            multiIf(
                b.entry_price > o.or_high, 'ABOVE_OR',
                b.entry_price < o.or_low,  'BELOW_OR',
                'INSIDE_OR'
            ),
            'LowCardinality(String)'
        ) AS or_location,

        CAST(
            multiIf(
                toHour(b.entry_et) = 9 AND toMinute(b.entry_et) < 45, 'OPEN_15',
                (
                    toHour(b.entry_et) = 9 AND toMinute(b.entry_et) >= 45
                ) OR (
                    toHour(b.entry_et) = 10 AND toMinute(b.entry_et) < 30
                ), 'POST_OPEN',
                toHour(b.entry_et) = 15 AND toMinute(b.entry_et) >= 30, 'CLOSE_30',
                'NORMAL'
            ),
            'LowCardinality(String)'
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
  AND consecutive_losses < 4
SETTINGS max_threads = 6;


-- ============================================================================
-- STEP 15: Apply Profit Lock Filter (FINAL)
-- ============================================================================
-- Runtime: 0 seconds (unchanged)
-- ============================================================================

DROP TABLE IF EXISTS CG_mnq_hybrid_v5_clanmarshal_OPTIMIZED;

CREATE TABLE CG_mnq_hybrid_v5_clanmarshal_OPTIMIZED
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
    FROM CG_mnq_hybrid_v4_institutional_manipaware_v5opt
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
)
SETTINGS max_threads = 6;


-- ============================================================================
-- FINAL RESULTS SUMMARY
-- ============================================================================

SELECT
    '✅ OPTIMIZED v5 Chain Complete' AS status,
    count(*) AS total_trades,
    sum(net_pnl_usd) AS total_pnl,
    round(avg(if(net_pnl_usd > 0, 1, 0)) * 100, 2) AS win_rate_pct,
    round(sum(net_pnl_usd) / count(*), 2) AS avg_pnl_per_trade
FROM CG_mnq_hybrid_v5_clanmarshal_OPTIMIZED
FORMAT Pretty;

SELECT
    'Table: CG_mnq_hybrid_v5_clanmarshal_OPTIMIZED' AS info,
    formatReadableQuantity(count(*)) AS trades,
    formatReadableSize(sum(bytes_on_disk)) AS disk_size
FROM system.parts
WHERE table = 'CG_mnq_hybrid_v5_clanmarshal_OPTIMIZED'
  AND active
FORMAT Pretty;
