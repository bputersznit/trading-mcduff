// CG_MNQ_DeployShell_v1_13.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-09 10:45:00 America/New_York
//
// PURPOSE
//   Diagnostic/forward-test shell after v1.12 produced no executions.
//   This version keeps the deployment safety shell but adds:
//     - explicit diagnostics for why entries are blocked,
//     - easier default proxy signal,
//     - optional momentum-only trigger,
//     - status Print output every N seconds,
//     - hard one-MNQ/no-overlap/no-scaling governance.
//
// IMPORTANT OBSERVATION
//   If enabled at 10:58 AM, the strategy cannot trade the already-finished
//   10:00-10:50 move. For Playback validation, enable the strategy before
//   pressing Play, ideally before 09:30 RTH.
//
// DEFAULTS IN v1.13 ARE FOR DIAGNOSTIC PLAYBACK, NOT LIVE.
//   They are intentionally easier than v1.12 so we can verify the shell actually
//   submits orders under controlled conditions.
//
// HARD GOVERNANCE
//   - 1 MNQ only.
//   - No overlap.
//   - No scaling.
//   - Target/stop bracket via SetProfitTarget / SetStopLoss.
//   - 80 tick target.
//   - 16 tick stop.
//   - 120 sec timeout.
//   - Entry spread gate.
//   - Runtime spread exit.
//   - Daily loss lock.
//   - Stop-streak lockout.
//   - Session/side allowlist.
//
// RECOMMENDED FIRST DIAGNOSTIC RUN
//   Chart: MNQ 1000-tick or 1-tick is OK; strategy adds internal 1-tick + 1-minute.
//   Playback: rewind to before 09:30, enable strategy, then press Play.
//   SignalMode = MomentumContinuation
//   MinMomentumTicks = 8
//   MomentumLookbackMinutes = 2
//   ConfirmTicksRequired = 2
//   CooldownSecondsAfterExit = 120
//   MinSecondsBetweenEntries = 120
//   MaxTradesPerDay = 8
//   ShortOnlyAfterOpen = false
//
// AFTER DIAGNOSTIC RUN
//   If it trades too much, raise MinMomentumTicks / ConfirmTicksRequired.
//   If it still does not trade, read telemetry rows beginning BLOCKED_* or STATUS.

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
    public class CG_MNQ_DeployShell_v1_13 : Strategy
    {
        private const int HardQuantity = 1;
        private const double MnqTickSize = 0.25;

        private Order activeEntryOrder;
        private DateTime activeEntryTime = Core.Globals.MinDate;
        private DateTime lastEntryTime = Core.Globals.MinDate;
        private DateTime lastExitTime = Core.Globals.MinDate;
        private DateTime stopStreakLockedUntil = Core.Globals.MinDate;
        private DateTime lastStatusPrintTime = Core.Globals.MinDate;

        private string activeEntryName = string.Empty;
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

        private long blockedNotRth = 0;
        private long blockedSpread = 0;
        private long blockedNotFlat = 0;
        private long blockedCooldown = 0;
        private long blockedSession = 0;
        private long blockedSignal = 0;
        private long entriesSubmitted = 0;

        public enum ProxySignalMode
        {
            Disabled,
            BreakoutContinuation,
            MomentumContinuation,
            EitherBreakoutOrMomentum
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

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", GroupName = "Telemetry", Order = 62)]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(5, 300)]
        [Display(Name = "StatusPrintSeconds", GroupName = "Telemetry", Order = 63)]
        public int StatusPrintSeconds { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_MNQ_DeployShell_v1_13";
                Description = "Diagnostic safer forward-test MNQ shell: one MNQ, no overlap, OCO, anti-churn, and detailed no-entry diagnostics.";
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

                SignalMode = ProxySignalMode.MomentumContinuation;
                BreakoutLookbackTicks = 300;
                BreakoutBufferTicks = 2;
                MomentumLookbackMinutes = 2;
                MinMomentumTicks = 8;
                ConfirmTicksRequired = 2;

                CooldownSecondsAfterExit = 120;
                MinSecondsBetweenEntries = 120;

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
                MaxTradesPerSessionPhase = 4;
                MaxConsecutiveStopLosses = 2;
                StopStreakLockoutMinutes = 20;
                DailyLossLockUsd = 250.0;
                EnableDailyLossLock = true;

                WriteTelemetry = true;
                PrintDiagnostics = true;
                StatusPrintSeconds = 30;

                string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "bin", "Custom", "Strategies");
                TelemetryCsvPath = Path.Combine(defaultDir, "CG_MNQ_DeployShell_v1_13_telemetry.csv");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
                AddDataSeries(BarsPeriodType.Minute, 1);

                SetProfitTarget("CGV113_LONG", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CGV113_LONG", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("CGV113_SHORT", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CGV113_SHORT", CalculationMode.Ticks, StopTicks, false);
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

            if (CurrentBars[1] < Math.Max(BreakoutLookbackTicks + 2, 10) || CurrentBars[2] < MomentumLookbackMinutes + 2)
                return;

            DateTime now = Times[1][0];
            ResetSessionIfNeeded(now);

            double bid = GetCurrentBidSafe();
            double ask = GetCurrentAskSafe();
            double spreadTicks = GetSpreadTicks(bid, ask);
            string phase = GetSessionPhase(now);

            MaybePrintStatus(now, phase, spreadTicks);

            if (Position.Quantity > HardQuantity)
            {
                LogTelemetry("EMERGENCY_FLATTEN_QTY_GT_1", now, phase, "position_qty_gt_1");
                ExitLong("CGV113_EmergencyQty_Long", "CGV113_LONG");
                ExitShort("CGV113_EmergencyQty_Short", "CGV113_SHORT");
                return;
            }

            if (EnableDailyLossLock && !dailyLocked)
            {
                double realizedToday = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartCumProfit;
                if (realizedToday <= -Math.Abs(DailyLossLockUsd))
                {
                    dailyLocked = true;
                    LogTelemetry("DAILY_LOCK_TRIGGERED", now, phase, "realized_loss_limit");
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong("CGV113_DailyLock_Long", "CGV113_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CGV113_DailyLock_Short", "CGV113_SHORT");
                    return;
                }
            }

            if (Position.MarketPosition != MarketPosition.Flat && spreadTicks > MaxRuntimeSpreadTicks)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("CGV113_RuntimeSpread_Long", "CGV113_LONG");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("CGV113_RuntimeSpread_Short", "CGV113_SHORT");

                LogTelemetry("RUNTIME_SPREAD_EXIT", now, phase, "spread_gt_runtime_max");
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat && activeEntryTime != Core.Globals.MinDate)
            {
                double heldSeconds = (now - activeEntryTime).TotalSeconds;
                if (heldSeconds >= MaxHoldSeconds)
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong("CGV113_TimeExit_Long", "CGV113_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CGV113_TimeExit_Short", "CGV113_SHORT");

                    LogTelemetry("TIME_EXIT_SUBMITTED", now, phase, "max_hold_seconds");
                    return;
                }
            }

            if (dailyLocked)
            {
                blockedSignal++;
                return;
            }

            if (now < stopStreakLockedUntil)
            {
                blockedSignal++;
                return;
            }

            if (RequireRth && !IsRth(now))
            {
                blockedNotRth++;
                return;
            }

            if (sessionTradeCount >= MaxTradesPerDay)
            {
                blockedSignal++;
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat || activeEntryOrder != null)
            {
                blockedNotFlat++;
                return;
            }

            if (lastExitTime != Core.Globals.MinDate && (now - lastExitTime).TotalSeconds < CooldownSecondsAfterExit)
            {
                blockedCooldown++;
                return;
            }

            if (lastEntryTime != Core.Globals.MinDate && (now - lastEntryTime).TotalSeconds < MinSecondsBetweenEntries)
            {
                blockedCooldown++;
                return;
            }

            if (spreadTicks > MaxEntrySpreadTicks)
            {
                blockedSpread++;
                return;
            }

            if (GetPhaseTradeCount(phase) >= MaxTradesPerSessionPhase)
            {
                blockedSession++;
                return;
            }

            bool allowLong = IsLongAllowed(phase);
            bool allowShort = IsShortAllowed(phase);

            bool rawLong = allowLong && EvaluateLongSignal();
            bool rawShort = allowShort && EvaluateShortSignal();

            longConfirmTicks = rawLong ? longConfirmTicks + 1 : 0;
            shortConfirmTicks = rawShort ? shortConfirmTicks + 1 : 0;

            bool longSignal = longConfirmTicks >= ConfirmTicksRequired;
            bool shortSignal = shortConfirmTicks >= ConfirmTicksRequired;

            if (!longSignal && !shortSignal)
            {
                blockedSignal++;
                return;
            }

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
            IncrementPhaseTradeCount(phase);
            sessionTradeCount++;
            entriesSubmitted++;

            longConfirmTicks = 0;
            shortConfirmTicks = 0;

            if (side == "LONG")
            {
                activeEntryName = "CGV113_LONG";
                activeEntryTime = now;
                EnterLong(HardQuantity, activeEntryName);
                LogTelemetry("ENTRY_SUBMITTED_LONG", now, phase, "proxy_signal_confirmed");
            }
            else
            {
                activeEntryName = "CGV113_SHORT";
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

            double momentum = GetMinuteMomentumTicks();
            bool momentumLong = momentum >= MinMomentumTicks;
            bool breakoutLong = IsBreakoutLong();

            if (SignalMode == ProxySignalMode.MomentumContinuation)
                return momentumLong;
            if (SignalMode == ProxySignalMode.BreakoutContinuation)
                return momentumLong && breakoutLong;
            if (SignalMode == ProxySignalMode.EitherBreakoutOrMomentum)
                return momentumLong || breakoutLong;

            return false;
        }

        private bool EvaluateShortSignal()
        {
            if (SignalMode == ProxySignalMode.Disabled)
                return false;

            double momentum = GetMinuteMomentumTicks();
            bool momentumShort = momentum <= -MinMomentumTicks;
            bool breakdownShort = IsBreakoutShort();

            if (SignalMode == ProxySignalMode.MomentumContinuation)
                return momentumShort;
            if (SignalMode == ProxySignalMode.BreakoutContinuation)
                return momentumShort && breakdownShort;
            if (SignalMode == ProxySignalMode.EitherBreakoutOrMomentum)
                return momentumShort || breakdownShort;

            return false;
        }

        private bool IsBreakoutLong()
        {
            double current = Closes[1][0];
            double recentHigh = Highs[1][1];

            for (int i = 2; i <= BreakoutLookbackTicks && i < CurrentBars[1]; i++)
                recentHigh = Math.Max(recentHigh, Highs[1][i]);

            return current >= recentHigh + BreakoutBufferTicks * MnqTickSize;
        }

        private bool IsBreakoutShort()
        {
            double current = Closes[1][0];
            double recentLow = Lows[1][1];

            for (int i = 2; i <= BreakoutLookbackTicks && i < CurrentBars[1]; i++)
                recentLow = Math.Min(recentLow, Lows[1][i]);

            return current <= recentLow - BreakoutBufferTicks * MnqTickSize;
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
            ResetBlockCounters();

            LogTelemetry("SESSION_RESET", now, "", "new_session");
        }

        private void ResetBlockCounters()
        {
            blockedNotRth = 0;
            blockedSpread = 0;
            blockedNotFlat = 0;
            blockedCooldown = 0;
            blockedSession = 0;
            blockedSignal = 0;
            entriesSubmitted = 0;
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

        private void MaybePrintStatus(DateTime now, string phase, double spreadTicks)
        {
            if (!PrintDiagnostics)
                return;

            if (lastStatusPrintTime != Core.Globals.MinDate && (now - lastStatusPrintTime).TotalSeconds < StatusPrintSeconds)
                return;

            lastStatusPrintTime = now;
            string msg = string.Format(CultureInfo.InvariantCulture,
                "CGV113 STATUS {0} phase={1} pos={2} trades={3} spread={4:F1} mom={5:F1} Lconf={6} Sconf={7} blocks[rth={8},spr={9},flat={10},cool={11},sess={12},sig={13}] entries={14}",
                now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                phase,
                Position.MarketPosition,
                sessionTradeCount,
                spreadTicks,
                GetMinuteMomentumTicks(),
                longConfirmTicks,
                shortConfirmTicks,
                blockedNotRth,
                blockedSpread,
                blockedNotFlat,
                blockedCooldown,
                blockedSession,
                blockedSignal,
                entriesSubmitted);

            Print(msg);
            LogTelemetry("STATUS", now, phase, msg);
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

            if (order.Name == "CGV113_LONG" || order.Name == "CGV113_SHORT")
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
                        "timestamp,event,phase_or_order,position,bid,ask,spread_ticks,momentum_ticks,session_trades,open_trades,morning_trades,midday_trades,afternoon_trades,power_trades,consecutive_stops,daily_locked,locked_until,active_entry_time,last_exit_time,note" + Environment.NewLine);
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
                    "{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7:F2},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19}",
                    timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    EscapeCsv(eventName),
                    EscapeCsv(phaseOrOrder ?? string.Empty),
                    Position.MarketPosition,
                    bid,
                    ask,
                    spreadTicks,
                    GetMinuteMomentumTicks(),
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
