-- L2 Depth Deduplication Checks and Cleanup

-- Check for duplicate rows (exact matches)
SELECT
    timestamp,
    side,
    operation,
    position,
    price,
    size,
    COUNT(*) as duplicates
FROM l2_depth_raw
GROUP BY timestamp, side, operation, position, price, size
HAVING COUNT(*) > 1
ORDER BY duplicates DESC
LIMIT 100;

-- Count total duplicates
SELECT
    COUNT(*) as total_duplicate_groups,
    SUM(cnt - 1) as total_extra_rows
FROM (
    SELECT COUNT(*) as cnt
    FROM l2_depth_raw
    GROUP BY timestamp, side, operation, position, price, size
    HAVING COUNT(*) > 1
);

-- Create deduplicated view (if needed)
CREATE VIEW IF NOT EXISTS l2_depth_deduped AS
SELECT DISTINCT *
FROM l2_depth_raw;

-- Manual deduplication (if needed) - creates new table without duplicates
-- USE WITH CAUTION - Only run if you have confirmed duplicates
/*
CREATE TABLE l2_depth_raw_deduped AS
SELECT DISTINCT *
FROM l2_depth_raw;

-- After verification, swap tables:
RENAME TABLE l2_depth_raw TO l2_depth_raw_backup,
             l2_depth_raw_deduped TO l2_depth_raw;
*/

-- Check timestamp coverage to find gaps or overlaps
SELECT
    date,
    COUNT(*) as rows,
    MIN(timestamp) as first_event,
    MAX(timestamp) as last_event,
    round((MAX(timestamp) - MIN(timestamp)) / 3600, 2) as hours_covered
FROM (
    SELECT
        toDate(timestamp) as date,
        timestamp
    FROM l2_depth_raw
)
GROUP BY date
ORDER BY date;
