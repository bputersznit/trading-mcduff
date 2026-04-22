#!/usr/bin/env python3
"""
CG MNQ Order Flow Signal Generator for NT8
Reads real-time order flow from ClickHouse, generates signals, writes to CSV for NT8 consumption
"""

import clickhouse_connect
import time
import csv
from datetime import datetime, timedelta
import os
import sys

# ============================================================================
# CONFIGURATION
# ============================================================================

# ClickHouse connection
CH_HOST = 'localhost'
CH_PORT = 8123
CH_USER = 'default'
CH_PASSWORD = ''
CH_DATABASE = 'marketreplay'

# NT8 signal file (must match NinjaScript strategy parameter)
SIGNAL_FILE = r'C:\Trading\Signals\mnq_signals.csv'

# Symbol
SYMBOL = 'MNQ'

# How often to check for new signals (seconds)
CHECK_INTERVAL = 1

# Signal parameters (from backtest)
SIGNAL_PARAMS = {
    'ABSORPTION': {
        'min_aggressor': 30,
        'response_multiplier': 1.1,
        'min_net_resting': 15,
    },
    'ICEBERG': {
        'min_volume': 40,
        'max_visible_adds_ratio': 0.3,
        'min_imbalance': 10,
    },
    'BREAKOUT': {
        'volume_spike': 2.0,
        'aggression_spike': 2.5,
        'min_volume': 25,
        'min_aggression': 12,
    }
}

# ============================================================================
# SIGNAL DETECTION QUERIES
# ============================================================================

def get_absorption_signals(client, lookback_seconds=5):
    """
    Detect ABSORPTION signals (bid/ask absorption reversal)
    """
    query = f"""
    WITH signals AS (
        SELECT
            timestamp_1sec,
            symbol,
            price,

            -- SELL ABSORPTION (buy signal)
            sell_aggressor_volume,
            bid_adds,
            net_resting_bid,

            -- BUY ABSORPTION (sell signal)
            buy_aggressor_volume,
            ask_adds,
            net_resting_ask

        FROM mnq_orderflow_1sec
        WHERE symbol = '{SYMBOL}'
          AND timestamp_1sec >= now() - INTERVAL {lookback_seconds} SECOND
          AND timestamp_1sec <= now()
    )
    SELECT
        timestamp_1sec,
        symbol,

        -- LONG signal: Selling absorbed by bids
        argMaxIf(price, sell_aggressor_volume,
                 sell_aggressor_volume > {SIGNAL_PARAMS['ABSORPTION']['min_aggressor']}
                 AND bid_adds > sell_aggressor_volume * {SIGNAL_PARAMS['ABSORPTION']['response_multiplier']}
                 AND net_resting_bid > {SIGNAL_PARAMS['ABSORPTION']['min_net_resting']}) as long_price,

        maxIf(sell_aggressor_volume,
              sell_aggressor_volume > {SIGNAL_PARAMS['ABSORPTION']['min_aggressor']}
              AND bid_adds > sell_aggressor_volume * {SIGNAL_PARAMS['ABSORPTION']['response_multiplier']}
              AND net_resting_bid > {SIGNAL_PARAMS['ABSORPTION']['min_net_resting']}) as long_strength,

        -- SHORT signal: Buying absorbed by asks
        argMaxIf(price, buy_aggressor_volume,
                 buy_aggressor_volume > {SIGNAL_PARAMS['ABSORPTION']['min_aggressor']}
                 AND ask_adds > buy_aggressor_volume * {SIGNAL_PARAMS['ABSORPTION']['response_multiplier']}
                 AND net_resting_ask > {SIGNAL_PARAMS['ABSORPTION']['min_net_resting']}) as short_price,

        maxIf(buy_aggressor_volume,
              buy_aggressor_volume > {SIGNAL_PARAMS['ABSORPTION']['min_aggressor']}
              AND ask_adds > buy_aggressor_volume * {SIGNAL_PARAMS['ABSORPTION']['response_multiplier']}
              AND net_resting_ask > {SIGNAL_PARAMS['ABSORPTION']['min_net_resting']}) as short_strength

    FROM signals
    GROUP BY timestamp_1sec, symbol
    HAVING long_strength > 0 OR short_strength > 0
    ORDER BY timestamp_1sec DESC
    LIMIT 1
    """

    result = client.query(query)
    if result.result_rows:
        row = result.result_rows[0]
        timestamp, symbol, long_price, long_strength, short_price, short_strength = row

        if long_strength > short_strength:
            return {
                'timestamp': timestamp,
                'symbol': symbol,
                'signal_type': 'ABSORPTION',
                'direction': 'LONG',
                'price': long_price,
                'strength': long_strength
            }
        elif short_strength > 0:
            return {
                'timestamp': timestamp,
                'symbol': symbol,
                'signal_type': 'ABSORPTION',
                'direction': 'SHORT',
                'price': short_price,
                'strength': short_strength
            }

    return None


def get_iceberg_signals(client, lookback_seconds=5):
    """
    Detect ICEBERG signals (hidden institutional orders)
    """
    query = f"""
    WITH signals AS (
        SELECT
            timestamp_1sec,
            symbol,
            price,
            total_volume,
            bid_adds,
            ask_adds,
            net_resting_bid,
            net_resting_ask,
            aggressor_delta

        FROM mnq_orderflow_1sec
        WHERE symbol = '{SYMBOL}'
          AND timestamp_1sec >= now() - INTERVAL {lookback_seconds} SECOND
          AND timestamp_1sec <= now()
    )
    SELECT
        timestamp_1sec,
        symbol,

        -- LONG signal: Hidden bid support
        argMaxIf(price, net_resting_bid,
                 total_volume > {SIGNAL_PARAMS['ICEBERG']['min_volume']}
                 AND bid_adds < total_volume * {SIGNAL_PARAMS['ICEBERG']['max_visible_adds_ratio']}
                 AND net_resting_bid > {SIGNAL_PARAMS['ICEBERG']['min_imbalance']}) as long_price,

        maxIf(net_resting_bid,
              total_volume > {SIGNAL_PARAMS['ICEBERG']['min_volume']}
              AND bid_adds < total_volume * {SIGNAL_PARAMS['ICEBERG']['max_visible_adds_ratio']}
              AND net_resting_bid > {SIGNAL_PARAMS['ICEBERG']['min_imbalance']}) as long_strength,

        -- SHORT signal: Hidden ask pressure
        argMaxIf(price, net_resting_ask,
                 total_volume > {SIGNAL_PARAMS['ICEBERG']['min_volume']}
                 AND ask_adds < total_volume * {SIGNAL_PARAMS['ICEBERG']['max_visible_adds_ratio']}
                 AND net_resting_ask > {SIGNAL_PARAMS['ICEBERG']['min_imbalance']}) as short_price,

        maxIf(net_resting_ask,
              total_volume > {SIGNAL_PARAMS['ICEBERG']['min_volume']}
              AND ask_adds < total_volume * {SIGNAL_PARAMS['ICEBERG']['max_visible_adds_ratio']}
              AND net_resting_ask > {SIGNAL_PARAMS['ICEBERG']['min_imbalance']}) as short_strength

    FROM signals
    GROUP BY timestamp_1sec, symbol
    HAVING long_strength > 0 OR short_strength > 0
    ORDER BY timestamp_1sec DESC
    LIMIT 1
    """

    result = client.query(query)
    if result.result_rows:
        row = result.result_rows[0]
        timestamp, symbol, long_price, long_strength, short_price, short_strength = row

        if long_strength > short_strength:
            return {
                'timestamp': timestamp,
                'symbol': symbol,
                'signal_type': 'ICEBERG',
                'direction': 'LONG',
                'price': long_price,
                'strength': long_strength
            }
        elif short_strength > 0:
            return {
                'timestamp': timestamp,
                'symbol': symbol,
                'signal_type': 'ICEBERG',
                'direction': 'SHORT',
                'price': short_price,
                'strength': short_strength
            }

    return None


def get_breakout_signals(client, lookback_seconds=5):
    """
    Detect BREAKOUT signals (sudden aggression spikes)
    """
    query = f"""
    WITH baseline AS (
        SELECT
            timestamp_1sec,
            symbol,
            price,
            total_volume,
            abs(aggressor_delta) as abs_aggression,
            aggressor_delta,

            -- Rolling baseline (last 30 seconds)
            avg(total_volume) OVER (ORDER BY timestamp_1sec ROWS BETWEEN 30 PRECEDING AND 1 PRECEDING) as vol_baseline,
            avg(abs(aggressor_delta)) OVER (ORDER BY timestamp_1sec ROWS BETWEEN 30 PRECEDING AND 1 PRECEDING) as agg_baseline

        FROM mnq_orderflow_1sec
        WHERE symbol = '{SYMBOL}'
          AND timestamp_1sec >= now() - INTERVAL 35 SECOND
          AND timestamp_1sec <= now()
    )
    SELECT
        timestamp_1sec,
        symbol,

        -- LONG signal: Sudden buy pressure spike
        argMaxIf(price, aggressor_delta,
                 total_volume > vol_baseline * {SIGNAL_PARAMS['BREAKOUT']['volume_spike']}
                 AND abs_aggression > agg_baseline * {SIGNAL_PARAMS['BREAKOUT']['aggression_spike']}
                 AND total_volume > {SIGNAL_PARAMS['BREAKOUT']['min_volume']}
                 AND aggressor_delta > {SIGNAL_PARAMS['BREAKOUT']['min_aggression']}) as long_price,

        maxIf(aggressor_delta,
              total_volume > vol_baseline * {SIGNAL_PARAMS['BREAKOUT']['volume_spike']}
              AND abs_aggression > agg_baseline * {SIGNAL_PARAMS['BREAKOUT']['aggression_spike']}
              AND total_volume > {SIGNAL_PARAMS['BREAKOUT']['min_volume']}
              AND aggressor_delta > {SIGNAL_PARAMS['BREAKOUT']['min_aggression']}) as long_strength,

        -- SHORT signal: Sudden sell pressure spike
        argMaxIf(price, abs(aggressor_delta),
                 total_volume > vol_baseline * {SIGNAL_PARAMS['BREAKOUT']['volume_spike']}
                 AND abs_aggression > agg_baseline * {SIGNAL_PARAMS['BREAKOUT']['aggression_spike']}
                 AND total_volume > {SIGNAL_PARAMS['BREAKOUT']['min_volume']}
                 AND aggressor_delta < -{SIGNAL_PARAMS['BREAKOUT']['min_aggression']}) as short_price,

        maxIf(abs(aggressor_delta),
              total_volume > vol_baseline * {SIGNAL_PARAMS['BREAKOUT']['volume_spike']}
              AND abs_aggression > agg_baseline * {SIGNAL_PARAMS['BREAKOUT']['aggression_spike']}
              AND total_volume > {SIGNAL_PARAMS['BREAKOUT']['min_volume']}
              AND aggressor_delta < -{SIGNAL_PARAMS['BREAKOUT']['min_aggression']}) as short_strength

    FROM baseline
    WHERE timestamp_1sec >= now() - INTERVAL {lookback_seconds} SECOND
    GROUP BY timestamp_1sec, symbol
    HAVING long_strength > 0 OR short_strength > 0
    ORDER BY timestamp_1sec DESC
    LIMIT 1
    """

    result = client.query(query)
    if result.result_rows:
        row = result.result_rows[0]
        timestamp, symbol, long_price, long_strength, short_price, short_strength = row

        if long_strength > short_strength:
            return {
                'timestamp': timestamp,
                'symbol': symbol,
                'signal_type': 'BREAKOUT',
                'direction': 'LONG',
                'price': long_price,
                'strength': long_strength
            }
        elif short_strength > 0:
            return {
                'timestamp': timestamp,
                'symbol': symbol,
                'signal_type': 'BREAKOUT',
                'direction': 'SHORT',
                'price': short_price,
                'strength': short_strength
            }

    return None


# ============================================================================
# SIGNAL WRITING
# ============================================================================

def write_signal(signal):
    """
    Write signal to CSV file for NT8 consumption
    Overwrites file with most recent signal only
    """
    try:
        # Ensure directory exists
        directory = os.path.dirname(SIGNAL_FILE)
        if directory and not os.path.exists(directory):
            os.makedirs(directory)

        # Write signal (overwrite mode to avoid file growth)
        with open(SIGNAL_FILE, 'w', newline='') as f:
            writer = csv.writer(f)
            # Format: timestamp,symbol,signal_type,direction,price
            writer.writerow([
                signal['timestamp'].strftime('%Y-%m-%d %H:%M:%S'),
                signal['symbol'],
                signal['signal_type'],
                signal['direction'],
                signal['price']
            ])

        print(f"[{datetime.now()}] SIGNAL: {signal['signal_type']} {signal['direction']} @ {signal['price']} (strength: {signal['strength']:.0f})")

        return True

    except Exception as e:
        print(f"[{datetime.now()}] ERROR writing signal: {e}")
        return False


# ============================================================================
# MAIN LOOP
# ============================================================================

def main():
    print("="*80)
    print("CG MNQ Order Flow Signal Generator for NT8")
    print("="*80)
    print(f"Signal file: {SIGNAL_FILE}")
    print(f"Check interval: {CHECK_INTERVAL}s")
    print(f"Symbol: {SYMBOL}")
    print()

    # Connect to ClickHouse
    try:
        client = clickhouse_connect.get_client(
            host=CH_HOST,
            port=CH_PORT,
            username=CH_USER,
            password=CH_PASSWORD,
            database=CH_DATABASE
        )
        print(f"✅ Connected to ClickHouse: {CH_HOST}:{CH_PORT}/{CH_DATABASE}")
    except Exception as e:
        print(f"❌ Failed to connect to ClickHouse: {e}")
        sys.exit(1)

    # Main loop
    print("\n🔄 Monitoring for signals... (Ctrl+C to stop)\n")

    last_signal_time = datetime.min

    try:
        while True:
            # Check each signal type
            signals = []

            absorption = get_absorption_signals(client)
            if absorption:
                signals.append(absorption)

            iceberg = get_iceberg_signals(client)
            if iceberg:
                signals.append(iceberg)

            breakout = get_breakout_signals(client)
            if breakout:
                signals.append(breakout)

            # Process strongest signal (if any)
            if signals:
                # Sort by strength, take strongest
                best_signal = max(signals, key=lambda s: s['strength'])

                # Only send if it's a new signal (different timestamp)
                if best_signal['timestamp'] > last_signal_time:
                    write_signal(best_signal)
                    last_signal_time = best_signal['timestamp']

            # Wait before next check
            time.sleep(CHECK_INTERVAL)

    except KeyboardInterrupt:
        print("\n\n✋ Stopped by user")
    except Exception as e:
        print(f"\n❌ Error in main loop: {e}")
        import traceback
        traceback.print_exc()
    finally:
        print("\n👋 Signal generator stopped")


if __name__ == '__main__':
    main()
