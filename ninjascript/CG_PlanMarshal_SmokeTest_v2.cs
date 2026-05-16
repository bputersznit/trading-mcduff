//
// CG_PlanMarshal_SmokeTest_v2.cs
// NinjaTrader 8 Strategy
//
// Safer smoke test for PlanMarshal execution mechanics.
// Fixes:
// - No rapid re-entry swarm inside the same minute.
// - Optional one-trade-per-session mode.
// - Explicit bracket submission after entry execution.
// - Stop/target prices sanity-checked against current market before submission.
// - Heavy Output-window diagnostics.
//
// This is NOT production alpha.
//

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
    public class CG_PlanMarshal_SmokeTest_v2 : Strategy
    {
        private enum TestDirection { None = 0, Long = 1, Short = -1 }

        private DateTime currentSessionDate = Core.Globals.MinDate;
        private DateTime lastEntrySubmitTime = Core.Globals.MinDate;
        private DateTime lastExitTime = Core.Globals.MinDate;

        private bool entryWorking = false;
        private bool bracketSubmitted = false;
        private bool tradedThisSession = false;

        private double sessionStartCumProfit = 0.0;
        private double dailyRealizedPnl = 0.0;
        private double dailyPeakPnl = 0.0;
        private double lastTradeCumProfit = 0.0;

        private int consecutiveLosses = 0;
        private int entryAttempts = 0;
        private int acceptedSignals = 0;
        private int rejectedSignals = 0;
        private int orderUpdates = 0;
        private int executionUpdates = 0;
        private int bracketSubmissions = 0;

        private bool softLocked = false;
        private bool hardLocked = false;
        private bool profitLocked = false;

        [NinjaScriptProperty]
        [Display(Name = "EnableDebugPrints", Order = 1, GroupName = "Diagnostics")]
        public bool EnableDebugPrints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintEveryBar", Order = 2, GroupName = "Diagnostics")]
        public bool PrintEveryBar { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseTimeWindow", Order = 3, GroupName = "Session")]
        public bool UseTimeWindow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "StartTimeHHMMSS", Order = 4, GroupName = "Session")]
        public int StartTimeHHMMSS { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EndTimeHHMMSS", Order = 5, GroupName = "Session")]
        public int EndTimeHHMMSS { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OneTradePerSession", Order = 6, GroupName = "Signal")]
        public bool OneTradePerSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EntryEveryNMinutes", Order = 7, GroupName = "Signal")]
        public int EntryEveryNMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AlternateLongShort", Order = 8, GroupName = "Signal")]
        public bool AlternateLongShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ForceLongOnly", Order = 9, GroupName = "Signal")]
        public bool ForceLongOnly { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Quantity", Order = 10, GroupName = "Orders")]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "StopTicks", Order = 11, GroupName = "Orders")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TargetTicks", Order = 12, GroupName = "Orders")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SafetyTicksAwayFromMarket", Order = 13, GroupName = "Orders")]
        public int SafetyTicksAwayFromMarket { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseDailySoftStop", Order = 14, GroupName = "Risk")]
        public bool UseDailySoftStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SoftDailyStopUsd", Order = 15, GroupName = "Risk")]
        public double SoftDailyStopUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseDailyHardStop", Order = 16, GroupName = "Risk")]
        public bool UseDailyHardStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HardDailyStopUsd", Order = 17, GroupName = "Risk")]
        public double HardDailyStopUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseLossStreakLock", Order = 18, GroupName = "Risk")]
        public bool UseLossStreakLock { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MaxConsecutiveLosses", Order = 19, GroupName = "Risk")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseProfitLock", Order = 20, GroupName = "Risk")]
        public bool UseProfitLock { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ProfitLockPeakUsd", Order = 21, GroupName = "Risk")]
        public double ProfitLockPeakUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ProfitLockGivebackUsd", Order = 22, GroupName = "Risk")]
        public double ProfitLockGivebackUsd { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_PlanMarshal_SmokeTest_v2";
                Description = "Safer smoke test: deterministic entries, explicit protective brackets, detailed diagnostics.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                BarsRequiredToTrade = 20;
                StartBehavior = StartBehavior.WaitUntilFlat;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                TraceOrders = true;

                EnableDebugPrints = true;
                PrintEveryBar = false;

                UseTimeWindow = true;
                StartTimeHHMMSS = 94500;
                EndTimeHHMMSS = 110000;

                OneTradePerSession = false;
                EntryEveryNMinutes = 15;
                AlternateLongShort = true;
                ForceLongOnly = false;

                Quantity = 1;
                StopTicks = 16;
                TargetTicks = 32;
                SafetyTicksAwayFromMarket = 2;

                UseDailySoftStop = false;
                SoftDailyStopUsd = -99999;
                UseDailyHardStop = false;
                HardDailyStopUsd = -99999;
                UseLossStreakLock = false;
                MaxConsecutiveLosses = 999;
                UseProfitLock = false;
                ProfitLockPeakUsd = 3000;
                ProfitLockGivebackUsd = 500;
            }
            else if (State == State.DataLoaded)
            {
                PrintBanner("DATA LOADED");
            }
            else if (State == State.Historical)
            {
                DPrint("STATE", "Historical processing started.");
            }
            else if (State == State.Realtime)
            {
                PrintBanner("REALTIME / PLAYBACK STARTED");
            }
            else if (State == State.Terminated)
            {
                PrintBanner("TERMINATED");
                DPrint("SUMMARY",
                    "entryAttempts=" + entryAttempts +
                    " acceptedSignals=" + acceptedSignals +
                    " rejectedSignals=" + rejectedSignals +
                    " orderUpdates=" + orderUpdates +
                    " executionUpdates=" + executionUpdates +
                    " bracketSubmissions=" + bracketSubmissions +
                    " dailyPnL=" + dailyRealizedPnl.ToString("F2"));
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
            {
                if (CurrentBar == BarsRequiredToTrade - 1)
                    DPrint("READY", "BarsRequiredToTrade reached. CurrentBar=" + CurrentBar + " Time=" + SafeTime());
                return;
            }

            HandleNewSessionIfNeeded();
            UpdateDailyRiskState();

            if (PrintEveryBar && IsFirstTickOfBar)
            {
                DPrint("BAR",
                    "Time=" + SafeTime() +
                    " Pos=" + Position.MarketPosition +
                    " Close=" + Close[0].ToString("F2") +
                    " entryWorking=" + entryWorking +
                    " bracketSubmitted=" + bracketSubmitted +
                    " dailyPnL=" + dailyRealizedPnl.ToString("F2") +
                    " locks[soft=" + softLocked + ",hard=" + hardLocked + ",profit=" + profitLocked + "]" +
                    " consecLoss=" + consecutiveLosses);
            }

            if (!IsFirstTickOfBar)
                return;

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (entryWorking)
                return;

            TestDirection signal = GetSmokeTestSignal();

            if (signal == TestDirection.None)
                return;

            string reason;
            if (!CanSubmitNewEntry(out reason))
            {
                rejectedSignals++;
                DPrint("REJECT", "Signal=" + signal + " reason=" + reason);
                return;
            }

            acceptedSignals++;
            SubmitSmokeTestEntry(signal);
        }

        private void SubmitSmokeTestEntry(TestDirection signal)
        {
            entryAttempts++;
            entryWorking = true;
            bracketSubmitted = false;
            lastEntrySubmitTime = Time[0];

            if (signal == TestDirection.Long)
            {
                DPrint("ENTRY_SUBMIT",
                    "Submitting LONG qty=" + Quantity +
                    " Time=" + SafeTime() +
                    " Close=" + Close[0].ToString("F2") +
                    " StopTicks=" + StopTicks +
                    " TargetTicks=" + TargetTicks);
                EnterLong(Quantity, "PM2_Long");
            }
            else if (signal == TestDirection.Short)
            {
                DPrint("ENTRY_SUBMIT",
                    "Submitting SHORT qty=" + Quantity +
                    " Time=" + SafeTime() +
                    " Close=" + Close[0].ToString("F2") +
                    " StopTicks=" + StopTicks +
                    " TargetTicks=" + TargetTicks);
                EnterShort(Quantity, "PM2_Short");
            }
        }

        private TestDirection GetSmokeTestSignal()
        {
            int now = ToTime(Time[0]);

            if (UseTimeWindow && (now < StartTimeHHMMSS || now > EndTimeHHMMSS))
                return TestDirection.None;

            if (OneTradePerSession && tradedThisSession)
                return TestDirection.None;

            if (EntryEveryNMinutes <= 0)
                return TestDirection.None;

            if (lastEntrySubmitTime != Core.Globals.MinDate &&
                Time[0] < lastEntrySubmitTime.AddMinutes(EntryEveryNMinutes))
                return TestDirection.None;

            if (lastExitTime != Core.Globals.MinDate &&
                Time[0] <= lastExitTime)
                return TestDirection.None;

            if (Time[0].Minute % EntryEveryNMinutes != 0)
                return TestDirection.None;

            if (ForceLongOnly)
                return TestDirection.Long;

            if (!AlternateLongShort)
                return TestDirection.Long;

            int bucket = (Time[0].Hour * 60 + Time[0].Minute) / EntryEveryNMinutes;
            return (bucket % 2 == 0) ? TestDirection.Long : TestDirection.Short;
        }

        private bool CanSubmitNewEntry(out string reason)
        {
            if (hardLocked)
            {
                reason = "hard daily lock";
                return false;
            }

            if (softLocked)
            {
                reason = "soft daily lock";
                return false;
            }

            if (profitLocked)
            {
                reason = "profit lock";
                return false;
            }

            if (UseLossStreakLock && consecutiveLosses >= MaxConsecutiveLosses)
            {
                reason = "loss streak lock consecutiveLosses=" + consecutiveLosses;
                return false;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                reason = "not flat: " + Position.MarketPosition;
                return false;
            }

            if (entryWorking)
            {
                reason = "entry already working";
                return false;
            }

            reason = "OK";
            return true;
        }

        private void SubmitProtectiveBracketFromFill(string entryName, MarketPosition pos, double fillPrice, int qty)
        {
            if (qty <= 0)
            {
                DPrint("BRACKET_SKIP", "quantity <= 0");
                return;
            }

            double stopPrice;
            double targetPrice;

            double bid = GetCurrentBid();
            double ask = GetCurrentAsk();
            double last = Close[0];

            if (bid <= 0) bid = last;
            if (ask <= 0) ask = last;

            if (pos == MarketPosition.Long)
            {
                stopPrice = fillPrice - (StopTicks * TickSize);
                targetPrice = fillPrice + (TargetTicks * TickSize);

                double maxValidSellStop = bid - (SafetyTicksAwayFromMarket * TickSize);
                if (stopPrice >= maxValidSellStop)
                {
                    DPrint("BRACKET_ADJUST",
                        "Long stop adjusted from " + stopPrice.ToString("F2") +
                        " to " + maxValidSellStop.ToString("F2") +
                        " bid=" + bid.ToString("F2"));
                    stopPrice = maxValidSellStop;
                }

                DPrint("BRACKET_SUBMIT",
                    "LONG bracket fromEntry=" + entryName +
                    " fill=" + fillPrice.ToString("F2") +
                    " stop=" + stopPrice.ToString("F2") +
                    " target=" + targetPrice.ToString("F2") +
                    " bid=" + bid.ToString("F2") +
                    " ask=" + ask.ToString("F2"));

                ExitLongStopMarket(0, true, qty, stopPrice, "PM2_Long_Stop", entryName);
                ExitLongLimit(0, true, qty, targetPrice, "PM2_Long_Target", entryName);
            }
            else if (pos == MarketPosition.Short)
            {
                stopPrice = fillPrice + (StopTicks * TickSize);
                targetPrice = fillPrice - (TargetTicks * TickSize);

                double minValidBuyStop = ask + (SafetyTicksAwayFromMarket * TickSize);
                if (stopPrice <= minValidBuyStop)
                {
                    DPrint("BRACKET_ADJUST",
                        "Short stop adjusted from " + stopPrice.ToString("F2") +
                        " to " + minValidBuyStop.ToString("F2") +
                        " ask=" + ask.ToString("F2"));
                    stopPrice = minValidBuyStop;
                }

                DPrint("BRACKET_SUBMIT",
                    "SHORT bracket fromEntry=" + entryName +
                    " fill=" + fillPrice.ToString("F2") +
                    " stop=" + stopPrice.ToString("F2") +
                    " target=" + targetPrice.ToString("F2") +
                    " bid=" + bid.ToString("F2") +
                    " ask=" + ask.ToString("F2"));

                ExitShortStopMarket(0, true, qty, stopPrice, "PM2_Short_Stop", entryName);
                ExitShortLimit(0, true, qty, targetPrice, "PM2_Short_Target", entryName);
            }

            bracketSubmissions++;
            bracketSubmitted = true;
            DPrint("OCO++", "Protective bracket submitted. Verify active stop+target orders in Orders tab.");
        }

        private void HandleNewSessionIfNeeded()
        {
            DateTime sessionDate = Time[0].Date;

            if (currentSessionDate == Core.Globals.MinDate || sessionDate != currentSessionDate)
            {
                currentSessionDate = sessionDate;
                sessionStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                lastTradeCumProfit = sessionStartCumProfit;
                dailyRealizedPnl = 0.0;
                dailyPeakPnl = 0.0;
                consecutiveLosses = 0;
                softLocked = false;
                hardLocked = false;
                profitLocked = false;
                entryWorking = false;
                bracketSubmitted = false;
                tradedThisSession = false;
                lastEntrySubmitTime = Core.Globals.MinDate;
                lastExitTime = Core.Globals.MinDate;

                PrintBanner("NEW SESSION " + currentSessionDate.ToString("yyyy-MM-dd"));
                DPrint("SESSION",
                    "sessionStartCumProfit=" + sessionStartCumProfit.ToString("F2") +
                    " Account=" + (Account != null ? Account.Name : "NULL") +
                    " Instrument=" + (Instrument != null ? Instrument.FullName : "NULL"));
            }
        }

        private void UpdateDailyRiskState()
        {
            double cum = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            dailyRealizedPnl = cum - sessionStartCumProfit;

            if (dailyRealizedPnl > dailyPeakPnl)
                dailyPeakPnl = dailyRealizedPnl;

            if (UseDailySoftStop && dailyRealizedPnl <= SoftDailyStopUsd)
            {
                if (!softLocked)
                    DPrint("LOCK", "Soft stop triggered dailyPnL=" + dailyRealizedPnl.ToString("F2") + " threshold=" + SoftDailyStopUsd.ToString("F2"));
                softLocked = true;
            }

            if (UseDailyHardStop && dailyRealizedPnl <= HardDailyStopUsd)
            {
                if (!hardLocked)
                    DPrint("LOCK", "Hard stop triggered dailyPnL=" + dailyRealizedPnl.ToString("F2") + " threshold=" + HardDailyStopUsd.ToString("F2"));
                hardLocked = true;
            }

            if (UseProfitLock && dailyPeakPnl >= ProfitLockPeakUsd && (dailyPeakPnl - dailyRealizedPnl) >= ProfitLockGivebackUsd)
            {
                if (!profitLocked)
                    DPrint("LOCK",
                        "Profit lock triggered dailyPeak=" + dailyPeakPnl.ToString("F2") +
                        " dailyPnL=" + dailyRealizedPnl.ToString("F2") +
                        " giveback=" + (dailyPeakPnl - dailyRealizedPnl).ToString("F2"));
                profitLocked = true;
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            orderUpdates++;

            if (order == null)
                return;

            DPrint("ORDER",
                "name=" + order.Name +
                " id=" + order.OrderId +
                " state=" + orderState +
                " action=" + order.OrderAction +
                " type=" + order.OrderType +
                " qty=" + quantity +
                " filled=" + filled +
                " avgFill=" + averageFillPrice.ToString("F2") +
                " limit=" + limitPrice.ToString("F2") +
                " stop=" + stopPrice.ToString("F2") +
                " time=" + time.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                " error=" + error +
                " native='" + nativeError + "'");

            if (error != ErrorCode.NoError)
                DPrint("ORDER_ERROR", "error=" + error + " native='" + nativeError + "'");

            if ((order.Name == "PM2_Long" || order.Name == "PM2_Short") &&
                (orderState == OrderState.Cancelled || orderState == OrderState.Rejected))
            {
                entryWorking = false;
                DPrint("ENTRY_RESET", "Entry order no longer working due to state=" + orderState);
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            executionUpdates++;

            if (execution == null || execution.Order == null)
                return;

            DPrint("EXEC",
                "order=" + execution.Order.Name +
                " id=" + orderId +
                " execId=" + executionId +
                " price=" + price.ToString("F2") +
                " qty=" + quantity +
                " marketPos=" + marketPosition +
                " orderState=" + execution.Order.OrderState +
                " time=" + time.ToString("yyyy-MM-dd HH:mm:ss.fff"));

            if ((execution.Order.Name == "PM2_Long" || execution.Order.Name == "PM2_Short") &&
                execution.Order.OrderState == OrderState.Filled)
            {
                tradedThisSession = true;
                entryWorking = false;
                SubmitProtectiveBracketFromFill(execution.Order.Name, marketPosition, price, quantity);
            }

            if (execution.Order.Name.Contains("_Stop") ||
                execution.Order.Name.Contains("_Target") ||
                execution.Order.Name.Contains("StopCancelClose"))
            {
                lastExitTime = time;
                bracketSubmitted = false;

                double currentCum = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                double deltaSinceLast = currentCum - lastTradeCumProfit;

                DPrint("EXIT",
                    "Exit order=" + execution.Order.Name +
                    " deltaSinceLastTrade=" + deltaSinceLast.ToString("F2") +
                    " cum=" + currentCum.ToString("F2"));

                if (deltaSinceLast < 0)
                    consecutiveLosses++;
                else if (deltaSinceLast > 0)
                    consecutiveLosses = 0;

                lastTradeCumProfit = currentCum;
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            DPrint("POSITION",
                "marketPosition=" + marketPosition +
                " qty=" + quantity +
                " avgPrice=" + averagePrice.ToString("F2") +
                " Time=" + SafeTime());
        }

        private void DPrint(string tag, string message)
        {
            if (!EnableDebugPrints)
                return;

            Print("[CG_PM_SMOKE_V2][" + tag + "] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + message);
        }

        private void PrintBanner(string text)
        {
            if (!EnableDebugPrints)
                return;

            Print("");
            Print("============================================================");
            Print("[CG_PM_SMOKE_V2] " + text);
            Print("Strategy=" + Name +
                  " Instrument=" + (Instrument != null ? Instrument.FullName : "NULL") +
                  " Account=" + (Account != null ? Account.Name : "NULL"));
            Print("Time=" + SafeTime() + " State=" + State);
            Print("============================================================");
            Print("");
        }

        private string SafeTime()
        {
            try
            {
                if (CurrentBar >= 0)
                    return Time[0].ToString("yyyy-MM-dd HH:mm:ss.fff");
            }
            catch { }

            return "NO_TIME";
        }
    }
}
