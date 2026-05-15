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
	public class CG_OrderFlow_Imbalance_v1_0_CORRECTED : Strategy
	{
		#region Variables
		private DateTime bucketStartTime;
		private long bidEventVolume;
		private long askEventVolume;
		private int bidEventCount;
		private int askEventCount;
		private double currentBestBid;
		private double currentBestAsk;

		private string lastSignal = "NONE";

		private double orHigh = 0;
		private double orLow = 0;
		private bool orCalculated = false;

		private double dailyPnL = 0;
		private int consecutiveLosses = 0;
		private double dailyPeakPnL = 0;
		private DateTime lastTradeDate;
		private bool dailyLimitHit = false;

		private DateTime entryTime;
		private double entryPrice;
		private MarketPosition entryDirection; // CRITICAL: Track entry direction for P&L calc
		private int tradesCountToday = 0;

		// Reduce print spam
		private int printCounter = 0;
		private const int PRINT_EVERY_N_EVENTS = 1000;
		#endregion

		#region OnStateChange
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = @"Order Flow Imbalance - P&L CORRECTED VERSION";
				Name = "CG_OrderFlow_Imbalance_v1_0_CORRECTED";
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
				TraceOrders = false;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade = 20;

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
				EnableVerboseLogging = false; // Turn off spam by default
				RTHStartHour = 9;
				RTHStartMinute = 30;
				RTHEndHour = 16;
				RTHEndMinute = 0;
			}
			else if (State == State.Configure)
			{
				// Market Depth auto-available when OnMarketDepth is overridden
			}
			else if (State == State.DataLoaded)
			{
				bucketStartTime = DateTime.MinValue;
				ResetBucket();
				entryDirection = MarketPosition.Flat;
			}
		}
		#endregion

		#region OnBarUpdate
		protected override void OnBarUpdate()
		{
			if (CurrentBar < BarsRequiredToTrade)
				return;

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

		#region OnMarketDepth
		protected override void OnMarketDepth(MarketDepthEventArgs e)
		{
			if (State != State.Realtime || CurrentBar < BarsRequiredToTrade)
				return;

			printCounter++;

			if (bucketStartTime == DateTime.MinValue)
				bucketStartTime = e.Time;

			TimeSpan elapsed = e.Time - bucketStartTime;
			if (elapsed.TotalMilliseconds >= BucketSizeMs)
			{
				ProcessBucket();
				bucketStartTime = e.Time;
				ResetBucket();
			}

			if (e.MarketDataType == MarketDataType.Ask && e.Position == 0)
				currentBestAsk = e.Price;
			else if (e.MarketDataType == MarketDataType.Bid && e.Position == 0)
				currentBestBid = e.Price;

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
			if (bidEventVolume == 0 && askEventVolume == 0)
				return;

			long eventDelta = bidEventVolume - askEventVolume;
			long totalVolume = bidEventVolume + askEventVolume;

			if (totalVolume == 0)
				return;

			double eventImbalance = (double)eventDelta / totalVolume;

			string signal = "NONE";

			if (eventDelta > MinEventDelta && eventImbalance > MinEventImbalance)
				signal = "LONG";
			else if (eventDelta < -MinEventDelta && eventImbalance < -MinEventImbalance)
				signal = "SHORT";

			// Only act on signal CHANGES
			if (signal != "NONE" && signal != lastSignal)
			{
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
			if (Position.MarketPosition != MarketPosition.Flat)
				return false;

			if (!IsRTH())
				return false;

			if (EnableDailyLimits && dailyLimitHit)
				return false;

			if (EnableOpeningRangeFilter && !orCalculated)
				return false;

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
		private void EnterPosition(string signal, long eventSize)
		{
			entryTime = Time[0];
			tradesCountToday++;

			if (signal == "LONG")
			{
				EnterLong(1, "OFI_Long");
				entryPrice = currentBestAsk;
				entryDirection = MarketPosition.Long; // CRITICAL: Track direction
			}
			else if (signal == "SHORT")
			{
				EnterShort(1, "OFI_Short");
				entryPrice = currentBestBid;
				entryDirection = MarketPosition.Short; // CRITICAL: Track direction
			}

			if (EnableVerboseLogging)
			{
				Print(string.Format("{0} | ENTRY {1} @ {2} | TZ: {3} | OR: {4} | Event: {5}",
					Time[0], signal, entryPrice, GetTimeZone(), GetORLocation(), eventSize));
			}
		}

		private void CheckTimeout()
		{
			// Use CURRENT TIME not bar time for timeout
			TimeSpan holdTime = DateTime.Now - entryTime;

			if (holdTime.TotalMinutes >= TimeoutMinutes)
			{
				if (Position.MarketPosition == MarketPosition.Long)
					ExitLong("OFI_Timeout");
				else if (Position.MarketPosition == MarketPosition.Short)
					ExitShort("OFI_Timeout");

				if (EnableVerboseLogging)
					Print(string.Format("{0} | TIMEOUT EXIT after {1:F1} min", Time[0], holdTime.TotalMinutes));
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

				// EXIT FILL - CORRECTED P&L CALCULATION
				else if (execution.Order.Name == "OFI_Target" || execution.Order.Name == "OFI_Stop" ||
				         execution.Order.Name == "OFI_Timeout" || execution.Order.Name == "Close position")
				{
					// CRITICAL FIX: Calculate P&L from entry/exit prices, not unrealized P&L
					double tradePnL = 0;

					if (entryDirection == MarketPosition.Long)
					{
						// Long: profit when exit > entry
						tradePnL = (execution.Price - entryPrice) * execution.Quantity * Instrument.MasterInstrument.PointValue;
					}
					else if (entryDirection == MarketPosition.Short)
					{
						// Short: profit when entry > exit
						tradePnL = (entryPrice - execution.Price) * execution.Quantity * Instrument.MasterInstrument.PointValue;
					}

					UpdateDailyTracking(tradePnL, execution.Order.Name);

					Print(string.Format("{0} | #{1} EXIT {2} | P&L:${3:F2} | Daily:${4:F2}/{5}",
						time.ToShortTimeString(), tradesCountToday, execution.Order.Name.Replace("OFI_", ""),
						tradePnL, dailyPnL, consecutiveLosses));

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

			// DAILY LIMIT ENFORCEMENT - Now will work correctly with proper P&L
			if (EnableDailyLimits)
			{
				// Max daily loss limit
				if (dailyPnL <= -MaxDailyLoss)
				{
					dailyLimitHit = true;
					Print(string.Format("*** DAILY LOSS LIMIT HIT: ${0:F2} ***", dailyPnL));
				}

				// Consecutive loss limit
				if (consecutiveLosses >= MaxConsecutiveLosses)
				{
					dailyLimitHit = true;
					Print(string.Format("*** CONSECUTIVE LOSS LIMIT HIT: {0} losses ***", consecutiveLosses));
				}

				// Profit lock
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
		[Range(50, 500)]
		[Display(Name="Bucket Size (ms)", Order=1, GroupName="1. Signal")]
		public int BucketSizeMs { get; set; }

		[NinjaScriptProperty]
		[Range(10, 200)]
		[Display(Name="Min Event Delta", Order=2, GroupName="1. Signal")]
		public int MinEventDelta { get; set; }

		[NinjaScriptProperty]
		[Range(0.3, 0.9)]
		[Display(Name="Min Event Imbalance", Order=3, GroupName="1. Signal")]
		public double MinEventImbalance { get; set; }

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
		[Range(30, 500)]
		[Display(Name="Max Daily Loss ($)", Order=1, GroupName="3. Daily Limits")]
		public double MaxDailyLoss { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name="Max Consecutive Losses", Order=2, GroupName="3. Daily Limits")]
		public int MaxConsecutiveLosses { get; set; }

		[NinjaScriptProperty]
		[Range(500, 10000)]
		[Display(Name="Profit Lock Peak ($)", Order=3, GroupName="3. Daily Limits")]
		public double ProfitLockPeak { get; set; }

		[NinjaScriptProperty]
		[Range(100, 2000)]
		[Display(Name="Profit Lock Drawdown ($)", Order=4, GroupName="3. Daily Limits")]
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
		[Display(Name="Enable Verbose Logging", Order=4, GroupName="4. Filters")]
		public bool EnableVerboseLogging { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name="RTH Start Hour", Order=1, GroupName="5. RTH")]
		public int RTHStartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name="RTH Start Minute", Order=2, GroupName="5. RTH")]
		public int RTHStartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name="RTH End Hour", Order=3, GroupName="5. RTH")]
		public int RTHEndHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name="RTH End Minute", Order=4, GroupName="5. RTH")]
		public int RTHEndMinute { get; set; }
		#endregion
	}
}
