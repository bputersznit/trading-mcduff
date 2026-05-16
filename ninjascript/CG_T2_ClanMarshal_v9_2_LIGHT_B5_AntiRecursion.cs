// CG_T2_ClanMarshal_v9_2_LIGHT_B5_AntiRecursion.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-06 14:34:00 America/New_York
//
// MNQ Intraday Strategy — v9.2 LIGHT B5 Anti-Recursion
//
// PURPOSE
//   B4 proved the tick engine is alive and can submit/exit trades on an MNQ 1-tick chart.
//   However, the trade log showed rapid same-second re-entry after profit targets.
//   B5 fixes that with a hard execution-governance layer.
//
// CORE FIXES VS B4
//   1. OnBarUpdate remains the primary single-series 1-tick engine.
//   2. Adds one-entry-per-bar lock.
//   3. Adds minimum milliseconds between entries.
//   4. Adds post-exit tick lockout counter.
//   5. Adds pending-entry lock so the strategy cannot spam entries before fill feedback.
//   6. Commits cooldown on ALL observed exits, including profit targets and stop losses.
//   7. Blocks re-entry on the same bar/tick after an exit.
//   8. Keeps momentum-cascade suppression.
//   9. Keeps diagnostics.
//
// REQUIRED CHART SETUP
//   Instrument: MNQ front contract
//   Bars type:  Tick
//   Value:      1
//
// DESIGN IDENTITY
//   Lightweight microstructure fade engine:
//     LONG  = downward impulse -> reclaim upward -> buy micro-reversion
//     SHORT = upward impulse   -> reject downward -> sell micro-reversion
//
// IMPORTANT
//   Time parameters are chart-time HHMMSS.
//   If chart time is Central, use CT values.
//   If chart time is Eastern, use ET values.
//
// INSTALL
//   Documents\NinjaTrader 8\bin\Custom\Strategies\CG_T2_ClanMarshal_v9_2_LIGHT_B5_AntiRecursion.cs
//
// DISCLAIMER
//   Research strategy. Validate in Playback/Sim before live use.

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_T2_ClanMarshal_v9_2_LIGHT_B5_AntiRecursion : Strategy
    {
        // --------------------------------------------------------------------
        // Rolling tick-state
        // --------------------------------------------------------------------

        private double[] recentPrices;
        private int recentIndex;
        private int recentCount;

        // --------------------------------------------------------------------
        // Session / trade state
        // --------------------------------------------------------------------

        private DateTime currentSessionDate = Core.Globals.MinDate;

        private DateTime entryTime = Core.Globals.MinDate;
        private DateTime lastEntryTime = Core.Globals.MinDate;
        private DateTime lastExitTime = Core.Globals.MinDate;

        private int lastEntryBar = -1;
        private int lastExitBar = -1;

        private bool pendingEntry = false;
        private int pendingEntryBar = -1;
        private DateTime pendingEntryTime = Core.Globals.MinDate;

        private int postExitTickLockRemaining = 0;
        private bool exitObservedThisBar = false;

        private DateTime longLockoutUntil = Core.Globals.MinDate;
        private DateTime shortLockoutUntil = Core.Globals.MinDate;
        private DateTime longCascadeSuppressUntil = Core.Globals.MinDate;
        private DateTime shortCascadeSuppressUntil = Core.Globals.MinDate;

        private DateTime lastLongEmergencyTime = Core.Globals.MinDate;
        private DateTime lastShortEmergencyTime = Core.Globals.MinDate;

        private double entryPrice = 0.0;
        private double highSinceEntry = 0.0;
        private double lowSinceEntry = 0.0;
        private double lastLongEmergencyLow = 0.0;
        private double lastShortEmergencyHigh = 0.0;

        private int tradesToday = 0;
        private int emergencyExitsToday = 0;
        private int longEmergencyCountWindow = 0;
        private int shortEmergencyCountWindow = 0;

        // --------------------------------------------------------------------
        // Diagnostics
        // --------------------------------------------------------------------

        private long ticksSeenToday = 0;
        private long longSetupsSeenToday = 0;
        private long shortSetupsSeenToday = 0;
        private long sessionBlockedToday = 0;
        private long cooldownBlockedToday = 0;
        private long governanceBlockedToday = 0;
        private long antiRecursionBlockedToday = 0;
        private long pendingEntryBlockedToday = 0;
        private long longSuppressedToday = 0;
        private long shortSuppressedToday = 0;
        private long submittedLongsToday = 0;
        private long submittedShortsToday = 0;

        private int lastDiagBar = -1;

        private const string LongSignalName = "B5_Long";
        private const string ShortSignalName = "B5_Short";

        // --------------------------------------------------------------------
        // Parameters
        // --------------------------------------------------------------------

        [NinjaScriptProperty]
        [Display(Name = "UseSessionFilter", GroupName = "01 Session", Order = 1)]
        public bool UseSessionFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EntryStartTime", GroupName = "01 Session", Order = 2)]
        public int EntryStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EntryEndTime", GroupName = "01 Session", Order = 3)]
        public int EntryEndTime { get; set; }

        [NinjaScriptProperty]
        [Range(3, 200)]
        [Display(Name = "ImpulseLookbackTicks", GroupName = "02 Impulse", Order = 1)]
        public int ImpulseLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "LongMinImpulseTicks", GroupName = "03 Long", Order = 1)]
        public int LongMinImpulseTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "LongReclaimTicks", GroupName = "03 Long", Order = 2)]
        public int LongReclaimTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "LongMinTicksAfterLow", GroupName = "03 Long", Order = 3)]
        public int LongMinTicksAfterLow { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ShortMinImpulseTicks", GroupName = "04 Short", Order = 1)]
        public int ShortMinImpulseTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ShortRejectTicks", GroupName = "04 Short", Order = 2)]
        public int ShortRejectTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "ShortMinTicksAfterHigh", GroupName = "04 Short", Order = 3)]
        public int ShortMinTicksAfterHigh { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireTurnTick", GroupName = "05 Entry Quality", Order = 1)]
        public bool RequireTurnTick { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "ProfitTargetTicks", GroupName = "06 Exits", Order = 1)]
        public int ProfitTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "StopLossTicks", GroupName = "06 Exits", Order = 2)]
        public int StopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "EmergencyExitTicks", GroupName = "06 Exits", Order = 3)]
        public int EmergencyExitTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 600)]
        [Display(Name = "TimeStopSeconds", GroupName = "06 Exits", Order = 4)]
        public int TimeStopSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 300)]
        [Display(Name = "EarlyFailureSeconds", GroupName = "06 Exits", Order = 5)]
        public int EarlyFailureSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "EarlyFailureMinProgressTicks", GroupName = "06 Exits", Order = 6)]
        public int EarlyFailureMinProgressTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 600)]
        [Display(Name = "CooldownSeconds", GroupName = "07 Governance", Order = 1)]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxTradesPerDay", GroupName = "07 Governance", Order = 2)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxEmergencyExits", GroupName = "07 Governance", Order = 3)]
        public int MaxEmergencyExits { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "SideLockoutMinutes", GroupName = "07 Governance", Order = 4)]
        public int SideLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "PostExitTickLock", GroupName = "08 Anti Recursion", Order = 1)]
        public int PostExitTickLock { get; set; }

        [NinjaScriptProperty]
        [Range(0, 60000)]
        [Display(Name = "MinMillisecondsBetweenEntries", GroupName = "08 Anti Recursion", Order = 2)]
        public int MinMillisecondsBetweenEntries { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "PendingEntryMaxBars", GroupName = "08 Anti Recursion", Order = 3)]
        public int PendingEntryMaxBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BlockSameBarReentry", GroupName = "08 Anti Recursion", Order = 4)]
        public bool BlockSameBarReentry { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "CascadeEmergencyThreshold", GroupName = "09 Cascade Suppression", Order = 1)]
        public int CascadeEmergencyThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "CascadeWindowMinutes", GroupName = "09 Cascade Suppression", Order = 2)]
        public int CascadeWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "CascadeSuppressMinutes", GroupName = "09 Cascade Suppression", Order = 3)]
        public int CascadeSuppressMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "CascadeContinuationTicks", GroupName = "09 Cascade Suppression", Order = 4)]
        public int CascadeContinuationTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", GroupName = "10 Diagnostics", Order = 1)]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(50, 100000)]
        [Display(Name = "DiagnosticEveryTicks", GroupName = "10 Diagnostics", Order = 2)]
        public int DiagnosticEveryTicks { get; set; }

        // --------------------------------------------------------------------
        // NT lifecycle
        // --------------------------------------------------------------------

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_2_LIGHT_B5_AntiRecursion";
                Description = "v9.2 LIGHT B5 single-series MNQ tick fade engine with hard anti-recursion execution governance.";

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
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                TimeInForce = TimeInForce.Day;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;

                UseSessionFilter = true;
                EntryStartTime = 83000;
                EntryEndTime = 150000;

                ImpulseLookbackTicks = 16;

                LongMinImpulseTicks = 5;
                LongReclaimTicks = 3;
                LongMinTicksAfterLow = 2;

                ShortMinImpulseTicks = 5;
                ShortRejectTicks = 3;
                ShortMinTicksAfterHigh = 2;

                RequireTurnTick = true;

                ProfitTargetTicks = 8;
                StopLossTicks = 8;
                EmergencyExitTicks = 7;
                TimeStopSeconds = 45;
                EarlyFailureSeconds = 15;
                EarlyFailureMinProgressTicks = 2;

                CooldownSeconds = 45;
                MaxTradesPerDay = 20;
                MaxEmergencyExits = 8;
                SideLockoutMinutes = 3;

                PostExitTickLock = 8;
                MinMillisecondsBetweenEntries = 1500;
                PendingEntryMaxBars = 20;
                BlockSameBarReentry = true;

                CascadeEmergencyThreshold = 2;
                CascadeWindowMinutes = 12;
                CascadeSuppressMinutes = 8;
                CascadeContinuationTicks = 4;

                PrintDiagnostics = true;
                DiagnosticEveryTicks = 1000;
            }
            else if (State == State.Configure)
            {
                SetProfitTarget(LongSignalName, CalculationMode.Ticks, ProfitTargetTicks);
                SetStopLoss(LongSignalName, CalculationMode.Ticks, StopLossTicks, false);

                SetProfitTarget(ShortSignalName, CalculationMode.Ticks, ProfitTargetTicks);
                SetStopLoss(ShortSignalName, CalculationMode.Ticks, StopLossTicks, false);
            }
            else if (State == State.DataLoaded)
            {
                int bufferSize = Math.Max(ImpulseLookbackTicks + 32, 128);
                recentPrices = new double[bufferSize];
                recentIndex = 0;
                recentCount = 0;

                if (PrintDiagnostics)
                {
                    Print("------------------------------------------------------------");
                    Print(Name + " loaded.");
                    Print("Required chart: MNQ 1-tick chart.");
                    Print("Actual BarsPeriod: " + BarsPeriod.BarsPeriodType + " value=" + BarsPeriod.Value);
                    Print("Session filter: " + UseSessionFilter + " " + EntryStartTime + " to " + EntryEndTime + " chart time.");
                    Print("Anti-recursion: PostExitTickLock=" + PostExitTickLock
                        + " MinMillisecondsBetweenEntries=" + MinMillisecondsBetweenEntries
                        + " BlockSameBarReentry=" + BlockSameBarReentry);
                    Print("No AddDataSeries() is used.");
                    Print("------------------------------------------------------------");
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 2)
                return;

            ResetPerBarFlags();
            ResetSessionIfNeeded();

            double px = Close[0];
            DateTime now = Time[0];

            ticksSeenToday++;
            PushRecentPrice(px);

            if (postExitTickLockRemaining > 0)
                postExitTickLockRemaining--;

            ExpireStalePendingEntry();

            ManageOpenPosition(px, now);

            if (Position.MarketPosition == MarketPosition.Flat)
                EvaluateEntries(px, now);

            MaybePrintDiagnostics(px, now);
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

            if (execution.Order.OrderState != OrderState.Filled)
                return;

            string orderName = execution.Order.Name ?? string.Empty;

            if (orderName == LongSignalName || orderName == ShortSignalName)
            {
                pendingEntry = false;
                pendingEntryBar = -1;
                pendingEntryTime = Core.Globals.MinDate;

                entryTime = time;
                lastEntryTime = time;
                lastEntryBar = CurrentBar;
                entryPrice = price;
                highSinceEntry = price;
                lowSinceEntry = price;
                tradesToday++;

                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} FILLED ENTRY {1} @ {2:F2} tradesToday={3}",
                        time, orderName, price, tradesToday));

                return;
            }

            // Any filled non-entry order is treated as an exit event for lockout purposes.
            CommitExitLock(time, orderName, price);
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

            string orderName = order.Name ?? string.Empty;

            if (orderName == LongSignalName || orderName == ShortSignalName)
            {
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                {
                    pendingEntry = false;
                    pendingEntryBar = -1;
                    pendingEntryTime = Core.Globals.MinDate;

                    if (PrintDiagnostics)
                        Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTRY ORDER {1} {2} err={3} native={4}",
                            time, orderName, orderState, error, nativeError));
                }
            }
        }

        // --------------------------------------------------------------------
        // Entry engine
        // --------------------------------------------------------------------

        private void EvaluateEntries(double px, DateTime now)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (pendingEntry)
            {
                pendingEntryBlockedToday++;
                return;
            }

            if (!IsTradableSession(now))
            {
                sessionBlockedToday++;
                return;
            }

            if (tradesToday >= MaxTradesPerDay || emergencyExitsToday >= MaxEmergencyExits)
            {
                governanceBlockedToday++;
                return;
            }

            if (IsAntiRecursionBlocked(now))
            {
                antiRecursionBlockedToday++;
                return;
            }

            if (lastExitTime != Core.Globals.MinDate && now < lastExitTime.AddSeconds(CooldownSeconds))
            {
                cooldownBlockedToday++;
                return;
            }

            if (recentCount < Math.Max(ImpulseLookbackTicks, 4))
                return;

            bool longSetup = IsLongFadeSetup(px);
            bool shortSetup = IsShortFadeSetup(px);

            if (longSetup)
                longSetupsSeenToday++;

            if (shortSetup)
                shortSetupsSeenToday++;

            if (longSetup && CanEnterLong(now))
            {
                SubmitLong(now, px);
                return;
            }

            if (shortSetup && CanEnterShort(now))
            {
                SubmitShort(now, px);
                return;
            }

            if (longSetup && !CanEnterLong(now))
                longSuppressedToday++;

            if (shortSetup && !CanEnterShort(now))
                shortSuppressedToday++;
        }

        private bool IsAntiRecursionBlocked(DateTime now)
        {
            if (exitObservedThisBar)
                return true;

            if (postExitTickLockRemaining > 0)
                return true;

            if (BlockSameBarReentry && CurrentBar == lastEntryBar)
                return true;

            if (BlockSameBarReentry && CurrentBar == lastExitBar)
                return true;

            if (lastEntryTime != Core.Globals.MinDate &&
                (now - lastEntryTime).TotalMilliseconds < MinMillisecondsBetweenEntries)
                return true;

            if (lastExitTime != Core.Globals.MinDate &&
                now == lastExitTime)
                return true;

            return false;
        }

        private void SubmitLong(DateTime now, double px)
        {
            pendingEntry = true;
            pendingEntryBar = CurrentBar;
            pendingEntryTime = now;
            lastEntryBar = CurrentBar;
            lastEntryTime = now;
            submittedLongsToday++;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} SUBMIT LONG px={1:F2}", now, px));

            EnterLong(1, LongSignalName);
        }

        private void SubmitShort(DateTime now, double px)
        {
            pendingEntry = true;
            pendingEntryBar = CurrentBar;
            pendingEntryTime = now;
            lastEntryBar = CurrentBar;
            lastEntryTime = now;
            submittedShortsToday++;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} SUBMIT SHORT px={1:F2}", now, px));

            EnterShort(1, ShortSignalName);
        }

        private bool CanEnterLong(DateTime now)
        {
            return now >= longLockoutUntil && now >= longCascadeSuppressUntil;
        }

        private bool CanEnterShort(DateTime now)
        {
            return now >= shortLockoutUntil && now >= shortCascadeSuppressUntil;
        }

        private bool IsLongFadeSetup(double px)
        {
            double recentHigh = GetRecentHigh();
            double recentLow = GetRecentLow();

            int impulseTicks = ToTicks(recentHigh - recentLow);
            int reclaimTicks = ToTicks(px - recentLow);
            int ticksAfterLow = TicksSinceExtreme(false);

            bool turnOk = true;
            if (RequireTurnTick && recentCount >= 2)
                turnOk = px > GetRecentPriceByAge(1);

            return impulseTicks >= LongMinImpulseTicks
                && reclaimTicks >= LongReclaimTicks
                && ticksAfterLow >= LongMinTicksAfterLow
                && turnOk;
        }

        private bool IsShortFadeSetup(double px)
        {
            double recentHigh = GetRecentHigh();
            double recentLow = GetRecentLow();

            int impulseTicks = ToTicks(recentHigh - recentLow);
            int rejectTicks = ToTicks(recentHigh - px);
            int ticksAfterHigh = TicksSinceExtreme(true);

            bool turnOk = true;
            if (RequireTurnTick && recentCount >= 2)
                turnOk = px < GetRecentPriceByAge(1);

            return impulseTicks >= ShortMinImpulseTicks
                && rejectTicks >= ShortRejectTicks
                && ticksAfterHigh >= ShortMinTicksAfterHigh
                && turnOk;
        }

        // --------------------------------------------------------------------
        // Exit engine
        // --------------------------------------------------------------------

        private void ManageOpenPosition(double px, DateTime now)
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            highSinceEntry = Math.Max(highSinceEntry, px);
            lowSinceEntry = Math.Min(lowSinceEntry, px);

            double heldSeconds = entryTime == Core.Globals.MinDate
                ? 0.0
                : Math.Max(0.0, (now - entryTime).TotalSeconds);

            if (Position.MarketPosition == MarketPosition.Long)
            {
                int adverseTicks = ToTicks(entryPrice - px);
                int favorableTicks = ToTicks(highSinceEntry - entryPrice);

                if (adverseTicks >= EmergencyExitTicks)
                {
                    RegisterEmergencyExit(true, now, px);
                    ExitLong("B5_Emergency_Long", LongSignalName);
                    return;
                }

                if (heldSeconds >= TimeStopSeconds)
                {
                    CommitExitIntent(now, "B5_TimeStop_Long", px);
                    ExitLong("B5_TimeStop_Long", LongSignalName);
                    return;
                }

                if (heldSeconds >= EarlyFailureSeconds && favorableTicks < EarlyFailureMinProgressTicks)
                {
                    CommitExitIntent(now, "B5_EarlyFailure_Long", px);
                    ExitLong("B5_EarlyFailure_Long", LongSignalName);
                    return;
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                int adverseTicks = ToTicks(px - entryPrice);
                int favorableTicks = ToTicks(entryPrice - lowSinceEntry);

                if (adverseTicks >= EmergencyExitTicks)
                {
                    RegisterEmergencyExit(false, now, px);
                    ExitShort("B5_Emergency_Short", ShortSignalName);
                    return;
                }

                if (heldSeconds >= TimeStopSeconds)
                {
                    CommitExitIntent(now, "B5_TimeStop_Short", px);
                    ExitShort("B5_TimeStop_Short", ShortSignalName);
                    return;
                }

                if (heldSeconds >= EarlyFailureSeconds && favorableTicks < EarlyFailureMinProgressTicks)
                {
                    CommitExitIntent(now, "B5_EarlyFailure_Short", px);
                    ExitShort("B5_EarlyFailure_Short", ShortSignalName);
                    return;
                }
            }
        }

        private void CommitExitIntent(DateTime now, string reason, double px)
        {
            lastExitTime = now;
            lastExitBar = CurrentBar;
            postExitTickLockRemaining = Math.Max(postExitTickLockRemaining, PostExitTickLock);
            exitObservedThisBar = true;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EXIT INTENT {1} px={2:F2} lockTicks={3}",
                    now, reason, px, postExitTickLockRemaining));
        }

        private void CommitExitLock(DateTime time, string orderName, double price)
        {
            lastExitTime = time;
            lastExitBar = CurrentBar;
            postExitTickLockRemaining = Math.Max(postExitTickLockRemaining, PostExitTickLock);
            exitObservedThisBar = true;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} FILLED EXIT {1} @ {2:F2} cooldownUntil={3:HH:mm:ss} postExitTickLock={4}",
                    time, orderName, price, time.AddSeconds(CooldownSeconds), postExitTickLockRemaining));
        }

        private void RegisterEmergencyExit(bool wasLong, DateTime now, double px)
        {
            emergencyExitsToday++;
            CommitExitIntent(now, wasLong ? "B5_Emergency_Long" : "B5_Emergency_Short", px);

            if (wasLong)
            {
                longLockoutUntil = now.AddMinutes(SideLockoutMinutes);

                if (lastLongEmergencyTime == Core.Globals.MinDate ||
                    now > lastLongEmergencyTime.AddMinutes(CascadeWindowMinutes))
                    longEmergencyCountWindow = 1;
                else
                    longEmergencyCountWindow++;

                lastLongEmergencyTime = now;
                lastLongEmergencyLow = px;

                if (ShouldSuppressLongCascade(px))
                    longCascadeSuppressUntil = now.AddMinutes(CascadeSuppressMinutes);
            }
            else
            {
                shortLockoutUntil = now.AddMinutes(SideLockoutMinutes);

                if (lastShortEmergencyTime == Core.Globals.MinDate ||
                    now > lastShortEmergencyTime.AddMinutes(CascadeWindowMinutes))
                    shortEmergencyCountWindow = 1;
                else
                    shortEmergencyCountWindow++;

                lastShortEmergencyTime = now;
                lastShortEmergencyHigh = px;

                if (ShouldSuppressShortCascade(px))
                    shortCascadeSuppressUntil = now.AddMinutes(CascadeSuppressMinutes);
            }

            if (PrintDiagnostics)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EMERGENCY {1} px={2:F2} emergencyToday={3} longSuppressUntil={4:HH:mm:ss} shortSuppressUntil={5:HH:mm:ss}",
                    now,
                    wasLong ? "LONG" : "SHORT",
                    px,
                    emergencyExitsToday,
                    longCascadeSuppressUntil,
                    shortCascadeSuppressUntil));
            }
        }

        private bool ShouldSuppressLongCascade(double px)
        {
            if (longEmergencyCountWindow < CascadeEmergencyThreshold)
                return false;

            double recentLow = GetRecentLow();
            int nearRecentLowTicks = ToTicks(px - recentLow);
            int continuationTicks = ToTicks(lastLongEmergencyLow - px);

            return continuationTicks >= CascadeContinuationTicks || nearRecentLowTicks <= 1;
        }

        private bool ShouldSuppressShortCascade(double px)
        {
            if (shortEmergencyCountWindow < CascadeEmergencyThreshold)
                return false;

            double recentHigh = GetRecentHigh();
            int nearRecentHighTicks = ToTicks(recentHigh - px);
            int continuationTicks = ToTicks(px - lastShortEmergencyHigh);

            return continuationTicks >= CascadeContinuationTicks || nearRecentHighTicks <= 1;
        }

        // --------------------------------------------------------------------
        // Rolling buffer
        // --------------------------------------------------------------------

        private void PushRecentPrice(double px)
        {
            if (recentPrices == null || recentPrices.Length == 0)
                return;

            recentPrices[recentIndex] = px;
            recentIndex = (recentIndex + 1) % recentPrices.Length;
            recentCount = Math.Min(recentCount + 1, recentPrices.Length);
        }

        private double GetRecentHigh()
        {
            int n = Math.Min(ImpulseLookbackTicks, recentCount);
            if (n <= 0)
                return Close[0];

            double hi = double.MinValue;
            for (int age = 0; age < n; age++)
            {
                double v = GetRecentPriceByAge(age);
                if (v > hi)
                    hi = v;
            }

            return hi;
        }

        private double GetRecentLow()
        {
            int n = Math.Min(ImpulseLookbackTicks, recentCount);
            if (n <= 0)
                return Close[0];

            double lo = double.MaxValue;
            for (int age = 0; age < n; age++)
            {
                double v = GetRecentPriceByAge(age);
                if (v < lo)
                    lo = v;
            }

            return lo;
        }

        private int TicksSinceExtreme(bool highExtreme)
        {
            int n = Math.Min(ImpulseLookbackTicks, recentCount);
            if (n <= 0)
                return 0;

            double extreme = highExtreme ? double.MinValue : double.MaxValue;
            int extremeAge = 0;

            for (int age = 0; age < n; age++)
            {
                double v = GetRecentPriceByAge(age);

                if (highExtreme)
                {
                    if (v > extreme)
                    {
                        extreme = v;
                        extremeAge = age;
                    }
                }
                else
                {
                    if (v < extreme)
                    {
                        extreme = v;
                        extremeAge = age;
                    }
                }
            }

            return extremeAge;
        }

        private double GetRecentPriceByAge(int age)
        {
            int idx = recentIndex - 1 - age;
            while (idx < 0)
                idx += recentPrices.Length;

            return recentPrices[idx % recentPrices.Length];
        }

        // --------------------------------------------------------------------
        // Governance / diagnostics / utility
        // --------------------------------------------------------------------

        private void ResetPerBarFlags()
        {
            // This is intentionally per OnBarUpdate, not per timestamp.
            // It prevents a fill observed during one update from allowing an immediate entry
            // in the same update path, while allowing future bars to use the hard locks.
            exitObservedThisBar = false;
        }

        private void ExpireStalePendingEntry()
        {
            if (!pendingEntry)
                return;

            if (pendingEntryBar < 0)
                return;

            if (CurrentBar - pendingEntryBar > PendingEntryMaxBars)
            {
                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} CLEAR STALE PENDING ENTRY after {1} bars",
                        Time[0], CurrentBar - pendingEntryBar));

                pendingEntry = false;
                pendingEntryBar = -1;
                pendingEntryTime = Core.Globals.MinDate;
            }
        }

        private void ResetSessionIfNeeded()
        {
            DateTime d = Time[0].Date;
            if (currentSessionDate == Core.Globals.MinDate)
            {
                currentSessionDate = d;
                return;
            }

            if (d == currentSessionDate)
                return;

            if (PrintDiagnostics)
                PrintDailySummary(Time[0]);

            currentSessionDate = d;

            entryTime = Core.Globals.MinDate;
            lastEntryTime = Core.Globals.MinDate;
            lastExitTime = Core.Globals.MinDate;

            lastEntryBar = -1;
            lastExitBar = -1;

            pendingEntry = false;
            pendingEntryBar = -1;
            pendingEntryTime = Core.Globals.MinDate;

            postExitTickLockRemaining = 0;
            exitObservedThisBar = false;

            longLockoutUntil = Core.Globals.MinDate;
            shortLockoutUntil = Core.Globals.MinDate;
            longCascadeSuppressUntil = Core.Globals.MinDate;
            shortCascadeSuppressUntil = Core.Globals.MinDate;

            lastLongEmergencyTime = Core.Globals.MinDate;
            lastShortEmergencyTime = Core.Globals.MinDate;

            entryPrice = 0.0;
            highSinceEntry = 0.0;
            lowSinceEntry = 0.0;
            lastLongEmergencyLow = 0.0;
            lastShortEmergencyHigh = 0.0;

            tradesToday = 0;
            emergencyExitsToday = 0;
            longEmergencyCountWindow = 0;
            shortEmergencyCountWindow = 0;

            ticksSeenToday = 0;
            longSetupsSeenToday = 0;
            shortSetupsSeenToday = 0;
            sessionBlockedToday = 0;
            cooldownBlockedToday = 0;
            governanceBlockedToday = 0;
            antiRecursionBlockedToday = 0;
            pendingEntryBlockedToday = 0;
            longSuppressedToday = 0;
            shortSuppressedToday = 0;
            submittedLongsToday = 0;
            submittedShortsToday = 0;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} NEW SESSION reset", Time[0]));
        }

        private bool IsTradableSession(DateTime now)
        {
            if (!UseSessionFilter)
                return true;

            int t = ToTime(now);
            return t >= EntryStartTime && t <= EntryEndTime;
        }

        private int ToTicks(double points)
        {
            if (TickSize <= 0.0)
                return 0;

            return (int)Math.Round(points / TickSize, MidpointRounding.AwayFromZero);
        }

        private void MaybePrintDiagnostics(double px, DateTime now)
        {
            if (!PrintDiagnostics)
                return;

            if (CurrentBar == lastDiagBar)
                return;

            if (DiagnosticEveryTicks <= 0)
                return;

            if (ticksSeenToday % DiagnosticEveryTicks != 0)
                return;

            lastDiagBar = CurrentBar;

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm:ss} DIAG ticks={1} px={2:F2} pos={3} trades={4} submittedL={5} submittedS={6} Lsetup={7} Ssetup={8} sessBlk={9} coolBlk={10} govBlk={11} antiRecBlk={12} pendBlk={13} Lsup={14} Ssup={15} postExitTicks={16} recentHigh={17:F2} recentLow={18:F2}",
                now,
                ticksSeenToday,
                px,
                Position.MarketPosition,
                tradesToday,
                submittedLongsToday,
                submittedShortsToday,
                longSetupsSeenToday,
                shortSetupsSeenToday,
                sessionBlockedToday,
                cooldownBlockedToday,
                governanceBlockedToday,
                antiRecursionBlockedToday,
                pendingEntryBlockedToday,
                longSuppressedToday,
                shortSuppressedToday,
                postExitTickLockRemaining,
                GetRecentHigh(),
                GetRecentLow()));
        }

        private void PrintDailySummary(DateTime now)
        {
            Print("------------------------------------------------------------");
            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} B5 DAILY SUMMARY", now));
            Print(string.Format("ticksSeen={0} trades={1} emergencies={2}", ticksSeenToday, tradesToday, emergencyExitsToday));
            Print(string.Format("submittedLongs={0} submittedShorts={1}", submittedLongsToday, submittedShortsToday));
            Print(string.Format("longSetups={0} shortSetups={1}", longSetupsSeenToday, shortSetupsSeenToday));
            Print(string.Format("sessionBlocked={0} cooldownBlocked={1} governanceBlocked={2}", sessionBlockedToday, cooldownBlockedToday, governanceBlockedToday));
            Print(string.Format("antiRecursionBlocked={0} pendingEntryBlocked={1}", antiRecursionBlockedToday, pendingEntryBlockedToday));
            Print(string.Format("longSuppressed={0} shortSuppressed={1}", longSuppressedToday, shortSuppressedToday));
            Print("------------------------------------------------------------");
        }
    }
}
