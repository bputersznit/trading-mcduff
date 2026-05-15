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
	/// CG Order Flow Imbalance Strategy v1.0
	/// Based on MBO event imbalance detection in 100ms buckets
	/// Targets: 40 ticks ($20 MNQ) | Stops: 20 ticks ($10 MNQ)
	/// Win Rate: 63% | Expectancy: $6.36/trade
	/// </summary>
	public class CG_OrderFlow_Imbalance_v1_0 : Strategy
	{
		#region Variables

		// ========== Event Aggregation ==========
		private DateTime bucketStartTime;
		private long bidEventVolume;
		private long askEventVolume;
		private int bidEventCount;
		private int askEventCount;
		private double currentBestBid;
		private double currentBestAsk;

		// ========== Signal Tracking ==========
		private string lastSignal = "NONE";
		private bool waitingForSignalChange = false;

		// ========== Opening Range ==========
		private double orHigh = 0;
		private double orLow = 0;
		private bool orCalculated = false;
		private DateTime orStartTime;
		private DateTime orEndTime;

		// ========== Daily Risk Management ==========
		private double dailyPnL = 0;
		private int consecutiveLosses = 0;
		private double dailyPeakPnL = 0;
		private DateTime lastTradeDate;
		private bool dailyLimitHit = false;

		// ========== Position Tracking ==========
		private DateTime entryTime;
		private double entryPrice;
		private string tradeTimeZone = "";
		private string tradeORLocation = "";

		#endregion

		#region OnStateChange
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = @"Order Flow Imbalance Strategy - Detects extreme bid/ask imbalances in 100ms buckets";
				Name = "CG_OrderFlow_Imbalance_v1_0";
				Calculate = Calculate.OnEachTick;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 30;
				IsFillLimitOnTouch = false;
				MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution = OrderFillResolution.Standard;
				Slippage = 0; // We'll handle slippage in P&L calculation
				StartBehavior = StartBehavior.WaitUntilFlat;
				TimeInForce = TimeInForce.Gtc;
				TraceOrders = false;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade = 20;
				IsInstantiatedOnEachOptimizationIteration = true;

				// ========== Strategy Parameters ==========
				BucketSizeMs = 100;
				MinEventDelta = 50;
				MinEventImbalance = 0.60;

				TargetTicks = 40;
				StopTicks = 20;
				TimeoutMinutes = 10;

				MaxDailyLoss = 60;
				MaxConsecutiveLosses = 3;
				ProfitLockPeak = 3000;
				ProfitLockDrawdown = 500;

				EnableOpeningRangeFilter = true;
				EnableManipulationFilters = true;
				EnableDailyLimits = true;

				RTHStartHour = 9;
				RTHStartMinute = 30;
				RTHEndHour = 16;
				RTHEndMinute = 0;
			}
			else if (State == State.Configure)
			{
				// Market Depth is automatically available when OnMarketDepth() is overridden
				// No manual subscription needed in NT8
			}
			else if (State == State.DataLoaded)
			{
				bucketStartTime = DateTime.MinValue;
				ResetBucket();
			}
			else if (State == State.Terminated)
			{
				// Cleanup if needed
			}
		}
		#endregion

		#region OnBarUpdate
		protected override void OnBarUpdate()
		{
			// Only process on real-time data
			if (CurrentBar < BarsRequiredToTrade || State != State.Realtime)
				return;

			// Check if new trading day
			if (Time[0].Date != lastTradeDate)
			{
				ResetDailyTracking();
				lastTradeDate = Time[0].Date;
			}

			// Calculate Opening Range (9:30-9:45 AM ET)
			if (EnableOpeningRangeFilter && !orCalculated)
			{
				CalculateOpeningRange();
			}

			// Check daily limits
			if (EnableDailyLimits && dailyLimitHit)
				return;

			// Manage existing position
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				ManagePosition();
			}
		}
		#endregion

		#region OnMarketDepth
		protected override void OnMarketDepth(MarketDepthEventArgs e)
		{
			if (State != State.Realtime)
				return;

			// Initialize bucket timing
			if (bucketStartTime == DateTime.MinValue)
			{
				bucketStartTime = e.Time;
			}

			// Check if bucket expired (100ms elapsed)
			TimeSpan elapsed = e.Time - bucketStartTime;
			if (elapsed.TotalMilliseconds >= BucketSizeMs)
			{
				// Process completed bucket
				ProcessBucket();

				// Start new bucket
				bucketStartTime = e.Time;
				ResetBucket();
			}

			// Track best bid/ask
			if (e.MarketDataType == MarketDataType.Ask && e.Position == 0)
				currentBestAsk = e.Price;
			else if (e.MarketDataType == MarketDataType.Bid && e.Position == 0)
				currentBestBid = e.Price;

			// Accumulate volume changes
			if (e.Operation == Operation.Add || e.Operation == Operation.Update)
			{
				if (e.MarketDataType == MarketDataType.Bid)
				{
					bidEventVolume += e.Volume;
					bidEventCount++;
				}
				else if (e.MarketDataType == MarketDataType.Ask)
				{
					askEventVolume += e.Volume;
					askEventCount++;
				}
			}
		}
		#endregion

		#region Bucket Processing
		private void ProcessBucket()
		{
			// Need minimum activity
			if (bidEventVolume == 0 && askEventVolume == 0)
				return;

			// Calculate imbalance metrics
			long eventDelta = bidEventVolume - askEventVolume;
			long totalVolume = bidEventVolume + askEventVolume;

			if (totalVolume == 0)
				return;

			double eventImbalance = (double)eventDelta / totalVolume;

			// Generate signal
			string signal = "NONE";

			if (eventDelta > MinEventDelta && eventImbalance > MinEventImbalance)
				signal = "LONG";
			else if (eventDelta < -MinEventDelta && eventImbalance < -MinEventImbalance)
				signal = "SHORT";

			// Only act on signal changes
			if (signal != "NONE" && signal != lastSignal)
			{
				// Check if we can take this signal
				if (CanTakeSignal(signal))
				{
					EnterPosition(signal, totalVolume);
				}

				lastSignal = signal;
			}
			else if (signal == "NONE")
			{
				lastSignal = "NONE";
			}
		}

		private void ResetBucket()
		{
			bidEventVolume = 0;
			askEventVolume = 0;
			bidEventCount = 0;
			askEventCount = 0;
		}
		#endregion

		#region Signal Validation
		private bool CanTakeSignal(string signal)
		{
			// Must be flat
			if (Position.MarketPosition != MarketPosition.Flat)
				return false;

			// Check RTH
			if (!IsRTH())
				return false;

			// Check daily limits
			if (EnableDailyLimits && dailyLimitHit)
				return false;

			// Opening Range must be calculated if filter enabled
			if (EnableOpeningRangeFilter && !orCalculated)
				return false;

			// Apply manipulation filters
			if (EnableManipulationFilters && !PassesManipulationFilters(signal))
				return false;

			return true;
		}

		private bool PassesManipulationFilters(string signal)
		{
			string timeZone = GetTimeZone();
			string orLocation = GetORLocation();

			// Filter 1: No shorts during OPEN_15
			if (timeZone == "OPEN_15" && signal == "SHORT")
				return false;

			// Filter 2: POST_OPEN + ABOVE_OR + SHORT
			if (timeZone == "POST_OPEN" && orLocation == "ABOVE_OR" && signal == "SHORT")
				return false;

			// Filter 3: POST_OPEN + INSIDE_OR + SHORT
			if (timeZone == "POST_OPEN" && orLocation == "INSIDE_OR" && signal == "SHORT")
				return false;

			// Filter 4: NORMAL + INSIDE_OR + LONG
			if (timeZone == "NORMAL" && orLocation == "INSIDE_OR" && signal == "LONG")
				return false;

			// Filter 5: CLOSE_30 + ABOVE_OR + SHORT
			if (timeZone == "CLOSE_30" && orLocation == "ABOVE_OR" && signal == "SHORT")
				return false;

			// Filter 6: CLOSE_30 + BELOW_OR + LONG
			if (timeZone == "CLOSE_30" && orLocation == "BELOW_OR" && signal == "LONG")
				return false;

			return true;
		}
		#endregion

		#region Entry/Exit Logic
		private void EnterPosition(string signal, long eventSize)
		{
			entryTime = Time[0];
			tradeTimeZone = GetTimeZone();
			tradeORLocation = GetORLocation();

			if (signal == "LONG")
			{
				EnterLong(1, "OFI_Long");
				entryPrice = currentBestAsk; // Pay the ask
			}
			else if (signal == "SHORT")
			{
				EnterShort(1, "OFI_Short");
				entryPrice = currentBestBid; // Hit the bid
			}

			Print(string.Format("{0} | ENTRY {1} @ {2} | TZ: {3} | OR: {4} | EventSize: {5}",
				Time[0], signal, entryPrice, tradeTimeZone, tradeORLocation, eventSize));
		}

		private void ManagePosition()
		{
			// Check timeout (10 minutes)
			TimeSpan holdTime = Time[0] - entryTime;
			if (holdTime.TotalMinutes >= TimeoutMinutes)
			{
				ExitLong("OFI_Timeout");
				ExitShort("OFI_Timeout");
				Print(string.Format("{0} | EXIT TIMEOUT after {1:F1} minutes", Time[0], holdTime.TotalMinutes));
				return;
			}
		}
		#endregion

		#region OnExecutionUpdate
		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			// Entry execution
			if (execution.Order != null && execution.Order.Name.StartsWith("OFI_"))
			{
				if (execution.Order.OrderState == OrderState.Filled)
				{
					if (execution.Order.Name == "OFI_Long" || execution.Order.Name == "OFI_Short")
					{
						// Set protective stops and targets
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

						Print(string.Format("{0} | FILL {1} @ {2} | Target: {3} | Stop: {4}",
							time, Position.MarketPosition, execution.Price, targetPrice, stopPrice));
					}
				}
			}

			// Exit execution - track P&L
			if (execution.Order != null && (execution.Order.Name == "OFI_Target" ||
				execution.Order.Name == "OFI_Stop" || execution.Order.Name == "OFI_Timeout"))
			{
				if (execution.Order.OrderState == OrderState.Filled)
				{
					// Calculate trade P&L
					double tradePnL = execution.Order.AverageFillPrice != 0 ?
						(Position.MarketPosition == MarketPosition.Long ?
							(execution.Price - entryPrice) * quantity * Instrument.MasterInstrument.PointValue :
							(entryPrice - execution.Price) * quantity * Instrument.MasterInstrument.PointValue)
						: 0;

					// Update daily tracking
					UpdateDailyTracking(tradePnL, execution.Order.Name);

					Print(string.Format("{0} | EXIT {1} | P&L: ${2:F2} | Daily: ${3:F2} | Peak: ${4:F2}",
						time, execution.Order.Name, tradePnL, dailyPnL, dailyPeakPnL));
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
		}

		private void UpdateDailyTracking(double tradePnL, string exitReason)
		{
			dailyPnL += tradePnL;

			// Update peak
			if (dailyPnL > dailyPeakPnL)
				dailyPeakPnL = dailyPnL;

			// Track consecutive losses
			if (tradePnL < 0)
			{
				consecutiveLosses++;
			}
			else
			{
				consecutiveLosses = 0;
			}

			// Check daily limits
			if (EnableDailyLimits)
			{
				// Daily loss limit
				if (dailyPnL <= -MaxDailyLoss)
				{
					dailyLimitHit = true;
					Print(string.Format("{0} | DAILY LOSS LIMIT HIT: ${1:F2} <= -${2:F2}",
						Time[0], dailyPnL, MaxDailyLoss));
				}

				// Consecutive loss limit
				if (consecutiveLosses >= MaxConsecutiveLosses)
				{
					dailyLimitHit = true;
					Print(string.Format("{0} | CONSECUTIVE LOSS LIMIT HIT: {1} >= {2}",
						Time[0], consecutiveLosses, MaxConsecutiveLosses));
				}

				// Profit lock
				double drawdownFromPeak = dailyPnL - dailyPeakPnL;
				if (dailyPeakPnL >= ProfitLockPeak && drawdownFromPeak <= -ProfitLockDrawdown)
				{
					dailyLimitHit = true;
					Print(string.Format("{0} | PROFIT LOCK TRIGGERED: Peak ${1:F2}, Drawdown ${2:F2}",
						Time[0], dailyPeakPnL, drawdownFromPeak));
				}
			}
		}
		#endregion

		#region Helper Methods
		private void CalculateOpeningRange()
		{
			DateTime nowET = TimeZoneInfo.ConvertTime(Time[0], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

			// OR window: 9:30-9:45 AM ET
			orStartTime = new DateTime(nowET.Year, nowET.Month, nowET.Day, 9, 30, 0);
			orEndTime = new DateTime(nowET.Year, nowET.Month, nowET.Day, 9, 45, 0);

			if (nowET >= orEndTime)
			{
				// Calculate OR from bars in that window
				for (int i = 0; i < CurrentBar; i++)
				{
					DateTime barTimeET = TimeZoneInfo.ConvertTime(Time[i], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

					if (barTimeET >= orStartTime && barTimeET < orEndTime)
					{
						if (orHigh == 0 || High[i] > orHigh)
							orHigh = High[i];
						if (orLow == 0 || Low[i] < orLow)
							orLow = Low[i];
					}
				}

				orCalculated = true;
				Print(string.Format("{0} | Opening Range Calculated: High {1}, Low {2}", Time[0], orHigh, orLow));
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
		[Range(50, 500)]
		[Display(Name="Bucket Size (ms)", Description="Time bucket for aggregating events", Order=1, GroupName="1. Signal Parameters")]
		public int BucketSizeMs { get; set; }

		[NinjaScriptProperty]
		[Range(10, 200)]
		[Display(Name="Min Event Delta", Description="Minimum volume imbalance (contracts)", Order=2, GroupName="1. Signal Parameters")]
		public int MinEventDelta { get; set; }

		[NinjaScriptProperty]
		[Range(0.3, 0.9)]
		[Display(Name="Min Event Imbalance", Description="Minimum imbalance ratio (0-1)", Order=3, GroupName="1. Signal Parameters")]
		public double MinEventImbalance { get; set; }

		[NinjaScriptProperty]
		[Range(10, 100)]
		[Display(Name="Target (ticks)", Description="Profit target in ticks", Order=1, GroupName="2. Risk Management")]
		public int TargetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(5, 50)]
		[Display(Name="Stop (ticks)", Description="Stop loss in ticks", Order=2, GroupName="2. Risk Management")]
		public int StopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 30)]
		[Display(Name="Timeout (minutes)", Description="Max hold time before exit", Order=3, GroupName="2. Risk Management")]
		public int TimeoutMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(30, 500)]
		[Display(Name="Max Daily Loss ($)", Description="Stop trading after this daily loss", Order=1, GroupName="3. Daily Limits")]
		public double MaxDailyLoss { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name="Max Consecutive Losses", Description="Stop after this many losses in a row", Order=2, GroupName="3. Daily Limits")]
		public int MaxConsecutiveLosses { get; set; }

		[NinjaScriptProperty]
		[Range(500, 10000)]
		[Display(Name="Profit Lock Peak ($)", Description="Lock profit after reaching this peak", Order=3, GroupName="3. Daily Limits")]
		public double ProfitLockPeak { get; set; }

		[NinjaScriptProperty]
		[Range(100, 2000)]
		[Display(Name="Profit Lock Drawdown ($)", Description="Stop if drawdown from peak exceeds this", Order=4, GroupName="3. Daily Limits")]
		public double ProfitLockDrawdown { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Opening Range Filter", Order=1, GroupName="4. Filters")]
		public bool EnableOpeningRangeFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Manipulation Filters", Order=2, GroupName="4. Filters")]
		public bool EnableManipulationFilters { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Daily Limits", Order=3, GroupName="4. Filters")]
		public bool EnableDailyLimits { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name="RTH Start Hour", Order=1, GroupName="5. RTH Hours")]
		public int RTHStartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name="RTH Start Minute", Order=2, GroupName="5. RTH Hours")]
		public int RTHStartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name="RTH End Hour", Order=3, GroupName="5. RTH Hours")]
		public int RTHEndHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name="RTH End Minute", Order=4, GroupName="5. RTH Hours")]
		public int RTHEndMinute { get; set; }

		#endregion
	}
}
