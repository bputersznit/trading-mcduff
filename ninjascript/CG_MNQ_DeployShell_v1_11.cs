// CG_MNQ_DeployShell_v1_11.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-09 09:45:00 America/New_York
//
// PURPOSE
//   Forward-test deployment shell for the MNQ hierarchical deployment candidate.
//   This strategy does NOT depend on historical ClickHouse signal CSV files.
//
// HARD GOVERNANCE
//   - 1 MNQ only, hardcoded quantity = 1
//   - no overlap, no scaling
//   - RTH/session-side allowlist
//   - entry spread governor and runtime spread flatten
//   - broker/NT managed target + stop bracket
//   - 120-second timeout
//   - telemetry CSV
//
// IMPORTANT
//   The signal engine in this file is intentionally conservative and modular.
//   It is a placeholder/proxy signal, not the final ClickHouse structural signal.
//   Replace EvaluateLongSignal() / EvaluateShortSignal() after the shell validates.

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_MNQ_DeployShell_v1_11 : Strategy
    {
        private const int HardQuantity = 1;
        private const double MnqTickSize = 0.25;

        private Order activeEntryOrder;
        private DateTime activeEntryTime = Core.Globals.MinDate;
        private string activeEntryName = string.Empty;
        private DateTime lastFlatOrEntryResetTime = Core.Globals.MinDate;
        private DateTime currentSessionDate = Core.Globals.MinDate;
        private double sessionStartCumProfit = 0.0;
        private int sessionTradeCount = 0;
        private bool dailyLocked = false;
        private bool telemetryHeaderWritten = false;

        public enum ProxySignalMode
        {
            Disabled,
            BreakoutContinuation,
            MomentumContinuation
        }

        [NinjaScriptProperty]
        [Display(Name = "SignalMode", GroupName = "Signal", Order = 1)]
        public ProxySignalMode SignalMode { get; set; }

        [NinjaScriptProperty]
        [Range(5, 500)]
        [Display(Name = "BreakoutLookbackTicks", GroupName = "Signal", Order = 2)]
        public int BreakoutLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "BreakoutBufferTicks", GroupName = "Signal", Order = 3)]
        public int BreakoutBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "MomentumLookbackMinutes", GroupName = "Signal", Order = 4)]
        public int MomentumLookbackMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MinMomentumTicks", GroupName = "Signal", Order = 5)]
        public int MinMomentumTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 600)]
        [Display(Name = "CooldownSeconds", GroupName = "Signal", Order = 6)]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "TargetTicks", GroupName = "Risk", Order = 10)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "StopTicks", GroupName = "Risk", Order = 11)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 900)]
        [Display(Name = "MaxHoldSeconds", GroupName = "Risk", Order = 12)]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxEntrySpreadTicks", GroupName = "Execution", Order = 20)]
        public int MaxEntrySpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxRuntimeSpreadTicks", GroupName = "Execution", Order = 21)]
        public int MaxRuntimeSpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireRth", GroupName = "Time", Order = 30)]
        public bool RequireRth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowOpenLong", GroupName = "Session Allowlist", Order = 40)]
        public bool AllowOpenLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMorningLong", GroupName = "Session Allowlist", Order = 41)]
        public bool AllowMorningLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMorningShort", GroupName = "Session Allowlist", Order = 42)]
        public bool AllowMorningShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMiddayShort", GroupName = "Session Allowlist", Order = 43)]
        public bool AllowMiddayShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowPowerShort", GroupName = "Session Allowlist", Order = 44)]
        public bool AllowPowerShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowAfternoonTrades", GroupName = "Session Allowlist", Order = 45)]
        public bool AllowAfternoonTrades { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "MaxTradesPerDay", GroupName = "Daily Governance", Order = 50)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "DailyLossLockUsd", GroupName = "Daily Governance", Order = 51)]
        public double DailyLossLockUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableDailyLossLock", GroupName = "Daily Governance", Order = 52)]
        public bool EnableDailyLossLock { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TelemetryCsvPath", GroupName = "Telemetry", Order = 60)]
        public string TelemetryCsvPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "WriteTelemetry", GroupName = "Telemetry", Order = 61)]
        public bool WriteTelemetry { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_MNQ_DeployShell_v1_11";
                Description = "Forward-test shell for MNQ v1.9 candidate: one MNQ, no overlap, bracket, spread/session governance, telemetry, proxy signal hooks.";
                Calculate = Calculate.OnEachTick;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Day;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;

                SignalMode = ProxySignalMode.BreakoutContinuation;
                BreakoutLookbackTicks = 120;
                BreakoutBufferTicks = 2;
                MomentumLookbackMinutes = 3;
                MinMomentumTicks = 8;
                CooldownSeconds = 120;

                TargetTicks = 80;
                StopTicks = 16;
                MaxHoldSeconds = 120;
                MaxEntrySpreadTicks = 2;
                MaxRuntimeSpreadTicks = 6;

                RequireRth = true;
                AllowOpenLong = true;
                AllowMorningLong = true;
                AllowMorningShort = true;
                AllowMiddayShort = true;
                AllowPowerShort = true;
                AllowAfternoonTrades = false;

                MaxTradesPerDay = 30;
                DailyLossLockUsd = 250.0;
                EnableDailyLossLock = true;

                WriteTelemetry = true;
                string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "bin", "Custom", "Strategies");
                TelemetryCsvPath = Path.Combine(defaultDir, "CG_MNQ_DeployShell_v1_11_telemetry.csv");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
                AddDataSeries(BarsPeriodType.Minute, 1);

                SetProfitTarget("CGV111_LONG", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CGV111_LONG", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("CGV111_SHORT", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CGV111_SHORT", CalculationMode.Ticks, StopTicks, false);
            }
            else if (State == State.DataLoaded)
            {
                WriteTelemetryHeader();
                LogTelemetry("STRATEGY_LOADED", Core.Globals.Now, "", "initialization");
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 1)
                return;

            if (CurrentBars[1] < Math.Max(BreakoutLookbackTicks + 2, 20))
                return;

            if (CurrentBars[2] < MomentumLookbackMinutes + 2)
                return;

            DateTime now = Times[1][0];
            ResetSessionIfNeeded(now);

            double bid = GetCurrentBidSafe();
            double ask = GetCurrentAskSafe();
            double spreadTicks = GetSpreadTicks(bid, ask);

            if (Position.Quantity > HardQuantity)
            {
                LogTelemetry("EMERGENCY_FLATTEN_QTY_GT_1", now, "", "position_qty_gt_1");
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("CGV111_EmergencyQty_Long", "CGV111_LONG");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("CGV111_EmergencyQty_Short", "CGV111_SHORT");
                return;
            }

            if (EnableDailyLossLock && !dailyLocked)
            {
                double realizedToday = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartCumProfit;
                if (realizedToday <= -Math.Abs(DailyLossLockUsd))
                {
                    dailyLocked = true;
                    LogTelemetry("DAILY_LOCK_TRIGGERED", now, "", "realized_loss_limit");
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong("CGV111_DailyLock_Long", "CGV111_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CGV111_DailyLock_Short", "CGV111_SHORT");
                    return;
                }
            }

            if (Position.MarketPosition != MarketPosition.Flat && spreadTicks > MaxRuntimeSpreadTicks)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("CGV111_RuntimeSpread_Long", "CGV111_LONG");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("CGV111_RuntimeSpread_Short", "CGV111_SHORT");
                LogTelemetry("RUNTIME_SPREAD_EXIT", now, "", "spread_gt_runtime_max");
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat && activeEntryTime != Core.Globals.MinDate)
            {
                if ((now - activeEntryTime).TotalSeconds >= MaxHoldSeconds)
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong("CGV111_TimeExit_Long", "CGV111_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CGV111_TimeExit_Short", "CGV111_SHORT");
                    LogTelemetry("TIME_EXIT_SUBMITTED", now, "", "max_hold_seconds");
                    return;
                }
            }

            if (dailyLocked || (RequireRth && !IsRth(now)) || sessionTradeCount >= MaxTradesPerDay)
                return;

            if (Position.MarketPosition != MarketPosition.Flat || activeEntryOrder != null)
                return;

            if (lastFlatOrEntryResetTime != Core.Globals.MinDate && (now - lastFlatOrEntryResetTime).TotalSeconds < CooldownSeconds)
                return;

            if (spreadTicks > MaxEntrySpreadTicks)
                return;

            string phase = GetSessionPhase(now);
            bool longSignal = IsLongAllowed(phase) && EvaluateLongSignal();
            bool shortSignal = IsShortAllowed(phase) && EvaluateShortSignal();

            if (longSignal && shortSignal)
            {
                LogTelemetry("SIGNAL_CONFLICT_SKIP", now, phase, "both_long_and_short_true");
                return;
            }

            if (longSignal)
            {
                activeEntryName = "CGV111_LONG";
                activeEntryTime = now;
                lastFlatOrEntryResetTime = now;
                EnterLong(HardQuantity, activeEntryName);
                sessionTradeCount++;
                LogTelemetry("ENTRY_SUBMITTED_LONG", now, phase, "proxy_signal");
            }
            else if (shortSignal)
            {
                activeEntryName = "CGV111_SHORT";
                activeEntryTime = now;
                lastFlatOrEntryResetTime = now;
                EnterShort(HardQuantity, activeEntryName);
                sessionTradeCount++;
                LogTelemetry("ENTRY_SUBMITTED_SHORT", now, phase, "proxy_signal");
            }
        }

        private bool EvaluateLongSignal()
        {
            if (SignalMode == ProxySignalMode.Disabled)
                return false;

            if (SignalMode == ProxySignalMode.MomentumContinuation)
                return GetMinuteMomentumTicks() >= MinMomentumTicks;

            double current = Closes[1][0];
            double recentHigh = Highs[1][1];
            int maxLookback = Math.Min(BreakoutLookbackTicks, CurrentBars[1] - 1);
            for (int i = 2; i <= maxLookback; i++)
                recentHigh = Math.Max(recentHigh, Highs[1][i]);

            return current >= recentHigh + BreakoutBufferTicks * MnqTickSize
                && GetMinuteMomentumTicks() >= MinMomentumTicks;
        }

        private bool EvaluateShortSignal()
        {
            if (SignalMode == ProxySignalMode.Disabled)
                return false;

            if (SignalMode == ProxySignalMode.MomentumContinuation)
                return GetMinuteMomentumTicks() <= -MinMomentumTicks;

            double current = Closes[1][0];
            double recentLow = Lows[1][1];
            int maxLookback = Math.Min(BreakoutLookbackTicks, CurrentBars[1] - 1);
            for (int i = 2; i <= maxLookback; i++)
                recentLow = Math.Min(recentLow, Lows[1][i]);

            return current <= recentLow - BreakoutBufferTicks * MnqTickSize
                && GetMinuteMomentumTicks() <= -MinMomentumTicks;
        }

        private double GetMinuteMomentumTicks()
        {
            if (CurrentBars[2] < MomentumLookbackMinutes + 1)
                return 0.0;

            return (Closes[2][0] - Closes[2][MomentumLookbackMinutes]) / MnqTickSize;
        }

        private bool IsRth(DateTime t)
        {
            int hms = t.Hour * 10000 + t.Minute * 100 + t.Second;
            return hms >= 93000 && hms < 160000;
        }

        private string GetSessionPhase(DateTime t)
        {
            int hms = t.Hour * 10000 + t.Minute * 100 + t.Second;
            if (hms >= 93000 && hms < 100000) return "OPEN_0930_1000";
            if (hms >= 100000 && hms < 113000) return "MORNING_1000_1130";
            if (hms >= 113000 && hms < 133000) return "MIDDAY_1130_1330";
            if (hms >= 133000 && hms < 150000) return "AFTERNOON_1330_1500";
            if (hms >= 150000 && hms < 160000) return "POWER_1500_1600";
            return "OUTSIDE_RTH";
        }

        private bool IsLongAllowed(string phase)
        {
            if (phase == "OPEN_0930_1000") return AllowOpenLong;
            if (phase == "MORNING_1000_1130") return AllowMorningLong;
            if (phase == "AFTERNOON_1330_1500") return AllowAfternoonTrades;
            return false;
        }

        private bool IsShortAllowed(string phase)
        {
            if (phase == "MORNING_1000_1130") return AllowMorningShort;
            if (phase == "MIDDAY_1130_1330") return AllowMiddayShort;
            if (phase == "AFTERNOON_1330_1500") return AllowAfternoonTrades;
            if (phase == "POWER_1500_1600") return AllowPowerShort;
            return false;
        }

        private void ResetSessionIfNeeded(DateTime now)
        {
            if (currentSessionDate.Date == now.Date)
                return;

            currentSessionDate = now.Date;
            sessionStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            sessionTradeCount = 0;
            dailyLocked = false;
            activeEntryTime = Core.Globals.MinDate;
            activeEntryOrder = null;
            activeEntryName = string.Empty;
            lastFlatOrEntryResetTime = Core.Globals.MinDate;
            LogTelemetry("SESSION_RESET", now, "", "new_session");
        }

        private double GetCurrentBidSafe()
        {
            double bid = GetCurrentBid();
            if (double.IsNaN(bid) || bid <= 0)
                return Closes[1][0];
            return bid;
        }

        private double GetCurrentAskSafe()
        {
            double ask = GetCurrentAsk();
            if (double.IsNaN(ask) || ask <= 0)
                return Closes[1][0];
            return ask;
        }

        private double GetSpreadTicks(double bid, double ask)
        {
            if (ask <= 0 || bid <= 0 || ask < bid)
                return 999.0;
            return (ask - bid) / MnqTickSize;
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null)
                return;

            if (order.Name == "CGV111_LONG" || order.Name == "CGV111_SHORT")
            {
                activeEntryOrder = order;
                if (orderState == OrderState.Filled)
                {
                    activeEntryTime = time;
                    LogTelemetry("ENTRY_FILLED", time, order.Name,
                        string.Format(CultureInfo.InvariantCulture, "avgFill={0};qty={1}", averageFillPrice, filled));
                }
                else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                {
                    LogTelemetry("ENTRY_" + orderState.ToString().ToUpperInvariant(), time, order.Name, nativeError ?? string.Empty);
                    activeEntryOrder = null;
                    activeEntryName = string.Empty;
                    activeEntryTime = Core.Globals.MinDate;
                    lastFlatOrEntryResetTime = time;
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            string orderName = execution.Order.Name ?? string.Empty;
            LogTelemetry("EXECUTION", time, orderName,
                string.Format(CultureInfo.InvariantCulture, "price={0};qty={1};mp={2};orderId={3}", price, quantity, marketPosition, orderId));

            if (marketPosition == MarketPosition.Flat || Position.MarketPosition == MarketPosition.Flat)
            {
                activeEntryOrder = null;
                activeEntryName = string.Empty;
                activeEntryTime = Core.Globals.MinDate;
                lastFlatOrEntryResetTime = time;
            }
        }

        private void WriteTelemetryHeader()
        {
            if (!WriteTelemetry || telemetryHeaderWritten)
                return;

            try
            {
                string dir = Path.GetDirectoryName(TelemetryCsvPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (!File.Exists(TelemetryCsvPath))
                {
                    File.AppendAllText(TelemetryCsvPath,
                        "timestamp,event,phase_or_order,position,bid,ask,spread_ticks,session_trades,daily_locked,active_entry_time,note" + Environment.NewLine);
                }

                telemetryHeaderWritten = true;
            }
            catch (Exception ex)
            {
                Print("Telemetry header write failed: " + ex.Message);
            }
        }

        private void LogTelemetry(string eventName, DateTime timestamp, string phaseOrOrder, string note)
        {
            if (!WriteTelemetry)
                return;

            try
            {
                WriteTelemetryHeader();
                double bid = GetCurrentBidSafe();
                double ask = GetCurrentAskSafe();
                double spreadTicks = GetSpreadTicks(bid, ask);

                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7},{8},{9},{10}",
                    timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    EscapeCsv(eventName),
                    EscapeCsv(phaseOrOrder ?? string.Empty),
                    Position.MarketPosition,
                    bid,
                    ask,
                    spreadTicks,
                    sessionTradeCount,
                    dailyLocked ? 1 : 0,
                    activeEntryTime == Core.Globals.MinDate ? "" : activeEntryTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    EscapeCsv(note ?? string.Empty));

                File.AppendAllText(TelemetryCsvPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Print("Telemetry write failed: " + ex.Message);
            }
        }

        private static string EscapeCsv(string s)
        {
            if (s == null)
                return string.Empty;
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
