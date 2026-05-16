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
    public class CG_NoTrailTrendStrategy_QUIET_v2 : Strategy
    {
        // Strategy / methodology notes:
        // - Primary series (BarsInProgress == 0) is the execution series.
        // - Secondary series (BarsInProgress == 1) is the swing-detection series.
        // - A confirmed swing is only known AFTER the confirmation window closes,
        //   so entries must be timed from the DETECTION/ARMING moment, not from the pivot bar itself.
        // - We therefore arm a pending long/short signal when a new swing is detected on BIP=1,
        //   then permit entry for the next 1-2 primary bars on BIP=0.
        // - Signals are single-use and are cleared after entry or expiration.

        private double entryPrice = 0;
        private bool trendModeActive = false;
        private int swingDetectionBars = 3;

        private double lastRecordedSwingHigh = 0;
        private double lastRecordedSwingLow = 0;
        private DateTime lastRecordedSwingHighPivotTime = Core.Globals.MinDate;
        private DateTime lastRecordedSwingLowPivotTime = Core.Globals.MinDate;

        // Pending signal state measured in PRIMARY-series bars from the moment the swing is confirmed.
        private bool pendingShortSignal = false;
        private bool pendingLongSignal = false;
        private int pendingShortSignalBar = -1;
        private int pendingLongSignalBar = -1;
        private double pendingShortSwingPrice = 0;
        private double pendingLongSwingPrice = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                 = @"Swing-confirmation strategy with corrected armed-entry timing";
                Name                                        = "CG_NoTrailTrendStrategy_QUIET_v2";
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
                EntryWindowBars     = 2;
            }
            else if (State == State.Configure)
            {
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

            ExpireStaleSignals();

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

                Print(string.Format(
                    "{0} | {1} filled @ {2:F2}",
                    time.ToString("yyyy-MM-dd HH:mm:ss"),
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

                    Print(string.Format("{0} | EXIT {1:F1}T", time.ToString("yyyy-MM-dd HH:mm:ss"), pnlTicks));
                }

                ResetTradeState();
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
                    lastRecordedSwingHighPivotTime = Times[1][barsAgo];

                    // Arm on the primary series at the current primary bar.
                    pendingShortSignal = true;
                    pendingShortSignalBar = CurrentBars[0];
                    pendingShortSwingPrice = currentHigh;

                    Print(string.Format(
                        "{0} | SWING HIGH confirmed @ {1:F2} range={2:F1}T pivot={3} armedPrimaryBar={4}",
                        Times[1][0].ToString("yyyy-MM-dd HH:mm:ss"),
                        currentHigh,
                        swingRange,
                        lastRecordedSwingHighPivotTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        pendingShortSignalBar));
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
                    lastRecordedSwingLowPivotTime = Times[1][barsAgo];

                    pendingLongSignal = true;
                    pendingLongSignalBar = CurrentBars[0];
                    pendingLongSwingPrice = currentLow;

                    Print(string.Format(
                        "{0} | SWING LOW confirmed @ {1:F2} range={2:F1}T pivot={3} armedPrimaryBar={4}",
                        Times[1][0].ToString("yyyy-MM-dd HH:mm:ss"),
                        currentLow,
                        swingRange,
                        lastRecordedSwingLowPivotTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        pendingLongSignalBar));
                }
            }
        }

        private void ExpireStaleSignals()
        {
            if (pendingShortSignal && pendingShortSignalBar >= 0)
            {
                int barsSinceArm = CurrentBar - pendingShortSignalBar;
                if (barsSinceArm > EntryWindowBars)
                {
                    Print(string.Format(
                        "{0} | Expiring SHORT signal from swing @ {1:F2}; barsSinceArm={2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        pendingShortSwingPrice,
                        barsSinceArm));
                    pendingShortSignal = false;
                    pendingShortSignalBar = -1;
                    pendingShortSwingPrice = 0;
                }
            }

            if (pendingLongSignal && pendingLongSignalBar >= 0)
            {
                int barsSinceArm = CurrentBar - pendingLongSignalBar;
                if (barsSinceArm > EntryWindowBars)
                {
                    Print(string.Format(
                        "{0} | Expiring LONG signal from swing @ {1:F2}; barsSinceArm={2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        pendingLongSwingPrice,
                        barsSinceArm));
                    pendingLongSignal = false;
                    pendingLongSignalBar = -1;
                    pendingLongSwingPrice = 0;
                }
            }
        }

        private void CheckForEntry()
        {
            if (!IsRTH())
                return;

            bool entered = false;

            if (pendingShortSignal && pendingShortSignalBar >= 0)
            {
                int barsSinceArm = CurrentBar - pendingShortSignalBar;

                if (barsSinceArm >= 1 && barsSinceArm <= EntryWindowBars)
                {
                    Print(string.Format(
                        "{0} | EnterShort armed from swing @ {1:F2}; barsSinceArm={2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        pendingShortSwingPrice,
                        barsSinceArm));

                    SetStopLoss("SwingHigh", CalculationMode.Ticks, InitialStop, false);
                    EnterShort(PositionSize, "SwingHigh");

                    pendingShortSignal = false;
                    pendingShortSignalBar = -1;
                    pendingShortSwingPrice = 0;
                    entered = true;
                }
            }

            if (!entered && pendingLongSignal && pendingLongSignalBar >= 0)
            {
                int barsSinceArm = CurrentBar - pendingLongSignalBar;

                if (barsSinceArm >= 1 && barsSinceArm <= EntryWindowBars)
                {
                    Print(string.Format(
                        "{0} | EnterLong armed from swing @ {1:F2}; barsSinceArm={2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        pendingLongSwingPrice,
                        barsSinceArm));

                    SetStopLoss("SwingLow", CalculationMode.Ticks, InitialStop, false);
                    EnterLong(PositionSize, "SwingLow");

                    pendingLongSignal = false;
                    pendingLongSignalBar = -1;
                    pendingLongSwingPrice = 0;
                    entered = true;
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

        private void ResetTradeState()
        {
            trendModeActive = false;
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

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Entry Window Bars", Order = 11, GroupName = "Parameters")]
        public int EntryWindowBars { get; set; }
        #endregion
    }
}
