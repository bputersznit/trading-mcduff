// CG_MNQ_DeployShell_v1_16.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-09 12:20:00 America/New_York
//
// PURPOSE
//   Fixed rolling BOS/CHOCH + premarket-context forward-test shell for MNQ.
//
// WHY v1.16 EXISTS
//   v1.14 proved that opening-impulse logic can detect bias changes, but it was
//   too anchored to the first five RTH minutes and failed to trade obvious later
//   rolling structure breaks. v1.16 changes the signal brain:
//
//     - Premarket is observed and summarized.
//     - RTH is traded fully, but not blindly.
//     - Rolling swing high/low structure drives BOS/CHOCH.
//     - Premarket high/low/midpoint act as context and filters.
//     - Entries are continuation after BOS/CHOCH plus either retest or impulse.
//     - Execution shell remains one-MNQ/no-overlap/OCO-protected.
//
// IMPORTANT
//   This is still a forward-test approximation. It is closer to the desired
//   structural behavior than v1.13/v1.14, but it is not yet the final ClickHouse
//   structural signal engine.
//
// TRADING INTENT
//   Example behavior desired:
//     1. Watch premarket climb/selloff and record key structure.
//     2. At RTH, do not chase the first noisy ticks.
//     3. Detect rolling BOS down when price breaks a meaningful swing low.
//     4. If market pulls back/retests, short renewed continuation.
//     5. If market breaks hard with strong momentum, allow one impulse continuation.
//     6. Keep one MNQ only and protect immediately with stop/target.
//
// HARD GOVERNANCE
//   - 1 MNQ only.
//   - No overlap.
//   - No scaling.
//   - Managed target/stop brackets via SetProfitTarget / SetStopLoss.
//   - RTH entries only.
//   - Premarket used for context only.
//   - Entry spread gate.
//   - Runtime spread exit.
//   - Time exit.
//   - Daily loss lock.
//   - Stop-streak lockout.
//   - Session/side caps.
//   - Telemetry-safe logging.
//
// DEFAULT FIRST TEST SETTINGS
//   TargetTicks = 80
//   StopTicks = 16
//   MaxHoldSeconds = 120
//   MaxEntrySpreadTicks = 2
//   MaxRuntimeSpreadTicks = 6
//
//   PremarketStartTime = 40000
//   RthStartTime = 93000
//   RthEndTime = 160000
//   NoTradeFirstMinutes = 3
//
//   SwingLookbackTicks = 900
//   BosBreakTicks = 6
//   ChochBreakTicks = 6
//   RetestDistanceTicks = 18
//   ContinuationBreakTicks = 3
//   MinMomentumTicks = 16
//   ImpulseMomentumTicks = 48
//   ConfirmTicksRequired = 2
//   RequireRetestForEntry = false
//
//   CooldownSecondsAfterExit = 180
//   MinSecondsBetweenEntries = 240
//   MaxTradesPerDay = 6
//   MaxTradesPerSessionPhase = 2
//   MaxConsecutiveStopLosses = 2
//   StopStreakLockoutMinutes = 30
//
// CHART / DATA
//   Apply to MNQ. A 1-tick or 1000-volume chart is acceptable.
//   The strategy internally adds:
//     - 1 tick series for signal/execution
//     - 1 minute series for momentum/context
//

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
    public class CG_MNQ_DeployShell_v1_16 : Strategy
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

        private double premarketHigh = double.MinValue;
        private double premarketLow = double.MaxValue;
        private double premarketMid = 0.0;
        private double premarketOpen = 0.0;
        private double premarketLast = 0.0;
        private bool premarketSeen = false;

        private int structureBias = 0; // +1 long, -1 short, 0 neutral
        private string structureReason = "NONE";
        private DateTime lastBosTime = Core.Globals.MinDate;
        private double lastBosPrice = 0.0;

        private bool longRetestArmed = false;
        private bool shortRetestArmed = false;
        private bool longImpulseArmed = false;
        private bool shortImpulseArmed = false;
        private DateTime longArmTime = Core.Globals.MinDate;
        private DateTime shortArmTime = Core.Globals.MinDate;
        private double longContinuationTrigger = double.MinValue;
        private double shortContinuationTrigger = double.MaxValue;

        private int longConfirmTicks = 0;
        private int shortConfirmTicks = 0;

        private long blockedNotRth = 0;
        private long blockedSpread = 0;
        private long blockedNotFlat = 0;
        private long blockedCooldown = 0;
        private long blockedSession = 0;
        private long blockedSignal = 0;
        private long blockedStructure = 0;
        private long entriesSubmitted = 0;

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "PremarketStartTime", GroupName = "Time", Order = 1)]
        public int PremarketStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RthStartTime", GroupName = "Time", Order = 2)]
        public int RthStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RthEndTime", GroupName = "Time", Order = 3)]
        public int RthEndTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 30)]
        [Display(Name = "NoTradeFirstMinutes", GroupName = "Time", Order = 4)]
        public int NoTradeFirstMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(50, 5000)]
        [Display(Name = "SwingLookbackTicks", GroupName = "Structure", Order = 10)]
        public int SwingLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BosBreakTicks", GroupName = "Structure", Order = 11)]
        public int BosBreakTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ChochBreakTicks", GroupName = "Structure", Order = 12)]
        public int ChochBreakTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "RetestDistanceTicks", GroupName = "Structure", Order = 13)]
        public int RetestDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "ContinuationBreakTicks", GroupName = "Structure", Order = 14)]
        public int ContinuationBreakTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MinMomentumTicks", GroupName = "Structure", Order = 15)]
        public int MinMomentumTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 300)]
        [Display(Name = "ImpulseMomentumTicks", GroupName = "Structure", Order = 16)]
        public int ImpulseMomentumTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "ConfirmTicksRequired", GroupName = "Structure", Order = 17)]
        public int ConfirmTicksRequired { get; set; }

        [NinjaScriptProperty]
        [Range(10, 1800)]
        [Display(Name = "ArmExpirationSeconds", GroupName = "Structure", Order = 18)]
        public int ArmExpirationSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireRetestForEntry", GroupName = "Structure", Order = 19)]
        public bool RequireRetestForEntry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UsePremarketContextFilter", GroupName = "Structure", Order = 19)]
        public bool UsePremarketContextFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1800)]
        [Display(Name = "CooldownSecondsAfterExit", GroupName = "Anti-Churn", Order = 30)]
        public int CooldownSecondsAfterExit { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1800)]
        [Display(Name = "MinSecondsBetweenEntries", GroupName = "Anti-Churn", Order = 31)]
        public int MinSecondsBetweenEntries { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "TargetTicks", GroupName = "Risk", Order = 40)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "StopTicks", GroupName = "Risk", Order = 41)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 900)]
        [Display(Name = "MaxHoldSeconds", GroupName = "Risk", Order = 42)]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxEntrySpreadTicks", GroupName = "Execution", Order = 50)]
        public int MaxEntrySpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxRuntimeSpreadTicks", GroupName = "Execution", Order = 51)]
        public int MaxRuntimeSpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireRth", GroupName = "Time", Order = 60)]
        public bool RequireRth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowOpenLong", GroupName = "Session Allowlist", Order = 70)]
        public bool AllowOpenLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowOpenShort", GroupName = "Session Allowlist", Order = 71)]
        public bool AllowOpenShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMorningLong", GroupName = "Session Allowlist", Order = 72)]
        public bool AllowMorningLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMorningShort", GroupName = "Session Allowlist", Order = 73)]
        public bool AllowMorningShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMiddayLong", GroupName = "Session Allowlist", Order = 74)]
        public bool AllowMiddayLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMiddayShort", GroupName = "Session Allowlist", Order = 75)]
        public bool AllowMiddayShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowAfternoonTrades", GroupName = "Session Allowlist", Order = 76)]
        public bool AllowAfternoonTrades { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowPowerShort", GroupName = "Session Allowlist", Order = 77)]
        public bool AllowPowerShort { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxTradesPerDay", GroupName = "Daily Governance", Order = 80)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxTradesPerSessionPhase", GroupName = "Daily Governance", Order = 81)]
        public int MaxTradesPerSessionPhase { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxConsecutiveStopLosses", GroupName = "Daily Governance", Order = 82)]
        public int MaxConsecutiveStopLosses { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name = "StopStreakLockoutMinutes", GroupName = "Daily Governance", Order = 83)]
        public int StopStreakLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "DailyLossLockUsd", GroupName = "Daily Governance", Order = 84)]
        public double DailyLossLockUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableDailyLossLock", GroupName = "Daily Governance", Order = 85)]
        public bool EnableDailyLossLock { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TelemetryCsvPath", GroupName = "Telemetry", Order = 90)]
        public string TelemetryCsvPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "WriteTelemetry", GroupName = "Telemetry", Order = 91)]
        public bool WriteTelemetry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", GroupName = "Telemetry", Order = 92)]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(5, 300)]
        [Display(Name = "StatusPrintSeconds", GroupName = "Telemetry", Order = 93)]
        public int StatusPrintSeconds { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_MNQ_DeployShell_v1_16";
                Description = "Rolling BOS/CHOCH + premarket-context MNQ forward shell. One MNQ, no overlap, OCO bracket, RTH entries only.";
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

                PremarketStartTime = 40000;
                RthStartTime = 93000;
                RthEndTime = 160000;
                NoTradeFirstMinutes = 3;

                SwingLookbackTicks = 300;
                BosBreakTicks = 4;
                ChochBreakTicks = 4;
                RetestDistanceTicks = 18;
                ContinuationBreakTicks = 2;
                MinMomentumTicks = 12;
                ImpulseMomentumTicks = 36;
                ConfirmTicksRequired = 1;
                ArmExpirationSeconds = 180;
                RequireRetestForEntry = false;
                UsePremarketContextFilter = true;

                CooldownSecondsAfterExit = 180;
                MinSecondsBetweenEntries = 240;

                TargetTicks = 80;
                StopTicks = 16;
                MaxHoldSeconds = 120;
                MaxEntrySpreadTicks = 3;
                MaxRuntimeSpreadTicks = 6;
                RequireRth = true;

                AllowOpenLong = true;
                AllowOpenShort = true;
                AllowMorningLong = true;
                AllowMorningShort = true;
                AllowMiddayLong = false;
                AllowMiddayShort = true;
                AllowAfternoonTrades = false;
                AllowPowerShort = true;

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
                TelemetryCsvPath = Path.Combine(defaultDir, "CG_MNQ_DeployShell_v1_16_telemetry.csv");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
                AddDataSeries(BarsPeriodType.Minute, 1);

                SetProfitTarget("CGV116_LONG", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CGV116_LONG", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("CGV116_SHORT", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CGV116_SHORT", CalculationMode.Ticks, StopTicks, false);
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

            if (CurrentBars[1] < Math.Max(SwingLookbackTicks + 5, 50) || CurrentBars[2] < 5)
                return;

            DateTime now = Times[1][0];
            ResetSessionIfNeeded(now);

            double bid = GetCurrentBidSafe();
            double ask = GetCurrentAskSafe();
            double spreadTicks = GetSpreadTicks(bid, ask);
            string phase = GetSessionPhase(now);

            UpdatePremarketContext(now);
            ExpireArms(now);
            UpdateRollingStructure(now);
            MaybePrintStatus(now, phase, spreadTicks);

            if (Position.Quantity > HardQuantity)
            {
                LogTelemetry("EMERGENCY_FLATTEN_QTY_GT_1", now, phase, "position_qty_gt_1");
                ExitLong("CGV116_EmergencyQty_Long", "CGV116_LONG");
                ExitShort("CGV116_EmergencyQty_Short", "CGV116_SHORT");
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
                        ExitLong("CGV116_DailyLock_Long", "CGV116_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CGV116_DailyLock_Short", "CGV116_SHORT");
                    return;
                }
            }

            if (Position.MarketPosition != MarketPosition.Flat && spreadTicks > MaxRuntimeSpreadTicks)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("CGV116_RuntimeSpread_Long", "CGV116_LONG");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("CGV116_RuntimeSpread_Short", "CGV116_SHORT");

                LogTelemetry("RUNTIME_SPREAD_EXIT", now, phase, "spread_gt_runtime_max");
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat && activeEntryTime != Core.Globals.MinDate)
            {
                double heldSeconds = (now - activeEntryTime).TotalSeconds;
                if (heldSeconds >= MaxHoldSeconds)
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong("CGV116_TimeExit_Long", "CGV116_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CGV116_TimeExit_Short", "CGV116_SHORT");

                    LogTelemetry("TIME_EXIT_SUBMITTED", now, phase, "max_hold_seconds");
                    return;
                }
            }

            if (!CanConsiderEntry(now, phase, spreadTicks))
                return;

            bool allowLong = IsLongAllowed(phase);
            bool allowShort = IsShortAllowed(phase);

            bool rawLong = allowLong && EvaluateLongEntry(now);
            bool rawShort = allowShort && EvaluateShortEntry(now);

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

        private void UpdatePremarketContext(DateTime now)
        {
            int hms = ToHms(now);
            if (hms < PremarketStartTime || hms >= RthStartTime)
                return;

            double c = Closes[1][0];

            if (!premarketSeen)
            {
                premarketOpen = c;
                premarketHigh = Highs[1][0];
                premarketLow = Lows[1][0];
                premarketSeen = true;
            }

            premarketHigh = Math.Max(premarketHigh, Highs[1][0]);
            premarketLow = Math.Min(premarketLow, Lows[1][0]);
            premarketLast = c;
            premarketMid = (premarketHigh + premarketLow) * 0.5;
        }

        private void UpdateRollingStructure(DateTime now)
        {
            if (!IsRth(now))
                return;

            double current = Closes[1][0];
            double recentHigh = HighestHigh(SwingLookbackTicks, 1);
            double recentLow = LowestLow(SwingLookbackTicks, 1);
            double momentum = GetMinuteMomentumTicks();

            bool longBos = current >= recentHigh + BosBreakTicks * MnqTickSize;
            bool shortBos = current <= recentLow - BosBreakTicks * MnqTickSize;

            if (longBos)
            {
                bool isChoch = structureBias < 0;
                structureBias = 1;
                structureReason = isChoch ? "CHOCH_TO_LONG_ROLLING_SWING_BREAK" : "LONG_BOS_ROLLING_SWING_BREAK";
                lastBosTime = now;
                lastBosPrice = current;

                ClearShortArms();

                longImpulseArmed = momentum >= ImpulseMomentumTicks;
                longRetestArmed = false;
                longArmTime = now;

                longContinuationTrigger = current + ContinuationBreakTicks * MnqTickSize;
                LogTelemetry(isChoch ? "CHOCH_LONG" : "BOS_LONG", now, GetSessionPhase(now),
                    string.Format(CultureInfo.InvariantCulture, "recentHigh={0};price={1};mom={2};longImpulse={3};trigger={4}", recentHigh, current, momentum, longImpulseArmed ? 1 : 0, longContinuationTrigger));
            }
            else if (shortBos)
            {
                bool isChoch = structureBias > 0;
                structureBias = -1;
                structureReason = isChoch ? "CHOCH_TO_SHORT_ROLLING_SWING_BREAK" : "SHORT_BOS_ROLLING_SWING_BREAK";
                lastBosTime = now;
                lastBosPrice = current;

                ClearLongArms();

                shortImpulseArmed = momentum <= -ImpulseMomentumTicks;
                shortRetestArmed = false;
                shortArmTime = now;

                shortContinuationTrigger = current - ContinuationBreakTicks * MnqTickSize;
                LogTelemetry(isChoch ? "CHOCH_SHORT" : "BOS_SHORT", now, GetSessionPhase(now),
                    string.Format(CultureInfo.InvariantCulture, "recentLow={0};price={1};mom={2};shortImpulse={3};trigger={4}", recentLow, current, momentum, shortImpulseArmed ? 1 : 0, shortContinuationTrigger));
            }

            UpdateRetestArms(now, current);
        }

        private void UpdateRetestArms(DateTime now, double current)
        {
            if (structureBias == 1)
            {
                // Retest means price comes back toward the BOS price without fully invalidating.
                bool nearBos = Math.Abs(current - lastBosPrice) / MnqTickSize <= RetestDistanceTicks;
                bool aboveMid = !UsePremarketContextFilter || !premarketSeen || current >= premarketMid || current >= premarketHigh;
                if (nearBos && aboveMid)
                {
                    longRetestArmed = true;
                    longArmTime = now;
                    longContinuationTrigger = Math.Max(longContinuationTrigger, HighestHigh(120, 1) + ContinuationBreakTicks * MnqTickSize);
                    LogTelemetry("LONG_RETEST_ARMED", now, GetSessionPhase(now),
                        string.Format(CultureInfo.InvariantCulture, "price={0};bosPrice={1};trigger={2}", current, lastBosPrice, longContinuationTrigger));
                }
            }
            else if (structureBias == -1)
            {
                bool nearBos = Math.Abs(current - lastBosPrice) / MnqTickSize <= RetestDistanceTicks;
                bool belowMid = !UsePremarketContextFilter || !premarketSeen || current <= premarketMid || current <= premarketLow;
                if (nearBos && belowMid)
                {
                    shortRetestArmed = true;
                    shortArmTime = now;
                    shortContinuationTrigger = Math.Min(shortContinuationTrigger, LowestLow(120, 1) - ContinuationBreakTicks * MnqTickSize);
                    LogTelemetry("SHORT_RETEST_ARMED", now, GetSessionPhase(now),
                        string.Format(CultureInfo.InvariantCulture, "price={0};bosPrice={1};trigger={2}", current, lastBosPrice, shortContinuationTrigger));
                }
            }
        }

        private bool EvaluateLongEntry(DateTime now)
        {
            if (structureBias != 1)
            {
                blockedStructure++;
                return false;
            }

            double current = Closes[1][0];
            double momentum = GetMinuteMomentumTicks();

            bool contextOk = !UsePremarketContextFilter || !premarketSeen || current >= premarketMid || current >= premarketHigh;
            bool impulseEntry = !RequireRetestForEntry && longImpulseArmed && momentum >= MinMomentumTicks && current >= longContinuationTrigger;
            bool retestEntry = longRetestArmed && momentum >= MinMomentumTicks && current >= longContinuationTrigger;

            bool pass = contextOk && (impulseEntry || retestEntry);

            if (pass)
            {
                LogTelemetry("LONG_ENTRY_RAW_TRUE", now, GetSessionPhase(now),
                    string.Format(CultureInfo.InvariantCulture, "price={0};mom={1};trigger={2};impulse={3};retest={4}",
                        current, momentum, longContinuationTrigger, impulseEntry ? 1 : 0, retestEntry ? 1 : 0));
            }

            return pass;
        }

        private bool EvaluateShortEntry(DateTime now)
        {
            if (structureBias != -1)
            {
                blockedStructure++;
                return false;
            }

            double current = Closes[1][0];
            double momentum = GetMinuteMomentumTicks();

            bool contextOk = !UsePremarketContextFilter || !premarketSeen || current <= premarketMid || current <= premarketLow;
            bool impulseEntry = !RequireRetestForEntry && shortImpulseArmed && momentum <= -MinMomentumTicks && current <= shortContinuationTrigger;
            bool retestEntry = shortRetestArmed && momentum <= -MinMomentumTicks && current <= shortContinuationTrigger;

            bool pass = contextOk && (impulseEntry || retestEntry);

            if (pass)
            {
                LogTelemetry("SHORT_ENTRY_RAW_TRUE", now, GetSessionPhase(now),
                    string.Format(CultureInfo.InvariantCulture, "price={0};mom={1};trigger={2};impulse={3};retest={4}",
                        current, momentum, shortContinuationTrigger, impulseEntry ? 1 : 0, retestEntry ? 1 : 0));
            }

            return pass;
        }

        private bool CanConsiderEntry(DateTime now, string phase, double spreadTicks)
        {
            if (dailyLocked)
            {
                blockedSignal++;
                return false;
            }

            if (now < stopStreakLockedUntil)
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

            if (structureBias == 0)
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
                activeEntryTime = now;
                EnterLong(HardQuantity, "CGV116_LONG");
                LogTelemetry("ENTRY_SUBMITTED_LONG", now, phase, "rolling_bos_choch_context");
                ClearLongArms();
            }
            else
            {
                activeEntryTime = now;
                EnterShort(HardQuantity, "CGV116_SHORT");
                LogTelemetry("ENTRY_SUBMITTED_SHORT", now, phase, "rolling_bos_choch_context");
                ClearShortArms();
            }
        }

        private void ExpireArms(DateTime now)
        {
            if ((longImpulseArmed || longRetestArmed) && longArmTime != Core.Globals.MinDate)
            {
                if ((now - longArmTime).TotalSeconds > ArmExpirationSeconds)
                {
                    LogTelemetry("LONG_ARM_EXPIRED", now, GetSessionPhase(now), "arm_timeout");
                    ClearLongArms();
                }
            }

            if ((shortImpulseArmed || shortRetestArmed) && shortArmTime != Core.Globals.MinDate)
            {
                if ((now - shortArmTime).TotalSeconds > ArmExpirationSeconds)
                {
                    LogTelemetry("SHORT_ARM_EXPIRED", now, GetSessionPhase(now), "arm_timeout");
                    ClearShortArms();
                }
            }
        }

        private void ClearLongArms()
        {
            longRetestArmed = false;
            longImpulseArmed = false;
            longArmTime = Core.Globals.MinDate;
            longContinuationTrigger = double.MinValue;
            longConfirmTicks = 0;
        }

        private void ClearShortArms()
        {
            shortRetestArmed = false;
            shortImpulseArmed = false;
            shortArmTime = Core.Globals.MinDate;
            shortContinuationTrigger = double.MaxValue;
            shortConfirmTicks = 0;
        }

        private double HighestHigh(int lookback, int startAgo)
        {
            double h = Highs[1][startAgo];
            int max = Math.Min(lookback + startAgo, CurrentBars[1] - 1);
            for (int i = startAgo; i <= max; i++)
                h = Math.Max(h, Highs[1][i]);
            return h;
        }

        private double LowestLow(int lookback, int startAgo)
        {
            double l = Lows[1][startAgo];
            int max = Math.Min(lookback + startAgo, CurrentBars[1] - 1);
            for (int i = startAgo; i <= max; i++)
                l = Math.Min(l, Lows[1][i]);
            return l;
        }

        private double GetMinuteMomentumTicks()
        {
            try
            {
                if (CurrentBars[2] < 4)
                    return 0.0;

                int lookback = Math.Min(3, CurrentBars[2] - 1);
                return (Closes[2][0] - Closes[2][lookback]) / MnqTickSize;
            }
            catch { return 0.0; }
        }

        private bool IsRth(DateTime t)
        {
            int hms = ToHms(t);
            return hms >= RthStartTime && hms < RthEndTime;
        }

        private int ToHms(DateTime t)
        {
            return t.Hour * 10000 + t.Minute * 100 + t.Second;
        }

        private int MinutesSinceRthOpen(DateTime t)
        {
            DateTime open = HmsToDateTime(t, RthStartTime);
            return (int)Math.Floor((t - open).TotalMinutes);
        }

        private DateTime HmsToDateTime(DateTime baseDate, int hms)
        {
            int h = hms / 10000;
            int m = (hms / 100) % 100;
            int s = hms % 100;
            return new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, h, m, s);
        }

        private string GetSessionPhase(DateTime t)
        {
            int hms = ToHms(t);

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
            if (phase == "OPEN_0930_1000") return AllowOpenLong;
            if (phase == "MORNING_1000_1130") return AllowMorningLong;
            if (phase == "MIDDAY_1130_1330") return AllowMiddayLong;
            if (phase == "AFTERNOON_1330_1500") return AllowAfternoonTrades;
            if (phase == "POWER_1500_1600") return false;
            return false;
        }

        private bool IsShortAllowed(string phase)
        {
            if (phase == "OPEN_0930_1000") return AllowOpenShort;
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

            premarketHigh = double.MinValue;
            premarketLow = double.MaxValue;
            premarketMid = 0.0;
            premarketOpen = 0.0;
            premarketLast = 0.0;
            premarketSeen = false;

            structureBias = 0;
            structureReason = "NONE";
            lastBosTime = Core.Globals.MinDate;
            lastBosPrice = 0.0;

            ClearLongArms();
            ClearShortArms();

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
            blockedStructure = 0;
            entriesSubmitted = 0;
        }

        private double GetCurrentBidSafe()
        {
            try
            {
                double bid = GetCurrentBid();
                if (!double.IsNaN(bid) && bid > 0)
                    return bid;
            }
            catch { }

            try { return Closes[1][0]; } catch { return 0.0; }
        }

        private double GetCurrentAskSafe()
        {
            try
            {
                double ask = GetCurrentAsk();
                if (!double.IsNaN(ask) && ask > 0)
                    return ask;
            }
            catch { }

            try { return Closes[1][0]; } catch { return 0.0; }
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
                "CGV116 STATUS {0} phase={1} pos={2} trades={3} spread={4:F1} mom={5:F1} bias={6}/{7} pmSeen={8} pmH={9:F2} pmL={10:F2} pmMid={11:F2} armedL={12}/{13} armedS={14}/{15} Lconf={16} Sconf={17} trigL={18:F2} trigS={19:F2} blocks[rth={20},spr={21},flat={22},cool={23},sess={24},struct={25},sig={26}] entries={27}",
                now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                phase,
                Position.MarketPosition,
                sessionTradeCount,
                spreadTicks,
                GetMinuteMomentumTicks(),
                structureBias,
                structureReason,
                premarketSeen ? 1 : 0,
                premarketSeen ? premarketHigh : 0.0,
                premarketSeen ? premarketLow : 0.0,
                premarketSeen ? premarketMid : 0.0,
                longRetestArmed ? 1 : 0,
                longImpulseArmed ? 1 : 0,
                shortRetestArmed ? 1 : 0,
                shortImpulseArmed ? 1 : 0,
                longConfirmTicks,
                shortConfirmTicks,
                longContinuationTrigger == double.MinValue ? 0.0 : longContinuationTrigger,
                shortContinuationTrigger == double.MaxValue ? 0.0 : shortContinuationTrigger,
                blockedNotRth,
                blockedSpread,
                blockedNotFlat,
                blockedCooldown,
                blockedSession,
                blockedStructure,
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

            if (order.Name == "CGV116_LONG" || order.Name == "CGV116_SHORT")
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
                        "timestamp,event,phase_or_order,position,bid,ask,spread_ticks,momentum_ticks,bias,bias_reason,premarket_seen,premarket_high,premarket_low,premarket_mid,long_retest,long_impulse,short_retest,short_impulse,session_trades,consecutive_stops,daily_locked,locked_until,active_entry_time,last_exit_time,note" + Environment.NewLine);
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
                string pos = "UNKNOWN";
                try { pos = Position == null ? "UNKNOWN" : Position.MarketPosition.ToString(); } catch { }

                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7:F2},{8},{9},{10},{11:F2},{12:F2},{13:F2},{14},{15},{16},{17},{18},{19},{20},{21},{22},{23},{24}",
                    timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    EscapeCsv(eventName),
                    EscapeCsv(phaseOrOrder ?? string.Empty),
                    EscapeCsv(pos),
                    bid,
                    ask,
                    spreadTicks,
                    GetMinuteMomentumTicks(),
                    structureBias,
                    EscapeCsv(structureReason ?? string.Empty),
                    premarketSeen ? 1 : 0,
                    premarketSeen ? premarketHigh : 0.0,
                    premarketSeen ? premarketLow : 0.0,
                    premarketSeen ? premarketMid : 0.0,
                    longRetestArmed ? 1 : 0,
                    longImpulseArmed ? 1 : 0,
                    shortRetestArmed ? 1 : 0,
                    shortImpulseArmed ? 1 : 0,
                    sessionTradeCount,
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
                try { Print("Telemetry write failed: " + ex.Message); } catch { }
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
