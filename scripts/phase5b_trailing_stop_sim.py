#!/usr/bin/env python3
"""
Phase 5B Trailing Stop Simulator
McDuff MNQ Intraday Strategy

Purpose:
    Authoritative path-dependent simulation for situational trailing stops.

Strategy methodology:
    - Use Phase 5 pullback/confirmation entries.
    - Hold up to 10 minutes.
    - Use broker-style initial protective stop.
    - Activate trailing only after sufficient MFE.
    - Trail using profit locks + local swing structure.
    - Never loosen stop.
    - Charge realistic MNQ costs.

Why Python:
    ClickHouse aggregate screening cannot reliably determine stop-hit order
    for adaptive trailing stops. This simulator iterates the tick path in time order.
"""

from __future__ import annotations

import os
import math
from dataclasses import dataclass, asdict
from datetime import timedelta
from typing import Optional, Iterable

import pandas as pd
import clickhouse_connect


# ============================================================
# Config
# ============================================================

MNQ_TICK_SIZE = 0.25
MNQ_TICK_VALUE = 0.50

# Round-turn cost approximation from prior Phase 5 analysis:
# 4 ticks slippage + 1.4 ticks commission = 5.4 ticks.
DEFAULT_COST_TICKS = 5.4

MAX_HOLD_SECONDS = 600

# Keep this modest while testing.
MAX_TRADES = 5000


@dataclass(frozen=True)
class TrailParams:
    name: str
    initial_stop_ticks: int
    activation_ticks: int
    structure_lookback_seconds: int
    structure_buffer_ticks: int
    lock1_mfe_ticks: int
    lock1_profit_ticks: int
    lock2_mfe_ticks: int
    lock2_profit_ticks: int
    hard_timeout_seconds: int = MAX_HOLD_SECONDS


@dataclass
class SimResult:
    trade_date: str
    wall_time: str
    trigger_model: str
    trade_side: str
    setup_family: str
    wall_side: str
    delta_flip_pattern: str
    orb_position: str
    vwap_relation: str
    atr_regime: str
    time_bucket: str
    session_extreme_location: str

    param_name: str
    entry_time: str
    exit_time: str
    entry_price: float
    exit_price: float
    exit_reason: str

    hold_seconds: float
    mfe_ticks: float
    mae_ticks: float
    gross_ticks: float
    net_ticks: float
    gross_dollars: float
    net_dollars: float


# ============================================================
# ClickHouse
# ============================================================

def get_client():
    # Note: clickhouse_connect.get_client uses HTTP interface (port 8123)
    # Password is read from ~/.clickhouse-client/config.xml
    return clickhouse_connect.get_client(
        host="localhost",
        port=8123,
        username="default",
        password="unlucky-strange",
        database="default",
    )


def load_entries(client, max_trades: int = MAX_TRADES) -> pd.DataFrame:
    """
    Load only the most promising Phase 5 entries:
      - pullback models only
      - below VWAP
      - low/normal volatility
      - active time buckets
    """
    query = f"""
        SELECT
            trade_date,
            wall_time,
            wall_et,
            wall_side,
            wall_price,
            wall_score,
            wall_behavior,
            delta_flip_pattern,
            wall_aggression_pattern,
            orb_position,
            vwap_relation,
            atr_regime,
            time_bucket,
            session_extreme_location,
            trade_candidate_class,
            trade_side,
            setup_family,
            trigger_model,
            entry_time,
            entry_price
        FROM CG_mnq_phase5_trigger_candidates_v1
        WHERE trigger_model IN ('CONFIRM_6T_PULLBACK', 'CONFIRM_8T_PULLBACK')
          AND vwap_relation = 'BELOW_VWAP'
          AND atr_regime IN ('LOW_VOL', 'NORMAL_VOL')
          AND time_bucket IN ('RTH_OPEN', 'PM_DRIFT', 'CLOSE')
        ORDER BY
            trade_date,
            entry_time
        LIMIT {int(max_trades)}
    """
    return client.query_df(query)


def load_path_for_trade(client, entry_time: str, trade_date: str) -> pd.DataFrame:
    """
    Load tick path after entry.

    We intentionally use mnq_trades, not derived aggregates, because stop-hit
    order matters.

    CRITICAL: Use native datetime parameter binding to ensure correct time window.
    """
    # Convert entry_time string to Python datetime for proper parameter binding
    entry_dt = pd.to_datetime(entry_time).to_pydatetime()
    end_dt = entry_dt + pd.Timedelta(minutes=10)

    query = """
        SELECT
            ts_event,
            price
        FROM mnq_trades
        WHERE ts_event >= %(entry_time)s
          AND ts_event <  %(end_time)s
        ORDER BY ts_event
    """

    path = client.query_df(
        query,
        parameters={"entry_time": entry_dt, "end_time": end_dt}
    )

    # MANDATORY VALIDATION: Ensure we got the correct 10-minute window
    if not path.empty:
        path["ts_event"] = pd.to_datetime(path["ts_event"])
        duration_seconds = (path["ts_event"].max() - path["ts_event"].min()).total_seconds()

        # Hard guardrail: path window must be reasonable
        if duration_seconds > 610:
            raise RuntimeError(
                f"CRITICAL BUG: Path window is {duration_seconds:.1f} seconds "
                f"(expected <= 600s). Query returned wrong time range. "
                f"Entry: {entry_time}, Path range: {path['ts_event'].min()} to {path['ts_event'].max()}"
            )

    return path


# ============================================================
# Simulation helpers
# ============================================================

def ticks_to_points(ticks: float) -> float:
    return ticks * MNQ_TICK_SIZE


def points_to_ticks(points: float) -> float:
    return points / MNQ_TICK_SIZE


def pnl_ticks(side: str, entry: float, exit_price: float) -> float:
    if side == "LONG":
        return points_to_ticks(exit_price - entry)
    if side == "SHORT":
        return points_to_ticks(entry - exit_price)
    raise ValueError(f"Unsupported side: {side}")


def update_mfe_mae(side: str, entry: float, price: float, mfe: float, mae: float) -> tuple[float, float]:
    move_ticks = pnl_ticks(side, entry, price)
    adverse_ticks = -move_ticks
    return max(mfe, move_ticks), max(mae, adverse_ticks)


def local_structure_stop(
    side: str,
    path_so_far: pd.DataFrame,
    entry_price: float,
    lookback_seconds: int,
    buffer_ticks: int,
) -> Optional[float]:
    """
    Structure-aware trailing stop:
      LONG  -> below recent swing low
      SHORT -> above recent swing high

    Uses recent path window as a practical approximation of 5s/10s structure.
    """
    if path_so_far.empty:
        return None

    last_ts = path_so_far["ts_event"].iloc[-1]
    cutoff = last_ts - pd.Timedelta(seconds=lookback_seconds)
    recent = path_so_far[path_so_far["ts_event"] >= cutoff]

    if recent.empty:
        return None

    buffer_pts = ticks_to_points(buffer_ticks)

    if side == "LONG":
        recent_low = float(recent["price"].min())
        return recent_low - buffer_pts

    if side == "SHORT":
        recent_high = float(recent["price"].max())
        return recent_high + buffer_pts

    return None


def tighten_stop(side: str, current_stop: float, candidate_stop: Optional[float]) -> float:
    """
    Never loosen stop.
    LONG stop can only rise.
    SHORT stop can only fall.
    """
    if candidate_stop is None or math.isnan(candidate_stop):
        return current_stop

    if side == "LONG":
        return max(current_stop, candidate_stop)

    if side == "SHORT":
        return min(current_stop, candidate_stop)

    raise ValueError(f"Unsupported side: {side}")


def simulate_one_trade(
    row: pd.Series,
    path: pd.DataFrame,
    params: TrailParams,
    cost_ticks: float = DEFAULT_COST_TICKS,
) -> Optional[SimResult]:
    if path.empty:
        return None

    side = str(row["trade_side"])
    entry_price = float(row["entry_price"])
    entry_time = pd.to_datetime(row["entry_time"])

    if side == "LONG":
        stop_price = entry_price - ticks_to_points(params.initial_stop_ticks)
    elif side == "SHORT":
        stop_price = entry_price + ticks_to_points(params.initial_stop_ticks)
    else:
        return None

    mfe_ticks = 0.0
    mae_ticks = 0.0
    exit_price = float(path["price"].iloc[-1])
    exit_time = path["ts_event"].iloc[-1]
    exit_reason = "TIMEOUT"

    # OPTIMIZATION: Work with path DataFrame directly instead of building incrementally
    for tick_idx in range(len(path)):
        tick = path.iloc[tick_idx]
        ts = tick["ts_event"]
        price = float(tick["price"])

        mfe_ticks, mae_ticks = update_mfe_mae(
            side=side,
            entry=entry_price,
            price=price,
            mfe=mfe_ticks,
            mae=mae_ticks,
        )

        # Initial protective stop / current trailing stop hit.
        if side == "LONG" and price <= stop_price:
            exit_price = stop_price
            exit_time = ts
            exit_reason = "STOP"
            break

        if side == "SHORT" and price >= stop_price:
            exit_price = stop_price
            exit_time = ts
            exit_reason = "STOP"
            break

        # Timeout.
        hold_seconds_now = (ts - entry_time).total_seconds()
        if hold_seconds_now >= params.hard_timeout_seconds:
            exit_price = price
            exit_time = ts
            exit_reason = "TIMEOUT"
            break

        # Do not trail until activation.
        if mfe_ticks < params.activation_ticks:
            continue

        # Use slice of path up to current position (much faster than building new DataFrame)
        path_so_far = path.iloc[:tick_idx + 1]

        # Structural trailing candidate.
        structural_stop = local_structure_stop(
            side=side,
            path_so_far=path_so_far,
            entry_price=entry_price,
            lookback_seconds=params.structure_lookback_seconds,
            buffer_ticks=params.structure_buffer_ticks,
        )

        stop_price = tighten_stop(side, stop_price, structural_stop)

        # Profit lock 1.
        if mfe_ticks >= params.lock1_mfe_ticks:
            if side == "LONG":
                lock_stop = entry_price + ticks_to_points(params.lock1_profit_ticks)
            else:
                lock_stop = entry_price - ticks_to_points(params.lock1_profit_ticks)
            stop_price = tighten_stop(side, stop_price, lock_stop)

        # Profit lock 2.
        if mfe_ticks >= params.lock2_mfe_ticks:
            if side == "LONG":
                lock_stop = entry_price + ticks_to_points(params.lock2_profit_ticks)
            else:
                lock_stop = entry_price - ticks_to_points(params.lock2_profit_ticks)
            stop_price = tighten_stop(side, stop_price, lock_stop)

    gross_ticks = pnl_ticks(side, entry_price, float(exit_price))
    net_ticks = gross_ticks - cost_ticks

    return SimResult(
        trade_date=str(row["trade_date"]),
        wall_time=str(row["wall_time"]),
        trigger_model=str(row["trigger_model"]),
        trade_side=side,
        setup_family=str(row["setup_family"]),
        wall_side=str(row["wall_side"]),
        delta_flip_pattern=str(row["delta_flip_pattern"]),
        orb_position=str(row["orb_position"]),
        vwap_relation=str(row["vwap_relation"]),
        atr_regime=str(row["atr_regime"]),
        time_bucket=str(row["time_bucket"]),
        session_extreme_location=str(row["session_extreme_location"]),

        param_name=params.name,
        entry_time=str(entry_time),
        exit_time=str(exit_time),
        entry_price=entry_price,
        exit_price=float(exit_price),
        exit_reason=exit_reason,

        hold_seconds=(pd.to_datetime(exit_time) - entry_time).total_seconds(),
        mfe_ticks=mfe_ticks,
        mae_ticks=mae_ticks,
        gross_ticks=gross_ticks,
        net_ticks=net_ticks,
        gross_dollars=gross_ticks * MNQ_TICK_VALUE,
        net_dollars=net_ticks * MNQ_TICK_VALUE,
    )


# ============================================================
# Parameter grid
# ============================================================

def parameter_grid() -> list[TrailParams]:
    return [
        TrailParams(
            name="S16_A12_SW10_B4_L20x5_L30x10",
            initial_stop_ticks=16,
            activation_ticks=12,
            structure_lookback_seconds=10,
            structure_buffer_ticks=4,
            lock1_mfe_ticks=20,
            lock1_profit_ticks=5,
            lock2_mfe_ticks=30,
            lock2_profit_ticks=10,
        ),
        TrailParams(
            name="S20_A12_SW10_B4_L24x6_L36x12",
            initial_stop_ticks=20,
            activation_ticks=12,
            structure_lookback_seconds=10,
            structure_buffer_ticks=4,
            lock1_mfe_ticks=24,
            lock1_profit_ticks=6,
            lock2_mfe_ticks=36,
            lock2_profit_ticks=12,
        ),
        TrailParams(
            name="S20_A16_SW15_B5_L30x8_L44x16",
            initial_stop_ticks=20,
            activation_ticks=16,
            structure_lookback_seconds=15,
            structure_buffer_ticks=5,
            lock1_mfe_ticks=30,
            lock1_profit_ticks=8,
            lock2_mfe_ticks=44,
            lock2_profit_ticks=16,
        ),
        TrailParams(
            name="S24_A16_SW15_B6_L32x8_L48x18",
            initial_stop_ticks=24,
            activation_ticks=16,
            structure_lookback_seconds=15,
            structure_buffer_ticks=6,
            lock1_mfe_ticks=32,
            lock1_profit_ticks=8,
            lock2_mfe_ticks=48,
            lock2_profit_ticks=18,
        ),
    ]


# ============================================================
# Reporting
# ============================================================

def summarize(results: pd.DataFrame) -> pd.DataFrame:
    group_cols = [
        "param_name",
        "trigger_model",
        "trade_side",
        "setup_family",
    ]

    summary = (
        results
        .groupby(group_cols, dropna=False)
        .agg(
            trades=("net_ticks", "size"),
            win_rate=("net_ticks", lambda s: (s > 0).mean()),
            gross_expectancy_ticks=("gross_ticks", "mean"),
            net_expectancy_ticks=("net_ticks", "mean"),
            avg_hold_seconds=("hold_seconds", "mean"),
            med_hold_seconds=("hold_seconds", "median"),
            avg_mfe_ticks=("mfe_ticks", "mean"),
            avg_mae_ticks=("mae_ticks", "mean"),
            stops=("exit_reason", lambda s: (s == "STOP").sum()),
            timeouts=("exit_reason", lambda s: (s == "TIMEOUT").sum()),
        )
        .reset_index()
        .sort_values(["net_expectancy_ticks", "trades"], ascending=[False, False])
    )

    return summary


def summarize_conditioned(results: pd.DataFrame) -> pd.DataFrame:
    group_cols = [
        "param_name",
        "trigger_model",
        "trade_side",
        "setup_family",
        "wall_side",
        "delta_flip_pattern",
        "orb_position",
        "time_bucket",
        "atr_regime",
        "session_extreme_location",
    ]

    summary = (
        results
        .groupby(group_cols, dropna=False)
        .agg(
            trades=("net_ticks", "size"),
            win_rate=("net_ticks", lambda s: (s > 0).mean()),
            gross_expectancy_ticks=("gross_ticks", "mean"),
            net_expectancy_ticks=("net_ticks", "mean"),
            avg_hold_seconds=("hold_seconds", "mean"),
            avg_mfe_ticks=("mfe_ticks", "mean"),
            avg_mae_ticks=("mae_ticks", "mean"),
            stops=("exit_reason", lambda s: (s == "STOP").sum()),
            timeouts=("exit_reason", lambda s: (s == "TIMEOUT").sum()),
        )
        .reset_index()
    )

    summary = summary[summary["trades"] >= 30]
    summary = summary.sort_values(["net_expectancy_ticks", "trades"], ascending=[False, False])
    return summary


# ============================================================
# Main
# ============================================================

def main() -> None:
    out_dir = "/home/bernard/trading4/CG_MNQ_MarketReplayLab/results"
    os.makedirs(out_dir, exist_ok=True)

    client = get_client()
    entries = load_entries(client)

    if entries.empty:
        raise RuntimeError("No Phase 5 entries found. Check CG_mnq_phase5_trigger_candidates_v1.")

    params_grid = parameter_grid()
    results: list[SimResult] = []

    print(f"Loaded {len(entries)} entries.")
    print(f"Testing {len(params_grid)} trailing parameter sets.")

    for i, row in entries.iterrows():
        if i % 100 == 0:
            print(f"Processing trade {i + 1}/{len(entries)}")

        path = load_path_for_trade(
            client=client,
            entry_time=str(row["entry_time"]),
            trade_date=str(row["trade_date"]),
        )

        if path.empty:
            continue

        path["ts_event"] = pd.to_datetime(path["ts_event"])

        for params in params_grid:
            sim = simulate_one_trade(row=row, path=path, params=params)
            if sim is not None:
                results.append(sim)

    if not results:
        raise RuntimeError("No simulation results generated.")

    results_df = pd.DataFrame([asdict(r) for r in results])

    raw_path = os.path.join(out_dir, "CG_mnq_phase5b_trailing_sim_results.csv")
    summary_path = os.path.join(out_dir, "CG_mnq_phase5b_trailing_sim_summary.csv")
    conditioned_path = os.path.join(out_dir, "CG_mnq_phase5b_trailing_conditioned_summary.csv")

    results_df.to_csv(raw_path, index=False)

    summary_df = summarize(results_df)
    summary_df.to_csv(summary_path, index=False)

    conditioned_df = summarize_conditioned(results_df)
    conditioned_df.to_csv(conditioned_path, index=False)

    print("\n=== TOP PARAMETER SUMMARIES ===")
    print(summary_df.head(30).to_string(index=False))

    print("\n=== TOP CONDITIONED EDGES ===")
    print(conditioned_df.head(30).to_string(index=False))

    print(f"\nSaved raw results: {raw_path}")
    print(f"Saved summary: {summary_path}")
    print(f"Saved conditioned summary: {conditioned_path}")


if __name__ == "__main__":
    main()
