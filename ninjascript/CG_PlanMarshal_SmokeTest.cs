// 
// CG_PlanMarshal_SmokeTest.cs
// NinjaTrader 8 Strategy
//
// Purpose:
//   - Prove the execution/risk/order-state harness actually places trades in Playback/Sim101.
//   - Produce meaningful diagnostics in the NinjaScript Output window.
//   - Submit immediate protective stop/target brackets using managed SetStopLoss/SetProfitTarget.
//   - Keep this separate from the real ClanMarshal alpha until order mechanics are validated.
//
// Install:
//   NinjaTrader 8 -> New -> NinjaScript Editor -> Strategies -> right click -> New Strategy
//   Name it: CG_PlanMarshal_SmokeTest
//   Replace all generated code with this file contents.
//   Compile.
//   Add to MNQ chart / Playback / Sim101.
//
// Recommended test settings:
//   Calculate: On each tick
//   BarsRequiredToTrade: 20
//   EnableDebugPrints: true
//   UseTimeWindow: true
//   StartTime: 094500
//   EndTime: 110000
//   EntryEveryNMinutes: 15
//
// IMPORTANT:
//   This is NOT the production alpha.
//   It intentionally fires deterministic test signals so we can verify that NT8 executes,
//   prints diagnostics, and attaches protective brackets.
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
    public class CG_PlanMarshal_SmokeTest : Strategy
    {
        private enum TestDirection
        {
            None = 0,
            Long = 1,
            Short = -1
        }

        private DateTime lastSignalBarTime = Core.Globals.MinDate;
        private DateTime currentSessionDate = Core.Globals.MinDate;

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

        private bool softLocked = false;
        private bool hardLocked = false;
        private bool profitLocked = false;

        private string activeEntrySignal = string.Empty;

        #region User parameters

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
        [Display(Name = "EntryEveryNMinutes", Order = 6, GroupName = "Signal")]
        public int EntryEveryNMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AlternateLongShort", Order = 7, GroupName = "Signal")]
        public bool AlternateLongShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ForceLongOnly", Order = 8, GroupName = "Signal")]
        public bool ForceLongOnly { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Quantity", Order = 9, GroupName = "Orders")]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "StopTicks", Order = 10, GroupName = "Orders")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TargetTicks", Order = 11, GroupName = "Orders")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseDailySoftStop", Order = 12, GroupName = "Risk")]
        public bool UseDailySoftStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SoftDailyStopUsd", Order = 13, GroupName = "Risk")]
        public double SoftDailyStopUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseDailyHardStop", Order = 14, GroupName = "Risk")]
        public bool UseDailyHardStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HardDailyStopUsd", Order = 15, GroupName = "Risk")]
        public double HardDailyStopUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseLossStreakLock", Order = 16, GroupName = "Risk")]
        public bool UseLossStreakLock { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MaxConsecutiveLosses", Order = 17, GroupName = "Risk")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseProfitLock", Order = 18, GroupName = "Risk")]
        public bool UseProfitLock { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ProfitLockPeakUsd", Order = 19, GroupName = "Risk")]
        public double ProfitLockPeakUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ProfitLockGivebackUsd", Order = 20, GroupName = "Risk")]
        public double ProfitLockGivebackUsd { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_PlanMarshal_SmokeTest";
                Description = "Smoke-test strategy for PlanMarshal execution harness: deterministic entries, OCO++ brackets, and diagnostic output.";
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

                EntryEveryNMinutes = 15;
                AlternateLongShort = true;
                ForceLongOnly = false;

                Quantity = 1;
                StopTicks = 16;
                TargetTicks = 32;

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
            else if (State == State.Configure)
            {
                SetStopLoss("PM_Long", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("PM_Long", CalculationMode.Ticks, TargetTicks);

                SetStopLoss("PM_Short", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("PM_Short", CalculationMode.Ticks, TargetTicks);
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
                    " dailyPnL=" + dailyRealizedPnl.ToString("F2") +
                    " locks[soft=" + softLocked + ",hard=" + hardLocked + ",profit=" + profitLocked + "]" +
                    " consecLoss=" + consecutiveLosses);
            }

            if (Position.MarketPosition != MarketPosition.Flat)
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

            if (signal == TestDirection.Long)
            {
                activeEntrySignal = "PM_Long";
                DPrint("ENTRY_SUBMIT",
                    "Submitting LONG qty=" + Quantity +
                    " Time=" + SafeTime() +
                    " Close=" + Close[0].ToString("F2") +
                    " StopTicks=" + StopTicks +
                    " TargetTicks=" + TargetTicks);
                EnterLong(Quantity, "PM_Long");
            }
            else if (signal == TestDirection.Short)
            {
                activeEntrySignal = "PM_Short";
                DPrint("ENTRY_SUBMIT",
                    "Submitting SHORT qty=" + Quantity +
                    " Time=" + SafeTime() +
                    " Close=" + Close[0].ToString("F2") +
                    " StopTicks=" + StopTicks +
                    " TargetTicks=" + TargetTicks);
                EnterShort(Quantity, "PM_Short");
            }
        }

        private TestDirection GetSmokeTestSignal()
        {
            int now = ToTime(Time[0]);

            if (UseTimeWindow && (now < StartTimeHHMMSS || now > EndTimeHHMMSS))
                return TestDirection.None;

            if (EntryEveryNMinutes <= 0)
                return TestDirection.None;

            if (Time[0] == lastSignalBarTime)
                return TestDirection.None;

            if (Time[0].Minute % EntryEveryNMinutes != 0)
                return TestDirection.None;

            lastSignalBarTime = Time[0];

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

            reason = "OK";
            return true;
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
                activeEntrySignal = string.Empty;

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

            if (execution.Order.Name == "PM_Long" || execution.Order.Name == "PM_Short")
            {
                DPrint("OCO++",
                    "Entry fill detected for " + execution.Order.Name +
                    ". Managed stop/target templates should now be working. " +
                    "StopTicks=" + StopTicks +
                    " TargetTicks=" + TargetTicks +
                    ". Verify active stop+target orders in Orders tab.");
            }

            double currentCum = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            double deltaSinceLast = currentCum - lastTradeCumProfit;

            if (execution.Order.Name.Contains("Profit target") ||
                execution.Order.Name.Contains("Stop loss") ||
                execution.Order.Name.Contains("Stop") ||
                execution.Order.Name.Contains("Target"))
            {
                DPrint("EXIT",
                    "Possible exit order=" + execution.Order.Name +
                    " deltaSinceLastTrade=" + deltaSinceLast.ToString("F2") +
                    " cum=" + currentCum.ToString("F2"));

                if (deltaSinceLast < 0)
                    consecutiveLosses++;
                else if (deltaSinceLast > 0)
                    consecutiveLosses = 0;

                lastTradeCumProfit = currentCum;
            }
        }

        protected override void OnPositionUpdate(
            Position position,
            double averagePrice,
            int quantity,
            MarketPosition marketPosition)
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

            Print("[CG_PM_SMOKE][" + tag + "] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + message);
        }

        private void PrintBanner(string text)
        {
            if (!EnableDebugPrints)
                return;

            Print("");
            Print("============================================================");
            Print("[CG_PM_SMOKE] " + text);
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
            catch
            {
            }

            return "NO_TIME";
        }
    }
}
