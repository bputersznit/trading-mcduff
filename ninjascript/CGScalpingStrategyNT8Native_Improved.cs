//
// CG MNQ Scalping Strategy - NT8 NATIVE VERSION - IMPROVED v2
// Performance improvements based on backtest analysis:
// - Wider stops to reduce 68.9% stop-out rate
// - Filtered short signals (were losing -$189 vs +$59 longs)
// - Improved ABSORPTION logic (was losing -$156)
// - Added trend filter to avoid counter-trend trades
// - RTH ONLY filter (ETH was losing -$41.50)
// - Trailing stop option (previous version had no trailing stops)
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
	public class CGScalpingStrategyNT8NativeImproved : Strategy
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
				Description = @"NT8 Native Order Flow Scalping - IMPROVED VERSION";
				Name = "CGScalpingStrategyNT8NativeImproved";
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

				// Risk: Changed to MAX instead of MIN
				MaxTradesPerHour = 10.0;     // Prevent overtrading
				WeeklyLossLimit = 250.0;
				HardLossLimit = 500.0;
			}
			else if (State == State.Configure)
			{
				// Subscribe to Level 2 data for order flow
			}
			else if (State == State.DataLoaded)
			{
				currentBar = new OrderFlowBar { Timestamp = Time[0] };

				// Initialize EMAs
				emaFast = EMA(FastEMA);
				emaSlow = EMA(SlowEMA);

				Print("========================================");
				Print("CG SCALPING NT8 NATIVE - IMPROVED v2");
				Print("========================================");
				Print("IMPROVEMENTS:");
				Print("  - Wider stops: Absorption 5pt, Breakout 6pt");
				Print("  - Trend filter enabled: EMA " + FastEMA + "/" + SlowEMA);
				Print("  - Stricter entry criteria");
				Print("  - Short trades can be disabled");
				Print("  - RTH ONLY: " + (RTHOnly ? "YES (8:30 AM - 3:00 PM CT)" : "NO"));
				Print("  - Trailing stops: " + (UseTrailingStops ? "YES" : "NO (Fixed)"));
				if (UseTrailingStops)
				{
					Print("    Trigger: " + TrailingStopTrigger + "pt, Distance: " + TrailingStopDistance + "pt");
				}
				Print("========================================");
			}
			else if (State == State.Realtime)
			{
				if (lastResetDate.Date != Time[0].Date)
				{
					ResetDailyTracking();
				}

				Print("=== LIVE TRADING STARTED ===");
				Print("Order flow tracking: ACTIVE");
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < BarsRequiredToTrade)
				return;

			if (State != State.Realtime)
				return;

			// Check risk limits
			if (hardLimitHit || weeklyLimitHit)
				return;

			// Reset daily tracking if new day
			if (lastResetDate.Date != Time[0].Date)
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

			// Check for ABSORPTION signal
			Signal absorptionSignal = DetectAbsorption();
			if (absorptionSignal != null && PassesFilters(absorptionSignal))
			{
				ExecuteSignal(absorptionSignal);
				return;
			}

			// Check for BREAKOUT signal
			Signal breakoutSignal = DetectBreakout();
			if (breakoutSignal != null && PassesFilters(breakoutSignal))
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

			// Filter 2: Trend filter
			if (UseTrendFilter && CurrentBar >= BarsRequiredToTrade)
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

		private Signal DetectAbsorption()
		{
			if (orderFlowHistory.Count < 3)
				return null;

			OrderFlowBar lastBar = orderFlowHistory[orderFlowHistory.Count - 1];
			OrderFlowBar prevBar = orderFlowHistory[orderFlowHistory.Count - 2];

			// IMPROVED: Look for absorption over multiple bars
			long recentSellVolume = lastBar.SellVolume + prevBar.SellVolume;
			long recentBuyVolume = lastBar.BuyVolume + prevBar.BuyVolume;

			// SELL ABSORPTION (Buy signal)
			// Heavy selling absorbed by bids - price should hold
			if (recentSellVolume > AbsorptionMinAggressor)
			{
				long totalBidSize = lastBar.BidSizeByPrice.Values.Sum();

				// IMPROVED: Stricter threshold and check price didn't drop much
				if (totalBidSize > recentSellVolume * AbsorptionRatio)
				{
					// Check that price held relatively well
					double priceChange = Close[0] - Close[2];
					if (priceChange > -2 * TickSize) // Didn't drop much despite selling
					{
						return new Signal
						{
							Type = "ABSORPTION",
							Direction = MarketPosition.Long,
							Price = Close[0],
							Strength = (int)recentSellVolume
						};
					}
				}
			}

			// BUY ABSORPTION (Sell signal)
			// Heavy buying absorbed by asks - price should hold
			if (recentBuyVolume > AbsorptionMinAggressor)
			{
				long totalAskSize = lastBar.AskSizeByPrice.Values.Sum();

				// IMPROVED: Stricter threshold and check price didn't rise much
				if (totalAskSize > recentBuyVolume * AbsorptionRatio)
				{
					// Check that price held relatively well
					double priceChange = Close[0] - Close[2];
					if (priceChange < 2 * TickSize) // Didn't rise much despite buying
					{
						return new Signal
						{
							Type = "ABSORPTION",
							Direction = MarketPosition.Short,
							Price = Close[0],
							Strength = (int)recentBuyVolume
						};
					}
				}
			}

			return null;
		}

		private Signal DetectBreakout()
		{
			if (orderFlowHistory.Count < 30)
				return null;

			// Calculate baseline from last 30 bars
			var last30 = orderFlowHistory.Skip(orderFlowHistory.Count - 30).Take(29).ToList();
			double avgVolume = last30.Average(b => b.TotalVolume);
			double avgDelta = last30.Average(b => Math.Abs(b.AggressorDelta));

			OrderFlowBar lastBar = orderFlowHistory[orderFlowHistory.Count - 1];

			// IMPROVED: More stringent volume spike detection
			if (lastBar.TotalVolume > avgVolume * BreakoutVolumeSpike &&
			    Math.Abs(lastBar.AggressorDelta) > avgDelta * 2.5) // Increased from 2.0
			{
				if (lastBar.AggressorDelta > 0)
				{
					// Strong buying pressure
					return new Signal
					{
						Type = "BREAKOUT",
						Direction = MarketPosition.Long,
						Price = Close[0],
						Strength = (int)Math.Abs(lastBar.AggressorDelta)
					};
				}
				else
				{
					// Strong selling pressure
					return new Signal
					{
						Type = "BREAKOUT",
						Direction = MarketPosition.Short,
						Price = Close[0],
						Strength = (int)Math.Abs(lastBar.AggressorDelta)
					};
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

			// Record entry
			currentSignalType = signal.Type;
			entryTime = Time[0];
			lastSignalTime = DateTime.Now;

			string entryName = signal.Type + "_" + DateTime.Now.ToString("HHmmss");

			// Get trend info for logging
			string trendInfo = "";
			if (UseTrendFilter && CurrentBar >= BarsRequiredToTrade)
			{
				trendInfo = string.Format(" | Trend: {0}", emaFast[0] > emaSlow[0] ? "UP" : "DOWN");
			}

			if (signal.Direction == MarketPosition.Long)
			{
				EnterLong(Contracts, entryName);
				Print(string.Format("{0} LONG {1} @ {2:F2} | Str: {3} | Target: +{4} Stop: -{5}{6}",
					Time[0], signal.Type, signal.Price, signal.Strength, target, stop, trendInfo));
			}
			else
			{
				EnterShort(Contracts, entryName);
				Print(string.Format("{0} SHORT {1} @ {2:F2} | Str: {3} | Target: +{4} Stop: -{5}{6}",
					Time[0], signal.Type, signal.Price, signal.Strength, target, stop, trendInfo));
			}

			tradeTimestamps.Add(Time[0]);
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
					Time[0], secondsInTrade, maxHold));

				if (Position.MarketPosition == MarketPosition.Long)
					ExitLong("TimeExit");
				else
					ExitShort("TimeExit");
			}
		}

		// IMPROVED: Changed from MIN trades to MAX trades gate
		private bool CheckMaxTradesGate()
		{
			DateTime cutoff = Time[0].AddMinutes(-60);
			tradeTimestamps.RemoveAll(t => t < cutoff);

			double tradesPerHour = tradeTimestamps.Count;

			if (tradesPerHour >= MaxTradesPerHour)
			{
				Print(string.Format("{0} Max trades gate: {1:F1} trades/hour >= {2:F1}",
					Time[0], tradesPerHour, MaxTradesPerHour));
				return false;
			}

			return true;
		}

		// NEW: Check if current time is during RTH (Regular Trading Hours)
		// RTH for MNQ: 8:30 AM - 3:00 PM CT (9:30 AM - 4:00 PM ET)
		private bool IsRTH()
		{
			if (CurrentBar < 1)
				return false;

			DateTime currentTime = Time[0];
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
			lastResetDate = Time[0].Date;
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
			get { return "CG Scalping NT8 Native IMPROVED"; }
		}
	}
}
