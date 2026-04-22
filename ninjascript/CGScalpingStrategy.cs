//
// Copyright (C) 2024, NinjaTrader LLC <www.ninjatrader.com>.
// NinjaTrader reserves all rights and is protected under US copyright laws.
//
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
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
using System.IO;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public class CGScalpingStrategy : Strategy
	{
		#region Variables

		// Signal tracking
		private string lastSignalType = "";
		private DateTime lastSignalTime = DateTime.MinValue;
		private double lastSignalPrice = 0;

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

		// Emergency flags
		private bool emergencyFlatten = false;
		private bool weeklyLimitHit = false;
		private bool hardLimitHit = false;

		// Connection monitoring
		private DateTime lastConnectionCheck = DateTime.MinValue;
		private bool wasConnected = true;

		// Signal file path (updated by external process)
		private string signalFilePath = "";
		private DateTime lastSignalFileCheck = DateTime.MinValue;

		#endregion

		#region Properties

		[NinjaScriptProperty]
		[Display(Name = "Signal File Path", Description = "Path to CSV file with order flow signals", Order = 1, GroupName = "1. Signal Source")]
		public string SignalFile
		{
			get { return signalFilePath; }
			set { signalFilePath = value; }
		}

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Contracts", Description = "Number of contracts (MUST be 1 for safety)", Order = 1, GroupName = "2. Position")]
		public int Contracts { get; set; }

		// ABSORPTION parameters
		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Target (points)", Description = "Profit target", Order = 1, GroupName = "3a. ABSORPTION")]
		public double AbsorptionTarget { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Stop (points)", Description = "Stop loss", Order = 2, GroupName = "3a. ABSORPTION")]
		public double AbsorptionStop { get; set; }

		[NinjaScriptProperty]
		[Range(10, 600)]
		[Display(Name = "Max Hold (seconds)", Description = "Time-based exit", Order = 3, GroupName = "3a. ABSORPTION")]
		public int AbsorptionMaxHold { get; set; }

		// ICEBERG parameters
		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Target (points)", Description = "Profit target", Order = 1, GroupName = "3b. ICEBERG")]
		public double IcebergTarget { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Stop (points)", Description = "Stop loss", Order = 2, GroupName = "3b. ICEBERG")]
		public double IcebergStop { get; set; }

		[NinjaScriptProperty]
		[Range(10, 600)]
		[Display(Name = "Max Hold (seconds)", Description = "Time-based exit", Order = 3, GroupName = "3b. ICEBERG")]
		public int IcebergMaxHold { get; set; }

		// BREAKOUT parameters
		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Target (points)", Description = "Profit target", Order = 1, GroupName = "3c. BREAKOUT")]
		public double BreakoutTarget { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Stop (points)", Description = "Stop loss", Order = 2, GroupName = "3c. BREAKOUT")]
		public double BreakoutStop { get; set; }

		[NinjaScriptProperty]
		[Range(10, 600)]
		[Display(Name = "Max Hold (seconds)", Description = "Time-based exit", Order = 3, GroupName = "3c. BREAKOUT")]
		public int BreakoutMaxHold { get; set; }

		// Risk management
		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Min Trades/Hour", Description = "Rolling gate threshold", Order = 1, GroupName = "4. Risk Management")]
		public double MinTradesPerHour { get; set; }

		[NinjaScriptProperty]
		[Range(100, 1000)]
		[Display(Name = "Weekly Loss Limit", Description = "Stop if lose this much in 5 days", Order = 2, GroupName = "4. Risk Management")]
		public double WeeklyLossLimit { get; set; }

		[NinjaScriptProperty]
		[Range(100, 2000)]
		[Display(Name = "Hard Loss Limit", Description = "Absolute maximum loss (NT8 survival)", Order = 3, GroupName = "4. Risk Management")]
		public double HardLossLimit { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Emergency Flatten", Description = "Flatten all on disconnect", Order = 4, GroupName = "4. Risk Management")]
		public bool EnableEmergencyFlatten { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "P&L File Path", Description = "Path to save daily P&L tracking", Order = 5, GroupName = "4. Risk Management")]
		public string PnLFilePath { get; set; }

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = @"CG MNQ Scalping Strategy - 1 Contract NT8 Survival Mode";
				Name = "CGScalpingStrategy";
				Calculate = Calculate.OnEachTick;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 30;
				IsFillLimitOnTouch = false;
				MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution = OrderFillResolution.Standard;
				Slippage = 0; // Using broker fills
				StartBehavior = StartBehavior.WaitUntilFlat;
				TimeInForce = TimeInForce.Gtc;
				TraceOrders = true;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade = 1;
				IsInstantiatedOnEachOptimizationIteration = true;

				// Default values - SCALPING parameters
				Contracts = 1;

				AbsorptionTarget = 6.0;
				AbsorptionStop = 3.0;
				AbsorptionMaxHold = 120;

				IcebergTarget = 5.0;
				IcebergStop = 2.5;
				IcebergMaxHold = 90;

				BreakoutTarget = 8.0;
				BreakoutStop = 4.0;
				BreakoutMaxHold = 60;

				MinTradesPerHour = 4.0;
				WeeklyLossLimit = 250.0;
				HardLossLimit = 500.0;
				EnableEmergencyFlatten = true;

				SignalFile = @"C:\Trading\Signals\mnq_signals.csv";
				PnLFilePath = @"C:\Trading\Logs\daily_pnl.csv";
			}
			else if (State == State.Configure)
			{
				// Broker-side stops and targets (OCO automatic)
				// Will be set per entry in OnBarUpdate
			}
			else if (State == State.DataLoaded)
			{
				// Load historical P&L if exists
				LoadPnLHistory();
			}
			else if (State == State.Realtime)
			{
				// Reset daily tracking
				if (lastResetDate.Date != Time[0].Date)
				{
					ResetDailyTracking();
				}
			}
			else if (State == State.Terminated)
			{
				// Save P&L on shutdown
				SavePnLHistory();
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < BarsRequiredToTrade)
				return;

			// Only trade in real-time
			if (State != State.Realtime)
				return;

			// Check connection status
			CheckConnectionStatus();

			// Emergency flatten if needed
			if (emergencyFlatten && Position.MarketPosition != MarketPosition.Flat)
			{
				Print(string.Format("{0} EMERGENCY FLATTEN - Closing all positions", Time[0]));
				ExitLong();
				ExitShort();
				return;
			}

			// Check hard limit
			if (hardLimitHit)
			{
				Print(string.Format("{0} HARD LIMIT HIT - Strategy disabled", Time[0]));
				return;
			}

			// Check weekly limit
			if (weeklyLimitHit)
			{
				Print(string.Format("{0} WEEKLY LIMIT HIT - No trading today", Time[0]));
				return;
			}

			// Reset daily tracking if new day
			if (lastResetDate.Date != Time[0].Date)
			{
				ResetDailyTracking();
			}

			// Time-based exit check
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				CheckTimeBasedExit();
			}

			// Only check for new entries if flat
			if (Position.MarketPosition == MarketPosition.Flat)
			{
				// Check rolling trades/hour gate
				if (!CheckRollingGate())
				{
					return; // Don't take new trades
				}

				// Read signals from file
				CheckForNewSignals();
			}
		}

		#region Signal Processing

		private void CheckForNewSignals()
		{
			// Only check every second to avoid excessive file reads
			if ((DateTime.Now - lastSignalFileCheck).TotalSeconds < 1)
				return;

			lastSignalFileCheck = DateTime.Now;

			if (string.IsNullOrEmpty(signalFilePath) || !File.Exists(signalFilePath))
				return;

			try
			{
				// Read last line from signal file
				// Expected format: timestamp,symbol,signal_type,direction,price
				// Example: 2024-01-15 09:30:01,MNQ,ABSORPTION,LONG,17450.25

				string[] lines = File.ReadAllLines(signalFilePath);
				if (lines.Length == 0)
					return;

				string lastLine = lines[lines.Length - 1];
				string[] fields = lastLine.Split(',');

				if (fields.Length < 5)
					return;

				DateTime signalTime = DateTime.Parse(fields[0]);
				string symbol = fields[1].Trim();
				string signalType = fields[2].Trim().ToUpper();
				string direction = fields[3].Trim().ToUpper();
				double price = double.Parse(fields[4]);

				// Check if this is a new signal (not already processed)
				if (signalTime <= lastSignalTime)
					return;

				// Check if signal is recent (within 5 seconds)
				if ((DateTime.Now - signalTime).TotalSeconds > 5)
					return;

				// Valid signal types
				if (signalType != "ABSORPTION" && signalType != "ICEBERG" && signalType != "BREAKOUT")
					return;

				// Process signal
				lastSignalTime = signalTime;
				lastSignalType = signalType;
				lastSignalPrice = price;

				ProcessSignal(signalType, direction, price);
			}
			catch (Exception ex)
			{
				Print(string.Format("{0} Error reading signal file: {1}", Time[0], ex.Message));
			}
		}

		private void ProcessSignal(string signalType, string direction, double price)
		{
			// Get parameters for this signal type
			double target = 0;
			double stop = 0;
			int maxHold = 0;

			switch (signalType)
			{
				case "ABSORPTION":
					target = AbsorptionTarget;
					stop = AbsorptionStop;
					maxHold = AbsorptionMaxHold;
					break;
				case "ICEBERG":
					target = IcebergTarget;
					stop = IcebergStop;
					maxHold = IcebergMaxHold;
					break;
				case "BREAKOUT":
					target = BreakoutTarget;
					stop = BreakoutStop;
					maxHold = BreakoutMaxHold;
					break;
			}

			// Set broker-side stops and targets (OCO)
			SetProfitTarget(CalculationMode.Ticks, target / TickSize);
			SetStopLoss(CalculationMode.Ticks, stop / TickSize);

			// Enter trade
			currentSignalType = signalType;
			entryTime = Time[0];

			if (direction == "LONG")
			{
				EnterLong(Contracts, signalType);
				Print(string.Format("{0} LONG {1} @ {2} | Target: +{3} Stop: -{4} MaxHold: {5}s",
					Time[0], signalType, price, target, stop, maxHold));
			}
			else if (direction == "SHORT")
			{
				EnterShort(Contracts, signalType);
				Print(string.Format("{0} SHORT {1} @ {2} | Target: +{3} Stop: -{4} MaxHold: {5}s",
					Time[0], signalType, price, target, stop, maxHold));
			}

			// Record trade timestamp for rolling gate
			tradeTimestamps.Add(Time[0]);
		}

		#endregion

		#region Risk Management

		private bool CheckRollingGate()
		{
			// Need at least 5 trades before checking
			if (tradeTimestamps.Count < 5)
				return true;

			// Remove trades older than 60 minutes
			DateTime cutoff = Time[0].AddMinutes(-60);
			tradeTimestamps.RemoveAll(t => t < cutoff);

			// Calculate trades per hour
			double tradesPerHour = tradeTimestamps.Count;

			if (tradesPerHour < MinTradesPerHour)
			{
				Print(string.Format("{0} Rolling gate triggered: {1:F1} trades/hour < {2:F1} minimum",
					Time[0], tradesPerHour, MinTradesPerHour));
				return false;
			}

			return true;
		}

		private void CheckTimeBasedExit()
		{
			if (Position.MarketPosition == MarketPosition.Flat)
				return;

			int maxHold = 0;
			switch (currentSignalType)
			{
				case "ABSORPTION":
					maxHold = AbsorptionMaxHold;
					break;
				case "ICEBERG":
					maxHold = IcebergMaxHold;
					break;
				case "BREAKOUT":
					maxHold = BreakoutMaxHold;
					break;
			}

			double secondsInTrade = (Time[0] - entryTime).TotalSeconds;

			if (secondsInTrade >= maxHold)
			{
				Print(string.Format("{0} Time-based exit: {1:F0}s >= {2}s max hold",
					Time[0], secondsInTrade, maxHold));

				if (Position.MarketPosition == MarketPosition.Long)
					ExitLong();
				else if (Position.MarketPosition == MarketPosition.Short)
					ExitShort();
			}
		}

		private void CheckWeeklyLimit()
		{
			if (last5DaysPnL.Count < 5)
				return;

			double weeklyPnL = last5DaysPnL.Sum();

			if (weeklyPnL <= -WeeklyLossLimit)
			{
				weeklyLimitHit = true;
				Print(string.Format("{0} WEEKLY LIMIT HIT: ${1:F2} loss in last 5 days",
					Time[0], weeklyPnL));

				// Alert user
				Alert("WeeklyLimit", Priority.High,
					string.Format("Weekly loss limit hit: ${0:F2}", weeklyPnL),
					NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert3.wav", 10, Brushes.Red, Brushes.White);
			}
		}

		private void CheckHardLimit()
		{
			if (cumulativePnL <= -HardLossLimit)
			{
				hardLimitHit = true;
				Print(string.Format("{0} HARD LIMIT HIT: ${1:F2} cumulative loss",
					Time[0], cumulativePnL));

				// Alert user
				Alert("HardLimit", Priority.High,
					string.Format("HARD LIMIT BREACHED: ${0:F2} - STRATEGY DISABLED", cumulativePnL),
					NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert4.wav", 60, Brushes.Red, Brushes.White);

				// Flatten any open position
				if (Position.MarketPosition != MarketPosition.Flat)
				{
					ExitLong();
					ExitShort();
				}
			}
		}

		#endregion

		#region Connection Monitoring

		private void CheckConnectionStatus()
		{
			// Only check every 2 seconds
			if ((DateTime.Now - lastConnectionCheck).TotalSeconds < 2)
				return;

			lastConnectionCheck = DateTime.Now;

			bool isConnected = (Account != null && Account.Connection != null && Account.Connection.Status == ConnectionStatus.Connected);

			// Detect disconnect
			if (wasConnected && !isConnected && EnableEmergencyFlatten)
			{
				Print(string.Format("{0} CONNECTION LOST - Emergency flatten enabled", Time[0]));
				emergencyFlatten = true;

				// Try to flatten immediately
				if (Position.MarketPosition != MarketPosition.Flat)
				{
					ExitLong();
					ExitShort();
				}

				Alert("Disconnect", Priority.High, "Connection lost - Emergency flatten",
					NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert4.wav", 60, Brushes.Red, Brushes.White);
			}

			// Detect reconnect
			if (!wasConnected && isConnected)
			{
				Print(string.Format("{0} CONNECTION RESTORED", Time[0]));
				emergencyFlatten = false; // Reset flag on reconnect
			}

			wasConnected = isConnected;
		}

		#endregion

		#region P&L Tracking

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
			MarketPosition marketPosition, string orderId, DateTime time)
		{
			// Only track realized P&L from exits
			if (execution.Order != null && execution.Order.OrderState == OrderState.Filled)
			{
				// Check if this is an exit order
				bool isExitOrder = execution.Order.Name.Contains("Exit") ||
				                   execution.Order.Name.Contains("Target") ||
				                   execution.Order.Name.Contains("Stop") ||
				                   execution.Order.Name.Contains("Time");

				if (isExitOrder)
				{
					// Calculate P&L for this exit
					double pnl = execution.Order.AverageFillPrice * execution.Quantity * (execution.Order.IsLong ? -1 : 1);

					// Update tracking
					todayPnL += pnl;
					cumulativePnL += pnl;

					Print(string.Format("{0} Trade closed: ${1:F2} | Today: ${2:F2} | Cumulative: ${3:F2}",
						time, pnl, todayPnL, cumulativePnL));

					// Check limits
					CheckWeeklyLimit();
					CheckHardLimit();
				}
			}
		}

		private void ResetDailyTracking()
		{
			// Save yesterday's P&L
			if (lastResetDate != DateTime.MinValue)
			{
				last5DaysPnL.Add(todayPnL);
				if (last5DaysPnL.Count > 5)
					last5DaysPnL.RemoveAt(0);

				SavePnLHistory();
			}

			// Reset daily counters
			todayPnL = 0;
			lastResetDate = Time[0].Date;
			weeklyLimitHit = false; // Reset weekly flag at start of day
			tradeTimestamps.Clear(); // Reset rolling gate

			Print(string.Format("{0} === NEW TRADING DAY ===", Time[0]));
			Print(string.Format("Cumulative P&L: ${0:F2}", cumulativePnL));

			if (last5DaysPnL.Count > 0)
			{
				double weeklyPnL = last5DaysPnL.Sum();
				Print(string.Format("Last 5 days P&L: ${0:F2}", weeklyPnL));
			}

			// Check if we should trade today
			CheckWeeklyLimit();
			CheckHardLimit();
		}

		private void LoadPnLHistory()
		{
			if (string.IsNullOrEmpty(PnLFilePath) || !File.Exists(PnLFilePath))
				return;

			try
			{
				string[] lines = File.ReadAllLines(PnLFilePath);

				// Expected format: date,daily_pnl,cumulative_pnl
				foreach (string line in lines)
				{
					if (line.StartsWith("date,"))
						continue; // Skip header

					string[] fields = line.Split(',');
					if (fields.Length >= 3)
					{
						DateTime date = DateTime.Parse(fields[0]);
						double dailyPnL = double.Parse(fields[1]);
						double cumPnL = double.Parse(fields[2]);

						// Only load recent history (last 5 days)
						if ((DateTime.Now.Date - date.Date).TotalDays <= 5)
						{
							last5DaysPnL.Add(dailyPnL);
						}

						// Get most recent cumulative
						if (date.Date >= lastResetDate)
						{
							cumulativePnL = cumPnL;
							lastResetDate = date.Date;
						}
					}
				}

				Print(string.Format("Loaded P&L history: Cumulative ${0:F2}", cumulativePnL));
			}
			catch (Exception ex)
			{
				Print(string.Format("Error loading P&L history: {0}", ex.Message));
			}
		}

		private void SavePnLHistory()
		{
			if (string.IsNullOrEmpty(PnLFilePath))
				return;

			try
			{
				// Ensure directory exists
				string directory = Path.GetDirectoryName(PnLFilePath);
				if (!Directory.Exists(directory))
					Directory.CreateDirectory(directory);

				// Append today's result
				bool fileExists = File.Exists(PnLFilePath);

				using (StreamWriter sw = new StreamWriter(PnLFilePath, true))
				{
					// Write header if new file
					if (!fileExists)
					{
						sw.WriteLine("date,daily_pnl,cumulative_pnl");
					}

					// Write today's data
					sw.WriteLine(string.Format("{0:yyyy-MM-dd},{1:F2},{2:F2}",
						DateTime.Now.Date, todayPnL, cumulativePnL));
				}

				Print(string.Format("Saved P&L history: Today ${0:F2}, Cumulative ${1:F2}",
					todayPnL, cumulativePnL));
			}
			catch (Exception ex)
			{
				Print(string.Format("Error saving P&L history: {0}", ex.Message));
			}
		}

		#endregion

		#region Properties Display

		public override string DisplayName
		{
			get { return "CG Scalping (1c)"; }
		}

		#endregion
	}
}
