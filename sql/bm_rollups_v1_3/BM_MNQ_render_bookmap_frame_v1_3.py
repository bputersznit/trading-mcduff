#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# BM_MNQ_render_bookmap_frame_v1_3.py
# Generated: 2026-05-10
#
# Purpose:
#   Bookmap-style renderer with dynamic viewport, EMA persistence trails,
#   and local ladder rendering (target ~80-250 price levels vs 2000+).
#
# Key improvements over v1_2:
#   1. Dynamic viewport centering around traded prices
#   2. Configurable price window size (--price-window-points)
#   3. EMA persistence trails for heatmap temporal memory
#   4. Local ladder rendering instead of entire session ladder
#   5. Better normalization and bubble overlay
#
# Required:
#   pip install clickhouse-connect pandas numpy matplotlib
#
# Example:
#   python BM_MNQ_render_bookmap_frame_v1_3.py \
#     --trade-date 2025-10-07 \
#     --start-time 09:30:00 \
#     --end-time 09:35:00 \
#     --scale 1S \
#     --symbol "" \
#     --price-window-points 15 \
#     --center-mode traded_median \
#     --heatmap-field heatmap_proxy_value \
#     --heatmap-lower-quantile 0.55 \
#     --heatmap-upper-quantile 0.999 \
#     --heatmap-gamma 0.38 \
#     --persistence-alpha 0.85 \
#     --bubble-area-q95 45 \
#     --bubble-alpha 0.35 \
#     --out ./BM_MNQ_v1_3.png

from __future__ import annotations

import argparse
import os
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Tuple

import clickhouse_connect
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd


@dataclass
class CHConfig:
    host: str
    port: int
    user: str
    password: str
    database: str
    secure: bool


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Render BM_MNQ Bookmap-style frame with dynamic viewport.")
    p.add_argument("--trade-date", default="2025-10-07")
    p.add_argument("--start-time", default="09:30:00")
    p.add_argument("--end-time", default="09:35:00")
    p.add_argument("--scale", default="1S", choices=["1S", "5S", "30S", "1M", "5M"])
    p.add_argument("--symbol", default="MNQZ5")
    p.add_argument("--table-prefix", default="BM_MNQ_FRAME_SOURCE_")
    p.add_argument("--out", default="")
    p.add_argument("--title", default="")
    p.add_argument("--dpi", type=int, default=180)
    p.add_argument("--fig-width", type=float, default=16.0)
    p.add_argument("--fig-height", type=float, default=9.0)

    # Viewport controls
    p.add_argument(
        "--price-window-points",
        type=int,
        default=15,
        help="Number of price tick points above and below center (total range = 2*N+1)",
    )
    p.add_argument(
        "--center-mode",
        default="traded_median",
        choices=["traded_median", "traded_mean", "global_median", "global_mean"],
        help="Method for centering the price viewport",
    )

    # Heatmap controls
    p.add_argument(
        "--heatmap-field",
        default="heatmap_proxy_value",
        choices=["heatmap_intensity", "heatmap_proxy_value", "total_liquidity_event_size"],
        help="Field used to render heatmap stripes",
    )
    p.add_argument("--heatmap-lower-quantile", type=float, default=0.50)
    p.add_argument("--heatmap-upper-quantile", type=float, default=0.995)
    p.add_argument("--heatmap-gamma", type=float, default=0.50)
    p.add_argument(
        "--persistence-alpha",
        type=float,
        default=0.85,
        help="EMA alpha for heatmap persistence (0=no memory, 1=full persistence)",
    )

    # Bubble controls
    p.add_argument("--no-bubbles", action="store_true")
    p.add_argument("--bubble-area-q95", type=float, default=110.0)
    p.add_argument("--bubble-min-area", type=float, default=4.0)
    p.add_argument("--bubble-max-area", type=float, default=400.0)
    p.add_argument("--bubble-alpha", type=float, default=0.65)

    # Debug controls
    p.add_argument("--show-grid", action="store_true")
    p.add_argument("--print-head", type=int, default=0)
    return p.parse_args()


def getenv_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "y", "on"}


def build_ch_config() -> CHConfig:
    return CHConfig(
        host=os.getenv("CH_HOST", "localhost"),
        port=int(os.getenv("CH_PORT", "8123")),
        user=os.getenv("CH_USER", "default"),
        password=os.getenv("CH_PASSWORD", ""),
        database=os.getenv("CH_DATABASE", "default"),
        secure=getenv_bool("CH_SECURE", False),
    )


def get_client(cfg: CHConfig):
    return clickhouse_connect.get_client(
        host=cfg.host,
        port=cfg.port,
        username=cfg.user,
        password=cfg.password,
        database=cfg.database,
        secure=cfg.secure,
    )


def validate_identifier(identifier: str) -> str:
    allowed = set("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_")
    if not identifier or any(ch not in allowed for ch in identifier):
        raise ValueError(f"Unsafe identifier: {identifier!r}")
    return identifier


def build_output_path(args: argparse.Namespace) -> Path:
    if args.out:
        return Path(args.out)
    return Path.cwd() / (
        f"BM_MNQ_{args.trade_date}_{args.start_time.replace(':','')}_{args.end_time.replace(':','')}_{args.scale}_v1_3.png"
    )


def query_frame_data(client, table_name: str, args: argparse.Namespace) -> pd.DataFrame:
    start_ts = f"{args.trade_date} {args.start_time}"
    end_ts = f"{args.trade_date} {args.end_time}"
    symbol_clause = ""
    if args.symbol:
        symbol_safe = args.symbol.replace("'", "''")
        symbol_clause = f"\n      AND symbol = '{symbol_safe}'"

    q = f"""
    SELECT
        trade_date,
        ts_et,
        symbol,
        price,
        total_liquidity_event_size,
        heatmap_proxy_value,
        heatmap_intensity,
        buy_exec_size,
        sell_exec_size,
        total_exec_size,
        exec_delta,
        exec_imbalance
    FROM {table_name}
    WHERE trade_date = toDate('{args.trade_date}')
      AND ts_et >= toDateTime64('{start_ts}', 3, 'America/New_York')
      AND ts_et <  toDateTime64('{end_ts}', 3, 'America/New_York'){symbol_clause}
    ORDER BY ts_et, price
    """
    return client.query_df(q)


def split_layers(df: pd.DataFrame) -> Tuple[pd.DataFrame, pd.DataFrame]:
    heatmap_mask = (
        (df["total_liquidity_event_size"].fillna(0) > 0)
        | (df["heatmap_proxy_value"].fillna(0) > 0)
        | (df["heatmap_intensity"].fillna(0) > 0)
    )
    aggression_mask = df["total_exec_size"].fillna(0) > 0
    return df.loc[heatmap_mask].copy(), df.loc[aggression_mask].copy()


def compute_viewport_center(df: pd.DataFrame, mode: str) -> float:
    """Compute center price for viewport based on mode."""
    if df.empty:
        return 0.0

    prices = df["price"].astype(float)

    if mode == "traded_median":
        # Use execution-weighted prices
        traded = df.loc[df["total_exec_size"] > 0, "price"]
        if traded.empty:
            return float(prices.median())
        return float(traded.median())

    elif mode == "traded_mean":
        # Use execution-weighted prices
        traded = df.loc[df["total_exec_size"] > 0, "price"]
        if traded.empty:
            return float(prices.mean())
        return float(traded.mean())

    elif mode == "global_median":
        return float(prices.median())

    elif mode == "global_mean":
        return float(prices.mean())

    else:
        return float(prices.median())


def filter_viewport_prices(
    df: pd.DataFrame,
    center_price: float,
    window_points: int,
    tick_size: float = 0.25,
) -> Tuple[float, float]:
    """Calculate min/max prices for viewport window."""
    half_range = window_points * tick_size
    return center_price - half_range, center_price + half_range


def normalize_values(raw_values: np.ndarray, lower_q: float, upper_q: float, gamma: float) -> np.ndarray:
    values = np.asarray(raw_values, dtype=float)
    values = np.where(np.isfinite(values), values, 0.0)
    values = np.where(values > 0, values, 0.0)

    if values.size == 0 or np.max(values) <= 0:
        return values

    # log1p for long-tail raw fields
    values = np.log1p(values)

    positive = values[values > 0]
    if positive.size == 0:
        return values

    q_low = float(np.quantile(positive, lower_q))
    q_high = float(np.quantile(positive, upper_q))

    if not np.isfinite(q_low):
        q_low = float(np.min(positive))
    if not np.isfinite(q_high):
        q_high = float(np.max(positive))
    if q_high <= q_low:
        q_low = float(np.min(positive))
        q_high = float(np.max(positive))
    if q_high <= q_low:
        return np.where(values > 0, 1.0, 0.0)

    stretched = (values - q_low) / (q_high - q_low)
    stretched = np.clip(stretched, 0.0, 1.0)

    if gamma > 0 and gamma != 1.0:
        stretched = np.where(stretched > 0, np.power(stretched, gamma), 0.0)

    return stretched


def prepare_heatmap_matrix_with_persistence(
    heatmap_df: pd.DataFrame,
    heatmap_field: str,
    lower_q: float,
    upper_q: float,
    gamma: float,
    persistence_alpha: float,
    min_price: float,
    max_price: float,
) -> Tuple[np.ma.MaskedArray, List[pd.Timestamp], List[float], int]:
    """Prepare heatmap with EMA persistence trails and viewport filtering."""
    if heatmap_df.empty:
        return np.ma.masked_all((1, 1)), [], [], 0

    # Filter to viewport
    heatmap_df = heatmap_df.loc[
        (heatmap_df["price"] >= min_price) & (heatmap_df["price"] <= max_price)
    ].copy()

    if heatmap_df.empty:
        return np.ma.masked_all((1, 1)), [], [], 0

    grouped = (
        heatmap_df.groupby(["ts_et", "price"], as_index=False)[heatmap_field]
        .max()
        .sort_values(["ts_et", "price"])
    )

    times = [pd.Timestamp(x) for x in sorted(pd.to_datetime(grouped["ts_et"]).unique().tolist())]
    prices = [float(x) for x in sorted(pd.to_numeric(grouped["price"]).unique().tolist())]

    time_index: Dict[pd.Timestamp, int] = {pd.Timestamp(t): i for i, t in enumerate(times)}
    price_index: Dict[float, int] = {float(p): i for i, p in enumerate(prices)}

    raw = np.zeros((len(prices), len(times)), dtype=float)
    for row in grouped.itertuples(index=False):
        t = pd.Timestamp(row.ts_et)
        p = float(row.price)
        raw[price_index[p], time_index[t]] = float(getattr(row, heatmap_field))

    # Apply EMA persistence across time dimension
    if persistence_alpha > 0 and persistence_alpha < 1.0:
        persisted = np.zeros_like(raw)
        for t_idx in range(raw.shape[1]):
            if t_idx == 0:
                persisted[:, t_idx] = raw[:, t_idx]
            else:
                persisted[:, t_idx] = (
                    persistence_alpha * persisted[:, t_idx - 1] + (1 - persistence_alpha) * raw[:, t_idx]
                )
        raw = persisted

    visible = normalize_values(raw, lower_q, upper_q, gamma)
    positive_cells = int((raw > 0).sum())
    return np.ma.masked_where(visible <= 0, visible), times, prices, positive_cells


def prepare_bubbles(aggression_df: pd.DataFrame, min_price: float, max_price: float) -> pd.DataFrame:
    """Prepare aggression bubbles with viewport filtering."""
    if aggression_df.empty:
        return aggression_df.copy()

    # Filter to viewport
    aggression_df = aggression_df.loc[
        (aggression_df["price"] >= min_price) & (aggression_df["price"] <= max_price)
    ].copy()

    if aggression_df.empty:
        return aggression_df.copy()

    grouped = (
        aggression_df.groupby(["ts_et", "price"], as_index=False)
        .agg(
            buy_exec_size=("buy_exec_size", "sum"),
            sell_exec_size=("sell_exec_size", "sum"),
            total_exec_size=("total_exec_size", "sum"),
            exec_delta=("exec_delta", "sum"),
        )
        .sort_values(["ts_et", "price"])
        .reset_index(drop=True)
    )
    grouped["exec_imbalance"] = np.where(
        grouped["total_exec_size"] > 0,
        grouped["exec_delta"] / grouped["total_exec_size"],
        0.0,
    )
    grouped["edge_color"] = np.where(grouped["exec_delta"] >= 0, "#00ff66", "#ff2a2a")
    return grouped


def compute_bubble_sizes(bubbles: pd.DataFrame, area_q95: float, min_area: float, max_area: float) -> np.ndarray:
    if bubbles.empty:
        return np.array([], dtype=float)
    q95 = float(np.percentile(bubbles["total_exec_size"], 95))
    scale = 1.0 if q95 <= 0 else area_q95 / q95
    return np.clip(bubbles["total_exec_size"].to_numpy(dtype=float) * scale, min_area, max_area)


def choose_tick_step(n: int) -> int:
    if n <= 20:
        return 1
    if n <= 60:
        return 5
    if n <= 180:
        return 10
    if n <= 360:
        return 20
    return max(1, n // 20)


def render(
    matrix: np.ma.MaskedArray,
    times: List[pd.Timestamp],
    prices: List[float],
    bubbles: pd.DataFrame,
    args: argparse.Namespace,
    output_path: Path,
) -> None:
    fig, ax = plt.subplots(figsize=(args.fig_width, args.fig_height))
    fig.patch.set_facecolor("black")
    ax.set_facecolor("black")

    has_heatmap = len(times) > 0 and len(prices) > 0
    has_bubbles = (not args.no_bubbles) and (not bubbles.empty)

    if not has_heatmap and not has_bubbles:
        raise RuntimeError("Nothing to render: no heatmap cells and no aggression bubbles in viewport.")

    master_times = times
    master_prices = prices

    if not has_heatmap and has_bubbles:
        master_times = [pd.Timestamp(x) for x in sorted(pd.to_datetime(bubbles["ts_et"]).unique().tolist())]
        master_prices = [float(x) for x in sorted(pd.to_numeric(bubbles["price"]).unique().tolist())]

    time_pos = {pd.Timestamp(t): i for i, t in enumerate(master_times)}
    price_pos = {float(p): i for i, p in enumerate(master_prices)}

    if has_heatmap:
        ax.imshow(
            matrix,
            origin="lower",
            aspect="auto",
            interpolation="nearest",
            cmap="gray",
            vmin=0.0,
            vmax=1.0,
            extent=[-0.5, len(master_times) - 0.5, -0.5, len(master_prices) - 0.5],
            zorder=1,
        )

    if has_bubbles:
        bubbles = bubbles.copy()
        bubbles["x"] = bubbles["ts_et"].map(lambda x: time_pos.get(pd.Timestamp(x), np.nan))
        bubbles["y"] = bubbles["price"].astype(float).map(lambda x: price_pos.get(float(x), np.nan))
        bubbles = bubbles.dropna(subset=["x", "y"]).copy()

        sizes = compute_bubble_sizes(
            bubbles,
            area_q95=args.bubble_area_q95,
            min_area=args.bubble_min_area,
            max_area=args.bubble_max_area,
        )
        widths = 0.5 + 1.5 * np.abs(bubbles["exec_imbalance"].clip(-1, 1).to_numpy(dtype=float))

        for color in ["#00ff66", "#ff2a2a"]:
            sub = bubbles.loc[bubbles["edge_color"] == color].copy()
            if sub.empty:
                continue
            idx = sub.index.to_numpy()
            ax.scatter(
                sub["x"].to_numpy(dtype=float),
                sub["y"].to_numpy(dtype=float),
                s=sizes[idx],
                facecolors="none",
                edgecolors=color,
                linewidths=widths[idx],
                alpha=args.bubble_alpha,
                zorder=2,
            )

    ax.set_xlabel("Time (ET)", color="white")
    ax.set_ylabel("Price", color="white")
    ax.set_title(
        args.title
        or f"BM_MNQ Bookmap v1.3 | {args.symbol or 'ALL'} | {args.trade_date} "
           f"{args.start_time}-{args.end_time} | {args.scale} | {args.center_mode}",
        color="white",
    )

    for spine in ax.spines.values():
        spine.set_color("white")
    ax.tick_params(axis="x", colors="white")
    ax.tick_params(axis="y", colors="white")

    if master_times:
        step = choose_tick_step(len(master_times))
        xticks = list(range(0, len(master_times), step))
        ax.set_xticks(xticks)
        ax.set_xticklabels([master_times[i].strftime("%H:%M:%S") for i in xticks], rotation=45, ha="right", color="white")

    if master_prices:
        if len(master_prices) <= 20:
            ytick_idx = list(range(len(master_prices)))
        else:
            step = max(1, len(master_prices) // 18)
            ytick_idx = list(range(0, len(master_prices), step))
            if (len(master_prices) - 1) not in ytick_idx:
                ytick_idx.append(len(master_prices) - 1)
        ax.set_yticks(ytick_idx)
        ax.set_yticklabels([f"{master_prices[i]:.2f}" for i in ytick_idx], color="white")

    if args.show_grid:
        ax.grid(color="white", alpha=0.12)

    plt.tight_layout()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(output_path, dpi=args.dpi, bbox_inches="tight", facecolor=fig.get_facecolor())
    plt.close(fig)


def main() -> int:
    args = parse_args()
    table_name = validate_identifier(f"{args.table_prefix}{args.scale}")
    output_path = build_output_path(args)

    cfg = build_ch_config()
    print("Connecting to ClickHouse:")
    print(f"  host={cfg.host} port={cfg.port} user={cfg.user} db={cfg.database} secure={cfg.secure}")

    client = get_client(cfg)
    try:
        df = query_frame_data(client, table_name, args)
    finally:
        try:
            client.close()
        except Exception:
            pass

    if df.empty:
        print("No rows returned for requested window.", file=sys.stderr)
        return 1

    df["ts_et"] = pd.to_datetime(df["ts_et"])
    for col in [
        "price",
        "total_liquidity_event_size",
        "heatmap_proxy_value",
        "heatmap_intensity",
        "buy_exec_size",
        "sell_exec_size",
        "total_exec_size",
        "exec_delta",
        "exec_imbalance",
    ]:
        df[col] = pd.to_numeric(df[col], errors="coerce").fillna(0)

    # Compute viewport center and bounds
    center_price = compute_viewport_center(df, args.center_mode)
    min_price, max_price = filter_viewport_prices(df, center_price, args.price_window_points)

    print(f"\n=== Viewport Configuration ===")
    print(f"Center mode:        {args.center_mode}")
    print(f"Center price:       {center_price:.2f}")
    print(f"Window points:      ±{args.price_window_points}")
    print(f"Price range:        {min_price:.2f} to {max_price:.2f}")
    print(f"Persistence alpha:  {args.persistence_alpha:.3f}")

    heatmap_df, aggression_df = split_layers(df)

    if args.print_head > 0:
        print("\n=== Sample Data ===")
        print(df.head(args.print_head).to_string(index=False))

    matrix, times, prices, positive_cells = prepare_heatmap_matrix_with_persistence(
        heatmap_df,
        heatmap_field=args.heatmap_field,
        lower_q=args.heatmap_lower_quantile,
        upper_q=args.heatmap_upper_quantile,
        gamma=args.heatmap_gamma,
        persistence_alpha=args.persistence_alpha,
        min_price=min_price,
        max_price=max_price,
    )

    bubbles = prepare_bubbles(aggression_df, min_price, max_price)

    print(f"\n=== BM_MNQ_render_bookmap_frame_v1_3 Summary ===")
    print(f"Source table:                       {table_name}")
    print(f"Rows queried (full):                {len(df):,}")
    print(f"Heatmap rows (full):                {len(heatmap_df):,}")
    print(f"Aggression rows (full):             {len(aggression_df):,}")
    print(f"Positive heatmap cells (viewport):  {positive_cells:,}")
    print(f"Time buckets rendered:              {len(times):,}")
    print(f"Price levels rendered:              {len(prices):,}")
    print(f"Aggression volume (viewport):       {float(bubbles['total_exec_size'].sum()) if not bubbles.empty else 0:,.0f}")
    print(f"Heatmap field:                      {args.heatmap_field}")

    if len(prices) > 300:
        print(
            f"\nWARNING: Rendered {len(prices)} price levels. Consider reducing --price-window-points for better focus.",
            file=sys.stderr,
        )

    if positive_cells == 0:
        print(
            "\nWARNING: No positive heatmap cells in viewport. Adjust window or run diagnostics.",
            file=sys.stderr,
        )

    render(matrix, times, prices, bubbles, args, output_path)
    print(f"\nSaved PNG: {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
