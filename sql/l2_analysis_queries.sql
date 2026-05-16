-- ============================================================
-- L2 DEPTH DATA ANALYSIS QUERIES
-- ============================================================

-- 1. OVERALL SUMMARY
SELECT 
    COUNT(*) as total_events,
    COUNT(DISTINCT toDate(timestamp)) as trading_days,
    MIN(timestamp) as first_event,
    MAX(timestamp) as last_event,
    COUNT(DISTINCT position) as depth_levels,
    formatReadableQuantity(COUNT(*)) as readable_count
FROM l2_depth_raw;

-- 2. EVENTS BY DATE
SELECT 
    toDate(timestamp) as date,
    COUNT(*) as events,
    formatReadableQuantity(COUNT(*)) as readable,
    COUNT(DISTINCT position) as levels,
    MIN(timestamp) as first_time,
    MAX(timestamp) as last_time
FROM l2_depth_raw
GROUP BY date
ORDER BY date;

-- 3. RTH vs NON-RTH BREAKDOWN
SELECT 
    toDate(timestamp) as date,
    if(hour(timestamp) >= 8 AND hour(timestamp) <= 15, 'RTH', 'Non-RTH') as session,
    COUNT(*) as events,
    formatReadableQuantity(COUNT(*)) as readable,
    round(COUNT(*) * 100.0 / SUM(COUNT(*)) OVER (PARTITION BY date), 2) as pct_of_day
FROM l2_depth_raw
GROUP BY date, session
ORDER BY date, session;

-- 4. HOURLY EVENT DENSITY (RTH ONLY)
SELECT 
    hour(timestamp) as hour,
    COUNT(*) as total_events,
    COUNT(*) / COUNT(DISTINCT toDate(timestamp)) as avg_events_per_day,
    round(COUNT(*) / 3600.0 / COUNT(DISTINCT toDate(timestamp)), 1) as avg_per_second
FROM l2_depth_raw
WHERE hour(timestamp) >= 8 AND hour(timestamp) <= 15
GROUP BY hour
ORDER BY hour;

-- 5. DEPTH LEVEL DISTRIBUTION
SELECT 
    position,
    COUNT(*) as occurrences,
    formatReadableQuantity(COUNT(*)) as readable,
    round(COUNT(*) * 100.0 / SUM(COUNT(*)) OVER (), 2) as pct_total
FROM l2_depth_raw
GROUP BY position
ORDER BY position;

-- 6. OPERATION TYPE BREAKDOWN
SELECT 
    operation,
    CASE operation
        WHEN 'A' THEN 'Add'
        WHEN 'U' THEN 'Update'
        WHEN 'R' THEN 'Remove'
        ELSE 'Unknown'
    END as operation_name,
    COUNT(*) as count,
    formatReadableQuantity(COUNT(*)) as readable,
    round(COUNT(*) * 100.0 / SUM(COUNT(*)) OVER (), 2) as pct
FROM l2_depth_raw
GROUP BY operation
ORDER BY count DESC;

-- 7. BID vs ASK BALANCE
SELECT 
    side,
    CASE side
        WHEN 'B' THEN 'Bid'
        WHEN 'A' THEN 'Ask'
        ELSE 'Unknown'
    END as side_name,
    COUNT(*) as count,
    formatReadableQuantity(COUNT(*)) as readable,
    round(COUNT(*) * 100.0 / SUM(COUNT(*)) OVER (), 2) as pct
FROM l2_depth_raw
GROUP BY side;

-- 8. CHECK FOR GAPS (>1 minute without data)
SELECT 
    t1.timestamp as gap_start,
    t2.timestamp as gap_end,
    dateDiff('second', t1.timestamp, t2.timestamp) as gap_seconds
FROM (
    SELECT timestamp, ROW_NUMBER() OVER (ORDER BY timestamp) as rn
    FROM (SELECT DISTINCT timestamp FROM l2_depth_raw)
) t1
JOIN (
    SELECT timestamp, ROW_NUMBER() OVER (ORDER BY timestamp) as rn
    FROM (SELECT DISTINCT timestamp FROM l2_depth_raw)
) t2 ON t1.rn = t2.rn - 1
WHERE dateDiff('second', t1.timestamp, t2.timestamp) > 60
ORDER BY gap_seconds DESC
LIMIT 20;

-- 9. BUSIEST 5-MINUTE WINDOWS
SELECT 
    toStartOfFiveMinute(timestamp) as window,
    COUNT(*) as events,
    formatReadableQuantity(COUNT(*)) as readable,
    round(COUNT() / 300.0, 1) as events_per_second
FROM l2_depth_raw
WHERE hour(timestamp) >= 8 AND hour(timestamp) <= 15
GROUP BY window
ORDER BY events DESC
LIMIT 20;

-- 10. PRICE LEVELS ANALYSIS (TOP OF BOOK)
SELECT 
    toDate(timestamp) as date,
    position,
    COUNT(*) as updates,
    round(AVG(size), 1) as avg_size,
    round(MIN(price), 2) as min_price,
    round(MAX(price), 2) as max_price
FROM l2_depth_raw
WHERE position <= 2  -- Top 3 levels only
GROUP BY date, position
ORDER BY date, position;

-- 11. DATA QUALITY CHECK - Events per position per day
SELECT 
    toDate(timestamp) as date,
    position,
    COUNT(*) as events
FROM l2_depth_raw
GROUP BY date, position
ORDER BY date, position
FORMAT Vertical;

-- 12. SAMPLE DATA - First 100 events
SELECT *
FROM l2_depth_raw
ORDER BY timestamp
LIMIT 100;

