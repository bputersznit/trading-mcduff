#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_MNQ_Hybrid_v5_ClanMarshal : Strategy
    {
        #region Variables
        // === Aggression Tracking (100ms buckets) ===
        private DateTime currentBucket = DateTime.MinValue;
        private double bidEventSize = 0;
        private double askEventSize = 0;
        private int bidEventCount = 0;
        private int askEventCount = 0;

        private double lastSignalEventDelta = 0;
        private double lastSignalImbalance = 0;
        private DateTime lastSignalTime = DateTime.MinValue;

        // === Position Tracking ===
        private double entryPrice = 0;
        private double targetPrice = 0;
        private double stopPrice = 0;
        private DateTime entryTime = DateTime.MinValue;
        private DateTime exitTime = DateTime.MinValue;

        // === Opening Range (9:30-9:45 AM ET) ===
        private double orHigh = double.MinValue;
        private double orLow = double.MaxValue;
        private bool orCalculated = false;
        private DateTime lastTradeDate = DateTime.MinValue;

        // === Daily Tracking ===
        private double runningDailyPnL = 0;
        private double runningDailyPeak = 0;
        private int consecutiveLosses = 0;
        private bool dailyLimitHit = false;
        private bool profitLockHit = false;

        // === Trade Counting ===
        private int tradesToday = 0;
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name="Event Delta Threshold", Order=1, GroupName="Signal")]
        public double EventDeltaThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1)]
        [Display(Name="Event Imbalance Threshold", Order=2, GroupName="Signal")]
        public double EventImbalanceThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Target (ticks)", Order=3, GroupName="Risk")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Stop (ticks)", Order=4, GroupName="Risk")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name="Daily Loss Limit (USD)", Order=5, GroupName="Guards")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Max Consecutive Losses", Order=6, GroupName="Guards")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name="Profit Lock Peak (USD)", Order=7, GroupName="Guards")]
        public double ProfitLockPeak { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name="Profit Lock Drawdown (USD)", Order=8, GroupName="Guards")]
        public double ProfitLockDrawdown { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Enable Manipulation Gate", Order=9, GroupName="Filters")]
        public bool EnableManipulationGate { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Verbose Logging", Order=10, GroupName="Debug")]
        public bool VerboseLogging { get; set; }
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"CG MNQ Hybrid v5 ClanMarshal - 100ms Order Flow Imbalance Strategy";
                Name = "CG_MNQ_Hybrid_v5_ClanMarshal";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 60;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 3;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // Default Parameters (from v5opt SQL)
                EventDeltaThreshold = 50;
                EventImbalanceThreshold = 0.60;
                TargetTicks = 40;  // 10 points
                StopTicks = 20;    // 5 points
                DailyLossLimit = 60;
                MaxConsecutiveLosses = 4;
                ProfitLockPeak = 3000;
                ProfitLockDrawdown = 500;
                EnableManipulationGate = true;
                VerboseLogging = false;
            }
            else if (State == State.Configure)
            {
                // Add tick data series for real-time aggression tracking
                AddDataSeries(Data.BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                ClearOutputWindow();
                Print(string.Format("{0} loaded - v5 ClanMarshal Strategy", Name));
                Print(string.Format("Signal: EventDelta>{0}, Imbalance>{1:F2}",
                    EventDeltaThreshold, EventImbalanceThreshold));
                Print(string.Format("Risk: Target={0} ticks, Stop={1} ticks",
                    TargetTicks, StopTicks));
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < BarsRequiredToTrade) return;
            if (BarsInProgress != 0) return;

            // Check if new trading day
            DateTime now = Time[0];
            DateTime nowET = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
            DateTime tradeDateET = nowET.Date;

            if (tradeDateET != lastTradeDate)
            {
                OnNewTradingDay(nowET);
                lastTradeDate = tradeDateET;
            }

            // RTH Check (9:30 AM - 4:00 PM ET)
            if (!IsRTH(nowET))
                return;

            // Update Opening Range (9:30-9:45 AM)
            UpdateOpeningRange(nowET);

            // Check daily guards
            if (dailyLimitHit || profitLockHit)
                return;

            // Manage existing position
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                ManagePosition();
                return;
            }
        }
        #endregion

        #region OnMarketData
        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            if (marketDataUpdate.MarketDataType != MarketDataType.Last)
                return;

            // Get current 100ms bucket
            DateTime eventTime = marketDataUpdate.Time;
            DateTime bucketTime = new DateTime(
                eventTime.Year, eventTime.Month, eventTime.Day,
                eventTime.Hour, eventTime.Minute, eventTime.Second,
                (eventTime.Millisecond / 100) * 100
            );

            // New bucket - evaluate previous bucket
            if (bucketTime != currentBucket && currentBucket != DateTime.MinValue)
            {
                EvaluateBucket();
                ResetBucket();
            }

            currentBucket = bucketTime;

            // Accumulate aggression in current bucket
            double price = marketDataUpdate.Price;
            double volume = marketDataUpdate.Volume;

            // Determine aggression side (simplified - compare to bid/ask)
            if (price >= marketDataUpdate.Ask)
            {
                // Buy aggression (lifting offer)
                bidEventSize += volume;
                bidEventCount++;
            }
            else if (price <= marketDataUpdate.Bid)
            {
                // Sell aggression (hitting bid)
                askEventSize += volume;
                askEventCount++;
            }
        }
        #endregion

        #region Bucket Evaluation
        private void EvaluateBucket()
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (dailyLimitHit || profitLockHit)
                return;

            double eventDelta = bidEventSize - askEventSize;
            double totalEventSize = bidEventSize + askEventSize;

            if (totalEventSize < 1)
                return;

            double eventImbalance = eventDelta / totalEventSize;

            // Check signal thresholds
            bool longSignal = eventDelta > EventDeltaThreshold && eventImbalance > EventImbalanceThreshold;
            bool shortSignal = eventDelta < -EventDeltaThreshold && eventImbalance < -EventImbalanceThreshold;

            if (!longSignal && !shortSignal)
                return;

            // Store signal data
            lastSignalEventDelta = eventDelta;
            lastSignalImbalance = eventImbalance;
            lastSignalTime = currentBucket;

            // Apply filters
            string side = longSignal ? "LONG" : "SHORT";

            if (EnableManipulationGate && !PassesManipulationGate(side))
            {
                if (VerboseLogging)
                    Print(string.Format("{0} | {1} signal BLOCKED by manipulation gate",
                        currentBucket, side));
                return;
            }

            // Execute entry
            ExecuteEntry(side);
        }

        private void ResetBucket()
        {
            bidEventSize = 0;
            askEventSize = 0;
            bidEventCount = 0;
            askEventCount = 0;
        }
        #endregion

        #region Entry Logic
        private void ExecuteEntry(string side)
        {
            entryTime = currentBucket;

            if (side == "LONG")
            {
                EnterLong(1, "ClanMarshal_Long");
                entryPrice = Close[0];  // Approximate (will be updated on fill)
                targetPrice = entryPrice + (TargetTicks * TickSize);
                stopPrice = entryPrice - (StopTicks * TickSize);
            }
            else
            {
                EnterShort(1, "ClanMarshal_Short");
                entryPrice = Close[0];
                targetPrice = entryPrice - (TargetTicks * TickSize);
                stopPrice = entryPrice + (StopTicks * TickSize);
            }

            tradesToday++;

            if (VerboseLogging)
            {
                Print(string.Format("{0} | {1} ENTRY @ {2:F2} | Delta={3:F0} Imb={4:F2} | OR: {5:F2}-{6:F2}",
                    entryTime, side, entryPrice, lastSignalEventDelta, lastSignalImbalance, orLow, orHigh));
            }
        }
        #endregion

        #region Position Management
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order.Name.Contains("ClanMarshal"))
            {
                entryPrice = execution.Price;

                if (Position.MarketPosition == MarketPosition.Long)
                {
                    targetPrice = entryPrice + (TargetTicks * TickSize);
                    stopPrice = entryPrice - (StopTicks * TickSize);

                    SetProfitTarget("ClanMarshal_Long", CalculationMode.Price, targetPrice);
                    SetStopLoss("ClanMarshal_Long", CalculationMode.Price, stopPrice, false);
                }
                else if (Position.MarketPosition == MarketPosition.Short)
                {
                    targetPrice = entryPrice - (TargetTicks * TickSize);
                    stopPrice = entryPrice + (StopTicks * TickSize);

                    SetProfitTarget("ClanMarshal_Short", CalculationMode.Price, targetPrice);
                    SetStopLoss("ClanMarshal_Short", CalculationMode.Price, stopPrice, false);
                }
            }
            else if (execution.Order.Name.Contains("Profit") || execution.Order.Name.Contains("Stop"))
            {
                // Exit execution
                exitTime = time;
                double pnl = Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]);

                UpdateDailyStats(pnl, execution.Order.Name.Contains("Stop"));

                Print(string.Format("{0} | EXIT @ {1:F2} | P&L: ${2:F2} | Daily: ${3:F2} | Losses: {4}",
                    exitTime, execution.Price, pnl, runningDailyPnL, consecutiveLosses));
            }
        }

        private void ManagePosition()
        {
            // 10 minute timeout
            if ((Time[0] - entryTime).TotalMinutes >= 10)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("ClanMarshal_Long");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("ClanMarshal_Short");

                Print(string.Format("{0} | TIMEOUT EXIT after {1:F1} min",
                    Time[0], (Time[0] - entryTime).TotalMinutes));
            }
        }
        #endregion

        #region Daily Stats & Guards
        private void UpdateDailyStats(double tradePnL, bool wasStop)
        {
            runningDailyPnL += tradePnL;

            // Update peak
            if (runningDailyPnL > runningDailyPeak)
                runningDailyPeak = runningDailyPnL;

            // Track consecutive losses
            if (wasStop)
                consecutiveLosses++;
            else
                consecutiveLosses = 0;

            // Check daily loss limit
            if (runningDailyPnL < -DailyLossLimit)
            {
                dailyLimitHit = true;
                Print(string.Format("*** DAILY LOSS LIMIT HIT: ${0:F2} ***", runningDailyPnL));
            }

            // Check consecutive loss limit
            if (consecutiveLosses >= MaxConsecutiveLosses)
            {
                dailyLimitHit = true;
                Print(string.Format("*** MAX CONSECUTIVE LOSSES HIT: {0} ***", consecutiveLosses));
            }

            // Check profit lock
            double drawdownFromPeak = runningDailyPnL - runningDailyPeak;
            if (runningDailyPeak >= ProfitLockPeak && drawdownFromPeak <= -ProfitLockDrawdown)
            {
                profitLockHit = true;
                Print(string.Format("*** PROFIT LOCK TRIGGERED: Peak=${0:F2}, DD=${1:F2} ***",
                    runningDailyPeak, drawdownFromPeak));
            }
        }

        private void OnNewTradingDay(DateTime nowET)
        {
            Print(string.Format("\n========== {0:yyyy-MM-dd} - NEW TRADING DAY ==========", nowET));
            Print(string.Format("Previous Day: Trades={0}, P&L=${1:F2}, Peak=${2:F2}",
                tradesToday, runningDailyPnL, runningDailyPeak));

            // Reset daily counters
            runningDailyPnL = 0;
            runningDailyPeak = 0;
            consecutiveLosses = 0;
            dailyLimitHit = false;
            profitLockHit = false;
            tradesToday = 0;

            // Reset opening range
            orHigh = double.MinValue;
            orLow = double.MaxValue;
            orCalculated = false;
        }
        #endregion

        #region Opening Range
        private void UpdateOpeningRange(DateTime nowET)
        {
            if (orCalculated)
                return;

            int hour = nowET.Hour;
            int minute = nowET.Minute;

            // Calculate OR during 9:30-9:45
            if (hour == 9 && minute >= 30 && minute < 45)
            {
                double high = High[0];
                double low = Low[0];

                if (high > orHigh) orHigh = high;
                if (low < orLow) orLow = low;
            }
            else if (hour == 9 && minute >= 45)
            {
                orCalculated = true;
                Print(string.Format("9:45 AM | OR CALCULATED: High={0:F2} Low={1:F2}", orHigh, orLow));
            }
        }

        private string GetORLocation(double price)
        {
            if (!orCalculated)
                return "UNKNOWN";

            if (price > orHigh)
                return "ABOVE_OR";
            else if (price < orLow)
                return "BELOW_OR";
            else
                return "INSIDE_OR";
        }

        private string GetTimeZone(DateTime nowET)
        {
            int hour = nowET.Hour;
            int minute = nowET.Minute;

            if (hour == 9 && minute < 45)
                return "OPEN_15";
            else if ((hour == 9 && minute >= 45) || (hour == 10 && minute < 30))
                return "POST_OPEN";
            else if (hour == 15 && minute >= 30)
                return "CLOSE_30";
            else
                return "NORMAL";
        }
        #endregion

        #region Manipulation Gate
        private bool PassesManipulationGate(string side)
        {
            if (!orCalculated)
                return false;  // Wait for OR

            DateTime nowET = TimeZoneInfo.ConvertTime(Time[0], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
            string timeZone = GetTimeZone(nowET);
            string orLocation = GetORLocation(Close[0]);

            // Block patterns from v5opt SQL:
            // 1. SHORT during OPEN_15
            if (timeZone == "OPEN_15" && side == "SHORT")
                return false;

            // 2. SHORT when INSIDE_OR in POST_OPEN
            if (timeZone == "POST_OPEN" && orLocation == "INSIDE_OR" && side == "SHORT")
                return false;

            // 3. LONG when INSIDE_OR in NORMAL with MARKET (we use market orders)
            if (timeZone == "NORMAL" && orLocation == "INSIDE_OR" && side == "LONG")
                return false;

            // 4. SHORT when ABOVE_OR in CLOSE_30
            if (timeZone == "CLOSE_30" && orLocation == "ABOVE_OR" && side == "SHORT")
                return false;

            // 5. LONG when BELOW_OR in CLOSE_30 with MARKET
            if (timeZone == "CLOSE_30" && orLocation == "BELOW_OR" && side == "LONG")
                return false;

            return true;
        }
        #endregion

        #region Helper Methods
        private bool IsRTH(DateTime nowET)
        {
            int hour = nowET.Hour;
            int minute = nowET.Minute;

            // 9:30 AM - 4:00 PM ET
            if (hour < 9 || (hour == 9 && minute < 30))
                return false;
            if (hour >= 16)
                return false;

            return true;
        }
        #endregion
    }
}
