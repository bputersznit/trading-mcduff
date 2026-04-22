#!/usr/bin/env python3
"""
Backtest Bull, Bear, and Swing strategies on real market days.

Tests the adaptive regime-based strategies developed earlier on actual
historical MBO data from ClickHouse.

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

import subprocess
import json
from dataclasses import dataclass
from collections import defaultdict

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_exec.CGCl_fill_model_strict import StrictFillModel
from cg_exec.CGCl_stop_limit_orders import StopOrderManager
from cg_exec.CGCl_oco_bracket_orders import OCOBracketManager
from cg_exec.CGCl_performance_metrics import PerformanceMetrics, Trade
from cg_exec.CGCl_order_flow_analysis import OrderFlowAnalyzer
from cg_sim.models import MarketEvent, EventType

F_LAST = 128


@dataclass
class RegimeParams:
    """Trading parameters for a regime."""
    allowed_sides: list[str]
    stop_ticks: int
    target_ticks: int | None
    trail_offset_ticks: int | None
    entry_type: str  # "market" or "limit"


def detect_regime(analyzer: OrderFlowAnalyzer) -> tuple[str, RegimeParams]:
    """
    Detect market regime and return appropriate parameters.

    Returns:
        (regime_name, parameters)
    """
    delta = analyzer.get_delta()
    delta_trend = analyzer.get_delta_trend(periods=20)
    signals = analyzer.get_recent_signals(last_n=20)
    sweeps = [s for s in signals if s.signal_type == "sweep"]

    # Classify regime
    if delta > 200 and delta_trend == "bullish" and len(sweeps) >= 3:
        regime = "BULL_TREND"
        params = RegimeParams(
            allowed_sides=["BUY"],
            stop_ticks=20,  # Wide stops
            target_ticks=None,  # No fixed target
            trail_offset_ticks=12,  # Trail by 12 ticks
            entry_type="market",
        )
    elif delta < -200 and delta_trend == "bearish" and len(sweeps) >= 3:
        regime = "BEAR_TREND"
        params = RegimeParams(
            allowed_sides=["SELL"],
            stop_ticks=20,
            target_ticks=None,
            trail_offset_ticks=12,
            entry_type="market",
        )
    else:
        regime = "CHOPPY"
        params = RegimeParams(
            allowed_sides=["BUY", "SELL"],
            stop_ticks=5,  # Tight stops
            target_ticks=10,  # Quick targets
            trail_offset_ticks=None,
            entry_type="limit",
        )

    return regime, params


def load_day_from_clickhouse(date_str: str, max_events: int | None = None) -> list[tuple[MarketEvent, str]]:
    """Load MBO events for a day from ClickHouse."""
    print(f"Loading data for {date_str}...")

    # Query for day's data
    query = f"""
    SELECT
        toInt64(toUnixTimestamp64Nano(ts_event)) as ts_event_ns,
        action,
        side,
        price,
        size,
        order_id,
        flags
    FROM mnq_mbo
    WHERE toDate(ts_event) = '{date_str}'
      AND symbol = 'MNQZ5'
      AND hour(ts_event) >= 9
      AND hour(ts_event) < 16
    ORDER BY ts_event
    {'LIMIT ' + str(max_events) if max_events else ''}
    FORMAT JSON
    """

    result = subprocess.run(
        ["clickhouse-client", "--query", query],
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        raise Exception(f"Query failed: {result.stderr}")

    data = json.loads(result.stdout)
    print(f"Loaded {len(data['data'])} events")

    # Convert to MarketEvent objects
    events = []
    for row in data["data"]:
        # Map action letters to semantic names
        action_map = {
            'A': 'add',
            'C': 'cancel',
            'M': 'modify',
            'T': 'trade_aggressor',
            'F': 'fill_resting',
        }

        evt = MarketEvent(
            ts_event_ns=int(row["ts_event_ns"]),
            event_type=EventType.MARKER,
            side=row.get("side"),
            price=float(row["price"]) if row.get("price") else None,
            size=int(row["size"]) if row.get("size") else None,
            order_id=int(row["order_id"]) if row.get("order_id") else None,
            flags=int(row["flags"]) if row.get("flags") else None,
        )

        evt_name = action_map.get(row["action"], "none")
        events.append((evt, evt_name))

    return events


def run_backtest(
    date_str: str,
    expected_regime: str,
    max_events: int | None = None,
) -> dict:
    """
    Run backtest for a single day.

    Args:
        date_str: Date to backtest (YYYY-MM-DD)
        expected_regime: Expected regime (for validation)
        max_events: Optional limit on events to process

    Returns:
        Dictionary with backtest results
    """
    print("\n" + "=" * 80)
    print(f"BACKTESTING: {date_str} (Expected: {expected_regime})")
    print("=" * 80)

    # Load data
    events = load_day_from_clickhouse(date_str, max_events)

    # Initialize system
    book = OrderBookEventBatchedStrict()
    fill_model = StrictFillModel(assume_queue_position="front")
    stop_mgr = StopOrderManager(fill_model)
    oco_mgr = OCOBracketManager(fill_model, stop_mgr)
    analyzer = OrderFlowAnalyzer()
    metrics = PerformanceMetrics(initial_capital=100000.0)

    # Strategy state
    current_position = 0
    position_entry_price = 0.0
    active_trail_id = None
    last_regime = "UNKNOWN"
    regime_changes = []

    # Group events by timestamp (event batching)
    batches = defaultdict(list)
    for evt, evt_name in events:
        batches[evt.ts_event_ns].append((evt, evt_name))

    batch_timestamps = sorted(batches.keys())

    print(f"\nProcessing {len(batch_timestamps)} event batches...")

    # Track regime detections
    regime_detections = defaultdict(int)
    total_batches_processed = 0

    # Process batches
    for i, ts_ns in enumerate(batch_timestamps):
        batch = batches[ts_ns]

        # Apply all events in batch to book
        for evt, evt_name in batch:
            book.apply_event(evt, evt_name)

            # Analyze order flow
            best_bid = book.best_bid()
            best_ask = book.best_ask()

            if best_bid and best_ask:
                signals = analyzer.process_event(evt, evt_name, best_bid, best_ask)

        # Every 100 batches, check regime and potentially trade
        if i % 100 == 0 and i > 0:
            best_bid = book.best_bid()
            best_ask = book.best_ask()

            if not best_bid or not best_ask:
                continue

            # Detect regime
            regime, params = detect_regime(analyzer)
            regime_detections[regime] += 1

            if regime != last_regime:
                regime_changes.append((ts_ns, regime, analyzer.get_delta()))
                print(f"\n[{i:>6}] Regime change: {last_regime} -> {regime} (Delta: {analyzer.get_delta()})")
                last_regime = regime

            # Trading logic based on regime
            mid_price = (best_bid + best_ask) / 2

            # Check if we should exit (trailing stop check)
            if current_position != 0 and active_trail_id:
                triggered_m, triggered_l = stop_mgr.check_triggers(book, ts_ns)

                if triggered_m:
                    # Exit via trailing stop
                    stop_mgr.execute_triggered_orders(triggered_m, [], book, ts_ns)

                    for trig in triggered_m:
                        if trig.market_fill:
                            exit_price = trig.market_fill.avg_fill_price
                            pnl = (exit_price - position_entry_price) * abs(current_position)
                            if current_position < 0:  # Short
                                pnl = -pnl

                            trade = Trade(
                                trade_id=f"trade_{len(metrics.trades)}",
                                entry_ts_ns=ts_ns - 100_000_000_000,
                                exit_ts_ns=ts_ns,
                                side="LONG" if current_position > 0 else "SHORT",
                                quantity=abs(current_position),
                                entry_price=position_entry_price,
                                exit_price=exit_price,
                                gross_pnl=pnl,
                                entry_fill_type="market",
                                exit_fill_type="trailing_stop",
                            )
                            metrics.record_trade(trade)

                            print(f"   EXIT {trade.side} @ {exit_price:.2f} | PnL: ${pnl:.2f}")

                            current_position = 0
                            active_trail_id = None

            # Entry logic
            if current_position == 0:  # Not in position
                # Bull trend: Go long
                if regime == "BULL_TREND" and "BUY" in params.allowed_sides:
                    fill = fill_model.simulate_market_buy(5, book)
                    current_position = 5
                    position_entry_price = fill.avg_fill_price

                    # Place trailing stop
                    stop_price = position_entry_price - params.stop_ticks * 0.25
                    trail = stop_mgr.place_trailing_stop_market(
                        f"trail_{i}",
                        "SELL",
                        5,
                        stop_price,
                        trail_offset=params.trail_offset_ticks * 0.25 if params.trail_offset_ticks else 0,
                        ts_ns=ts_ns,
                    )
                    active_trail_id = trail.order_id

                    print(f"   ENTER LONG @ {position_entry_price:.2f} | Stop: {stop_price:.2f}")

                # Bear trend: Go short
                elif regime == "BEAR_TREND" and "SELL" in params.allowed_sides:
                    fill = fill_model.simulate_market_sell(5, book)
                    current_position = -5
                    position_entry_price = fill.avg_fill_price

                    # Place trailing stop
                    stop_price = position_entry_price + params.stop_ticks * 0.25
                    trail = stop_mgr.place_trailing_stop_market(
                        f"trail_{i}",
                        "BUY",
                        5,
                        stop_price,
                        trail_offset=params.trail_offset_ticks * 0.25 if params.trail_offset_ticks else 0,
                        ts_ns=ts_ns,
                    )
                    active_trail_id = trail.order_id

                    print(f"   ENTER SHORT @ {position_entry_price:.2f} | Stop: {stop_price:.2f}")

        total_batches_processed += 1

        # Progress indicator
        if i % 10000 == 0 and i > 0:
            print(f"Processed {i:,} / {len(batch_timestamps):,} batches ({i/len(batch_timestamps)*100:.1f}%)")

    # Close any remaining position
    if current_position != 0:
        best_bid = book.best_bid()
        best_ask = book.best_ask()

        if best_bid and best_ask:
            if current_position > 0:
                fill = fill_model.simulate_market_sell(abs(current_position), book)
            else:
                fill = fill_model.simulate_market_buy(abs(current_position), book)

            exit_price = fill.avg_fill_price
            pnl = (exit_price - position_entry_price) * abs(current_position)
            if current_position < 0:
                pnl = -pnl

            trade = Trade(
                trade_id=f"trade_{len(metrics.trades)}",
                entry_ts_ns=batch_timestamps[0],
                exit_ts_ns=batch_timestamps[-1],
                side="LONG" if current_position > 0 else "SHORT",
                quantity=abs(current_position),
                entry_price=position_entry_price,
                exit_price=exit_price,
                gross_pnl=pnl,
                entry_fill_type="market",
                exit_fill_type="market",
            )
            metrics.record_trade(trade)

            print(f"\n   CLOSE position @ {exit_price:.2f} | PnL: ${pnl:.2f}")

    # Results
    print("\n" + "=" * 80)
    print(f"BACKTEST RESULTS: {date_str}")
    print("=" * 80)

    print("\n📊 Regime Detections:")
    for regime, count in sorted(regime_detections.items(), key=lambda x: -x[1]):
        pct = count / max(sum(regime_detections.values()), 1) * 100
        print(f"   {regime:15s}: {count:>5} ({pct:>5.1f}%)")

    print(f"\n🔄 Regime Changes: {len(regime_changes)}")
    for ts, regime, delta in regime_changes[:10]:  # Show first 10
        print(f"   {regime:15s} (Delta: {delta:>6})")

    print("\n💰 Performance:")
    metrics.print_summary(current_price=book.best_bid() or 25000.0)

    # Return results
    return {
        "date": date_str,
        "expected_regime": expected_regime,
        "detected_regimes": dict(regime_detections),
        "regime_changes": len(regime_changes),
        "trades": len(metrics.trades),
        "total_pnl": metrics.realized_pnl,
        "win_rate": metrics.win_rate(),
        "profit_factor": metrics.profit_factor(),
    }


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 18 + "REGIME-BASED STRATEGY BACKTEST" + " " * 30 + "║")
    print("╚" + "=" * 78 + "╝")

    # Test days
    days = [
        ("2025-10-01", "BULL", 200000),  # Bull day
        ("2025-10-10", "BEAR", 200000),  # Bear day
        ("2025-10-15", "SWING", 200000),  # Swing day
    ]

    results = []

    for date_str, expected_regime, max_events in days:
        result = run_backtest(date_str, expected_regime, max_events=max_events)
        results.append(result)

    # Summary comparison
    print("\n\n" + "=" * 80)
    print("SUMMARY: ALL DAYS")
    print("=" * 80)

    print(f"\n{'Date':<12} {'Expected':<12} {'Trades':<8} {'Win Rate':<10} {'P&L':<12} {'PF':<8}")
    print("-" * 80)
    for r in results:
        print(f"{r['date']:<12} {r['expected_regime']:<12} {r['trades']:<8} "
              f"{r['win_rate']:<10.1f}% ${r['total_pnl']:<11.2f} {r['profit_factor']:<8.2f}")

    print("\n💡 Key Insights:")
    print(f"   • Bull day: {results[0]['trades']} trades, ${results[0]['total_pnl']:.2f} P&L")
    print(f"   • Bear day: {results[1]['trades']} trades, ${results[1]['total_pnl']:.2f} P&L")
    print(f"   • Swing day: {results[2]['trades']} trades, ${results[2]['total_pnl']:.2f} P&L")

    total_pnl = sum(r['total_pnl'] for r in results)
    total_trades = sum(r['trades'] for r in results)

    print(f"\n   TOTAL: {total_trades} trades, ${total_pnl:.2f} P&L across 3 days")
    print(f"   Average per day: ${total_pnl / 3:.2f}")

    print("\n" + "=" * 80)
    print("✅ BACKTEST COMPLETE!")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
