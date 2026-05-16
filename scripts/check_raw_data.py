import clickhouse_connect
from datetime import datetime

client = clickhouse_connect.get_client(
    host='localhost', port=8123, username='default',
    password='unlucky-strange', database='default'
)

date = '2025-10-01'
test_time = '2025-10-01 10:30:00'

print(f"Checking data at {test_time}\n")

# Check if heatmap 5S has ANY data in that minute
hm_count = client.query(f"""
    SELECT count(*) FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
    WHERE trade_date = '{date}'
      AND ts_bucket >= '{test_time}' - INTERVAL 1 MINUTE
      AND ts_bucket <= '{test_time}'
""")
print(f"Heatmap 5S rows in window: {hm_count.result_rows[0][0]}")

# Check aggression
ag_count = client.query(f"""
    SELECT count(*), sum(buy_volume), sum(sell_volume) 
    FROM CG_mnq_aggression_100ms
    WHERE trade_date = '{date}'
      AND bucket_time >= '{test_time}' - INTERVAL 10 SECOND
      AND bucket_time <= '{test_time}'
""")
print(f"Aggression rows: {ag_count.result_rows[0]}")

# Check what prices exist in heatmap around that time
prices = client.query_df(f"""
    SELECT DISTINCT price
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
    WHERE trade_date = '{date}'
      AND ts_bucket >= '{test_time}' - INTERVAL 1 MINUTE
      AND ts_bucket <= '{test_time}'
    ORDER BY price
    LIMIT 20
""")
print(f"\nPrices in heatmap: {len(prices)} distinct")
if len(prices) > 0:
    print(f"  Range: {prices.iloc[0]['price']:.2f} to {prices.iloc[-1]['price']:.2f}")

