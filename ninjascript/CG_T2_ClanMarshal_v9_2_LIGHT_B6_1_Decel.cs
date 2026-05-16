// CG_T2_ClanMarshal_v9_2_LIGHT_B6_1_Decel.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-06 15:10:00 America/New_York
//
// MNQ Intraday Strategy — v9.2 LIGHT B6.1 Deceleration
//
// PURPOSE
//   B6 was too selective and produced no executions by noon.
//   B6.1 loosens the entry thresholds while adding a lightweight
//   acceleration-suppression filter.
//
// EVOLUTION PATH
//   B4: live tick engine worked, but overtraded.
//   B5: fixed same-second re-entry / recursion.
//   B6: improved selectivity but became too restrictive.
//   B6.1: restores participation, but refuses to fade straight-line acceleration.
//
// CORE IDEA
//   Do not require a perfect V-bottom/V-top.
//   Instead:
//     - allow moderate impulse + reclaim/reject,
//     - require a small turn,
//     - reject entries when the last N ticks are overwhelmingly one-directional.
//
// LONG SETUP
//   Down impulse -> some reclaim -> not still in straight-line sell acceleration.
//
// SHORT SETUP
//   Up impulse -> some reject -> not still in straight-line buy acceleration.
//
// REQUIRED CHART SETUP
//   Instrument: MNQ front contract
//   Bars type:  Tick
//   Value:      1
//
// TIME WARNING
//   EntryStartTime and EntryEndTime are chart-time HHMMSS values.
//
// INSTALL
//   Documents\NinjaTrader 8\bin\Custom\Strategies\CG_T2_ClanMarshal_v9_2_LIGHT_B6_1_Decel.cs
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
    public class CG_T2_ClanMarshal_v9_2_LIGHT_B6_1_Decel : Strategy
    {
        private double[] recentPrices;
        private int recentIndex;
        private int recentCount;

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
        private DateTime anyEmergencyLockoutUntil = Core.Globals.MinDate;

        private double entryPrice = 0.0;
        private double highSinceEntry = 0.0;
        private double lowSinceEntry = 0.0;
        private double lastLongEmergencyLow = 0.0;
        private double lastShortEmergencyHigh = 0.0;

        private int tradesToday = 0;
        private int emergencyExitsToday = 0;
        private int longEmergencyCountWindow = 0;
        private int shortEmergencyCountWindow = 0;

        private string lastEmergencySide = "";
        private int alternatingEmergencyCount = 0;

        private long ticksSeenToday = 0;
        private long longSetupsSeenToday = 0;
        private long shortSetupsSeenToday = 0;
        private long longAccelBlockedToday = 0;
        private long shortAccelBlockedToday = 0;
        private long sessionBlockedToday = 0;
        private long cooldownBlockedToday = 0;
        private long governanceBlockedToday = 0;
        private long antiRecursionBlockedToday = 0;
        private long pendingEntryBlockedToday = 0;
        private long longSuppressedToday = 0;
        private long shortSuppressedToday = 0;
        private long flipLockBlockedToday = 0;
        private long submittedLongsToday = 0;
        private long submittedShortsToday = 0;

        private int lastDiagBar = -1;

        private const string LongSignalName = "B61_Long";
        private const string ShortSignalName = "B61_Short";

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
        [Range(3, 300)]
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
        [Range(0, 100)]
        [Display(Name = "LongConfirmTicksFromLow", GroupName = "03 Long", Order = 4)]
        public int LongConfirmTicksFromLow { get; set; }

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
        [Range(0, 100)]
        [Display(Name = "ShortConfirmTicksFromHigh", GroupName = "04 Short", Order = 4)]
        public int ShortConfirmTicksFromHigh { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireTurnTick", GroupName = "05 Deceleration", Order = 1)]
        public bool RequireTurnTick { get; set; }

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "AccelerationLookbackTicks", GroupName = "05 Deceleration", Order = 2)]
        public int AccelerationLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "MaxSameDirectionTicks", GroupName = "05 Deceleration", Order = 3)]
        public int MaxSameDirectionTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "MaxLateReclaimTicks", GroupName = "05 Deceleration", Order = 4)]
        public int MaxLateReclaimTicks { get; set; }

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
        [Range(1, 60)]
        [Display(Name = "EmergencyFlipLockoutMinutes", GroupName = "07 Governance", Order = 5)]
        public int EmergencyFlipLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "AlternationLockoutMinutes", GroupName = "07 Governance", Order = 6)]
        public int AlternationLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "AlternatingEmergencyThreshold", GroupName = "07 Governance", Order = 7)]
        public int AlternatingEmergencyThreshold { get; set; }

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

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_2_LIGHT_B6_1_Decel";
                Description = "v9.2 LIGHT B6.1 loosened selective MNQ tick fade engine with acceleration suppression.";

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
                EntryStartTime = 83500;
                EntryEndTime = 110000;

                ImpulseLookbackTicks = 18;

                LongMinImpulseTicks = 6;
                LongReclaimTicks = 3;
                LongMinTicksAfterLow = 2;
                LongConfirmTicksFromLow = 3;

                ShortMinImpulseTicks = 6;
                ShortRejectTicks = 3;
                ShortMinTicksAfterHigh = 2;
                ShortConfirmTicksFromHigh = 3;

                RequireTurnTick = true;
                AccelerationLookbackTicks = 8;
                MaxSameDirectionTicks = 6;
                MaxLateReclaimTicks = 12;

                ProfitTargetTicks = 8;
                StopLossTicks = 8;
                EmergencyExitTicks = 7;
                TimeStopSeconds = 45;
                EarlyFailureSeconds = 15;
                EarlyFailureMinProgressTicks = 2;

                CooldownSeconds = 45;
                MaxTradesPerDay = 16;
                MaxEmergencyExits = 5;
                SideLockoutMinutes = 4;
                EmergencyFlipLockoutMinutes = 3;
                AlternationLockoutMinutes = 6;
                AlternatingEmergencyThreshold = 2;

                PostExitTickLock = 10;
                MinMillisecondsBetweenEntries = 2000;
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
                int bufferSize = Math.Max(Math.Max(ImpulseLookbackTicks, AccelerationLookbackTicks) + 64, 256);
                recentPrices = new double[bufferSize];

                if (PrintDiagnostics)
                {
                    Print("------------------------------------------------------------");
                    Print(Name + " loaded.");
                    Print("Required chart: MNQ 1-tick chart.");
                    Print("Actual BarsPeriod: " + BarsPeriod.BarsPeriodType + " value=" + BarsPeriod.Value);
                    Print("Session: " + EntryStartTime + " to " + EntryEndTime + " chart time.");
                    Print("Loosened thresholds: impulse 6, reclaim/reject 3, confirm 3.");
                    Print("Acceleration suppression: lookback=" + AccelerationLookbackTicks + " maxSameDir=" + MaxSameDirectionTicks);
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
            if (execution == null || execution.Order == null || execution.Order.OrderState != OrderState.Filled)
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
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} FILLED ENTRY {1} @ {2:F2} tradesToday={3}", time, orderName, price, tradesToday));

                return;
            }

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
                        Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTRY ORDER {1} {2} err={3} native={4}", time, orderName, orderState, error, nativeError));
                }
            }
        }

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

            if (now < anyEmergencyLockoutUntil)
            {
                flipLockBlockedToday++;
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

            if (recentCount < Math.Max(ImpulseLookbackTicks, AccelerationLookbackTicks + 2))
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

        private bool IsLongFadeSetup(double px)
        {
            double recentHigh = GetRecentHigh();
            double recentLow = GetRecentLow();

            int impulseTicks = ToTicks(recentHigh - recentLow);
            int reclaimTicks = ToTicks(px - recentLow);
            int ticksAfterLow = TicksSinceExtreme(false);

            bool turnOk = !RequireTurnTick || (recentCount >= 2 && px > GetRecentPriceByAge(1));
            bool notTooLate = MaxLateReclaimTicks <= 0 || reclaimTicks <= MaxLateReclaimTicks;

            int downTicks = CountDirectionalTicks(AccelerationLookbackTicks, false);
            bool acceleratingDown = downTicks > MaxSameDirectionTicks;

            if (acceleratingDown)
                longAccelBlockedToday++;

            return impulseTicks >= LongMinImpulseTicks
                && reclaimTicks >= LongReclaimTicks
                && reclaimTicks >= LongConfirmTicksFromLow
                && ticksAfterLow >= LongMinTicksAfterLow
                && turnOk
                && notTooLate
                && !acceleratingDown;
        }

        private bool IsShortFadeSetup(double px)
        {
            double recentHigh = GetRecentHigh();
            double recentLow = GetRecentLow();

            int impulseTicks = ToTicks(recentHigh - recentLow);
            int rejectTicks = ToTicks(recentHigh - px);
            int ticksAfterHigh = TicksSinceExtreme(true);

            bool turnOk = !RequireTurnTick || (recentCount >= 2 && px < GetRecentPriceByAge(1));
            bool notTooLate = MaxLateReclaimTicks <= 0 || rejectTicks <= MaxLateReclaimTicks;

            int upTicks = CountDirectionalTicks(AccelerationLookbackTicks, true);
            bool acceleratingUp = upTicks > MaxSameDirectionTicks;

            if (acceleratingUp)
                shortAccelBlockedToday++;

            return impulseTicks >= ShortMinImpulseTicks
                && rejectTicks >= ShortRejectTicks
                && rejectTicks >= ShortConfirmTicksFromHigh
                && ticksAfterHigh >= ShortMinTicksAfterHigh
                && turnOk
                && notTooLate
                && !acceleratingUp;
        }

        private bool CanEnterLong(DateTime now)
        {
            return now >= longLockoutUntil && now >= longCascadeSuppressUntil;
        }

        private bool CanEnterShort(DateTime now)
        {
            return now >= shortLockoutUntil && now >= shortCascadeSuppressUntil;
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

            if (lastEntryTime != Core.Globals.MinDate && (now - lastEntryTime).TotalMilliseconds < MinMillisecondsBetweenEntries)
                return true;

            if (lastExitTime != Core.Globals.MinDate && now == lastExitTime)
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

        private void ManageOpenPosition(double px, DateTime now)
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            highSinceEntry = Math.Max(highSinceEntry, px);
            lowSinceEntry = Math.Min(lowSinceEntry, px);

            double heldSeconds = entryTime == Core.Globals.MinDate ? 0.0 : Math.Max(0.0, (now - entryTime).TotalSeconds);

            if (Position.MarketPosition == MarketPosition.Long)
            {
                int adverseTicks = ToTicks(entryPrice - px);
                int favorableTicks = ToTicks(highSinceEntry - entryPrice);

                if (adverseTicks >= EmergencyExitTicks)
                {
                    RegisterEmergencyExit(true, now, px);
                    ExitLong("B61_Emergency_Long", LongSignalName);
                    return;
                }

                if (heldSeconds >= TimeStopSeconds)
                {
                    CommitExitIntent(now, "B61_TimeStop_Long", px);
                    ExitLong("B61_TimeStop_Long", LongSignalName);
                    return;
                }

                if (heldSeconds >= EarlyFailureSeconds && favorableTicks < EarlyFailureMinProgressTicks)
                {
                    CommitExitIntent(now, "B61_EarlyFailure_Long", px);
                    ExitLong("B61_EarlyFailure_Long", LongSignalName);
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
                    ExitShort("B61_Emergency_Short", ShortSignalName);
                    return;
                }

                if (heldSeconds >= TimeStopSeconds)
                {
                    CommitExitIntent(now, "B61_TimeStop_Short", px);
                    ExitShort("B61_TimeStop_Short", ShortSignalName);
                    return;
                }

                if (heldSeconds >= EarlyFailureSeconds && favorableTicks < EarlyFailureMinProgressTicks)
                {
                    CommitExitIntent(now, "B61_EarlyFailure_Short", px);
                    ExitShort("B61_EarlyFailure_Short", ShortSignalName);
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
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EXIT INTENT {1} px={2:F2} lockTicks={3}", now, reason, px, postExitTickLockRemaining));
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
            CommitExitIntent(now, wasLong ? "B61_Emergency_Long" : "B61_Emergency_Short", px);

            string thisSide = wasLong ? "LONG" : "SHORT";

            if (lastEmergencySide.Length > 0 && lastEmergencySide != thisSide)
                alternatingEmergencyCount++;
            else
                alternatingEmergencyCount = 1;

            lastEmergencySide = thisSide;

            if (alternatingEmergencyCount >= AlternatingEmergencyThreshold)
                anyEmergencyLockoutUntil = now.AddMinutes(AlternationLockoutMinutes);
            else
                anyEmergencyLockoutUntil = now.AddMinutes(EmergencyFlipLockoutMinutes);

            if (wasLong)
            {
                longLockoutUntil = now.AddMinutes(SideLockoutMinutes);
                shortLockoutUntil = MaxDate(shortLockoutUntil, now.AddMinutes(EmergencyFlipLockoutMinutes));

                if (lastLongEmergencyTime == Core.Globals.MinDate || now > lastLongEmergencyTime.AddMinutes(CascadeWindowMinutes))
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
                longLockoutUntil = MaxDate(longLockoutUntil, now.AddMinutes(EmergencyFlipLockoutMinutes));

                if (lastShortEmergencyTime == Core.Globals.MinDate || now > lastShortEmergencyTime.AddMinutes(CascadeWindowMinutes))
                    shortEmergencyCountWindow = 1;
                else
                    shortEmergencyCountWindow++;

                lastShortEmergencyTime = now;
                lastShortEmergencyHigh = px;

                if (ShouldSuppressShortCascade(px))
                    shortCascadeSuppressUntil = now.AddMinutes(CascadeSuppressMinutes);
            }

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EMERGENCY {1} px={2:F2} emergencyToday={3} lockUntil={4:HH:mm:ss}",
                    now, thisSide, px, emergencyExitsToday, anyEmergencyLockoutUntil));
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

        private int CountDirectionalTicks(int lookback, bool countUp)
        {
            int n = Math.Min(lookback, recentCount - 1);
            int count = 0;

            for (int age = 0; age < n; age++)
            {
                double newer = GetRecentPriceByAge(age);
                double older = GetRecentPriceByAge(age + 1);

                if (countUp && newer > older)
                    count++;

                if (!countUp && newer < older)
                    count++;
            }

            return count;
        }

        private double GetRecentPriceByAge(int age)
        {
            int idx = recentIndex - 1 - age;
            while (idx < 0)
                idx += recentPrices.Length;

            return recentPrices[idx % recentPrices.Length];
        }

        private void ResetPerBarFlags()
        {
            exitObservedThisBar = false;
        }

        private void ExpireStalePendingEntry()
        {
            if (!pendingEntry || pendingEntryBar < 0)
                return;

            if (CurrentBar - pendingEntryBar > PendingEntryMaxBars)
            {
                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} CLEAR STALE PENDING ENTRY after {1} bars", Time[0], CurrentBar - pendingEntryBar));

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
            anyEmergencyLockoutUntil = Core.Globals.MinDate;

            entryPrice = 0.0;
            highSinceEntry = 0.0;
            lowSinceEntry = 0.0;
            lastLongEmergencyLow = 0.0;
            lastShortEmergencyHigh = 0.0;

            tradesToday = 0;
            emergencyExitsToday = 0;
            longEmergencyCountWindow = 0;
            shortEmergencyCountWindow = 0;
            lastEmergencySide = "";
            alternatingEmergencyCount = 0;

            ticksSeenToday = 0;
            longSetupsSeenToday = 0;
            shortSetupsSeenToday = 0;
            longAccelBlockedToday = 0;
            shortAccelBlockedToday = 0;
            sessionBlockedToday = 0;
            cooldownBlockedToday = 0;
            governanceBlockedToday = 0;
            antiRecursionBlockedToday = 0;
            pendingEntryBlockedToday = 0;
            longSuppressedToday = 0;
            shortSuppressedToday = 0;
            flipLockBlockedToday = 0;
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

        private DateTime MaxDate(DateTime a, DateTime b)
        {
            return a >= b ? a : b;
        }

        private void MaybePrintDiagnostics(double px, DateTime now)
        {
            if (!PrintDiagnostics || CurrentBar == lastDiagBar || DiagnosticEveryTicks <= 0)
                return;

            if (ticksSeenToday % DiagnosticEveryTicks != 0)
                return;

            lastDiagBar = CurrentBar;

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm:ss} DIAG ticks={1} px={2:F2} pos={3} trades={4} submittedL={5} submittedS={6} Lsetup={7} Ssetup={8} LaccelBlk={9} SaccelBlk={10} sessBlk={11} coolBlk={12} govBlk={13} antiRecBlk={14} pendBlk={15} flipBlk={16} Lsup={17} Ssup={18} recentHigh={19:F2} recentLow={20:F2}",
                now,
                ticksSeenToday,
                px,
                Position.MarketPosition,
                tradesToday,
                submittedLongsToday,
                submittedShortsToday,
                longSetupsSeenToday,
                shortSetupsSeenToday,
                longAccelBlockedToday,
                shortAccelBlockedToday,
                sessionBlockedToday,
                cooldownBlockedToday,
                governanceBlockedToday,
                antiRecursionBlockedToday,
                pendingEntryBlockedToday,
                flipLockBlockedToday,
                longSuppressedToday,
                shortSuppressedToday,
                GetRecentHigh(),
                GetRecentLow()));
        }

        private void PrintDailySummary(DateTime now)
        {
            Print("------------------------------------------------------------");
            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} B6.1 DAILY SUMMARY", now));
            Print(string.Format("ticksSeen={0} trades={1} emergencies={2}", ticksSeenToday, tradesToday, emergencyExitsToday));
            Print(string.Format("submittedLongs={0} submittedShorts={1}", submittedLongsToday, submittedShortsToday));
            Print(string.Format("longSetups={0} shortSetups={1}", longSetupsSeenToday, shortSetupsSeenToday));
            Print(string.Format("longAccelBlocked={0} shortAccelBlocked={1}", longAccelBlockedToday, shortAccelBlockedToday));
            Print(string.Format("sessionBlocked={0} cooldownBlocked={1} governanceBlocked={2}", sessionBlockedToday, cooldownBlockedToday, governanceBlockedToday));
            Print(string.Format("antiRecursionBlocked={0} pendingEntryBlocked={1} flipLockBlocked={2}", antiRecursionBlockedToday, pendingEntryBlockedToday, flipLockBlockedToday));
            Print(string.Format("longSuppressed={0} shortSuppressed={1}", longSuppressedToday, shortSuppressedToday));
            Print("------------------------------------------------------------");
        }
    }
}
