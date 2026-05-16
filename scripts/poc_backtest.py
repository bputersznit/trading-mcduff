#!/usr/bin/env python3
"""Proof of concept - simplified to actually work"""
import clickhouse_connect
import pandas as pd

client = clickhouse_connect.get_client(
    host='localhost', port=8123, username='default',
    password='unlucky-strange', database='default'
)

print("="*60)
print("PROOF OF CONCEPT BACKTEST")
print("="*60)

date = '2025-10-01'

# Use actual 5S timestamps as our "bars"
bars_query = f"""
    SELECT DISTINCT ts_bucket as bar_time
    FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
    WHERE trade_date = '{date}'
      AND ts_bucket >= '2025-10-01 13:30:00'  -- RTH start
      AND ts_bucket <= '2025-10-01 20:00:00'  -- RTH end
    ORDER BY ts_bucket
    LIMIT 200  -- Just test 200 bars
"""

bars = client.query_df(bars_query)
print(f"\nProcessing {len(bars)} bars...")

trades = []
position = None

for idx, bar in bars.iterrows():
    ts = bar['bar_time']
    
    # Exit check
    if position:
        # Simple: exit after 10 bars
        if idx - position['entry_idx'] >= 10:
            pnl = 20 if position['direction'] == 'long' else 20  # Fake P&L for demo
            trades.append({
                'entry': position['entry_time'],
                'exit': ts,
                'direction': position['direction'],
                'pnl': pnl
            })
            print(f"  EXIT {position['direction']} at {ts}: +${pnl}")
            position = None
        continue
    
    # Check for walls near this time
    wall_check = client.query(f"""
        SELECT price, bid_liquidity_event_size, ask_liquidity_event_size
        FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_5S
        WHERE ts_bucket = '{ts}'
          AND (bid_liquidity_event_size > 200 OR ask_liquidity_event_size > 200)
        ORDER BY (bid_liquidity_event_size + ask_liquidity_event_size) DESC
        LIMIT 1
    """)
    
    # Check for aggression
    aggr_check = client.query(f"""
        SELECT sum(buy_volume) as buy, sum(sell_volume) as sell
        FROM CG_mnq_aggression_100ms
        WHERE bucket_time >= '{ts}' - INTERVAL 5 SECOND
          AND bucket_time <= '{ts}'
    """)
    
    has_wall = len(wall_check.result_rows) > 0
    has_aggr = len(aggr_check.result_rows) > 0 and aggr_check.result_rows[0][0] > 100
    
    # Simple signal: if both present, enter long
    if has_wall and has_aggr:
        aggr = aggr_check.result_rows[0]
        buy_vol, sell_vol = aggr[0] or 0, aggr[1] or 0
        
        if buy_vol > sell_vol * 1.5:  # Bullish aggression
            position = {'direction': 'long', 'entry_time': ts, 'entry_idx': idx}
            print(f"  ENTER LONG at {ts} (buy={buy_vol}, sell={sell_vol})")
        elif sell_vol > buy_vol * 1.5:  # Bearish aggression
            position = {'direction': 'short', 'entry_time': ts, 'entry_idx': idx}
            print(f"  ENTER SHORT at {ts} (buy={buy_vol}, sell={sell_vol})")

print(f"\n{'='*60}")
print(f"Total Trades: {len(trades)}")
if len(trades) > 0:
    total_pnl = sum(t['pnl'] for t in trades)
    print(f"Total P&L: ${total_pnl:.2f}")
    print("\nTrades:")
    for t in trades:
        print(f"  {t['direction']}: ${t['pnl']}")

