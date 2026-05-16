#!/usr/bin/env python3
"""
T2-Enhanced Wall Breakout Strategy
Correct Logic (per user feedback):
1. ENTRY: After price CLEARS/BREAKS through wall cluster with continuation aggression
2. STOP: At the broken wall cluster bottom (breakout failure level)
3. EXIT: Opposing wall absorption or aggression reversal

This is a BREAKOUT strategy, not wall rejection.
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
    def __init__(self, direction, entry_time, entry_price, entry_reason, regime,
                 entry_delta, entry_ratio, broken_wall_price):
        self.direction = direction
        self.entry_time = entry_time
        self.entry_price = entry_price
        self.entry_reason = entry_reason
        self.regime = regime
        self.entry_delta = entry_delta
        self.entry_ratio = entry_ratio
        self.broken_wall_price = broken_wall_price  # The wall we broke through
        self.exit_time = None
        self.exit_price = None
        self.exit_reason = None
        self.gross_pnl = 0
        self.costs = 0
        self.net_pnl = 0
        self.bars_held = 0


class T2WallBreakout:
    def __init__(self, params):
        self.params = params
        self.client = get_client()
        self.trades = []
        self.position = None
        self.equity = params['initial_capital']
        self.daily_opens = {}
        self.signal_stats = {
            'total_checked': 0,
            'no_walls': 0,
            'price_not_clear': 0,
            'weak_aggression': 0,
            'failed_violence': 0,
            'failed_lockout': 0,
            'qualified': 0
        }
        self.exit_stats = {
            'wall_absorption': 0,
            'aggression_reversal': 0,
            'stop_loss': 0,
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

    def get_regime_params(self, regime):
        if regime == 'trending_up':
            return {'allowed_direction': 'long'}
        elif regime == 'trending_down':
            return {'allowed_direction': 'short'}
        else:
            return {'allowed_direction': 'both'}

    def get_market_snapshot(self, timestamp, current_price):
        ts_str = str(timestamp)[:19]

        # Look for walls in wider range (we want to detect walls we just broke through)
        wall_query = f"""
            SELECT price,
                   sum(bid_liquidity_event_size) as bid_liq,
                   sum(ask_liquidity_event_size) as ask_liq
            FROM BM_MNQ_HEATMAP_LIQUIDITY_EVENTS_30S
            WHERE symbol = 'MNQZ5'
              AND toDateTime(ts_bucket) >= toDateTime('{ts_str}') - INTERVAL 60 SECOND
              AND toDateTime(ts_bucket) <= toDateTime('{ts_str}')
              AND price >= {current_price - self.params['wall_distance'] * 2}
              AND price <= {current_price + self.params['wall_distance'] * 2}
            GROUP BY price
            HAVING bid_liq >= {self.params['wall_threshold']} OR ask_liq >= {self.params['wall_threshold']}
            ORDER BY (bid_liq + ask_liq) DESC LIMIT 30
        """
        walls = self.client.query_df(wall_query)

        aggr_query = f"""
            SELECT sum(buy_exec_size) as buy_vol,
                   sum(sell_exec_size) as sell_vol,
                   sum(exec_delta) as delta
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

    def check_wall_absorption_exit(self, current_price, walls, aggression):
        """Exit when hit opposing wall showing absorption"""
        if not self.position or len(walls) == 0:
            return None

        buy_vol = aggression['buy_volume']
        sell_vol = aggression['sell_volume']
        delta = aggression['delta']
        total_vol = buy_vol + sell_vol

        if total_vol < self.params['aggression_threshold']:
            return None

        # For LONG: Look for ASK walls (resistance) with selling pressure
        if self.position.direction == 'long':
            ask_walls = walls[walls['ask_liq'] >= self.params['wall_threshold']].to_dict('records')
            for wall in ask_walls:
                wall_price = wall['price']
                wall_size = wall['ask_liq']

                if current_price >= wall_price - 1.0:
                    if sell_vol > buy_vol and wall_size >= self.params['wall_threshold'] * 1.5:
                        sell_ratio = sell_vol / buy_vol if buy_vol > 0 else 999
                        if sell_ratio >= 1.5 and delta < 0:
                            self.exit_stats['wall_absorption'] += 1
                            return 'wall_absorption', wall_price, f'ASK@{wall_price:.2f}[{wall_size:.0f}]'

        # For SHORT: Look for BID walls (support) with buying pressure
        else:
            bid_walls = walls[walls['bid_liq'] >= self.params['wall_threshold']].to_dict('records')
            for wall in bid_walls:
                wall_price = wall['price']
                wall_size = wall['bid_liq']

                if current_price <= wall_price + 1.0:
                    if buy_vol > sell_vol and wall_size >= self.params['wall_threshold'] * 1.5:
                        buy_ratio = buy_vol / sell_vol if sell_vol > 0 else 999
                        if buy_ratio >= 1.5 and delta > 0:
                            self.exit_stats['wall_absorption'] += 1
                            return 'wall_absorption', wall_price, f'BID@{wall_price:.2f}[{wall_size:.0f}]'

        return None

    def check_aggression_reversal_exit(self, aggression):
        """Exit when aggression reverses with similar strength to entry"""
        if not self.position:
            return None

        buy_vol = aggression['buy_volume']
        sell_vol = aggression['sell_volume']
        delta = aggression['delta']
        total_vol = buy_vol + sell_vol

        if total_vol < self.params['aggression_threshold']:
            return None

        # For LONG: Exit if selling pressure becomes as strong as entry buying
        if self.position.direction == 'long':
            current_sell_ratio = sell_vol / buy_vol if buy_vol > 0 else 999

            if current_sell_ratio >= self.position.entry_ratio * self.params['reversal_ratio_factor']:
                if delta < 0 and abs(delta) >= abs(self.position.entry_delta) * self.params['reversal_delta_factor']:
                    self.exit_stats['aggression_reversal'] += 1
                    return 'aggression_reversal', f'Δ{delta:.0f} (entry Δ{self.position.entry_delta:.0f})'

        # For SHORT: Exit if buying pressure becomes as strong as entry selling
        else:
            current_buy_ratio = buy_vol / sell_vol if sell_vol > 0 else 999

            if current_buy_ratio >= self.position.entry_ratio * self.params['reversal_ratio_factor']:
                if delta > 0 and abs(delta) >= abs(self.position.entry_delta) * self.params['reversal_delta_factor']:
                    self.exit_stats['aggression_reversal'] += 1
                    return 'aggression_reversal', f'Δ{delta:.0f} (entry Δ{self.position.entry_delta:.0f})'

        return None

    def check_stop_loss_exit(self, current_price):
        """
        NEW: Stop at the broken wall cluster
        If price reclaims the wall we broke, the breakout failed
        """
        if not self.position:
            return None

        # For LONG: Stop below the wall we broke through
        if self.position.direction == 'long':
            stop_price = self.position.broken_wall_price - self.params['stop_buffer']
            if current_price <= stop_price:
                self.exit_stats['stop_loss'] += 1
                return 'stop_loss', stop_price

        # For SHORT: Stop above the wall we broke through
        else:
            stop_price = self.position.broken_wall_price + self.params['stop_buffer']
            if current_price >= stop_price:
                self.exit_stats['stop_loss'] += 1
                return 'stop_loss', stop_price

        return None

    def check_exits(self, current_time, current_price, walls, aggression):
        """Check all exit conditions in priority order"""
        if not self.position:
            return None

        self.position.bars_held += 1

        # Priority 1: Wall absorption (take profit at natural resistance/support)
        wall_exit = self.check_wall_absorption_exit(current_price, walls, aggression)
        if wall_exit:
            exit_reason, exit_price, detail = wall_exit
            return exit_reason, exit_price, detail

        # Priority 2: Aggression reversal (cut when flow reverses)
        aggr_exit = self.check_aggression_reversal_exit(aggression)
        if aggr_exit:
            exit_reason, detail = aggr_exit
            return exit_reason, current_price, detail

        # Priority 3: Stop loss at broken wall (breakout failure)
        stop_exit = self.check_stop_loss_exit(current_price)
        if stop_exit:
            exit_reason, exit_price = stop_exit
            return exit_reason, exit_price, 'wall_reclaim'

        # Priority 4: Time exit
        if self.position.bars_held >= self.params['max_bars_in_position']:
            self.exit_stats['time_exit'] += 1
            return 'time_exit', current_price, None

        return None

    def evaluate_breakout_signal(self, walls, aggression, current_price, current_time, regime_params):
        """
        NEW LOGIC: Detect wall breakouts
        1. Find walls BELOW current price (for LONG) or ABOVE (for SHORT)
        2. Check if price has CLEARED the wall
        3. Confirm with strong continuation aggression
        4. Enter on breakout, use wall as stop
        """
        self.signal_stats['total_checked'] += 1

        if len(walls) == 0:
            self.signal_stats['no_walls'] += 1
            return None

        buy_vol = aggression['buy_volume']
        sell_vol = aggression['sell_volume']
        delta = aggression['delta']
        total_vol = buy_vol + sell_vol

        # Basic aggression filters
        if total_vol < self.params['aggression_threshold']:
            self.signal_stats['weak_aggression'] += 1
            return None
        if abs(delta) < self.params['min_delta']:
            self.signal_stats['weak_aggression'] += 1
            return None

        # T2 FILTER: Violence suppression
        if abs(delta) > self.params['violence_threshold']:
            self.signal_stats['failed_violence'] += 1
            return None

        # T2 FILTER: Stopout lockout
        if self.last_stop_time is not None:
            seconds_since_stop = (current_time - self.last_stop_time).total_seconds()
            if seconds_since_stop < self.params['stopout_lockout_seconds']:
                self.signal_stats['failed_lockout'] += 1
                return None

        allowed_direction = regime_params['allowed_direction']

        # LONG BREAKOUT: Look for resistance walls we just broke ABOVE
        if allowed_direction in ['long', 'both']:
            # Find ASK walls BELOW current price (resistance we broke through)
            ask_walls = walls[
                (walls['ask_liq'] >= self.params['wall_threshold']) &
                (walls['price'] < current_price) &
                (walls['price'] >= current_price - self.params['breakout_lookback'])
            ].to_dict('records')

            if len(ask_walls) > 0:
                # Sort by proximity to current price (most recent break)
                ask_walls = sorted(ask_walls, key=lambda x: abs(current_price - x['price']))
                wall = ask_walls[0]
                wall_price = wall['price']
                wall_size = wall['ask_liq']

                # Check if we've cleared the wall with enough distance
                clearance = current_price - wall_price
                if clearance >= self.params['min_clearance'] and clearance <= self.params['max_clearance']:
                    # Confirm with strong buying continuation
                    buy_ratio = buy_vol / sell_vol if sell_vol > 0 else 999
                    if buy_ratio >= self.params['aggression_ratio'] and delta > 0:
                        self.signal_stats['qualified'] += 1
                        return {
                            'direction': 'long',
                            'reason': f'BO_ASK@{wall_price:.2f}[{wall_size:.0f}]→{current_price:.2f}',
                            'broken_wall_price': wall_price,
                            'delta': delta,
                            'ratio': buy_ratio
                        }

        # SHORT BREAKOUT: Look for support walls we just broke BELOW
        if allowed_direction in ['short', 'both']:
            # Find BID walls ABOVE current price (support we broke through)
            bid_walls = walls[
                (walls['bid_liq'] >= self.params['wall_threshold']) &
                (walls['price'] > current_price) &
                (walls['price'] <= current_price + self.params['breakout_lookback'])
            ].to_dict('records')

            if len(bid_walls) > 0:
                # Sort by proximity to current price
                bid_walls = sorted(bid_walls, key=lambda x: abs(current_price - x['price']))
                wall = bid_walls[0]
                wall_price = wall['price']
                wall_size = wall['bid_liq']

                # Check if we've cleared the wall with enough distance
                clearance = wall_price - current_price
                if clearance >= self.params['min_clearance'] and clearance <= self.params['max_clearance']:
                    # Confirm with strong selling continuation
                    sell_ratio = sell_vol / buy_vol if buy_vol > 0 else 999
                    if sell_ratio >= self.params['aggression_ratio'] and delta < 0:
                        self.signal_stats['qualified'] += 1
                        return {
                            'direction': 'short',
                            'reason': f'BO_BID@{wall_price:.2f}[{wall_size:.0f}]→{current_price:.2f}',
                            'broken_wall_price': wall_price,
                            'delta': delta,
                            'ratio': sell_ratio
                        }

        self.signal_stats['price_not_clear'] += 1
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
        regime_counts = {'trending_up': 0, 'trending_down': 0, 'choppy': 0}
        day_start_equity = self.equity

        for idx, bar in bars.iterrows():
            timestamp = bar['ts_bucket']
            price = bar['price']

            regime = self.detect_regime_daily(date_str, price)
            regime_counts[regime] += 1
            regime_params = self.get_regime_params(regime)

            walls, aggression = self.get_market_snapshot(timestamp, price)

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

                    if exit_reason == 'stop_loss':
                        self.last_stop_time = timestamp

                    self.position = None
                continue

            # Check for breakout signals
            signal = self.evaluate_breakout_signal(walls, aggression, price, timestamp, regime_params)

            if signal:
                self.position = Trade(
                    direction=signal['direction'],
                    entry_time=timestamp,
                    entry_price=price,
                    entry_reason=signal['reason'],
                    regime=regime,
                    entry_delta=signal['delta'],
                    entry_ratio=signal['ratio'],
                    broken_wall_price=signal['broken_wall_price']
                )

        day_pnl = self.equity - day_start_equity
        day_trades = len([t for t in self.trades if t.entry_time.date() == pd.to_datetime(date_str).date()])
        total = sum(regime_counts.values())
        print(f"{date_str}: Trades={day_trades:3d} | P&L=${day_pnl:+7,.0f} | Regimes: UP={regime_counts['trending_up']/total*100:.0f}% DN={regime_counts['trending_down']/total*100:.0f}% CH={regime_counts['choppy']/total*100:.0f}%")

    def generate_report(self):
        if len(self.trades) == 0:
            print("\nNO TRADES")
            print(f"\nSignal Filter Stats:")
            for key, val in self.signal_stats.items():
                pct = (val / self.signal_stats['total_checked'] * 100) if self.signal_stats['total_checked'] > 0 else 0
                print(f"  {key}: {val:,} ({pct:.1f}%)")
            return

        trades_df = pd.DataFrame([{
            'date': t.entry_time.date(), 'direction': t.direction, 'regime': t.regime,
            'exit_reason': t.exit_reason, 'gross_pnl': t.gross_pnl, 'costs': t.costs, 'net_pnl': t.net_pnl
        } for t in self.trades])

        print("\n" + "="*70)
        print("T2-ENHANCED WALL BREAKOUT STRATEGY")
        print("="*70)

        total_trades = len(trades_df)
        winners = trades_df[trades_df['net_pnl'] > 0]
        gross_pnl = trades_df['gross_pnl'].sum()
        total_costs = trades_df['costs'].sum()
        net_pnl = trades_df['net_pnl'].sum()

        print(f"\nENTRY FILTER EFFECTIVENESS:")
        for key, val in self.signal_stats.items():
            pct = (val / self.signal_stats['total_checked'] * 100) if self.signal_stats['total_checked'] > 0 else 0
            print(f"  {key}: {val:,} ({pct:.1f}%)")

        print(f"\nEXIT BREAKDOWN:")
        print(f"  Wall absorption: {self.exit_stats['wall_absorption']} ({self.exit_stats['wall_absorption']/total_trades*100:.1f}%)")
        print(f"  Aggression reversal: {self.exit_stats['aggression_reversal']} ({self.exit_stats['aggression_reversal']/total_trades*100:.1f}%)")
        print(f"  Stop loss (wall reclaim): {self.exit_stats['stop_loss']} ({self.exit_stats['stop_loss']/total_trades*100:.1f}%)")
        print(f"  Time exit: {self.exit_stats['time_exit']} ({self.exit_stats['time_exit']/total_trades*100:.1f}%)")

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
        'wall_threshold': 200,
        'wall_distance': 12.5,

        # Breakout detection (NEW)
        'breakout_lookback': 5.0,     # Look for walls within 5 points we just broke
        'min_clearance': 1.0,          # Must be at least 1 point clear of wall
        'max_clearance': 8.0,          # But not too far (catch fresh breakouts)

        # Aggression confirmation
        'aggression_threshold': 150,
        'aggression_ratio': 2.0,
        'min_delta': 100,

        # T2 Filters
        'violence_threshold': 300,
        'stopout_lockout_seconds': 120,

        # Stop loss (NEW: wall-based)
        'stop_buffer': 1.0,  # Stop 1 point beyond broken wall

        # Dynamic exits
        'reversal_ratio_factor': 0.8,
        'reversal_delta_factor': 0.7,

        # Costs
        'commission_per_rt': 0.70,
        'slippage_ticks': 1,

        'max_bars_in_position': 10
    }

    print("="*70)
    print("T2-ENHANCED WALL BREAKOUT STRATEGY")
    print("="*70)
    print(f"Timeframe: 30-second bars")
    print(f"\nNEW LOGIC:")
    print(f"  ✓ ENTRY: After clearing wall cluster + continuation aggression")
    print(f"  ✓ STOP: At broken wall ± {params['stop_buffer']} pts (breakout failure)")
    print(f"  ✓ EXIT: Opposing wall absorption or aggression reversal")
    print(f"\nBreakout Detection:")
    print(f"  Wall threshold: {params['wall_threshold']}")
    print(f"  Clearance: {params['min_clearance']}-{params['max_clearance']} points")
    print(f"  Lookback: {params['breakout_lookback']} points")
    print("="*70 + "\n")

    bt = T2WallBreakout(params)
    start = datetime.strptime(args.start_date, '%Y-%m-%d')
    end = datetime.strptime(args.end_date, '%Y-%m-%d')
    current = start
    while current <= end:
        bt.run_date(current.strftime('%Y-%m-%d'))
        current += timedelta(days=1)

    bt.generate_report()


if __name__ == '__main__':
    main()
