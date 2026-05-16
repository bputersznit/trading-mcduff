"""
CG Wall Top 5 Patterns Backtest - McDuff Phase 4 Validated Patterns

Trades ONLY the 5 highest-expectancy patterns validated in Phase 4:

Pattern 1: ASK CONTINUED_SELL + INSIDE_OR + BELOW_VWAP + HIGH_VOL + RTH_OPEN
           → SHORT (reject) | 16.41 tick expectancy | 230 setups

Pattern 2: ASK BUY_TO_SELL_FLIP + BELOW_OR_LOW + BELOW_VWAP + HIGH_VOL + PM_DRIFT
           → SHORT (reject) | 12.44 tick expectancy | 666 setups

Pattern 3: ASK CONTINUED_SELL + BELOW_OR_LOW + BELOW_VWAP + NORMAL_VOL + PM_DRIFT
           → SHORT (reject) | 11.01 tick expectancy | 218 setups

Pattern 4: ASK BUY_TO_SELL_FLIP + INSIDE_OR + BELOW_VWAP + HIGH_VOL + MIDDAY
           → SHORT (reject) | 10.89 tick expectancy | 212 setups

Pattern 5: BID SELL_TO_BUY_FLIP + BELOW_OR_LOW + BELOW_VWAP + NORMAL_VOL + MIDDAY
           → LONG (reject) | 10.76 tick expectancy | 211 setups

Safety Controls:
- One MNQ contract max
- One trade per interaction_id
- 30-second cooldown after every exit
- 2 tick slippage + $0.70 commission per round trip

Date: 2026-05-04 23:30 ET
Source: Phase 4 Regime Layer validation (29 PRIMARY candidates)
"""

from dataclasses import dataclass
from datetime import timedelta
import pandas as pd
import clickhouse_connect
import os
from dotenv import load_dotenv

# Load ClickHouse credentials from .env
load_dotenv()


TICK_SIZE = 0.25
POINT_VALUE = 2.0  # MNQ = $2 per point
SLIPPAGE_TICKS_SIDE = 2
COMMISSION_ROUND_TURN = 0.70


@dataclass
class Trade:
    interaction_id: int
    entry_time: pd.Timestamp
    exit_time: pd.Timestamp
    side: str
    pattern_id: int
    pattern_name: str
    entry_price: float
    exit_price: float
    stop_price: float
    target_price: float
    exit_reason: str
    gross_ticks: float
    net_ticks: float
    net_usd: float


def choose_signal(row) -> tuple[str, int, str] | None:
    """
    Match ONLY the Top 5 validated patterns.

    Returns: (side, pattern_id, pattern_name) or None
    """

    wall_side = row.get("wall_side")
    delta_flip = row.get("delta_flip_pattern")
    orb_position = row.get("orb_position")
    vwap_relation = row.get("vwap_relation")
    atr_regime = row.get("atr_regime")
    time_bucket = row.get("time_bucket")

    # All patterns require BELOW_VWAP (100% of validated edge)
    if vwap_relation != "BELOW_VWAP":
        return None

    # Pattern 1: ASK CONTINUED_SELL + INSIDE_OR + BELOW_VWAP + HIGH_VOL + RTH_OPEN
    if (wall_side == "ASK" and
        delta_flip == "CONTINUED_SELL" and
        orb_position == "INSIDE_OR" and
        atr_regime == "HIGH_VOL" and
        time_bucket == "RTH_OPEN"):
        return ("SHORT", 1, "P1_ASK_CONT_SELL_INSIDE_HVOL_OPEN")

    # Pattern 2: ASK BUY_TO_SELL_FLIP + BELOW_OR_LOW + BELOW_VWAP + HIGH_VOL + PM_DRIFT
    if (wall_side == "ASK" and
        delta_flip == "BUY_TO_SELL_FLIP" and
        orb_position == "BELOW_OR_LOW" and
        atr_regime == "HIGH_VOL" and
        time_bucket == "PM_DRIFT"):
        return ("SHORT", 2, "P2_ASK_FLIP_BELOW_HVOL_PM")

    # Pattern 3: ASK CONTINUED_SELL + BELOW_OR_LOW + BELOW_VWAP + NORMAL_VOL + PM_DRIFT
    if (wall_side == "ASK" and
        delta_flip == "CONTINUED_SELL" and
        orb_position == "BELOW_OR_LOW" and
        atr_regime == "NORMAL_VOL" and
        time_bucket == "PM_DRIFT"):
        return ("SHORT", 3, "P3_ASK_CONT_SELL_BELOW_NVOL_PM")

    # Pattern 4: ASK BUY_TO_SELL_FLIP + INSIDE_OR + BELOW_VWAP + HIGH_VOL + MIDDAY
    if (wall_side == "ASK" and
        delta_flip == "BUY_TO_SELL_FLIP" and
        orb_position == "INSIDE_OR" and
        atr_regime == "HIGH_VOL" and
        time_bucket == "MIDDAY"):
        return ("SHORT", 4, "P4_ASK_FLIP_INSIDE_HVOL_MIDDAY")

    # Pattern 5: BID SELL_TO_BUY_FLIP + BELOW_OR_LOW + BELOW_VWAP + NORMAL_VOL + MIDDAY
    if (wall_side == "BID" and
        delta_flip == "SELL_TO_BUY_FLIP" and
        orb_position == "BELOW_OR_LOW" and
        atr_regime == "NORMAL_VOL" and
        time_bucket == "MIDDAY"):
        return ("LONG", 5, "P5_BID_FLIP_BELOW_NVOL_MIDDAY")

    return None


def simulate_trade(row, ticks_df, side: str, pattern_id: int, pattern_name: str) -> Trade | None:
    """
    Walk forward tick-by-tick after signal.
    Uses 8-tick stop (matches Phase 4 expectancy calculation).
    Uses 2:1 R/R (16-tick target).
    """

    interaction_id = int(row["interaction_id"])
    entry_time = pd.Timestamp(row["wall_time"])

    future_ticks = ticks_df[ticks_df["ts_event"] > entry_time].head(5000)
    if future_ticks.empty:
        return None

    raw_entry_price = float(future_ticks.iloc[0]["price"])

    if side == "LONG":
        entry_price = raw_entry_price + SLIPPAGE_TICKS_SIDE * TICK_SIZE
        stop_price = entry_price - 8 * TICK_SIZE  # 8-tick stop (matches Phase 4)
        target_price = entry_price + 16 * TICK_SIZE  # 2:1 R/R
    else:
        entry_price = raw_entry_price - SLIPPAGE_TICKS_SIDE * TICK_SIZE
        stop_price = entry_price + 8 * TICK_SIZE
        target_price = entry_price - 16 * TICK_SIZE

    max_hold_time = entry_time + timedelta(seconds=120)

    for _, tick in future_ticks.iterrows():
        tick_time = pd.Timestamp(tick["ts_event"])
        price = float(tick["price"])

        if side == "LONG":
            if price <= stop_price:
                exit_price = stop_price - SLIPPAGE_TICKS_SIDE * TICK_SIZE
                exit_reason = "STOP"
                break
            if price >= target_price:
                exit_price = target_price - SLIPPAGE_TICKS_SIDE * TICK_SIZE
                exit_reason = "TARGET"
                break
        else:
            if price >= stop_price:
                exit_price = stop_price + SLIPPAGE_TICKS_SIDE * TICK_SIZE
                exit_reason = "STOP"
                break
            if price <= target_price:
                exit_price = target_price + SLIPPAGE_TICKS_SIDE * TICK_SIZE
                exit_reason = "TARGET"
                break

        if tick_time >= max_hold_time:
            exit_price = price
            exit_reason = "TIMEOUT"
            break
    else:
        last_tick = future_ticks.iloc[-1]
        tick_time = pd.Timestamp(last_tick["ts_event"])
        exit_price = float(last_tick["price"])
        exit_reason = "DATA_END"

    if side == "LONG":
        gross_ticks = (exit_price - entry_price) / TICK_SIZE
    else:
        gross_ticks = (entry_price - exit_price) / TICK_SIZE

    commission_ticks = COMMISSION_ROUND_TURN / (POINT_VALUE * TICK_SIZE)
    net_ticks = gross_ticks - SLIPPAGE_TICKS_SIDE - commission_ticks  # Total slippage both sides
    net_usd = net_ticks * TICK_SIZE * POINT_VALUE

    return Trade(
        interaction_id=interaction_id,
        entry_time=entry_time,
        exit_time=tick_time,
        side=side,
        pattern_id=pattern_id,
        pattern_name=pattern_name,
        entry_price=entry_price,
        exit_price=exit_price,
        stop_price=stop_price,
        target_price=target_price,
        exit_reason=exit_reason,
        gross_ticks=gross_ticks,
        net_ticks=net_ticks,
        net_usd=net_usd,
    )


def run_backtest(signals_df, ticks_df) -> pd.DataFrame:
    """
    Critical controls:
    - ONE open position at a time (memory safety from 125-short disaster)
    - ONE trade per interaction_id
    - 30-second cooldown after exit
    """

    trades: list[Trade] = []
    traded_interactions: set[int] = set()

    flat_after = pd.Timestamp.min
    cooldown_seconds = 30

    signals_df = signals_df.sort_values("wall_time").copy()
    ticks_df = ticks_df.sort_values("ts_event").copy()

    for _, row in signals_df.iterrows():
        interaction_id = int(row["interaction_id"])
        signal_time = pd.Timestamp(row["wall_time"])

        # Enforce one trade per interaction
        if interaction_id in traded_interactions:
            continue

        # Enforce cooldown
        if signal_time < flat_after:
            continue

        signal_result = choose_signal(row)
        if signal_result is None:
            continue

        side, pattern_id, pattern_name = signal_result

        trade = simulate_trade(row, ticks_df, side, pattern_id, pattern_name)
        if trade is None:
            continue

        trades.append(trade)
        traded_interactions.add(interaction_id)
        flat_after = trade.exit_time + timedelta(seconds=cooldown_seconds)

    return pd.DataFrame([t.__dict__ for t in trades])


def load_signals_from_clickhouse():
    """Load regime-filtered wall interactions from Phase 4 table"""
    client = clickhouse_connect.get_client(
        host=os.getenv('CLICKHOUSE_HOST', 'localhost'),
        port=int(os.getenv('CLICKHOUSE_PORT', '8123')),
        username=os.getenv('CLICKHOUSE_USER', 'default'),
        password=os.getenv('CLICKHOUSE_PASSWORD', ''),
        database=os.getenv('CLICKHOUSE_DATABASE', 'default')
    )

    query = """
    SELECT
        trade_date,
        wall_time,
        wall_side,
        wall_price,
        wall_score,

        outcome_label_30s,
        outcome_move_ticks_30s,

        delta_flip_pattern,
        wall_aggression_pattern,

        orb_position,
        vwap_relation,
        atr_regime,
        time_bucket,
        session_extreme_location,

        distance_from_vwap,
        distance_from_orb_low,
        distance_from_orb_high,

        -- Generate pseudo interaction_id from wall_time for tracking
        toUnixTimestamp(wall_time) AS interaction_id
    FROM CG_mnq_wall_outcomes_regime_v1
    WHERE time_bucket != 'OUTSIDE_RTH'
    ORDER BY wall_time
    """

    result = client.query(query)
    columns = [
        'trade_date', 'wall_time', 'wall_side', 'wall_price', 'wall_score',
        'outcome_label_30s', 'outcome_move_ticks_30s',
        'delta_flip_pattern', 'wall_aggression_pattern',
        'orb_position', 'vwap_relation', 'atr_regime', 'time_bucket',
        'session_extreme_location', 'distance_from_vwap',
        'distance_from_orb_low', 'distance_from_orb_high', 'interaction_id'
    ]

    return pd.DataFrame(result.result_rows, columns=columns)


def load_ticks_from_clickhouse(start_date, end_date):
    """Load tick data from ClickHouse"""
    client = clickhouse_connect.get_client(
        host=os.getenv('CLICKHOUSE_HOST', 'localhost'),
        port=int(os.getenv('CLICKHOUSE_PORT', '8123')),
        username=os.getenv('CLICKHOUSE_USER', 'default'),
        password=os.getenv('CLICKHOUSE_PASSWORD', ''),
        database=os.getenv('CLICKHOUSE_DATABASE', 'default')
    )

    query = f"""
    SELECT
        ts_event,
        price
    FROM mnq_trades
    WHERE toDate(ts_event) BETWEEN '{start_date}' AND '{end_date}'
    ORDER BY ts_event
    """

    result = client.query(query)
    return pd.DataFrame(result.result_rows, columns=['ts_event', 'price'])


def analyze_results(trades_df):
    """Comprehensive backtest analysis"""

    print(f"\n{'='*80}")
    print(f"PHASE 4 TOP 5 PATTERNS - BACKTEST RESULTS")
    print(f"{'='*80}")

    print(f"\nTrade Execution:")
    print(f"  Total trades: {len(trades_df)}")

    if len(trades_df) == 0:
        print("\n⚠️  No trades executed - check pattern matching logic")
        return

    # Overall performance
    total_pnl = trades_df['net_usd'].sum()
    total_ticks = trades_df['net_ticks'].sum()
    avg_ticks = trades_df['net_ticks'].mean()
    win_rate = (trades_df['net_ticks'] > 0).mean() * 100
    winners = (trades_df['net_ticks'] > 0).sum()
    losers = (trades_df['net_ticks'] <= 0).sum()

    print(f"\nOverall Performance:")
    print(f"  Net P&L: ${total_pnl:,.2f}")
    print(f"  Net ticks: {total_ticks:.2f}")
    print(f"  Avg per trade: {avg_ticks:.2f} ticks")
    print(f"  Win rate: {win_rate:.2f}%")
    print(f"  Winners: {winners}")
    print(f"  Losers: {losers}")

    # Pattern breakdown
    print(f"\nPattern Breakdown:")
    pattern_stats = trades_df.groupby('pattern_name').agg({
        'net_ticks': ['count', 'sum', 'mean'],
        'net_usd': 'sum'
    }).round(2)

    # Calculate win rate per pattern
    win_rates = trades_df.groupby('pattern_name').apply(
        lambda x: (x['net_ticks'] > 0).mean() * 100
    ).round(2)

    pattern_stats['win_rate_pct'] = win_rates

    print(pattern_stats.to_string())

    # Exit reason distribution
    print(f"\nExit Reasons:")
    exit_counts = trades_df['exit_reason'].value_counts()
    for reason, count in exit_counts.items():
        pct = count / len(trades_df) * 100
        print(f"  {reason}: {count} ({pct:.1f}%)")

    # Side distribution
    print(f"\nSide Distribution:")
    side_stats = trades_df.groupby('side').agg({
        'net_ticks': ['count', 'sum', 'mean']
    }).round(2)
    side_win_rates = trades_df.groupby('side').apply(
        lambda x: (x['net_ticks'] > 0).mean() * 100
    ).round(2)
    side_stats['win_rate_pct'] = side_win_rates
    print(side_stats.to_string())

    # Risk metrics
    if winners > 0 and losers > 0:
        avg_win = trades_df[trades_df['net_ticks'] > 0]['net_ticks'].mean()
        avg_loss = trades_df[trades_df['net_ticks'] <= 0]['net_ticks'].mean()
        profit_factor = abs(avg_win * winners / (avg_loss * losers))

        print(f"\nRisk Metrics:")
        print(f"  Avg win: {avg_win:.2f} ticks")
        print(f"  Avg loss: {avg_loss:.2f} ticks")
        print(f"  Profit factor: {profit_factor:.2f}")
        print(f"  Max drawdown: {trades_df['net_ticks'].cumsum().min():.2f} ticks")

    print(f"\n{'='*80}")


if __name__ == "__main__":
    print("="*80)
    print("McDuff Phase 4 - Top 5 Patterns Backtest")
    print("="*80)

    print("\nLoading regime-filtered signals from ClickHouse...")
    signals = load_signals_from_clickhouse()
    print(f"Loaded {len(signals):,} regime interactions")
    print(f"Date range: {signals['wall_time'].min()} to {signals['wall_time'].max()}")

    # Show regime distribution
    print(f"\nRegime distribution in signals:")
    print(f"  Time buckets: {signals['time_bucket'].value_counts().to_dict()}")
    print(f"  VWAP relation: {signals['vwap_relation'].value_counts().to_dict()}")
    print(f"  ORB position: {signals['orb_position'].value_counts().to_dict()}")
    print(f"  ATR regime: {signals['atr_regime'].value_counts().to_dict()}")

    print("\nLoading tick data...")
    start_date = signals['trade_date'].min().strftime('%Y-%m-%d')
    end_date = signals['trade_date'].max().strftime('%Y-%m-%d')
    ticks = load_ticks_from_clickhouse(start_date, end_date)
    print(f"Loaded {len(ticks):,} ticks")

    print("\nRunning backtest with Top 5 patterns...")
    print("  Pattern 1: ASK CONT_SELL + INSIDE_OR + BELOW_VWAP + HIGH_VOL + RTH_OPEN")
    print("  Pattern 2: ASK FLIP + BELOW_OR_LOW + BELOW_VWAP + HIGH_VOL + PM_DRIFT")
    print("  Pattern 3: ASK CONT_SELL + BELOW_OR_LOW + BELOW_VWAP + NORMAL_VOL + PM_DRIFT")
    print("  Pattern 4: ASK FLIP + INSIDE_OR + BELOW_VWAP + HIGH_VOL + MIDDAY")
    print("  Pattern 5: BID FLIP + BELOW_OR_LOW + BELOW_VWAP + NORMAL_VOL + MIDDAY")

    trades_df = run_backtest(signals, ticks)

    if len(trades_df) > 0:
        analyze_results(trades_df)

        # Save results
        output_file = 'CG_mnq_top5_patterns_backtest_results.csv'
        trades_df.to_csv(output_file, index=False)
        print(f"\nResults saved to: {output_file}")
    else:
        print("\n⚠️  No trades executed")
        print("\nDebugging info:")
        print(f"  Total signals checked: {len(signals)}")
        print(f"  Signals BELOW_VWAP: {(signals['vwap_relation'] == 'BELOW_VWAP').sum()}")
        print(f"  Delta flip patterns: {signals['delta_flip_pattern'].value_counts().to_dict()}")
