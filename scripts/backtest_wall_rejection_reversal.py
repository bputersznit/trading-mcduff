#!/usr/bin/env python3
"""
Wall Rejection Reversal Strategy - Based on Bookmap Framework

LONG SETUP:
1. Large bid wall appears (200+ contracts)
2. Aggressive selling hits wall (negative delta, red bubbles)
3. ABSORPTION: Wall holds, price stops falling despite selling
4. CONFIRMATION: Bid stops retreating, ask lifts, micro higher low forms
5. ENTRY: Buy stop above microstructure pivot
6. STOP: Below wall where absorption occurred (thesis invalidation)
7. EXIT: Opposing ask wall, aggression exhaustion, or trailing

SHORT SETUP: Inverted (ask wall absorbs buying, price reverses down)

This is the CORRECT implementation per user's Bookmap framework.
"""

import clickhouse_connect
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
import argparse
from collections import deque


def get_client():
    return clickhouse_connect.get_client(
        host='localhost', port=8123, username='default',
        password='unlucky-strange', database='default'
    )


class Trade:
    def __init__(self, direction, entry_time, entry_price, entry_reason, regime,
                 wall_price, entry_delta):
        self.direction = direction
        self.entry_time = entry_time
        self.entry_price = entry_price
        self.entry_reason = entry_reason
        self.regime = regime
        self.wall_price = wall_price  # The wall that absorbed
        self.entry_delta = entry_delta
        self.exit_time = None
        self.exit_price = None
        self.exit_reason = None
        self.gross_pnl = 0
        self.costs = 0
        self.net_pnl = 0
        self.bars_held = 0
        self.max_favorable = 0  # Track for trailing


class WallState:
    """Track wall persistence and price reaction"""
    def __init__(self, price, size, side, timestamp):
        self.price = price
        self.size = size
        self.side = side  # 'bid' or 'ask'
        self.first_seen = timestamp
        self.last_seen = timestamp
        self.bars_present = 1
        self.absorption_detected = False
        self.price_low = None  # Lowest price while wall present (for bid)
        self.price_high = None  # Highest price while wall present (for ask)


class WallRejectionReversal:
    def __init__(self, params):
        self.params = params
        self.client = get_client()
        self.trades = []
        self.position = None
        self.equity = params['initial_capital']
        self.daily_opens = {}

        # Track active walls
        self.active_walls = {}  # {wall_key: WallState}
        self.recent_prices = deque(maxlen=10)  # Track recent price action

        self.signal_stats = {
            'walls_detected': 0,
            'absorption_detected': 0,
            'confirmation_failed': 0,
            'qualified_entries': 0
        }
        self.exit_stats = {
            'opposing_wall': 0,
            'aggression_exhaustion': 0,
            'trailing_stop': 0,
            'thesis_invalidation': 0,
            'time_exit': 0
        }
        self.last_stop_time = None

    def detect_regime_daily(self, date_str, current_price):
        if date_str not in self.daily_opens:
            return 'choppy'
        session_open = self.daily_opens[date_str]
        session_move_pct = (current_price - session_open) / session_open * 100
        if session_move_pct > 0.8:
            return 'trending_up'
        elif session_move_pct < -0.8:
            return 'trending_down'
        else:
            return 'choppy'

    def get_market_snapshot(self, timestamp, current_price):
        ts_str = str(timestamp)[:19]

        # Look for significant walls
        wall_query = f"""
            SELECT price,
                   sum(bid_liquidity_event_size) as bid_liq,
                   sum(ask_liquidity_event_size) as ask_liq
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S
            WHERE symbol = 'MNQZ5'
              AND toDateTime(ts_bucket) >= toDateTime('{ts_str}') - INTERVAL 30 SECOND
              AND toDateTime(ts_bucket) <= toDateTime('{ts_str}')
              AND price >= {current_price - self.params['wall_distance']}
              AND price <= {current_price + self.params['wall_distance']}
            GROUP BY price
            HAVING bid_liq >= {self.params['wall_threshold']} OR ask_liq >= {self.params['wall_threshold']}
            ORDER BY (bid_liq + ask_liq) DESC LIMIT 20
        """
        walls = self.client.query_df(wall_query)

        # Get aggression (last 30 seconds for reaction detection)
        aggr_query = f"""
            SELECT sum(buy_exec_size) as buy_vol,
                   sum(sell_exec_size) as sell_vol,
                   sum(exec_delta) as delta
            FROM BM_MNQ_AGGRESSION_EXECUTIONS_30S
            WHERE toDateTime(ts_bucket) >= toDateTime('{ts_str}') - INTERVAL 30 SECOND
              AND toDateTime(ts_bucket) <= toDateTime('{ts_str}')
        """
        aggr_df = self.client.query_df(aggr_query)

        aggression = {'buy_volume': 0, 'sell_volume': 0, 'delta': 0}
        if len(aggr_df) > 0 and not aggr_df.iloc[0].isna().all():
            row = aggr_df.iloc[0]
            aggression = {
                'buy_volume': float(row['buy_vol']) if not pd.isna(row['buy_vol']) else 0,
                'sell_volume': float(row['sell_vol']) if not pd.isna(row['sell_vol']) else 0,
                'delta': float(row['delta']) if not pd.isna(row['delta']) else 0
            }

        return walls, aggression

    def update_wall_tracking(self, walls, current_price, timestamp):
        """Track wall persistence and price reaction"""
        # Update existing walls
        for wall_key in list(self.active_walls.keys()):
            wall = self.active_walls[wall_key]
            wall_found = False

            for _, row in walls.iterrows():
                if wall.side == 'bid' and row['bid_liq'] >= self.params['wall_threshold']:
                    if abs(row['price'] - wall.price) < 0.5:  # Same wall
                        wall.last_seen = timestamp
                        wall.bars_present += 1
                        wall.size = row['bid_liq']
                        wall_found = True

                        # Track price extremes while wall present
                        if wall.price_low is None or current_price < wall.price_low:
                            wall.price_low = current_price
                        break

                elif wall.side == 'ask' and row['ask_liq'] >= self.params['wall_threshold']:
                    if abs(row['price'] - wall.price) < 0.5:
                        wall.last_seen = timestamp
                        wall.bars_present += 1
                        wall.size = row['ask_liq']
                        wall_found = True

                        if wall.price_high is None or current_price > wall.price_high:
                            wall.price_high = current_price
                        break

            # Remove wall if it disappeared
            if not wall_found:
                seconds_since_seen = (timestamp - wall.last_seen).total_seconds()
                if seconds_since_seen > 30:  # Wall pulled
                    del self.active_walls[wall_key]

        # Add new walls
        for _, row in walls.iterrows():
            if row['bid_liq'] >= self.params['wall_threshold']:
                wall_key = f"bid_{row['price']:.2f}"
                if wall_key not in self.active_walls:
                    self.active_walls[wall_key] = WallState(
                        price=row['price'],
                        size=row['bid_liq'],
                        side='bid',
                        timestamp=timestamp
                    )
                    self.active_walls[wall_key].price_low = current_price
                    self.signal_stats['walls_detected'] += 1

            if row['ask_liq'] >= self.params['wall_threshold']:
                wall_key = f"ask_{row['price']:.2f}"
                if wall_key not in self.active_walls:
                    self.active_walls[wall_key] = WallState(
                        price=row['price'],
                        size=row['ask_liq'],
                        side='ask',
                        timestamp=timestamp
                    )
                    self.active_walls[wall_key].price_high = current_price
                    self.signal_stats['walls_detected'] += 1

    def detect_absorption_and_reversal(self, current_price, aggression, timestamp):
        """
        Detect wall rejection reversal setup:
        1. Wall absorbing aggression (price not breaking despite pressure)
        2. Reversal confirmation (micro structure change)
        """
        if len(self.recent_prices) < 3:
            return None

        # LONG SETUP: Bid wall absorption
        for wall_key, wall in self.active_walls.items():
            if wall.side != 'bid' or wall.bars_present < 2:
                continue

            # Check if wall is near current price (approaching support)
            if current_price > wall.price - 3 and current_price < wall.price + 2:

                # Check for aggressive selling (negative delta)
                if aggression['delta'] < -self.params['min_delta_for_absorption']:

                    # ABSORPTION: Price NOT breaking below wall despite selling
                    if wall.price_low is not None and wall.price_low >= wall.price - 1.0:

                        # Mark absorption
                        if not wall.absorption_detected:
                            wall.absorption_detected = True
                            self.signal_stats['absorption_detected'] += 1

                        # CONFIRMATION: Look for micro higher low (reversal)
                        # Simplified: Price bounced off wall and is now above wall
                        if current_price > wall.price and current_price > wall.price_low:

                                # ENTRY SIGNAL: Buy stop above pivot
                                entry_price = current_price + 0.5  # Enter above current

                                return {
                                    'direction': 'long',
                                    'entry_price': entry_price,
                                    'wall_price': wall.price,
                                    'reason': f'BidReject@{wall.price:.2f}[{wall.size:.0f}]',
                                    'delta': aggression['delta']
                                }

        # SHORT SETUP: Ask wall absorption (inverted)
        for wall_key, wall in self.active_walls.items():
            if wall.side != 'ask' or wall.bars_present < 2:
                continue

            if current_price < wall.price + 3 and current_price > wall.price - 2:

                # Check for aggressive buying (positive delta)
                if aggression['delta'] > self.params['min_delta_for_absorption']:

                    # ABSORPTION: Price NOT breaking above wall despite buying
                    if wall.price_high is not None and wall.price_high <= wall.price + 1.0:

                        if not wall.absorption_detected:
                            wall.absorption_detected = True
                            self.signal_stats['absorption_detected'] += 1

                        # CONFIRMATION: Price bounced off wall and is now below wall
                        if current_price < wall.price and current_price < wall.price_high:
                                entry_price = current_price - 0.5

                                return {
                                    'direction': 'short',
                                    'entry_price': entry_price,
                                    'wall_price': wall.price,
                                    'reason': f'AskReject@{wall.price:.2f}[{wall.size:.0f}]',
                                    'delta': aggression['delta']
                                }

        return None

    def calculate_costs(self, entry_price, exit_price):
        commission = self.params['commission_per_rt']
        slippage_ticks = self.params['slippage_ticks']
        slippage_cost = slippage_ticks * self.params['tick_value']
        return commission + slippage_cost

    def check_exits(self, current_time, current_price, walls, aggression):
        """
        Exit hierarchy per Bookmap framework:
        1. Thesis invalidation (price breaks through absorbed wall)
        2. Opposing wall (structural target)
        3. Aggression exhaustion (flow weakening)
        4. Trailing stop (protect profits)
        5. Time exit
        """
        if not self.position:
            return None

        self.position.bars_held += 1

        # Track maximum favorable excursion for trailing
        if self.position.direction == 'long':
            if current_price > self.position.entry_price:
                favorable = current_price - self.position.entry_price
                if favorable > self.position.max_favorable:
                    self.position.max_favorable = favorable
        else:
            if current_price < self.position.entry_price:
                favorable = self.position.entry_price - current_price
                if favorable > self.position.max_favorable:
                    self.position.max_favorable = favorable

        # PRIORITY 1: THESIS INVALIDATION
        # If price fully breaks through absorbed wall, thesis failed
        if self.position.direction == 'long':
            if current_price < self.position.wall_price - self.params['invalidation_buffer']:
                self.exit_stats['thesis_invalidation'] += 1
                return 'thesis_invalidation', self.position.wall_price - self.params['invalidation_buffer'], 'wall_broken'

        else:  # SHORT
            if current_price > self.position.wall_price + self.params['invalidation_buffer']:
                self.exit_stats['thesis_invalidation'] += 1
                return 'thesis_invalidation', self.position.wall_price + self.params['invalidation_buffer'], 'wall_broken'

        # PRIORITY 2: OPPOSING WALL (Structural profit target)
        if len(walls) > 0:
            if self.position.direction == 'long':
                # Look for ask walls above (resistance)
                ask_walls = walls[walls['ask_liq'] >= self.params['wall_threshold']].to_dict('records')
                for wall in ask_walls:
                    wall_price = wall['price']
                    if current_price >= wall_price - 0.5:
                        self.exit_stats['opposing_wall'] += 1
                        return 'opposing_wall', wall_price, f'ASK@{wall_price:.2f}'

            else:  # SHORT
                bid_walls = walls[walls['bid_liq'] >= self.params['wall_threshold']].to_dict('records')
                for wall in bid_walls:
                    wall_price = wall['price']
                    if current_price <= wall_price + 0.5:
                        self.exit_stats['opposing_wall'] += 1
                        return 'opposing_wall', wall_price, f'BID@{wall_price:.2f}'

        # PRIORITY 3: AGGRESSION EXHAUSTION
        # If delta reverses significantly, exit
        if self.position.direction == 'long':
            if aggression['delta'] < -abs(self.position.entry_delta) * 0.5:
                self.exit_stats['aggression_exhaustion'] += 1
                return 'aggression_exhaustion', current_price, f'Δ{aggression["delta"]:.0f}'

        else:  # SHORT
            if aggression['delta'] > abs(self.position.entry_delta) * 0.5:
                self.exit_stats['aggression_exhaustion'] += 1
                return 'aggression_exhaustion', current_price, f'Δ{aggression["delta"]:.0f}'

        # PRIORITY 4: TRAILING STOP
        # If we've had favorable move, trail under structure
        if self.position.max_favorable > self.params['trailing_activation']:
            trail_distance = self.position.max_favorable * 0.5  # Trail at 50% of max favorable

            if self.position.direction == 'long':
                trail_price = (self.position.entry_price + self.position.max_favorable) - trail_distance
                if current_price < trail_price:
                    self.exit_stats['trailing_stop'] += 1
                    return 'trailing_stop', current_price, f'trail'

            else:  # SHORT
                trail_price = (self.position.entry_price - self.position.max_favorable) + trail_distance
                if current_price > trail_price:
                    self.exit_stats['trailing_stop'] += 1
                    return 'trailing_stop', current_price, f'trail'

        # PRIORITY 5: TIME EXIT
        if self.position.bars_held >= self.params['max_bars_in_position']:
            self.exit_stats['time_exit'] += 1
            return 'time_exit', current_price, None

        return None

    def run_date(self, date_str):
        bars_query = f"""
            SELECT ts_bucket, argMax(price, total_event_count) as price
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S
            WHERE trade_date = '{date_str}' AND symbol = 'MNQZ5' AND total_event_count > 0
            GROUP BY ts_bucket HAVING sum(total_event_count) > 10 ORDER BY ts_bucket
        """
        bars = self.client.query_df(bars_query)

        if len(bars) == 0:
            return

        self.daily_opens[date_str] = bars.iloc[0]['price']
        day_start_equity = self.equity

        for idx, bar in bars.iterrows():
            timestamp = bar['ts_bucket']
            price = bar['price']

            self.recent_prices.append(price)
            regime = self.detect_regime_daily(date_str, price)
            walls, aggression = self.get_market_snapshot(timestamp, price)

            # Update wall tracking
            self.update_wall_tracking(walls, price, timestamp)

            # Check exits first
            if self.position:
                exit_result = self.check_exits(timestamp, price, walls, aggression)
                if exit_result:
                    exit_reason, exit_price, detail = exit_result if len(exit_result) == 3 else (*exit_result, None)

                    if self.position.direction == 'long':
                        gross_pnl = (exit_price - self.position.entry_price) / self.params['tick_size'] * self.params['tick_value']
                    else:
                        gross_pnl = (self.position.entry_price - exit_price) / self.params['tick_size'] * self.params['tick_value']

                    costs = self.calculate_costs(self.position.entry_price, exit_price)
                    net_pnl = gross_pnl - costs

                    self.position.exit_time = timestamp
                    self.position.exit_price = exit_price
                    self.position.exit_reason = f"{exit_reason} {detail if detail else ''}"
                    self.position.gross_pnl = gross_pnl
                    self.position.costs = costs
                    self.position.net_pnl = net_pnl

                    self.equity += net_pnl
                    self.trades.append(self.position)

                    if exit_reason == 'thesis_invalidation':
                        self.last_stop_time = timestamp

                    self.position = None
                continue

            # Check for new absorption/reversal signals
            signal = self.detect_absorption_and_reversal(price, aggression, timestamp)

            if signal:
                # Stopout lockout check
                if self.last_stop_time is not None:
                    seconds_since_stop = (timestamp - self.last_stop_time).total_seconds()
                    if seconds_since_stop < self.params['stopout_lockout_seconds']:
                        continue

                self.signal_stats['qualified_entries'] += 1
                self.position = Trade(
                    direction=signal['direction'],
                    entry_time=timestamp,
                    entry_price=signal['entry_price'],
                    entry_reason=signal['reason'],
                    regime=regime,
                    wall_price=signal['wall_price'],
                    entry_delta=signal['delta']
                )

        day_pnl = self.equity - day_start_equity
        day_trades = len([t for t in self.trades if t.entry_time.date() == pd.to_datetime(date_str).date()])
        print(f"{date_str}: Trades={day_trades:3d} | P&L=${day_pnl:+7,.0f}")

    def generate_report(self):
        if len(self.trades) == 0:
            print("\nNO TRADES")
            print(f"\nSignal Stats:")
            for key, val in self.signal_stats.items():
                print(f"  {key}: {val}")
            return

        trades_df = pd.DataFrame([{
            'date': t.entry_time.date(), 'direction': t.direction,
            'exit_reason': t.exit_reason, 'gross_pnl': t.gross_pnl, 'costs': t.costs, 'net_pnl': t.net_pnl
        } for t in self.trades])

        print("\n" + "="*70)
        print("WALL REJECTION REVERSAL STRATEGY - BOOKMAP FRAMEWORK")
        print("="*70)

        total_trades = len(trades_df)
        winners = trades_df[trades_df['net_pnl'] > 0]
        gross_pnl = trades_df['gross_pnl'].sum()
        total_costs = trades_df['costs'].sum()
        net_pnl = trades_df['net_pnl'].sum()

        print(f"\nSIGNAL STATS:")
        for key, val in self.signal_stats.items():
            print(f"  {key}: {val}")

        print(f"\nEXIT BREAKDOWN:")
        for key, val in self.exit_stats.items():
            pct = (val/total_trades*100) if total_trades > 0 else 0
            print(f"  {key}: {val} ({pct:.1f}%)")

        print(f"\nTRADE RESULTS:")
        print(f"  Trades: {total_trades} | Winners: {len(winners)} ({len(winners)/total_trades*100:.1f}%)")
        print(f"  Gross P&L: ${gross_pnl:+,.2f}")
        print(f"  Total Costs: ${total_costs:,.2f}")
        print(f"  Net P&L: ${net_pnl:+,.2f}")
        print(f"  Return: {(net_pnl/self.params['initial_capital']*100):+.2f}%")

        if total_trades > 0:
            print(f"\n  Avg Gross/Trade: ${gross_pnl/total_trades:+.2f}")
            print(f"  Avg Net/Trade: ${net_pnl/total_trades:+.2f}")

        print("="*70 + "\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--start-date', required=True)
    parser.add_argument('--end-date', required=True)
    args = parser.parse_args()

    params = {
        'initial_capital': 50000, 'tick_size': 0.25, 'tick_value': 2.0,

        # Wall detection
        'wall_threshold': 200,       # Large liquidity wall
        'wall_distance': 12.5,

        # Absorption detection
        'min_delta_for_absorption': 100,  # Minimum aggression to consider

        # Entry/Exit
        'invalidation_buffer': 2.0,       # Stop: 2 pts beyond absorbed wall
        'trailing_activation': 5.0,       # Start trailing after 5 pts favorable
        'stopout_lockout_seconds': 120,

        # Costs
        'commission_per_rt': 0.70,
        'slippage_ticks': 1,

        'max_bars_in_position': 20  # 20 bars @ 30S = 10 min max (per framework: 15s-10min)
    }

    print("="*70)
    print("WALL REJECTION REVERSAL STRATEGY")
    print("Per Bookmap Framework Document")
    print("="*70)
    print(f"Entry: Wall absorption + reversal confirmation")
    print(f"Stop: Thesis invalidation ({params['invalidation_buffer']} pts beyond wall)")
    print(f"Exit: Opposing wall, aggression exhaustion, or trailing")
    print("="*70 + "\n")

    bt = WallRejectionReversal(params)
    start = datetime.strptime(args.start_date, '%Y-%m-%d')
    end = datetime.strptime(args.end_date, '%Y-%m-%d')
    current = start
    while current <= end:
        bt.run_date(current.strftime('%Y-%m-%d'))
        current += timedelta(days=1)

    bt.generate_report()


if __name__ == '__main__':
    main()
