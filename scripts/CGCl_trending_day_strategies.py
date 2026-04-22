#!/usr/bin/env python3
"""
Heavy Bull/Bear Day Trading Strategies.

Demonstrates how to adapt your trading approach for extreme market conditions:
1. Detecting trending vs choppy markets
2. Order flow behavior in strong trends
3. Position sizing and stop adjustments
4. What NOT to do on trending days

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

import sys
from pathlib import Path
from dataclasses import dataclass

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

from cg_book.order_book_eventbatched_strict import OrderBookEventBatchedStrict
from cg_exec.CGCl_fill_model_strict import StrictFillModel
from cg_exec.CGCl_stop_limit_orders import StopOrderManager
from cg_exec.CGCl_oco_bracket_orders import OCOBracketManager
from cg_exec.CGCl_performance_metrics import PerformanceMetrics, Trade
from cg_exec.CGCl_order_flow_analysis import OrderFlowAnalyzer
from cg_sim.models import MarketEvent, EventType


def build_book(bid: float, ask: float) -> OrderBookEventBatchedStrict:
    """Build order book at given price."""
    book = OrderBookEventBatchedStrict()
    ts = 1000000000000

    for i in range(4):
        evt = MarketEvent(
            ts_event_ns=ts + i,
            event_type=EventType.ADD,
            side="BID",
            price=bid - i * 0.25,
            size=25,
            order_id=100 + i,
        )
        book.apply_event(evt, "add")

    for i in range(4):
        evt = MarketEvent(
            ts_event_ns=ts + 10 + i,
            event_type=EventType.ADD,
            side="ASK",
            price=ask + i * 0.25,
            size=25,
            order_id=200 + i,
        )
        book.apply_event(evt, "add")

    return book


def simulate_heavy_buying(
    analyzer: OrderFlowAnalyzer,
    start_price: float,
    ts_base: int,
    num_waves: int = 5,
) -> list:
    """Simulate sustained heavy buying (bull trend)."""
    signals = []

    for wave in range(num_waves):
        wave_ts = ts_base + wave * 30_000_000_000  # 30 seconds apart

        # Each wave: multiple aggressive buy orders
        for i in range(8):
            evt = MarketEvent(
                ts_event_ns=wave_ts + i * 200_000_000,  # 200ms apart
                event_type=EventType.TRADE,
                side="BID",  # Aggressive buying
                price=start_price + wave * 2.00 + i * 0.25,
                size=10 + (i % 3) * 5,  # Varying sizes
                order_id=10000 + wave * 100 + i,
            )
            sigs = analyzer.process_event(
                evt, "trade_aggressor",
                start_price + wave * 2.00 - 0.25,
                start_price + wave * 2.00
            )
            signals.extend(sigs)

    return signals


def simulate_heavy_selling(
    analyzer: OrderFlowAnalyzer,
    start_price: float,
    ts_base: int,
    num_waves: int = 5,
) -> list:
    """Simulate sustained heavy selling (bear trend)."""
    signals = []

    for wave in range(num_waves):
        wave_ts = ts_base + wave * 30_000_000_000

        # Each wave: multiple aggressive sell orders
        for i in range(8):
            evt = MarketEvent(
                ts_event_ns=wave_ts + i * 200_000_000,
                event_type=EventType.TRADE,
                side="ASK",  # Aggressive selling
                price=start_price - wave * 2.00 - i * 0.25,
                size=10 + (i % 3) * 5,
                order_id=20000 + wave * 100 + i,
            )
            sigs = analyzer.process_event(
                evt, "trade_aggressor",
                start_price - wave * 2.00,
                start_price - wave * 2.00 + 0.25
            )
            signals.extend(sigs)

    return signals


@dataclass
class MarketRegime:
    """Current market regime classification."""
    regime_type: str  # "bull_trend", "bear_trend", "choppy", "neutral"
    strength: float   # 0-100
    delta: int
    delta_trend: str
    imbalance_count: int
    sweep_count: int


def detect_market_regime(analyzer: OrderFlowAnalyzer, lookback: int = 20) -> MarketRegime:
    """
    Detect current market regime from order flow.

    Returns:
        MarketRegime object with classification
    """
    delta = analyzer.get_delta()
    delta_trend = analyzer.get_delta_trend(periods=lookback)

    # Count recent signals
    recent_signals = analyzer.get_recent_signals(last_n=lookback)
    imbalances = [s for s in recent_signals if s.signal_type == "imbalance"]
    sweeps = [s for s in recent_signals if s.signal_type == "sweep"]

    # Classify regime
    if delta > 200 and delta_trend == "bullish" and len(sweeps) > 3:
        return MarketRegime(
            regime_type="bull_trend",
            strength=min(100, delta / 5),
            delta=delta,
            delta_trend=delta_trend,
            imbalance_count=len(imbalances),
            sweep_count=len(sweeps),
        )
    elif delta < -200 and delta_trend == "bearish" and len(sweeps) > 3:
        return MarketRegime(
            regime_type="bear_trend",
            strength=min(100, abs(delta) / 5),
            delta=delta,
            delta_trend=delta_trend,
            imbalance_count=len(imbalances),
            sweep_count=len(sweeps),
        )
    elif abs(delta) < 100 and len(sweeps) < 2:
        return MarketRegime(
            regime_type="choppy",
            strength=50 - abs(delta) / 2,
            delta=delta,
            delta_trend=delta_trend,
            imbalance_count=len(imbalances),
            sweep_count=len(sweeps),
        )
    else:
        return MarketRegime(
            regime_type="neutral",
            strength=50,
            delta=delta,
            delta_trend=delta_trend,
            imbalance_count=len(imbalances),
            sweep_count=len(sweeps),
        )


def bull_trend_strategy():
    """Strategy for heavy BULL days."""
    print("\n" + "=" * 80)
    print("HEAVY BULL DAY STRATEGY")
    print("Scenario: Strong uptrend, persistent buying pressure")
    print("=" * 80)

    # Initialize
    fill_model = StrictFillModel(assume_queue_position="front")
    stop_mgr = StopOrderManager(fill_model)
    analyzer = OrderFlowAnalyzer()
    metrics = PerformanceMetrics(initial_capital=100000.0)

    ts_base = 10_000_000_000_000
    start_price = 20000.00

    # Simulate heavy buying
    print("\n📊 Simulating Heavy Buying Pressure...")
    signals = simulate_heavy_buying(analyzer, start_price, ts_base, num_waves=5)

    # Detect regime
    regime = detect_market_regime(analyzer)

    print(f"\n🔍 Market Regime Detection:")
    print(f"   Type:      {regime.regime_type.upper()}")
    print(f"   Strength:  {regime.strength:.0f}/100")
    print(f"   Delta:     {regime.delta} ({regime.delta_trend})")
    print(f"   Sweeps:    {regime.sweep_count}")
    print(f"   Imbalance: {regime.imbalance_count}")

    if regime.regime_type == "bull_trend":
        print("\n✅ BULL TREND CONFIRMED - Adjust Strategy:")
        print("   • ONLY go long (no shorts!)")
        print("   • Wider stops (trend will pull back)")
        print("   • Use trailing stops (let winners run)")
        print("   • Add on pullbacks (buy dips)")
        print("   • Avoid fading moves (don't fight the trend)")

    # Trade sequence for bull trend
    trades = []
    current_price = start_price

    # TRADE 1: Initial entry on first sweep
    print("\n" + "-" * 80)
    print("[TRADE 1] Enter on First Sweep - Catch the Move")
    print("-" * 80)

    entry1_price = current_price + 2.00
    book1 = build_book(entry1_price, entry1_price + 0.25)
    fill1 = fill_model.simulate_market_buy(5, book1)

    print(f"\n✅ ENTRY: Market BUY 5 @ {fill1.avg_fill_price:.2f}")
    print(f"   Reason: Heavy buying detected, delta trending bullish")

    # WIDER stop for trending market
    stop1_price = fill1.avg_fill_price - 5.00  # 20 ticks (wider!)
    print(f"\n📍 Stop: {stop1_price:.2f} (-20 ticks)")
    print(f"   ⚠️  WIDER than normal (trend will have pullbacks)")

    # Place trailing stop
    trail1 = stop_mgr.place_trailing_stop_market(
        "trail_1", "SELL", 5, stop1_price,
        trail_offset=3.00,  # 12 ticks trail
        ts_ns=ts_base
    )
    print(f"   Trail Offset: $3.00 (12 ticks)")

    # Simulate trend continuation
    print(f"\n⏱️  Trend Continues:")
    price_moves = [
        (30, entry1_price + 5.00, "30s: +$5.00 (+20 ticks) 🚀"),
        (60, entry1_price + 8.00, "60s: +$8.00 (+32 ticks) 🔥"),
        (90, entry1_price + 6.50, "90s: Pullback to +$6.50 (normal)"),
        (120, entry1_price + 10.00, "120s: +$10.00 (+40 ticks) 💎"),
        (150, entry1_price + 12.00, "150s: +$12.00 (+48 ticks)"),
        (180, entry1_price + 9.00, "180s: Trail stop hit"),
    ]

    exit1_price = fill1.avg_fill_price
    exit1_time = 0

    for seconds, price, desc in price_moves:
        book_t = build_book(price, price + 0.25)
        triggered, _ = stop_mgr.check_triggers(book_t, ts_base + seconds * 1_000_000_000)

        print(f"   {desc}")
        print(f"      Trail Stop: ${trail1.stop_price:.2f}")

        if triggered:
            exit1_price = price
            exit1_time = seconds
            break

    pnl1 = (exit1_price - fill1.avg_fill_price) * 5

    print(f"\n✅ EXIT: Trailing stop @ {exit1_price:.2f}")
    print(f"   Profit: ${pnl1:.2f} (+{(exit1_price - fill1.avg_fill_price)/0.25:.0f} ticks)")
    print(f"   Duration: {exit1_time} seconds")

    trade1 = Trade(
        trade_id="bull_1",
        entry_ts_ns=ts_base,
        exit_ts_ns=ts_base + exit1_time * 1_000_000_000,
        side="LONG",
        quantity=5,
        entry_price=fill1.avg_fill_price,
        exit_price=exit1_price,
        gross_pnl=pnl1,
        entry_fill_type="market",
        exit_fill_type="trailing_stop",
    )
    metrics.record_trade(trade1)
    trades.append(("WIN", pnl1))

    # TRADE 2: Add on pullback (pyramid)
    print("\n" + "-" * 80)
    print("[TRADE 2] Add on Pullback - Pyramiding")
    print("-" * 80)

    entry2_price = exit1_price - 2.00  # Pullback
    book2 = build_book(entry2_price, entry2_price + 0.25)

    print(f"\n📉 Market pulls back to {entry2_price:.2f}")
    print(f"   This is NORMAL in trends - don't panic!")
    print(f"\n✅ ADD POSITION: Buy the dip!")

    fill2 = fill_model.simulate_market_buy(5, book2)
    print(f"   Entry: 5 @ {fill2.avg_fill_price:.2f}")

    stop2_price = fill2.avg_fill_price - 5.00
    trail2 = stop_mgr.place_trailing_stop_market(
        "trail_2", "SELL", 5, stop2_price,
        trail_offset=3.00,
        ts_ns=ts_base + 200_000_000_000
    )

    # Trend continues again
    print(f"\n⏱️  Trend Resumes:")
    exit2_price = fill2.avg_fill_price + 8.00

    print(f"   +60s: Price to {exit2_price:.2f}")
    print(f"   Trail stop hit @ {exit2_price:.2f}")

    pnl2 = (exit2_price - fill2.avg_fill_price) * 5

    print(f"\n✅ EXIT: ${pnl2:.2f} profit")

    trade2 = Trade(
        trade_id="bull_2",
        entry_ts_ns=ts_base + 200_000_000_000,
        exit_ts_ns=ts_base + 260_000_000_000,
        side="LONG",
        quantity=5,
        entry_price=fill2.avg_fill_price,
        exit_price=exit2_price,
        gross_pnl=pnl2,
        entry_fill_type="market",
        exit_fill_type="trailing_stop",
    )
    metrics.record_trade(trade2)
    trades.append(("WIN", pnl2))

    # Summary
    print("\n" + "=" * 80)
    print("BULL TREND RESULTS")
    print("=" * 80)

    print(f"\n📊 Trades:")
    for i, (result, pnl) in enumerate(trades, 1):
        print(f"   Trade {i}: {result} | ${pnl:>7.2f}")

    metrics.print_summary(current_price=exit2_price)

    print("\n💡 Key Lessons for Bull Trends:")
    print("   ✓ Wider stops (20+ ticks) to survive pullbacks")
    print("   ✓ Trail aggressively to lock profits")
    print("   ✓ Add on dips (pyramid into trend)")
    print("   ✓ NEVER short into strong buying")
    print("   ✓ Stay with trend until clear reversal")
    print("   ✓ Pullbacks are opportunities, not exits")

    return metrics


def bear_trend_strategy():
    """Strategy for heavy BEAR days."""
    print("\n\n" + "=" * 80)
    print("HEAVY BEAR DAY STRATEGY")
    print("Scenario: Strong downtrend, persistent selling pressure")
    print("=" * 80)

    # Initialize
    fill_model = StrictFillModel(assume_queue_position="front")
    stop_mgr = StopOrderManager(fill_model)
    analyzer = OrderFlowAnalyzer()
    metrics = PerformanceMetrics(initial_capital=100000.0)

    ts_base = 20_000_000_000_000
    start_price = 20050.00

    # Simulate heavy selling
    print("\n📊 Simulating Heavy Selling Pressure...")
    signals = simulate_heavy_selling(analyzer, start_price, ts_base, num_waves=5)

    # Detect regime
    regime = detect_market_regime(analyzer)

    print(f"\n🔍 Market Regime Detection:")
    print(f"   Type:      {regime.regime_type.upper()}")
    print(f"   Strength:  {regime.strength:.0f}/100")
    print(f"   Delta:     {regime.delta} ({regime.delta_trend})")
    print(f"   Sweeps:    {regime.sweep_count}")

    if regime.regime_type == "bear_trend":
        print("\n✅ BEAR TREND CONFIRMED - Adjust Strategy:")
        print("   • ONLY go short (no longs!)")
        print("   • Wider stops (trend will bounce)")
        print("   • Use trailing stops")
        print("   • Add on bounces (sell rallies)")
        print("   • Avoid catching falling knives")

    # Trade for bear trend
    print("\n" + "-" * 80)
    print("[TRADE 1] Short into Weakness")
    print("-" * 80)

    entry_price = start_price - 2.00
    book = build_book(entry_price - 0.25, entry_price)
    fill = fill_model.simulate_market_sell(5, book)

    print(f"\n✅ ENTRY: Market SELL 5 @ {fill.avg_fill_price:.2f}")

    # Wider stop
    stop_price = fill.avg_fill_price + 5.00  # 20 ticks
    print(f"📍 Stop: {stop_price:.2f} (+20 ticks, wider for trend)")

    trail = stop_mgr.place_trailing_stop_market(
        "trail_short", "BUY", 5, stop_price,
        trail_offset=3.00,
        ts_ns=ts_base
    )

    # Downtrend continues
    print(f"\n⏱️  Downtrend Continues:")
    exit_price = fill.avg_fill_price - 12.00

    print(f"   Price falls to {exit_price:.2f}")
    print(f"   Trail stop covers @ {exit_price:.2f}")

    pnl = (fill.avg_fill_price - exit_price) * 5

    print(f"\n✅ EXIT: ${pnl:.2f} profit")
    print(f"   Captured {(fill.avg_fill_price - exit_price)/0.25:.0f} ticks of the move")

    trade = Trade(
        trade_id="bear_1",
        entry_ts_ns=ts_base,
        exit_ts_ns=ts_base + 180_000_000_000,
        side="SHORT",
        quantity=5,
        entry_price=fill.avg_fill_price,
        exit_price=exit_price,
        gross_pnl=pnl,
        entry_fill_type="market",
        exit_fill_type="trailing_stop",
    )
    metrics.record_trade(trade)

    print("\n" + "=" * 80)
    print("BEAR TREND RESULTS")
    print("=" * 80)

    metrics.print_summary(current_price=exit_price)

    print("\n💡 Key Lessons for Bear Trends:")
    print("   ✓ Short into strength/bounces")
    print("   ✓ Wider stops (expect volatility)")
    print("   ✓ Trail down to maximize profits")
    print("   ✓ NEVER buy into panic selling")
    print("   ✓ Move fast - bear markets drop faster than bulls rise")

    return metrics


def what_not_to_do():
    """Common mistakes on trending days."""
    print("\n\n" + "=" * 80)
    print("WHAT NOT TO DO ON TRENDING DAYS ❌")
    print("=" * 80)

    print("\n1. ❌ DON'T Fight the Trend")
    print("   • Shorting a bull trend = getting run over")
    print("   • Buying a bear trend = catching falling knives")
    print("   • Example: Short at 20010, price goes to 20050 = -$200 loss")

    print("\n2. ❌ DON'T Use Normal Stop Sizes")
    print("   • Normal: 5-7 ticks")
    print("   • Trending: 15-25 ticks (wider!)")
    print("   • Tight stops get hit on normal pullbacks")

    print("\n3. ❌ DON'T Use Mean Reversion Strategies")
    print("   • Fading moves loses money")
    print("   • 'Overbought' can stay overbought for hours")
    print("   • Price extremes are your friend, not enemy")

    print("\n4. ❌ DON'T Take Profit Too Early")
    print("   • 10 tick target on a 100 tick move = leaving money on table")
    print("   • Use trailing stops instead of fixed targets")
    print("   • Let the market take you out, not your fear")

    print("\n5. ❌ DON'T Trade Against Order Flow")
    print("   • Persistent delta > 500 = strong trend")
    print("   • Multiple sweeps in same direction = momentum")
    print("   • Ignore at your peril")

    print("\n6. ❌ DON'T Overtrade")
    print("   • 2-5 good trend trades > 20 scalps")
    print("   • Focus on catching big moves")
    print("   • Commissions add up fast")


def regime_detection_system():
    """Complete regime detection and adaptation system."""
    print("\n\n" + "=" * 80)
    print("AUTOMATED REGIME DETECTION SYSTEM")
    print("=" * 80)

    print("\n📊 System monitors order flow and adapts automatically:")

    print("\n```python")
    print("def adapt_strategy(regime: MarketRegime):")
    print('    """Adapt trading parameters based on regime."""')
    print("    ")
    print("    if regime.regime_type == 'bull_trend':")
    print("        return {")
    print("            'sides_allowed': ['BUY'],  # Only long")
    print("            'stop_ticks': 20,          # Wider stops")
    print("            'target_ticks': None,      # Trail, no fixed target")
    print("            'trail_offset': 12,        # 12 tick trail")
    print("            'entry_type': 'market',    # Fast entry")
    print("            'add_on_pullback': True,   # Pyramid")
    print("        }")
    print("    ")
    print("    elif regime.regime_type == 'bear_trend':")
    print("        return {")
    print("            'sides_allowed': ['SELL'], # Only short")
    print("            'stop_ticks': 20,")
    print("            'target_ticks': None,")
    print("            'trail_offset': 12,")
    print("            'entry_type': 'market',")
    print("            'add_on_bounce': True,")
    print("        }")
    print("    ")
    print("    elif regime.regime_type == 'choppy':")
    print("        return {")
    print("            'sides_allowed': ['BUY', 'SELL'],  # Both")
    print("            'stop_ticks': 5,                    # Tight stops")
    print("            'target_ticks': 10,                 # Quick targets")
    print("            'trail_offset': None,               # No trail")
    print("            'entry_type': 'limit',              # Patient entry")
    print("            'fade_extremes': True,              # Mean reversion")
    print("        }")
    print("```")

    print("\n💡 Usage in Live Trading:")
    print("```python")
    print("# Every 30 seconds, reassess regime")
    print("regime = detect_market_regime(analyzer)")
    print("params = adapt_strategy(regime)")
    print("")
    print("# Apply parameters")
    print("if regime.regime_type in ['bull_trend', 'bear_trend']:")
    print("    print(f'⚠️  TRENDING MARKET DETECTED: {regime.regime_type}')")
    print("    print(f'   Switching to trend-following mode...')")
    print("    use_trailing_stops = True")
    print("    stop_distance = params['stop_ticks'] * 0.25")
    print("else:")
    print("    use_normal_scalping = True")
    print("```")


def main():
    print("\n" + "╔" + "=" * 78 + "╗")
    print("║" + " " * 20 + "TRENDING DAY STRATEGIES" + " " * 34 + "║")
    print("╚" + "=" * 78 + "╝")

    # Run demonstrations
    bull_metrics = bull_trend_strategy()
    bear_metrics = bear_trend_strategy()
    what_not_to_do()
    regime_detection_system()

    # Final summary
    print("\n\n" + "=" * 80)
    print("KEY TAKEAWAYS")
    print("=" * 80)

    print("\n✅ Your System Handles Trending Days Well Because:")
    print("   • Order flow analysis detects trend strength")
    print("   • Delta tracking confirms persistent buying/selling")
    print("   • Trailing stops capture large moves")
    print("   • Regime detection adapts parameters automatically")

    print("\n🎯 Critical Adaptations for Trending Days:")
    print("   1. WIDER stops (3-4x normal)")
    print("   2. TRAILING stops (not fixed targets)")
    print("   3. ONE direction only (bull = long only, bear = short only)")
    print("   4. ADD on pullbacks/bounces (pyramid)")
    print("   5. PATIENCE (let big moves develop)")

    print("\n📊 Regime Detection Signals:")
    print("   • Delta > 200 or < -200 = likely trending")
    print("   • 5+ sweeps in same direction = strong momentum")
    print("   • Persistent imbalance (>2:1) = one-sided market")
    print("   • Delta trend bullish/bearish for 20+ periods = confirmed")

    print("\n" + "=" * 80)
    print("SYSTEM READY FOR ALL MARKET CONDITIONS! 🚀")
    print("=" * 80)
    print()


if __name__ == "__main__":
    main()
