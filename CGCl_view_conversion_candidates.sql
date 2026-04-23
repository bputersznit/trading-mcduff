-- Tables that could potentially be converted to views
-- IMPORTANT: Only convert if the table is rarely queried or query performance is acceptable
-- BACKUP YOUR DATA BEFORE CONVERSION!

-- mnq_features_5s: 36.08 MiB, 366,796 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.mnq_features_5s;

-- mnq_regime_5s_v2: 21.55 MiB, 366,796 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.mnq_regime_5s_v2;

-- mnq_regime_5s_v3: 21.55 MiB, 366,796 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.mnq_regime_5s_v3;

-- mnq_regime_5s: 21.52 MiB, 366,796 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.mnq_regime_5s;

-- mnq_regime_5s_v3_backup: 20.51 MiB, 366,796 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.mnq_regime_5s_v3_backup;

-- mnq_regime_5s_hyst: 15.72 MiB, 366,796 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.mnq_regime_5s_hyst;

-- mnq_regime_5s_sticky: 4.16 MiB, 366,796 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.mnq_regime_5s_sticky;

-- mnq_wyckoff_phases: 58.83 KiB, 6,072 rows
-- Engine: ReplacingMergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.mnq_wyckoff_phases;

-- CG_mnq_opening_range_15m: 1.59 KiB, 22 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.CG_mnq_opening_range_15m;

-- CG_mnq_daily_atr: 1.55 KiB, 27 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.CG_mnq_daily_atr;

-- CG_mnq_daily_tr: 1.51 KiB, 27 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.CG_mnq_daily_tr;

-- mnq_params_by_regime: 1.22 KiB, 4 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.mnq_params_by_regime;

-- mnq_doubling_runs: 1.11 KiB, 1 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.mnq_doubling_runs;

-- CG_mnq_daily_atr_prior: 659.00 B, 27 rows
-- Engine: MergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.CG_mnq_daily_atr_prior;

-- mnq_features_1s_days: 561.00 B, 30 rows
-- Engine: ReplacingMergeTree
-- To convert:
--   1. Create a view with the same logic
--   2. Test the view performance
--   3. If acceptable, drop the table and use the view
-- DROP TABLE IF EXISTS default.mnq_features_1s_days;

