#!/usr/bin/env python3
"""
ClanMarshal v9.2 True Sequential Walk

Strategy methodology:
- Source table: CG_mnq_cm_v92_production_dynamic_backtest
- Enforces live deployment rule: 1 MNQ contract only, no overlapping positions.
- Greedy chronological walk:
    1. Sort by entry_time.
    2. Accept first available trade.
    3. Hold until its exit_time.
    4. Skip all candidate trades with entry_time <= current exit_time.
    5. Accept next non-overlapping trade.
- Writes:
    CG_mnq_cm_v92_true_sequential_backtest
    CG_mnq_cm_v92_true_sequential_daily
    CG_mnq_cm_v92_true_sequential_friction
- Stores this Python code in CG_strategy_code_repository.
"""

import os
from pathlib import Path
import pandas as pd
import clickhouse_connect


CH_HOST = os.getenv("CH_HOST", os.getenv("CLICKHOUSE_HOST", "localhost"))
CH_PORT = int(os.getenv("CH_PORT", os.getenv("CLICKHOUSE_PORT", "8123")))
CH_USER = os.getenv("CH_USER", os.getenv("CLICKHOUSE_USER", "default"))
CH_PASSWORD = os.getenv("CH_PASSWORD", os.getenv("CLICKHOUSE_PASSWORD", ""))
CH_DATABASE = os.getenv("CH_DATABASE", os.getenv("CLICKHOUSE_DATABASE", "default"))

SOURCE_TABLE = "CG_mnq_cm_v92_production_dynamic_backtest"
SEQ_TABLE = "CG_mnq_cm_v92_true_sequential_backtest"
DAILY_TABLE = "CG_mnq_cm_v92_true_sequential_daily"
FRICTION_TABLE = "CG_mnq_cm_v92_true_sequential_friction"
CODE_TABLE = "CG_strategy_code_repository"


def get_client():
    return clickhouse_connect.get_client(
        host=CH_HOST,
        port=CH_PORT,
        username=CH_USER,
        password=CH_PASSWORD,
        database=CH_DATABASE,
    )


def load_candidates(client) -> pd.DataFrame:
    sql = f"""
    SELECT
        trade_date,
        entry_time,
        signal_side,
        structural_case_v2,
        force_edge_rank,
        entry_price,
        exit_threshold,
        exit_time,
        exit_price,
        pnl_pts,
        hold_seconds,
        exit_reason
    FROM {SOURCE_TABLE}
    WHERE
        entry_time IS NOT NULL
        AND exit_time IS NOT NULL
        AND exit_time >= entry_time
    ORDER BY
        entry_time,
        structural_case_v2
    """
    return client.query_df(sql)


def true_sequential_walk(df: pd.DataFrame) -> pd.DataFrame:
    accepted = []
    current_exit = None

    df = df.sort_values(["entry_time", "structural_case_v2"]).reset_index(drop=True)

    for _, row in df.iterrows():
        entry_time = row["entry_time"]
        exit_time = row["exit_time"]

        if current_exit is None or entry_time > current_exit:
            accepted.append(row)
            current_exit = exit_time

    if not accepted:
        return pd.DataFrame(columns=df.columns)

    out = pd.DataFrame(accepted).reset_index(drop=True)
    out.insert(0, "seq_trade_id", range(1, len(out) + 1))

    out["gross_equity_pts"] = out["pnl_pts"].cumsum()
    out["gross_equity_peak_pts"] = out["gross_equity_pts"].cummax()
    out["gross_drawdown_pts"] = out["gross_equity_pts"] - out["gross_equity_peak_pts"]

    return out


def write_table(client, table_name: str, df: pd.DataFrame):
    client.command(f"DROP TABLE IF EXISTS {table_name}")

    client.command(f"""
    CREATE TABLE {table_name}
    (
        seq_trade_id UInt32,
        trade_date Date,
        entry_time DateTime64(3, 'UTC'),
        signal_side String,
        structural_case_v2 String,
        force_edge_rank Float64,
        entry_price Float64,
        exit_threshold String,
        exit_time DateTime64(3, 'UTC'),
        exit_price Float64,
        pnl_pts Float64,
        hold_seconds Int64,
        exit_reason String,
        gross_equity_pts Float64,
        gross_equity_peak_pts Float64,
        gross_drawdown_pts Float64
    )
    ENGINE = MergeTree
    PARTITION BY trade_date
    ORDER BY (trade_date, entry_time)
    """)

    if not df.empty:
        client.insert_df(table_name, df)


def write_daily_summary(client):
    client.command(f"DROP TABLE IF EXISTS {DAILY_TABLE}")
    client.command(f"""
    CREATE TABLE {DAILY_TABLE}
    ENGINE = MergeTree
    ORDER BY trade_date
    AS
    SELECT
        trade_date,
        count() AS trades,
        round(sum(pnl_pts), 2) AS daily_pnl_pts,
        round(avg(pnl_pts), 2) AS daily_expectancy_pts,
        round(countIf(pnl_pts > 0) / count(), 4) AS daily_win_rate,
        round(min(gross_drawdown_pts), 2) AS worst_intraday_drawdown_pts
    FROM {SEQ_TABLE}
    GROUP BY trade_date
    ORDER BY trade_date
    """)


def write_friction_summary(client):
    client.command(f"DROP TABLE IF EXISTS {FRICTION_TABLE}")
    client.command(f"""
    CREATE TABLE {FRICTION_TABLE}
    ENGINE = MergeTree
    ORDER BY friction_pts
    AS
    SELECT
        friction_pts,
        count() AS trades,
        round(avg(pnl_pts - friction_pts), 2) AS net_expectancy_pts,
        round(sum(pnl_pts - friction_pts), 2) AS net_total_pts,
        round(countIf((pnl_pts - friction_pts) > 0) / count(), 4) AS net_win_rate,
        round(
            sumIf((pnl_pts - friction_pts), (pnl_pts - friction_pts) > 0)
            / nullIf(abs(sumIf((pnl_pts - friction_pts), (pnl_pts - friction_pts) < 0)), 0),
            2
        ) AS net_profit_factor
    FROM
    (
        SELECT
            *,
            arrayJoin([0.25, 0.50, 1.00, 1.50, 2.00]) AS friction_pts
        FROM {SEQ_TABLE}
    )
    GROUP BY friction_pts
    ORDER BY friction_pts
    """)


def save_code_to_clickhouse(client):
    code_text = Path(__file__).read_text()

    client.command(f"""
    CREATE TABLE IF NOT EXISTS {CODE_TABLE}
    (
        strategy_name String,
        version String,
        script_name String,
        created_at DateTime DEFAULT now(),
        code_text String,
        notes String
    )
    ENGINE = MergeTree
    ORDER BY (strategy_name, version, script_name, created_at)
    """)

    client.insert(
        CODE_TABLE,
        [[
            "ClanMarshal",
            "v9.2",
            Path(__file__).name,
            code_text,
            "True one-MNQ sequential walk. Greedy chronological no-overlap validation."
        ]],
        column_names=["strategy_name", "version", "script_name", "code_text", "notes"],
    )


def main():
    client = get_client()

    print("[LOAD] Loading v9.2 production candidates...")
    candidates = load_candidates(client)
    print(f"[LOAD] Candidate trades: {len(candidates)}")

    print("[WALK] Running true sequential one-MNQ walk...")
    seq = true_sequential_walk(candidates)
    print(f"[WALK] Accepted trades: {len(seq)}")

    print(f"[WRITE] Writing {SEQ_TABLE}...")
    write_table(client, SEQ_TABLE, seq)

    print(f"[WRITE] Writing {DAILY_TABLE}...")
    write_daily_summary(client)

    print(f"[WRITE] Writing {FRICTION_TABLE}...")
    write_friction_summary(client)

    print("[AUDIT] Saving Python code into ClickHouse...")
    save_code_to_clickhouse(client)

    print("[DONE] True sequential validation complete.")


if __name__ == "__main__":
    main()
