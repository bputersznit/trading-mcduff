// CG_T2_ClanMarshal_v9_2_LIGHT_B6_1_Decel.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-06 16:30:00 America/New_York
//
// MNQ Intraday Strategy - v9.2 LIGHT B6_1 Hybrid Trend/Fade
//
// SAME FILE AND CLASS NAME AS REQUESTED
//   File:  CG_T2_ClanMarshal_v9_2_LIGHT_B6_1_Decel.cs
//   Class: CG_T2_ClanMarshal_v9_2_LIGHT_B6_1_Decel
//
// PURPOSE
//   Previous B6_1 Decel/Stall could miss strong one-way auction moves.
//   This version keeps the same file/class name but adds an internal synthetic
//   60-second trend proxy built from the 1-tick chart.
//
//   It remains a one-contract MNQ strategy:
//     - Quantity is hardcoded to 1.
//     - No new entry unless Position.MarketPosition == Flat.
//     - Pending-entry lock prevents duplicate submissions.
//     - EntriesPerDirection = 1.
//     - No overlapping positions.
//
// MODES
//   1. UP_TREND_AUCTION:
//      - Synthetic 60-second bars show strong upward auction.
//      - Shorts are suppressed.
//      - Long continuation pullback/resume entries are allowed.
//
//   2. DOWN_TREND_AUCTION:
//      - Synthetic 60-second bars show strong downward auction.
//      - Longs are suppressed.
//      - Short continuation pullback/resume entries are allowed.
//
//   3. NO_TREND_AUCTION:
//      - Original micro-fade/reversion logic is allowed.
//
// REQUIRED CHART SETUP
//   Instrument: MNQ front contract
//   Bars type: Tick
//   Value: 1
//
// IMPORTANT
//   No AddDataSeries.
//   No drawing/rendering.
//   No BarsPeriod inspection.
//   Single-series OnBarUpdate engine.
//   Managed SetStopLoss / SetProfitTarget brackets.
//
// INSTALL
//   Documents\NinjaTrader 8\bin\Custom\Strategies\CG_T2_ClanMarshal_v9_2_LIGHT_B6_1_Decel.cs

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_T2_ClanMarshal_v9_2_LIGHT_B6_1_Decel : Strategy
    {
        private enum AuctionMode
        {
            None = 0,
            UpTrend = 1,
            DownTrend = 2
        }

        // --------------------------------------------------------------------
        // Rolling tick buffer
        // --------------------------------------------------------------------

        private double[] recentPrices;
        private int recentIndex;
        private int recentCount;

        // --------------------------------------------------------------------
        // Synthetic time-bar proxy from 1-tick series
        // --------------------------------------------------------------------

        private DateTime currentSyntheticStart = Core.Globals.MinDate;
        private double syntheticOpen = 0.0;
        private double syntheticHigh = 0.0;
        private double syntheticLow = 0.0;
        private double syntheticClose = 0.0;
        private bool syntheticActive = false;

        private double[] synthOpen;
        private double[] synthHigh;
        private double[] synthLow;
        private double[] synthClose;
        private DateTime[] synthEndTime;
        private int synthIndex = 0;
        private int synthCount = 0;

        private AuctionMode auctionMode = AuctionMode.None;
        private DateTime auctionModeSince = Core.Globals.MinDate;

        private double trendReferenceHigh = 0.0;
        private double trendReferenceLow = 0.0;

        // --------------------------------------------------------------------
        // Session / execution state
        // --------------------------------------------------------------------

        private DateTime currentSessionDate = Core.Globals.MinDate;

        private DateTime entryTime = Core.Globals.MinDate;
        private DateTime lastEntryTime = Core.Globals.MinDate;
        private DateTime lastExitTime = Core.Globals.MinDate;

        private int lastEntryBar = -1;
        private int lastExitBar = -1;

        private bool pendingEntry = false;
        private int pendingEntryBar = -1;

        private int postExitTickLockRemaining = 0;
        private bool exitObservedThisUpdate = false;

        private DateTime longLockoutUntil = Core.Globals.MinDate;
        private DateTime shortLockoutUntil = Core.Globals.MinDate;
        private DateTime longCascadeSuppressUntil = Core.Globals.MinDate;
        private DateTime shortCascadeSuppressUntil = Core.Globals.MinDate;
        private DateTime anyEmergencyLockoutUntil = Core.Globals.MinDate;

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

        private string lastEmergencySide = string.Empty;
        private int alternatingEmergencyCount = 0;

        // --------------------------------------------------------------------
        // Diagnostics
        // --------------------------------------------------------------------

        private long ticksSeenToday = 0;
        private long fadeLongSetupsToday = 0;
        private long fadeShortSetupsToday = 0;
        private long trendLongSetupsToday = 0;
        private long trendShortSetupsToday = 0;
        private long longAccelBlockedToday = 0;
        private long shortAccelBlockedToday = 0;
        private long longStallBlockedToday = 0;
        private long shortStallBlockedToday = 0;
        private long sessionBlockedToday = 0;
        private long cooldownBlockedToday = 0;
        private long governanceBlockedToday = 0;
        private long antiRecursionBlockedToday = 0;
        private long pendingEntryBlockedToday = 0;
        private long longSuppressedToday = 0;
        private long shortSuppressedToday = 0;
        private long flipLockBlockedToday = 0;
        private long trendSuppressedFadeToday = 0;
        private long submittedLongsToday = 0;
        private long submittedShortsToday = 0;

        private int lastDiagBar = -1;

        private const string FadeLongSignalName = "B61_Fade_Long";
        private const string FadeShortSignalName = "B61_Fade_Short";
        private const string TrendLongSignalName = "B61_Trend_Long";
        private const string TrendShortSignalName = "B61_Trend_Short";

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
        [Display(Name = "EnableTrendMode", GroupName = "02 TrendProxy", Order = 1)]
        public bool EnableTrendMode { get; set; }

        [NinjaScriptProperty]
        [Range(10, 300)]
        [Display(Name = "SyntheticTrendSeconds", GroupName = "02 TrendProxy", Order = 2)]
        public int SyntheticTrendSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "TrendBarsRequired", GroupName = "02 TrendProxy", Order = 3)]
        public int TrendBarsRequired { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 100.0)]
        [Display(Name = "TrendMinMovePoints", GroupName = "02 TrendProxy", Order = 4)]
        public double TrendMinMovePoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "TrendCurrentBarMinPoints", GroupName = "02 TrendProxy", Order = 5)]
        public double TrendCurrentBarMinPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "TrendPullbackTicks", GroupName = "03 TrendEntry", Order = 1)]
        public int TrendPullbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "TrendResumeTicks", GroupName = "03 TrendEntry", Order = 2)]
        public int TrendResumeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "TrendMaxPullbackTicks", GroupName = "03 TrendEntry", Order = 3)]
        public int TrendMaxPullbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(3, 300)]
        [Display(Name = "ImpulseLookbackTicks", GroupName = "04 FadeSignal", Order = 1)]
        public int ImpulseLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "LongMinImpulseTicks", GroupName = "05 FadeLong", Order = 1)]
        public int LongMinImpulseTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "LongReclaimTicks", GroupName = "05 FadeLong", Order = 2)]
        public int LongReclaimTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "LongMinTicksAfterLow", GroupName = "05 FadeLong", Order = 3)]
        public int LongMinTicksAfterLow { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "LongConfirmTicksFromLow", GroupName = "05 FadeLong", Order = 4)]
        public int LongConfirmTicksFromLow { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ShortMinImpulseTicks", GroupName = "06 FadeShort", Order = 1)]
        public int ShortMinImpulseTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ShortRejectTicks", GroupName = "06 FadeShort", Order = 2)]
        public int ShortRejectTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "ShortMinTicksAfterHigh", GroupName = "06 FadeShort", Order = 3)]
        public int ShortMinTicksAfterHigh { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "ShortConfirmTicksFromHigh", GroupName = "06 FadeShort", Order = 4)]
        public int ShortConfirmTicksFromHigh { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireTurnTick", GroupName = "07 Acceleration", Order = 1)]
        public bool RequireTurnTick { get; set; }

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "AccelerationLookbackTicks", GroupName = "07 Acceleration", Order = 2)]
        public int AccelerationLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "MaxSameDirectionTicks", GroupName = "07 Acceleration", Order = 3)]
        public int MaxSameDirectionTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "MaxLateReclaimTicks", GroupName = "07 Acceleration", Order = 4)]
        public int MaxLateReclaimTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "StallLookbackTicks", GroupName = "08 StallGate", Order = 1)]
        public int StallLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "LongNoFreshLowTicks", GroupName = "08 StallGate", Order = 2)]
        public int LongNoFreshLowTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "ShortNoFreshHighTicks", GroupName = "08 StallGate", Order = 3)]
        public int ShortNoFreshHighTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "ProfitTargetTicks", GroupName = "09 Exits", Order = 1)]
        public int ProfitTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "StopLossTicks", GroupName = "09 Exits", Order = 2)]
        public int StopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "EmergencyExitTicks", GroupName = "09 Exits", Order = 3)]
        public int EmergencyExitTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 600)]
        [Display(Name = "TimeStopSeconds", GroupName = "09 Exits", Order = 4)]
        public int TimeStopSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 300)]
        [Display(Name = "EarlyFailureSeconds", GroupName = "09 Exits", Order = 5)]
        public int EarlyFailureSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "EarlyFailureMinProgressTicks", GroupName = "09 Exits", Order = 6)]
        public int EarlyFailureMinProgressTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 600)]
        [Display(Name = "CooldownSeconds", GroupName = "10 Governance", Order = 1)]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxTradesPerDay", GroupName = "10 Governance", Order = 2)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxEmergencyExits", GroupName = "10 Governance", Order = 3)]
        public int MaxEmergencyExits { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "SideLockoutMinutes", GroupName = "10 Governance", Order = 4)]
        public int SideLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "EmergencyFlipLockoutMinutes", GroupName = "10 Governance", Order = 5)]
        public int EmergencyFlipLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "AlternationLockoutMinutes", GroupName = "10 Governance", Order = 6)]
        public int AlternationLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "AlternatingEmergencyThreshold", GroupName = "10 Governance", Order = 7)]
        public int AlternatingEmergencyThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "PostExitTickLock", GroupName = "11 AntiRecursion", Order = 1)]
        public int PostExitTickLock { get; set; }

        [NinjaScriptProperty]
        [Range(0, 60000)]
        [Display(Name = "MinMillisecondsBetweenEntries", GroupName = "11 AntiRecursion", Order = 2)]
        public int MinMillisecondsBetweenEntries { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "PendingEntryMaxBars", GroupName = "11 AntiRecursion", Order = 3)]
        public int PendingEntryMaxBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BlockSameBarReentry", GroupName = "11 AntiRecursion", Order = 4)]
        public bool BlockSameBarReentry { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "CascadeEmergencyThreshold", GroupName = "12 Cascade", Order = 1)]
        public int CascadeEmergencyThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "CascadeWindowMinutes", GroupName = "12 Cascade", Order = 2)]
        public int CascadeWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "CascadeSuppressMinutes", GroupName = "12 Cascade", Order = 3)]
        public int CascadeSuppressMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "CascadeContinuationTicks", GroupName = "12 Cascade", Order = 4)]
        public int CascadeContinuationTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", GroupName = "13 Diagnostics", Order = 1)]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(50, 100000)]
        [Display(Name = "DiagnosticEveryTicks", GroupName = "13 Diagnostics", Order = 2)]
        public int DiagnosticEveryTicks { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_2_LIGHT_B6_1_Decel";
                Description = "MNQ LIGHT B6_1 hybrid trend/fade single series tick engine.";

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

                EnableTrendMode = true;
                SyntheticTrendSeconds = 60;
                TrendBarsRequired = 2;
                TrendMinMovePoints = 12.0;
                TrendCurrentBarMinPoints = 3.0;

                TrendPullbackTicks = 6;
                TrendResumeTicks = 2;
                TrendMaxPullbackTicks = 22;

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

                StallLookbackTicks = 6;
                LongNoFreshLowTicks = 4;
                ShortNoFreshHighTicks = 4;

                ProfitTargetTicks = 10;
                StopLossTicks = 8;
                EmergencyExitTicks = 7;
                TimeStopSeconds = 45;
                EarlyFailureSeconds = 15;
                EarlyFailureMinProgressTicks = 2;

                CooldownSeconds = 40;
                MaxTradesPerDay = 18;
                MaxEmergencyExits = 6;
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
                SetProfitTarget(FadeLongSignalName, CalculationMode.Ticks, ProfitTargetTicks);
                SetStopLoss(FadeLongSignalName, CalculationMode.Ticks, StopLossTicks, false);

                SetProfitTarget(FadeShortSignalName, CalculationMode.Ticks, ProfitTargetTicks);
                SetStopLoss(FadeShortSignalName, CalculationMode.Ticks, StopLossTicks, false);

                SetProfitTarget(TrendLongSignalName, CalculationMode.Ticks, ProfitTargetTicks);
                SetStopLoss(TrendLongSignalName, CalculationMode.Ticks, StopLossTicks, false);

                SetProfitTarget(TrendShortSignalName, CalculationMode.Ticks, ProfitTargetTicks);
                SetStopLoss(TrendShortSignalName, CalculationMode.Ticks, StopLossTicks, false);
            }
            else if (State == State.DataLoaded)
            {
                int maxLookback = Math.Max(Math.Max(ImpulseLookbackTicks, AccelerationLookbackTicks), StallLookbackTicks);
                recentPrices = new double[Math.Max(maxLookback + 64, 256)];
                recentIndex = 0;
                recentCount = 0;

                synthOpen = new double[16];
                synthHigh = new double[16];
                synthLow = new double[16];
                synthClose = new double[16];
                synthEndTime = new DateTime[16];

                if (PrintDiagnostics)
                    Print(Name + " loaded. Hybrid trend/fade. Use on MNQ 1 tick chart. One MNQ only.");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 2)
                return;

            exitObservedThisUpdate = false;
            ResetSessionIfNeeded();

            double px = Close[0];
            DateTime now = Time[0];

            ticksSeenToday++;
            PushRecentPrice(px);
            UpdateSyntheticTrendBars(px, now);
            UpdateAuctionMode(px, now);

            if (postExitTickLockRemaining > 0)
                postExitTickLockRemaining--;

            ExpireStalePendingEntry();
            ManageOpenPosition(px, now);

            if (Position.MarketPosition == MarketPosition.Flat)
                EvaluateEntries(px, now);

            MaybePrintDiagnostics(px, now);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null || execution.Order.OrderState != OrderState.Filled)
                return;

            string orderName = execution.Order.Name ?? string.Empty;

            if (IsEntrySignalName(orderName))
            {
                pendingEntry = false;
                pendingEntryBar = -1;

                entryTime = time;
                lastEntryTime = time;
                lastEntryBar = CurrentBar;
                entryPrice = price;
                highSinceEntry = price;
                lowSinceEntry = price;
                tradesToday++;

                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTRY {1} {2:F2} tradesToday={3}", time, orderName, price, tradesToday));

                return;
            }

            CommitExitLock(time, orderName, price);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null)
                return;

            string orderName = order.Name ?? string.Empty;

            if (IsEntrySignalName(orderName))
            {
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                {
                    pendingEntry = false;
                    pendingEntryBar = -1;

                    if (PrintDiagnostics)
                        Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTRY ORDER {1} {2}", time, orderName, orderState));
                }
            }
        }

        // --------------------------------------------------------------------
        // Synthetic trend proxy
        // --------------------------------------------------------------------

        private void UpdateSyntheticTrendBars(double px, DateTime now)
        {
            if (!syntheticActive)
            {
                currentSyntheticStart = AlignSyntheticStart(now);
                syntheticOpen = px;
                syntheticHigh = px;
                syntheticLow = px;
                syntheticClose = px;
                syntheticActive = true;
                return;
            }

            DateTime bucketStart = AlignSyntheticStart(now);

            if (bucketStart > currentSyntheticStart)
            {
                StoreCompletedSyntheticBar(currentSyntheticStart.AddSeconds(SyntheticTrendSeconds), syntheticOpen, syntheticHigh, syntheticLow, syntheticClose);

                currentSyntheticStart = bucketStart;
                syntheticOpen = px;
                syntheticHigh = px;
                syntheticLow = px;
                syntheticClose = px;
                return;
            }

            if (px > syntheticHigh)
                syntheticHigh = px;

            if (px < syntheticLow)
                syntheticLow = px;

            syntheticClose = px;
        }

        private DateTime AlignSyntheticStart(DateTime now)
        {
            int seconds = Math.Max(10, SyntheticTrendSeconds);
            long ticks = now.Ticks;
            long bucketTicks = TimeSpan.FromSeconds(seconds).Ticks;
            return new DateTime((ticks / bucketTicks) * bucketTicks, now.Kind);
        }

        private void StoreCompletedSyntheticBar(DateTime endTime, double o, double h, double l, double c)
        {
            synthOpen[synthIndex] = o;
            synthHigh[synthIndex] = h;
            synthLow[synthIndex] = l;
            synthClose[synthIndex] = c;
            synthEndTime[synthIndex] = endTime;

            synthIndex = (synthIndex + 1) % synthOpen.Length;
            synthCount = Math.Min(synthCount + 1, synthOpen.Length);
        }

        private void UpdateAuctionMode(double px, DateTime now)
        {
            AuctionMode prior = auctionMode;

            if (!EnableTrendMode || synthCount < TrendBarsRequired)
            {
                auctionMode = AuctionMode.None;
            }
            else if (IsUpTrendAuction(px))
            {
                auctionMode = AuctionMode.UpTrend;
            }
            else if (IsDownTrendAuction(px))
            {
                auctionMode = AuctionMode.DownTrend;
            }
            else
            {
                auctionMode = AuctionMode.None;
            }

            if (auctionMode != prior)
            {
                auctionModeSince = now;
                trendReferenceHigh = GetRecentHigh();
                trendReferenceLow = GetRecentLow();

                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} AUCTION MODE {1}", now, auctionMode));
            }

            if (auctionMode == AuctionMode.UpTrend)
            {
                if (px > trendReferenceHigh || trendReferenceHigh <= 0.0)
                    trendReferenceHigh = px;
            }
            else if (auctionMode == AuctionMode.DownTrend)
            {
                if (px < trendReferenceLow || trendReferenceLow <= 0.0)
                    trendReferenceLow = px;
            }
        }

        private bool IsUpTrendAuction(double px)
        {
            int greenBars = 0;
            double firstOpen = 0.0;
            double lastClose = 0.0;

            for (int i = TrendBarsRequired - 1; i >= 0; i--)
            {
                double o = GetSynthOpenByAge(i);
                double c = GetSynthCloseByAge(i);

                if (c > o)
                    greenBars++;

                if (i == TrendBarsRequired - 1)
                    firstOpen = o;

                if (i == 0)
                    lastClose = c;
            }

            double completedMove = lastClose - firstOpen;
            double currentMove = px - syntheticOpen;

            return greenBars >= TrendBarsRequired
                && completedMove >= TrendMinMovePoints
                && currentMove >= TrendCurrentBarMinPoints;
        }

        private bool IsDownTrendAuction(double px)
        {
            int redBars = 0;
            double firstOpen = 0.0;
            double lastClose = 0.0;

            for (int i = TrendBarsRequired - 1; i >= 0; i--)
            {
                double o = GetSynthOpenByAge(i);
                double c = GetSynthCloseByAge(i);

                if (c < o)
                    redBars++;

                if (i == TrendBarsRequired - 1)
                    firstOpen = o;

                if (i == 0)
                    lastClose = c;
            }

            double completedMove = firstOpen - lastClose;
            double currentMove = syntheticOpen - px;

            return redBars >= TrendBarsRequired
                && completedMove >= TrendMinMovePoints
                && currentMove >= TrendCurrentBarMinPoints;
        }

        private double GetSynthOpenByAge(int age)
        {
            int idx = synthIndex - 1 - age;
            while (idx < 0)
                idx += synthOpen.Length;

            return synthOpen[idx % synthOpen.Length];
        }

        private double GetSynthCloseByAge(int age)
        {
            int idx = synthIndex - 1 - age;
            while (idx < 0)
                idx += synthClose.Length;

            return synthClose[idx % synthClose.Length];
        }

        // --------------------------------------------------------------------
        // Entry engine
        // --------------------------------------------------------------------

        private void EvaluateEntries(double px, DateTime now)
        {
            // Hard one-contract/no-overlap enforcement.
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

            int minCount = Math.Max(Math.Max(ImpulseLookbackTicks, AccelerationLookbackTicks + 2), StallLookbackTicks + 2);
            if (recentCount < minCount)
                return;

            if (auctionMode == AuctionMode.UpTrend)
            {
                bool setup = IsTrendLongSetup(px);
                if (setup)
                    trendLongSetupsToday++;

                if (setup && CanEnterLong(now))
                {
                    SubmitLong(now, px, TrendLongSignalName);
                    return;
                }

                if (setup && !CanEnterLong(now))
                    longSuppressedToday++;

                // In a strong up auction, do not short fades.
                trendSuppressedFadeToday++;
                return;
            }

            if (auctionMode == AuctionMode.DownTrend)
            {
                bool setup = IsTrendShortSetup(px);
                if (setup)
                    trendShortSetupsToday++;

                if (setup && CanEnterShort(now))
                {
                    SubmitShort(now, px, TrendShortSignalName);
                    return;
                }

                if (setup && !CanEnterShort(now))
                    shortSuppressedToday++;

                // In a strong down auction, do not buy fades.
                trendSuppressedFadeToday++;
                return;
            }

            // No trend auction active: allow original micro-fade engine.
            bool longFade = IsFadeLongSetup(px);
            bool shortFade = IsFadeShortSetup(px);

            if (longFade)
                fadeLongSetupsToday++;

            if (shortFade)
                fadeShortSetupsToday++;

            if (longFade && CanEnterLong(now))
            {
                SubmitLong(now, px, FadeLongSignalName);
                return;
            }

            if (shortFade && CanEnterShort(now))
            {
                SubmitShort(now, px, FadeShortSignalName);
                return;
            }

            if (longFade && !CanEnterLong(now))
                longSuppressedToday++;

            if (shortFade && !CanEnterShort(now))
                shortSuppressedToday++;
        }

        private bool IsTrendLongSetup(double px)
        {
            if (trendReferenceHigh <= 0.0)
                trendReferenceHigh = GetRecentHigh();

            int pullbackTicks = ToTicks(trendReferenceHigh - px);
            int resumeTicks = ToTicks(px - GetRecentLow());

            bool pullbackOk = pullbackTicks >= TrendPullbackTicks && pullbackTicks <= TrendMaxPullbackTicks;
            bool resumeOk = resumeTicks >= TrendResumeTicks;
            bool turnOk = !RequireTurnTick || (recentCount >= 2 && px > GetRecentPriceByAge(1));
            bool notFreshLow = HasNoFreshLow(LongNoFreshLowTicks);

            return pullbackOk && resumeOk && turnOk && notFreshLow;
        }

        private bool IsTrendShortSetup(double px)
        {
            if (trendReferenceLow <= 0.0)
                trendReferenceLow = GetRecentLow();

            int pullbackTicks = ToTicks(px - trendReferenceLow);
            int resumeTicks = ToTicks(GetRecentHigh() - px);

            bool pullbackOk = pullbackTicks >= TrendPullbackTicks && pullbackTicks <= TrendMaxPullbackTicks;
            bool resumeOk = resumeTicks >= TrendResumeTicks;
            bool turnOk = !RequireTurnTick || (recentCount >= 2 && px < GetRecentPriceByAge(1));
            bool notFreshHigh = HasNoFreshHigh(ShortNoFreshHighTicks);

            return pullbackOk && resumeOk && turnOk && notFreshHigh;
        }

        private bool IsFadeLongSetup(double px)
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

            bool stallOk = HasNoFreshLow(LongNoFreshLowTicks);
            if (!stallOk)
                longStallBlockedToday++;

            if (acceleratingDown)
                longAccelBlockedToday++;

            return impulseTicks >= LongMinImpulseTicks
                && reclaimTicks >= LongReclaimTicks
                && reclaimTicks >= LongConfirmTicksFromLow
                && ticksAfterLow >= LongMinTicksAfterLow
                && stallOk
                && turnOk
                && notTooLate
                && !acceleratingDown;
        }

        private bool IsFadeShortSetup(double px)
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

            bool stallOk = HasNoFreshHigh(ShortNoFreshHighTicks);
            if (!stallOk)
                shortStallBlockedToday++;

            if (acceleratingUp)
                shortAccelBlockedToday++;

            return impulseTicks >= ShortMinImpulseTicks
                && rejectTicks >= ShortRejectTicks
                && rejectTicks >= ShortConfirmTicksFromHigh
                && ticksAfterHigh >= ShortMinTicksAfterHigh
                && stallOk
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
            if (exitObservedThisUpdate)
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

        private void SubmitLong(DateTime now, double px, string signalName)
        {
            if (Position.MarketPosition != MarketPosition.Flat || pendingEntry)
                return;

            pendingEntry = true;
            pendingEntryBar = CurrentBar;
            lastEntryBar = CurrentBar;
            lastEntryTime = now;
            submittedLongsToday++;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} SUBMIT LONG {1} px={2:F2}", now, signalName, px));

            EnterLong(1, signalName);
        }

        private void SubmitShort(DateTime now, double px, string signalName)
        {
            if (Position.MarketPosition != MarketPosition.Flat || pendingEntry)
                return;

            pendingEntry = true;
            pendingEntryBar = CurrentBar;
            lastEntryBar = CurrentBar;
            lastEntryTime = now;
            submittedShortsToday++;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} SUBMIT SHORT {1} px={2:F2}", now, signalName, px));

            EnterShort(1, signalName);
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

            double heldSeconds = entryTime == Core.Globals.MinDate ? 0.0 : Math.Max(0.0, (now - entryTime).TotalSeconds);

            if (Position.MarketPosition == MarketPosition.Long)
            {
                int adverseTicks = ToTicks(entryPrice - px);
                int favorableTicks = ToTicks(highSinceEntry - entryPrice);

                if (adverseTicks >= EmergencyExitTicks)
                {
                    RegisterEmergencyExit(true, now, px);
                    ExitLong("B61_Emergency_Long");
                    return;
                }

                if (heldSeconds >= TimeStopSeconds)
                {
                    CommitExitIntent(now, "B61_TimeStop_Long", px);
                    ExitLong("B61_TimeStop_Long");
                    return;
                }

                if (heldSeconds >= EarlyFailureSeconds && favorableTicks < EarlyFailureMinProgressTicks)
                {
                    CommitExitIntent(now, "B61_EarlyFailure_Long", px);
                    ExitLong("B61_EarlyFailure_Long");
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
                    ExitShort("B61_Emergency_Short");
                    return;
                }

                if (heldSeconds >= TimeStopSeconds)
                {
                    CommitExitIntent(now, "B61_TimeStop_Short", px);
                    ExitShort("B61_TimeStop_Short");
                    return;
                }

                if (heldSeconds >= EarlyFailureSeconds && favorableTicks < EarlyFailureMinProgressTicks)
                {
                    CommitExitIntent(now, "B61_EarlyFailure_Short", px);
                    ExitShort("B61_EarlyFailure_Short");
                    return;
                }
            }
        }

        private void CommitExitIntent(DateTime now, string reason, double px)
        {
            lastExitTime = now;
            lastExitBar = CurrentBar;
            postExitTickLockRemaining = Math.Max(postExitTickLockRemaining, PostExitTickLock);
            exitObservedThisUpdate = true;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EXIT INTENT {1} px={2:F2}", now, reason, px));
        }

        private void CommitExitLock(DateTime time, string orderName, double price)
        {
            lastExitTime = time;
            lastExitBar = CurrentBar;
            postExitTickLockRemaining = Math.Max(postExitTickLockRemaining, PostExitTickLock);
            exitObservedThisUpdate = true;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EXIT {1} {2:F2} cooldownUntil={3:HH:mm:ss}", time, orderName, price, time.AddSeconds(CooldownSeconds)));
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
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EMERGENCY {1} px={2:F2} emergencyToday={3}", now, thisSide, px, emergencyExitsToday));
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
        // Rolling helpers
        // --------------------------------------------------------------------

        private bool IsEntrySignalName(string orderName)
        {
            return orderName == FadeLongSignalName
                || orderName == FadeShortSignalName
                || orderName == TrendLongSignalName
                || orderName == TrendShortSignalName;
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

        private bool HasNoFreshLow(int barsBack)
        {
            int n = Math.Min(barsBack, recentCount);
            if (n <= 1)
                return false;

            double current = GetRecentPriceByAge(0);
            double priorMin = double.MaxValue;

            for (int age = 1; age < n; age++)
            {
                double v = GetRecentPriceByAge(age);
                if (v < priorMin)
                    priorMin = v;
            }

            return current > priorMin;
        }

        private bool HasNoFreshHigh(int barsBack)
        {
            int n = Math.Min(barsBack, recentCount);
            if (n <= 1)
                return false;

            double current = GetRecentPriceByAge(0);
            double priorMax = double.MinValue;

            for (int age = 1; age < n; age++)
            {
                double v = GetRecentPriceByAge(age);
                if (v > priorMax)
                    priorMax = v;
            }

            return current < priorMax;
        }

        private double GetRecentPriceByAge(int age)
        {
            int idx = recentIndex - 1 - age;
            while (idx < 0)
                idx += recentPrices.Length;

            return recentPrices[idx % recentPrices.Length];
        }

        // --------------------------------------------------------------------
        // Governance / session / diagnostics
        // --------------------------------------------------------------------

        private void ExpireStalePendingEntry()
        {
            if (!pendingEntry || pendingEntryBar < 0)
                return;

            if (CurrentBar - pendingEntryBar > PendingEntryMaxBars)
            {
                pendingEntry = false;
                pendingEntryBar = -1;

                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} CLEAR STALE PENDING ENTRY", Time[0]));
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

            postExitTickLockRemaining = 0;
            exitObservedThisUpdate = false;

            longLockoutUntil = Core.Globals.MinDate;
            shortLockoutUntil = Core.Globals.MinDate;
            longCascadeSuppressUntil = Core.Globals.MinDate;
            shortCascadeSuppressUntil = Core.Globals.MinDate;
            anyEmergencyLockoutUntil = Core.Globals.MinDate;

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
            lastEmergencySide = string.Empty;
            alternatingEmergencyCount = 0;

            syntheticActive = false;
            currentSyntheticStart = Core.Globals.MinDate;
            synthIndex = 0;
            synthCount = 0;
            auctionMode = AuctionMode.None;
            auctionModeSince = Core.Globals.MinDate;
            trendReferenceHigh = 0.0;
            trendReferenceLow = 0.0;

            ticksSeenToday = 0;
            fadeLongSetupsToday = 0;
            fadeShortSetupsToday = 0;
            trendLongSetupsToday = 0;
            trendShortSetupsToday = 0;
            longAccelBlockedToday = 0;
            shortAccelBlockedToday = 0;
            longStallBlockedToday = 0;
            shortStallBlockedToday = 0;
            sessionBlockedToday = 0;
            cooldownBlockedToday = 0;
            governanceBlockedToday = 0;
            antiRecursionBlockedToday = 0;
            pendingEntryBlockedToday = 0;
            longSuppressedToday = 0;
            shortSuppressedToday = 0;
            flipLockBlockedToday = 0;
            trendSuppressedFadeToday = 0;
            submittedLongsToday = 0;
            submittedShortsToday = 0;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} NEW SESSION", Time[0]));
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
                "{0:yyyy-MM-dd HH:mm:ss} DIAG ticks={1} px={2:F2} mode={3} pos={4} trades={5} subL={6} subS={7} TL={8} TS={9} FL={10} FS={11} sessBlk={12} coolBlk={13} govBlk={14} antiRecBlk={15} trendSuppress={16}",
                now,
                ticksSeenToday,
                px,
                auctionMode,
                Position.MarketPosition,
                tradesToday,
                submittedLongsToday,
                submittedShortsToday,
                trendLongSetupsToday,
                trendShortSetupsToday,
                fadeLongSetupsToday,
                fadeShortSetupsToday,
                sessionBlockedToday,
                cooldownBlockedToday,
                governanceBlockedToday,
                antiRecursionBlockedToday,
                trendSuppressedFadeToday));
        }

        private void PrintDailySummary(DateTime now)
        {
            Print("------------------------------------------------------------");
            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} B61 HYBRID DAILY SUMMARY", now));
            Print(string.Format("ticksSeen={0} trades={1} emergencies={2}", ticksSeenToday, tradesToday, emergencyExitsToday));
            Print(string.Format("submittedLongs={0} submittedShorts={1}", submittedLongsToday, submittedShortsToday));
            Print(string.Format("trendLongSetups={0} trendShortSetups={1}", trendLongSetupsToday, trendShortSetupsToday));
            Print(string.Format("fadeLongSetups={0} fadeShortSetups={1}", fadeLongSetupsToday, fadeShortSetupsToday));
            Print(string.Format("trendSuppressedFade={0}", trendSuppressedFadeToday));
            Print(string.Format("sessionBlocked={0} cooldownBlocked={1} governanceBlocked={2}", sessionBlockedToday, cooldownBlockedToday, governanceBlockedToday));
            Print(string.Format("antiRecursionBlocked={0} pendingEntryBlocked={1} flipLockBlocked={2}", antiRecursionBlockedToday, pendingEntryBlockedToday, flipLockBlockedToday));
            Print(string.Format("longSuppressed={0} shortSuppressed={1}", longSuppressedToday, shortSuppressedToday));
            Print("------------------------------------------------------------");
        }
    }
}
