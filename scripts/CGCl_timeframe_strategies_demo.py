#!/usr/bin/env python3
"""
Timeframe Strategy Comparison: Active Day Trading vs Intraday Swing

Demonstrates two distinct trading approaches:
1. ACTIVE DAY TRADING (Seconds to Minutes)
   - Order flow driven entries
   - Quick scalps (5-20 ticks)
   - High win rate, tight stops

2. INTRADAY SWING (Minutes to Hours)
   - Trend following with confirmation
   - Larger targets (50-100 ticks)
   - Trail stops, let winners run

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

import sys
from pathlib import Path
from dataclasses import dataclass, field

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_exec.CGCl_fill_model_strict import StrictFillModel
from cg_exec.CGCl_stop_limit_orders import StopOrderManager
from cg_exec.CGCl_oco_bracket_orders import OCOBracketManager
from cg_exec.CGCl_performance_metrics import PerformanceMetrics, Trade
from cg_exec.CGCl_order_flow_analysis import OrderFlowAnalyzer
from cg_sim.models import MarketEvent, EventType


@dataclass
class MarketState:
    """Current market conditions."""
    best_bid: float
    best_ask: float
    mid_price: float
    ts_ns: int
    delta: int = 0
    delta_trend: str = "neutral"
    recent_imbalance: str = "none"


def build_dynamic_book(bid: float, ask: float, bid_depth: int = 30, ask_depth: int = 30) -> OrderBookEventBatchedStrict:
    """Build order book with configurable depth."""
    book = OrderBookEventBatchedStrict()
    ts = 1000000000000

    # Build bid side
    for i in range(4):
        evt = MarketEvent(
            ts_event_ns=ts + i,
            event_type=EventType.ADD,
            side="BID",
            price=bid - i * 0.25,
            size=bid_depth,
            order_id=1000 + i,
        )
        book.apply_event(evt, "add")

    # Build ask side
    for i in range(4):
        evt = MarketEvent(
            ts_event_ns=ts + 10 + i,
            event_type=EventType.ADD,
            side="ASK",
            price=ask + i * 0.25,
            size=ask_depth,
            order_id=2000 + i,
        )
        book.apply_event(evt, "add")

    return book


def simulate_order_flow_sequence(
    analyzer: OrderFlowAnalyzer,
    scenario: str,
    start_price: float,
    ts_base: int,
) -> tuple[list, MarketState]:
    """
    Simulate order flow patterns.

    Scenarios:
    - "buy_sweep": Aggressive buying through levels
    - "sell_absorption": Large sell order absorbs buying
    - "buy_imbalance": Sustained buy pressure
    """
    signals = []

    if scenario == "buy_sweep":
        # Aggressive market buys sweeping through ask levels
        for i in range(5):
            evt = MarketEvent(
                ts_event_ns=ts_base + i * 100_000_000,  # 100ms apart
                event_type=EventType.TRADE,
                side="BID",  # Aggressor buying
                price=start_price + i * 0.25,
                size=15,
                order_id=5000 + i,
            )
            sigs = analyzer.process_event(evt, "trade_aggressor", start_price - 0.25, start_price)
            signals.extend(sigs)

        state = MarketState(
            best_bid=start_price + 1.00,
            best_ask=start_price + 1.25,
            mid_price=start_price + 1.125,
            ts_ns=ts_base + 500_000_000,
            delta=analyzer.get_delta(),
            delta_trend="bullish",
            recent_imbalance="buy",
        )

    elif scenario == "sell_absorption":
        # Large passive sell order absorbs aggressive buying
        for i in range(3):
            evt = MarketEvent(
                ts_event_ns=ts_base + i * 200_000_000,
                event_type=EventType.TRADE,
                side="BID",
                price=start_price,
                size=20,
                order_id=6000 + i,
            )
            analyzer.process_event(evt, "trade_aggressor", start_price - 0.25, start_price)

        # Large fill at ask (absorption)
        absorption = MarketEvent(
            ts_event_ns=ts_base + 700_000_000,
            event_type=EventType.TRADE,
            side="ASK",
            price=start_price,
            size=80,
            order_id=6100,
        )
        sigs = analyzer.process_event(absorption, "fill_resting", start_price - 0.25, start_price)
        signals.extend(sigs)

        state = MarketState(
            best_bid=start_price - 0.50,
            best_ask=start_price - 0.25,
            mid_price=start_price - 0.375,
            ts_ns=ts_base + 1_000_000_000,
            delta=analyzer.get_delta(),
            delta_trend="bearish",
            recent_imbalance="none",
        )

    elif scenario == "buy_imbalance":
        # Sustained buying pressure
        for i in range(10):
            evt = MarketEvent(
                ts_event_ns=ts_base + i * 150_000_000,
                event_type=EventType.TRADE,
                side="BID",
                price=start_price,
                size=8,
                order_id=7000 + i,
            )
            sigs = analyzer.process_event(evt, "trade_aggressor", start_price - 0.25, start_price)
            signals.extend(sigs)

        state = MarketState(
            best_bid=start_price + 0.50,
            best_ask=start_price + 0.75,
            mid_price=start_price + 0.625,
            ts_ns=ts_base + 1_500_000_000,
            delta=analyzer.get_delta(),
            delta_trend="bullish",
            recent_imbalance="buy",
        )

    else:
        state = MarketState(
            best_bid=start_price,
            best_ask=start_price + 0.25,
            mid_price=start_price + 0.125,
            ts_ns=ts_base,
        )

    return signals, state


def active_day_trading_demo():
    """
    ACTIVE DAY TRADING (Seconds to Minutes)

    Strategy:
    1. Monitor order flow for imbalance/sweeps
    2. Enter on signal with market order
    3. Target: 10-20 ticks
    4. Stop: 5 ticks
    5. Hold time: 10-60 seconds
    """
    print("\n" + "=" * 80)
    print("ACTIVE DAY TRADING DEMO")
    print("Timeframe: Seconds to Minutes | Style: Order Flow Scalping")
    print("=" * 80)

    # Initialize
    fill_model = StrictFillModel()
    stop_mgr = StopOrderManager(fill_model)
    oco_mgr = OCOBracketManager(fill_model, stop_mgr)
    analyzer = OrderFlowAnalyzer()
    metrics = PerformanceMetrics(initial_capital=100000.0)

    trades_taken = []

    # --- TRADE 1: Buy Sweep Signal ---
    print("\n[TRADE 1] Buy Sweep Detected")
    print("-" * 80)

    ts1 = 10_000_000_000_000
    signals1, state1 = simulate_order_flow_sequence(
        analyzer, "buy_sweep", 20000.00, ts1
    )

    print(f"\n📊 Market State:")
    print(f"   Bid/Ask: {state1.best_bid:.2f} / {state1.best_ask:.2f}")
    print(f"   Delta: {state1.delta} ({state1.delta_trend})")

    print(f"\n🚨 Signals Detected: {len(signals1)}")
    for sig in signals1:
        if sig.signal_type in ["sweep", "imbalance"]:
            print(f"   {sig.signal_type.upper()}: {sig.side} | Strength: {sig.strength:.0f}")

    # Entry on sweep signal
    book1 = build_dynamic_book(state1.best_bid, state1.best_ask)
    entry1 = fill_model.simulate_market_buy(5, book1)

    print(f"\n✅ ENTRY: Market BUY 5 @ {entry1.avg_fill_price:.2f}")
    print(f"   Slippage: {entry1.slippage_ticks:.2f} ticks")

    # Quick bracket: 10 tick target, 5 tick stop
    target_price = entry1.avg_fill_price + 2.50  # 10 ticks
    stop_price = entry1.avg_fill_price - 1.25    # 5 ticks

    print(f"\n📍 Risk Management:")
    print(f"   Stop:   {stop_price:.2f} (-5 ticks / -${1.25 * 5:.2f})")
    print(f"   Target: {target_price:.2f} (+10 ticks / +${2.50 * 5:.2f})")
    print(f"   R:R = 2:1")

    # Simulate hitting target (momentum continues)
    print(f"\n⏱️  10 seconds later... price hits target")

    pnl1 = (target_price - entry1.avg_fill_price) * 5
    trade1 = Trade(
        trade_id="ADT_1",
        entry_ts_ns=ts1,
        exit_ts_ns=ts1 + 10_000_000_000,
        side="LONG",
        quantity=5,
        entry_price=entry1.avg_fill_price,
        exit_price=target_price,
        gross_pnl=pnl1,
        entry_slippage_ticks=entry1.slippage_ticks,
        entry_fill_type="market",
        exit_fill_type="limit",
    )
    metrics.record_trade(trade1)
    trades_taken.append(("WIN", pnl1, 10))

    print(f"✅ EXIT: Target hit @ {target_price:.2f}")
    print(f"   Profit: ${pnl1:.2f} | Duration: 10 seconds")

    # --- TRADE 2: Sell Absorption Signal ---
    print("\n\n[TRADE 2] Sell Absorption Detected")
    print("-" * 80)

    ts2 = ts1 + 60_000_000_000  # 1 minute later
    signals2, state2 = simulate_order_flow_sequence(
        analyzer, "sell_absorption", 20005.00, ts2
    )

    print(f"\n📊 Market State:")
    print(f"   Bid/Ask: {state2.best_bid:.2f} / {state2.best_ask:.2f}")
    print(f"   Delta: {state2.delta} ({state2.delta_trend})")

    print(f"\n🚨 Signals Detected: {len(signals2)}")
    for sig in signals2:
        if sig.signal_type == "absorption":
            print(f"   ABSORPTION: {sig.side} @ {sig.price:.2f} | Size: {sig.details.get('size')}")

    # Entry: Fade the absorption (contrarian)
    book2 = build_dynamic_book(state2.best_bid, state2.best_ask)
    entry2 = fill_model.simulate_market_sell(5, book2)

    print(f"\n✅ ENTRY: Market SELL 5 @ {entry2.avg_fill_price:.2f}")
    print(f"   Slippage: {entry2.slippage_ticks:.2f} ticks")

    # Quick bracket: 8 tick target, 5 tick stop
    target_price2 = entry2.avg_fill_price - 2.00  # 8 ticks
    stop_price2 = entry2.avg_fill_price + 1.25    # 5 ticks

    print(f"\n📍 Risk Management:")
    print(f"   Stop:   {stop_price2:.2f} (+5 ticks / -${1.25 * 5:.2f})")
    print(f"   Target: {target_price2:.2f} (-8 ticks / +${2.00 * 5:.2f})")
    print(f"   R:R = 1.6:1")

    # Simulate stop hit (wrong read)
    print(f"\n⏱️  8 seconds later... stop hit (absorption held)")

    pnl2 = (entry2.avg_fill_price - stop_price2) * 5
    trade2 = Trade(
        trade_id="ADT_2",
        entry_ts_ns=ts2,
        exit_ts_ns=ts2 + 8_000_000_000,
        side="SHORT",
        quantity=5,
        entry_price=entry2.avg_fill_price,
        exit_price=stop_price2,
        gross_pnl=pnl2,
        entry_slippage_ticks=entry2.slippage_ticks,
        entry_fill_type="market",
        exit_fill_type="stop",
    )
    metrics.record_trade(trade2)
    trades_taken.append(("LOSS", pnl2, 8))

    print(f"❌ EXIT: Stop hit @ {stop_price2:.2f}")
    print(f"   Loss: ${pnl2:.2f} | Duration: 8 seconds")

    # --- TRADE 3: Buy Imbalance ---
    print("\n\n[TRADE 3] Buy Imbalance - Momentum Trade")
    print("-" * 80)

    ts3 = ts2 + 45_000_000_000
    signals3, state3 = simulate_order_flow_sequence(
        analyzer, "buy_imbalance", 20003.00, ts3
    )

    print(f"\n📊 Market State:")
    print(f"   Bid/Ask: {state3.best_bid:.2f} / {state3.best_ask:.2f}")
    print(f"   Delta: {state3.delta} ({state3.delta_trend})")

    print(f"\n🚨 Signals Detected: {len(signals3)}")
    for sig in signals3:
        if sig.signal_type == "imbalance":
            print(f"   IMBALANCE: {sig.side} | Ratio: {sig.details.get('ratio', 0):.1f}:1")

    book3 = build_dynamic_book(state3.best_bid, state3.best_ask)
    entry3 = fill_model.simulate_market_buy(5, book3)

    print(f"\n✅ ENTRY: Market BUY 5 @ {entry3.avg_fill_price:.2f}")

    target_price3 = entry3.avg_fill_price + 3.00  # 12 ticks
    stop_price3 = entry3.avg_fill_price - 1.25    # 5 ticks

    print(f"\n📍 Risk Management:")
    print(f"   Stop:   {stop_price3:.2f} (-5 ticks)")
    print(f"   Target: {target_price3:.2f} (+12 ticks)")

    print(f"\n⏱️  18 seconds later... target hit")

    pnl3 = (target_price3 - entry3.avg_fill_price) * 5
    trade3 = Trade(
        trade_id="ADT_3",
        entry_ts_ns=ts3,
        exit_ts_ns=ts3 + 18_000_000_000,
        side="LONG",
        quantity=5,
        entry_price=entry3.avg_fill_price,
        exit_price=target_price3,
        gross_pnl=pnl3,
        entry_fill_type="market",
        exit_fill_type="limit",
    )
    metrics.record_trade(trade3)
    trades_taken.append(("WIN", pnl3, 18))

    print(f"✅ EXIT: Target hit @ {target_price3:.2f}")
    print(f"   Profit: ${pnl3:.2f} | Duration: 18 seconds")

    # Summary
    print("\n" + "=" * 80)
    print("ACTIVE DAY TRADING SUMMARY")
    print("=" * 80)

    print("\n📊 Trade Results:")
    for i, (result, pnl, duration) in enumerate(trades_taken, 1):
        print(f"   Trade {i}: {result:4s} | ${pnl:>7.2f} | {duration:>2d}s")

    metrics.print_summary(current_price=20005.00)

    print("\n💡 Active Day Trading Characteristics:")
    print("   ✓ Very short hold times (8-18 seconds)")
    print("   ✓ Order flow driven entries")
    print("   ✓ Tight stops, quick targets (5-12 ticks)")
    print("   ✓ High R:R ratios (1.6:1 to 2:1)")
    print("   ✓ Multiple trades per session")

    return metrics


def intraday_swing_demo():
    """
    INTRADAY SWING (Minutes to Hours)

    Strategy:
    1. Identify trend direction
    2. Enter on pullback with limit order
    3. Target: 50-100 ticks
    4. Trail stop to lock profits
    5. Hold time: 15 min - 3 hours
    """
    print("\n\n" + "=" * 80)
    print("INTRADAY SWING DEMO")
    print("Timeframe: Minutes to Hours | Style: Trend Following + Trailing")
    print("=" * 80)

    # Initialize
    fill_model = StrictFillModel(assume_queue_position="front")
    stop_mgr = StopOrderManager(fill_model)
    oco_mgr = OCOBracketManager(fill_model, stop_mgr)
    analyzer = OrderFlowAnalyzer()
    metrics = PerformanceMetrics(initial_capital=100000.0)

    # --- TRADE 1: Trend Following with Trail ---
    print("\n[TRADE 1] Uptrend Continuation - Trail Stop")
    print("-" * 80)

    ts1 = 20_000_000_000_000

    print("\n📈 Market Context:")
    print("   Trend: Bullish (higher highs, higher lows)")
    print("   Current: Price pulled back to support @ 20000.00")
    print("   Plan: Enter on pullback, trail stop for runners")

    # Entry at pullback level
    book1 = build_dynamic_book(19998.00, 20000.00, bid_depth=40, ask_depth=25)

    print(f"\n✅ ENTRY: Limit BUY 5 @ 20000.00 (at support)")
    entry_price = 20000.00

    # Place trailing stop
    initial_stop = entry_price - 10.00  # 40 ticks
    trail_offset = 5.00  # Trail by $5 (20 ticks)

    trail = stop_mgr.place_trailing_stop_market(
        "swing_trail_1", "SELL", 5, initial_stop, trail_offset=trail_offset, ts_ns=ts1
    )

    print(f"\n📍 Risk Management:")
    print(f"   Initial Stop: {initial_stop:.2f} (-40 ticks / -${10.00 * 5:.2f})")
    print(f"   Trail Offset: ${trail_offset:.2f} (20 ticks)")
    print(f"   Target: Trail until stopped out")

    # Simulate price movement over time
    print(f"\n⏱️  Price Action Over Time:")

    price_sequence = [
        (15, 20005.00, "15 min: +5.00 (+20 ticks)"),
        (30, 20015.00, "30 min: +15.00 (+60 ticks) 🔥"),
        (45, 20025.00, "45 min: +25.00 (+100 ticks) 🚀"),
        (60, 20030.00, "60 min: +30.00 (+120 ticks)"),
        (75, 20035.00, "75 min: +35.00 (+140 ticks) - Peak"),
        (90, 20028.00, "90 min: Reversal to +28.00"),
    ]

    exit_price = entry_price  # Default
    duration_min = 90  # Default

    for minutes, price, desc in price_sequence:
        book_t = build_dynamic_book(price - 0.25, price)
        triggered_m, _ = stop_mgr.check_triggers(book_t, ts1 + minutes * 60_000_000_000)

        print(f"   {desc}")
        print(f"      Trail Stop: ${trail.stop_price:.2f}")

        if triggered_m:
            exit_price = price
            duration_min = minutes
            break
    else:
        # If loop completes without break, set last price as exit
        exit_price = price_sequence[-1][1]
        duration_min = price_sequence[-1][0]

    # Trail stop hit
    pnl1 = (exit_price - entry_price) * 5

    trade1 = Trade(
        trade_id="IDS_1",
        entry_ts_ns=ts1,
        exit_ts_ns=ts1 + duration_min * 60_000_000_000,
        side="LONG",
        quantity=5,
        entry_price=entry_price,
        exit_price=exit_price,
        gross_pnl=pnl1,
        entry_fill_type="limit",
        exit_fill_type="trailing_stop",
    )
    metrics.record_trade(trade1)

    print(f"\n✅ EXIT: Trailing stop hit @ {exit_price:.2f}")
    print(f"   Profit: ${pnl1:.2f} (+{(exit_price - entry_price) / 0.25:.0f} ticks)")
    print(f"   Duration: {duration_min} minutes")
    print(f"   Peak Gain: ${35.00 * 5:.2f} (locked in ${pnl1:.2f})")

    # --- TRADE 2: Bracket Order on Breakout ---
    print("\n\n[TRADE 2] Breakout Trade - Bracket Order")
    print("-" * 80)

    ts2 = ts1 + 180 * 60_000_000_000  # 3 hours later

    print("\n📊 Market Context:")
    print("   Pattern: Consolidation at 20040-20050")
    print("   Setup: Breakout above 20050 resistance")
    print("   Plan: Enter on breakout, bracket with 30 tick stop, 80 tick target")

    book2 = build_dynamic_book(20048.00, 20050.00)

    # Market entry on breakout
    entry2 = fill_model.simulate_market_buy(5, book2)

    print(f"\n✅ ENTRY: Market BUY 5 @ {entry2.avg_fill_price:.2f} (breakout)")

    # Bracket order
    stop_price2 = entry2.avg_fill_price - 7.50   # 30 ticks
    target_price2 = entry2.avg_fill_price + 20.00 # 80 ticks

    bracket = oco_mgr.create_bracket_order(
        bracket_id="swing_bracket_1",
        entry_side="BUY",
        entry_qty=5,
        entry_price=entry2.avg_fill_price,
        stop_price=stop_price2,
        target_price=target_price2,
        entry_type="market",
        book=book2,
        ts_ns=ts2,
    )

    print(f"\n📍 Risk Management (Bracket):")
    print(f"   Stop:   {stop_price2:.2f} (-30 ticks / -${7.50 * 5:.2f})")
    print(f"   Target: {target_price2:.2f} (+80 ticks / +${20.00 * 5:.2f})")
    print(f"   R:R = 2.67:1")

    # Simulate target hit
    print(f"\n⏱️  120 minutes later...")
    print(f"   Price steadily climbs to target")

    pnl2 = (target_price2 - entry2.avg_fill_price) * 5

    trade2 = Trade(
        trade_id="IDS_2",
        entry_ts_ns=ts2,
        exit_ts_ns=ts2 + 120 * 60_000_000_000,
        side="LONG",
        quantity=5,
        entry_price=entry2.avg_fill_price,
        exit_price=target_price2,
        gross_pnl=pnl2,
        entry_fill_type="market",
        exit_fill_type="limit",
    )
    metrics.record_trade(trade2)

    print(f"✅ EXIT: Target hit @ {target_price2:.2f}")
    print(f"   Profit: ${pnl2:.2f}")
    print(f"   Duration: 2 hours")

    # Summary
    print("\n" + "=" * 80)
    print("INTRADAY SWING SUMMARY")
    print("=" * 80)

    print("\n📊 Trade Results:")
    print(f"   Trade 1: WIN  | ${pnl1:>7.2f} | {duration_min}min (trailing)")
    print(f"   Trade 2: WIN  | ${pnl2:>7.2f} | 120min (bracket)")

    metrics.print_summary(current_price=20070.00)

    print("\n💡 Intraday Swing Characteristics:")
    print("   ✓ Longer hold times (90-120 minutes)")
    print("   ✓ Trend following + confirmation")
    print("   ✓ Wider stops, larger targets (30-80 ticks)")
    print("   ✓ Trail stops to maximize winners")
    print("   ✓ Fewer trades, higher profit per trade")

    return metrics


def comparison_summary(active_metrics: PerformanceMetrics, swing_metrics: PerformanceMetrics):
    """Compare the two approaches."""
    print("\n\n" + "=" * 80)
    print("STRATEGY COMPARISON: ACTIVE DAY TRADING vs INTRADAY SWING")
    print("=" * 80)

    active_sum = active_metrics.summary_dict()
    swing_sum = swing_metrics.summary_dict()

    print("\n" + "-" * 80)
    print(f"{'Metric':<30s} | {'Active Day':<20s} | {'Intraday Swing':<20s}")
    print("-" * 80)

    comparisons = [
        ("Total Trades", active_sum['total_trades'], swing_sum['total_trades']),
        ("Win Rate", f"{active_sum['win_rate']:.1f}%", f"{swing_sum['win_rate']:.1f}%"),
        ("Total PnL", f"${active_sum['realized_pnl']:.2f}", f"${swing_sum['realized_pnl']:.2f}"),
        ("Avg Win", f"${active_sum['average_win']:.2f}", f"${swing_sum['average_win']:.2f}"),
        ("Avg Loss", f"${active_sum['average_loss']:.2f}", f"${swing_sum['average_loss']:.2f}"),
        ("Profit Factor", f"{active_sum['profit_factor']:.2f}", f"{swing_sum['profit_factor']:.2f}"),
        ("Sharpe Ratio", f"{active_sum['sharpe_ratio']:.2f}", f"{swing_sum['sharpe_ratio']:.2f}"),
    ]

    for metric, active_val, swing_val in comparisons:
        print(f"{metric:<30s} | {str(active_val):<20s} | {str(swing_val):<20s}")

    print("-" * 80)

    print("\n" + "=" * 80)
    print("KEY DIFFERENCES")
    print("=" * 80)

    print("\n[ACTIVE DAY TRADING]")
    print("  Timeframe:     Seconds to minutes (10-60 seconds)")
    print("  Entry Signal:  Order flow (sweeps, imbalance, absorption)")
    print("  Position Size: Standard (5 contracts)")
    print("  Targets:       Small (5-15 ticks / $1.25-$3.75)")
    print("  Stops:         Tight (5 ticks / $1.25)")
    print("  Hold Time:     10-20 seconds average")
    print("  Trades/Day:    20-50 potential trades")
    print("  Win Rate:      65-75% (tight stops, quick targets)")
    print("  Focus:         Microstructure, momentum, quick scalps")
    print("  Requirements:  Fast execution, order flow data, low latency")

    print("\n[INTRADAY SWING]")
    print("  Timeframe:     Minutes to hours (30 min - 3 hours)")
    print("  Entry Signal:  Trend + pullback/breakout confirmation")
    print("  Position Size: Standard (5 contracts)")
    print("  Targets:       Large (50-100 ticks / $12.50-$25.00)")
    print("  Stops:         Wide (30-40 ticks / $7.50-$10.00)")
    print("  Hold Time:     90-120 minutes average")
    print("  Trades/Day:    2-5 quality setups")
    print("  Win Rate:      60-70% (room to breathe)")
    print("  Focus:         Trends, patience, let winners run")
    print("  Requirements:  Chart patterns, trailing stops, discipline")

    print("\n" + "=" * 80)
    print("WHICH STYLE TO CHOOSE?")
    print("=" * 80)

    print("\nChoose ACTIVE DAY TRADING if you:")
    print("  ✓ Have low-latency execution infrastructure")
    print("  ✓ Enjoy fast-paced, high-frequency trading")
    print("  ✓ Can monitor order flow continuously")
    print("  ✓ Want many small wins rather than few large wins")
    print("  ✓ Have tight spreads and low commissions")

    print("\nChoose INTRADAY SWING if you:")
    print("  ✓ Prefer fewer, higher-quality setups")
    print("  ✓ Want to avoid overtrading")
    print("  ✓ Can tolerate wider stops and drawdowns")
    print("  ✓ Focus on trends and patterns over microstructure")
    print("  ✓ Can step away during trades (trailing stops work for you)")

    print("\n" + "=" * 80)


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 18 + "TIMEFRAME STRATEGY DEMONSTRATION" + " " * 28 + "║")
    print("╚" + "=" * 78 + "╝")

    # Run both strategies
    active_metrics = active_day_trading_demo()
    swing_metrics = intraday_swing_demo()

    # Compare results
    comparison_summary(active_metrics, swing_metrics)

    print("\n" + "=" * 80)
    print("DEMONSTRATION COMPLETE")
    print("=" * 80)
    print("\n✅ Both trading styles demonstrated")
    print("✅ Performance metrics calculated")
    print("✅ Key differences highlighted")
    print("\n💡 Your system supports BOTH approaches with full order flow and execution features!")
    print()


if __name__ == "__main__":
    main()
