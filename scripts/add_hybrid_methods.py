#!/usr/bin/env python3
"""
Add hybrid regime detection methods to v4.2
"""

HYBRID_METHODS = '''
		#region v4.2 Hybrid Regime Detection Methods

		// Detect current market regime based on multiple indicators
		private MarketRegime DetectRegime()
		{
			if (!UseHybridMode || CurrentBars[1] < 30)
				return MarketRegime.CHOPPY;  // Default to choppy if not enough data

			int trendScore = 0;

			// INDICATOR 1: EMA Separation (trend strength)
			double emaSeparation = Math.Abs(emaFast[0] - emaSlow[0]);
			if (emaSeparation >= TrendDetectionEMASeparation)
			{
				trendScore++;
			}

			// INDICATOR 2: Directional Consistency (bars moving same way)
			int directionalBars = CheckDirectionalConsistency();
			if (directionalBars >= 6)  // 6 out of 10 bars in same direction
			{
				trendScore++;
			}

			// INDICATOR 3: New Highs/Lows (trend signature)
			if (CheckNewHighLow())
			{
				trendScore++;
			}

			// INDICATOR 4: Volatility Expansion (trending markets expand)
			if (CheckVolatilityExpansion())
			{
				trendScore++;
			}

			// REGIME CLASSIFICATION
			// 3-4 indicators = TRENDING
			// 0-1 indicators = CHOPPY
			// 2 indicators = TRANSITION

			if (trendScore >= 3)
				return MarketRegime.TRENDING;
			else if (trendScore <= 1)
				return MarketRegime.CHOPPY;
			else
				return MarketRegime.TRANSITION;
		}

		// Check if price is moving consistently in one direction
		private int CheckDirectionalConsistency()
		{
			if (CurrentBars[1] < 10)
				return 0;

			bool upDirection = Closes[1][0] > Closes[1][10];
			int consistentBars = 0;

			for (int i = 1; i < 10; i++)
			{
				if (upDirection && Closes[1][i-1] > Closes[1][i])
					consistentBars++;
				else if (!upDirection && Closes[1][i-1] < Closes[1][i])
					consistentBars++;
			}

			return consistentBars;
		}

		// Check if making new 20-bar highs or lows (trend signature)
		private bool CheckNewHighLow()
		{
			if (CurrentBars[1] < 20)
				return false;

			// Find 20-bar high/low
			double high20 = Highs[1][0];
			double low20 = Lows[1][0];

			for (int i = 1; i < 20; i++)
			{
				high20 = Math.Max(high20, Highs[1][i]);
				low20 = Math.Min(low20, Lows[1][i]);
			}

			// Current price making new high or low?
			bool newHigh = Highs[1][0] >= high20;
			bool newLow = Lows[1][0] <= low20;

			return newHigh || newLow;
		}

		// Check if volatility is expanding (trend characteristic)
		private bool CheckVolatilityExpansion()
		{
			if (CurrentBars[1] < 10)
				return false;

			// Current bar range
			double currentRange = Highs[1][0] - Lows[1][0];

			// Average range of last 10 bars
			double avgRange = 0;
			for (int i = 1; i <= 10; i++)
			{
				avgRange += (Highs[1][i] - Lows[1][i]);
			}
			avgRange /= 10;

			// Expanding if current > 1.5x average
			return currentRange > avgRange * 1.5;
		}

		// Update regime with hysteresis (prevent whipsaw)
		private void UpdateRegime()
		{
			if (!UseHybridMode)
			{
				currentRegime = MarketRegime.CHOPPY;
				return;
			}

			MarketRegime detected = DetectRegime();

			if (detected == currentRegime)
			{
				// Same regime, reset confirmation counter
				regimeConfirmationBars = 0;
				lastDetectedRegime = detected;
			}
			else
			{
				// Different regime detected
				if (detected == lastDetectedRegime)
				{
					// Same different regime as before, increment counter
					regimeConfirmationBars++;
				}
				else
				{
					// Brand new regime, restart counter
					lastDetectedRegime = detected;
					regimeConfirmationBars = 1;
				}

				// Confirmed regime change?
				if (regimeConfirmationBars >= RegimeConfirmationBars)
				{
					Print(string.Format("=== REGIME CHANGE: {0} → {1} ===", currentRegime, detected));
					currentRegime = detected;
					regimeConfirmationBars = 0;

					// Update parameters for new regime
					UpdateParametersForRegime();
				}
			}
		}

		// Update stop/target/hold parameters based on current regime
		private void UpdateParametersForRegime()
		{
			switch (currentRegime)
			{
				case MarketRegime.TRENDING:
					// WIDER stops, BIGGER targets, LONGER holds
					currentStopDistance = TrendModeStop;
					currentTargetDistance = TrendModeTarget;
					currentMaxHold = TrendModeMaxHold;
					currentTrailDistance = TrendModeTrailDistance;
					Print(string.Format("TRENDING MODE: Stop={0:F1} Target={1:F1} Hold={2}s Trail={3:F1}",
						currentStopDistance, currentTargetDistance, currentMaxHold, currentTrailDistance));
					break;

				case MarketRegime.CHOPPY:
					// TIGHT stops, SMALL targets, SHORT holds
					currentStopDistance = AbsorptionStop;
					currentTargetDistance = AbsorptionTarget;
					currentMaxHold = AbsorptionMaxHold;
					currentTrailDistance = TrailingStopDistance;
					Print(string.Format("CHOPPY MODE: Stop={0:F1} Target={1:F1} Hold={2}s Trail={3:F1}",
						currentStopDistance, currentTargetDistance, currentMaxHold, currentTrailDistance));
					break;

				case MarketRegime.TRANSITION:
					// BALANCED parameters
					currentStopDistance = (TrendModeStop + AbsorptionStop) / 2;
					currentTargetDistance = (TrendModeTarget + AbsorptionTarget) / 2;
					currentMaxHold = (TrendModeMaxHold + AbsorptionMaxHold) / 2;
					currentTrailDistance = (TrendModeTrailDistance + TrailingStopDistance) / 2;
					Print(string.Format("TRANSITION MODE: Stop={0:F1} Target={1:F1} Hold={2}s Trail={3:F1}",
						currentStopDistance, currentTargetDistance, currentMaxHold, currentTrailDistance));
					break;
			}
		}

		#endregion
'''

def add_hybrid_methods():
    with open('ninjascript/CGScalpingStrategyNT8Native_v4_2_Hybrid.cs', 'r') as f:
        lines = f.readlines()

    # Find where to insert (before the last closing brace of the class)
    # Look for "public override string DisplayName"
    insert_index = None
    for i in range(len(lines) - 1, 0, -1):
        if 'public override string DisplayName' in lines[i]:
            insert_index = i
            break

    if insert_index is None:
        print("❌ Could not find insertion point")
        return

    # Insert hybrid methods before DisplayName
    output = lines[:insert_index]
    output.append('\n')
    output.append(HYBRID_METHODS)
    output.append('\n')
    output.extend(lines[insert_index:])

    with open('ninjascript/CGScalpingStrategyNT8Native_v4_2_Hybrid.cs', 'w') as f:
        f.writelines(output)

    print("✅ Added regime detection methods:")
    print("   - DetectRegime()")
    print("   - CheckDirectionalConsistency()")
    print("   - CheckNewHighLow()")
    print("   - CheckVolatilityExpansion()")
    print("   - UpdateRegime() with hysteresis")
    print("   - UpdateParametersForRegime()")

if __name__ == "__main__":
    add_hybrid_methods()
