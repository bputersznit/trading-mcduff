#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	/// <summary>
	/// v2.0: Execution Aggression Model
	/// - Tracks ACTUAL trades (not passive book updates)
	/// - Multi-scale confirmation (100ms/1s/5s)
	/// - Cancel tracking for book pulls
	/// - Spread filter
	/// - Cooldown timer
	/// - Realistic slippage
	/// - Event time (not DateTime.Now)
	/// </summary>
	public class CG_OrderFlow_Aggression_v2_0 : Strategy
	{
		#region Variables
		// Multi-scale buckets
		private DateTime bucket100msStart;
		private DateTime bucket1sStart;
		private DateTime bucket5sStart;

		// Execution aggression tracking (ACTUAL TRADES)
		private long aggBuyVol100ms;
		private long aggSellVol100ms;
		private long aggBuyVol1s;
		private long aggSellVol1s;
		private long aggBuyVol5s;
		private long aggSellVol5s;

		// Book liquidity tracking (for cancel detection)
		private long bidAddVol100ms;
		private long askAddVol100ms;
		private long bidRemoveVol100ms;
		private long askRemoveVol100ms;

		// Current market state
		private double currentBestBid;
		private double currentBestAsk;
		private double lastTradePrice;
		private DateTime currentMarketTime;

		private string lastSignal = "NONE";

		// Opening Range
		private double orHigh = 0;
		private double orLow = 0;
		private bool orCalculated = false;

		// Daily tracking
		private double dailyPnL = 0;
		private int consecutiveLosses = 0;
		private double dailyPeakPnL = 0;
		private DateTime lastTradeDate;
		private bool dailyLimitHit = false;

		// Position tracking
		private DateTime entryTime;
		private double entryPrice;
		private MarketPosition entryDirection;
		private int tradesCountToday = 0;

		// Cooldown
		private DateTime lastTradeExitTime;

		// Diagnostics
		private int depthEventCount = 0;
		private int tradeEventCount = 0;
		#endregion

		#region OnStateChange
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = @"Order Flow Aggression v2.0 - Execution-based imbalance with multi-scale confirmation";
				Name = "CG_OrderFlow_Aggression_v2_0";
				Calculate = Calculate.OnEachTick;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 30;
				IsFillLimitOnTouch = false;
				MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution = OrderFillResolution.Standard;
				Slippage = 3; // Realistic MNQ slippage: 3 ticks
				StartBehavior = StartBehavior.WaitUntilFlat;
				TimeInForce = TimeInForce.Gtc;
				TraceOrders = false;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade = 20;

				// Signal parameters
				MinAggressionDelta = 50;
				MinAggressionImbalance = 0.60;
				Require1sConfirmation = true;
				Require5sConfirmation = false;

				// Risk parameters
				TargetTicks = 40;
				StopTicks = 20;
				TimeoutMinutes = 10;

				// Spread filter
				MaxSpreadTicks = 3;

				// Cooldown
				CooldownSeconds = 30;
				PostStopCooldownSeconds = 60;

				// Daily limits (DISABLED for testing)
				MaxDailyLoss = 500;
				MaxConsecutiveLosses = 10;
				ProfitLockPeak = 10000;
				ProfitLockDrawdown = 2000;
				EnableDailyLimits = false; // DISABLED FOR TESTING

				// Filters
				EnableOpeningRangeFilter = true;
				EnableManipulationFilters = true;
				EnableSpreadFilter = true;
				EnableCooldown = true;
				EnableBookPullDetection = false; // Advanced feature

				EnableVerboseLogging = false;

				// RTH hours
				RTHStartHour = 9;
				RTHStartMinute = 30;
				RTHEndHour = 16;
				RTHEndMinute = 0;
			}
			else if (State == State.Configure)
			{
				// Market Depth and Market Data auto-available
			}
			else if (State == State.DataLoaded)
			{
				bucket100msStart = DateTime.MinValue;
				bucket1sStart = DateTime.MinValue;
				bucket5sStart = DateTime.MinValue;
				currentMarketTime = DateTime.MinValue;
				lastTradeExitTime = DateTime.MinValue;
				entryDirection = MarketPosition.Flat;
				ResetAllBuckets();
			}
		}
		#endregion

		#region OnBarUpdate
		protected override void OnBarUpdate()
		{
			if (CurrentBar < BarsRequiredToTrade)
				return;

			// Update market time from bar
			if (currentMarketTime == DateTime.MinValue)
				currentMarketTime = Time[0];

			// New trading day
			if (Time[0].Date != lastTradeDate)
			{
				ResetDailyTracking();
				lastTradeDate = Time[0].Date;
			}

			// Calculate Opening Range
			if (EnableOpeningRangeFilter && !orCalculated)
			{
				CalculateOpeningRange();
			}

			if (EnableDailyLimits && dailyLimitHit)
				return;

			// Manage position with TIMEOUT check
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				CheckTimeout();
			}
		}
		#endregion

		#region OnMarketData - EXECUTION AGGRESSION
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (State != State.Realtime || CurrentBar < BarsRequiredToTrade)
				return;

			// Update market time from events
			currentMarketTime = e.Time;

			// Track Last price (actual trades)
			if (e.MarketDataType == MarketDataType.Last)
			{
				tradeEventCount++;
				lastTradePrice = e.Price;
				long volume = e.Volume;

				// Initialize buckets
				if (bucket100msStart == DateTime.MinValue)
				{
					bucket100msStart = e.Time;
					bucket1sStart = e.Time;
					bucket5sStart = e.Time;
				}

				// Determine aggression: trade at ask = aggressive buy, trade at bid = aggressive sell
				bool isAggressiveBuy = false;
				bool isAggressiveSell = false;

				if (currentBestAsk > 0 && Math.Abs(e.Price - currentBestAsk) < TickSize * 0.5)
					isAggressiveBuy = true;
				else if (currentBestBid > 0 && Math.Abs(e.Price - currentBestBid) < TickSize * 0.5)
					isAggressiveSell = true;
				else
				{
					// Mid-spread execution - infer from last move
					if (e.Price > lastTradePrice)
						isAggressiveBuy = true;
					else if (e.Price < lastTradePrice)
						isAggressiveSell = true;
				}

				// Accumulate aggression
				if (isAggressiveBuy)
				{
					aggBuyVol100ms += volume;
					aggBuyVol1s += volume;
					aggBuyVol5s += volume;
				}
				else if (isAggressiveSell)
				{
					aggSellVol100ms += volume;
					aggSellVol1s += volume;
					aggSellVol5s += volume;
				}

				// Process 100ms bucket
				TimeSpan elapsed100ms = e.Time - bucket100msStart;
				if (elapsed100ms.TotalMilliseconds >= 100)
				{
					ProcessBucket100ms();
					bucket100msStart = e.Time;
					Reset100msBucket();
				}

				// Process 1s bucket
				TimeSpan elapsed1s = e.Time - bucket1sStart;
				if (elapsed1s.TotalSeconds >= 1.0)
				{
					bucket1sStart = e.Time;
					Reset1sBucket();
				}

				// Process 5s bucket
				TimeSpan elapsed5s = e.Time - bucket5sStart;
				if (elapsed5s.TotalSeconds >= 5.0)
				{
					bucket5sStart = e.Time;
					Reset5sBucket();
				}
			}
		}
		#endregion

		#region OnMarketDepth - BOOK TRACKING
		protected override void OnMarketDepth(MarketDepthEventArgs e)
		{
			if (State != State.Realtime || CurrentBar < BarsRequiredToTrade)
				return;

			depthEventCount++;

			// Update market time
			if (e.Time > currentMarketTime)
				currentMarketTime = e.Time;

			// Track best bid/ask
			if (e.MarketDataType == MarketDataType.Ask && e.Position == 0)
				currentBestAsk = e.Price;
			else if (e.MarketDataType == MarketDataType.Bid && e.Position == 0)
				currentBestBid = e.Price;

			// Track book adds/removes for pull detection
			if (EnableBookPullDetection)
			{
				if (e.Operation == Operation.Add || e.Operation == Operation.Update)
				{
					if (e.MarketDataType == MarketDataType.Bid)
						bidAddVol100ms += e.Volume;
					else if (e.MarketDataType == MarketDataType.Ask)
						askAddVol100ms += e.Volume;
				}
				else if (e.Operation == Operation.Remove)
				{
					if (e.MarketDataType == MarketDataType.Bid)
						bidRemoveVol100ms += e.Volume;
					else if (e.MarketDataType == MarketDataType.Ask)
						askRemoveVol100ms += e.Volume;
				}
			}
		}
		#endregion

		#region Bucket Processing
		private void ProcessBucket100ms()
		{
			if (aggBuyVol100ms == 0 && aggSellVol100ms == 0)
				return;

			long aggDelta = aggBuyVol100ms - aggSellVol100ms;
			long totalAggVol = aggBuyVol100ms + aggSellVol100ms;

			if (totalAggVol == 0)
				return;

			double aggImbalance = (double)aggDelta / totalAggVol;

			string signal = "NONE";

			// Primary signal from 100ms aggression
			if (aggDelta > MinAggressionDelta && aggImbalance > MinAggressionImbalance)
				signal = "LONG";
			else if (aggDelta < -MinAggressionDelta && aggImbalance < -MinAggressionImbalance)
				signal = "SHORT";

			// Multi-scale confirmation
			if (signal != "NONE" && Require1sConfirmation)
			{
				long delta1s = aggBuyVol1s - aggSellVol1s;
				if (signal == "LONG" && delta1s <= 0)
					signal = "NONE"; // 1s doesn't confirm
				else if (signal == "SHORT" && delta1s >= 0)
					signal = "NONE";
			}

			if (signal != "NONE" && Require5sConfirmation)
			{
				long delta5s = aggBuyVol5s - aggSellVol5s;
				if (signal == "LONG" && delta5s <= 0)
					signal = "NONE"; // 5s doesn't confirm
				else if (signal == "SHORT" && delta5s >= 0)
					signal = "NONE";
			}

			// Book pull detection (bearish if bid liquidity pulled, bullish if ask liquidity pulled)
			if (signal != "NONE" && EnableBookPullDetection)
			{
				long bidNetAdd = bidAddVol100ms - bidRemoveVol100ms;
				long askNetAdd = askAddVol100ms - askRemoveVol100ms;

				// If big bid pull during buy signal, cancel signal (manipulation)
				if (signal == "LONG" && bidNetAdd < -100)
					signal = "NONE";
				// If big ask pull during sell signal, cancel signal
				else if (signal == "SHORT" && askNetAdd < -100)
					signal = "NONE";
			}

			// Only act on signal CHANGES
			if (signal != "NONE" && signal != lastSignal)
			{
				if (CanTakeSignal(signal))
				{
					EnterPosition(signal, totalAggVol);
				}
				lastSignal = signal;
			}
			else if (signal == "NONE")
			{
				lastSignal = "NONE";
			}
		}

		private void ResetAllBuckets()
		{
			Reset100msBucket();
			Reset1sBucket();
			Reset5sBucket();
		}

		private void Reset100msBucket()
		{
			aggBuyVol100ms = 0;
			aggSellVol100ms = 0;
			bidAddVol100ms = 0;
			askAddVol100ms = 0;
			bidRemoveVol100ms = 0;
			askRemoveVol100ms = 0;
		}

		private void Reset1sBucket()
		{
			aggBuyVol1s = 0;
			aggSellVol1s = 0;
		}

		private void Reset5sBucket()
		{
			aggBuyVol5s = 0;
			aggSellVol5s = 0;
		}
		#endregion

		#region Signal Validation
		private bool CanTakeSignal(string signal)
		{
			if (Position.MarketPosition != MarketPosition.Flat)
				return false;

			if (!IsRTH())
				return false;

			if (EnableDailyLimits && dailyLimitHit)
				return false;

			if (EnableOpeningRangeFilter && !orCalculated)
				return false;

			// Spread filter
			if (EnableSpreadFilter)
			{
				double spreadTicks = (currentBestAsk - currentBestBid) / TickSize;
				if (spreadTicks > MaxSpreadTicks || spreadTicks < 0.5)
					return false; // Spread too wide or crossed book
			}

			// Cooldown
			if (EnableCooldown && lastTradeExitTime != DateTime.MinValue)
			{
				TimeSpan timeSinceLastTrade = currentMarketTime - lastTradeExitTime;
				double requiredCooldown = (consecutiveLosses > 0) ? PostStopCooldownSeconds : CooldownSeconds;

				if (timeSinceLastTrade.TotalSeconds < requiredCooldown)
					return false;
			}

			if (EnableManipulationFilters && !PassesManipulationFilters(signal))
				return false;

			return true;
		}

		private bool PassesManipulationFilters(string signal)
		{
			string timeZone = GetTimeZone();
			string orLocation = GetORLocation();

			// Rule 1: No shorts during OPEN_15
			if (timeZone == "OPEN_15" && signal == "SHORT")
				return false;

			// Rule 2: No shorts POST_OPEN when ABOVE_OR
			if (timeZone == "POST_OPEN" && orLocation == "ABOVE_OR" && signal == "SHORT")
				return false;

			// Rule 3: No shorts POST_OPEN when INSIDE_OR
			if (timeZone == "POST_OPEN" && orLocation == "INSIDE_OR" && signal == "SHORT")
				return false;

			// Rule 4: No longs NORMAL when INSIDE_OR
			if (timeZone == "NORMAL" && orLocation == "INSIDE_OR" && signal == "LONG")
				return false;

			// Rule 5: No shorts CLOSE_30 when ABOVE_OR
			if (timeZone == "CLOSE_30" && orLocation == "ABOVE_OR" && signal == "SHORT")
				return false;

			// Rule 6: No longs CLOSE_30 when BELOW_OR
			if (timeZone == "CLOSE_30" && orLocation == "BELOW_OR" && signal == "LONG")
				return false;

			return true;
		}
		#endregion

		#region Entry/Exit Logic
		private void EnterPosition(string signal, long aggVolume)
		{
			entryTime = currentMarketTime;
			tradesCountToday++;

			if (signal == "LONG")
			{
				EnterLong(1, "OFI_Long");
				entryPrice = currentBestAsk; // Assume fill at ask + slippage
				entryDirection = MarketPosition.Long;
			}
			else if (signal == "SHORT")
			{
				EnterShort(1, "OFI_Short");
				entryPrice = currentBestBid; // Assume fill at bid - slippage
				entryDirection = MarketPosition.Short;
			}

			if (EnableVerboseLogging)
			{
				double spread = (currentBestAsk - currentBestBid) / TickSize;
				Print(string.Format("{0} | ENTRY {1} @ {2} | TZ:{3} OR:{4} | AggVol:{5} Spread:{6:F1}",
					currentMarketTime.ToString("HH:mm:ss.fff"), signal, entryPrice, GetTimeZone(), GetORLocation(), aggVolume, spread));
			}
		}

		private void CheckTimeout()
		{
			// Use EVENT TIME not DateTime.Now or Time[0]
			TimeSpan holdTime = currentMarketTime - entryTime;

			if (holdTime.TotalMinutes >= TimeoutMinutes)
			{
				if (Position.MarketPosition == MarketPosition.Long)
					ExitLong("OFI_Timeout");
				else if (Position.MarketPosition == MarketPosition.Short)
					ExitShort("OFI_Timeout");

				if (EnableVerboseLogging)
					Print(string.Format("{0} | TIMEOUT EXIT after {1:F1} min", currentMarketTime.ToString("HH:mm:ss"), holdTime.TotalMinutes));
			}
		}
		#endregion

		#region OnExecutionUpdate
		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution.Order == null || !execution.Order.Name.StartsWith("OFI_"))
				return;

			if (execution.Order.OrderState == OrderState.Filled)
			{
				// ENTRY FILL
				if (execution.Order.Name == "OFI_Long" || execution.Order.Name == "OFI_Short")
				{
					double targetPrice = 0;
					double stopPrice = 0;

					if (Position.MarketPosition == MarketPosition.Long)
					{
						targetPrice = execution.Price + (TargetTicks * TickSize);
						stopPrice = execution.Price - (StopTicks * TickSize);
						ExitLongLimit(1, targetPrice, "OFI_Target", "OFI_Long");
						ExitLongStopMarket(1, stopPrice, "OFI_Stop", "OFI_Long");
					}
					else if (Position.MarketPosition == MarketPosition.Short)
					{
						targetPrice = execution.Price - (TargetTicks * TickSize);
						stopPrice = execution.Price + (StopTicks * TickSize);
						ExitShortLimit(1, targetPrice, "OFI_Target", "OFI_Short");
						ExitShortStopMarket(1, stopPrice, "OFI_Stop", "OFI_Short");
					}

					Print(string.Format("{0} | #{1} FILL {2} @ {3} | Tgt:{4} Stp:{5}",
						time.ToShortTimeString(), tradesCountToday, Position.MarketPosition,
						execution.Price, targetPrice, stopPrice));
				}

				// EXIT FILL
				else if (execution.Order.Name == "OFI_Target" || execution.Order.Name == "OFI_Stop" ||
				         execution.Order.Name == "OFI_Timeout" || execution.Order.Name == "Close position")
				{
					// Calculate P&L from entry/exit prices
					double tradePnL = 0;

					if (entryDirection == MarketPosition.Long)
						tradePnL = (execution.Price - entryPrice) * execution.Quantity * Instrument.MasterInstrument.PointValue;
					else if (entryDirection == MarketPosition.Short)
						tradePnL = (entryPrice - execution.Price) * execution.Quantity * Instrument.MasterInstrument.PointValue;

					lastTradeExitTime = currentMarketTime;
					UpdateDailyTracking(tradePnL, execution.Order.Name);

					Print(string.Format("{0} | #{1} EXIT {2} | P&L:${3:F2} | Daily:${4:F2}/{5} | Limits:{6}",
						time.ToShortTimeString(), tradesCountToday, execution.Order.Name.Replace("OFI_", ""),
						tradePnL, dailyPnL, consecutiveLosses, EnableDailyLimits ? "ON" : "OFF"));

					// Reset entry direction
					entryDirection = MarketPosition.Flat;
				}
			}
		}
		#endregion

		#region Daily Tracking
		private void ResetDailyTracking()
		{
			dailyPnL = 0;
			consecutiveLosses = 0;
			dailyPeakPnL = 0;
			dailyLimitHit = false;
			orCalculated = false;
			orHigh = 0;
			orLow = 0;
			tradesCountToday = 0;
			depthEventCount = 0;
			tradeEventCount = 0;

			Print(string.Format("========== {0} - NEW TRADING DAY ==========", Time[0].ToShortDateString()));
		}

		private void UpdateDailyTracking(double tradePnL, string exitReason)
		{
			dailyPnL += tradePnL;

			if (dailyPnL > dailyPeakPnL)
				dailyPeakPnL = dailyPnL;

			if (tradePnL < 0)
				consecutiveLosses++;
			else
				consecutiveLosses = 0;

			// Daily limit enforcement (if enabled)
			if (EnableDailyLimits)
			{
				if (dailyPnL <= -MaxDailyLoss)
				{
					dailyLimitHit = true;
					Print(string.Format("*** DAILY LOSS LIMIT HIT: ${0:F2} ***", dailyPnL));
				}

				if (consecutiveLosses >= MaxConsecutiveLosses)
				{
					dailyLimitHit = true;
					Print(string.Format("*** CONSECUTIVE LOSS LIMIT HIT: {0} losses ***", consecutiveLosses));
				}

				double drawdown = dailyPnL - dailyPeakPnL;
				if (dailyPeakPnL >= ProfitLockPeak && drawdown <= -ProfitLockDrawdown)
				{
					dailyLimitHit = true;
					Print(string.Format("*** PROFIT LOCK TRIGGERED: Peak ${0:F2}, DD ${1:F2} ***", dailyPeakPnL, drawdown));
				}
			}
		}
		#endregion

		#region Helper Methods
		private void CalculateOpeningRange()
		{
			DateTime nowET = TimeZoneInfo.ConvertTime(Time[0], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
			DateTime orStart = new DateTime(nowET.Year, nowET.Month, nowET.Day, 9, 30, 0);
			DateTime orEnd = new DateTime(nowET.Year, nowET.Month, nowET.Day, 9, 45, 0);

			if (nowET >= orEnd)
			{
				for (int i = 0; i < Math.Min(CurrentBar, 100); i++)
				{
					DateTime barTimeET = TimeZoneInfo.ConvertTime(Time[i], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

					if (barTimeET >= orStart && barTimeET < orEnd)
					{
						if (orHigh == 0 || High[i] > orHigh)
							orHigh = High[i];
						if (orLow == 0 || Low[i] < orLow)
							orLow = Low[i];
					}
				}

				if (orHigh > 0 && orLow > 0)
				{
					orCalculated = true;
					Print(string.Format("{0} | OR CALCULATED: High={1} Low={2}", Time[0].ToShortTimeString(), orHigh, orLow));
				}
			}
		}

		private bool IsRTH()
		{
			DateTime nowET = TimeZoneInfo.ConvertTime(Time[0], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
			TimeSpan now = nowET.TimeOfDay;
			TimeSpan rthStart = new TimeSpan(RTHStartHour, RTHStartMinute, 0);
			TimeSpan rthEnd = new TimeSpan(RTHEndHour, RTHEndMinute, 0);
			return now >= rthStart && now < rthEnd;
		}

		private string GetTimeZone()
		{
			DateTime nowET = TimeZoneInfo.ConvertTime(Time[0], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
			int hour = nowET.Hour;
			int minute = nowET.Minute;

			if (hour == 9 && minute < 45)
				return "OPEN_15";
			else if ((hour == 9 && minute >= 45) || (hour == 10 && minute < 30))
				return "POST_OPEN";
			else if (hour == 15 && minute >= 30)
				return "CLOSE_30";
			else
				return "NORMAL";
		}

		private string GetORLocation()
		{
			if (!orCalculated || orHigh == 0 || orLow == 0)
				return "UNKNOWN";

			double currentPrice = Close[0];

			if (currentPrice > orHigh)
				return "ABOVE_OR";
			else if (currentPrice < orLow)
				return "BELOW_OR";
			else
				return "INSIDE_OR";
		}
		#endregion

		#region Properties
		[NinjaScriptProperty]
		[Range(10, 200)]
		[Display(Name="Min Aggression Delta", Order=1, GroupName="1. Signal")]
		public int MinAggressionDelta { get; set; }

		[NinjaScriptProperty]
		[Range(0.3, 0.9)]
		[Display(Name="Min Aggression Imbalance", Order=2, GroupName="1. Signal")]
		public double MinAggressionImbalance { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Require 1s Confirmation", Order=3, GroupName="1. Signal")]
		public bool Require1sConfirmation { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Require 5s Confirmation", Order=4, GroupName="1. Signal")]
		public bool Require5sConfirmation { get; set; }

		[NinjaScriptProperty]
		[Range(10, 100)]
		[Display(Name="Target (ticks)", Order=1, GroupName="2. Risk")]
		public int TargetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(5, 50)]
		[Display(Name="Stop (ticks)", Order=2, GroupName="2. Risk")]
		public int StopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 30)]
		[Display(Name="Timeout (minutes)", Order=3, GroupName="2. Risk")]
		public int TimeoutMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name="Max Spread (ticks)", Order=4, GroupName="2. Risk")]
		public int MaxSpreadTicks { get; set; }

		[NinjaScriptProperty]
		[Range(5, 300)]
		[Display(Name="Cooldown (seconds)", Order=1, GroupName="3. Cooldown")]
		public int CooldownSeconds { get; set; }

		[NinjaScriptProperty]
		[Range(10, 600)]
		[Display(Name="Post-Stop Cooldown (seconds)", Order=2, GroupName="3. Cooldown")]
		public int PostStopCooldownSeconds { get; set; }

		[NinjaScriptProperty]
		[Range(30, 2000)]
		[Display(Name="Max Daily Loss ($)", Order=1, GroupName="4. Daily Limits")]
		public double MaxDailyLoss { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name="Max Consecutive Losses", Order=2, GroupName="4. Daily Limits")]
		public int MaxConsecutiveLosses { get; set; }

		[NinjaScriptProperty]
		[Range(500, 20000)]
		[Display(Name="Profit Lock Peak ($)", Order=3, GroupName="4. Daily Limits")]
		public double ProfitLockPeak { get; set; }

		[NinjaScriptProperty]
		[Range(100, 5000)]
		[Display(Name="Profit Lock Drawdown ($)", Order=4, GroupName="4. Daily Limits")]
		public double ProfitLockDrawdown { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Daily Limits", Order=1, GroupName="5. Filters")]
		public bool EnableDailyLimits { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Opening Range Filter", Order=2, GroupName="5. Filters")]
		public bool EnableOpeningRangeFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Manipulation Filters", Order=3, GroupName="5. Filters")]
		public bool EnableManipulationFilters { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Spread Filter", Order=4, GroupName="5. Filters")]
		public bool EnableSpreadFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Cooldown", Order=5, GroupName="5. Filters")]
		public bool EnableCooldown { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Book Pull Detection", Order=6, GroupName="5. Filters")]
		public bool EnableBookPullDetection { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Verbose Logging", Order=7, GroupName="5. Filters")]
		public bool EnableVerboseLogging { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name="RTH Start Hour", Order=1, GroupName="6. RTH")]
		public int RTHStartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name="RTH Start Minute", Order=2, GroupName="6. RTH")]
		public int RTHStartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name="RTH End Hour", Order=3, GroupName="6. RTH")]
		public int RTHEndHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name="RTH End Minute", Order=4, GroupName="6. RTH")]
		public int RTHEndMinute { get; set; }
		#endregion
	}
}
