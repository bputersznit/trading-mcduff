#!/usr/bin/env python3
"""
T2-Enhanced Strategy - 30S Timeframe
Key Features:
1. Violence Suppression: Block trades when abs(delta) > 300
2. Stopout Lockout: 120-second cooldown after stop hit
3. Wall Absorption: Only trade walls that HOLD under aggression
4. Higher quality thresholds: walls >= 200, aggression >= 150

Tests on 30S bars to reduce noise and overtrading.
"""

import clickhouse_connect
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
import argparse


def get_client():
    return clickhouse_connect.get_client(
        host='localhost', port=8123, username='default',
        password='unlucky-strange', database='default'
    )


class Trade:
    def __init__(self, direction, entry_time, entry_price, entry_reason, regime):
        self.direction = direction
        self.entry_time = entry_time
        self.entry_price = entry_price
        self.entry_reason = entry_reason
        self.regime = regime
        self.exit_time = None
        self.exit_price = None
        self.exit_reason = None
        self.gross_pnl = 0
        self.costs = 0
        self.net_pnl = 0
        self.bars_held = 0


class T2EnhancedBacktest:
    def __init__(self, params):
        self.params = params
        self.client = get_client()
        self.trades = []
        self.position = None
        self.equity = params['initial_capital']
        self.daily_opens = {}
        self.signal_stats = {
            'total_checked': 0,
            'passed_basic': 0,
            'failed_violence': 0,
            'failed_lockout': 0,
            'failed_absorption': 0,
            'qualified': 0
        }
        self.last_stop_time = None  # For stopout lockout

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

    def get_regime_params(self, regime):
        if regime == 'trending_up':
            return {
                'profit_target': self.params['trend_profit_target'],
                'stop_loss': self.params['trend_stop_loss'],
                'allowed_direction': 'long'
            }
        elif regime == 'trending_down':
            return {
                'profit_target': self.params['trend_profit_target'],
                'stop_loss': self.params['trend_stop_loss'],
                'allowed_direction': 'short'
            }
        else:
            return {
                'profit_target': self.params['chop_profit_target'],
                'stop_loss': self.params['chop_stop_loss'],
                'allowed_direction': 'both'
            }

    def get_market_snapshot(self, timestamp, current_price):
        ts_str = str(timestamp)[:19]

        # Use 30S heatmap table (uses contract symbol MNQZ5)
        wall_query = f"""
            SELECT price, sum(bid_liquidity_event_size) as bid_liq, sum(ask_liquidity_event_size) as ask_liq
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

        # Use 30S aggression table
        aggr_query = f"""
            SELECT sum(buy_exec_size) as buy_vol, sum(sell_exec_size) as sell_vol, sum(exec_delta) as delta
            FROM BM_MNQ_AGGRESSION_EXECUTIONS_30S
            WHERE toDateTime(ts_bucket) >= toDateTime('{ts_str}') - INTERVAL 60 SECOND
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

    def calculate_costs(self, entry_price, exit_price):
        commission = self.params['commission_per_rt']
        slippage_ticks = self.params['slippage_ticks']
        slippage_cost = slippage_ticks * self.params['tick_value']
        return commission + slippage_cost

    def check_exit(self, current_time, current_price, regime_params):
        if not self.position:
            return None

        pos = self.position
        tick_size = self.params['tick_size']
        profit_target_ticks = regime_params['profit_target']
        stop_loss_ticks = regime_params['stop_loss']

        if pos.direction == 'long':
            profit_price = pos.entry_price + (profit_target_ticks * tick_size)
            stop_price = pos.entry_price - (stop_loss_ticks * tick_size)
            if current_price >= profit_price:
                return 'profit_target', profit_price
            if current_price <= stop_price:
                return 'stop_loss', stop_price
        else:
            profit_price = pos.entry_price - (profit_target_ticks * tick_size)
            stop_price = pos.entry_price + (stop_loss_ticks * tick_size)
            if current_price <= profit_price:
                return 'profit_target', profit_price
            if current_price >= stop_price:
                return 'stop_loss', stop_price

        if pos.bars_held >= self.params['max_bars_in_position']:
            return 'time_exit', current_price

        return None

    def evaluate_signal(self, walls, aggression, current_price, current_time, regime_params):
        """T2-Enhanced signal evaluation with multiple quality filters"""
        self.signal_stats['total_checked'] += 1

        if len(walls) == 0:
            return None

        buy_vol = aggression['buy_volume']
        sell_vol = aggression['sell_volume']
        delta = aggression['delta']
        total_vol = buy_vol + sell_vol

        # Basic filters
        if total_vol < self.params['aggression_threshold']:
            return None
        if abs(delta) < self.params['min_delta']:
            return None

        self.signal_stats['passed_basic'] += 1

        # T2 FILTER 1: VIOLENCE SUPPRESSION
        # Block trades during extreme volatility spikes
        if abs(delta) > self.params['violence_threshold']:
            self.signal_stats['failed_violence'] += 1
            return None

        # T2 FILTER 2: STOPOUT LOCKOUT
        # 120-second cooldown after stop hit (prevents stupid re-entries)
        if self.last_stop_time is not None:
            seconds_since_stop = (current_time - self.last_stop_time).total_seconds()
            if seconds_since_stop < self.params['stopout_lockout_seconds']:
                self.signal_stats['failed_lockout'] += 1
                return None

        ratio_threshold = self.params['aggression_ratio']
        allowed_direction = regime_params['allowed_direction']

        bid_walls = walls[walls['bid_liq'] >= self.params['wall_threshold']].to_dict('records')
        ask_walls = walls[walls['ask_liq'] >= self.params['wall_threshold']].to_dict('records')

        # LONG signals
        if allowed_direction in ['long', 'both']:
            for wall in bid_walls:
                wall_price = wall['price']
                wall_size = wall['bid_liq']

                if abs(current_price - wall_price) <= self.params['wall_distance']:
                    buy_ratio = buy_vol / sell_vol if sell_vol > 0 else 999

                    # T2 FILTER 3: WALL ABSORPTION
                    # Wall must be STRONG and aggression must be ALIGNED
                    # (Strong buying into bid wall = absorption test)
                    if buy_ratio >= ratio_threshold and delta > 0:
                        # Check absorption: net resting > 0 means wall is holding
                        # Simplified: if wall_size is large and delta positive = absorption
                        if wall_size >= self.params['wall_threshold'] * 1.5:  # 1.5x threshold = "holding"
                            self.signal_stats['qualified'] += 1
                            return {
                                'direction': 'long',
                                'reason': f'BID@{wall_price:.2f}[{wall_size:.0f}]Δ{delta:.0f}',
                                'wall_size': wall_size,
                                'delta': delta
                            }
                        else:
                            self.signal_stats['failed_absorption'] += 1

        # SHORT signals
        if allowed_direction in ['short', 'both']:
            for wall in ask_walls:
                wall_price = wall['price']
                wall_size = wall['ask_liq']

                if abs(current_price - wall_price) <= self.params['wall_distance']:
                    sell_ratio = sell_vol / buy_vol if buy_vol > 0 else 999

                    if sell_ratio >= ratio_threshold and delta < 0:
                        if wall_size >= self.params['wall_threshold'] * 1.5:
                            self.signal_stats['qualified'] += 1
                            return {
                                'direction': 'short',
                                'reason': f'ASK@{wall_price:.2f}[{wall_size:.0f}]Δ{delta:.0f}',
                                'wall_size': wall_size,
                                'delta': delta
                            }
                        else:
                            self.signal_stats['failed_absorption'] += 1

        return None

    def run_date(self, date_str):
        # Use 30S bars (uses contract symbol MNQZ5)
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
        regime_counts = {'trending_up': 0, 'trending_down': 0, 'choppy': 0}
        day_start_equity = self.equity

        for idx, bar in bars.iterrows():
            timestamp = bar['ts_bucket']
            price = bar['price']

            regime = self.detect_regime_daily(date_str, price)
            regime_counts[regime] += 1
            regime_params = self.get_regime_params(regime)

            if self.position:
                self.position.bars_held += 1
                exit_result = self.check_exit(timestamp, price, regime_params)
                if exit_result:
                    exit_reason, exit_price = exit_result

                    # Calculate P&L
                    if self.position.direction == 'long':
                        gross_pnl = (exit_price - self.position.entry_price) / self.params['tick_size'] * self.params['tick_value']
                    else:
                        gross_pnl = (self.position.entry_price - exit_price) / self.params['tick_size'] * self.params['tick_value']

                    costs = self.calculate_costs(self.position.entry_price, exit_price)
                    net_pnl = gross_pnl - costs

                    self.position.exit_time = timestamp
                    self.position.exit_price = exit_price
                    self.position.exit_reason = exit_reason
                    self.position.gross_pnl = gross_pnl
                    self.position.costs = costs
                    self.position.net_pnl = net_pnl

                    self.equity += net_pnl
                    self.trades.append(self.position)

                    # T2 STOPOUT LOCKOUT: Record stop time
                    if exit_reason == 'stop_loss':
                        self.last_stop_time = timestamp

                    self.position = None
                continue

            # Check for new signals (every bar on 30S)
            walls, aggression = self.get_market_snapshot(timestamp, price)
            signal = self.evaluate_signal(walls, aggression, price, timestamp, regime_params)

            if signal:
                self.position = Trade(
                    direction=signal['direction'],
                    entry_time=timestamp,
                    entry_price=price,
                    entry_reason=signal['reason'],
                    regime=regime
                )

        day_pnl = self.equity - day_start_equity
        day_trades = len([t for t in self.trades if t.entry_time.date() == pd.to_datetime(date_str).date()])
        total = sum(regime_counts.values())
        print(f"{date_str}: Trades={day_trades:3d} | P&L=${day_pnl:+7,.0f} | Regimes: UP={regime_counts['trending_up']/total*100:.0f}% DN={regime_counts['trending_down']/total*100:.0f}% CH={regime_counts['choppy']/total*100:.0f}%")

    def generate_report(self):
        if len(self.trades) == 0:
            print("\nNO TRADES")
            print(f"\nSignal Filter Stats:")
            print(f"  Total checked: {self.signal_stats['total_checked']:,}")
            print(f"  Passed basic filters: {self.signal_stats['passed_basic']:,}")
            print(f"  Blocked by violence: {self.signal_stats['failed_violence']:,}")
            print(f"  Blocked by lockout: {self.signal_stats['failed_lockout']:,}")
            print(f"  Failed absorption: {self.signal_stats['failed_absorption']:,}")
            return

        trades_df = pd.DataFrame([{
            'date': t.entry_time.date(), 'direction': t.direction, 'regime': t.regime,
            'gross_pnl': t.gross_pnl, 'costs': t.costs, 'net_pnl': t.net_pnl
        } for t in self.trades])

        print("\n" + "="*70)
        print("T2-ENHANCED STRATEGY - 30S TIMEFRAME")
        print("="*70)

        total_trades = len(trades_df)
        winners = trades_df[trades_df['net_pnl'] > 0]
        gross_pnl = trades_df['gross_pnl'].sum()
        total_costs = trades_df['costs'].sum()
        net_pnl = trades_df['net_pnl'].sum()

        # Filter effectiveness
        print(f"\nT2 FILTER EFFECTIVENESS:")
        print(f"  Signals checked: {self.signal_stats['total_checked']:,}")
        print(f"  Passed basic: {self.signal_stats['passed_basic']:,} ({self.signal_stats['passed_basic']/self.signal_stats['total_checked']*100:.1f}%)")
        print(f"  Blocked by violence: {self.signal_stats['failed_violence']:,}")
        print(f"  Blocked by lockout: {self.signal_stats['failed_lockout']:,}")
        print(f"  Failed absorption: {self.signal_stats['failed_absorption']:,}")
        print(f"  QUALIFIED: {self.signal_stats['qualified']:,} ({self.signal_stats['qualified']/self.signal_stats['total_checked']*100:.2f}%)")

        print(f"\nTRADE RESULTS:")
        print(f"  Trades: {total_trades} | Winners: {len(winners)} ({len(winners)/total_trades*100:.1f}%)")
        print(f"  Gross P&L: ${gross_pnl:+,.2f}")
        print(f"  Total Costs: ${total_costs:,.2f}")
        print(f"  Net P&L: ${net_pnl:+,.2f}")
        print(f"  Return: {(net_pnl/self.params['initial_capital']*100):+.2f}%")

        if total_trades > 0:
            print(f"\n  Avg Gross/Trade: ${gross_pnl/total_trades:+.2f}")
            print(f"  Avg Net/Trade: ${net_pnl/total_trades:+.2f}")

        print(f"\n{'-'*70}\nBY REGIME")
        for regime in ['trending_up', 'trending_down', 'choppy']:
            rt = trades_df[trades_df['regime'] == regime]
            if len(rt) > 0:
                rw = rt[rt['net_pnl'] > 0]
                print(f"{regime:13s}: {len(rt):3d} trades | WR: {len(rw)/len(rt)*100:4.1f}% | Net P&L: ${rt['net_pnl'].sum():+8,.2f}")

        print(f"\n{'-'*70}\nBY DIRECTION")
        for direction in ['long', 'short']:
            dt = trades_df[trades_df['direction'] == direction]
            if len(dt) > 0:
                dw = dt[dt['net_pnl'] > 0]
                print(f"{direction.upper():5s}: {len(dt):3d} trades | WR: {len(dw)/len(dt)*100:4.1f}% | Net P&L: ${dt['net_pnl'].sum():+8,.2f}")

        print("="*70 + "\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--start-date', required=True)
    parser.add_argument('--end-date', required=True)
    args = parser.parse_args()

    params = {
        'initial_capital': 50000, 'tick_size': 0.25, 'tick_value': 2.0,

        # Quality thresholds
        'wall_threshold': 200,
        'wall_distance': 12.5,
        'aggression_threshold': 150,
        'aggression_ratio': 2.0,
        'min_delta': 100,

        # T2 FILTERS
        'violence_threshold': 300,      # NEW: Block trades when abs(delta) > 300
        'stopout_lockout_seconds': 120, # NEW: 120-sec cooldown after stops

        # Costs
        'commission_per_rt': 0.70,
        'slippage_ticks': 1,

        # Targets
        'chop_profit_target': 10, 'chop_stop_loss': 5,
        'trend_profit_target': 20, 'trend_stop_loss': 8,

        'max_bars_in_position': 10  # 10 bars @ 30S = 5 minutes max hold
    }

    print("="*70)
    print("T2-ENHANCED STRATEGY - 30S TIMEFRAME")
    print("="*70)
    print(f"Timeframe: 5-second bars")
    print(f"Wall: {params['wall_threshold']} | Aggression: {params['aggression_threshold']} | Ratio: {params['aggression_ratio']}")
    print(f"T2 Filters:")
    print(f"  ✓ Violence Suppression: abs(delta) > {params['violence_threshold']} blocked")
    print(f"  ✓ Stopout Lockout: {params['stopout_lockout_seconds']}s cooldown after stops")
    print(f"  ✓ Wall Absorption: Wall must be 1.5x threshold to qualify")
    print(f"Costs: ${params['commission_per_rt']}/RT + {params['slippage_ticks']} tick slip")
    print("="*70 + "\n")

    bt = T2EnhancedBacktest(params)
    start = datetime.strptime(args.start_date, '%Y-%m-%d')
    end = datetime.strptime(args.end_date, '%Y-%m-%d')
    current = start
    while current <= end:
        bt.run_date(current.strftime('%Y-%m-%d'))
        current += timedelta(days=1)

    bt.generate_report()


if __name__ == '__main__':
    main()
