#!/usr/bin/env python3
import clickhouse_connect
import pandas as pd
from datetime import datetime

client = clickhouse_connect.get_client(
    host='localhost', port=8123, username='default',
    password='unlucky-strange', database='default'
)

date = '2025-10-01'
test_time = '2025-10-01 10:00:00.000'

print(f"=== DEBUG: {test_time} ===\n")

# Get current price
price_query = f"""
    SELECT price, total_event_count
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
    WHERE trade_date = '{date}'
      AND ts_bucket = '{test_time}'
      AND symbol = 'MNQ'
      AND total_event_count > 0
    LIMIT 5
"""
print("Current prices:")
prices = client.query_df(price_query)
print(prices)

if len(prices) > 0:
    current_price = prices.iloc[0]['price']
    print(f"\nUsing price: {current_price}")
    
    # Check for walls near this price
    wall_query = f"""
        SELECT
            price,
            sum(bid_liquidity_event_size) as bid_liq,
            sum(ask_liquidity_event_size) as ask_liq
        FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
        WHERE symbol = 'MNQ'
          AND ts_bucket >= toDateTime64('{test_time}', 3) - INTERVAL 60 SECOND
          AND ts_bucket <= toDateTime64('{test_time}', 3)
          AND price >= {current_price - 1.25}
          AND price <= {current_price + 1.25}
        GROUP BY price
        HAVING bid_liq >= 100 OR ask_liq >= 100
        ORDER BY price
    """
    
    print("\nWalls query:")
    print(wall_query)
    print("\nWalls found:")
    walls = client.query_df(wall_query)
    print(walls)
    
    # Check aggression
    aggr_query = f"""
        SELECT
            sum(buy_volume) as buy_vol,
            sum(sell_volume) as sell_vol,
            sum(delta) as net_delta
        FROM CG_mnq_aggression_100ms
        WHERE bucket_time >= toDateTime64('{test_time}', 3) - INTERVAL 5 SECOND
          AND bucket_time <= toDateTime64('{test_time}', 3)
    """
    
    print("\nAggression:")
    aggr = client.query_df(aggr_query)
    print(aggr)

