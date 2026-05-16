"""
CG Wall Interaction Backtest v1 - McDuff Framework

Strategy/methodology:
- Trade only deduplicated interaction episodes
- Enforce one MNQ contract max
- Enforce one trade per interaction_id
- Enforce cooldown after every exit
- Use outcome table only for research labels, not live lookahead
- Entry is based on wall behavior + aggression pattern available at signal time

Date: 2026-05-04
Author: McDuff framework + implementation
"""

from dataclasses import dataclass
from datetime import timedelta
import pandas as pd
import click house_driver


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
    entry_price: float
    exit_price: float
    stop_price: float
    target_price: float
    exit_reason: str
    gross_ticks: float
    net_ticks: float
    net_usd: float


def choose_signal(row) -> str | None:
    """
    Signal logic placeholder.
    Replace with validated patterns only.

    Do NOT trade all wall interactions.
    Trade only narrow, validated combinations.
    """

    wall_behavior = row.get("wall_behavior")
    wall_side = row.get("wall_side")
    # delta_flip = row.get("delta_flip_pattern", None)  # TODO: add to dedup table

    # Example: pulled ask = breakout long
    if wall_behavior == "PULLED_WALL" and wall_side == "ASK":
        return "LONG"

    # Example: pulled bid = breakdown short
    if wall_behavior == "PULLED_WALL" and wall_side == "BID":
        return "SHORT"

    # Example: iceberg ask = fade short
    if wall_behavior in ("ICEBERG_LIKE_WALL", "REPLENISHING_WALL") and wall_side == "ASK":
        return "SHORT"

    # Example: iceberg bid = fade long
    if wall_behavior in ("ICEBERG_LIKE_WALL", "REPLENISHING_WALL") and wall_side == "BID":
        return "LONG"

    return None


def simulate_trade(row, ticks_df, side: str) -> Trade | None:
    """
    Walk forward tick-by-tick after signal.
    No overlapping trades.
    No lookahead entry.
    """

    interaction_id = int(row["interaction_id"])
    entry_time = pd.Timestamp(row["first_touch_time"])

    future_ticks = ticks_df[ticks_df["ts_event"] > entry_time].head(5000)
    if future_ticks.empty:
        return None

    raw_entry_price = float(future_ticks.iloc[0]["price"])

    if side == "LONG":
        entry_price = raw_entry_price + SLIPPAGE_TICKS_SIDE * TICK_SIZE
        stop_price = entry_price - 20 * TICK_SIZE
        target_price = entry_price + 40 * TICK_SIZE
    else:
        entry_price = raw_entry_price - SLIPPAGE_TICKS_SIDE * TICK_SIZE
        stop_price = entry_price + 20 * TICK_SIZE
        target_price = entry_price - 40 * TICK_SIZE

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
    net_ticks = gross_ticks - commission_ticks
    net_usd = net_ticks * TICK_SIZE * POINT_VALUE

    return Trade(
        interaction_id=interaction_id,
        entry_time=entry_time,
        exit_time=tick_time,
        side=side,
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
    - one open position at a time
    - one trade per interaction_id
    - cooldown after exit
    """

    trades: list[Trade] = []
    traded_interactions: set[int] = set()

    flat_after = pd.Timestamp.min
    cooldown_seconds = 30

    signals_df = signals_df.sort_values("first_touch_time").copy()
    ticks_df = ticks_df.sort_values("ts_event").copy()

    for _, row in signals_df.iterrows():
        interaction_id = int(row["interaction_id"])
        signal_time = pd.Timestamp(row["first_touch_time"])

        # Enforce one trade per interaction
        if interaction_id in traded_interactions:
            continue

        # Enforce cooldown
        if signal_time < flat_after:
            continue

        side = choose_signal(row)
        if side is None:
            continue

        trade = simulate_trade(row, ticks_df, side)
        if trade is None:
            continue

        trades.append(trade)
        traded_interactions.add(interaction_id)
        flat_after = trade.exit_time + timedelta(seconds=cooldown_seconds)

    return pd.DataFrame([t.__dict__ for t in trades])


def load_signals_from_clickhouse():
    """Load deduped wall interactions from ClickHouse"""
    client = clickhouse_driver.Client(host='localhost')

    query = """
    SELECT
        interaction_id,
        trade_date,
        wall_id,
        episode_num,
        first_touch_time,
        wall_side,
        wall_price,
        wall_size,
        wall_score,
        wall_rank,
        wall_type,
        wall_behavior,
        pull_ratio,
        fill_ratio,
        replenish_ratio,
        price_at_interaction,
        distance_to_wall_ticks
    FROM CG_mnq_wall_interactions_dedup_v1
    WHERE wall_behavior != ''  -- Only meaningful wall behaviors
    ORDER BY first_touch_time
    """

    rows = client.execute(query)
    columns = [
        'interaction_id', 'trade_date', 'wall_id', 'episode_num',
        'first_touch_time', 'wall_side', 'wall_price', 'wall_size',
        'wall_score', 'wall_rank', 'wall_type', 'wall_behavior',
        'pull_ratio', 'fill_ratio', 'replenish_ratio',
        'price_at_interaction', 'distance_to_wall_ticks'
    ]

    return pd.DataFrame(rows, columns=columns)


def load_ticks_from_clickhouse(start_date, end_date):
    """Load tick data from ClickHouse"""
    client = clickhouse_driver.Client(host='localhost')

    query = f"""
    SELECT
        ts_event,
        price
    FROM mnq_trades
    WHERE toDate(ts_event) BETWEEN '{start_date}' AND '{end_date}'
    ORDER BY ts_event
    """

    rows = client.execute(query)
    return pd.DataFrame(rows, columns=['ts_event', 'price'])


if __name__ == "__main__":
    print("Loading signals from ClickHouse...")
    signals = load_signals_from_clickhouse()
    print(f"Loaded {len(signals)} wall interaction signals")
    print(f"Date range: {signals['first_touch_time'].min()} to {signals['first_touch_time'].max()}")

    print("\nLoading tick data...")
    ticks = load_ticks_from_clickhouse('2025-09-23', '2025-09-24')
    print(f"Loaded {len(ticks)} ticks")

    print("\nRunning backtest...")
    trades_df = run_backtest(signals, ticks)

    print(f"\n=== BACKTEST RESULTS ===")
    print(f"Total signals: {len(signals)}")
    print(f"Trades executed: {len(trades_df)}")
    print(f"Trade rate: {len(trades_df) / len(signals) * 100:.2f}%")

    if len(trades_df) > 0:
        print(f"\nGross P&L: ${trades_df['net_usd'].sum():.2f}")
        print(f"Net ticks: {trades_df['net_ticks'].sum():.2f}")
        print(f"Avg per trade: {trades_df['net_ticks'].mean():.2f} ticks")
        print(f"Win rate: {(trades_df['net_ticks'] > 0).mean() * 100:.2f}%")
        print(f"Winners: {(trades_df['net_ticks'] > 0).sum()}")
        print(f"Losers: {(trades_df['net_ticks'] <= 0).sum()}")

        print(f"\nExit reasons:")
        print(trades_df['exit_reason'].value_counts())

        # Save results
        trades_df.to_csv('wall_interaction_backtest_results.csv', index=False)
        print(f"\nResults saved to: wall_interaction_backtest_results.csv")
    else:
        print("\nNo trades executed - check signal logic")
