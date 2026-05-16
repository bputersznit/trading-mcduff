#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_NoTrailTrendStrategy_QUIET : Strategy
    {
        // Strategy / methodology notes:
        // - Primary series (BarsInProgress == 0) is the execution series.
        // - Secondary 5-minute series (BarsInProgress == 1) is used for swing detection.
        // - We store actual swing timestamps from the 5-minute series instead of mixing bar indexes
        //   across series. This avoids false "bars since swing" calculations.
        // - Swing signals are consumed once used so stale swings are not re-entered repeatedly.
        // - Stop handling uses the named entry signal for consistency.

        private int entryBar = -1;
        private double entryPrice = 0;
        private bool trendModeActive = false;
        private int swingDetectionBars = 3;

        private double lastRecordedSwingHigh = 0;
        private double lastRecordedSwingLow = 0;
        private DateTime lastRecordedSwingHighTime = Core.Globals.MinDate;
        private DateTime lastRecordedSwingLowTime = Core.Globals.MinDate;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                 = @"Fixed stop loss version with corrected multi-series swing tracking";
                Name                                        = "CG_NoTrailTrendStrategy_QUIET";
                Calculate                                   = Calculate.OnBarClose;
                EntriesPerDirection                         = 1;
                EntryHandling                               = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy                = true;
                ExitOnSessionCloseSeconds                   = 30;
                IsFillLimitOnTouch                          = false;
                MaximumBarsLookBack                         = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution                         = OrderFillResolution.Standard;
                Slippage                                    = 0;
                StartBehavior                               = StartBehavior.WaitUntilFlat;
                TimeInForce                                 = TimeInForce.Gtc;
                TraceOrders                                 = false;
                RealtimeErrorHandling                       = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling                          = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade                         = 10;
                IsInstantiatedOnEachOptimizationIteration   = true;

                PositionSize        = 1;
                SwingWindowMinutes  = 15;
                MinSwingStrength    = 20;
                FirstTarget         = 40;
                ExtendedTarget      = 80;
                InitialStop         = 20;
                RTHStartHour        = 9;
                RTHStartMinute      = 30;
                RTHEndHour          = 16;
                RTHEndMinute        = 0;
            }
            else if (State == State.Configure)
            {
                // User parameter kept at 5/10/15/etc. minutes for flexibility.
                AddDataSeries(BarsPeriodType.Minute, SwingWindowMinutes);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < BarsRequiredToTrade)
                return;

            if (BarsInProgress == 1)
            {
                if (CurrentBars[1] >= swingDetectionBars * 2)
                    DetectSwingPoints();
                return;
            }

            if (BarsInProgress != 0)
                return;

            if (Position.MarketPosition == MarketPosition.Flat)
                CheckForEntry();
            else
                ManagePosition();
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

            if ((execution.Order.Name == "SwingHigh" || execution.Order.Name == "SwingLow")
                && execution.Order.OrderState == OrderState.Filled)
            {
                entryPrice = execution.Price;
                trendModeActive = false;
                entryBar = CurrentBar;

                Print(string.Format(
                    "{0} | {1} {2}",
                    time.ToString("HH:mm:ss"),
                    execution.Order.Name == "SwingHigh" ? "SHORT" : "LONG",
                    execution.Price));
            }
            else if (execution.Order.OrderState == OrderState.Filled && marketPosition == MarketPosition.Flat)
            {
                double pnlTicks = 0;

                if (entryPrice > 0)
                {
                    if (execution.Order.Name != null && execution.Order.Name.Contains("Short"))
                        pnlTicks = (entryPrice - execution.Price) / TickSize;
                    else
                        pnlTicks = (execution.Price - entryPrice) / TickSize;

                    Print(string.Format("{0} | EXIT {1:F1}T", time.ToString("HH:mm:ss"), pnlTicks));
                }

                ResetState();
            }
        }

        private bool IsRTH()
        {
            TimeSpan currentTime = Time[0].TimeOfDay;
            TimeSpan rthStart = new TimeSpan(RTHStartHour, RTHStartMinute, 0);
            TimeSpan rthEnd = new TimeSpan(RTHEndHour, RTHEndMinute, 0);
            return currentTime >= rthStart && currentTime < rthEnd;
        }

        private void DetectSwingPoints()
        {
            if (CurrentBars[1] < swingDetectionBars * 2)
                return;

            int barsAgo = swingDetectionBars;
            double currentHigh = Highs[1][barsAgo];
            double currentLow = Lows[1][barsAgo];

            bool isSwingHigh = true;
            bool isSwingLow = true;

            for (int i = 0; i < swingDetectionBars * 2 + 1; i++)
            {
                if (i == barsAgo)
                    continue;

                if (Highs[1][i] >= currentHigh)
                    isSwingHigh = false;

                if (Lows[1][i] <= currentLow)
                    isSwingLow = false;
            }

            if (isSwingHigh)
            {
                double windowLow = Lows[1][barsAgo];
                for (int i = 0; i < swingDetectionBars * 2 + 1; i++)
                    windowLow = Math.Min(windowLow, Lows[1][i]);

                double swingRange = (currentHigh - windowLow) / TickSize;

                if (swingRange >= MinSwingStrength && currentHigh != lastRecordedSwingHigh)
                {
                    lastRecordedSwingHigh = currentHigh;
                    lastRecordedSwingHighTime = Times[1][barsAgo];

                    Print(string.Format(
                        "{0} | SWING HIGH detected @ {1:F2} range={2:F1}T",
                        Times[1][0].ToString("yyyy-MM-dd HH:mm:ss"),
                        currentHigh,
                        swingRange));
                }
            }

            if (isSwingLow)
            {
                double windowHigh = Highs[1][barsAgo];
                for (int i = 0; i < swingDetectionBars * 2 + 1; i++)
                    windowHigh = Math.Max(windowHigh, Highs[1][i]);

                double swingRange = (windowHigh - currentLow) / TickSize;

                if (swingRange >= MinSwingStrength && currentLow != lastRecordedSwingLow)
                {
                    lastRecordedSwingLow = currentLow;
                    lastRecordedSwingLowTime = Times[1][barsAgo];

                    Print(string.Format(
                        "{0} | SWING LOW detected @ {1:F2} range={2:F1}T",
                        Times[1][0].ToString("yyyy-MM-dd HH:mm:ss"),
                        currentLow,
                        swingRange));
                }
            }
        }

        private void CheckForEntry()
        {
            if (!IsRTH())
                return;

            trendModeActive = false;
            entryBar = -1;
            entryPrice = 0;

            bool entered = false;

            // Use elapsed primary bars since the swing timestamp instead of mixed-series bar numbers.
            if (lastRecordedSwingHighTime != Core.Globals.MinDate)
            {
                int barsSinceSwingHigh = Bars.GetBar(lastRecordedSwingHighTime) >= 0
                    ? CurrentBar - Bars.GetBar(lastRecordedSwingHighTime)
                    : int.MaxValue;

                if (barsSinceSwingHigh >= 1 && barsSinceSwingHigh <= 2)
                {
                    SetStopLoss("SwingHigh", CalculationMode.Ticks, InitialStop, false);
                    EnterShort(PositionSize, "SwingHigh");
                    entryBar = CurrentBar;
                    entered = true;

                    // Consume this signal so it cannot be reused later.
                    lastRecordedSwingHighTime = Core.Globals.MinDate;
                }
            }

            if (!entered && lastRecordedSwingLowTime != Core.Globals.MinDate)
            {
                int barsSinceSwingLow = Bars.GetBar(lastRecordedSwingLowTime) >= 0
                    ? CurrentBar - Bars.GetBar(lastRecordedSwingLowTime)
                    : int.MaxValue;

                if (barsSinceSwingLow >= 1 && barsSinceSwingLow <= 2)
                {
                    SetStopLoss("SwingLow", CalculationMode.Ticks, InitialStop, false);
                    EnterLong(PositionSize, "SwingLow");
                    entryBar = CurrentBar;
                    entered = true;

                    // Consume this signal so it cannot be reused later.
                    lastRecordedSwingLowTime = Core.Globals.MinDate;
                }
            }
        }

        private void ManagePosition()
        {
            if (entryPrice == 0)
                entryPrice = Position.AveragePrice;

            double currentPrice = Close[0];
            double profitTicks;

            if (Position.MarketPosition == MarketPosition.Long)
            {
                profitTicks = (currentPrice - entryPrice) / TickSize;

                if (!trendModeActive && profitTicks >= FirstTarget)
                {
                    trendModeActive = true;
                    SetStopLoss("SwingLow", CalculationMode.Price, entryPrice, false);
                }

                if (trendModeActive && profitTicks >= ExtendedTarget)
                    ExitLong("Target", "SwingLow");
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                profitTicks = (entryPrice - currentPrice) / TickSize;

                if (!trendModeActive && profitTicks >= FirstTarget)
                {
                    trendModeActive = true;
                    SetStopLoss("SwingHigh", CalculationMode.Price, entryPrice, false);
                }

                if (trendModeActive && profitTicks >= ExtendedTarget)
                    ExitShort("Target", "SwingHigh");
            }
        }

        private void ResetState()
        {
            trendModeActive = false;
            entryBar = -1;
            entryPrice = 0;
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Position Size", Order = 1, GroupName = "Parameters")]
        public int PositionSize { get; set; }

        [NinjaScriptProperty]
        [Range(5, 60)]
        [Display(Name = "Swing Window (Minutes)", Order = 2, GroupName = "Parameters")]
        public int SwingWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Min Swing Strength (Ticks)", Order = 3, GroupName = "Parameters")]
        public int MinSwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "First Target (Ticks)", Order = 4, GroupName = "Parameters")]
        public int FirstTarget { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Extended Target (Ticks)", Order = 5, GroupName = "Parameters")]
        public int ExtendedTarget { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Initial Stop (Ticks)", Order = 6, GroupName = "Parameters")]
        public int InitialStop { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "RTH Start Hour", Order = 7, GroupName = "Parameters")]
        public int RTHStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "RTH Start Minute", Order = 8, GroupName = "Parameters")]
        public int RTHStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "RTH End Hour", Order = 9, GroupName = "Parameters")]
        public int RTHEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "RTH End Minute", Order = 10, GroupName = "Parameters")]
        public int RTHEndMinute { get; set; }
        #endregion
    }
}
