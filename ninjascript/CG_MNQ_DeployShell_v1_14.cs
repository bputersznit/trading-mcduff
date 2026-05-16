// CG_MNQ_DeployShell_v1_14.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-09 11:10:00 America/New_York
//
// PURPOSE
//   Structure-confirmed forward-test shell for MNQ after v1.13 diagnostics.
//
// v1.13 proved the execution shell works, but the uploaded Playback101 run showed
// a bad simple momentum proxy: 8 trades, all stop-loss exits. v1.14 keeps the
// proven execution shell and replaces the raw momentum proxy with a stricter
// opening-impulse / retest / continuation structure trigger.
//
// ARCHITECTURE
//   1. Observe RTH opening impulse range.
//   2. Wait NoTradeFirstMinutes before any entry.
//   3. Establish directional bias after price breaks opening impulse by BiasBreakTicks.
//   4. Flip bias on opposite opening-impulse CHOCH.
//   5. Arm a retest only when price pulls back toward a fast EMA in the trend context.
//   6. Enter only after renewed continuation break plus momentum confirmation.
//
// HARD GOVERNANCE
//   - Quantity hardcoded to 1 MNQ.
//   - No overlap / no scaling.
//   - Managed profit target and stop loss per entry.
//   - 80 tick target / 16 tick stop / 120 second timeout by default.
//   - RTH only by default.
//   - Entry spread gate and runtime spread exit.
//   - Daily loss lock and stop-streak lockout.
//   - Defensive telemetry: no null reference from unavailable bid/ask/series.
//
// RECOMMENDED FIRST SETTINGS
//   OpeningImpulseMinutes = 5
//   NoTradeFirstMinutes = 5
//   StructureLookbackTicks = 700
//   RetestLookbackTicks = 300
//   EmaFastPeriod = 20
//   EmaSlowPeriod = 60
//   BiasBreakTicks = 8
//   RetestDistanceTicks = 12
//   ContinuationBreakTicks = 3
//   MinTrendSeparationTicks = 8
//   MinMomentumTicks = 12
//   ConfirmTicksRequired = 3
//   CooldownSecondsAfterExit = 180
//   MinSecondsBetweenEntries = 240
//   MaxTradesPerDay = 6
//   MaxTradesPerSessionPhase = 2
//   MaxConsecutiveStopLosses = 2
//   StopStreakLockoutMinutes = 30

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_MNQ_DeployShell_v1_14 : Strategy
    {
        private const int HardQuantity = 1;
        private const double MnqTickSize = 0.25;

        private Order activeEntryOrder;
        private DateTime activeEntryTime = Core.Globals.MinDate;
        private DateTime lastEntryTime = Core.Globals.MinDate;
        private DateTime lastExitTime = Core.Globals.MinDate;
        private DateTime stopStreakLockedUntil = Core.Globals.MinDate;
        private DateTime lastStatusPrintTime = Core.Globals.MinDate;
        private DateTime currentSessionDate = Core.Globals.MinDate;

        private string activeEntryName = string.Empty;
        private double sessionStartCumProfit = 0.0;

        private int sessionTradeCount;
        private int openTradeCount;
        private int morningTradeCount;
        private int middayTradeCount;
        private int afternoonTradeCount;
        private int powerTradeCount;
        private int consecutiveStopLosses;

        private bool dailyLocked;
        private bool telemetryHeaderWritten;

        private double openingHigh = double.MinValue;
        private double openingLow = double.MaxValue;
        private bool openingRangeComplete;
        private int rthBias; // +1 long, -1 short, 0 neutral
        private string rthBiasReason = "NONE";

        private bool longRetestArmed;
        private bool shortRetestArmed;
        private double longContinuationTrigger = double.MinValue;
        private double shortContinuationTrigger = double.MaxValue;

        private int longConfirmTicks;
        private int shortConfirmTicks;

        private EMA emaFast;
        private EMA emaSlow;

        private long blockedNotRth;
        private long blockedSpread;
        private long blockedNotFlat;
        private long blockedCooldown;
        private long blockedSession;
        private long blockedSignal;
        private long blockedStructure;
        private long entriesSubmitted;

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "OpeningImpulseMinutes", GroupName = "Structure", Order = 1)]
        public int OpeningImpulseMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(0, 30)]
        [Display(Name = "NoTradeFirstMinutes", GroupName = "Structure", Order = 2)]
        public int NoTradeFirstMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(50, 5000)]
        [Display(Name = "StructureLookbackTicks", GroupName = "Structure", Order = 3)]
        public int StructureLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(20, 3000)]
        [Display(Name = "RetestLookbackTicks", GroupName = "Structure", Order = 4)]
        public int RetestLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BiasBreakTicks", GroupName = "Structure", Order = 5)]
        public int BiasBreakTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "RetestDistanceTicks", GroupName = "Structure", Order = 6)]
        public int RetestDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "ContinuationBreakTicks", GroupName = "Structure", Order = 7)]
        public int ContinuationBreakTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MinTrendSeparationTicks", GroupName = "Structure", Order = 8)]
        public int MinTrendSeparationTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MinMomentumTicks", GroupName = "Structure", Order = 9)]
        public int MinMomentumTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ConfirmTicksRequired", GroupName = "Structure", Order = 10)]
        public int ConfirmTicksRequired { get; set; }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name = "EmaFastPeriod", GroupName = "Structure", Order = 11)]
        public int EmaFastPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 400)]
        [Display(Name = "EmaSlowPeriod", GroupName = "Structure", Order = 12)]
        public int EmaSlowPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1800)]
        [Display(Name = "CooldownSecondsAfterExit", GroupName = "Anti-Churn", Order = 20)]
        public int CooldownSecondsAfterExit { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1800)]
        [Display(Name = "MinSecondsBetweenEntries", GroupName = "Anti-Churn", Order = 21)]
        public int MinSecondsBetweenEntries { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "TargetTicks", GroupName = "Risk", Order = 30)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "StopTicks", GroupName = "Risk", Order = 31)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 900)]
        [Display(Name = "MaxHoldSeconds", GroupName = "Risk", Order = 32)]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxEntrySpreadTicks", GroupName = "Execution", Order = 40)]
        public int MaxEntrySpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxRuntimeSpreadTicks", GroupName = "Execution", Order = 41)]
        public int MaxRuntimeSpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireRth", GroupName = "Time", Order = 50)]
        public bool RequireRth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowOpenLong", GroupName = "Session Allowlist", Order = 60)]
        public bool AllowOpenLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMorningLong", GroupName = "Session Allowlist", Order = 61)]
        public bool AllowMorningLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMorningShort", GroupName = "Session Allowlist", Order = 62)]
        public bool AllowMorningShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMiddayShort", GroupName = "Session Allowlist", Order = 63)]
        public bool AllowMiddayShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowPowerShort", GroupName = "Session Allowlist", Order = 64)]
        public bool AllowPowerShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowAfternoonTrades", GroupName = "Session Allowlist", Order = 65)]
        public bool AllowAfternoonTrades { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxTradesPerDay", GroupName = "Daily Governance", Order = 70)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxTradesPerSessionPhase", GroupName = "Daily Governance", Order = 71)]
        public int MaxTradesPerSessionPhase { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxConsecutiveStopLosses", GroupName = "Daily Governance", Order = 72)]
        public int MaxConsecutiveStopLosses { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name = "StopStreakLockoutMinutes", GroupName = "Daily Governance", Order = 73)]
        public int StopStreakLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "DailyLossLockUsd", GroupName = "Daily Governance", Order = 74)]
        public double DailyLossLockUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableDailyLossLock", GroupName = "Daily Governance", Order = 75)]
        public bool EnableDailyLossLock { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TelemetryCsvPath", GroupName = "Telemetry", Order = 80)]
        public string TelemetryCsvPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "WriteTelemetry", GroupName = "Telemetry", Order = 81)]
        public bool WriteTelemetry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", GroupName = "Telemetry", Order = 82)]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(5, 300)]
        [Display(Name = "StatusPrintSeconds", GroupName = "Telemetry", Order = 83)]
        public int StatusPrintSeconds { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_MNQ_DeployShell_v1_14";
                Description = "Structure-confirmed MNQ forward-test shell: opening impulse bias, retest, continuation break, one MNQ, OCO, anti-churn.";
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

                OpeningImpulseMinutes = 5;
                NoTradeFirstMinutes = 5;
                StructureLookbackTicks = 700;
                RetestLookbackTicks = 300;
                BiasBreakTicks = 8;
                RetestDistanceTicks = 12;
                ContinuationBreakTicks = 3;
                MinTrendSeparationTicks = 8;
                MinMomentumTicks = 12;
                ConfirmTicksRequired = 3;
                EmaFastPeriod = 20;
                EmaSlowPeriod = 60;

                CooldownSecondsAfterExit = 180;
                MinSecondsBetweenEntries = 240;

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

                MaxTradesPerDay = 6;
                MaxTradesPerSessionPhase = 2;
                MaxConsecutiveStopLosses = 2;
                StopStreakLockoutMinutes = 30;
                DailyLossLockUsd = 250.0;
                EnableDailyLossLock = true;

                WriteTelemetry = true;
                PrintDiagnostics = true;
                StatusPrintSeconds = 30;

                string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "bin", "Custom", "Strategies");
                TelemetryCsvPath = Path.Combine(defaultDir, "CG_MNQ_DeployShell_v1_14_telemetry.csv");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
                AddDataSeries(BarsPeriodType.Minute, 1);

                SetProfitTarget("CGV114_LONG", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CGV114_LONG", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("CGV114_SHORT", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CGV114_SHORT", CalculationMode.Ticks, StopTicks, false);
            }
            else if (State == State.DataLoaded)
            {
                emaFast = EMA(Closes[1], EmaFastPeriod);
                emaSlow = EMA(Closes[1], EmaSlowPeriod);
                WriteTelemetryHeader();
                LogTelemetry("STRATEGY_LOADED", Core.Globals.Now, "", "initialization");
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 1)
                return;

            if (CurrentBars[1] < Math.Max(Math.Max(StructureLookbackTicks, RetestLookbackTicks), EmaSlowPeriod + 5))
                return;

            if (CurrentBars[2] < OpeningImpulseMinutes + 2)
                return;

            DateTime now = Times[1][0];
            ResetSessionIfNeeded(now);

            double bid = GetCurrentBidSafe();
            double ask = GetCurrentAskSafe();
            double spreadTicks = GetSpreadTicks(bid, ask);
            string phase = GetSessionPhase(now);

            UpdateOpeningAndBias(now);
            UpdateRetestState(now);
            MaybePrintStatus(now, phase, spreadTicks);

            if (Position.Quantity > HardQuantity)
            {
                LogTelemetry("EMERGENCY_FLATTEN_QTY_GT_1", now, phase, "position_qty_gt_1");
                ExitLong("CGV114_EmergencyQty_Long", "CGV114_LONG");
                ExitShort("CGV114_EmergencyQty_Short", "CGV114_SHORT");
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
                        ExitLong("CGV114_DailyLock_Long", "CGV114_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CGV114_DailyLock_Short", "CGV114_SHORT");
                    return;
                }
            }

            if (Position.MarketPosition != MarketPosition.Flat && spreadTicks > MaxRuntimeSpreadTicks)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("CGV114_RuntimeSpread_Long", "CGV114_LONG");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("CGV114_RuntimeSpread_Short", "CGV114_SHORT");

                LogTelemetry("RUNTIME_SPREAD_EXIT", now, phase, "spread_gt_runtime_max");
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat && activeEntryTime != Core.Globals.MinDate)
            {
                double heldSeconds = (now - activeEntryTime).TotalSeconds;
                if (heldSeconds >= MaxHoldSeconds)
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong("CGV114_TimeExit_Long", "CGV114_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CGV114_TimeExit_Short", "CGV114_SHORT");

                    LogTelemetry("TIME_EXIT_SUBMITTED", now, phase, "max_hold_seconds");
                    return;
                }
            }

            if (!CanConsiderEntry(now, phase, spreadTicks))
                return;

            bool rawLong = IsLongAllowed(phase) && EvaluateLongContinuation();
            bool rawShort = IsShortAllowed(phase) && EvaluateShortContinuation();

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

        private bool CanConsiderEntry(DateTime now, string phase, double spreadTicks)
        {
            if (dailyLocked || now < stopStreakLockedUntil)
            {
                blockedSignal++;
                return false;
            }

            if (RequireRth && !IsRth(now))
            {
                blockedNotRth++;
                return false;
            }

            if (MinutesSinceRthOpen(now) < NoTradeFirstMinutes)
            {
                blockedStructure++;
                return false;
            }

            if (!openingRangeComplete || rthBias == 0)
            {
                blockedStructure++;
                return false;
            }

            if (sessionTradeCount >= MaxTradesPerDay)
            {
                blockedSession++;
                return false;
            }

            if (Position.MarketPosition != MarketPosition.Flat || activeEntryOrder != null)
            {
                blockedNotFlat++;
                return false;
            }

            if (lastExitTime != Core.Globals.MinDate && (now - lastExitTime).TotalSeconds < CooldownSecondsAfterExit)
            {
                blockedCooldown++;
                return false;
            }

            if (lastEntryTime != Core.Globals.MinDate && (now - lastEntryTime).TotalSeconds < MinSecondsBetweenEntries)
            {
                blockedCooldown++;
                return false;
            }

            if (spreadTicks > MaxEntrySpreadTicks)
            {
                blockedSpread++;
                return false;
            }

            if (GetPhaseTradeCount(phase) >= MaxTradesPerSessionPhase)
            {
                blockedSession++;
                return false;
            }

            return true;
        }

        private void UpdateOpeningAndBias(DateTime now)
        {
            if (!IsRth(now))
                return;

            double current = Closes[1][0];
            int minutes = MinutesSinceRthOpen(now);

            if (minutes < OpeningImpulseMinutes)
            {
                openingHigh = Math.Max(openingHigh, Highs[1][0]);
                openingLow = Math.Min(openingLow, Lows[1][0]);
                openingRangeComplete = false;
                rthBias = 0;
                rthBiasReason = "BUILDING_OPENING_IMPULSE";
                return;
            }

            if (!openingRangeComplete)
            {
                openingRangeComplete = true;
                LogTelemetry("OPENING_IMPULSE_COMPLETE", now, GetSessionPhase(now),
                    string.Format(CultureInfo.InvariantCulture, "openingHigh={0};openingLow={1}", openingHigh, openingLow));
            }

            if (rthBias == 0)
            {
                if (current >= openingHigh + BiasBreakTicks * MnqTickSize)
                {
                    rthBias = 1;
                    rthBiasReason = "BOS_ABOVE_OPENING_IMPULSE";
                    LogTelemetry("BIAS_LONG", now, GetSessionPhase(now), rthBiasReason);
                }
                else if (current <= openingLow - BiasBreakTicks * MnqTickSize)
                {
                    rthBias = -1;
                    rthBiasReason = "BOS_BELOW_OPENING_IMPULSE";
                    LogTelemetry("BIAS_SHORT", now, GetSessionPhase(now), rthBiasReason);
                }
            }
            else if (rthBias == 1 && current <= openingLow - BiasBreakTicks * MnqTickSize)
            {
                rthBias = -1;
                rthBiasReason = "CHOCH_TO_SHORT_BELOW_OPENING_IMPULSE";
                longRetestArmed = false;
                shortRetestArmed = false;
                LogTelemetry("BIAS_FLIP_SHORT", now, GetSessionPhase(now), rthBiasReason);
            }
            else if (rthBias == -1 && current >= openingHigh + BiasBreakTicks * MnqTickSize)
            {
                rthBias = 1;
                rthBiasReason = "CHOCH_TO_LONG_ABOVE_OPENING_IMPULSE";
                longRetestArmed = false;
                shortRetestArmed = false;
                LogTelemetry("BIAS_FLIP_LONG", now, GetSessionPhase(now), rthBiasReason);
            }
        }

        private void UpdateRetestState(DateTime now)
        {
            if (!openingRangeComplete || rthBias == 0)
                return;

            double current = Closes[1][0];
            double fast = SafeEmaFast();
            double slow = SafeEmaSlow();
            double recentHigh = HighestHigh(RetestLookbackTicks);
            double recentLow = LowestLow(RetestLookbackTicks);
            double distToFastTicks = Math.Abs(current - fast) / MnqTickSize;

            if (rthBias == 1)
            {
                bool trendOk = fast > slow && ((fast - slow) / MnqTickSize) >= MinTrendSeparationTicks;
                bool retestOk = trendOk && current >= slow && distToFastTicks <= RetestDistanceTicks;
                if (retestOk && !longRetestArmed)
                {
                    longRetestArmed = true;
                    longContinuationTrigger = recentHigh + ContinuationBreakTicks * MnqTickSize;
                    LogTelemetry("LONG_RETEST_ARMED", now, GetSessionPhase(now),
                        string.Format(CultureInfo.InvariantCulture, "trigger={0};fast={1};slow={2}", longContinuationTrigger, fast, slow));
                }
            }
            else if (rthBias == -1)
            {
                bool trendOk = fast < slow && ((slow - fast) / MnqTickSize) >= MinTrendSeparationTicks;
                bool retestOk = trendOk && current <= slow && distToFastTicks <= RetestDistanceTicks;
                if (retestOk && !shortRetestArmed)
                {
                    shortRetestArmed = true;
                    shortContinuationTrigger = recentLow - ContinuationBreakTicks * MnqTickSize;
                    LogTelemetry("SHORT_RETEST_ARMED", now, GetSessionPhase(now),
                        string.Format(CultureInfo.InvariantCulture, "trigger={0};fast={1};slow={2}", shortContinuationTrigger, fast, slow));
                }
            }
        }

        private bool EvaluateLongContinuation()
        {
            if (rthBias != 1 || !longRetestArmed)
            {
                blockedStructure++;
                return false;
            }

            double current = Closes[1][0];
            double momentum = GetMinuteMomentumTicks();
            double fast = SafeEmaFast();
            double slow = SafeEmaSlow();

            bool trendOk = fast > slow && ((fast - slow) / MnqTickSize) >= MinTrendSeparationTicks;
            bool continuationBreak = current >= longContinuationTrigger;
            bool momentumOk = momentum >= MinMomentumTicks;

            if (trendOk && continuationBreak && momentumOk)
            {
                longRetestArmed = false;
                return true;
            }

            return false;
        }

        private bool EvaluateShortContinuation()
        {
            if (rthBias != -1 || !shortRetestArmed)
            {
                blockedStructure++;
                return false;
            }

            double current = Closes[1][0];
            double momentum = GetMinuteMomentumTicks();
            double fast = SafeEmaFast();
            double slow = SafeEmaSlow();

            bool trendOk = fast < slow && ((slow - fast) / MnqTickSize) >= MinTrendSeparationTicks;
            bool continuationBreak = current <= shortContinuationTrigger;
            bool momentumOk = momentum <= -MinMomentumTicks;

            if (trendOk && continuationBreak && momentumOk)
            {
                shortRetestArmed = false;
                return true;
            }

            return false;
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
                activeEntryName = "CGV114_LONG";
                activeEntryTime = now;
                EnterLong(HardQuantity, activeEntryName);
                LogTelemetry("ENTRY_SUBMITTED_LONG", now, phase, "structure_continuation_confirmed");
            }
            else
            {
                activeEntryName = "CGV114_SHORT";
                activeEntryTime = now;
                EnterShort(HardQuantity, activeEntryName);
                LogTelemetry("ENTRY_SUBMITTED_SHORT", now, phase, "structure_continuation_confirmed");
            }
        }

        private double HighestHigh(int lookback)
        {
            double h = Highs[1][0];
            int max = Math.Min(lookback, CurrentBars[1] - 1);
            for (int i = 1; i <= max; i++)
                h = Math.Max(h, Highs[1][i]);
            return h;
        }

        private double LowestLow(int lookback)
        {
            double l = Lows[1][0];
            int max = Math.Min(lookback, CurrentBars[1] - 1);
            for (int i = 1; i <= max; i++)
                l = Math.Min(l, Lows[1][i]);
            return l;
        }

        private double GetMinuteMomentumTicks()
        {
            try
            {
                if (CurrentBars == null || CurrentBars.Length <= 2 || CurrentBars[2] < 4)
                    return 0.0;

                int lookback = Math.Min(3, CurrentBars[2] - 1);
                return (Closes[2][0] - Closes[2][lookback]) / MnqTickSize;
            }
            catch { return 0.0; }
        }

        private double SafeEmaFast()
        {
            try
            {
                if (emaFast != null && CurrentBars[1] > EmaFastPeriod + 2)
                    return emaFast[0];
            }
            catch { }
            return SafeClose();
        }

        private double SafeEmaSlow()
        {
            try
            {
                if (emaSlow != null && CurrentBars[1] > EmaSlowPeriod + 2)
                    return emaSlow[0];
            }
            catch { }
            return SafeClose();
        }

        private double SafeClose()
        {
            try
            {
                if (CurrentBars != null && CurrentBars.Length > 1 && CurrentBars[1] >= 0)
                    return Closes[1][0];
            }
            catch { }
            return 0.0;
        }

        private bool IsRth(DateTime t)
        {
            int hms = t.Hour * 10000 + t.Minute * 100 + t.Second;
            return hms >= 93000 && hms < 160000;
        }

        private int MinutesSinceRthOpen(DateTime t)
        {
            DateTime open = new DateTime(t.Year, t.Month, t.Day, 9, 30, 0);
            return (int)Math.Floor((t - open).TotalMinutes);
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
            sessionTradeCount = openTradeCount = morningTradeCount = middayTradeCount = afternoonTradeCount = powerTradeCount = 0;
            consecutiveStopLosses = 0;
            dailyLocked = false;
            stopStreakLockedUntil = Core.Globals.MinDate;
            activeEntryTime = Core.Globals.MinDate;
            activeEntryOrder = null;
            activeEntryName = string.Empty;

            openingHigh = double.MinValue;
            openingLow = double.MaxValue;
            openingRangeComplete = false;
            rthBias = 0;
            rthBiasReason = "NONE";
            longRetestArmed = shortRetestArmed = false;
            longContinuationTrigger = double.MinValue;
            shortContinuationTrigger = double.MaxValue;
            longConfirmTicks = shortConfirmTicks = 0;
            ResetBlockCounters();
            LogTelemetry("SESSION_RESET", now, "", "new_session");
        }

        private void ResetBlockCounters()
        {
            blockedNotRth = blockedSpread = blockedNotFlat = blockedCooldown = blockedSession = blockedSignal = blockedStructure = entriesSubmitted = 0;
        }

        private double GetCurrentBidSafe()
        {
            try
            {
                double bid = GetCurrentBid();
                if (!double.IsNaN(bid) && bid > 0) return bid;
            }
            catch { }
            return SafeClose();
        }

        private double GetCurrentAskSafe()
        {
            try
            {
                double ask = GetCurrentAsk();
                if (!double.IsNaN(ask) && ask > 0) return ask;
            }
            catch { }
            return SafeClose();
        }

        private double GetSpreadTicks(double bid, double ask)
        {
            if (ask <= 0 || bid <= 0 || ask < bid) return 999.0;
            return (ask - bid) / MnqTickSize;
        }

        private void MaybePrintStatus(DateTime now, string phase, double spreadTicks)
        {
            if (!PrintDiagnostics) return;
            if (lastStatusPrintTime != Core.Globals.MinDate && (now - lastStatusPrintTime).TotalSeconds < StatusPrintSeconds) return;
            lastStatusPrintTime = now;

            string msg = string.Format(CultureInfo.InvariantCulture,
                "CGV114 STATUS {0} phase={1} pos={2} trades={3} spread={4:F1} mom={5:F1} bias={6}/{7} armedL={8} armedS={9} Lconf={10} Sconf={11} blocks[rth={12},spr={13},flat={14},cool={15},sess={16},struct={17},sig={18}] entries={19}",
                now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), phase, Position.MarketPosition, sessionTradeCount,
                spreadTicks, GetMinuteMomentumTicks(), rthBias, rthBiasReason, longRetestArmed ? 1 : 0, shortRetestArmed ? 1 : 0,
                longConfirmTicks, shortConfirmTicks, blockedNotRth, blockedSpread, blockedNotFlat, blockedCooldown,
                blockedSession, blockedStructure, blockedSignal, entriesSubmitted);

            Print(msg);
            LogTelemetry("STATUS", now, phase, msg);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null) return;
            if (order.Name == "CGV114_LONG" || order.Name == "CGV114_SHORT")
            {
                activeEntryOrder = order;
                if (orderState == OrderState.Filled)
                {
                    activeEntryTime = time;
                    LogTelemetry("ENTRY_FILLED", time, order.Name, string.Format(CultureInfo.InvariantCulture, "avgFill={0};qty={1}", averageFillPrice, filled));
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

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null) return;
            string orderName = execution.Order.Name ?? string.Empty;
            LogTelemetry("EXECUTION", time, orderName, string.Format(CultureInfo.InvariantCulture, "price={0};qty={1};mp={2};orderId={3}", price, quantity, marketPosition, orderId));

            bool looksLikeStop = orderName.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0 || orderName.IndexOf("loss", StringComparison.OrdinalIgnoreCase) >= 0;
            bool looksLikeTarget = orderName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0 || orderName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            if (marketPosition == MarketPosition.Flat || Position.MarketPosition == MarketPosition.Flat)
            {
                lastExitTime = time;
                if (looksLikeStop)
                {
                    consecutiveStopLosses++;
                    if (consecutiveStopLosses >= MaxConsecutiveStopLosses)
                    {
                        stopStreakLockedUntil = time.AddMinutes(StopStreakLockoutMinutes);
                        LogTelemetry("STOP_STREAK_LOCKOUT", time, orderName, string.Format(CultureInfo.InvariantCulture, "consecutiveStops={0};lockedUntil={1}", consecutiveStopLosses, stopStreakLockedUntil.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                    }
                }
                else if (looksLikeTarget)
                {
                    consecutiveStopLosses = 0;
                }
                activeEntryOrder = null;
                activeEntryName = string.Empty;
                activeEntryTime = Core.Globals.MinDate;
                longConfirmTicks = shortConfirmTicks = 0;
            }
        }

        private void WriteTelemetryHeader()
        {
            if (!WriteTelemetry || telemetryHeaderWritten) return;
            try
            {
                string dir = Path.GetDirectoryName(TelemetryCsvPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(TelemetryCsvPath))
                {
                    File.AppendAllText(TelemetryCsvPath,
                        "timestamp,event,phase_or_order,position,bid,ask,spread_ticks,momentum_ticks,bias,bias_reason,long_armed,short_armed,session_trades,open_trades,morning_trades,midday_trades,afternoon_trades,power_trades,consecutive_stops,daily_locked,locked_until,active_entry_time,last_exit_time,note" + Environment.NewLine);
                }
                telemetryHeaderWritten = true;
            }
            catch (Exception ex) { Print("Telemetry header write failed: " + ex.Message); }
        }

        private void LogTelemetry(string eventName, DateTime timestamp, string phaseOrOrder, string note)
        {
            if (!WriteTelemetry) return;
            try
            {
                WriteTelemetryHeader();
                double bid = GetCurrentBidSafe();
                double ask = GetCurrentAskSafe();
                double spreadTicks = GetSpreadTicks(bid, ask);
                string pos = "UNKNOWN";
                try { pos = Position == null ? "UNKNOWN" : Position.MarketPosition.ToString(); } catch { }

                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7:F2},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19},{20},{21},{22},{23}",
                    timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), EscapeCsv(eventName), EscapeCsv(phaseOrOrder ?? string.Empty), EscapeCsv(pos),
                    bid, ask, spreadTicks, GetMinuteMomentumTicks(), rthBias, EscapeCsv(rthBiasReason ?? string.Empty), longRetestArmed ? 1 : 0, shortRetestArmed ? 1 : 0,
                    sessionTradeCount, openTradeCount, morningTradeCount, middayTradeCount, afternoonTradeCount, powerTradeCount,
                    consecutiveStopLosses, dailyLocked ? 1 : 0,
                    stopStreakLockedUntil == Core.Globals.MinDate ? "" : stopStreakLockedUntil.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    activeEntryTime == Core.Globals.MinDate ? "" : activeEntryTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    lastExitTime == Core.Globals.MinDate ? "" : lastExitTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), EscapeCsv(note ?? string.Empty));
                File.AppendAllText(TelemetryCsvPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                try { Print("Telemetry write failed: " + ex.Message); } catch { }
            }
        }

        private static string EscapeCsv(string s)
        {
            if (s == null) return string.Empty;
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r")) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
