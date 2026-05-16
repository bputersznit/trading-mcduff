#!/usr/bin/env python3
"""
Simplified backtest with full debug output to diagnose issues
"""
import clickhouse_connect
import pandas as pd
from datetime import datetime

client = clickhouse_connect.get_client(
    host='localhost', port=8123, username='default',
    password='unlucky-strange', database='default'
)

date = '2025-10-01'

print(f"Simple Wall Backtest for {date}")
print("="*60)

# Get 5-minute bars instead of 1-minute for faster testing
bars_query = f"""
    SELECT
        toStartOfInterval(ts_bucket, INTERVAL 5 MINUTE) as bar_time,
        argMin(price, ts_bucket) as close,  -- Just use first price as proxy
        count(*) as events
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
    WHERE trade_date = '{date}'
      AND symbol = 'MNQ'
      AND total_event_count > 0
    GROUP BY bar_time
    HAVING events > 100  -- Filter to active periods
    ORDER BY bar_time
"""

print("\nFetching bars...")
bars = client.query_df(bars_query)
print(f"Got {len(bars)} bars\n")

checked = 0
walls_found = 0
aggression_found = 0

for idx, bar in bars.iterrows():
    if idx > 20:  # Just check first 20 bars
        break

    timestamp = bar['bar_time']
    price = bar['close']

    checked += 1

    # Get walls (wider range, longer time window)
    wall_query = f"""
        SELECT price,
               sum(bid_liquidity_event_size) as bid_liq,
               sum(ask_liquidity_event_size) as ask_liq
        FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
        WHERE symbol = 'MNQ'
          AND trade_date = '{date}'
          AND ts_bucket >= '{timestamp}' - INTERVAL 5 MINUTE
          AND ts_bucket <= '{timestamp}'
          AND price >= {price - 50}
          AND price <= {price + 50}
        GROUP BY price
        HAVING bid_liq >= 100 OR ask_liq >= 100
        ORDER BY (bid_liq + ask_liq) DESC
        LIMIT 5
    """

    walls = client.query_df(wall_query)

    # Get aggression
    aggr_query = f"""
        SELECT sum(buy_volume) as buy_vol,
               sum(sell_volume) as sell_vol,
               sum(delta) as delta
        FROM CG_mnq_aggression_100ms
        WHERE trade_date = '{date}'
          AND bucket_time >= '{timestamp}' - INTERVAL 10 SECOND
          AND bucket_time <= '{timestamp}'
    """

    aggr = client.query_df(aggr_query)

    has_walls = len(walls) > 0
    has_aggr = len(aggr) > 0 and aggr.iloc[0]['buy_vol'] > 50

    if has_walls:
        walls_found += 1
    if has_aggr:
        aggression_found += 1

    if has_walls or has_aggr:
        print(f"\n{timestamp} | Price: {price:.2f}")

        if has_walls:
            print(f"  WALLS: {len(walls)} found")
            for _, w in walls.iterrows():
                print(f"    {w['price']:.2f}: bid={w['bid_liq']:.0f}, ask={w['ask_liq']:.0f}")

        if has_aggr:
            a = aggr.iloc[0]
            buy = a['buy_vol']
            sell = a['sell_vol']
            ratio = buy/sell if sell > 0 else 999
            print(f"  AGGR: buy={buy:.0f}, sell={sell:.0f}, ratio={ratio:.2f}, delta={a['delta']:.0f}")

        # Check if both present
        if has_walls and has_aggr:
            print(f"  >>> BOTH PRESENT - Could generate signal!")

print(f"\n{'='*60}")
print(f"Checked {checked} bars")
print(f"Walls found: {walls_found} bars ({walls_found/checked*100:.1f}%)")
print(f"Aggression found: {aggression_found} bars ({aggression_found/checked*100:.1f}%)")
print(f"Both present: Checking overlap...")
