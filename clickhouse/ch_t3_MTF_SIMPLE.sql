-- Simplified MTF backtest - just get signal count and estimate
WITH

bars_5min AS (
    SELECT
        toStartOfFiveMinutes(ts_event, 'America/New_York') as ts,
        toDate(ts_event, 'America/New_York') as dt,
        toHour(ts_event, 'America/New_York') as hr,
        argMax(price, ts_event) as c,
        sum(size) as v
    FROM mnq_mbo
    WHERE symbol = 'MNQZ5'
      AND toDate(ts_event, 'America/New_York') BETWEEN '2025-09-21' AND '2025-10-22'
      AND action IN ('T', 'F')
      AND toHour(ts_event, 'America/New_York') BETWEEN 9 AND 15
    GROUP BY ts, dt, hr
),

with_ema AS (
    SELECT
        ts, dt, hr, c, v,
        avg(c) OVER (ORDER BY ts ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) as ema5,
        v / nullIf(avg(v) OVER (ORDER BY ts ROWS BETWEEN 19 PRECEDING AND 1 PRECEDING), 0) as vr
    FROM bars_5min
),

signals AS (
    SELECT
        dt,
        ts,
        c,
        ema5,
        abs(c - ema5) as dist,
        vr,
        c - lag(c, 1) OVER (ORDER BY ts) as momentum
    FROM with_ema
    WHERE hr BETWEEN 11 AND 14  -- 11:00 AM - 3:00 PM
      AND ema5 IS NOT NULL
      AND vr IS NOT NULL
),

potential_signals AS (
    SELECT *
    FROM signals
    WHERE dist <= 3.0     -- Near EMA
      AND vr >= 1.3       -- Volume spike
      AND abs(momentum) > 0  -- Has momentum
)

SELECT
    '═══════════════════════════════════════════════════════' as output
UNION ALL SELECT 'T3 MTF TREND - Signal Count Analysis'
UNION ALL SELECT '═══════════════════════════════════════════════════════'
UNION ALL SELECT ''
UNION ALL SELECT concat('Total potential MTF signals: ', toString(count(*)))
FROM potential_signals

UNION ALL SELECT concat('Signals per day: ', toString(round(count(*) / 22.0, 1)))
FROM potential_signals

UNION ALL SELECT ''
UNION ALL SELECT '--- Daily Signal Count ---'
UNION ALL SELECT concat(toString(dt), ': ', toString(count(*)), ' signals')
FROM potential_signals
GROUP BY dt
ORDER BY dt

UNION ALL SELECT ''
UNION ALL SELECT '--- Estimated Performance (50% WR, 40pt target, 5pt stop) ---'
UNION ALL SELECT concat('Estimated trades (first per day): ', toString((SELECT uniq(dt) FROM potential_signals)))
UNION ALL SELECT concat('Winners (50%): ', toString((SELECT uniq(dt) FROM potential_signals) / 2), ' × $19.30 = $', toString(round((SELECT uniq(dt) FROM potential_signals) / 2 * 19.30, 2)))
UNION ALL SELECT concat('Losers (50%): ', toString((SELECT uniq(dt) FROM potential_signals) / 2), ' × -$3.20 = -$', toString(round((SELECT uniq(dt) FROM potential_signals) / 2 * 3.20, 2)))
UNION ALL SELECT concat('Estimated Total: $', toString(round((SELECT uniq(dt) FROM potential_signals) / 2 * 19.30 - (SELECT uniq(dt) FROM potential_signals) / 2 * 3.20, 2)))
UNION ALL SELECT ''
UNION ALL SELECT 'Math: 40pts = 160 ticks × $0.50 - $0.70 = $19.30'
UNION ALL SELECT '      5pts = 20 ticks × $0.50 + $0.70 = $3.20'
UNION ALL SELECT ''

FORMAT TSVRaw;
