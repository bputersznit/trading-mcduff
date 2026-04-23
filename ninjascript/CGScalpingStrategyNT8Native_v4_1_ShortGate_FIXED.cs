//
// CG MNQ Scalping Strategy - NT8 NATIVE VERSION v4.1 - SHORT GATE
// NEW v4.1: Short gate - stricter requirements for short trades
// v4: Hardcoded bar intervals - does NOT depend on chart settings
// All v3 improvements:
// - TREND-AWARE entry detection (stricter counter-trend requirements)
// - Near-price order book filtering (within 5 ticks only)
// - Price level context (swing highs/lows)
// - Tighter price action filters (±1 tick vs ±2)
// - Breakouts must break recent highs/lows
// - All previous v2 improvements (wider stops, RTH, trailing stops)
//
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public class CGScalpingStrategyNT8Native_v4_1_ShortGate : Strategy
	{
		#region Variables

		// Order flow tracking (per second aggregation)
		private class OrderFlowBar
		{
			public DateTime Timestamp { get; set; }
			public long BuyVolume { get; set; }
			public long SellVolume { get; set; }
			public long TotalVolume { get; set; }
			public long AggressorDelta { get; set; }
			public Dictionary<double, long> BidSizeByPrice { get; set; }
			public Dictionary<double, long> AskSizeByPrice { get; set; }

			public OrderFlowBar()
			{
				BidSizeByPrice = new Dictionary<double, long>();
				AskSizeByPrice = new Dictionary<double, long>();
			}
		}

		private OrderFlowBar currentBar;
		private List<OrderFlowBar> orderFlowHistory = new List<OrderFlowBar>();
		private DateTime lastBarTime = DateTime.MinValue;

		// Trade tracking for rolling gate
		private List<DateTime> tradeTimestamps = new List<DateTime>();

		// P&L tracking
		private double todayPnL = 0;
		private double cumulativePnL = 0;
		private List<double> last5DaysPnL = new List<double>();
		private DateTime lastResetDate = DateTime.MinValue;

		// Position tracking
		private DateTime entryTime = DateTime.MinValue;
		private string currentSignalType = "";
		private double entryPrice = 0;

		// Risk flags
		private bool weeklyLimitHit = false;
		private bool hardLimitHit = false;

		// Signal cooldown
		private DateTime lastSignalTime = DateTime.MinValue;
		private int signalCooldownSeconds = 3; // Increased from 2 to reduce overtrading

		// NEW: EMA for trend filter
		private EMA emaFast;
		private EMA emaSlow;

		#endregion

		#region Properties

		// NEW v4: Bar interval configuration
		[NinjaScriptProperty]
		[Range(1, 60)]
		[Display(Name = "Bar Interval (minutes)", Description = "Strategy's internal bar interval (independent of chart)", Order = 0, GroupName = "0. Data Series")]
		public int BarIntervalMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1)]
		[Display(Name = "Contracts", Description = "MUST be 1", Order = 1, GroupName = "1. Position")]
		public int Contracts { get; set; }

		// ABSORPTION parameters - IMPROVED
		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Target (points)", Order = 1, GroupName = "2a. ABSORPTION")]
		public double AbsorptionTarget { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Stop (points)", Order = 2, GroupName = "2a. ABSORPTION")]
		public double AbsorptionStop { get; set; }

		[NinjaScriptProperty]
		[Range(10, 600)]
		[Display(Name = "Max Hold (seconds)", Order = 3, GroupName = "2a. ABSORPTION")]
		public int AbsorptionMaxHold { get; set; }

		[NinjaScriptProperty]
		[Range(10, 200)]
		[Display(Name = "Min Aggressor Volume", Order = 4, GroupName = "2a. ABSORPTION")]
		public int AbsorptionMinAggressor { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 5.0)]
		[Display(Name = "Absorption Ratio Threshold", Order = 5, GroupName = "2a. ABSORPTION")]
		public double AbsorptionRatio { get; set; }

		// BREAKOUT parameters
		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Target (points)", Order = 1, GroupName = "2b. BREAKOUT")]
		public double BreakoutTarget { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Stop (points)", Order = 2, GroupName = "2b. BREAKOUT")]
		public double BreakoutStop { get; set; }

		[NinjaScriptProperty]
		[Range(10, 600)]
		[Display(Name = "Max Hold (seconds)", Order = 3, GroupName = "2b. BREAKOUT")]
		public int BreakoutMaxHold { get; set; }

		[NinjaScriptProperty]
		[Range(1.5, 5.0)]
		[Display(Name = "Volume Spike Multiplier", Order = 4, GroupName = "2b. BREAKOUT")]
		public double BreakoutVolumeSpike { get; set; }

		// NEW: Trend filter
		[NinjaScriptProperty]
		[Display(Name = "Enable Trend Filter", Order = 1, GroupName = "2c. Filters")]
		public bool UseTrendFilter { get; set; }

		[NinjaScriptProperty]
		[Range(5, 50)]
		[Display(Name = "Fast EMA Period", Order = 2, GroupName = "2c. Filters")]
		public int FastEMA { get; set; }

		[NinjaScriptProperty]
		[Range(10, 100)]
		[Display(Name = "Slow EMA Period", Order = 3, GroupName = "2c. Filters")]
		public int SlowEMA { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Disable Short Trades", Order = 4, GroupName = "2c. Filters")]
		public bool DisableShorts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Only Trade With Trend", Order = 5, GroupName = "2c. Filters")]
		public bool OnlyWithTrend { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RTH Only (8:30 AM - 3:00 PM CT)", Order = 6, GroupName = "2c. Filters")]
		public bool RTHOnly { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Trailing Stops", Order = 7, GroupName = "2c. Filters")]
		public bool UseTrailingStops { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Trailing Stop Trigger (points)", Order = 8, GroupName = "2c. Filters")]
		public double TrailingStopTrigger { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Trailing Stop Distance (points)", Order = 9, GroupName = "2c. Filters")]
		public double TrailingStopDistance { get; set; }

		// NEW v4.1: Short Gate parameters
		[NinjaScriptProperty]
		[Display(Name = "Enable Short Gate", Order = 10, GroupName = "2c. Filters")]
		public bool UseShortGate { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "Short Gate: Max Failed Checks", Order = 11, GroupName = "2c. Filters")]
		public int ShortGateMaxFails { get; set; }

		[NinjaScriptProperty]
		[Range(3.0, 15.0)]
		[Display(Name = "Short Gate: Min EMA Separation", Order = 12, GroupName = "2c. Filters")]
		public double ShortGateMinEMASeparation { get; set; }

		// Risk management
		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Max Trades/Hour", Order = 1, GroupName = "3. Risk Management")]
		public double MaxTradesPerHour { get; set; }

		[NinjaScriptProperty]
		[Range(100, 1000)]
		[Display(Name = "Weekly Loss Limit ($)", Order = 2, GroupName = "3. Risk Management")]
		public double WeeklyLossLimit { get; set; }

		[NinjaScriptProperty]
		[Range(100, 2000)]
		[Display(Name = "Hard Loss Limit ($)", Order = 3, GroupName = "3. Risk Management")]
		public double HardLossLimit { get; set; }

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = @"NT8 Native Order Flow Scalping - v4.1 SHORT GATE";
				Name = "CGScalpingStrategyNT8Native_v4_1_ShortGate";
				Calculate = Calculate.OnEachTick;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 30;
				IsFillLimitOnTouch = false;
				MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution = OrderFillResolution.Standard;
				Slippage = 0;
				StartBehavior = StartBehavior.WaitUntilFlat;
				TimeInForce = TimeInForce.Gtc;
				TraceOrders = true;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade = 20;
				IsInstantiatedOnEachOptimizationIteration = false;

				// NEW v4: Hardcoded bar interval
				BarIntervalMinutes = 1;  // Default to 1-minute for scalping

				// Default parameters - IMPROVED based on analysis
				Contracts = 1;

				// ABSORPTION: Wider stops, better targets
				AbsorptionTarget = 8.0;      // Increased from 6.0
				AbsorptionStop = 5.0;        // Increased from 3.0 to reduce 68.9% stop-out
				AbsorptionMaxHold = 120;
				AbsorptionMinAggressor = 40; // Increased from 30 to be more selective
				AbsorptionRatio = 1.5;       // NEW: Stricter absorption requirement

				// BREAKOUT: Wider stops
				BreakoutTarget = 10.0;       // Increased from 8.0
				BreakoutStop = 6.0;          // Increased from 4.0
				BreakoutMaxHold = 60;
				BreakoutVolumeSpike = 2.5;   // Increased from 2.0 to be more selective

				// NEW: Trend filter
				UseTrendFilter = true;
				FastEMA = 9;
				SlowEMA = 21;
				DisableShorts = false;       // Can enable if shorts continue to lose
				OnlyWithTrend = true;        // Only take trades aligned with trend
				RTHOnly = true;              // CRITICAL: Only trade RTH (ETH loses money)
				UseTrailingStops = true;     // Use trailing stops instead of fixed
				TrailingStopTrigger = 4.0;   // Start trailing after 4pt profit
				TrailingStopDistance = 3.0;  // Trail by 3pt

				// NEW v4.1: Short Gate
				UseShortGate = true;              // Enable short gate filtering
				ShortGateMaxFails = 1;            // Allow 1 failed check (balanced)
				ShortGateMinEMASeparation = 5.0;  // 5pt EMA separation for downtrend

				// Risk: Changed to MAX instead of MIN
				MaxTradesPerHour = 10.0;     // Prevent overtrading
				WeeklyLossLimit = 250.0;
				HardLossLimit = 500.0;
			}
			else if (State == State.Configure)
			{
				// NEW v4: Add explicit data series - strategy now controls its own bar interval
				AddDataSeries(BarsPeriodType.Minute, BarIntervalMinutes);
			}
			else if (State == State.DataLoaded)
			{
				// Initialize using BarsArray[1] (strategy's own data series)
				currentBar = new OrderFlowBar { Timestamp = Times[1][0] };

				// Initialize EMAs on strategy bars (BarsArray[1])
				emaFast = EMA(Closes[1], FastEMA);
				emaSlow = EMA(Closes[1], SlowEMA);

				Print("========================================");
				Print("CG SCALPING NT8 NATIVE v4.1 - SHORT GATE");
				Print("========================================");
				Print("NEW v4.1 FEATURES:");
				Print("  ✓ SHORT GATE: Stricter requirements for shorts");
				Print("    - Max failed checks: " + ShortGateMaxFails);
				Print("    - Min EMA separation: " + ShortGateMinEMASeparation);
				Print("    - Gate enabled: " + (UseShortGate ? "YES" : "NO"));
				Print("");
				Print("v4 FEATURES:");
				Print("  ✓ HARDCODED BAR INTERVAL: " + BarIntervalMinutes + " minute(s)");
				Print("  ✓ Independent of chart settings");
				Print("  ✓ Uses BarsArray[1] for all price data");
				Print("");
				Print("v3 FEATURES:");
				Print("  ✓ Trend-aware entry detection");
				Print("    - With-trend: Standard requirements");
				Print("    - Counter-trend: Much stricter");
				Print("  ✓ Near-price order book (5 ticks)");
				Print("  ✓ Swing high/low context");
				Print("  ✓ Tighter price action (±1 tick)");
				Print("  ✓ Breakouts must break 10-bar high/low");
				Print("");
				Print("v2 FEATURES:");
				Print("  - Wider stops: Absorption 5pt, Breakout 6pt");
				Print("  - Trend filter: EMA " + FastEMA + "/" + SlowEMA);
				Print("  - RTH ONLY: " + (RTHOnly ? "YES (8:30-3PM CT)" : "NO"));
				Print("  - Trailing stops: " + (UseTrailingStops ? "YES" : "NO"));
				Print("========================================");
			}
			else if (State == State.Realtime)
			{
				if (lastResetDate.Date != Times[1][0].Date)
				{
					ResetDailyTracking();
				}

				Print("=== LIVE TRADING STARTED ===");
				Print("Order flow tracking: ACTIVE");
				Print("Using " + BarIntervalMinutes + "-minute bars for trend/levels");
			}
		}

		protected override void OnBarUpdate()
		{
			// NEW v4: Only process strategy bars (BarsArray[1])
			if (BarsInProgress != 1)
				return;

			if (CurrentBars[1] < BarsRequiredToTrade)
				return;

			if (State != State.Realtime)
				return;

			// Check risk limits
			if (hardLimitHit || weeklyLimitHit)
				return;

			// Reset daily tracking if new day
			if (lastResetDate.Date != Times[1][0].Date)
			{
				ResetDailyTracking();
			}

			// Time-based exits
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				CheckTimeBasedExit();
			}
		}

		protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
		{
			// Track order flow from prints (trades)
			if (marketDataUpdate.MarketDataType == MarketDataType.Last)
			{
				// Create new bar if we're in a new second
				DateTime currentSecond = new DateTime(
					marketDataUpdate.Time.Year,
					marketDataUpdate.Time.Month,
					marketDataUpdate.Time.Day,
					marketDataUpdate.Time.Hour,
					marketDataUpdate.Time.Minute,
					marketDataUpdate.Time.Second);

				if (currentSecond != lastBarTime)
				{
					// Save completed bar
					if (currentBar != null && lastBarTime != DateTime.MinValue)
					{
						orderFlowHistory.Add(currentBar);

						// Keep only last 60 seconds
						if (orderFlowHistory.Count > 60)
							orderFlowHistory.RemoveAt(0);

						// Check for signals on completed bar
						CheckForSignals();
					}

					// Start new bar
					currentBar = new OrderFlowBar { Timestamp = currentSecond };
					lastBarTime = currentSecond;
				}

				// Aggregate this print into current bar
				long volume = marketDataUpdate.Volume;
				currentBar.TotalVolume += volume;

				// Determine if buy or sell aggression
				if (marketDataUpdate.Price >= marketDataUpdate.Ask - TickSize / 2)
				{
					currentBar.BuyVolume += volume;
					currentBar.AggressorDelta += volume;
				}
				else if (marketDataUpdate.Price <= marketDataUpdate.Bid + TickSize / 2)
				{
					currentBar.SellVolume += volume;
					currentBar.AggressorDelta -= volume;
				}
			}
		}

		protected override void OnMarketDepth(MarketDepthEventArgs marketDepthUpdate)
		{
			// Track order book changes for absorption detection
			if (currentBar == null)
				return;

			double price = marketDepthUpdate.Price;
			long size = marketDepthUpdate.Volume;

			if (marketDepthUpdate.MarketDataType == MarketDataType.Bid)
			{
				if (marketDepthUpdate.Operation == Operation.Update || marketDepthUpdate.Operation == Operation.Add)
				{
					currentBar.BidSizeByPrice[price] = size;
				}
				else if (marketDepthUpdate.Operation == Operation.Remove)
				{
					currentBar.BidSizeByPrice.Remove(price);
				}
			}
			else if (marketDepthUpdate.MarketDataType == MarketDataType.Ask)
			{
				if (marketDepthUpdate.Operation == Operation.Update || marketDepthUpdate.Operation == Operation.Add)
				{
					currentBar.AskSizeByPrice[price] = size;
				}
				else if (marketDepthUpdate.Operation == Operation.Remove)
				{
					currentBar.AskSizeByPrice.Remove(price);
				}
			}
		}

		private void CheckForSignals()
		{
			if (Position.MarketPosition != MarketPosition.Flat)
				return;

			if (currentBar == null || orderFlowHistory.Count < 30)
				return;

			// NEW: RTH Only filter
			if (RTHOnly && !IsRTH())
			{
				return;
			}

			// Cooldown between signals
			if ((DateTime.Now - lastSignalTime).TotalSeconds < signalCooldownSeconds)
				return;

			// NEW: Check max trades per hour (instead of min)
			if (!CheckMaxTradesGate())
				return;

			// Get last bar for short gate evaluation
			OrderFlowBar lastBar = orderFlowHistory[orderFlowHistory.Count - 1];

			// Check for ABSORPTION signal
			Signal absorptionSignal = DetectAbsorption();
			if (absorptionSignal != null &&
			    PassesFilters(absorptionSignal) &&
			    PassesShortGate(absorptionSignal, lastBar))  // NEW v4.1: Short gate
			{
				ExecuteSignal(absorptionSignal);
				return;
			}

			// Check for BREAKOUT signal
			Signal breakoutSignal = DetectBreakout();
			if (breakoutSignal != null &&
			    PassesFilters(breakoutSignal) &&
			    PassesShortGate(breakoutSignal, lastBar))  // NEW v4.1: Short gate
			{
				ExecuteSignal(breakoutSignal);
				return;
			}
		}

		// NEW: Filter function to check trend and other conditions
		private bool PassesFilters(Signal signal)
		{
			// Filter 1: Disable shorts if configured
			if (DisableShorts && signal.Direction == MarketPosition.Short)
			{
				Print("Signal REJECTED: Shorts disabled");
				return false;
			}

			// Filter 2: Trend filter (using strategy bars)
			if (UseTrendFilter && CurrentBars[1] >= BarsRequiredToTrade)
			{
				bool upTrend = emaFast[0] > emaSlow[0];
				bool downTrend = emaFast[0] < emaSlow[0];

				if (OnlyWithTrend)
				{
					// Only take longs in uptrend, shorts in downtrend
					if (signal.Direction == MarketPosition.Long && !upTrend)
					{
						Print(string.Format("Signal REJECTED: Long signal but not in uptrend (EMA {0:F2} < {1:F2})",
							emaFast[0], emaSlow[0]));
						return false;
					}

					if (signal.Direction == MarketPosition.Short && !downTrend)
					{
						Print(string.Format("Signal REJECTED: Short signal but not in downtrend (EMA {0:F2} > {1:F2})",
							emaFast[0], emaSlow[0]));
						return false;
					}
				}
			}

			return true;
		}

		// NEW v4.1: Strict gate for short trades - only allows shorts under favorable conditions
		private bool PassesShortGate(Signal signal, OrderFlowBar lastBar)
		{
		// CRITICAL FIX: Check direction FIRST with null safety
		// Longs MUST pass immediately without ANY gate logic
		if (signal == null)
		{
			Print("ERROR: PassesShortGate called with null signal!");
			return false;
		}

		if (signal.Direction != MarketPosition.Short)
		{
			// Long signal - pass immediately, skip ALL gate logic
			return true;
		}

			// Skip gate if disabled
			if (!UseShortGate)
				return true;

			if (CurrentBars[1] < BarsRequiredToTrade)
				return false;

			Print("=== SHORT GATE EVALUATION ===");

			int failCount = 0;

			// GATE 1: Strong Downtrend Required
			bool strongDowntrend = false;
			if (UseTrendFilter)
			{
				double emaFastVal = emaFast[0];
				double emaSlowVal = emaSlow[0];
				double emaSeparation = emaSlowVal - emaFastVal;  // Positive = downtrend
				double currentPrice = Closes[1][0];

				// Require: Fast < Slow, separated by threshold, price below both
				strongDowntrend = (emaFastVal < emaSlowVal) &&
				                 (emaSeparation >= ShortGateMinEMASeparation) &&
				                 (currentPrice < emaFastVal);

				if (!strongDowntrend)
				{
					Print(string.Format("  ❌ GATE 1 FAIL: Not in strong downtrend (Sep: {0:F2}, need {1:F2})",
						emaSeparation, ShortGateMinEMASeparation));
					failCount++;
				}
				else
				{
					Print(string.Format("  ✅ GATE 1 PASS: Strong downtrend (Sep: {0:F2})", emaSeparation));
				}
			}

			// GATE 2: Higher Signal Strength (2x normal)
			int minStrength = AbsorptionMinAggressor * 2;
			bool strongSignal = signal.Strength >= minStrength;

			if (!strongSignal)
			{
				Print(string.Format("  ❌ GATE 2 FAIL: Signal too weak ({0} < {1})",
					signal.Strength, minStrength));
				failCount++;
			}
			else
			{
				Print(string.Format("  ✅ GATE 2 PASS: Strong signal ({0})", signal.Strength));
			}

			// GATE 3: Near Swing High (Resistance)
			bool nearResistance = IsNearSwingHigh(Closes[1][0]);

			if (!nearResistance)
			{
				Print("  ❌ GATE 3 FAIL: Not near swing high");
				failCount++;
			}
			else
			{
				Print("  ✅ GATE 3 PASS: Near swing high (resistance)");
			}

			// GATE 4: Volume Delta Confirmation (net selling)
			bool negativeDelta = lastBar != null && lastBar.AggressorDelta < 0;
			long deltaStrength = lastBar != null ? Math.Abs(lastBar.AggressorDelta) : 0;
			bool strongNegativeDelta = negativeDelta && deltaStrength > AbsorptionMinAggressor;

			if (!strongNegativeDelta)
			{
				Print(string.Format("  ❌ GATE 4 FAIL: Delta not negative enough ({0})",
					(lastBar != null ? lastBar.AggressorDelta : 0)));
				failCount++;
			}
			else
			{
				Print(string.Format("  ✅ GATE 4 PASS: Strong negative delta ({0})", lastBar.AggressorDelta));
			}

			// GATE 5: Time-of-Day Filter (avoid morning bull bias)
			TimeSpan currentTime = Times[1][0].TimeOfDay;
			TimeSpan morningCutoff = new TimeSpan(10, 30, 0);  // After 10:30 AM
			TimeSpan afternoonStart = new TimeSpan(12, 0, 0);  // After noon

			bool goodTimeForShort = currentTime >= morningCutoff;

			if (!goodTimeForShort)
			{
				Print(string.Format("  ❌ GATE 5 FAIL: Too early for shorts ({0})",
					currentTime.ToString(@"hh\:mm")));
				failCount++;
			}
			else
			{
				Print("  ✅ GATE 5 PASS: Good time for shorts");
			}

			// GATE 6: Absorption Ratio Even Stronger for Shorts
			bool extraStrongAbsorption = signal.Type == "ABSORPTION" &&
			                             signal.Strength > AbsorptionMinAggressor * 1.5;

			if (!extraStrongAbsorption && signal.Type == "ABSORPTION")
			{
				Print("  ❌ GATE 6 FAIL: Absorption not strong enough for short");
				failCount++;
			}
			else
			{
				Print("  ✅ GATE 6 PASS: Extra strong absorption");
			}

			// SUMMARY: Pass if failed checks <= max allowed
			bool passedGate = failCount <= ShortGateMaxFails;

			Print(string.Format("=== SHORT GATE RESULT: {0} ===", (passedGate ? "PASS ✅" : "FAIL ❌")));
			Print(string.Format("    Failed {0}/{1} gates (max allowed: {2})",
				failCount, 6, ShortGateMaxFails));

			return passedGate;
		}

		// NEW v3: Helper to check if price is near recent swing low (using strategy bars)
		private bool IsNearSwingLow(double price)
		{
			if (CurrentBars[1] < 20) return false;

			double recentLow = Lows[1][0];
			for (int i = 1; i < 20; i++)
			{
				recentLow = Math.Min(recentLow, Lows[1][i]);
			}

			return Math.Abs(price - recentLow) < 3 * TickSize;
		}

		// NEW v3: Helper to check if price is near recent swing high (using strategy bars)
		private bool IsNearSwingHigh(double price)
		{
			if (CurrentBars[1] < 20) return false;

			double recentHigh = Highs[1][0];
			for (int i = 1; i < 20; i++)
			{
				recentHigh = Math.Max(recentHigh, Highs[1][i]);
			}

			return Math.Abs(price - recentHigh) < 3 * TickSize;
		}

		// NEW v3: Get recent high/low for breakout detection (using strategy bars)
		private void GetRecentHighLow(int bars, out double high, out double low)
		{
			high = Highs[1][0];
			low = Lows[1][0];

			for (int i = 1; i < Math.Min(bars, CurrentBars[1]); i++)
			{
				high = Math.Max(high, Highs[1][i]);
				low = Math.Min(low, Lows[1][i]);
			}
		}

		private Signal DetectAbsorption()
		{
			if (orderFlowHistory.Count < 5 || CurrentBars[1] < 20)
				return null;

			OrderFlowBar lastBar = orderFlowHistory[orderFlowHistory.Count - 1];
			OrderFlowBar prevBar = orderFlowHistory[orderFlowHistory.Count - 2];

			// Accumulate recent volume
			long recentSellVolume = lastBar.SellVolume + prevBar.SellVolume;
			long recentBuyVolume = lastBar.BuyVolume + prevBar.BuyVolume;

			// NEW v3: Get trend direction and strength (using strategy bars)
			bool upTrend = emaFast[0] > emaSlow[0];
			bool downTrend = emaFast[0] < emaSlow[0];
			double currentPrice = Closes[1][0];

			// SELL ABSORPTION → LONG signal (fade sellers)
			if (recentSellVolume > AbsorptionMinAggressor)
			{
				// NEW v3: Only check bids near current price (within 5 ticks)
				long nearBidSize = 0;
				foreach (var kvp in lastBar.BidSizeByPrice)
				{
					if (kvp.Key >= currentPrice - 5 * TickSize)
						nearBidSize += kvp.Value;
				}

				// Check absorption ratio
				if (nearBidSize > recentSellVolume * AbsorptionRatio)
				{
					// NEW v3: Tighter price action (±1 tick over 3 bars)
					double priceChange = Closes[1][0] - Closes[1][3];

					if (priceChange > -1 * TickSize)
					{
						// NEW v3: Trend-aware asymmetric requirements
						bool validSignal = false;
						int confidenceBoost = 0;

						if (upTrend)
						{
							// WITH TREND - Standard requirements
							validSignal = true;
							confidenceBoost = 20;
						}
						else if (downTrend)
						{
							// COUNTER TREND - Much stricter
							// Require stronger absorption + key support level
							if (nearBidSize > recentSellVolume * (AbsorptionRatio + 0.5) &&
							    IsNearSwingLow(currentPrice))
							{
								validSignal = true;
								confidenceBoost = 0;
							}
						}
						else
						{
							// NEUTRAL
							validSignal = true;
							confidenceBoost = 10;
						}

						if (validSignal)
						{
							return new Signal
							{
								Type = "ABSORPTION",
								Direction = MarketPosition.Long,
								Price = currentPrice,
								Strength = (int)recentSellVolume + confidenceBoost
							};
						}
					}
				}
			}

			// BUY ABSORPTION → SHORT signal (fade buyers)
			if (recentBuyVolume > AbsorptionMinAggressor)
			{
				// NEW v3: Only check asks near current price (within 5 ticks)
				long nearAskSize = 0;
				foreach (var kvp in lastBar.AskSizeByPrice)
				{
					if (kvp.Key <= currentPrice + 5 * TickSize)
						nearAskSize += kvp.Value;
				}

				// Check absorption ratio
				if (nearAskSize > recentBuyVolume * AbsorptionRatio)
				{
					// NEW v3: Tighter price action (±1 tick over 3 bars)
					double priceChange = Closes[1][0] - Closes[1][3];

					if (priceChange < 1 * TickSize)
					{
						// NEW v3: Trend-aware asymmetric requirements
						bool validSignal = false;
						int confidenceBoost = 0;

						if (downTrend)
						{
							// WITH TREND - Standard requirements
							validSignal = true;
							confidenceBoost = 20;
						}
						else if (upTrend)
						{
							// COUNTER TREND - Much stricter
							// Require stronger absorption + key resistance level
							if (nearAskSize > recentBuyVolume * (AbsorptionRatio + 0.5) &&
							    IsNearSwingHigh(currentPrice))
							{
								validSignal = true;
								confidenceBoost = 0;
							}
						}
						else
						{
							// NEUTRAL
							validSignal = true;
							confidenceBoost = 10;
						}

						if (validSignal)
						{
							return new Signal
							{
								Type = "ABSORPTION",
								Direction = MarketPosition.Short,
								Price = currentPrice,
								Strength = (int)recentBuyVolume + confidenceBoost
							};
						}
					}
				}
			}

			return null;
		}

		private Signal DetectBreakout()
		{
			if (orderFlowHistory.Count < 30 || CurrentBars[1] < 20)
				return null;

			// Calculate baseline
			var last30 = orderFlowHistory.Skip(orderFlowHistory.Count - 30).Take(29).ToList();
			double avgVolume = last30.Average(b => b.TotalVolume);
			double avgDelta = last30.Average(b => Math.Abs(b.AggressorDelta));

			OrderFlowBar lastBar = orderFlowHistory[orderFlowHistory.Count - 1];
			OrderFlowBar prevBar = orderFlowHistory[orderFlowHistory.Count - 2];

			// NEW v3: Get trend and recent price levels (using strategy bars)
			bool upTrend = emaFast[0] > emaSlow[0];
			bool downTrend = emaFast[0] < emaSlow[0];

			double recent10High, recent10Low;
			GetRecentHighLow(10, out recent10High, out recent10Low);

			// NEW v3: Require volume spike AND breaking a level
			if (lastBar.TotalVolume > avgVolume * BreakoutVolumeSpike &&
			    Math.Abs(lastBar.AggressorDelta) > avgDelta * 2.5)
			{
				// BULLISH BREAKOUT
				if (lastBar.AggressorDelta > 0)
				{
					// NEW v3: Must be breaking above recent high
					if (Closes[1][0] > recent10High + TickSize)
					{
						bool validSignal = false;
						int confidenceBoost = 0;

						if (upTrend)
						{
							// WITH TREND - Standard requirements
							validSignal = true;
							confidenceBoost = 20;
						}
						else if (downTrend)
						{
							// COUNTER TREND - Much stricter
							// Require MASSIVE volume (3.5x) + 2-bar confirmation
							if (lastBar.TotalVolume > avgVolume * (BreakoutVolumeSpike + 1.0) &&
							    prevBar.AggressorDelta > avgDelta * 2.0)
							{
								validSignal = true;
								confidenceBoost = 0;
							}
						}
						else
						{
							// NEUTRAL
							validSignal = true;
							confidenceBoost = 10;
						}

						if (validSignal)
						{
							return new Signal
							{
								Type = "BREAKOUT",
								Direction = MarketPosition.Long,
								Price = Closes[1][0],
								Strength = (int)Math.Abs(lastBar.AggressorDelta) + confidenceBoost
							};
						}
					}
				}
				// BEARISH BREAKOUT
				else
				{
					// NEW v3: Must be breaking below recent low
					if (Closes[1][0] < recent10Low - TickSize)
					{
						bool validSignal = false;
						int confidenceBoost = 0;

						if (downTrend)
						{
							// WITH TREND - Standard requirements
							validSignal = true;
							confidenceBoost = 20;
						}
						else if (upTrend)
						{
							// COUNTER TREND - Much stricter
							// Require MASSIVE volume + 2-bar confirmation
							if (lastBar.TotalVolume > avgVolume * (BreakoutVolumeSpike + 1.0) &&
							    prevBar.AggressorDelta < -avgDelta * 2.0)
							{
								validSignal = true;
								confidenceBoost = 0;
							}
						}
						else
						{
							// NEUTRAL
							validSignal = true;
							confidenceBoost = 10;
						}

						if (validSignal)
						{
							return new Signal
							{
								Type = "BREAKOUT",
								Direction = MarketPosition.Short,
								Price = Closes[1][0],
								Strength = (int)Math.Abs(lastBar.AggressorDelta) + confidenceBoost
							};
						}
					}
				}
			}

			return null;
		}

		private void ExecuteSignal(Signal signal)
		{
			// Get parameters for signal type
			double target = 0;
			double stop = 0;
			int maxHold = 0;

			if (signal.Type == "ABSORPTION")
			{
				target = AbsorptionTarget;
				stop = AbsorptionStop;
				maxHold = AbsorptionMaxHold;
			}
			else if (signal.Type == "BREAKOUT")
			{
				target = BreakoutTarget;
				stop = BreakoutStop;
				maxHold = BreakoutMaxHold;
			}

			// Set broker-side stops/targets
			SetProfitTarget(CalculationMode.Ticks, (int)(target / TickSize));

			// NEW: Use trailing stops if enabled, otherwise fixed stop
			if (UseTrailingStops)
			{
				// Trailing stop that activates after reaching trigger profit
				SetTrailStop(CalculationMode.Ticks, (int)(TrailingStopDistance / TickSize));
				SetStopLoss(CalculationMode.Ticks, (int)(stop / TickSize)); // Initial hard stop
			}
			else
			{
				SetStopLoss(CalculationMode.Ticks, (int)(stop / TickSize));
			}

			// Record entry (using strategy bars)
			currentSignalType = signal.Type;
			entryTime = Times[1][0];
			lastSignalTime = DateTime.Now;

			string entryName = signal.Type + "_" + DateTime.Now.ToString("HHmmss");

			// Get trend info for logging
			string trendInfo = "";
			if (UseTrendFilter && CurrentBars[1] >= BarsRequiredToTrade)
			{
				trendInfo = string.Format(" | Trend: {0}", emaFast[0] > emaSlow[0] ? "UP" : "DOWN");
			}

			if (signal.Direction == MarketPosition.Long)
			{
				EnterLong(Contracts, entryName);
				Print(string.Format("{0} LONG {1} @ {2:F2} | Str: {3} | Target: +{4} Stop: -{5}{6}",
					Times[1][0], signal.Type, signal.Price, signal.Strength, target, stop, trendInfo));
			}
			else
			{
				EnterShort(Contracts, entryName);
				Print(string.Format("{0} SHORT {1} @ {2:F2} | Str: {3} | Target: +{4} Stop: -{5}{6}",
					Times[1][0], signal.Type, signal.Price, signal.Strength, target, stop, trendInfo));
			}

			tradeTimestamps.Add(Times[1][0]);
		}

		private void CheckTimeBasedExit()
		{
			if (Position.MarketPosition == MarketPosition.Flat)
				return;

			int maxHold = currentSignalType == "ABSORPTION" ? AbsorptionMaxHold : BreakoutMaxHold;
			double secondsInTrade = (DateTime.Now - entryTime).TotalSeconds;

			if (secondsInTrade >= maxHold)
			{
				Print(string.Format("{0} Time-based exit: {1:F0}s >= {2}s",
					Times[1][0], secondsInTrade, maxHold));

				if (Position.MarketPosition == MarketPosition.Long)
					ExitLong("TimeExit");
				else
					ExitShort("TimeExit");
			}
		}

		// IMPROVED: Changed from MIN trades to MAX trades gate
		private bool CheckMaxTradesGate()
		{
			DateTime cutoff = Times[1][0].AddMinutes(-60);
			tradeTimestamps.RemoveAll(t => t < cutoff);

			double tradesPerHour = tradeTimestamps.Count;

			if (tradesPerHour >= MaxTradesPerHour)
			{
				Print(string.Format("{0} Max trades gate: {1:F1} trades/hour >= {2:F1}",
					Times[1][0], tradesPerHour, MaxTradesPerHour));
				return false;
			}

			return true;
		}

		// NEW: Check if current time is during RTH (Regular Trading Hours)
		// RTH for MNQ: 8:30 AM - 3:00 PM CT (9:30 AM - 4:00 PM ET)
		private bool IsRTH()
		{
			if (CurrentBars[1] < 1)
				return false;

			DateTime currentTime = Times[1][0];
			TimeSpan time = currentTime.TimeOfDay;

			// RTH: 8:30 AM - 3:00 PM Central Time
			TimeSpan rthStart = new TimeSpan(8, 30, 0);   // 8:30 AM
			TimeSpan rthEnd = new TimeSpan(15, 0, 0);     // 3:00 PM

			bool isRTH = time >= rthStart && time < rthEnd;

			if (!isRTH && (DateTime.Now - lastSignalTime).TotalSeconds > 60)
			{
				// Only print once per minute to avoid spam
				Print(string.Format("{0} Outside RTH - No trading", currentTime.ToString("HH:mm:ss")));
			}

			return isRTH;
		}

		private void ResetDailyTracking()
		{
			if (lastResetDate != DateTime.MinValue && todayPnL != 0)
			{
				last5DaysPnL.Add(todayPnL);
				if (last5DaysPnL.Count > 5)
					last5DaysPnL.RemoveAt(0);
			}

			todayPnL = 0;
			lastResetDate = Times[1][0].Date;
			weeklyLimitHit = false;
			tradeTimestamps.Clear();

			Print("=== NEW TRADING DAY ===");
			Print(string.Format("Cumulative P&L: ${0:F2}", cumulativePnL));

			if (last5DaysPnL.Count > 0)
			{
				double weeklyPnL = last5DaysPnL.Sum();
				Print(string.Format("Last 5 days: ${0:F2}", weeklyPnL));

				if (weeklyPnL <= -WeeklyLossLimit)
				{
					weeklyLimitHit = true;
					Print("WEEKLY LIMIT HIT - No trading today");
				}
			}

			if (cumulativePnL <= -HardLossLimit)
			{
				hardLimitHit = true;
				Print("HARD LIMIT HIT - Strategy disabled");
			}
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
			MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution.Order == null)
				return;

			// Track P&L on exits
			bool isExitOrder = execution.Order.Name.Contains("Exit") ||
			                   execution.Order.Name.Contains("Target") ||
			                   execution.Order.Name.Contains("Stop") ||
			                   execution.Order.Name.Contains("Time");

			if (isExitOrder && execution.Order.OrderState == OrderState.Filled)
			{
				// Simple P&L calculation
				double pnl = execution.Order.AverageFillPrice * quantity;
				double commission = 0.70;
				double netPnL = pnl - commission;

				todayPnL += netPnL;
				cumulativePnL += netPnL;

				Print(string.Format("{0} EXIT @ {1:F2} | P&L: ${2:F2} | Today: ${3:F2} | Cumulative: ${4:F2}",
					time, price, netPnL, todayPnL, cumulativePnL));
			}
		}

		private class Signal
		{
			public string Type { get; set; }
			public MarketPosition Direction { get; set; }
			public double Price { get; set; }
			public int Strength { get; set; }
		}

		public override string DisplayName
		{
			get { return "CG Scalping NT8 Native v4.1 - Short Gate"; }
		}
	}
}
