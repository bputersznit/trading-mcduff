#!/usr/bin/env python3
"""
CG Hybrid v5 FIXED Backtest - With Safety Checks
Recreates v5 hybrid backtest with mandatory position safety enforcements
"""

import clickhouse_connect
import pandas as pd
from datetime import datetime, timedelta
import sys

# ================================================================
# SAFETY CONSTANTS (MANDATORY)
# ================================================================
MIN_SECONDS_BETWEEN_ENTRIES = 10  # Prevent rapid-fire
MAX_POSITION_SIZE = 1              # NEVER more than 1 contract

# ================================================================
# Configuration
# ================================================================
MNQ_TICK_SIZE = 0.25
MNQ_TICK_VALUE = 0.50  # $0.50 per tick for micro
COMMISSION_RT = 0.70
SLIPPAGE_TICKS = 2

# ORB Parameters
OR_START_HOUR = 9
OR_START_MINUTE = 30
OR_DURATION_MINUTES = 15
OR_MIN_WIDTH = 5.0  # points

# T2 Event Parameters (adjusted for tick data)
MIN_EVENT_DELTA = 20.0
MIN_EVENT_IMBALANCE = 0.15
EVENT_LOOKBACK_BARS = 200

# Trade Parameters
STOP_TICKS = 20
TARGET_TICKS = 40
MAX_HOLD_SECONDS = 600

# Session Times (ET)
RTH_START = 93000  # 9:30 AM
RTH_END = 155900   # 3:59 PM

print("=" * 80)
print("CG HYBRID V5 FIXED BACKTEST - WITH SAFETY CHECKS")
print("=" * 80)
print()
print("SAFETY ENFORCEMENT:")
print(f"  ✅ Min {MIN_SECONDS_BETWEEN_ENTRIES}s between entries")
print(f"  ✅ Max {MAX_POSITION_SIZE} contract at any time")
print(f"  ✅ Position state check before every signal")
print()

# ================================================================
# Connect to ClickHouse
# ================================================================
print("Connecting to ClickHouse...")
try:
    client = clickhouse_connect.get_client(
        host='localhost',
        port=8123,
        username='default',
        password=''
    )
    print("✅ Connected")
except Exception as e:
    print(f"❌ Connection failed: {e}")
    sys.exit(1)

# ================================================================
# Query Configuration
# ================================================================
START_DATE = '2025-09-24'
END_DATE = '2025-10-22'

print(f"\nBacktest Period: {START_DATE} to {END_DATE}")
print()

# ================================================================
# Fetch Tick Data
# ================================================================
query = f"""
SELECT
    timestamp,
    price,
    volume,
    bid_price,
    ask_price,
    side
FROM mnq_ticks
WHERE date >= '{START_DATE}' AND date <= '{END_DATE}'
  AND symbol = 'MNQZ5'
ORDER BY timestamp
"""

print("Fetching tick data...")
df = client.query_df(query)
print(f"✅ Loaded {len(df):,} ticks")

if len(df) == 0:
    print("❌ No data found")
    sys.exit(1)

# ================================================================
# Helper Functions
# ================================================================

def to_et_time(ts):
    """Convert UTC timestamp to ET hour/minute"""
    # Assuming timestamp is already in ET for this dataset
    return ts.hour * 10000 + ts.minute * 100 + ts.second

def is_rth(ts):
    """Check if within RTH"""
    et_time = to_et_time(ts)
    return RTH_START <= et_time <= RTH_END

def calculate_event_features(df_window):
    """Calculate T2 event delta and imbalance"""
    if len(df_window) == 0:
        return 0, 0, 0

    # Volume-weighted up/down events
    up_events = df_window[df_window['price'] > df_window['price'].shift(1)]['volume'].sum()
    down_events = df_window[df_window['price'] < df_window['price'].shift(1)]['volume'].sum()
    total_events = up_events + down_events

    if total_events == 0:
        return 0, 0, 0

    event_delta = up_events - down_events
    event_imbalance = (up_events - down_events) / total_events if total_events > 0 else 0

    return event_delta, event_imbalance, total_events

# ================================================================
# Backtest State
# ================================================================
class BacktestState:
    def __init__(self):
        self.position = 0  # CRITICAL: 0 = flat, 1 = long, -1 = short
        self.entry_price = 0
        self.entry_time = None
        self.last_entry_time = None  # v5 FIXED: Track last entry for rapid-fire prevention
        self.side = ""

        self.or_high = None
        self.or_low = None
        self.or_complete = False

        self.trades = []
        self.pnl = 0
        self.peak_pnl = 0

        # Safety counters
        self.rapid_fire_blocks = 0
        self.position_blocks = 0

    def reset_daily(self):
        """Reset daily state"""
        self.or_high = None
        self.or_low = None
        self.or_complete = False

state = BacktestState()

# ================================================================
# Process Ticks
# ================================================================
print("\nProcessing ticks...")

current_date = None
or_start_time = None
or_end_time = None

for idx, row in df.iterrows():
    ts = row['timestamp']
    price = row['price']

    # New trading day
    if ts.date() != current_date:
        current_date = ts.date()
        state.reset_daily()
        or_start_time = ts.replace(hour=OR_START_HOUR, minute=OR_START_MINUTE, second=0, microsecond=0)
        or_end_time = or_start_time + timedelta(minutes=OR_DURATION_MINUTES)
        print(f"\n{current_date}: Trading day started")

    # Build OR
    if not state.or_complete and or_start_time <= ts < or_end_time:
        if state.or_high is None:
            state.or_high = price
            state.or_low = price
        else:
            state.or_high = max(state.or_high, price)
            state.or_low = min(state.or_low, price)

    # Complete OR
    if not state.or_complete and ts >= or_end_time:
        state.or_complete = True
        or_width = state.or_high - state.or_low
        print(f"  OR Complete: {state.or_low:.2f} - {state.or_high:.2f} (width: {or_width:.2f})")

        if or_width < OR_MIN_WIDTH:
            print(f"  ⚠️ OR too narrow ({or_width:.2f} < {OR_MIN_WIDTH}), skipping day")
            state.or_complete = False  # Skip trading this day

    # Skip if not RTH or OR not complete
    if not is_rth(ts) or not state.or_complete:
        continue

    # ================================================================
    # CRITICAL SAFETY CHECK #1: Position State
    # ================================================================
    if state.position != 0:
        # Already in position - check for exits
        if state.side == "LONG":
            # Check stop
            if price <= state.entry_price - (STOP_TICKS * MNQ_TICK_SIZE):
                pnl = -STOP_TICKS * MNQ_TICK_VALUE - COMMISSION_RT - (SLIPPAGE_TICKS * MNQ_TICK_VALUE)
                state.trades.append({
                    'entry_time': state.entry_time,
                    'exit_time': ts,
                    'side': 'LONG',
                    'entry_price': state.entry_price,
                    'exit_price': price,
                    'outcome': 'STOP',
                    'pnl': pnl
                })
                state.pnl += pnl
                state.position = 0
                state.side = ""
                continue

            # Check target
            if price >= state.entry_price + (TARGET_TICKS * MNQ_TICK_SIZE):
                pnl = TARGET_TICKS * MNQ_TICK_VALUE - COMMISSION_RT - (SLIPPAGE_TICKS * MNQ_TICK_VALUE)
                state.trades.append({
                    'entry_time': state.entry_time,
                    'exit_time': ts,
                    'side': 'LONG',
                    'entry_price': state.entry_price,
                    'exit_price': price,
                    'outcome': 'TARGET',
                    'pnl': pnl
                })
                state.pnl += pnl
                state.peak_pnl = max(state.peak_pnl, state.pnl)
                state.position = 0
                state.side = ""
                continue

        elif state.side == "SHORT":
            # Check stop
            if price >= state.entry_price + (STOP_TICKS * MNQ_TICK_SIZE):
                pnl = -STOP_TICKS * MNQ_TICK_VALUE - COMMISSION_RT - (SLIPPAGE_TICKS * MNQ_TICK_VALUE)
                state.trades.append({
                    'entry_time': state.entry_time,
                    'exit_time': ts,
                    'side': 'SHORT',
                    'entry_price': state.entry_price,
                    'exit_price': price,
                    'outcome': 'STOP',
                    'pnl': pnl
                })
                state.pnl += pnl
                state.position = 0
                state.side = ""
                continue

            # Check target
            if price <= state.entry_price - (TARGET_TICKS * MNQ_TICK_SIZE):
                pnl = TARGET_TICKS * MNQ_TICK_VALUE - COMMISSION_RT - (SLIPPAGE_TICKS * MNQ_TICK_VALUE)
                state.trades.append({
                    'entry_time': state.entry_time,
                    'exit_time': ts,
                    'side': 'SHORT',
                    'entry_price': state.entry_price,
                    'exit_price': price,
                    'outcome': 'TARGET',
                    'pnl': pnl
                })
                state.pnl += pnl
                state.peak_pnl = max(state.peak_pnl, state.pnl)
                state.position = 0
                state.side = ""
                continue

        # Position still open, continue
        state.position_blocks += 1
        continue

    # ================================================================
    # CRITICAL SAFETY CHECK #2: Rapid-Fire Prevention
    # ================================================================
    if state.last_entry_time is not None:
        seconds_since_last = (ts - state.last_entry_time).total_seconds()
        if seconds_since_last < MIN_SECONDS_BETWEEN_ENTRIES:
            state.rapid_fire_blocks += 1
            continue

    # ================================================================
    # Signal Evaluation (only if flat and passed safety checks)
    # ================================================================

    # Get lookback window
    lookback_start = max(0, idx - EVENT_LOOKBACK_BARS)
    df_window = df.iloc[lookback_start:idx]

    if len(df_window) < 10:
        continue

    # Calculate event features
    event_delta, event_imbalance, total_events = calculate_event_features(df_window)

    # Check ORB location
    or_location = "INSIDE_OR"
    if price > state.or_high:
        or_location = "ABOVE_OR"
    elif price < state.or_low:
        or_location = "BELOW_OR"

    # LONG Signal
    if (event_delta > MIN_EVENT_DELTA and
        event_imbalance > MIN_EVENT_IMBALANCE and
        or_location != "ABOVE_OR"):  # Don't buy into highs

        # Enter LONG
        state.position = 1
        state.entry_price = price + (SLIPPAGE_TICKS * MNQ_TICK_SIZE)  # Assume slippage
        state.entry_time = ts
        state.last_entry_time = ts  # v5 FIXED: Track for rapid-fire prevention
        state.side = "LONG"
        continue

    # SHORT Signal
    if (event_delta < -MIN_EVENT_DELTA and
        event_imbalance < -MIN_EVENT_IMBALANCE and
        or_location != "BELOW_OR"):  # Don't sell into lows

        # Enter SHORT
        state.position = -1
        state.entry_price = price - (SLIPPAGE_TICKS * MNQ_TICK_SIZE)  # Assume slippage
        state.entry_time = ts
        state.last_entry_time = ts  # v5 FIXED: Track for rapid-fire prevention
        state.side = "SHORT"
        continue

# ================================================================
# Results
# ================================================================
print("\n" + "=" * 80)
print("BACKTEST RESULTS")
print("=" * 80)
print()

trades_df = pd.DataFrame(state.trades)

if len(trades_df) > 0:
    winners = len(trades_df[trades_df['outcome'] == 'TARGET'])
    losers = len(trades_df[trades_df['outcome'] == 'STOP'])
    win_rate = winners / len(trades_df) * 100

    print(f"Total Trades:            {len(trades_df)}")
    print(f"Winners:                 {winners}")
    print(f"Losers:                  {losers}")
    print(f"Win Rate:                {win_rate:.2f}%")
    print()

    print(f"Total P&L:               ${state.pnl:,.2f}")
    print(f"Peak P&L:                ${state.peak_pnl:,.2f}")
    print(f"Avg P&L per trade:       ${state.pnl / len(trades_df):.2f}")
    print()

    # Safety statistics
    print("SAFETY ENFORCEMENT STATS:")
    print(f"  Position blocks:       {state.position_blocks:,} (already in position)")
    print(f"  Rapid-fire blocks:     {state.rapid_fire_blocks:,} (< {MIN_SECONDS_BETWEEN_ENTRIES}s since last entry)")
    print()

    # Save results
    output_file = 'CG_mnq_hybrid_v5_FIXED_trades.csv'
    trades_df.to_csv(output_file, index=False)
    print(f"✅ Saved to: {output_file}")

else:
    print("❌ No trades generated")

print()
print("=" * 80)
