#!/usr/bin/env python3
"""
CG_mnq_hierarchical_replay_v1.py
Generated: 2026-05-09 09:30:00 America/New_York

Purpose:
Chronological L1 replay for MNQ hierarchical 5m/1m strategy.

Important timestamp rule:
- CSV signal times are exported as naive America/New_York local time.
- mnq_trades.ts_event is UTC.
- This script converts CSV times from NY -> UTC.
- This script forces ClickHouse tick timestamps to UTC strings before parsing.

Execution assumptions:
- MNQ tick size = 0.25
- MNQ tick value = $0.50
- One MNQ contract only
- No overlapping positions
- Fixed conservative slippage:
    entry = 2 ticks
    exit = 2 ticks
- Commission = $0.70 round turn

Outputs:
- CG_mnq_hierarchical_replay_results_v1.csv
- CG_mnq_hierarchical_replay_daily_v1.csv
"""

import csv
import os
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from collections import defaultdict
from zoneinfo import ZoneInfo

import clickhouse_connect


TICK_SIZE = 0.25
TICK_VALUE_USD = 0.50
COMMISSION_RT_USD = 0.70

ENTRY_SLIP_TICKS = 2
EXIT_SLIP_TICKS = 2

SIGNAL_CSV = "CG_mnq_hierarchical_replay_seed_v1.csv"
OUT_TRADES_CSV = "CG_mnq_hierarchical_replay_results_v1.csv"
OUT_DAILY_CSV = "CG_mnq_hierarchical_replay_daily_v1.csv"

NY_TZ = ZoneInfo("America/New_York")


@dataclass
class Signal:
    trade_date: str
    signal_time: datetime
    entry_time: datetime
    side: str
    raw_entry_price: float
    target_ticks: int
    stop_ticks: int
    timeout_seconds: int


@dataclass
class TradeResult:
    trade_date: str
    signal_time: datetime
    entry_time: datetime
    exit_time: datetime
    side: str
    entry_price: float
    exit_price: float
    outcome: str
    gross_ticks: float
    net_usd: float
    hold_seconds: float


def ensure_utc(dt: datetime) -> datetime:
    if dt.tzinfo is None:
        return dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def parse_dt(value: str) -> datetime:
    """
    Seed CSV times are naive America/New_York local timestamps.
    """
    value = value.strip().replace("Z", "")
    dt = datetime.fromisoformat(value)

    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=NY_TZ)

    return dt.astimezone(timezone.utc)


def parse_utc_string(value: str) -> datetime:
    """
    Parse ClickHouse UTC timestamp strings emitted by formatDateTime(..., 'UTC').
    """
    value = value.strip()
    dt = datetime.strptime(value, "%Y-%m-%d %H:%M:%S.%f")
    return dt.replace(tzinfo=timezone.utc)


def load_signals(path: str) -> list[Signal]:
    signals: list[Signal] = []

    with open(path, newline="") as f:
        reader = csv.DictReader(f)

        for row in reader:
            signals.append(
                Signal(
                    trade_date=row["trade_date"],
                    signal_time=parse_dt(row["signal_time"]),
                    entry_time=parse_dt(row["entry_time"]),
                    side=row["signal_side"],
                    raw_entry_price=float(row["raw_entry_price"]),
                    target_ticks=int(row["target_ticks"]),
                    stop_ticks=int(row["stop_ticks"]),
                    timeout_seconds=int(row["timeout_seconds"]),
                )
            )

    return sorted(signals, key=lambda s: s.entry_time)


def get_client():
    return clickhouse_connect.get_client(
        host=os.getenv("CH_HOST", "localhost"),
        port=int(os.getenv("CH_PORT", "8123")),
        username=os.getenv("CH_USER", "default"),
        password=os.getenv("CH_PASSWORD", ""),
        database=os.getenv("CH_DATABASE", "default"),
    )


def make_result(
    sig: Signal,
    exit_time: datetime,
    entry_price: float,
    exit_price: float,
    outcome: str,
    gross_ticks: float,
) -> TradeResult:
    exit_time = ensure_utc(exit_time)
    entry_time = ensure_utc(sig.entry_time)

    hold_seconds = (exit_time - entry_time).total_seconds()
    net_usd = gross_ticks * TICK_VALUE_USD - COMMISSION_RT_USD

    return TradeResult(
        trade_date=sig.trade_date,
        signal_time=sig.signal_time,
        entry_time=sig.entry_time,
        exit_time=exit_time,
        side=sig.side,
        entry_price=entry_price,
        exit_price=exit_price,
        outcome=outcome,
        gross_ticks=gross_ticks,
        net_usd=net_usd,
        hold_seconds=hold_seconds,
    )


def replay_one_signal(client, sig: Signal) -> TradeResult | None:
    replay_end = sig.entry_time + timedelta(seconds=sig.timeout_seconds)

    rows = client.query(
        """
        SELECT
            formatDateTime(ts_event, '%%Y-%%m-%%d %%H:%%i:%%S.%%f', 'UTC') AS ts_event_utc,
            price
        FROM mnq_trades
        WHERE
            symbol LIKE 'MNQ%%'
            AND ts_event >= %(start)s
            AND ts_event <= %(end)s
        ORDER BY ts_event
        """,
        parameters={
            "start": sig.entry_time,
            "end": replay_end,
        },
    ).result_rows

    if not rows:
        return None

    first_ts_raw, first_price_raw = rows[0]
    first_ts = parse_utc_string(first_ts_raw)
    first_price = float(first_price_raw)

    if sig.side == "LONG":
        entry_price = first_price + ENTRY_SLIP_TICKS * TICK_SIZE
        target_price = entry_price + sig.target_ticks * TICK_SIZE
        stop_price = entry_price - sig.stop_ticks * TICK_SIZE

    elif sig.side == "SHORT":
        entry_price = first_price - ENTRY_SLIP_TICKS * TICK_SIZE
        target_price = entry_price - sig.target_ticks * TICK_SIZE
        stop_price = entry_price + sig.stop_ticks * TICK_SIZE

    else:
        return None

    last_ts = first_ts
    last_price = first_price

    for ts_raw, px_raw in rows:
        ts = parse_utc_string(ts_raw)
        px = float(px_raw)

        last_ts = ts
        last_price = px

        if sig.side == "LONG":
            if px <= stop_price:
                exit_price = stop_price - EXIT_SLIP_TICKS * TICK_SIZE
                gross_ticks = (exit_price - entry_price) / TICK_SIZE
                return make_result(sig, ts, entry_price, exit_price, "STOP", gross_ticks)

            if px >= target_price:
                exit_price = target_price - EXIT_SLIP_TICKS * TICK_SIZE
                gross_ticks = (exit_price - entry_price) / TICK_SIZE
                return make_result(sig, ts, entry_price, exit_price, "TARGET", gross_ticks)

        elif sig.side == "SHORT":
            if px >= stop_price:
                exit_price = stop_price + EXIT_SLIP_TICKS * TICK_SIZE
                gross_ticks = (entry_price - exit_price) / TICK_SIZE
                return make_result(sig, ts, entry_price, exit_price, "STOP", gross_ticks)

            if px <= target_price:
                exit_price = target_price + EXIT_SLIP_TICKS * TICK_SIZE
                gross_ticks = (entry_price - exit_price) / TICK_SIZE
                return make_result(sig, ts, entry_price, exit_price, "TARGET", gross_ticks)

    if sig.side == "LONG":
        exit_price = last_price - EXIT_SLIP_TICKS * TICK_SIZE
        gross_ticks = (exit_price - entry_price) / TICK_SIZE

    else:
        exit_price = last_price + EXIT_SLIP_TICKS * TICK_SIZE
        gross_ticks = (entry_price - exit_price) / TICK_SIZE

    return make_result(sig, last_ts, entry_price, exit_price, "TIMEOUT", gross_ticks)


def write_trades(results: list[TradeResult]) -> None:
    with open(OUT_TRADES_CSV, "w", newline="") as f:
        writer = csv.writer(f)

        writer.writerow(
            [
                "trade_date",
                "signal_time",
                "entry_time",
                "exit_time",
                "side",
                "entry_price",
                "exit_price",
                "outcome",
                "gross_ticks",
                "net_usd",
                "hold_seconds",
            ]
        )

        for r in results:
            writer.writerow(
                [
                    r.trade_date,
                    r.signal_time.isoformat(),
                    r.entry_time.isoformat(),
                    r.exit_time.isoformat(),
                    r.side,
                    round(r.entry_price, 2),
                    round(r.exit_price, 2),
                    r.outcome,
                    round(r.gross_ticks, 2),
                    round(r.net_usd, 2),
                    round(r.hold_seconds, 3),
                ]
            )


def write_daily(results: list[TradeResult]) -> None:
    grouped = defaultdict(list)

    for r in results:
        grouped[r.trade_date].append(r)

    with open(OUT_DAILY_CSV, "w", newline="") as f:
        writer = csv.writer(f)

        writer.writerow(
            [
                "trade_date",
                "trades",
                "net_usd",
                "expectancy_usd",
                "targets",
                "stops",
                "timeouts",
                "target_rate",
                "avg_hold_seconds",
            ]
        )

        for trade_date in sorted(grouped):
            rows = grouped[trade_date]
            total = sum(r.net_usd for r in rows)
            targets = sum(1 for r in rows if r.outcome == "TARGET")
            stops = sum(1 for r in rows if r.outcome == "STOP")
            timeouts = sum(1 for r in rows if r.outcome == "TIMEOUT")
            avg_hold = sum(r.hold_seconds for r in rows) / len(rows)

            writer.writerow(
                [
                    trade_date,
                    len(rows),
                    round(total, 2),
                    round(total / len(rows), 2),
                    targets,
                    stops,
                    timeouts,
                    round(targets / len(rows), 4),
                    round(avg_hold, 3),
                ]
            )


def print_summary(results: list[TradeResult], skipped_overlap: int, skipped_no_ticks: int) -> None:
    if not results:
        print("No replay results generated.")
        print(f"Skipped overlap: {skipped_overlap}")
        print(f"Skipped no ticks: {skipped_no_ticks}")
        return

    total_net = sum(r.net_usd for r in results)
    targets = sum(1 for r in results if r.outcome == "TARGET")
    stops = sum(1 for r in results if r.outcome == "STOP")
    timeouts = sum(1 for r in results if r.outcome == "TIMEOUT")
    avg_hold = sum(r.hold_seconds for r in results) / len(results)

    longs = [r for r in results if r.side == "LONG"]
    shorts = [r for r in results if r.side == "SHORT"]

    print("\n=== CG MNQ Hierarchical Replay Results ===")
    print(f"Trades: {len(results)}")
    print(f"Skipped overlap: {skipped_overlap}")
    print(f"Skipped no ticks: {skipped_no_ticks}")
    print(f"Total net USD: {total_net:.2f}")
    print(f"Expectancy USD: {total_net / len(results):.2f}")
    print(f"Targets: {targets}")
    print(f"Stops: {stops}")
    print(f"Timeouts: {timeouts}")
    print(f"Target rate: {targets / len(results):.4f}")
    print(f"Stop rate: {stops / len(results):.4f}")
    print(f"Timeout rate: {timeouts / len(results):.4f}")
    print(f"Average hold seconds: {avg_hold:.3f}")

    if longs:
        long_net = sum(r.net_usd for r in longs)
        print(f"LONG trades: {len(longs)} | net: {long_net:.2f} | exp: {long_net / len(longs):.2f}")

    if shorts:
        short_net = sum(r.net_usd for r in shorts)
        print(f"SHORT trades: {len(shorts)} | net: {short_net:.2f} | exp: {short_net / len(shorts):.2f}")

    print(f"\nWrote: {OUT_TRADES_CSV}")
    print(f"Wrote: {OUT_DAILY_CSV}")


def main() -> None:
    print("=== CG MNQ Hierarchical Replay v1 ===")

    if not os.path.exists(SIGNAL_CSV):
        raise FileNotFoundError(f"Missing file: {SIGNAL_CSV}")

    signals = load_signals(SIGNAL_CSV)
    print(f"Loaded signals: {len(signals)}")

    client = get_client()

    version_row = client.query(
        "SELECT version(), currentUser(), currentDatabase()"
    ).result_rows[0]

    print(
        f"ClickHouse OK: version={version_row[0]} "
        f"user={version_row[1]} db={version_row[2]}"
    )

    results: list[TradeResult] = []
    locked_until = datetime(1970, 1, 1, tzinfo=timezone.utc)

    skipped_overlap = 0
    skipped_no_ticks = 0

    for idx, sig in enumerate(signals, start=1):
        if sig.entry_time <= locked_until:
            skipped_overlap += 1
            continue

        result = replay_one_signal(client, sig)

        if result is None:
            skipped_no_ticks += 1
            continue

        results.append(result)
        locked_until = result.exit_time

        if idx % 100 == 0:
            print(f"Processed {idx}/{len(signals)} | accepted={len(results)}")

    write_trades(results)
    write_daily(results)

    print_summary(
        results=results,
        skipped_overlap=skipped_overlap,
        skipped_no_ticks=skipped_no_ticks,
    )


if __name__ == "__main__":
    main()
