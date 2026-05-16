#!/usr/bin/env python3
"""Quick test to see wall and aggression data"""
import clickhouse_connect

client = clickhouse_connect.get_client(
    host='localhost', port=8123, username='default',
    password='unlucky-strange', database='default'
)

# Check a specific time window
date = '2025-10-01'
test_time = '2025-10-01 09:30:00'

print(f"Testing data at {test_time}\n")

# Get price around that time
price_query = f"""
    SELECT ts_bucket, price, total_event_count
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
    WHERE trade_date = '{date}'
      AND ts_bucket >= '{test_time}'
      AND ts_bucket < '{test_time}' + INTERVAL 5 MINUTE
      AND symbol = 'MNQ'
    ORDER BY ts_bucket
    LIMIT 10
"""
print("Sample prices:")
print(client.query_df(price_query))

# Check for walls
wall_query = f"""
    SELECT price, 
           sum(bid_liquidity_event_size) as bid_liq,
           sum(ask_liquidity_event_size) as ask_liq,
           count(*) as buckets
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
    WHERE trade_date = '{date}'
      AND ts_bucket >= '{test_time}'
      AND ts_bucket < '{test_time}' + INTERVAL 5 MINUTE
      AND symbol = 'MNQ'
    GROUP BY price
    ORDER BY (bid_liq + ask_liq) DESC
    LIMIT 10
"""
print("\nTop liquidity levels:")
print(client.query_df(wall_query))

# Check aggression
aggr_query = f"""
    SELECT bucket_time, buy_volume, sell_volume, delta
    FROM CG_mnq_aggression_100ms
    WHERE trade_date = '{date}'
      AND bucket_time >= '{test_time}'
      AND bucket_time < '{test_time}' + INTERVAL 1 MINUTE
    ORDER BY bucket_time
    LIMIT 10
"""
print("\nSample aggression:")
print(client.query_df(aggr_query))

# Summary stats
print("\n=== SUMMARY STATS ===")

# Heatmap coverage
hm_count = client.query(f"SELECT count(*) FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S WHERE trade_date = '{date}'")
print(f"Heatmap 5S rows for {date}: {hm_count.result_rows[0][0]:,}")

# Aggression coverage  
ag_count = client.query(f"SELECT count(*) FROM CG_mnq_aggression_100ms WHERE trade_date = '{date}'")
print(f"Aggression 100ms rows for {date}: {ag_count.result_rows[0][0]:,}")

# Wall stats
wall_stats = client.query_df(f"""
    SELECT 
        quantile(0.5)(bid_liquidity_event_size) as median_bid,
        quantile(0.9)(bid_liquidity_event_size) as p90_bid,
        quantile(0.95)(bid_liquidity_event_size) as p95_bid,
        max(bid_liquidity_event_size) as max_bid
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
    WHERE trade_date = '{date}' AND bid_liquidity_event_size > 0
""")
print("\nBid liquidity distribution:")
print(wall_stats)

# Aggression stats
aggr_stats = client.query_df(f"""
    SELECT 
        sum(buy_volume) as total_buy,
        sum(sell_volume) as total_sell,
        avg(buy_volume) as avg_buy,
        avg(sell_volume) as avg_sell
    FROM CG_mnq_aggression_100ms
    WHERE trade_date = '{date}'
""")
print("\nAggression stats:")
print(aggr_stats)

