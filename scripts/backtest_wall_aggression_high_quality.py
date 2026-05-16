#!/usr/bin/env python3
"""
HIGH QUALITY SIGNALS - Wall + Aggression Backtest
Tests with much higher thresholds to reduce overtrading:
- Wall threshold: 200 (vs 80) - top 15% of signals
- Aggression volume: 150 (vs 80) - stronger signals only
- Aggression ratio: 2.0 (vs 1.4) - stronger imbalance required
- Minimum delta: 100 - require significant directional pressure

Includes realistic costs:
- Commission: $0.70/RT
- Slippage: 1 tick average
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


class HighQualityBacktest:
    def __init__(self, params):
        self.params = params
        self.client = get_client()
        self.trades = []
        self.position = None
        self.equity = params['initial_capital']
        self.daily_opens = {}
        self.signal_stats = {'checked': 0, 'qualified': 0}

    def detect_regime_daily(self, date_str, current_price):
        """Simple daily trend detection: Compare current price to session open"""
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
        """Get strategy parameters based on regime"""
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
        else:  # choppy
            return {
                'profit_target': self.params['chop_profit_target'],
                'stop_loss': self.params['chop_stop_loss'],
                'allowed_direction': 'both'
            }

    def get_market_snapshot(self, timestamp, current_price):
        ts_str = str(timestamp)[:19]

        wall_query = f"""
            SELECT price, sum(bid_liquidity_event_size) as bid_liq, sum(ask_liquidity_event_size) as ask_liq
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
            WHERE symbol = 'MNQ'
              AND toDateTime(ts_bucket) >= toDateTime('{ts_str}') - INTERVAL 30 SECOND
              AND toDateTime(ts_bucket) <= toDateTime('{ts_str}')
              AND price >= {current_price - self.params['wall_distance']}
              AND price <= {current_price + self.params['wall_distance']}
            GROUP BY price
            HAVING bid_liq >= {self.params['wall_threshold']} OR ask_liq >= {self.params['wall_threshold']}
            ORDER BY (bid_liq + ask_liq) DESC LIMIT 20
        """
        walls = self.client.query_df(wall_query)

        aggr_query = f"""
            SELECT sum(buy_volume) as buy_vol, sum(sell_volume) as sell_vol, sum(delta) as delta
            FROM CG_mnq_aggression_100ms
            WHERE toDateTime(bucket_time) >= toDateTime('{ts_str}') - INTERVAL 5 SECOND
              AND toDateTime(bucket_time) <= toDateTime('{ts_str}')
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
        """Calculate commission + slippage"""
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

    def evaluate_signal(self, walls, aggression, current_price, regime_params):
        """HIGH QUALITY signal evaluation with stricter filters"""
        self.signal_stats['checked'] += 1

        if len(walls) == 0:
            return None

        buy_vol = aggression['buy_volume']
        sell_vol = aggression['sell_volume']
        delta = aggression['delta']

        total_vol = buy_vol + sell_vol

        # Filter 1: Minimum total volume (higher threshold)
        if total_vol < self.params['aggression_threshold']:
            return None

        # Filter 2: Minimum absolute delta (NEW - require significant directional pressure)
        if abs(delta) < self.params['min_delta']:
            return None

        ratio_threshold = self.params['aggression_ratio']
        allowed_direction = regime_params['allowed_direction']

        bid_walls = walls[walls['bid_liq'] >= self.params['wall_threshold']].to_dict('records')
        ask_walls = walls[walls['ask_liq'] >= self.params['wall_threshold']].to_dict('records')

        # LONG signals: Strong bid wall + strong buying pressure
        if allowed_direction in ['long', 'both']:
            for wall in bid_walls:
                wall_price = wall['price']
                wall_size = wall['bid_liq']

                if abs(current_price - wall_price) <= self.params['wall_distance']:
                    buy_ratio = buy_vol / sell_vol if sell_vol > 0 else 999

                    # Require strong ratio AND positive delta AND significant wall size
                    if buy_ratio >= ratio_threshold and delta > 0 and wall_size >= self.params['wall_threshold']:
                        self.signal_stats['qualified'] += 1
                        return {'direction': 'long', 'reason': f'BID@{wall_price:.2f}[{wall_size:.0f}]Δ{delta:.0f}'}

        # SHORT signals: Strong ask wall + strong selling pressure
        if allowed_direction in ['short', 'both']:
            for wall in ask_walls:
                wall_price = wall['price']
                wall_size = wall['ask_liq']

                if abs(current_price - wall_price) <= self.params['wall_distance']:
                    sell_ratio = sell_vol / buy_vol if buy_vol > 0 else 999

                    # Require strong ratio AND negative delta AND significant wall size
                    if sell_ratio >= ratio_threshold and delta < 0 and wall_size >= self.params['wall_threshold']:
                        self.signal_stats['qualified'] += 1
                        return {'direction': 'short', 'reason': f'ASK@{wall_price:.2f}[{wall_size:.0f}]Δ{delta:.0f}'}

        return None

    def run_date(self, date_str):
        bars_query = f"""
            SELECT ts_bucket, argMax(price, total_event_count) as price
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_1S
            WHERE trade_date = '{date_str}' AND symbol = 'MNQ' AND total_event_count > 0
            GROUP BY ts_bucket HAVING sum(total_event_count) > 5 ORDER BY ts_bucket
        """
        bars = self.client.query_df(bars_query)

        if len(bars) == 0:
            return

        # Set session open
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

                    # Calculate gross P&L
                    if self.position.direction == 'long':
                        gross_pnl = (exit_price - self.position.entry_price) / self.params['tick_size'] * self.params['tick_value']
                    else:
                        gross_pnl = (self.position.entry_price - exit_price) / self.params['tick_size'] * self.params['tick_value']

                    # Apply costs
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
                    self.position = None
                continue

            # Only check signals every 5 bars (reduce computation)
            if idx % 5 != 0:
                continue

            walls, aggression = self.get_market_snapshot(timestamp, price)
            signal = self.evaluate_signal(walls, aggression, price, regime_params)

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
            return

        trades_df = pd.DataFrame([{
            'date': t.entry_time.date(), 'direction': t.direction, 'regime': t.regime,
            'gross_pnl': t.gross_pnl, 'costs': t.costs, 'net_pnl': t.net_pnl
        } for t in self.trades])

        print("\n" + "="*70)
        print("HIGH QUALITY SIGNALS - WITH COMMISSION & SLIPPAGE")
        print("="*70)

        total_trades = len(trades_df)
        winners = trades_df[trades_df['net_pnl'] > 0]
        gross_pnl = trades_df['gross_pnl'].sum()
        total_costs = trades_df['costs'].sum()
        net_pnl = trades_df['net_pnl'].sum()

        # Signal filtering stats
        signal_qualify_rate = (self.signal_stats['qualified'] / self.signal_stats['checked'] * 100) if self.signal_stats['checked'] > 0 else 0

        print(f"\nSignal Quality: {self.signal_stats['qualified']} qualified / {self.signal_stats['checked']} checked ({signal_qualify_rate:.2f}%)")
        print(f"Trades: {total_trades} | Winners: {len(winners)} ({len(winners)/total_trades*100:.1f}%)")
        print(f"Gross P&L: ${gross_pnl:+,.2f}")
        print(f"Total Costs: ${total_costs:,.2f} (Comm: ${self.params['commission_per_rt']*total_trades:.2f} + Slip: ${self.params['slippage_ticks']*self.params['tick_value']*total_trades:.2f})")
        print(f"Net P&L: ${net_pnl:+,.2f}")
        print(f"Return: {(net_pnl/self.params['initial_capital']*100):.2f}%")

        if total_trades > 0:
            print(f"\nAvg Gross P&L/Trade: ${gross_pnl/total_trades:.2f}")
            print(f"Avg Net P&L/Trade: ${net_pnl/total_trades:.2f}")

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

        # HIGH QUALITY THRESHOLDS
        'wall_threshold': 200,      # Was 80 - top 15% of signals
        'wall_distance': 12.5,
        'aggression_threshold': 150, # Was 80 - stronger volume required
        'aggression_ratio': 2.0,     # Was 1.4 - stronger imbalance
        'min_delta': 100,            # NEW - minimum directional pressure

        # Costs
        'commission_per_rt': 0.70,
        'slippage_ticks': 1,

        # Choppy regime
        'chop_profit_target': 10, 'chop_stop_loss': 5,

        # Trending regime
        'trend_profit_target': 20, 'trend_stop_loss': 8,

        'max_bars_in_position': 300
    }

    print("="*70)
    print("HIGH QUALITY SIGNALS: Wall + Aggression with Realistic Costs")
    print("="*70)
    print(f"Wall Threshold: {params['wall_threshold']} (was 80)")
    print(f"Aggression Volume: {params['aggression_threshold']} (was 80)")
    print(f"Aggression Ratio: {params['aggression_ratio']} (was 1.4)")
    print(f"Minimum Delta: {params['min_delta']} (NEW)")
    print(f"Commission: ${params['commission_per_rt']}/RT | Slippage: {params['slippage_ticks']} tick")
    print(f"Choppy: PT/SL={params['chop_profit_target']}/{params['chop_stop_loss']} | Trend: PT/SL={params['trend_profit_target']}/{params['trend_stop_loss']}")
    print("="*70 + "\n")

    bt = HighQualityBacktest(params)
    start = datetime.strptime(args.start_date, '%Y-%m-%d')
    end = datetime.strptime(args.end_date, '%Y-%m-%d')
    current = start
    while current <= end:
        bt.run_date(current.strftime('%Y-%m-%d'))
        current += timedelta(days=1)

    bt.generate_report()


if __name__ == '__main__':
    main()
