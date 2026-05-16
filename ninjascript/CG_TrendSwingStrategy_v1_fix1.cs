#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// CG_TrendSwingStrategy_v1
    ///
    /// Methodology:
    /// 1) Detect confirmed swing highs/lows on a secondary timeframe.
    /// 2) Determine higher-timeframe trend regime using an EMA on the primary series.
    /// 3) In bull regime, only arm LONG pullback entries from confirmed swing lows.
    /// 4) In bear regime, only arm SHORT pullback entries from confirmed swing highs.
    /// 5) Use an initial fixed stop, move to breakeven after a threshold, then trail.
    ///
    /// This is intentionally a trend-participation variant, not a pure fade strategy.
    /// It is designed to avoid repeatedly shorting a strong bull day and to give
    /// winners room to run instead of hard-capping them too early.
    /// </summary>
    public class CG_TrendSwingStrategy_v1 : Strategy
    {
        // ---------- Swing / signal state ----------
        private int swingDetectionBars = 3;

        private double lastConfirmedSwingHigh = 0;
        private double lastConfirmedSwingLow = 0;
        private DateTime lastConfirmedSwingHighTime = Core.Globals.MinDate;
        private DateTime lastConfirmedSwingLowTime = Core.Globals.MinDate;

        private bool pendingLongSignal = false;
        private bool pendingShortSignal = false;
        private int pendingLongArmedPrimaryBar = -1;
        private int pendingShortArmedPrimaryBar = -1;
        private double pendingLongSwingPrice = 0;
        private double pendingShortSwingPrice = 0;

        // ---------- Position state ----------
        private string activeEntrySignal = string.Empty;
        private double trackedEntryPrice = 0;
        private bool movedToBreakeven = false;
        private double bestPriceSinceEntry = 0;

        // ---------- Indicators ----------
        private EMA regimeEmaFast;
        private EMA regimeEmaSlow;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                    = @"Trend-following swing strategy with regime filter and trailing runner.";
                Name                                           = "CG_TrendSwingStrategy_v1";
                Calculate                                      = Calculate.OnBarClose;
                EntriesPerDirection                            = 1;
                EntryHandling                                  = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy                   = true;
                ExitOnSessionCloseSeconds                      = 30;
                IsFillLimitOnTouch                             = false;
                MaximumBarsLookBack                            = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution                            = OrderFillResolution.Standard;
                Slippage                                       = 0;
                StartBehavior                                  = StartBehavior.WaitUntilFlat;
                TimeInForce                                    = TimeInForce.Gtc;
                TraceOrders                                    = false;
                RealtimeErrorHandling                          = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling                             = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade                            = 50;
                IsInstantiatedOnEachOptimizationIteration      = true;

                // Core sizing / timing
                PositionSize                                   = 1;
                SwingWindowMinutes                             = 15;
                EntryWindowBars                                = 3;

                // Swing quality
                MinSwingStrength                               = 20;

                // Regime filter
                RegimeFastEmaPeriod                            = 20;
                RegimeSlowEmaPeriod                            = 50;
                RegimeDistanceTicks                            = 8;

                // Pullback entry model
                RequirePullbackTouch                           = true;
                PullbackBufferTicks                            = 16;
                ReclaimBufferTicks                             = 4;

                // Risk management
                InitialStopTicks                               = 20;
                BreakevenTriggerTicks                          = 40;
                TrailStartTicks                                = 80;
                TrailDistanceTicks                             = 40;

                // Optional guardrail
                UseTrendStrengthGuard                          = true;
                MinSlopeTicks                                  = 6;

                // RTH only by default
                RTHStartHour                                   = 9;
                RTHStartMinute                                 = 30;
                RTHEndHour                                     = 16;
                RTHEndMinute                                   = 0;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, SwingWindowMinutes);
            }
            else if (State == State.DataLoaded)
            {
                regimeEmaFast = EMA(Close, RegimeFastEmaPeriod);
                regimeEmaSlow = EMA(Close, RegimeSlowEmaPeriod);
            }
        }

        protected override void OnBarUpdate()
        {
            // Secondary series: detect confirmed swings
            if (BarsInProgress == 1)
            {
                if (CurrentBars[1] >= swingDetectionBars * 2)
                    DetectAndArmSwingSignals();
                return;
            }

            if (BarsInProgress != 0)
                return;

            if (CurrentBar < Math.Max(BarsRequiredToTrade, RegimeSlowEmaPeriod + 5))
                return;

            // Expire stale armed signals
            ExpirePendingSignals();

            if (Position.MarketPosition == MarketPosition.Flat)
                CheckForTrendEntry();
            else
                ManageTrendPosition();
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            // Entry fill
            if (execution.Order.OrderState == OrderState.Filled &&
                (execution.Order.Name == "TrendLong" || execution.Order.Name == "TrendShort") &&
                marketPosition != MarketPosition.Flat)
            {
                activeEntrySignal = execution.Order.Name;
                trackedEntryPrice = execution.Price;
                movedToBreakeven = false;
                bestPriceSinceEntry = execution.Price;

                Print(string.Format("{0} | ENTRY {1} @ {2:F2}",
                    time.ToString("yyyy-MM-dd HH:mm:ss"),
                    activeEntrySignal,
                    execution.Price));
            }
            // Exit fill
            else if (execution.Order.OrderState == OrderState.Filled && marketPosition == MarketPosition.Flat)
            {
                double pnlTicks = 0;

                if (trackedEntryPrice > 0)
                {
                    if (activeEntrySignal == "TrendShort")
                        pnlTicks = (trackedEntryPrice - execution.Price) / TickSize;
                    else if (activeEntrySignal == "TrendLong")
                        pnlTicks = (execution.Price - trackedEntryPrice) / TickSize;
                }

                Print(string.Format("{0} | EXIT {1} @ {2:F2} pnl={3:F1}T via {4}",
                    time.ToString("yyyy-MM-dd HH:mm:ss"),
                    activeEntrySignal,
                    execution.Price,
                    pnlTicks,
                    execution.Order.Name));

                ResetPositionState();
            }
        }

        private void DetectAndArmSwingSignals()
        {
            if (CurrentBars[1] < swingDetectionBars * 2)
                return;

            int barsAgo = swingDetectionBars;
            double candidateHigh = Highs[1][barsAgo];
            double candidateLow = Lows[1][barsAgo];

            bool isSwingHigh = true;
            bool isSwingLow = true;

            for (int i = 0; i < swingDetectionBars * 2 + 1; i++)
            {
                if (i == barsAgo)
                    continue;

                if (Highs[1][i] >= candidateHigh)
                    isSwingHigh = false;

                if (Lows[1][i] <= candidateLow)
                    isSwingLow = false;
            }

            if (isSwingHigh)
            {
                double windowLow = Lows[1][barsAgo];
                for (int i = 0; i < swingDetectionBars * 2 + 1; i++)
                    windowLow = Math.Min(windowLow, Lows[1][i]);

                double swingRangeTicks = (candidateHigh - windowLow) / TickSize;

                if (swingRangeTicks >= MinSwingStrength && candidateHigh != lastConfirmedSwingHigh)
                {
                    lastConfirmedSwingHigh = candidateHigh;
                    lastConfirmedSwingHighTime = Times[1][0];

                    if (Position.MarketPosition == MarketPosition.Flat)
                    {
                        pendingShortSignal = true;
                        pendingShortArmedPrimaryBar = CurrentBar;
                        pendingShortSwingPrice = candidateHigh;
                    }

                    Print(string.Format("{0} | SWING HIGH confirmed @ {1:F2} range={2:F1}T",
                        Times[1][0].ToString("yyyy-MM-dd HH:mm:ss"),
                        candidateHigh,
                        swingRangeTicks));
                }
            }

            if (isSwingLow)
            {
                double windowHigh = Highs[1][barsAgo];
                for (int i = 0; i < swingDetectionBars * 2 + 1; i++)
                    windowHigh = Math.Max(windowHigh, Highs[1][i]);

                double swingRangeTicks = (windowHigh - candidateLow) / TickSize;

                if (swingRangeTicks >= MinSwingStrength && candidateLow != lastConfirmedSwingLow)
                {
                    lastConfirmedSwingLow = candidateLow;
                    lastConfirmedSwingLowTime = Times[1][0];

                    if (Position.MarketPosition == MarketPosition.Flat)
                    {
                        pendingLongSignal = true;
                        pendingLongArmedPrimaryBar = CurrentBar;
                        pendingLongSwingPrice = candidateLow;
                    }

                    Print(string.Format("{0} | SWING LOW confirmed @ {1:F2} range={2:F1}T",
                        Times[1][0].ToString("yyyy-MM-dd HH:mm:ss"),
                        candidateLow,
                        swingRangeTicks));
                }
            }
        }

        private void CheckForTrendEntry()
        {
            if (!IsRTH())
                return;

            bool bullRegime = IsBullRegime();
            bool bearRegime = IsBearRegime();

            // Bull trend: buy pullback/reclaim from a confirmed swing low.
            if (bullRegime && pendingLongSignal)
            {
                int barsSinceArm = CurrentBar - pendingLongArmedPrimaryBar;

                bool touchedPullbackZone = !RequirePullbackTouch
                    || Low[0] <= pendingLongSwingPrice + PullbackBufferTicks * TickSize;

                bool reclaimed = Close[0] >= pendingLongSwingPrice + ReclaimBufferTicks * TickSize;

                if (barsSinceArm >= 0 && barsSinceArm <= EntryWindowBars && touchedPullbackZone && reclaimed)
                {
                    SetStopLoss("TrendLong", CalculationMode.Ticks, InitialStopTicks, false);
                    EnterLong(PositionSize, "TrendLong");

                    Print(string.Format("{0} | EnterLong from swingLow={1:F2} barsSinceArm={2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        pendingLongSwingPrice,
                        barsSinceArm));

                    pendingLongSignal = false;
                    pendingLongArmedPrimaryBar = -1;
                    pendingLongSwingPrice = 0;
                }
            }

            // Bear trend: sell pullback/reclaim from a confirmed swing high.
            if (bearRegime && pendingShortSignal)
            {
                int barsSinceArm = CurrentBar - pendingShortArmedPrimaryBar;

                bool touchedPullbackZone = !RequirePullbackTouch
                    || High[0] >= pendingShortSwingPrice - PullbackBufferTicks * TickSize;

                bool reclaimed = Close[0] <= pendingShortSwingPrice - ReclaimBufferTicks * TickSize;

                if (barsSinceArm >= 0 && barsSinceArm <= EntryWindowBars && touchedPullbackZone && reclaimed)
                {
                    SetStopLoss("TrendShort", CalculationMode.Ticks, InitialStopTicks, false);
                    EnterShort(PositionSize, "TrendShort");

                    Print(string.Format("{0} | EnterShort from swingHigh={1:F2} barsSinceArm={2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        pendingShortSwingPrice,
                        barsSinceArm));

                    pendingShortSignal = false;
                    pendingShortArmedPrimaryBar = -1;
                    pendingShortSwingPrice = 0;
                }
            }
        }

        private void ManageTrendPosition()
        {
            if (trackedEntryPrice == 0)
                trackedEntryPrice = Position.AveragePrice;

            if (Position.MarketPosition == MarketPosition.Long)
            {
                bestPriceSinceEntry = Math.Max(bestPriceSinceEntry, High[0]);
                double profitTicks = (Close[0] - trackedEntryPrice) / TickSize;

                if (!movedToBreakeven && profitTicks >= BreakevenTriggerTicks)
                {
                    SetStopLoss("TrendLong", CalculationMode.Price, trackedEntryPrice, false);
                    movedToBreakeven = true;

                    Print(string.Format("{0} | LONG breakeven armed @ {1:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        trackedEntryPrice));
                }

                if (profitTicks >= TrailStartTicks)
                {
                    double trailStop = bestPriceSinceEntry - TrailDistanceTicks * TickSize;
                    double effectiveStop = Math.Max(trackedEntryPrice, trailStop);

                    SetStopLoss("TrendLong", CalculationMode.Price, effectiveStop, false);

                    Print(string.Format("{0} | LONG trail stop -> {1:F2} best={2:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        effectiveStop,
                        bestPriceSinceEntry));
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                bestPriceSinceEntry = (bestPriceSinceEntry == 0)
                    ? Low[0]
                    : Math.Min(bestPriceSinceEntry, Low[0]);

                double profitTicks = (trackedEntryPrice - Close[0]) / TickSize;

                if (!movedToBreakeven && profitTicks >= BreakevenTriggerTicks)
                {
                    SetStopLoss("TrendShort", CalculationMode.Price, trackedEntryPrice, false);
                    movedToBreakeven = true;

                    Print(string.Format("{0} | SHORT breakeven armed @ {1:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        trackedEntryPrice));
                }

                if (profitTicks >= TrailStartTicks)
                {
                    double trailStop = bestPriceSinceEntry + TrailDistanceTicks * TickSize;
                    double effectiveStop = Math.Min(trackedEntryPrice, trailStop);

                    SetStopLoss("TrendShort", CalculationMode.Price, effectiveStop, false);

                    Print(string.Format("{0} | SHORT trail stop -> {1:F2} best={2:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        effectiveStop,
                        bestPriceSinceEntry));
                }
            }
        }

        private void ExpirePendingSignals()
        {
            if (pendingLongSignal && pendingLongArmedPrimaryBar >= 0)
            {
                if (CurrentBar - pendingLongArmedPrimaryBar > EntryWindowBars)
                {
                    Print(string.Format("{0} | Expiring LONG signal from swingLow={1:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        pendingLongSwingPrice));

                    pendingLongSignal = false;
                    pendingLongArmedPrimaryBar = -1;
                    pendingLongSwingPrice = 0;
                }
            }

            if (pendingShortSignal && pendingShortArmedPrimaryBar >= 0)
            {
                if (CurrentBar - pendingShortArmedPrimaryBar > EntryWindowBars)
                {
                    Print(string.Format("{0} | Expiring SHORT signal from swingHigh={1:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        pendingShortSwingPrice));

                    pendingShortSignal = false;
                    pendingShortArmedPrimaryBar = -1;
                    pendingShortSwingPrice = 0;
                }
            }
        }

        private bool IsBullRegime()
        {
            if (CurrentBar < Math.Max(RegimeFastEmaPeriod, RegimeSlowEmaPeriod) + 2)
                return false;

            bool aligned = regimeEmaFast[0] > regimeEmaSlow[0];
            bool closeAbove = Close[0] > regimeEmaFast[0];
            bool separated = (regimeEmaFast[0] - regimeEmaSlow[0]) / TickSize >= RegimeDistanceTicks;

            bool slopeOk = true;
            if (UseTrendStrengthGuard && CurrentBar > 5)
                slopeOk = (regimeEmaFast[0] - regimeEmaFast[5]) / TickSize >= MinSlopeTicks;

            return aligned && closeAbove && separated && slopeOk;
        }

        private bool IsBearRegime()
        {
            if (CurrentBar < Math.Max(RegimeFastEmaPeriod, RegimeSlowEmaPeriod) + 2)
                return false;

            bool aligned = regimeEmaFast[0] < regimeEmaSlow[0];
            bool closeBelow = Close[0] < regimeEmaFast[0];
            bool separated = (regimeEmaSlow[0] - regimeEmaFast[0]) / TickSize >= RegimeDistanceTicks;

            bool slopeOk = true;
            if (UseTrendStrengthGuard && CurrentBar > 5)
                slopeOk = (regimeEmaFast[5] - regimeEmaFast[0]) / TickSize >= MinSlopeTicks;

            return aligned && closeBelow && separated && slopeOk;
        }

        private bool IsRTH()
        {
            TimeSpan currentTime = Time[0].TimeOfDay;
            TimeSpan rthStart = new TimeSpan(RTHStartHour, RTHStartMinute, 0);
            TimeSpan rthEnd = new TimeSpan(RTHEndHour, RTHEndMinute, 0);
            return currentTime >= rthStart && currentTime < rthEnd;
        }

        private void ResetPositionState()
        {
            activeEntrySignal = string.Empty;
            trackedEntryPrice = 0;
            movedToBreakeven = false;
            bestPriceSinceEntry = 0;
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Position Size", Order=1, GroupName="Parameters")]
        public int PositionSize { get; set; }

        [NinjaScriptProperty]
        [Range(5, 60)]
        [Display(Name="Swing Window (Minutes)", Order=2, GroupName="Parameters")]
        public int SwingWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="Entry Window Bars", Order=3, GroupName="Parameters")]
        public int EntryWindowBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="Min Swing Strength (Ticks)", Order=4, GroupName="Parameters")]
        public int MinSwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name="Regime Fast EMA", Order=5, GroupName="Parameters")]
        public int RegimeFastEmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(3, 400)]
        [Display(Name="Regime Slow EMA", Order=6, GroupName="Parameters")]
        public int RegimeSlowEmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name="Regime Distance (Ticks)", Order=7, GroupName="Parameters")]
        public int RegimeDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Require Pullback Touch", Order=8, GroupName="Parameters")]
        public bool RequirePullbackTouch { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="Pullback Buffer (Ticks)", Order=9, GroupName="Parameters")]
        public int PullbackBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name="Reclaim Buffer (Ticks)", Order=10, GroupName="Parameters")]
        public int ReclaimBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="Initial Stop (Ticks)", Order=11, GroupName="Parameters")]
        public int InitialStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="Breakeven Trigger (Ticks)", Order=12, GroupName="Parameters")]
        public int BreakevenTriggerTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 400)]
        [Display(Name="Trail Start (Ticks)", Order=13, GroupName="Parameters")]
        public int TrailStartTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 400)]
        [Display(Name="Trail Distance (Ticks)", Order=14, GroupName="Parameters")]
        public int TrailDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Use Trend Strength Guard", Order=15, GroupName="Parameters")]
        public bool UseTrendStrengthGuard { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name="Min Slope (Ticks)", Order=16, GroupName="Parameters")]
        public int MinSlopeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name="RTH Start Hour", Order=17, GroupName="Parameters")]
        public int RTHStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name="RTH Start Minute", Order=18, GroupName="Parameters")]
        public int RTHStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name="RTH End Hour", Order=19, GroupName="Parameters")]
        public int RTHEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name="RTH End Minute", Order=20, GroupName="Parameters")]
        public int RTHEndMinute { get; set; }
        #endregion
    }
}
