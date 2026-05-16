#!/usr/bin/env python3
"""
CG_MNQ rolling heatmap + aggression chart renderer (CLI version).

This version uses clickhouse-client subprocess calls instead of clickhouse_connect
to avoid HTTP authentication issues.
"""

from __future__ import annotations

import argparse
import math
import subprocess
from dataclasses import dataclass
from datetime import datetime, timedelta, time
from pathlib import Path
from typing import Iterable
import io

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from matplotlib.colors import LogNorm
from matplotlib.patches import Circle, Wedge


# =============================================================================
# CONFIG
# =============================================================================

@dataclass(frozen=True)
class Config:
    heatmap_table: str = "CG_mnq_heatmap_1s"
    heatmap_time_col: str = "bucket_time"
    heatmap_price_col: str = "price"
    resting_expr: str = "greatest(abs(bid_net_liquidity), abs(ask_net_liquidity))"

    aggression_table: str = "CG_mnq_aggression_multiscale_v1"
    aggression_time_col: str = "ts_100ms"
    buy_col: str = "buy_exec_size_1s"
    sell_col: str = "sell_exec_size_1s"

    tz: str = "America/New_York"
    rth_start_minute: int = 570
    rth_end_minute: int = 960

    frame_minutes: int = 30
    step_minutes: int = 5
    price_tick: float = 0.25
    max_bubble_area: float = 700.0
    min_bubble_area: float = 20.0
    dpi: int = 140


CONFIG = Config()


# =============================================================================
# CLICKHOUSE CLI
# =============================================================================

def query_clickhouse(sql: str) -> pd.DataFrame:
    """Execute SQL via clickhouse-client and return DataFrame."""
    result = subprocess.run(
        ["clickhouse-client", "--query", sql, "--format", "CSVWithNames"],
        capture_output=True,
        text=True,
        check=True,
    )
    if not result.stdout.strip():
        return pd.DataFrame()
    return pd.read_csv(io.StringIO(result.stdout))


def fetch_days(start_date: str | None, end_date: str | None) -> list[pd.Timestamp]:
    where = []
    if start_date:
        where.append(f"trade_date >= '{start_date}'")
    if end_date:
        where.append(f"trade_date <= '{end_date}'")
    where_sql = "WHERE " + " AND ".join(where) if where else ""

    q = f"""
        SELECT DISTINCT trade_date
        FROM CG_mnq_session_regime_v2
        {where_sql}
        ORDER BY trade_date
    """
    df = query_clickhouse(q)
    return list(pd.to_datetime(df["trade_date"]))


def fetch_daily_max_resting(cfg: Config, trade_date: pd.Timestamp) -> float:
    q = f"""
        SELECT max(resting_liquidity) AS daily_max_resting
        FROM
        (
            SELECT {cfg.resting_expr} AS resting_liquidity
            FROM {cfg.heatmap_table}
            WHERE toDate(toTimeZone({cfg.heatmap_time_col}, '{cfg.tz}')) = '{trade_date.date()}'
              AND (toHour(toTimeZone({cfg.heatmap_time_col}, '{cfg.tz}')) * 60
                   + toMinute(toTimeZone({cfg.heatmap_time_col}, '{cfg.tz}'))
                  ) BETWEEN {cfg.rth_start_minute} AND {cfg.rth_end_minute}
        )
    """
    df = query_clickhouse(q)
    value = float(df.loc[0, "daily_max_resting"] or 1.0)
    return max(value, 1.0)


def fetch_window_data(
    cfg: Config,
    trade_date: pd.Timestamp,
    window_start: datetime,
    window_end: datetime,
) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Fetch heatmap and aggression data for one rolling window."""

    heat_q = f"""
        SELECT
            toStartOfInterval({cfg.heatmap_time_col}, toIntervalSecond(1)) AS t,
            round({cfg.heatmap_price_col} / {cfg.price_tick}) * {cfg.price_tick} AS price,
            sum({cfg.resting_expr}) AS resting_liquidity
        FROM {cfg.heatmap_table}
        WHERE {cfg.heatmap_time_col} >= '{window_start}'
          AND {cfg.heatmap_time_col} <  '{window_end}'
          AND toDate(toTimeZone({cfg.heatmap_time_col}, '{cfg.tz}')) = '{trade_date.date()}'
        GROUP BY t, price
        HAVING resting_liquidity > 0
        ORDER BY t, price
    """

    aggr_q = f"""
        SELECT
            toStartOfInterval({cfg.aggression_time_col}, toIntervalSecond(1)) AS t,
            sum({cfg.buy_col}) AS buy_volume,
            sum({cfg.sell_col}) AS sell_volume,
            buy_volume + sell_volume AS total_volume
        FROM {cfg.aggression_table}
        WHERE {cfg.aggression_time_col} >= '{window_start}'
          AND {cfg.aggression_time_col} <  '{window_end}'
          AND trade_date = '{trade_date.date()}'
        GROUP BY t
        HAVING total_volume > 0
        ORDER BY t
    """

    heat = query_clickhouse(heat_q)
    aggr = query_clickhouse(aggr_q)

    if not heat.empty:
        heat["t"] = pd.to_datetime(heat["t"])
    if not aggr.empty:
        aggr["t"] = pd.to_datetime(aggr["t"])

    return heat, aggr


# =============================================================================
# RENDERING
# =============================================================================

def make_grid(heat: pd.DataFrame) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    times = np.sort(heat["t"].unique())
    prices = np.sort(heat["price"].unique())

    pivot = heat.pivot_table(
        index="price",
        columns="t",
        values="resting_liquidity",
        aggfunc="sum",
        fill_value=0.0,
    )
    pivot = pivot.reindex(index=prices, columns=times, fill_value=0.0)

    z = pivot.to_numpy(dtype=float)
    z[z <= 0] = np.nan

    return times, prices, z


def draw_aggression_bubble(
    ax,
    x,
    y,
    total_volume: float,
    buy_volume: float,
    sell_volume: float,
    max_total_volume: float,
    cfg: Config,
):
    if total_volume <= 0 or max_total_volume <= 0:
        return

    area = cfg.min_bubble_area + (cfg.max_bubble_area - cfg.min_bubble_area) * math.sqrt(total_volume / max_total_volume)
    radius = math.sqrt(area) / 180.0

    ax.add_patch(Circle((x, y), radius=radius, fill=False, linewidth=0.4, edgecolor="white", alpha=0.55))

    buy_frac = float(buy_volume) / float(total_volume) if total_volume else 0.0
    sell_frac = float(sell_volume) / float(total_volume) if total_volume else 0.0

    if buy_frac > 0:
        ax.add_patch(
            Wedge(
                (x, y),
                r=radius,
                theta1=90,
                theta2=90 + 360 * buy_frac,
                width=radius * 0.18,
                facecolor="green",
                edgecolor="green",
                alpha=0.95,
            )
        )
    if sell_frac > 0:
        ax.add_patch(
            Wedge(
                (x, y),
                r=radius,
                theta1=90 + 360 * buy_frac,
                theta2=90 + 360 * (buy_frac + sell_frac),
                width=radius * 0.18,
                facecolor="red",
                edgecolor="red",
                alpha=0.95,
            )
        )


def render_frame(
    heat: pd.DataFrame,
    aggr: pd.DataFrame,
    daily_max_resting: float,
    out_path: Path,
    title: str,
    cfg: Config,
):
    if heat.empty:
        return

    times, prices, z = make_grid(heat)
    if len(times) < 2 or len(prices) < 2:
        return

    t0 = pd.Timestamp(times[0])
    x_vals = np.array([(pd.Timestamp(t) - t0).total_seconds() / 60.0 for t in times])

    fig, ax = plt.subplots(figsize=(16, 8), dpi=cfg.dpi)

    vmin = max(1.0, np.nanmin(z[np.isfinite(z)]) if np.any(np.isfinite(z)) else 1.0)
    vmax = max(daily_max_resting, vmin * 1.01)

    mesh = ax.pcolormesh(
        x_vals,
        prices,
        z,
        shading="auto",
        cmap="gray",
        norm=LogNorm(vmin=vmin, vmax=vmax),
    )

    cbar = fig.colorbar(mesh, ax=ax)
    cbar.set_label("Resting liquidity, log scale; daily max = white")

    if not aggr.empty:
        max_total = float(aggr["total_volume"].max())
        y_mid = float(np.nanmedian(prices))
        for _, row in aggr.iterrows():
            x = (pd.Timestamp(row["t"]) - t0).total_seconds() / 60.0
            draw_aggression_bubble(
                ax=ax,
                x=x,
                y=y_mid,
                total_volume=float(row["total_volume"]),
                buy_volume=float(row["buy_volume"]),
                sell_volume=float(row["sell_volume"]),
                max_total_volume=max_total,
                cfg=cfg,
            )

    ax.set_title(title)
    ax.set_xlabel("Minutes from rolling-window start")
    ax.set_ylabel("Price")
    ax.grid(False)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    fig.tight_layout()
    fig.savefig(out_path)
    plt.close(fig)


def rth_window_times(trade_date: pd.Timestamp, cfg: Config) -> Iterable[tuple[datetime, datetime]]:
    d = trade_date.date()
    start = datetime.combine(d, time(9, 30))
    end = datetime.combine(d, time(16, 0))

    cur = start
    width = timedelta(minutes=cfg.frame_minutes)
    step = timedelta(minutes=cfg.step_minutes)

    while cur + width <= end:
        yield cur, cur + width
        cur += step


# =============================================================================
# MAIN
# =============================================================================

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--start-date", default=None, help="YYYY-MM-DD")
    parser.add_argument("--end-date", default=None, help="YYYY-MM-DD")
    parser.add_argument("--out-dir", default="frames_heatmap_aggression")
    parser.add_argument("--heatmap-table", default=CONFIG.heatmap_table)
    parser.add_argument("--resting-expr", default=CONFIG.resting_expr)
    parser.add_argument("--frame-minutes", type=int, default=CONFIG.frame_minutes)
    parser.add_argument("--step-minutes", type=int, default=CONFIG.step_minutes)
    args = parser.parse_args()

    cfg = Config(
        heatmap_table=args.heatmap_table,
        resting_expr=args.resting_expr,
        frame_minutes=args.frame_minutes,
        step_minutes=args.step_minutes,
    )

    out_dir = Path(args.out_dir)

    days = fetch_days(args.start_date, args.end_date)
    if not days:
        raise RuntimeError("No trading days found in CG_mnq_session_regime_v2 for requested range.")

    frame_id = 0
    for trade_date in days:
        daily_max = fetch_daily_max_resting(cfg, trade_date)
        for window_start, window_end in rth_window_times(trade_date, cfg):
            heat, aggr = fetch_window_data(cfg, trade_date, window_start, window_end)
            if heat.empty:
                continue

            frame_id += 1
            out_path = out_dir / f"frame_{frame_id:05d}_{trade_date.date()}_{window_start.strftime('%H%M')}.png"
            title = (
                f"MNQ RTH Liquidity Heatmap + Aggression | {trade_date.date()} "
                f"{window_start.strftime('%H:%M')}–{window_end.strftime('%H:%M')} ET"
            )
            render_frame(heat, aggr, daily_max, out_path, title, cfg)
            print(f"Wrote {out_path}")

    print(f"Done. Frames written: {frame_id}")
    print(f"Output directory: {out_dir.resolve()}")
    print("Optional MP4:")
    print(f"  ffmpeg -framerate 8 -i {out_dir}/frame_%05d_*.png -pix_fmt yuv420p CG_MNQ_heatmap_aggression_roll.mp4")


if __name__ == "__main__":
    main()
