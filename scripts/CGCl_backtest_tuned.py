#!/usr/bin/env python3
"""
Tuned Parameter Backtest - Improved regime detection.

Changes from original:
1. Delta threshold: 200 → 100 (more sensitive)
2. Regime check: Every 100 → Every 50 batches (faster detection)
3. Trail offset: 12 ticks → 20 ticks (wider for trends)
4. Confirmation: Require 2 consecutive regime detections before entering
5. Better logging of regime changes

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
    entry_type: str


def detect_regime(analyzer: OrderFlowAnalyzer) -> tuple[str, RegimeParams]:
    """
    Detect market regime with TUNED thresholds.

    CHANGES:
    - Delta threshold: 200 → 100 (more sensitive)
    - Sweep requirement: 3 → 2 (easier to trigger)
    """
    delta = analyzer.get_delta()
    delta_trend = analyzer.get_delta_trend(periods=10)  # Shorter lookback
    signals = analyzer.get_recent_signals(last_n=20)
    sweeps = [s for s in signals if s.signal_type == "sweep"]

    # TUNED: Lower threshold from 200 to 100
    if delta > 100 and delta_trend == "bullish" and len(sweeps) >= 2:
        regime = "BULL_TREND"
        params = RegimeParams(
            allowed_sides=["BUY"],
            stop_ticks=20,
            target_ticks=None,
            trail_offset_ticks=20,  # TUNED: 12 → 20 ticks
            entry_type="market",
        )
    elif delta < -100 and delta_trend == "bearish" and len(sweeps) >= 2:
        regime = "BEAR_TREND"
        params = RegimeParams(
            allowed_sides=["SELL"],
            stop_ticks=20,
            target_ticks=None,
            trail_offset_ticks=20,  # TUNED: 12 → 20 ticks
            entry_type="market",
        )
    else:
        regime = "CHOPPY"
        params = RegimeParams(
            allowed_sides=["BUY", "SELL"],
            stop_ticks=5,
            target_ticks=10,
            trail_offset_ticks=None,
            entry_type="limit",
        )

    return regime, params


def load_day_from_clickhouse(date_str: str, max_events: int | None = None) -> list[tuple[MarketEvent, str]]:
    """Load MBO events for a day from ClickHouse."""
    print(f"Loading data for {date_str}...")

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

    events = []
    for row in data["data"]:
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


def run_tuned_backtest(
    date_str: str,
    expected_regime: str,
    max_events: int | None = None,
) -> dict:
    """Run backtest with TUNED parameters."""
    print("\n" + "=" * 80)
    print(f"TUNED BACKTEST: {date_str} (Expected: {expected_regime})")
    print("=" * 80)

    # Load data
    events = load_day_from_clickhouse(date_str, max_events)

    # Initialize
    book = OrderBookEventBatchedStrict()
    fill_model = StrictFillModel(assume_queue_position="front")
    stop_mgr = StopOrderManager(fill_model)
    oco_mgr = OCOBracketManager(fill_model, stop_mgr)
    analyzer = OrderFlowAnalyzer()
    metrics = PerformanceMetrics(initial_capital=100000.0)

    # Strategy state
    current_position = 0
    position_entry_price = 0.0
    position_entry_time = 0
    active_trail_id = None
    last_regime = "UNKNOWN"
    regime_changes = []

    # NEW: Confirmation tracking
    regime_confirmation_count = 0
    regime_confirmation_target = "UNKNOWN"

    # Group events
    batches = defaultdict(list)
    for evt, evt_name in events:
        batches[evt.ts_event_ns].append((evt, evt_name))

    batch_timestamps = sorted(batches.keys())
    print(f"\nProcessing {len(batch_timestamps)} event batches...")
    print(f"TUNED SETTINGS:")
    print(f"  • Delta threshold: ±100 (was ±200)")
    print(f"  • Check frequency: Every 50 batches (was 100)")
    print(f"  • Trail offset: 20 ticks (was 12)")
    print(f"  • Confirmation: Require 2 consecutive (NEW)")

    regime_detections = defaultdict(int)
    trades_taken = []

    # Process batches
    for i, ts_ns in enumerate(batch_timestamps):
        batch = batches[ts_ns]

        # Apply events to book
        for evt, evt_name in batch:
            book.apply_event(evt, evt_name)

            best_bid = book.best_bid()
            best_ask = book.best_ask()

            if best_bid and best_ask:
                signals = analyzer.process_event(evt, evt_name, best_bid, best_ask)

        # TUNED: Check every 50 batches instead of 100
        if i % 50 == 0 and i > 0:
            best_bid = book.best_bid()
            best_ask = book.best_ask()

            if not best_bid or not best_ask:
                continue

            # Detect regime
            regime, params = detect_regime(analyzer)
            regime_detections[regime] += 1

            # NEW: Confirmation logic
            if regime == regime_confirmation_target:
                regime_confirmation_count += 1
            else:
                regime_confirmation_target = regime
                regime_confirmation_count = 1

            # Regime change logging
            if regime != last_regime:
                regime_changes.append((ts_ns, regime, analyzer.get_delta(), i))
                print(f"[{i:>6}] {last_regime:12s} → {regime:12s} | Delta: {analyzer.get_delta():>6} | Confirm: {regime_confirmation_count}/2")
                last_regime = regime

            mid_price = (best_bid + best_ask) / 2

            # Check trailing stops
            if current_position != 0 and active_trail_id:
                triggered_m, triggered_l = stop_mgr.check_triggers(book, ts_ns)

                if triggered_m:
                    stop_mgr.execute_triggered_orders(triggered_m, [], book, ts_ns)

                    for trig in triggered_m:
                        if trig.market_fill:
                            exit_price = trig.market_fill.avg_fill_price
                            pnl = (exit_price - position_entry_price) * abs(current_position)
                            if current_position < 0:
                                pnl = -pnl

                            duration_batches = i - (position_entry_time // 50)
                            duration_pct = duration_batches / len(batch_timestamps) * 100

                            trade = Trade(
                                trade_id=f"trade_{len(trades_taken)}",
                                entry_ts_ns=position_entry_time,
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
                            trades_taken.append(trade)

                            print(f"   EXIT {trade.side} @ {exit_price:.2f} | PnL: ${pnl:>7.2f} | Duration: {duration_batches} batches ({duration_pct:.1f}%)")

                            current_position = 0
                            active_trail_id = None
                            regime_confirmation_count = 0  # Reset after trade

            # Entry logic - NEW: Require confirmation
            if current_position == 0 and regime_confirmation_count >= 2:
                # Bull trend
                if regime == "BULL_TREND" and "BUY" in params.allowed_sides:
                    fill = fill_model.simulate_market_buy(5, book)
                    current_position = 5
                    position_entry_price = fill.avg_fill_price
                    position_entry_time = i

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

                    print(f"   ✅ ENTER LONG @ {position_entry_price:.2f} | Stop: {stop_price:.2f} | Trail: ${params.trail_offset_ticks * 0.25:.2f}")

                # Bear trend
                elif regime == "BEAR_TREND" and "SELL" in params.allowed_sides:
                    fill = fill_model.simulate_market_sell(5, book)
                    current_position = -5
                    position_entry_price = fill.avg_fill_price
                    position_entry_time = i

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

                    print(f"   ✅ ENTER SHORT @ {position_entry_price:.2f} | Stop: {stop_price:.2f} | Trail: ${params.trail_offset_ticks * 0.25:.2f}")

        # Progress
        if i % 10000 == 0 and i > 0:
            print(f"Processed {i:,} / {len(batch_timestamps):,} batches ({i/len(batch_timestamps)*100:.1f}%) | Current regime: {last_regime}")

    # Close remaining position
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
                trade_id=f"trade_{len(trades_taken)}",
                entry_ts_ns=position_entry_time,
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
            trades_taken.append(trade)

            print(f"\n   CLOSE position @ {exit_price:.2f} | PnL: ${pnl:.2f}")

    # Results
    print("\n" + "=" * 80)
    print(f"TUNED BACKTEST RESULTS: {date_str}")
    print("=" * 80)

    print("\n📊 Regime Detections:")
    for regime, count in sorted(regime_detections.items(), key=lambda x: -x[1]):
        pct = count / max(sum(regime_detections.values()), 1) * 100
        print(f"   {regime:15s}: {count:>5} ({pct:>5.1f}%)")

    print(f"\n🔄 Regime Changes: {len(regime_changes)}")
    if len(regime_changes) <= 15:
        for ts, regime, delta, batch in regime_changes:
            print(f"   Batch {batch:>6}: {regime:15s} (Delta: {delta:>6})")
    else:
        print("   (Showing first 10 and last 5)")
        for ts, regime, delta, batch in regime_changes[:10]:
            print(f"   Batch {batch:>6}: {regime:15s} (Delta: {delta:>6})")
        print("   ...")
        for ts, regime, delta, batch in regime_changes[-5:]:
            print(f"   Batch {batch:>6}: {regime:15s} (Delta: {delta:>6})")

    print("\n💰 Performance:")
    metrics.print_summary(current_price=book.best_bid() or 25000.0)

    print(f"\n📈 Trades Taken: {len(trades_taken)}")
    for i, trade in enumerate(trades_taken, 1):
        print(f"   {i}. {trade.side:5s} {trade.quantity} @ {trade.entry_price:.2f} → {trade.exit_price:.2f} | ${trade.gross_pnl:>7.2f}")

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
    print("║" + " " * 18 + "TUNED PARAMETER BACKTEST" + " " * 34 + "║")
    print("╚" + "=" * 78 + "╝")

    print("\nPARAMETER CHANGES:")
    print("  1. Delta threshold:  200 → 100 (more sensitive)")
    print("  2. Check frequency:  Every 100 → Every 50 batches")
    print("  3. Trail offset:     12 → 20 ticks (wider)")
    print("  4. Confirmation:     None → Require 2 consecutive")
    print("  5. Sweep requirement: 3 → 2 (easier to trigger)")

    days = [
        ("2025-10-01", "BULL", 200000),
        ("2025-10-10", "BEAR", 200000),
        ("2025-10-15", "SWING", 200000),
    ]

    results = []

    for date_str, expected_regime, max_events in days:
        result = run_tuned_backtest(date_str, expected_regime, max_events=max_events)
        results.append(result)

    # Comparison
    print("\n\n" + "=" * 80)
    print("TUNED RESULTS SUMMARY")
    print("=" * 80)

    print(f"\n{'Date':<12} {'Expected':<12} {'Trades':<8} {'Win Rate':<10} {'P&L':<12} {'PF':<8}")
    print("-" * 80)
    for r in results:
        print(f"{r['date']:<12} {r['expected_regime']:<12} {r['trades']:<8} "
              f"{r['win_rate']:<10.1f}% ${r['total_pnl']:<11.2f} {r['profit_factor']:<8.2f}")

    total_pnl = sum(r['total_pnl'] for r in results)
    total_trades = sum(r['trades'] for r in results)

    print(f"\n💰 TOTAL: {total_trades} trades, ${total_pnl:.2f} P&L")
    print(f"   Average per day: ${total_pnl / 3:.2f}")

    print("\n📊 IMPROVEMENT vs ORIGINAL:")
    original_pnl = -453.25
    improvement = total_pnl - original_pnl
    improvement_pct = (improvement / abs(original_pnl)) * 100

    print(f"   Original P&L:  ${original_pnl:.2f}")
    print(f"   Tuned P&L:     ${total_pnl:.2f}")
    print(f"   Improvement:   ${improvement:+.2f} ({improvement_pct:+.1f}%)")

    print("\n" + "=" * 80)
    print("✅ TUNED BACKTEST COMPLETE!")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
