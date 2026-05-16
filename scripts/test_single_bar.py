import clickhouse_connect

client = clickhouse_connect.get_client(
    host='localhost', port=8123, username='default',
    password='unlucky-strange', database='default'
)

# Get a real timestamp from the 1S data
ts_query = """
    SELECT ts_bucket, price
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
    WHERE trade_date = '2025-10-01'
      AND symbol = 'MNQ'
      AND total_event_count > 0
    ORDER BY ts_bucket
    LIMIT 1000, 1
"""

result = client.query_df(ts_query)
if len(result) > 0:
    timestamp = result.iloc[0]['ts_bucket']
    price = result.iloc[0]['price']

    print(f"Testing timestamp: {timestamp}")
    print(f"Price: {price}\n")

    # Test wall query
    wall_query = f"""
        SELECT
            price,
            sum(bid_liquidity_event_size) as bid_liq,
            sum(ask_liquidity_event_size) as ask_liq
        FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
        WHERE symbol = 'MNQ'
          AND ts_bucket >= '{timestamp}' - INTERVAL 30 SECOND
          AND ts_bucket <= '{timestamp}'
          AND price >= {price - 12.5}
          AND price <= {price + 12.5}
        GROUP BY price
        HAVING bid_liq >= 80 OR ask_liq >= 80
        ORDER BY (bid_liq + ask_liq) DESC
        LIMIT 5
    """

    print("Wall query result:")
    walls = client.query_df(wall_query)
    print(walls)
    print(f"Rows: {len(walls)}\n")

    # Test aggression query
    aggr_query = f"""
        SELECT
            sum(buy_volume) as buy_vol,
            sum(sell_volume) as sell_vol,
            sum(delta) as delta
        FROM CG_mnq_aggression_100ms
        WHERE bucket_time >= '{timestamp}' - INTERVAL 5 SECOND
          AND bucket_time <= '{timestamp}'
    """

    print("Aggression query result:")
    aggr = client.query_df(aggr_query)
    print(aggr)

