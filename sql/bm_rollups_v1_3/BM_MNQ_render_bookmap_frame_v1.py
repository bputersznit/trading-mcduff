#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# BM_MNQ_render_bookmap_frame_v1.py
# Generated: 2026-05-10 14:50:00 America/New_York
#
# Purpose:
#   Render a Bookmap-style static frame from the BM_MNQ Bookmap-emulation tables.
#
# Default source:
#   BM_MNQ_FRAME_SOURCE_1S
#
# Default example window:
#   2025-10-07, 09:30:00 to 09:35:00 America/New_York
#
# Visual design:
#   - Heatmap layer: logarithmic grayscale intensity already normalized into
#     heatmap_intensity in [0, 1], rendered black -> white.
#   - Aggression layer: open circles only.
#       * Circle area is proportional to total_exec_size.
#       * Edge color is green for net-buy aggression and red for net-sell aggression.
#       * Edge line width is proportional to |exec_imbalance|.
#
# Notes:
#   - In the current BM_MNQ frame model, heatmap rows and aggression rows may be
#     stored as separate records at the same scale. That is acceptable for
#     rendering. This script independently builds:
#       1) a heatmap grid from rows with liquidity-event content, and
#       2) a bubble overlay from rows with aggression content.
#   - The script is intentionally written as a standalone audit-friendly file.
#
# Required Python packages:
#   pip install clickhouse-connect pandas numpy matplotlib
#
# Optional environment variables:
#   CH_HOST=localhost
#   CH_PORT=8123
#   CH_USER=default
#   CH_PASSWORD=unlucky-strange
#   CH_DATABASE=default
#   CH_SECURE=false
#
# Example usage:
#   python BM_MNQ_render_bookmap_frame_v1.py \
#       --trade-date 2025-10-07 \
#       --start-time 09:30:00 \
#       --end-time 09:35:00 \
#       --scale 1S \
#       --symbol MNQZ5 \
#       --out ./BM_MNQ_2025-10-07_093000_093500_1S.png
#
# Strategy / methodology notes:
#   1. Query the selected BM_MNQ_FRAME_SOURCE_<scale> table for the requested
#      trade_date/time window.
#   2. Split rows into two analytical layers:
#        - Heatmap rows: total_liquidity_event_size > 0 or heatmap_intensity > 0
#        - Aggression rows: total_exec_size > 0
#   3. Build a time x price matrix for the heatmap using max intensity per cell.
#   4. Aggregate aggression rows by (ts_et, price) and compute bubble sizes and
#      edge widths from total volume and imbalance.
#   5. Render with matplotlib only.

from __future__ import annotations

import argparse
import math
import os
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Tuple

import clickhouse_connect
import matplotlib.dates as mdates
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
    parser.add_argument(
        "--dpi",
        type=int,
        default=180,
        help="Output DPI.",
    )
    parser.add_argument(
        "--fig-width",
        type=float,
        default=16.0,
        help="Figure width in inches.",
    )
    parser.add_argument(
        "--fig-height",
        type=float,
        default=9.0,
        help="Figure height in inches.",
    )
    parser.add_argument(
        "--bubble-area-q95",
        type=float,
        default=500.0,
        help="Target scatter area (points^2) for the 95th percentile bubble size.",
    )
    parser.add_argument(
        "--bubble-min-area",
        type=float,
        default=12.0,
        help="Minimum bubble area in points^2.",
    )
    parser.add_argument(
        "--bubble-max-area",
        type=float,
        default=1400.0,
        help="Maximum bubble area in points^2.",
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
    stem = (
        f"BM_MNQ_{args.trade_date}_{args.start_time.replace(':', '')}_"
        f"{args.end_time.replace(':', '')}_{args.scale}.png"
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
        bid_add_size AS bid_add_size,
        ask_add_size AS ask_add_size,
        bid_cancel_size AS bid_cancel_size,
        ask_cancel_size AS ask_cancel_size,
        bid_modify_size AS bid_modify_size,
        ask_modify_size AS ask_modify_size,
        bid_trade_size AS bid_trade_size,
        ask_trade_size AS ask_trade_size,
        bid_event_count AS bid_event_count,
        ask_event_count AS ask_event_count,
        total_event_count AS total_event_count,
        bid_liquidity_event_size AS bid_liquidity_event_size,
        ask_liquidity_event_size AS ask_liquidity_event_size,
        total_liquidity_event_size AS total_liquidity_event_size,
        net_liquidity_event_delta AS net_liquidity_event_delta,
        heatmap_proxy_value AS heatmap_proxy_value,
        max_heatmap_proxy_value AS max_heatmap_proxy_value,
        avg_heatmap_proxy_value AS avg_heatmap_proxy_value,
        persistence_bucket_count AS persistence_bucket_count,
        daily_max_heatmap_proxy_value AS daily_max_heatmap_proxy_value,
        daily_p999_heatmap_proxy_value AS daily_p999_heatmap_proxy_value,
        heatmap_intensity AS heatmap_intensity,
        is_bid_liquidity_event_wall AS is_bid_liquidity_event_wall,
        is_ask_liquidity_event_wall AS is_ask_liquidity_event_wall,
        buy_exec_size AS buy_exec_size,
        sell_exec_size AS sell_exec_size,
        total_exec_size AS total_exec_size,
        exec_delta AS exec_delta,
        exec_imbalance AS exec_imbalance,
        buy_trade_count AS buy_trade_count,
        sell_trade_count AS sell_trade_count,
        total_trade_count AS total_trade_count,
        bubble_total_size AS bubble_total_size,
        bubble_buy_share AS bubble_buy_share,
        bubble_sell_share AS bubble_sell_share,
        bubble_side AS bubble_side,
        rth_flag AS rth_flag
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
    grouped["edge_color"] = np.where(grouped["exec_delta"] >= 0, "green", "red")
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
    return 0.8 + (2.4 * imbalance)


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

    has_heatmap = len(heatmap_times) > 0 and len(heatmap_prices) > 0
    has_bubbles = not bubbles.empty

    if not has_heatmap and not has_bubbles:
        raise RuntimeError("Nothing to render: both heatmap and aggression layers are empty.")

    # Determine master time axis and price axis.
    if has_heatmap:
        master_times = heatmap_times
        master_prices = heatmap_prices
    else:
        master_times = sorted(pd.to_datetime(bubbles["ts_et"]).unique().tolist())
        master_prices = sorted(pd.to_numeric(bubbles["price"]).unique().tolist())

    time_pos = {pd.Timestamp(t): i for i, t in enumerate(master_times)}
    price_pos = {float(p): i for i, p in enumerate(master_prices)}

    if has_heatmap:
        ax.imshow(
            heatmap_matrix,
            origin="lower",
            aspect="auto",
            interpolation="nearest",
            cmap="gray",
            vmin=0.0,
            vmax=1.0,
            extent=[-0.5, len(master_times) - 0.5, -0.5, len(master_prices) - 0.5],
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

        # Plot buy and sell circles separately to allow clean edge coloring.
        for edge_color in ["green", "red"]:
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
                alpha=0.95,
            )

    # Axis labels.
    ax.set_xlabel("Time (ET)")
    ax.set_ylabel("Price")

    # Time ticks.
    tick_step = choose_time_tick_step(len(master_times))
    xticks = list(range(0, len(master_times), tick_step))
    xlabels = [pd.Timestamp(master_times[i]).strftime("%H:%M:%S") for i in xticks]
    ax.set_xticks(xticks)
    ax.set_xticklabels(xlabels, rotation=45, ha="right")

    # Price ticks.
    if len(master_prices) <= 20:
        ytick_idx = list(range(len(master_prices)))
    else:
        ytick_step = max(1, len(master_prices) // 18)
        ytick_idx = list(range(0, len(master_prices), ytick_step))
        if (len(master_prices) - 1) not in ytick_idx:
            ytick_idx.append(len(master_prices) - 1)
    ax.set_yticks(ytick_idx)
    ax.set_yticklabels([f"{master_prices[i]:.2f}" for i in ytick_idx])

    title = args.title.strip() if args.title else (
        f"BM_MNQ Bookmap Frame | {symbol or 'ALL'} | {args.trade_date} "
        f"{args.start_time}-{args.end_time} ET | {args.scale}"
    )
    ax.set_title(title)

    if args.show_grid:
        ax.grid(alpha=0.15)

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
        "Heatmap: black→white | Bubbles: open green/red circles",
    ]
    ax.text(
        0.995,
        0.01,
        "\n".join(summary_lines),
        transform=ax.transAxes,
        ha="right",
        va="bottom",
        fontsize=9,
        bbox=dict(facecolor="white", alpha=0.75, edgecolor="black"),
    )

    plt.tight_layout()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(output_path, dpi=args.dpi, bbox_inches="tight")
    plt.close(fig)


def print_summary(df: pd.DataFrame, heatmap_df: pd.DataFrame, aggression_df: pd.DataFrame, symbol: str, table_name: str) -> None:
    print("=== BM_MNQ_render_bookmap_frame_v1 summary ===")
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
        print(f"Price levels:        {heatmap_df['price'].nunique():,}")
        print(f"Time buckets:        {heatmap_df['ts_et'].nunique():,}")


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
