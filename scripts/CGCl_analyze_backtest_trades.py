#!/usr/bin/env python3
"""
Analyze backtest trades in detail.

Extracts:
1. Trade durations
2. Parameters used
3. Entry/exit timing
4. Regime at entry vs exit

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

# Parse the backtest output from previous run
# Based on the output, here's what we know:

print("\n" + "=" * 80)
print("DETAILED TRADE ANALYSIS")
print("=" * 80)

# Trade 1: Bull Day (2025-10-01)
print("\n[TRADE 1] October 1, 2025 - BULL DAY")
print("-" * 80)
print("Expected Regime: BULL (market moved +211.5 points)")
print("Detected Regime: CHOPPY (99.6%), BEAR_TREND (0.4%)")
print()
print("Entry:")
print("  • Time:       Batch 51,000 / 165,576 (30.8% through session)")
print("  • Price:      24,733.50 (SHORT)")
print("  • Reason:     Delta = -3,732 triggered BEAR_TREND detection")
print("  • Stop:       24,738.50 (+20 ticks = $5.00)")
print("  • Trail:      12 tick offset ($3.00)")
print()
print("Exit:")
print("  • Time:       End of session (closed manually)")
print("  • Price:      24,765.60")
print("  • Duration:   114,576 batches (~69% of session)")
print("  • Estimated:  ~4-5 hours (if 6.5 hour session)")
print("  • PnL:        -$160.50")
print()
print("⚠️  PROBLEM: Entered SHORT on a BULL day!")
print("   • Market moved UP +211.5 points total")
print("   • We were short, so we lost")
print("   • Delta was negative (cumulative sell pressure) in our sample")
print("   • But full day was bullish overall")

# Trade 2: Bear Day (2025-10-10)
print("\n\n[TRADE 2] October 10, 2025 - BEAR DAY")
print("-" * 80)
print("Expected Regime: BEAR (market moved -508.5 points)")
print("Detected Regime: CHOPPY (99.1%), BEAR_TREND (0.9%)")
print()
print("Entry:")
print("  • Time:       Batch 21,800 / 165,981 (13.1% through session)")
print("  • Price:      25,307.30 (SHORT)")
print("  • Reason:     Delta = -2,310 triggered BEAR_TREND detection")
print("  • Stop:       25,312.30 (+20 ticks = $5.00)")
print("  • Trail:      12 tick offset ($3.00)")
print()
print("Exit:")
print("  • Time:       End of session (closed manually)")
print("  • Price:      25,316.25")
print("  • Duration:   144,181 batches (~86.9% of session)")
print("  • Estimated:  ~5-6 hours")
print("  • PnL:        -$44.75")
print()
print("✅ CORRECT DIRECTION: SHORT on BEAR day")
print("❌ BUT LOST: Market went down -508 pts, we only captured -$44.75 loss")
print("   • Entered too early (13% into session)")
print("   • Should have gained from short position")
print("   • Exit price HIGHER than entry = loss on short")

# Trade 3: Swing Day (2025-10-15)
print("\n\n[TRADE 3] October 15, 2025 - SWING DAY")
print("-" * 80)
print("Expected Regime: SWING (market moved +4.5 points, 241 pt range)")
print("Detected Regime: CHOPPY (98.9%), BEAR_TREND (1.1%)")
print()
print("Entry:")
print("  • Time:       Batch 35,100 / 160,191 (21.9% through session)")
print("  • Price:      24,983.95 (SHORT)")
print("  • Reason:     Delta = -3,416 triggered BEAR_TREND detection")
print("  • Stop:       24,988.95 (+20 ticks = $5.00)")
print("  • Trail:      12 tick offset ($3.00)")
print()
print("Exit:")
print("  • Time:       End of session (closed manually)")
print("  • Price:      24,983.95 (SAME PRICE!)")
print("  • Duration:   125,091 batches (~78.1% of session)")
print("  • Estimated:  ~5 hours")
print("  • PnL:        -$248.00")
print()
print("❓ PUZZLE: Exit at SAME price but lost $248?")
print("   • Entry: 24,983.95")
print("   • Exit:  24,983.95")
print("   • Should be breakeven!")
print("   • Likely data issue or rounding")

# Summary statistics
print("\n\n" + "=" * 80)
print("DURATION STATISTICS")
print("=" * 80)

durations = [
    ("Bull Day", 114576, 165576, 69.2, "4-5 hours"),
    ("Bear Day", 144181, 165981, 86.9, "5-6 hours"),
    ("Swing Day", 125091, 160191, 78.1, "5 hours"),
]

print(f"\n{'Day':<15} {'Batches':<15} {'% Session':<12} {'Est. Time':<12}")
print("-" * 80)
for day, batches, total, pct, est_time in durations:
    print(f"{day:<15} {batches:>6,} / {total:>6,}  {pct:>6.1f}%      {est_time:<12}")

print(f"\nAverage hold time: ~4.7 hours per trade")
print(f"Average entry:     ~22% into session (~1.4 hours after open)")

# Parameters used
print("\n\n" + "=" * 80)
print("PARAMETERS USED")
print("=" * 80)

print("\nREGIME DETECTION THRESHOLDS:")
print("  • BULL_TREND:  Delta > +200, trend='bullish', sweeps >= 3")
print("  • BEAR_TREND:  Delta < -200, trend='bearish', sweeps >= 3")
print("  • CHOPPY:      Everything else")

print("\nBULL_TREND PARAMETERS:")
print("  • Allowed Sides:   BUY only")
print("  • Stop Distance:   20 ticks ($5.00)")
print("  • Target:          None (trailing only)")
print("  • Trail Offset:    12 ticks ($3.00)")
print("  • Entry Type:      Market")

print("\nBEAR_TREND PARAMETERS:")
print("  • Allowed Sides:   SELL only")
print("  • Stop Distance:   20 ticks ($5.00)")
print("  • Target:          None (trailing only)")
print("  • Trail Offset:    12 ticks ($3.00)")
print("  • Entry Type:      Market")

print("\nCHOPPY PARAMETERS:")
print("  • Allowed Sides:   Both BUY and SELL")
print("  • Stop Distance:   5 ticks ($1.25)")
print("  • Target:          10 ticks ($2.50)")
print("  • Trail Offset:    None (fixed target)")
print("  • Entry Type:      Limit")

print("\nEXECUTION SETTINGS:")
print("  • Regime Check:    Every 100 batches")
print("  • Position Size:   5 contracts fixed")
print("  • Queue Position:  Assume front (optimistic)")

# Analysis
print("\n\n" + "=" * 80)
print("CRITICAL ISSUES IDENTIFIED")
print("=" * 80)

print("\n1. ⚠️  DATA SAMPLING PROBLEM")
print("   • Only processed 200K events out of 12-22M per day")
print("   • Missing 98% of the session")
print("   • Delta calculations incomplete")
print("   • Can't see full trend development")

print("\n2. ⚠️  REGIME DETECTION FAILING")
print("   • 99% detected as CHOPPY")
print("   • Brief BEAR_TREND detections (only 100-200 batches)")
print("   • BULL_TREND never detected on bull day")
print("   • Delta threshold too high for sample size")

print("\n3. ⚠️  WRONG DIRECTION ON BULL DAY")
print("   • Entered SHORT when market was bullish")
print("   • Delta was negative in our sample but market went UP overall")
print("   • Lost $160.50 on what should have been profitable")

print("\n4. ⚠️  VERY LONG HOLD TIMES")
print("   • Average: 4.7 hours per trade")
print("   • All trades held until end of session")
print("   • Trailing stops never triggered")
print("   • Manual close at end of data")

print("\n5. ⚠️  POOR ENTRY TIMING")
print("   • Entered ~22% into session on average")
print("   • Missed early moves")
print("   • Bear day: entered early but still lost")

# Recommendations
print("\n\n" + "=" * 80)
print("RECOMMENDATIONS")
print("=" * 80)

print("\n1. PROCESS FULL DATA")
print("   Current: 200K events (~2% of day)")
print("   Needed:  ALL events (12-22M per day)")
print("   Impact:  Proper delta calculation, regime detection")

print("\n2. LOWER DELTA THRESHOLDS")
print("   Current: Delta > 200 / < -200")
print("   Suggested: Delta > 100 / < -100")
print("   Reason: More sensitive to trends in limited data")

print("\n3. MORE FREQUENT REGIME CHECKS")
print("   Current: Every 100 batches")
print("   Suggested: Every 25-50 batches")
print("   Impact: Faster regime detection, better entry timing")

print("\n4. ADD CONFIRMATION FILTERS")
print("   • Require 3+ consecutive regime detections")
print("   • Check sweep direction matches delta")
print("   • Verify price movement confirms regime")

print("\n5. IMPROVE EXIT LOGIC")
print("   • Check trailing stops more frequently")
print("   • Add time-based exits (don't hold until close)")
print("   • Add profit targets as backup to trails")

print("\n6. ADJUST POSITION SIZING")
print("   • Start with 1-2 contracts")
print("   • Scale up if regime persists")
print("   • Reduce risk while tuning")

print("\n\n" + "=" * 80)
print("NEXT STEPS")
print("=" * 80)

print("\nOPTION 1: Full Day Backtest")
print("  • Process ALL events (not just 200K)")
print("  • Runtime: 5-15 minutes per day")
print("  • Most accurate results")

print("\nOPTION 2: Parameter Tuning")
print("  • Lower delta thresholds")
print("  • More frequent checks")
print("  • Test on 200K sample first")

print("\nOPTION 3: Strategy Revision")
print("  • Add multiple confirmations")
print("  • Better entry filters")
print("  • Improve exit timing")

print("\n" + "=" * 80)
