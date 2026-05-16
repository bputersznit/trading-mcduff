#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# BM_MNQ_render_bookmap_frame_v1_1.py
# Generated: 2026-05-10 15:12:00 America/New_York
#
# Purpose:
#   Render a Bookmap-style static frame from the BM_MNQ Bookmap-emulation tables,
#   with a black background and higher-contrast heatmap stripes.
#
# What changed vs v1:
#   1. Black background by default.
#   2. Heatmap zeros are masked so empty space stays black.
#   3. Positive heatmap values are contrast-stretched by quantile, so the
#      horizontal liquidity stripes are visible instead of getting washed out.
#   4. Bubble defaults are reduced so they do not overwhelm the heatmap.
#
# Required Python packages:
#   pip install clickhouse-connect pandas numpy matplotlib
#
# Optional environment variables:
#   CH_HOST=localhost
#   CH_PORT=8123
#   CH_USER=default
#   CH_PASSWORD=
#   CH_DATABASE=default
#   CH_SECURE=false
#
# Example usage:
#   python BM_MNQ_render_bookmap_frame_v1_1.py \
#       --trade-date 2025-10-07 \
#       --start-time 09:30:00 \
#       --end-time 09:35:00 \
#       --scale 1S \
#       --symbol MNQZ5 \
#       --out ./BM_MNQ_2025-10-07_093000_093500_1S_v1_1.png
#
# Strategy / methodology notes:
#   1. Query BM_MNQ_FRAME_SOURCE_<scale> for the selected window.
#   2. Split rows into heatmap rows and aggression rows.
#   3. Build a time x price heatmap matrix from max heatmap_intensity per cell.
#   4. Contrast-stretch the positive heatmap cells using quantiles.
#   5. Overlay open aggression circles.

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


# -----------------------------------------------------------------------------
# CLI
# -----------------------------------------------------------------------------

def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Render a Bookmap-style static frame from BM_MNQ_FRAME_SOURCE_* tables."
    )
    parser.add_argument("--trade-date", default="2025-10-07", help="Trade date in YYYY-MM-DD.")
    parser.add_argument("--start-time", default="09:30:00", help="Start time in HH:MM:SS ET.")
    parser.add_argument("--end-time", default="09:35:00", help="End time in HH:MM:SS ET.")
    parser.add_argument(
        "--scale",
        default="1S",
        choices=["1S", "5S", "30S", "1M", "5M"],
        help="BM_MNQ frame source scale to render.",
    )
    parser.add_argument(
        "--symbol",
        default="",
        help="Optional exact symbol filter. Leave empty to auto-detect if only one symbol exists.",
    )
    parser.add_argument(
        "--table-prefix",
        default="BM_MNQ_FRAME_SOURCE_",
        help="Table prefix. Default targets BM_MNQ_FRAME_SOURCE_<scale>.",
    )
    parser.add_argument(
        "--out",
        default="",
        help="Output PNG path. If omitted, a filename is generated in the current directory.",
    )
    parser.add_argument(
        "--title",
        default="",
        help="Optional custom chart title.",
    )
    parser.add_argument("--dpi", type=int, default=180, help="Output DPI.")
    parser.add_argument("--fig-width", type=float, default=16.0, help="Figure width in inches.")
    parser.add_argument("--fig-height", type=float, default=9.0, help="Figure height in inches.")

    # Bubble controls.
    parser.add_argument(
        "--bubble-area-q95",
        type=float,
        default=180.0,
        help="Target scatter area (points^2) for the 95th percentile bubble size.",
    )
    parser.add_argument(
        "--bubble-min-area",
        type=float,
        default=8.0,
        help="Minimum bubble area in points^2.",
    )
    parser.add_argument(
        "--bubble-max-area",
        type=float,
        default=700.0,
        help="Maximum bubble area in points^2.",
    )
    parser.add_argument(
        "--bubble-alpha",
        type=float,
        default=0.80,
        help="Bubble edge alpha.",
    )

    # Heatmap contrast controls.
    parser.add_argument(
        "--heatmap-lower-quantile",
        type=float,
        default=0.70,
        help=(
            "Lower quantile for contrast stretch of positive heatmap cells. "
            "Higher values emphasize only stronger stripes."
        ),
    )
    parser.add_argument(
        "--heatmap-upper-quantile",
        type=float,
        default=0.995,
        help="Upper quantile for contrast stretch of positive heatmap cells.",
    )
    parser.add_argument(
        "--heatmap-gamma",
        type=float,
        default=0.65,
        help="Gamma applied after contrast stretch. <1 brightens the visible stripes.",
    )
    parser.add_argument(
        "--show-grid",
        action="store_true",
        help="Overlay a faint axis grid for debugging / inspection.",
    )
    parser.add_argument(
        "--print-head",
        type=int,
        default=0,
        help="If > 0, print the first N queried rows for inspection.",
    )
    return parser.parse_args()


# -----------------------------------------------------------------------------
# ClickHouse helpers
# -----------------------------------------------------------------------------

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


# -----------------------------------------------------------------------------
# Query + transform
# -----------------------------------------------------------------------------

def build_output_path(args: argparse.Namespace) -> Path:
    if args.out:
        return Path(args.out)
    stem = (
        f"BM_MNQ_{args.trade_date}_{args.start_time.replace(':', '')}_"
        f"{args.end_time.replace(':', '')}_{args.scale}_v1_1.png"
    )
    return Path.cwd() / stem


def query_frame_data(
    client,
    table_name: str,
    trade_date: str,
    start_time: str,
    end_time: str,
    symbol: str,
) -> pd.DataFrame:
    start_ts = f"{trade_date} {start_time}"
    end_ts = f"{trade_date} {end_time}"

    symbol_clause = ""
    if symbol:
        symbol_safe = symbol.replace("'", "''")
        symbol_clause = f"\n  AND symbol = '{symbol_safe}'"

    query = f"""
    SELECT
        trade_date AS trade_date,
        ts_bucket AS ts_bucket,
        ts_et AS ts_et,
        symbol AS symbol,
        price AS price,
        total_liquidity_event_size AS total_liquidity_event_size,
        heatmap_intensity AS heatmap_intensity,
        buy_exec_size AS buy_exec_size,
        sell_exec_size AS sell_exec_size,
        total_exec_size AS total_exec_size,
        exec_delta AS exec_delta,
        exec_imbalance AS exec_imbalance
    FROM {table_name}
    WHERE trade_date = toDate('{trade_date}')
      AND ts_et >= toDateTime64('{start_ts}', 3, 'America/New_York')
      AND ts_et <  toDateTime64('{end_ts}', 3, 'America/New_York'){symbol_clause}
    ORDER BY ts_et, price
    """
    return client.query_df(query)


def resolve_symbol(df: pd.DataFrame, requested_symbol: str) -> Tuple[pd.DataFrame, str]:
    if requested_symbol:
        if df.empty:
            return df, requested_symbol
        filtered = df.loc[df["symbol"] == requested_symbol].copy()
        return filtered, requested_symbol

    symbols = sorted(df["symbol"].dropna().astype(str).unique().tolist())
    if len(symbols) == 0:
        return df, ""
    if len(symbols) == 1:
        symbol = symbols[0]
        return df.loc[df["symbol"] == symbol].copy(), symbol

    raise ValueError(
        "Multiple symbols found in the requested window. "
        f"Please rerun with --symbol. Symbols found: {symbols}"
    )


def split_layers(df: pd.DataFrame) -> Tuple[pd.DataFrame, pd.DataFrame]:
    heatmap_mask = (df["total_liquidity_event_size"].fillna(0) > 0) | (df["heatmap_intensity"].fillna(0) > 0)
    aggression_mask = df["total_exec_size"].fillna(0) > 0

    heatmap_df = df.loc[heatmap_mask].copy()
    aggression_df = df.loc[aggression_mask].copy()
    return heatmap_df, aggression_df


def prepare_heatmap_matrix(heatmap_df: pd.DataFrame) -> Tuple[np.ndarray, List[pd.Timestamp], List[float]]:
    if heatmap_df.empty:
        return np.zeros((1, 1), dtype=float), [], []

    grouped = (
        heatmap_df.groupby(["ts_et", "price"], as_index=False)["heatmap_intensity"]
        .max()
        .sort_values(["ts_et", "price"])
    )

    time_values = sorted(pd.to_datetime(grouped["ts_et"]).unique().tolist())
    price_values = sorted(pd.to_numeric(grouped["price"]).unique().tolist())

    time_index: Dict[pd.Timestamp, int] = {pd.Timestamp(t): i for i, t in enumerate(time_values)}
    price_index: Dict[float, int] = {float(p): i for i, p in enumerate(price_values)}

    matrix = np.zeros((len(price_values), len(time_values)), dtype=float)
    for row in grouped.itertuples(index=False):
        t = pd.Timestamp(row.ts_et)
        p = float(row.price)
        matrix[price_index[p], time_index[t]] = max(0.0, min(1.0, float(row.heatmap_intensity)))

    return matrix, [pd.Timestamp(t) for t in time_values], [float(p) for p in price_values]


def contrast_stretch_heatmap(
    matrix: np.ndarray,
    lower_quantile: float,
    upper_quantile: float,
    gamma: float,
) -> np.ma.MaskedArray:
    positive = matrix[matrix > 0]
    if positive.size == 0:
        return np.ma.masked_where(matrix <= 0, matrix)

    q_low = float(np.quantile(positive, lower_quantile))
    q_high = float(np.quantile(positive, upper_quantile))

    # Safety guards.
    if not np.isfinite(q_low):
        q_low = float(np.min(positive))
    if not np.isfinite(q_high):
        q_high = float(np.max(positive))
    if q_high <= q_low:
        q_low = float(np.min(positive))
        q_high = float(np.max(positive))
    if q_high <= q_low:
        stretched = np.where(matrix > 0, 1.0, 0.0)
        return np.ma.masked_where(stretched <= 0, stretched)

    stretched = (matrix - q_low) / (q_high - q_low)
    stretched = np.clip(stretched, 0.0, 1.0)
    stretched = np.where(matrix > 0, stretched, 0.0)

    if gamma > 0 and gamma != 1.0:
        stretched = np.where(stretched > 0, np.power(stretched, gamma), 0.0)

    return np.ma.masked_where(stretched <= 0, stretched)


def prepare_bubbles(aggression_df: pd.DataFrame) -> pd.DataFrame:
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


def compute_bubble_sizes(
    bubbles: pd.DataFrame,
    bubble_area_q95: float,
    bubble_min_area: float,
    bubble_max_area: float,
) -> np.ndarray:
    if bubbles.empty:
        return np.array([], dtype=float)

    q95 = float(np.percentile(bubbles["total_exec_size"], 95)) if len(bubbles) > 0 else 0.0
    if q95 <= 0:
        scale = 1.0
    else:
        scale = bubble_area_q95 / q95

    sizes = bubbles["total_exec_size"].to_numpy(dtype=float) * scale
    sizes = np.clip(sizes, bubble_min_area, bubble_max_area)
    return sizes


def compute_linewidths(bubbles: pd.DataFrame) -> np.ndarray:
    if bubbles.empty:
        return np.array([], dtype=float)
    imbalance = bubbles["exec_imbalance"].abs().clip(lower=0.0, upper=1.0).to_numpy(dtype=float)
    return 0.6 + (1.8 * imbalance)


def choose_time_tick_step(n_times: int) -> int:
    if n_times <= 20:
        return 1
    if n_times <= 60:
        return 5
    if n_times <= 180:
        return 10
    if n_times <= 360:
        return 20
    return max(1, n_times // 20)


# -----------------------------------------------------------------------------
# Render
# -----------------------------------------------------------------------------

def render_chart(
    heatmap_matrix: np.ndarray,
    heatmap_times: List[pd.Timestamp],
    heatmap_prices: List[float],
    bubbles: pd.DataFrame,
    symbol: str,
    args: argparse.Namespace,
    output_path: Path,
) -> None:
    fig, ax = plt.subplots(figsize=(args.fig_width, args.fig_height))

    # Black Bookmap-style background.
    fig.patch.set_facecolor("black")
    ax.set_facecolor("black")

    has_heatmap = len(heatmap_times) > 0 and len(heatmap_prices) > 0
    has_bubbles = not bubbles.empty

    if not has_heatmap and not has_bubbles:
        raise RuntimeError("Nothing to render: both heatmap and aggression layers are empty.")

    # Determine master axes.
    if has_heatmap:
        master_times = heatmap_times
        master_prices = heatmap_prices
    else:
        master_times = sorted(pd.to_datetime(bubbles["ts_et"]).unique().tolist())
        master_prices = sorted(pd.to_numeric(bubbles["price"]).unique().tolist())

    time_pos = {pd.Timestamp(t): i for i, t in enumerate(master_times)}
    price_pos = {float(p): i for i, p in enumerate(master_prices)}

    if has_heatmap:
        display_matrix = contrast_stretch_heatmap(
            heatmap_matrix,
            lower_quantile=args.heatmap_lower_quantile,
            upper_quantile=args.heatmap_upper_quantile,
            gamma=args.heatmap_gamma,
        )

        ax.imshow(
            display_matrix,
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
        bubbles["x"] = bubbles["ts_et"].map(lambda x: time_pos[pd.Timestamp(x)])
        bubbles["y"] = bubbles["price"].astype(float).map(price_pos)
        bubbles = bubbles.dropna(subset=["x", "y"]).copy()

        sizes = compute_bubble_sizes(
            bubbles,
            bubble_area_q95=args.bubble_area_q95,
            bubble_min_area=args.bubble_min_area,
            bubble_max_area=args.bubble_max_area,
        )
        linewidths = compute_linewidths(bubbles)

        for edge_color in ["#00ff66", "#ff2a2a"]:
            sub = bubbles.loc[bubbles["edge_color"] == edge_color].copy()
            if sub.empty:
                continue
            idx = sub.index.to_numpy()
            ax.scatter(
                sub["x"].to_numpy(dtype=float),
                sub["y"].to_numpy(dtype=float),
                s=sizes[idx],
                facecolors="none",
                edgecolors=edge_color,
                linewidths=linewidths[idx],
                alpha=args.bubble_alpha,
                zorder=2,
            )

    # White-on-black styling.
    ax.set_xlabel("Time (ET)", color="white")
    ax.set_ylabel("Price", color="white")
    for spine in ax.spines.values():
        spine.set_color("white")
    ax.tick_params(axis="x", colors="white")
    ax.tick_params(axis="y", colors="white")

    # X ticks.
    tick_step = choose_time_tick_step(len(master_times))
    xticks = list(range(0, len(master_times), tick_step))
    xlabels = [pd.Timestamp(master_times[i]).strftime("%H:%M:%S") for i in xticks]
    ax.set_xticks(xticks)
    ax.set_xticklabels(xlabels, rotation=45, ha="right", color="white")

    # Y ticks.
    if len(master_prices) <= 20:
        ytick_idx = list(range(len(master_prices)))
    else:
        ytick_step = max(1, len(master_prices) // 18)
        ytick_idx = list(range(0, len(master_prices), ytick_step))
        if (len(master_prices) - 1) not in ytick_idx:
            ytick_idx.append(len(master_prices) - 1)
    ax.set_yticks(ytick_idx)
    ax.set_yticklabels([f"{master_prices[i]:.2f}" for i in ytick_idx], color="white")

    title = args.title.strip() if args.title else (
        f"BM_MNQ Bookmap Frame | {symbol or 'ALL'} | {args.trade_date} "
        f"{args.start_time}-{args.end_time} ET | {args.scale}"
    )
    ax.set_title(title, color="white")

    if args.show_grid:
        ax.grid(alpha=0.12, color="white")

    # Summary textbox.
    total_bubble_vol = float(bubbles["total_exec_size"].sum()) if has_bubbles else 0.0
    buy_bubble_vol = float(bubbles["buy_exec_size"].sum()) if has_bubbles else 0.0
    sell_bubble_vol = float(bubbles["sell_exec_size"].sum()) if has_bubbles else 0.0
    heat_rows = int((heatmap_matrix > 0).sum()) if has_heatmap else 0
    summary_lines = [
        f"Heatmap cells > 0: {heat_rows:,}",
        f"Aggression volume: {total_bubble_vol:,.0f}",
        f"Buy volume: {buy_bubble_vol:,.0f}",
        f"Sell volume: {sell_bubble_vol:,.0f}",
        f"Heatmap q-range: {args.heatmap_lower_quantile:.3f} - {args.heatmap_upper_quantile:.3f}",
    ]
    ax.text(
        0.995,
        0.01,
        "\n".join(summary_lines),
        transform=ax.transAxes,
        ha="right",
        va="bottom",
        fontsize=9,
        color="white",
        bbox=dict(facecolor="black", alpha=0.70, edgecolor="white"),
    )

    plt.tight_layout()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(output_path, dpi=args.dpi, bbox_inches="tight", facecolor=fig.get_facecolor())
    plt.close(fig)


# -----------------------------------------------------------------------------
# Diagnostics
# -----------------------------------------------------------------------------

def print_summary(df: pd.DataFrame, heatmap_df: pd.DataFrame, aggression_df: pd.DataFrame, symbol: str, table_name: str) -> None:
    print("=== BM_MNQ_render_bookmap_frame_v1_1 summary ===")
    print(f"Source table:        {table_name}")
    print(f"Rows queried:        {len(df):,}")
    print(f"Symbol:              {symbol or 'ALL'}")
    print(f"Heatmap rows:        {len(heatmap_df):,}")
    print(f"Aggression rows:     {len(aggression_df):,}")
    if not aggression_df.empty:
        print(f"Aggression volume:   {aggression_df['total_exec_size'].sum():,.0f}")
        print(f"Buy exec size:       {aggression_df['buy_exec_size'].sum():,.0f}")
        print(f"Sell exec size:      {aggression_df['sell_exec_size'].sum():,.0f}")
    if not heatmap_df.empty:
        print(f"Max heat intensity:  {heatmap_df['heatmap_intensity'].max():.6f}")
        print(f"Positive heat cells: {int((heatmap_df['heatmap_intensity'] > 0).sum()):,}")
        print(f"Price levels:        {heatmap_df['price'].nunique():,}")
        print(f"Time buckets:        {heatmap_df['ts_et'].nunique():,}")


# -----------------------------------------------------------------------------
# Main
# -----------------------------------------------------------------------------

def main() -> int:
    args = parse_args()
    table_name = validate_identifier(f"{args.table_prefix}{args.scale}")
    output_path = build_output_path(args)

    cfg = build_ch_config()
    print("Connecting to ClickHouse:")
    print(f"  host={cfg.host} port={cfg.port} user={cfg.user} db={cfg.database} secure={cfg.secure}")

    client = get_client(cfg)
    try:
        df = query_frame_data(
            client=client,
            table_name=table_name,
            trade_date=args.trade_date,
            start_time=args.start_time,
            end_time=args.end_time,
            symbol=args.symbol,
        )
    finally:
        try:
            client.close()
        except Exception:
            pass

    if df.empty:
        print("No data returned for the requested window.", file=sys.stderr)
        return 1

    # Normalize dtypes.
    df["ts_et"] = pd.to_datetime(df["ts_et"])
    numeric_cols = [
        "price",
        "total_liquidity_event_size",
        "heatmap_intensity",
        "buy_exec_size",
        "sell_exec_size",
        "total_exec_size",
        "exec_delta",
        "exec_imbalance",
    ]
    for col in numeric_cols:
        if col in df.columns:
            df[col] = pd.to_numeric(df[col], errors="coerce").fillna(0)

    df, resolved_symbol = resolve_symbol(df, args.symbol)
    if df.empty:
        print("No rows remain after symbol resolution/filtering.", file=sys.stderr)
        return 1

    heatmap_df, aggression_df = split_layers(df)

    if args.print_head > 0:
        print(df.head(args.print_head).to_string(index=False))

    print_summary(df, heatmap_df, aggression_df, resolved_symbol, table_name)

    heatmap_matrix, heatmap_times, heatmap_prices = prepare_heatmap_matrix(heatmap_df)
    bubbles = prepare_bubbles(aggression_df)

    render_chart(
        heatmap_matrix=heatmap_matrix,
        heatmap_times=heatmap_times,
        heatmap_prices=heatmap_prices,
        bubbles=bubbles,
        symbol=resolved_symbol,
        args=args,
        output_path=output_path,
    )

    print(f"Saved PNG: {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
