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
    /// CG_TrendContinuationStrategy_v2
    ///
    /// Single-contract continuation model intended for MNQ trend days.
    ///
    /// Key differences vs prior versions:
    /// - Earlier breakout entry
    /// - 15-minute regime filter
    /// - Single contract only
    /// - No pyramiding
    /// - No fixed profit target
    /// - Wide trailing runner logic
    /// - Avoids fading strong directional days
    /// </summary>
    public class CG_TrendContinuationStrategy_v2 : Strategy
    {
        private EMA fastEMA15;
        private EMA slowEMA15;
        private ATR atr15;

        private bool longBreakoutArmed = false;
        private bool shortBreakdownArmed = false;

        private DateTime longArmedTime = Core.Globals.MinDate;
        private DateTime shortArmedTime = Core.Globals.MinDate;

        private double longTriggerPrice = 0;
        private double shortTriggerPrice = 0;

        private double entryPrice = 0;
        private double highestPriceSinceEntry = 0;
        private double lowestPriceSinceEntry = 0;
        private bool movedToBreakeven = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Single-contract trend continuation strategy";
                Name = "CG_TrendContinuationStrategy_v2";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 50;
                IsInstantiatedOnEachOptimizationIteration = true;

                PositionSize = 1;
                SwingWindowMinutes = 15;

                FastEMAPeriod = 20;
                SlowEMAPeriod = 50;
                ATRPeriod = 14;

                BreakoutBufferTicks = 4;
                EntryWindowMinutes = 120;

                InitialStopTicks = 28;
                BreakevenTriggerTicks = 50;
                TrailStartTicks = 100;
                TrailDistanceTicks = 80;

                RTHStartHour = 9;
                RTHStartMinute = 30;
                RTHEndHour = 16;
                RTHEndMinute = 0;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, SwingWindowMinutes);
            }
            else if (State == State.DataLoaded)
            {
                fastEMA15 = EMA(Closes[1], FastEMAPeriod);
                slowEMA15 = EMA(Closes[1], SlowEMAPeriod);
                atr15 = ATR(BarsArray[1], ATRPeriod);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                if (CurrentBars[1] < Math.Max(SlowEMAPeriod + 5, 20))
                    return;

                Evaluate15MinuteStructure();
                return;
            }

            if (BarsInProgress != 0)
                return;

            if (CurrentBar < BarsRequiredToTrade)
                return;

            ExpireSignals();

            if (Position.MarketPosition == MarketPosition.Flat)
                CheckForEntry();
            else
                ManageTrade();
        }

        private void Evaluate15MinuteStructure()
        {
            bool bullRegime =
                fastEMA15[0] > slowEMA15[0] &&
                Closes[1][0] > fastEMA15[0];

            bool bearRegime =
                fastEMA15[0] < slowEMA15[0] &&
                Closes[1][0] < fastEMA15[0];

            // Earlier breakout logic:
            // use prior 15-minute bar high/low rather than waiting for a deep confirmed swing
            double priorHigh = Highs[1][1];
            double priorLow = Lows[1][1];

            if (bullRegime)
            {
                longBreakoutArmed = true;
                longArmedTime = Times[1][0];
                longTriggerPrice = priorHigh + BreakoutBufferTicks * TickSize;

                Print(string.Format(
                    "{0} | Armed LONG breakout above priorHigh={1:F2} trigger={2:F2}",
                    Times[1][0].ToString("yyyy-MM-dd HH:mm:ss"),
                    priorHigh,
                    longTriggerPrice));
            }

            if (bearRegime)
            {
                shortBreakdownArmed = true;
                shortArmedTime = Times[1][0];
                shortTriggerPrice = priorLow - BreakoutBufferTicks * TickSize;

                Print(string.Format(
                    "{0} | Armed SHORT breakdown below priorLow={1:F2} trigger={2:F2}",
                    Times[1][0].ToString("yyyy-MM-dd HH:mm:ss"),
                    priorLow,
                    shortTriggerPrice));
            }
        }

        private void CheckForEntry()
        {
            if (!IsRTH())
                return;

            if (longBreakoutArmed && longArmedTime != Core.Globals.MinDate)
            {
                double minsSinceArm = (Time[0] - longArmedTime).TotalMinutes;

                if (minsSinceArm >= 0 &&
                    minsSinceArm <= EntryWindowMinutes &&
                    Close[0] >= longTriggerPrice)
                {
                    SetStopLoss("LongBreakout", CalculationMode.Ticks, InitialStopTicks, false);
                    EnterLong(PositionSize, "LongBreakout");

                    Print(string.Format(
                        "{0} | LONG ENTRY trigger={1:F2} close={2:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        longTriggerPrice,
                        Close[0]));

                    longBreakoutArmed = false;
                    longArmedTime = Core.Globals.MinDate;
                }
            }

            if (shortBreakdownArmed && shortArmedTime != Core.Globals.MinDate)
            {
                double minsSinceArm = (Time[0] - shortArmedTime).TotalMinutes;

                if (minsSinceArm >= 0 &&
                    minsSinceArm <= EntryWindowMinutes &&
                    Close[0] <= shortTriggerPrice)
                {
                    SetStopLoss("ShortBreakdown", CalculationMode.Ticks, InitialStopTicks, false);
                    EnterShort(PositionSize, "ShortBreakdown");

                    Print(string.Format(
                        "{0} | SHORT ENTRY trigger={1:F2} close={2:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        shortTriggerPrice,
                        Close[0]));

                    shortBreakdownArmed = false;
                    shortArmedTime = Core.Globals.MinDate;
                }
            }
        }

        private void ManageTrade()
        {
            if (entryPrice == 0)
                entryPrice = Position.AveragePrice;

            if (Position.MarketPosition == MarketPosition.Long)
            {
                highestPriceSinceEntry = Math.Max(highestPriceSinceEntry == 0 ? High[0] : highestPriceSinceEntry, High[0]);

                double profitTicks = (Close[0] - entryPrice) / TickSize;

                if (!movedToBreakeven && profitTicks >= BreakevenTriggerTicks)
                {
                    SetStopLoss("LongBreakout", CalculationMode.Price, entryPrice, false);
                    movedToBreakeven = true;

                    Print(string.Format(
                        "{0} | LONG moved to breakeven @ {1:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        entryPrice));
                }

                if (profitTicks >= TrailStartTicks)
                {
                    double trailStop = highestPriceSinceEntry - TrailDistanceTicks * TickSize;
                    double effectiveStop = Math.Max(entryPrice, trailStop);

                    SetStopLoss("LongBreakout", CalculationMode.Price, effectiveStop, false);

                    Print(string.Format(
                        "{0} | LONG trailing stop -> {1:F2} highest={2:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        effectiveStop,
                        highestPriceSinceEntry));
                }
            }

            if (Position.MarketPosition == MarketPosition.Short)
            {
                lowestPriceSinceEntry = lowestPriceSinceEntry == 0
                    ? Low[0]
                    : Math.Min(lowestPriceSinceEntry, Low[0]);

                double profitTicks = (entryPrice - Close[0]) / TickSize;

                if (!movedToBreakeven && profitTicks >= BreakevenTriggerTicks)
                {
                    SetStopLoss("ShortBreakdown", CalculationMode.Price, entryPrice, false);
                    movedToBreakeven = true;

                    Print(string.Format(
                        "{0} | SHORT moved to breakeven @ {1:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        entryPrice));
                }

                if (profitTicks >= TrailStartTicks)
                {
                    double trailStop = lowestPriceSinceEntry + TrailDistanceTicks * TickSize;
                    double effectiveStop = Math.Min(entryPrice, trailStop);

                    SetStopLoss("ShortBreakdown", CalculationMode.Price, effectiveStop, false);

                    Print(string.Format(
                        "{0} | SHORT trailing stop -> {1:F2} lowest={2:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        effectiveStop,
                        lowestPriceSinceEntry));
                }
            }
        }

        private void ExpireSignals()
        {
            if (longBreakoutArmed && longArmedTime != Core.Globals.MinDate)
            {
                if ((Time[0] - longArmedTime).TotalMinutes > EntryWindowMinutes)
                {
                    longBreakoutArmed = false;
                    longArmedTime = Core.Globals.MinDate;
                }
            }

            if (shortBreakdownArmed && shortArmedTime != Core.Globals.MinDate)
            {
                if ((Time[0] - shortArmedTime).TotalMinutes > EntryWindowMinutes)
                {
                    shortBreakdownArmed = false;
                    shortArmedTime = Core.Globals.MinDate;
                }
            }
        }

        private bool IsRTH()
        {
            TimeSpan currentTime = Time[0].TimeOfDay;
            TimeSpan start = new TimeSpan(RTHStartHour, RTHStartMinute, 0);
            TimeSpan end = new TimeSpan(RTHEndHour, RTHEndMinute, 0);
            return currentTime >= start && currentTime < end;
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="Position Size", Order=1, GroupName="Parameters")]
        public int PositionSize { get; set; }

        [NinjaScriptProperty]
        [Range(5, 60)]
        [Display(Name="Swing Window Minutes", Order=2, GroupName="Parameters")]
        public int SwingWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name="Fast EMA Period", Order=3, GroupName="Parameters")]
        public int FastEMAPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name="Slow EMA Period", Order=4, GroupName="Parameters")]
        public int SlowEMAPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name="ATR Period", Order=5, GroupName="Parameters")]
        public int ATRPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name="Breakout Buffer Ticks", Order=6, GroupName="Parameters")]
        public int BreakoutBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name="Entry Window Minutes", Order=7, GroupName="Parameters")]
        public int EntryWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="Initial Stop Ticks", Order=8, GroupName="Parameters")]
        public int InitialStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="Breakeven Trigger Ticks", Order=9, GroupName="Parameters")]
        public int BreakevenTriggerTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="Trail Start Ticks", Order=10, GroupName="Parameters")]
        public int TrailStartTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="Trail Distance Ticks", Order=11, GroupName="Parameters")]
        public int TrailDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name="RTH Start Hour", Order=12, GroupName="Parameters")]
        public int RTHStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name="RTH Start Minute", Order=13, GroupName="Parameters")]
        public int RTHStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name="RTH End Hour", Order=14, GroupName="Parameters")]
        public int RTHEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name="RTH End Minute", Order=15, GroupName="Parameters")]
        public int RTHEndMinute { get; set; }
        #endregion
    }
}
