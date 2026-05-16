#!/usr/bin/env python3
"""
Phase 5C Structure-Based Exit Simulator
McDuff MNQ Intraday Strategy

Purpose:
    Test whether Phase 5 entries become viable when fixed TP/trailing exits
    are replaced by structural exits:
        - VWAP touch
        - ORB boundary touch
        - opposite pressure event
        - protective stop
        - timeout

Methodology:
    This is path-dependent and therefore simulated in Python, not aggregate SQL.

Decision rule:
    If structure exits produce net_expectancy_ticks > 0 with >= 100 trades,
    the wall signal may remain an executable secondary system.
    If not, walls remain context-only.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, asdict
from typing import Optional

import pandas as pd
import clickhouse_connect


MNQ_TICK_SIZE = 0.25
MNQ_TICK_VALUE = 0.50
DEFAULT_COST_TICKS = 5.4
MAX_TRADES = 10000


@dataclass(frozen=True)
class ExitParams:
    name: str
    stop_ticks: int
    timeout_seconds: int
    use_vwap_target: bool
    use_orb_target: bool
    use_opposite_pressure_exit: bool
    breakeven_after_ticks: int
    breakeven_plus_ticks: int


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

    session_vwap: Optional[float]
    orb_low: Optional[float]
    orb_high: Optional[float]
    vwap_target_price: Optional[float]
    orb_target_price: Optional[float]

    hold_seconds: float
    mfe_ticks: float
    mae_ticks: float
    gross_ticks: float
    net_ticks: float
    gross_dollars: float
    net_dollars: float


def get_client():
    return clickhouse_connect.get_client(
        host="localhost",
        port=8123,
        username="default",
        password="unlucky-strange",
        database="default",
    )


def load_entries(client, max_trades: int = MAX_TRADES) -> pd.DataFrame:
    query = f"""
        SELECT
            trade_date,
            wall_time,
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
            entry_price,
            session_vwap,
            orb_low,
            orb_high,
            vwap_target_price,
            orb_target_price,
            opposite_pressure_time
        FROM CG_mnq_phase5c_structure_exit_features_v1
        ORDER BY
            trade_date,
            entry_time
        LIMIT {int(max_trades)}
    """
    return client.query_df(query)


def load_path_for_trade(client, entry_time: pd.Timestamp, trade_date: str, timeout_seconds: int) -> pd.DataFrame:
    entry_dt = entry_time.to_pydatetime()
    end_dt = entry_dt + pd.Timedelta(seconds=timeout_seconds)

    query = """
        SELECT
            ts_event,
            price
        FROM mnq_trades
        WHERE ts_event >= %(entry_time)s
          AND ts_event <  %(end_time)s
        ORDER BY ts_event
    """
    return client.query_df(
        query,
        parameters={
            "entry_time": entry_dt,
            "end_time": end_dt,
        },
    )


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
    move = pnl_ticks(side, entry, price)
    return max(mfe, move), max(mae, -move)


def target_hit(side: str, price: float, target: Optional[float]) -> bool:
    if target is None or pd.isna(target):
        return False
    if side == "LONG":
        return price >= float(target)
    if side == "SHORT":
        return price <= float(target)
    return False


def simulate_one(
    row: pd.Series,
    path: pd.DataFrame,
    params: ExitParams,
    cost_ticks: float = DEFAULT_COST_TICKS,
) -> Optional[SimResult]:
    if path.empty:
        return None

    side = str(row["trade_side"])
    entry_price = float(row["entry_price"])
    entry_time = pd.to_datetime(row["entry_time"])

    if side == "LONG":
        stop_price = entry_price - ticks_to_points(params.stop_ticks)
    elif side == "SHORT":
        stop_price = entry_price + ticks_to_points(params.stop_ticks)
    else:
        return None

    opposite_pressure_time = row.get("opposite_pressure_time", None)
    if opposite_pressure_time is not None and not pd.isna(opposite_pressure_time):
        opposite_pressure_time = pd.to_datetime(opposite_pressure_time)
    else:
        opposite_pressure_time = None

    mfe = 0.0
    mae = 0.0
    exit_price = float(path["price"].iloc[-1])
    exit_time = pd.to_datetime(path["ts_event"].iloc[-1])
    exit_reason = "TIMEOUT"

    for _, tick in path.iterrows():
        ts = pd.to_datetime(tick["ts_event"])
        price = float(tick["price"])

        mfe, mae = update_mfe_mae(side, entry_price, price, mfe, mae)

        # Breakeven protection after move confirms.
        if params.breakeven_after_ticks > 0 and mfe >= params.breakeven_after_ticks:
            if side == "LONG":
                candidate = entry_price + ticks_to_points(params.breakeven_plus_ticks)
                stop_price = max(stop_price, candidate)
            else:
                candidate = entry_price - ticks_to_points(params.breakeven_plus_ticks)
                stop_price = min(stop_price, candidate)

        # Protective stop.
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

        # VWAP target.
        if params.use_vwap_target and target_hit(side, price, row["vwap_target_price"]):
            exit_price = float(row["vwap_target_price"])
            exit_time = ts
            exit_reason = "VWAP_TARGET"
            break

        # ORB target.
        if params.use_orb_target and target_hit(side, price, row["orb_target_price"]):
            exit_price = float(row["orb_target_price"])
            exit_time = ts
            exit_reason = "ORB_TARGET"
            break

        # Opposite pressure event.
        if (
            params.use_opposite_pressure_exit
            and opposite_pressure_time is not None
            and ts >= opposite_pressure_time
        ):
            exit_price = price
            exit_time = ts
            exit_reason = "OPPOSITE_PRESSURE"
            break

        if (ts - entry_time).total_seconds() >= params.timeout_seconds:
            exit_price = price
            exit_time = ts
            exit_reason = "TIMEOUT"
            break

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

        session_vwap=None if pd.isna(row["session_vwap"]) else float(row["session_vwap"]),
        orb_low=None if pd.isna(row["orb_low"]) else float(row["orb_low"]),
        orb_high=None if pd.isna(row["orb_high"]) else float(row["orb_high"]),
        vwap_target_price=None if pd.isna(row["vwap_target_price"]) else float(row["vwap_target_price"]),
        orb_target_price=None if pd.isna(row["orb_target_price"]) else float(row["orb_target_price"]),

        hold_seconds=(exit_time - entry_time).total_seconds(),
        mfe_ticks=mfe,
        mae_ticks=mae,
        gross_ticks=gross_ticks,
        net_ticks=net_ticks,
        gross_dollars=gross_ticks * MNQ_TICK_VALUE,
        net_dollars=net_ticks * MNQ_TICK_VALUE,
    )


def parameter_grid() -> list[ExitParams]:
    return [
        ExitParams(
            name="S16_VWAP_ORB_OPP_BE12p1_T10m",
            stop_ticks=16,
            timeout_seconds=600,
            use_vwap_target=True,
            use_orb_target=True,
            use_opposite_pressure_exit=True,
            breakeven_after_ticks=12,
            breakeven_plus_ticks=1,
        ),
        ExitParams(
            name="S20_VWAP_ORB_OPP_BE16p2_T10m",
            stop_ticks=20,
            timeout_seconds=600,
            use_vwap_target=True,
            use_orb_target=True,
            use_opposite_pressure_exit=True,
            breakeven_after_ticks=16,
            breakeven_plus_ticks=2,
        ),
        ExitParams(
            name="S20_VWAP_ONLY_BE16p2_T10m",
            stop_ticks=20,
            timeout_seconds=600,
            use_vwap_target=True,
            use_orb_target=False,
            use_opposite_pressure_exit=False,
            breakeven_after_ticks=16,
            breakeven_plus_ticks=2,
        ),
        ExitParams(
            name="S20_ORB_ONLY_BE16p2_T10m",
            stop_ticks=20,
            timeout_seconds=600,
            use_vwap_target=False,
            use_orb_target=True,
            use_opposite_pressure_exit=False,
            breakeven_after_ticks=16,
            breakeven_plus_ticks=2,
        ),
        ExitParams(
            name="S24_VWAP_ORB_NOOPP_BE20p4_T15m",
            stop_ticks=24,
            timeout_seconds=900,
            use_vwap_target=True,
            use_orb_target=True,
            use_opposite_pressure_exit=False,
            breakeven_after_ticks=20,
            breakeven_plus_ticks=4,
        ),
        ExitParams(
            name="S24_NO_TARGET_OPP_BE20p4_T15m",
            stop_ticks=24,
            timeout_seconds=900,
            use_vwap_target=False,
            use_orb_target=False,
            use_opposite_pressure_exit=True,
            breakeven_after_ticks=20,
            breakeven_plus_ticks=4,
        ),
    ]


def summarize(results: pd.DataFrame) -> pd.DataFrame:
    group_cols = [
        "param_name",
        "trigger_model",
        "trade_side",
        "setup_family",
    ]

    return (
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
            vwap_targets=("exit_reason", lambda s: (s == "VWAP_TARGET").sum()),
            orb_targets=("exit_reason", lambda s: (s == "ORB_TARGET").sum()),
            opposite_exits=("exit_reason", lambda s: (s == "OPPOSITE_PRESSURE").sum()),
            timeouts=("exit_reason", lambda s: (s == "TIMEOUT").sum()),
        )
        .reset_index()
        .sort_values(["net_expectancy_ticks", "trades"], ascending=[False, False])
    )


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

    out = (
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
            vwap_targets=("exit_reason", lambda s: (s == "VWAP_TARGET").sum()),
            orb_targets=("exit_reason", lambda s: (s == "ORB_TARGET").sum()),
            opposite_exits=("exit_reason", lambda s: (s == "OPPOSITE_PRESSURE").sum()),
            timeouts=("exit_reason", lambda s: (s == "TIMEOUT").sum()),
        )
        .reset_index()
    )

    out = out[out["trades"] >= 30]
    return out.sort_values(["net_expectancy_ticks", "trades"], ascending=[False, False])


def main() -> None:
    out_dir = "/home/bernard/trading4/CG_MNQ_MarketReplayLab/results"
    os.makedirs(out_dir, exist_ok=True)

    client = get_client()
    entries = load_entries(client)
    params_grid = parameter_grid()

    if entries.empty:
        raise RuntimeError("No entries found in CG_mnq_phase5c_structure_exit_features_v1")

    results: list[SimResult] = []

    print(f"Loaded {len(entries)} Phase 5C entries.")
    print(f"Testing {len(params_grid)} structure-exit parameter sets.")

    for i, row in entries.iterrows():
        if i % 100 == 0:
            print(f"Processing {i + 1}/{len(entries)}")

        entry_time = pd.to_datetime(row["entry_time"])

        max_timeout = max(p.timeout_seconds for p in params_grid)

        path = load_path_for_trade(
            client=client,
            entry_time=entry_time,
            trade_date=str(row["trade_date"]),
            timeout_seconds=max_timeout,
        )

        if path.empty:
            continue

        path["ts_event"] = pd.to_datetime(path["ts_event"])

        duration = (path["ts_event"].max() - path["ts_event"].min()).total_seconds()
        if duration > max_timeout + 10:
            raise RuntimeError(
                f"Bad path window: {duration} seconds for entry_time={entry_time}"
            )

        for params in params_grid:
            sim = simulate_one(row, path, params)
            if sim is not None:
                results.append(sim)

    if not results:
        raise RuntimeError("No simulation results generated.")

    results_df = pd.DataFrame([asdict(r) for r in results])

    raw_path = os.path.join(out_dir, "CG_mnq_phase5c_structure_exit_results.csv")
    summary_path = os.path.join(out_dir, "CG_mnq_phase5c_structure_exit_summary.csv")
    conditioned_path = os.path.join(out_dir, "CG_mnq_phase5c_structure_exit_conditioned_summary.csv")

    results_df.to_csv(raw_path, index=False)

    summary_df = summarize(results_df)
    summary_df.to_csv(summary_path, index=False)

    conditioned_df = summarize_conditioned(results_df)
    conditioned_df.to_csv(conditioned_path, index=False)

    print("\n=== TOP STRUCTURE EXIT PARAMETER SUMMARIES ===")
    print(summary_df.head(40).to_string(index=False))

    print("\n=== TOP STRUCTURE EXIT CONDITIONED EDGES ===")
    print(conditioned_df.head(40).to_string(index=False))

    print(f"\nSaved raw results: {raw_path}")
    print(f"Saved summary: {summary_path}")
    print(f"Saved conditioned summary: {conditioned_path}")


if __name__ == "__main__":
    main()
