//
// CG MNQ Scalping Strategy - LIVE TRADING VERSION
// 1 Contract NT8 Survival Mode
//
// CRITICAL: This strategy trades real money. Test thoroughly in simulation first.
//
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
using System.IO;
using System.Globalization;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public class CGScalpingStrategyLive : Strategy
	{
		#region Variables

		// Signal tracking
		private string lastSignalType = "";
		private DateTime lastSignalTime = DateTime.MinValue;
		private double lastSignalPrice = 0;
		private int lastSignalBar = -1;

		// Trade tracking for rolling gate
		private List<DateTime> tradeTimestamps = new List<DateTime>();
		private int tradesExecutedToday = 0;

		// P&L tracking
		private double todayPnL = 0;
		private double todayGrossPnL = 0;
		private double todayCommission = 0;
		private double cumulativePnL = 0;
		private List<DailyPnL> last5DaysPnL = new List<DailyPnL>();
		private DateTime lastResetDate = DateTime.MinValue;

		// Position tracking
		private DateTime entryTime = DateTime.MinValue;
		private string currentSignalType = "";
		private double entryPrice = 0;
		private int positionQuantity = 0;

		// Emergency flags
		private bool emergencyFlatten = false;
		private bool weeklyLimitHit = false;
		private bool hardLimitHit = false;
		private bool tradingDisabled = false;

		// Connection monitoring
		private DateTime lastConnectionCheck = DateTime.MinValue;
		private bool wasConnected = true;
		private int disconnectCount = 0;

		// Signal file tracking
		private string signalFilePath = "";
		private DateTime lastSignalFileCheck = DateTime.MinValue;
		private DateTime lastSignalFileModified = DateTime.MinValue;

		// Performance tracking
		private int totalTrades = 0;
		private int winningTrades = 0;
		private int losingTrades = 0;
		private double largestWin = 0;
		private double largestLoss = 0;
		private List<double> tradePnLs = new List<double>();

		// Order tracking
		private bool entryOrderPending = false;
		private DateTime lastOrderTime = DateTime.MinValue;

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
		[Range(1, 1)]
		[Display(Name = "Contracts", Description = "MUST be 1 for NT8 survival mode", Order = 1, GroupName = "2. Position")]
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
		[Display(Name = "Weekly Loss Limit ($)", Description = "Stop if lose this much in 5 days", Order = 2, GroupName = "4. Risk Management")]
		public double WeeklyLossLimit { get; set; }

		[NinjaScriptProperty]
		[Range(100, 2000)]
		[Display(Name = "Hard Loss Limit ($)", Description = "Absolute maximum loss", Order = 3, GroupName = "4. Risk Management")]
		public double HardLossLimit { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Emergency Flatten", Description = "Flatten all on disconnect", Order = 4, GroupName = "4. Risk Management")]
		public bool EnableEmergencyFlatten { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "P&L File Path", Description = "Path to save daily P&L tracking", Order = 5, GroupName = "4. Risk Management")]
		public string PnLFilePath { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trade Log Path", Description = "Path to save trade-by-trade log", Order = 6, GroupName = "4. Risk Management")]
		public string TradeLogPath { get; set; }

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = @"CG MNQ Scalping Strategy - LIVE TRADING VERSION";
				Name = "CGScalpingStrategyLive";
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
				IsInstantiatedOnEachOptimizationIteration = false;

				// Default values - SCALPING parameters (from backtest)
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
				TradeLogPath = @"C:\Trading\Logs\trade_log.csv";
			}
			else if (State == State.Configure)
			{
				// Ensure we only trade on the primary instrument
				ClearOutputWindow();
			}
			else if (State == State.DataLoaded)
			{
				// Load historical P&L if exists
				LoadPnLHistory();

				Print(string.Format("{0} ========================================", Time[0]));
				Print(string.Format("{0} CG SCALPING STRATEGY LIVE - LOADED", Time[0]));
				Print(string.Format("{0} ========================================", Time[0]));
				Print(string.Format("{0} Cumulative P&L: ${1:F2}", Time[0], cumulativePnL));
				Print(string.Format("{0} Distance from hard limit: ${1:F2}", Time[0], HardLossLimit + cumulativePnL));

				if (last5DaysPnL.Count > 0)
				{
					double weeklyPnL = last5DaysPnL.Sum(d => d.NetPnL);
					Print(string.Format("{0} Last 5 days P&L: ${1:F2}", Time[0], weeklyPnL));
					Print(string.Format("{0} Distance from weekly limit: ${1:F2}", Time[0], WeeklyLossLimit + weeklyPnL));
				}

				Print(string.Format("{0} ========================================", Time[0]));
			}
			else if (State == State.Realtime)
			{
				// Reset daily tracking if new day
				if (lastResetDate.Date != Time[0].Date)
				{
					ResetDailyTracking();
				}

				Print(string.Format("{0} === LIVE TRADING STARTED ===", Time[0]));
				Print(string.Format("{0} Signal file: {1}", Time[0], SignalFile));
				Print(string.Format("{0} Connection: {1}", Time[0], Account != null && Account.Connection != null ? Account.Connection.Status.ToString() : "Unknown"));
			}
			else if (State == State.Terminated)
			{
				// Save P&L on shutdown
				SavePnLHistory();
				SaveDailySummary();

				Print(string.Format("{0} ========================================", Time[0]));
				Print(string.Format("{0} STRATEGY TERMINATED", Time[0]));
				Print(string.Format("{0} Today's P&L: ${1:F2}", Time[0], todayPnL));
				Print(string.Format("{0} Cumulative P&L: ${1:F2}", Time[0], cumulativePnL));
				Print(string.Format("{0} ========================================", Time[0]));
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < BarsRequiredToTrade)
				return;

			// Only trade in real-time
			if (State != State.Realtime)
				return;

			// Check connection status every tick
			CheckConnectionStatus();

			// Emergency flatten if needed
			if (emergencyFlatten && Position.MarketPosition != MarketPosition.Flat)
			{
				LogCritical("EMERGENCY FLATTEN - Closing all positions");
				ExitLong("EmergencyExit");
				ExitShort("EmergencyExit");
				return;
			}

			// Check if trading is disabled
			if (tradingDisabled)
				return;

			// Check hard limit
			if (hardLimitHit)
			{
				LogCritical("HARD LIMIT HIT - Strategy permanently disabled");
				return;
			}

			// Check weekly limit
			if (weeklyLimitHit)
			{
				LogWarning("WEEKLY LIMIT HIT - No trading today");
				return;
			}

			// Reset daily tracking if new day
			if (lastResetDate.Date != Time[0].Date)
			{
				ResetDailyTracking();
			}

			// Time-based exit check (every tick while in position)
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				CheckTimeBasedExit();
			}

			// Only check for new entries if flat and not pending
			if (Position.MarketPosition == MarketPosition.Flat && !entryOrderPending)
			{
				// Check rolling trades/hour gate
				if (!CheckRollingGate())
				{
					return; // Don't take new trades
				}

				// Read signals from file (throttled to once per second)
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
				// Check if file was modified
				DateTime fileModified = File.GetLastWriteTime(signalFilePath);
				if (fileModified <= lastSignalFileModified)
					return; // No new signal

				lastSignalFileModified = fileModified;

				// Read last line from signal file
				// Expected format: timestamp,symbol,signal_type,direction,price
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
				double price = double.Parse(fields[4], CultureInfo.InvariantCulture);

				// Check if this is a new signal (not already processed)
				if (signalTime <= lastSignalTime)
					return;

				// Check if signal is recent (within 5 seconds)
				if ((DateTime.Now - signalTime).TotalSeconds > 5)
				{
					LogWarning(string.Format("Signal too old: {0:F1}s", (DateTime.Now - signalTime).TotalSeconds));
					return;
				}

				// Valid signal types
				if (signalType != "ABSORPTION" && signalType != "ICEBERG" && signalType != "BREAKOUT")
				{
					LogWarning(string.Format("Invalid signal type: {0}", signalType));
					return;
				}

				// Check if we already traded on this bar
				if (lastSignalBar == CurrentBar)
				{
					LogInfo("Already traded on this bar - skipping");
					return;
				}

				// Process signal
				lastSignalTime = signalTime;
				lastSignalType = signalType;
				lastSignalPrice = price;
				lastSignalBar = CurrentBar;

				ProcessSignal(signalType, direction, price);
			}
			catch (Exception ex)
			{
				LogError(string.Format("Error reading signal file: {0}", ex.Message));
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
			SetProfitTarget(CalculationMode.Ticks, (int)(target / TickSize));
			SetStopLoss(CalculationMode.Ticks, (int)(stop / TickSize));

			// Record entry intent
			currentSignalType = signalType;
			entryTime = Time[0];
			entryOrderPending = true;
			lastOrderTime = DateTime.Now;

			// Enter trade
			string entryName = signalType + "_" + DateTime.Now.ToString("HHmmss");

			if (direction == "LONG")
			{
				EnterLong(Contracts, entryName);
				LogTrade(string.Format("LONG {0} @ {1:F2} | Target: +{2} Stop: -{3} MaxHold: {4}s",
					signalType, price, target, stop, maxHold));
			}
			else if (direction == "SHORT")
			{
				EnterShort(Contracts, entryName);
				LogTrade(string.Format("SHORT {0} @ {1:F2} | Target: +{2} Stop: -{3} MaxHold: {4}s",
					signalType, price, target, stop, maxHold));
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
				LogWarning(string.Format("Rolling gate triggered: {0:F1} trades/hour < {1:F1} minimum",
					tradesPerHour, MinTradesPerHour));
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

			double secondsInTrade = (DateTime.Now - entryTime).TotalSeconds;

			if (secondsInTrade >= maxHold)
			{
				LogTrade(string.Format("Time-based exit: {0:F0}s >= {1}s max hold",
					secondsInTrade, maxHold));

				if (Position.MarketPosition == MarketPosition.Long)
					ExitLong("TimeExit");
				else if (Position.MarketPosition == MarketPosition.Short)
					ExitShort("TimeExit");
			}
		}

		private void CheckWeeklyLimit()
		{
			if (last5DaysPnL.Count < 5)
				return;

			double weeklyPnL = last5DaysPnL.Sum(d => d.NetPnL);

			if (weeklyPnL <= -WeeklyLossLimit)
			{
				weeklyLimitHit = true;
				LogCritical(string.Format("WEEKLY LIMIT HIT: ${0:F2} loss in last 5 days", weeklyPnL));

				// Alert user
				Alert("WeeklyLimit", Priority.High,
					string.Format("Weekly loss limit hit: ${0:F2} - NO TRADING TODAY", weeklyPnL),
					NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert3.wav", 30, Brushes.Red, Brushes.White);
			}
		}

		private void CheckHardLimit()
		{
			if (cumulativePnL <= -HardLossLimit)
			{
				hardLimitHit = true;
				tradingDisabled = true;

				LogCritical(string.Format("HARD LIMIT BREACHED: ${0:F2} cumulative loss - STRATEGY DISABLED", cumulativePnL));

				// Alert user
				Alert("HardLimit", Priority.High,
					string.Format("HARD LIMIT BREACHED: ${0:F2} - STRATEGY PERMANENTLY DISABLED", cumulativePnL),
					NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert4.wav", 60, Brushes.Red, Brushes.White);

				// Flatten any open position
				if (Position.MarketPosition != MarketPosition.Flat)
				{
					ExitLong("HardLimitExit");
					ExitShort("HardLimitExit");
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
				disconnectCount++;
				LogCritical(string.Format("CONNECTION LOST (#{0}) - Emergency flatten enabled", disconnectCount));
				emergencyFlatten = true;

				// Try to flatten immediately
				if (Position.MarketPosition != MarketPosition.Flat)
				{
					ExitLong("DisconnectExit");
					ExitShort("DisconnectExit");
				}

				Alert("Disconnect", Priority.High,
					string.Format("Connection lost - Emergency flatten (disconnect #{0})", disconnectCount),
					NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert4.wav", 60, Brushes.Red, Brushes.White);
			}

			// Detect reconnect
			if (!wasConnected && isConnected)
			{
				LogInfo(string.Format("CONNECTION RESTORED (was disconnected {0} times)", disconnectCount));
				emergencyFlatten = false; // Reset flag on reconnect
			}

			wasConnected = isConnected;
		}

		#endregion

		#region Execution Tracking

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
			MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution.Order == null)
				return;

			// Track entry fills (check if order name contains signal type)
			bool isEntryOrder = execution.Order.Name.Contains("ABSORPTION") ||
			                    execution.Order.Name.Contains("ICEBERG") ||
			                    execution.Order.Name.Contains("BREAKOUT");

			if (isEntryOrder && execution.Order.OrderState == OrderState.Filled)
			{
				entryPrice = execution.Price;
				positionQuantity = execution.Quantity * (execution.Order.IsLong ? 1 : -1);
				entryOrderPending = false;

				LogTrade(string.Format("FILLED: {0} {1} @ {2:F2} | Qty: {3}",
					execution.Order.IsLong ? "LONG" : "SHORT",
					execution.Order.Name,
					execution.Price,
					execution.Quantity));
			}

			// Track exit fills and calculate P&L (check if order name contains Exit, Target, Stop, or Time)
			bool isExitOrder = execution.Order.Name.Contains("Exit") ||
			                   execution.Order.Name.Contains("Target") ||
			                   execution.Order.Name.Contains("Stop") ||
			                   execution.Order.Name.Contains("Time");

			if (isExitOrder && execution.Order.OrderState == OrderState.Filled)
			{
				// Calculate P&L for this execution
				double ticksPnL = (execution.Price - entryPrice) * positionQuantity / TickSize;
				double pointsPnL = ticksPnL * TickSize / 0.25; // MNQ: 0.25 tick size
				double dollarPnL = pointsPnL * 2.0; // MNQ: $2 per point

				// Commission
				double commission = 0.70; // Per round-trip
				double netPnL = dollarPnL - commission;

				// Update tracking
				todayGrossPnL += dollarPnL;
				todayCommission += commission;
				todayPnL += netPnL;
				cumulativePnL += netPnL;
				totalTrades++;
				tradesExecutedToday++;

				if (netPnL > 0)
				{
					winningTrades++;
					if (netPnL > largestWin)
						largestWin = netPnL;
				}
				else
				{
					losingTrades++;
					if (netPnL < largestLoss)
						largestLoss = netPnL;
				}

				tradePnLs.Add(netPnL);

				// Log trade result
				LogTrade(string.Format("EXIT: {0} @ {1:F2} | P&L: ${2:F2} (Gross: ${3:F2}, Comm: ${4:F2})",
					execution.Order.Name,
					execution.Price,
					netPnL,
					dollarPnL,
					commission));

				LogTrade(string.Format("TODAY: ${0:F2} | CUMULATIVE: ${1:F2} | Trades: {2} ({3}W/{4}L)",
					todayPnL,
					cumulativePnL,
					totalTrades,
					winningTrades,
					losingTrades));

				// Save trade to log
				SaveTradeLog(execution, dollarPnL, commission, netPnL);

				// Check limits
				CheckWeeklyLimit();
				CheckHardLimit();

				// Reset position tracking
				entryPrice = 0;
				positionQuantity = 0;
				currentSignalType = "";
			}

			// Track order rejections
			if (execution.Order.OrderState == OrderState.Rejected)
			{
				entryOrderPending = false;
				LogError(string.Format("ORDER REJECTED: {0} - {1}",
					execution.Order.Name,
					execution.Order.OrderState.ToString()));
			}

			// Track order cancellations
			if (execution.Order.OrderState == OrderState.Cancelled)
			{
				entryOrderPending = false;
				LogWarning(string.Format("ORDER CANCELLED: {0}",
					execution.Order.Name));
			}
		}

		#endregion

		#region P&L Tracking

		private void ResetDailyTracking()
		{
			// Save yesterday's P&L
			if (lastResetDate != DateTime.MinValue && todayPnL != 0)
			{
				DailyPnL yesterday = new DailyPnL
				{
					Date = lastResetDate,
					GrossPnL = todayGrossPnL,
					Commission = todayCommission,
					NetPnL = todayPnL,
					Trades = tradesExecutedToday
				};

				last5DaysPnL.Add(yesterday);
				if (last5DaysPnL.Count > 5)
					last5DaysPnL.RemoveAt(0);

				SavePnLHistory();
				SaveDailySummary();
			}

			// Reset daily counters
			todayPnL = 0;
			todayGrossPnL = 0;
			todayCommission = 0;
			tradesExecutedToday = 0;
			lastResetDate = Time[0].Date;
			weeklyLimitHit = false; // Reset weekly flag at start of day
			tradeTimestamps.Clear(); // Reset rolling gate

			LogInfo("=== NEW TRADING DAY ===");
			LogInfo(string.Format("Cumulative P&L: ${0:F2}", cumulativePnL));
			LogInfo(string.Format("Distance from hard limit: ${0:F2}", HardLossLimit + cumulativePnL));

			if (last5DaysPnL.Count > 0)
			{
				double weeklyPnL = last5DaysPnL.Sum(d => d.NetPnL);
				LogInfo(string.Format("Last 5 days P&L: ${0:F2}", weeklyPnL));
				LogInfo(string.Format("Distance from weekly limit: ${0:F2}", WeeklyLossLimit + weeklyPnL));
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

				foreach (string line in lines)
				{
					if (line.StartsWith("date,"))
						continue; // Skip header

					string[] fields = line.Split(',');
					if (fields.Length >= 5)
					{
						DailyPnL day = new DailyPnL
						{
							Date = DateTime.Parse(fields[0]),
							GrossPnL = double.Parse(fields[1], CultureInfo.InvariantCulture),
							Commission = double.Parse(fields[2], CultureInfo.InvariantCulture),
							NetPnL = double.Parse(fields[3], CultureInfo.InvariantCulture),
							Trades = int.Parse(fields[4])
						};

						// Cumulative from last entry
						if (fields.Length >= 6)
							cumulativePnL = double.Parse(fields[5], CultureInfo.InvariantCulture);

						// Only load recent history (last 5 days)
						if ((DateTime.Now.Date - day.Date).TotalDays <= 5)
						{
							last5DaysPnL.Add(day);
						}

						lastResetDate = day.Date;
					}
				}

				LogInfo(string.Format("Loaded P&L history: Cumulative ${0:F2}", cumulativePnL));
			}
			catch (Exception ex)
			{
				LogError(string.Format("Error loading P&L history: {0}", ex.Message));
			}
		}

		private void SavePnLHistory()
		{
			if (string.IsNullOrEmpty(PnLFilePath))
				return;

			try
			{
				string directory = Path.GetDirectoryName(PnLFilePath);
				if (!Directory.Exists(directory))
					Directory.CreateDirectory(directory);

				bool fileExists = File.Exists(PnLFilePath);

				using (StreamWriter sw = new StreamWriter(PnLFilePath, true))
				{
					if (!fileExists)
					{
						sw.WriteLine("date,gross_pnl,commission,net_pnl,trades,cumulative_pnl");
					}

					sw.WriteLine(string.Format("{0:yyyy-MM-dd},{1:F2},{2:F2},{3:F2},{4},{5:F2}",
						lastResetDate,
						todayGrossPnL,
						todayCommission,
						todayPnL,
						tradesExecutedToday,
						cumulativePnL));
				}
			}
			catch (Exception ex)
			{
				LogError(string.Format("Error saving P&L history: {0}", ex.Message));
			}
		}

		private void SaveDailySummary()
		{
			if (tradesExecutedToday == 0)
				return;

			double winRate = totalTrades > 0 ? (double)winningTrades / totalTrades * 100 : 0;
			double avgTrade = tradePnLs.Count > 0 ? tradePnLs.Average() : 0;

			LogInfo("=== DAILY SUMMARY ===");
			LogInfo(string.Format("Trades: {0} ({1}W/{2}L, {3:F1}% win rate)",
				tradesExecutedToday, winningTrades, losingTrades, winRate));
			LogInfo(string.Format("P&L: ${0:F2} (Gross: ${1:F2}, Comm: ${2:F2})",
				todayPnL, todayGrossPnL, todayCommission));
			LogInfo(string.Format("Avg trade: ${0:F2} | Largest win: ${1:F2} | Largest loss: ${2:F2}",
				avgTrade, largestWin, largestLoss));
			LogInfo(string.Format("Cumulative P&L: ${0:F2}", cumulativePnL));
			LogInfo("==================");
		}

		private void SaveTradeLog(Execution execution, double grossPnL, double commission, double netPnL)
		{
			if (string.IsNullOrEmpty(TradeLogPath))
				return;

			try
			{
				string directory = Path.GetDirectoryName(TradeLogPath);
				if (!Directory.Exists(directory))
					Directory.CreateDirectory(directory);

				bool fileExists = File.Exists(TradeLogPath);

				using (StreamWriter sw = new StreamWriter(TradeLogPath, true))
				{
					if (!fileExists)
					{
						sw.WriteLine("timestamp,signal_type,direction,entry_price,exit_price,qty,gross_pnl,commission,net_pnl,hold_seconds,exit_reason");
					}

					double holdSeconds = (execution.Time - entryTime).TotalSeconds;
					string exitReason = execution.Order.Name.Contains("Target") ? "Target" :
									   execution.Order.Name.Contains("Stop") ? "Stop" :
									   execution.Order.Name.Contains("Time") ? "Time" : "Other";

					sw.WriteLine(string.Format("{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3:F2},{4:F2},{5},{6:F2},{7:F2},{8:F2},{9:F1},{10}",
						execution.Time,
						currentSignalType,
						positionQuantity > 0 ? "LONG" : "SHORT",
						entryPrice,
						execution.Price,
						Math.Abs(positionQuantity),
						grossPnL,
						commission,
						netPnL,
						holdSeconds,
						exitReason));
				}
			}
			catch (Exception ex)
			{
				LogError(string.Format("Error saving trade log: {0}", ex.Message));
			}
		}

		#endregion

		#region Logging Helpers

		private void LogTrade(string message)
		{
			string formatted = string.Format("{0} [TRADE] {1}", Time[0], message);
			Print(formatted);
		}

		private void LogInfo(string message)
		{
			string formatted = string.Format("{0} [INFO] {1}", Time[0], message);
			Print(formatted);
		}

		private void LogWarning(string message)
		{
			string formatted = string.Format("{0} [WARN] {1}", Time[0], message);
			Print(formatted);
		}

		private void LogError(string message)
		{
			string formatted = string.Format("{0} [ERROR] {1}", Time[0], message);
			Print(formatted);
		}

		private void LogCritical(string message)
		{
			string formatted = string.Format("{0} [CRITICAL] {1}", Time[0], message);
			Print(formatted);

			// Also send alert for critical issues
			Alert("Critical", Priority.High, message,
				NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert4.wav", 30, Brushes.Red, Brushes.White);
		}

		#endregion

		#region Helper Classes

		private class DailyPnL
		{
			public DateTime Date { get; set; }
			public double GrossPnL { get; set; }
			public double Commission { get; set; }
			public double NetPnL { get; set; }
			public int Trades { get; set; }
		}

		#endregion

		#region Properties Display

		public override string DisplayName
		{
			get { return "CG Scalping LIVE"; }
		}

		#endregion
	}
}
