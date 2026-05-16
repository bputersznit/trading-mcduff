// CG_MNQ_DeployShell_v1_12.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-09 10:20:00 America/New_York
//
// PURPOSE
//   Corrected forward-test deployment shell after first Playback101 observation.
//
//   v1.11 proved the OCO bracket / one-MNQ shell works, but the placeholder
//   proxy signal over-traded badly in chop. The uploaded Playback101 execution
//   log shows rapid re-entry after stop-outs and many clustered stop losses.
//   That was expected risk from a simple proxy signal, but it is not acceptable
//   as a forward-test harness.
//
// KEY FIXES IN v1.12
//   1. Real post-exit cooldown:
//        v1.11 reset activeEntryTime after exit, which made the time cooldown
//        ineffective. v1.12 uses lastExitTime and lastEntryTime separately.
//   2. Minimum seconds between entries:
//        A hard wall prevents churn even if signals stay true.
//   3. Stop-streak lockout:
//        After N managed stop-loss exits, the strategy locks for a configurable
//        number of minutes.
//   4. Signal de-bounce:
//        Breakout condition must persist for ConfirmTicksRequired consecutive
//        1-tick updates before entry.
//   5. Stronger default proxy signal:
//        Defaults are much stricter than v1.11.
//   6. Session-phase trade caps:
//        Prevents one regime from machine-gunning trades.
//   7. Optional "short-only after open":
//        Because v1.9 showed the strongest deployable buckets were morning/midday
//        shorts, while long power-hour and many long chop trades were weaker.
//
// STILL IMPORTANT
//   This is still NOT the true ClickHouse structural signal.
//   It is a safer NinjaTrader execution shell for forward testing.
//   Replace EvaluateLongSignal() / EvaluateShortSignal() with the real structural
//   continuation trigger after shell behavior is stable.
//
// HARD GOVERNANCE
//   - 1 MNQ only.
//   - No overlap.
//   - No scaling.
//   - Broker/NT-managed target + stop bracket.
//   - 80 tick target.
//   - 16 tick stop.
//   - 120 second timeout.
//   - Entry spread gate.
//   - Runtime spread exit.
//   - RTH/session-side allowlist.
//   - Stop-streak lock.
//   - Daily loss lock.
//   - Telemetry.
//
// RECOMMENDED FIRST TEST SETTINGS
//   TargetTicks = 80
//   StopTicks = 16
//   MaxHoldSeconds = 120
//   MaxEntrySpreadTicks = 2
//   MaxRuntimeSpreadTicks = 6
//   CooldownSecondsAfterExit = 180
//   MinSecondsBetweenEntries = 180
//   BreakoutLookbackTicks = 600
//   BreakoutBufferTicks = 4
//   MomentumLookbackMinutes = 5
//   MinMomentumTicks = 20
//   ConfirmTicksRequired = 8
//   MaxTradesPerDay = 8
//   MaxTradesPerSessionPhase = 3
//   MaxConsecutiveStopLosses = 2
//   StopStreakLockoutMinutes = 30
//
// TELEMETRY
//   Documents\NinjaTrader 8\bin\Custom\Strategies\CG_MNQ_DeployShell_v1_12_telemetry.csv

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
    public class CG_MNQ_DeployShell_v1_12 : Strategy
    {
        private const int HardQuantity = 1;
        private const double MnqTickSize = 0.25;

        private Order activeEntryOrder;
        private DateTime activeEntryTime = Core.Globals.MinDate;
        private DateTime lastEntryTime = Core.Globals.MinDate;
        private DateTime lastExitTime = Core.Globals.MinDate;
        private DateTime stopStreakLockedUntil = Core.Globals.MinDate;

        private string activeEntryName = string.Empty;
        private int lastEntryBar = -999999;
        private DateTime currentSessionDate = Core.Globals.MinDate;
        private double sessionStartCumProfit = 0.0;
        private int sessionTradeCount = 0;
        private int openTradeCount = 0;
        private int morningTradeCount = 0;
        private int middayTradeCount = 0;
        private int afternoonTradeCount = 0;
        private int powerTradeCount = 0;
        private int consecutiveStopLosses = 0;
        private bool dailyLocked = false;
        private bool telemetryHeaderWritten = false;

        private int longConfirmTicks = 0;
        private int shortConfirmTicks = 0;

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
        [Range(20, 3000)]
        [Display(Name = "BreakoutLookbackTicks", GroupName = "Signal", Order = 2)]
        public int BreakoutLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 40)]
        [Display(Name = "BreakoutBufferTicks", GroupName = "Signal", Order = 3)]
        public int BreakoutBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "MomentumLookbackMinutes", GroupName = "Signal", Order = 4)]
        public int MomentumLookbackMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MinMomentumTicks", GroupName = "Signal", Order = 5)]
        public int MinMomentumTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ConfirmTicksRequired", GroupName = "Signal", Order = 6)]
        public int ConfirmTicksRequired { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1800)]
        [Display(Name = "CooldownSecondsAfterExit", GroupName = "Anti-Churn", Order = 7)]
        public int CooldownSecondsAfterExit { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1800)]
        [Display(Name = "MinSecondsBetweenEntries", GroupName = "Anti-Churn", Order = 8)]
        public int MinSecondsBetweenEntries { get; set; }

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
        [Display(Name = "ShortOnlyAfterOpen", GroupName = "Session Allowlist", Order = 46)]
        public bool ShortOnlyAfterOpen { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxTradesPerDay", GroupName = "Daily Governance", Order = 50)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxTradesPerSessionPhase", GroupName = "Daily Governance", Order = 51)]
        public int MaxTradesPerSessionPhase { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxConsecutiveStopLosses", GroupName = "Daily Governance", Order = 52)]
        public int MaxConsecutiveStopLosses { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name = "StopStreakLockoutMinutes", GroupName = "Daily Governance", Order = 53)]
        public int StopStreakLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "DailyLossLockUsd", GroupName = "Daily Governance", Order = 54)]
        public double DailyLossLockUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableDailyLossLock", GroupName = "Daily Governance", Order = 55)]
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
                Name = "CG_MNQ_DeployShell_v1_12";
                Description = "Safer forward-test MNQ deployment shell: anti-churn, stop-streak lockout, one MNQ only, OCO bracket, spread/session governance.";
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
                BreakoutLookbackTicks = 600;
                BreakoutBufferTicks = 4;
                MomentumLookbackMinutes = 5;
                MinMomentumTicks = 20;
                ConfirmTicksRequired = 8;

                CooldownSecondsAfterExit = 180;
                MinSecondsBetweenEntries = 180;

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
                ShortOnlyAfterOpen = false;

                MaxTradesPerDay = 8;
                MaxTradesPerSessionPhase = 3;
                MaxConsecutiveStopLosses = 2;
                StopStreakLockoutMinutes = 30;
                DailyLossLockUsd = 250.0;
                EnableDailyLossLock = true;

                WriteTelemetry = true;
                string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "bin", "Custom", "Strategies");
                TelemetryCsvPath = Path.Combine(defaultDir, "CG_MNQ_DeployShell_v1_12_telemetry.csv");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
                AddDataSeries(BarsPeriodType.Minute, 1);

                SetProfitTarget("CGV112_LONG", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CGV112_LONG", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("CGV112_SHORT", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CGV112_SHORT", CalculationMode.Ticks, StopTicks, false);
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

            if (CurrentBars[1] < Math.Max(BreakoutLookbackTicks + 2, 10))
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
                ExitLong("CGV112_EmergencyQty_Long", "CGV112_LONG");
                ExitShort("CGV112_EmergencyQty_Short", "CGV112_SHORT");
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
                        ExitLong("CGV112_DailyLock_Long", "CGV112_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CGV112_DailyLock_Short", "CGV112_SHORT");
                    return;
                }
            }

            if (Position.MarketPosition != MarketPosition.Flat && spreadTicks > MaxRuntimeSpreadTicks)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("CGV112_RuntimeSpread_Long", "CGV112_LONG");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("CGV112_RuntimeSpread_Short", "CGV112_SHORT");

                LogTelemetry("RUNTIME_SPREAD_EXIT", now, "", "spread_gt_runtime_max");
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat && activeEntryTime != Core.Globals.MinDate)
            {
                double heldSeconds = (now - activeEntryTime).TotalSeconds;
                if (heldSeconds >= MaxHoldSeconds)
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong("CGV112_TimeExit_Long", "CGV112_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CGV112_TimeExit_Short", "CGV112_SHORT");

                    LogTelemetry("TIME_EXIT_SUBMITTED", now, "", "max_hold_seconds");
                    return;
                }
            }

            if (dailyLocked)
                return;

            if (now < stopStreakLockedUntil)
                return;

            if (RequireRth && !IsRth(now))
                return;

            if (sessionTradeCount >= MaxTradesPerDay)
                return;

            if (Position.MarketPosition != MarketPosition.Flat || activeEntryOrder != null)
                return;

            if (lastExitTime != Core.Globals.MinDate && (now - lastExitTime).TotalSeconds < CooldownSecondsAfterExit)
                return;

            if (lastEntryTime != Core.Globals.MinDate && (now - lastEntryTime).TotalSeconds < MinSecondsBetweenEntries)
                return;

            if (spreadTicks > MaxEntrySpreadTicks)
                return;

            string phase = GetSessionPhase(now);

            if (GetPhaseTradeCount(phase) >= MaxTradesPerSessionPhase)
                return;

            bool allowLong = IsLongAllowed(phase);
            bool allowShort = IsShortAllowed(phase);

            bool rawLong = allowLong && EvaluateLongSignal();
            bool rawShort = allowShort && EvaluateShortSignal();

            longConfirmTicks = rawLong ? longConfirmTicks + 1 : 0;
            shortConfirmTicks = rawShort ? shortConfirmTicks + 1 : 0;

            bool longSignal = longConfirmTicks >= ConfirmTicksRequired;
            bool shortSignal = shortConfirmTicks >= ConfirmTicksRequired;

            if (longSignal && shortSignal)
            {
                LogTelemetry("SIGNAL_CONFLICT_SKIP", now, phase, "both_long_and_short_true");
                return;
            }

            if (longSignal)
                SubmitEntry(now, phase, "LONG");
            else if (shortSignal)
                SubmitEntry(now, phase, "SHORT");
        }

        private void SubmitEntry(DateTime now, string phase, string side)
        {
            lastEntryTime = now;
            lastEntryBar = CurrentBar;
            IncrementPhaseTradeCount(phase);
            sessionTradeCount++;

            longConfirmTicks = 0;
            shortConfirmTicks = 0;

            if (side == "LONG")
            {
                activeEntryName = "CGV112_LONG";
                activeEntryTime = now;
                EnterLong(HardQuantity, activeEntryName);
                LogTelemetry("ENTRY_SUBMITTED_LONG", now, phase, "proxy_signal_confirmed");
            }
            else
            {
                activeEntryName = "CGV112_SHORT";
                activeEntryTime = now;
                EnterShort(HardQuantity, activeEntryName);
                LogTelemetry("ENTRY_SUBMITTED_SHORT", now, phase, "proxy_signal_confirmed");
            }
        }

        private bool EvaluateLongSignal()
        {
            if (SignalMode == ProxySignalMode.Disabled)
                return false;

            if (ShortOnlyAfterOpen && GetSessionPhase(Times[1][0]) != "OPEN_0930_1000")
                return false;

            if (SignalMode == ProxySignalMode.MomentumContinuation)
                return GetMinuteMomentumTicks() >= MinMomentumTicks;

            double current = Closes[1][0];
            double recentHigh = Highs[1][1];

            for (int i = 2; i <= BreakoutLookbackTicks && i < CurrentBars[1]; i++)
                recentHigh = Math.Max(recentHigh, Highs[1][i]);

            bool breakout = current >= recentHigh + BreakoutBufferTicks * MnqTickSize;
            bool momentum = GetMinuteMomentumTicks() >= MinMomentumTicks;
            return breakout && momentum;
        }

        private bool EvaluateShortSignal()
        {
            if (SignalMode == ProxySignalMode.Disabled)
                return false;

            if (SignalMode == ProxySignalMode.MomentumContinuation)
                return GetMinuteMomentumTicks() <= -MinMomentumTicks;

            double current = Closes[1][0];
            double recentLow = Lows[1][1];

            for (int i = 2; i <= BreakoutLookbackTicks && i < CurrentBars[1]; i++)
                recentLow = Math.Min(recentLow, Lows[1][i]);

            bool breakdown = current <= recentLow - BreakoutBufferTicks * MnqTickSize;
            bool momentum = GetMinuteMomentumTicks() <= -MinMomentumTicks;
            return breakdown && momentum;
        }

        private double GetMinuteMomentumTicks()
        {
            if (CurrentBars[2] < MomentumLookbackMinutes + 1)
                return 0.0;

            double nowClose = Closes[2][0];
            double pastClose = Closes[2][MomentumLookbackMinutes];
            return (nowClose - pastClose) / MnqTickSize;
        }

        private bool IsRth(DateTime t)
        {
            int hms = t.Hour * 10000 + t.Minute * 100 + t.Second;
            return hms >= 93000 && hms < 160000;
        }

        private string GetSessionPhase(DateTime t)
        {
            int hms = t.Hour * 10000 + t.Minute * 100 + t.Second;

            if (hms >= 93000 && hms < 100000)
                return "OPEN_0930_1000";
            if (hms >= 100000 && hms < 113000)
                return "MORNING_1000_1130";
            if (hms >= 113000 && hms < 133000)
                return "MIDDAY_1130_1330";
            if (hms >= 133000 && hms < 150000)
                return "AFTERNOON_1330_1500";
            if (hms >= 150000 && hms < 160000)
                return "POWER_1500_1600";

            return "OUTSIDE_RTH";
        }

        private bool IsLongAllowed(string phase)
        {
            if (phase == "OPEN_0930_1000")
                return AllowOpenLong;
            if (phase == "MORNING_1000_1130")
                return AllowMorningLong;
            if (phase == "MIDDAY_1130_1330")
                return false;
            if (phase == "AFTERNOON_1330_1500")
                return AllowAfternoonTrades;
            if (phase == "POWER_1500_1600")
                return false;
            return false;
        }

        private bool IsShortAllowed(string phase)
        {
            if (phase == "OPEN_0930_1000")
                return false;
            if (phase == "MORNING_1000_1130")
                return AllowMorningShort;
            if (phase == "MIDDAY_1130_1330")
                return AllowMiddayShort;
            if (phase == "AFTERNOON_1330_1500")
                return AllowAfternoonTrades;
            if (phase == "POWER_1500_1600")
                return AllowPowerShort;
            return false;
        }

        private int GetPhaseTradeCount(string phase)
        {
            if (phase == "OPEN_0930_1000") return openTradeCount;
            if (phase == "MORNING_1000_1130") return morningTradeCount;
            if (phase == "MIDDAY_1130_1330") return middayTradeCount;
            if (phase == "AFTERNOON_1330_1500") return afternoonTradeCount;
            if (phase == "POWER_1500_1600") return powerTradeCount;
            return 999999;
        }

        private void IncrementPhaseTradeCount(string phase)
        {
            if (phase == "OPEN_0930_1000") openTradeCount++;
            else if (phase == "MORNING_1000_1130") morningTradeCount++;
            else if (phase == "MIDDAY_1130_1330") middayTradeCount++;
            else if (phase == "AFTERNOON_1330_1500") afternoonTradeCount++;
            else if (phase == "POWER_1500_1600") powerTradeCount++;
        }

        private void ResetSessionIfNeeded(DateTime now)
        {
            if (currentSessionDate.Date == now.Date)
                return;

            currentSessionDate = now.Date;
            sessionStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            sessionTradeCount = 0;
            openTradeCount = 0;
            morningTradeCount = 0;
            middayTradeCount = 0;
            afternoonTradeCount = 0;
            powerTradeCount = 0;
            consecutiveStopLosses = 0;
            dailyLocked = false;
            stopStreakLockedUntil = Core.Globals.MinDate;
            activeEntryTime = Core.Globals.MinDate;
            activeEntryOrder = null;
            activeEntryName = string.Empty;
            longConfirmTicks = 0;
            shortConfirmTicks = 0;

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

        protected override void OnOrderUpdate(
            Order order,
            double limitPrice,
            double stopPrice,
            int quantity,
            int filled,
            double averageFillPrice,
            OrderState orderState,
            DateTime time,
            ErrorCode error,
            string nativeError)
        {
            if (order == null)
                return;

            if (order.Name == "CGV112_LONG" || order.Name == "CGV112_SHORT")
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
                }
            }
        }

        protected override void OnExecutionUpdate(
            Execution execution,
            string executionId,
            double price,
            int quantity,
            MarketPosition marketPosition,
            string orderId,
            DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            string orderName = execution.Order.Name ?? string.Empty;
            LogTelemetry("EXECUTION", time, orderName,
                string.Format(CultureInfo.InvariantCulture, "price={0};qty={1};mp={2};orderId={3}", price, quantity, marketPosition, orderId));

            bool looksLikeStop = orderName.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0
                                || orderName.IndexOf("loss", StringComparison.OrdinalIgnoreCase) >= 0;

            bool looksLikeTarget = orderName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                                  || orderName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            if (marketPosition == MarketPosition.Flat || Position.MarketPosition == MarketPosition.Flat)
            {
                lastExitTime = time;

                if (looksLikeStop)
                {
                    consecutiveStopLosses++;
                    if (consecutiveStopLosses >= MaxConsecutiveStopLosses)
                    {
                        stopStreakLockedUntil = time.AddMinutes(StopStreakLockoutMinutes);
                        LogTelemetry("STOP_STREAK_LOCKOUT", time, orderName,
                            string.Format(CultureInfo.InvariantCulture, "consecutiveStops={0};lockedUntil={1}",
                                consecutiveStopLosses,
                                stopStreakLockedUntil.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                    }
                }
                else if (looksLikeTarget)
                {
                    consecutiveStopLosses = 0;
                }

                activeEntryOrder = null;
                activeEntryName = string.Empty;
                activeEntryTime = Core.Globals.MinDate;
                longConfirmTicks = 0;
                shortConfirmTicks = 0;
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
                        "timestamp,event,phase_or_order,position,bid,ask,spread_ticks,session_trades,open_trades,morning_trades,midday_trades,afternoon_trades,power_trades,consecutive_stops,daily_locked,locked_until,active_entry_time,last_exit_time,note" + Environment.NewLine);
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
                    "{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18}",
                    timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    EscapeCsv(eventName),
                    EscapeCsv(phaseOrOrder ?? string.Empty),
                    Position.MarketPosition,
                    bid,
                    ask,
                    spreadTicks,
                    sessionTradeCount,
                    openTradeCount,
                    morningTradeCount,
                    middayTradeCount,
                    afternoonTradeCount,
                    powerTradeCount,
                    consecutiveStopLosses,
                    dailyLocked ? 1 : 0,
                    stopStreakLockedUntil == Core.Globals.MinDate ? "" : stopStreakLockedUntil.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    activeEntryTime == Core.Globals.MinDate ? "" : activeEntryTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    lastExitTime == Core.Globals.MinDate ? "" : lastExitTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
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
