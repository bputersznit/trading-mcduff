//
// CG_T2_ClanMarshal_ForcedSmoke_v1.cs
// NinjaTrader 8 Strategy
//
// Purpose:
//   Forced-signal smoke test for the future T2 ClanMarshal model.
//   This proves mechanics only:
//     - strategy receives Playback/Sim data
//     - entries submit
//     - managed stop/target brackets attach
//     - Output-window diagnostics work
//     - daily/session guards reset correctly
//
// This is NOT the validated ClickHouse T2 alpha.
// Replace GetForcedSmokeSignal() later with real T2 signal logic.
//
// Defaults:
//   - Select Playback101 manually in the strategy UI
//   - Forced short by default
//   - Test window: 09:45:00 to 09:46:00
//   - One trade per session
//   - Stop: 16 ticks
//   - Target: 32 ticks
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
    public class CG_T2_ClanMarshal_ForcedSmoke_v1 : Strategy
    {
        private enum SignalDirection
        {
            None = 0,
            Long = 1,
            Short = -1
        }

        private DateTime currentSessionDate = Core.Globals.MinDate;
        private DateTime lastEntrySubmitTime = Core.Globals.MinDate;
        private DateTime lastExitTime = Core.Globals.MinDate;

        private bool entryWorking = false;
        private bool tradedThisSession = false;
        private bool bracketObserved = false;

        private double sessionStartCumProfit = 0.0;
        private double dailyRealizedPnl = 0.0;
        private double dailyPeakPnl = 0.0;
        private double lastTradeCumProfit = 0.0;

        private int entryAttempts = 0;
        private int acceptedSignals = 0;
        private int rejectedSignals = 0;
        private int orderUpdates = 0;
        private int executionUpdates = 0;
        private int bracketObservations = 0;
        private int consecutiveLosses = 0;

        private bool softLocked = false;
        private bool hardLocked = false;
        private bool profitLocked = false;

        #region User parameters

        [NinjaScriptProperty]
        [Display(Name = "EnableDebugPrints", Order = 1, GroupName = "Diagnostics")]
        public bool EnableDebugPrints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintEveryBar", Order = 2, GroupName = "Diagnostics")]
        public bool PrintEveryBar { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ExpectedAccountName", Order = 3, GroupName = "Diagnostics")]
        public string ExpectedAccountName { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseTimeWindow", Order = 4, GroupName = "Session")]
        public bool UseTimeWindow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "StartTimeHHMMSS", Order = 5, GroupName = "Session")]
        public int StartTimeHHMMSS { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EndTimeHHMMSS", Order = 6, GroupName = "Session")]
        public int EndTimeHHMMSS { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OneTradePerSession", Order = 7, GroupName = "Signal")]
        public bool OneTradePerSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ForceLongOnly", Order = 8, GroupName = "Signal")]
        public bool ForceLongOnly { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ForceShortOnly", Order = 9, GroupName = "Signal")]
        public bool ForceShortOnly { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EntryCooldownSeconds", Order = 10, GroupName = "Signal")]
        public int EntryCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Quantity", Order = 11, GroupName = "Orders")]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "StopTicks", Order = 12, GroupName = "Orders")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TargetTicks", Order = 13, GroupName = "Orders")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MaxSpreadTicks", Order = 14, GroupName = "Protection")]
        public int MaxSpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MaxVelocityPoints", Order = 15, GroupName = "Protection")]
        public double MaxVelocityPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseDailySoftStop", Order = 16, GroupName = "Risk")]
        public bool UseDailySoftStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SoftDailyStopUsd", Order = 17, GroupName = "Risk")]
        public double SoftDailyStopUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseDailyHardStop", Order = 18, GroupName = "Risk")]
        public bool UseDailyHardStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HardDailyStopUsd", Order = 19, GroupName = "Risk")]
        public double HardDailyStopUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseLossStreakLock", Order = 20, GroupName = "Risk")]
        public bool UseLossStreakLock { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MaxConsecutiveLosses", Order = 21, GroupName = "Risk")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseProfitLock", Order = 22, GroupName = "Risk")]
        public bool UseProfitLock { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ProfitLockPeakUsd", Order = 23, GroupName = "Risk")]
        public double ProfitLockPeakUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ProfitLockGivebackUsd", Order = 24, GroupName = "Risk")]
        public double ProfitLockGivebackUsd { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_ForcedSmoke_v1";
                Description = "Forced-signal smoke test for T2 ClanMarshal execution: market entries, managed bracket exits, rich diagnostics.";
                Calculate = Calculate.OnEachTick;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                StartBehavior = StartBehavior.WaitUntilFlat;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                IsInstantiatedOnEachOptimizationIteration = false;
                TraceOrders = true;
                BarsRequiredToTrade = 20;

                EnableDebugPrints = true;
                PrintEveryBar = false;
                ExpectedAccountName = "Playback101";

                UseTimeWindow = true;
                StartTimeHHMMSS = 94500;
                EndTimeHHMMSS = 94600;

                OneTradePerSession = true;
                ForceLongOnly = false;
                ForceShortOnly = true;
                EntryCooldownSeconds = 60;

                Quantity = 1;
                StopTicks = 16;
                TargetTicks = 32;

                MaxSpreadTicks = 8;
                MaxVelocityPoints = 50.0;

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
                // Managed bracket templates.
                // NinjaTrader submits the stop/target after the matching entry fills.
                SetStopLoss("T2Smoke_Long", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("T2Smoke_Long", CalculationMode.Ticks, TargetTicks);

                SetStopLoss("T2Smoke_Short", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("T2Smoke_Short", CalculationMode.Ticks, TargetTicks);
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
                    " bracketObservations=" + bracketObservations +
                    " consecutiveLosses=" + consecutiveLosses +
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

            double bid = GetCurrentBid();
            double ask = GetCurrentAsk();
            double close = Close[0];

            if (bid <= 0) bid = close;
            if (ask <= 0) ask = close;

            double spreadTicks = Math.Max(0.0, (ask - bid) / TickSize);
            double velocityPoints = CurrentBar > 0 ? Math.Abs(Close[0] - Close[1]) : 0.0;

            if (PrintEveryBar && IsFirstTickOfBar)
            {
                DPrint("BAR",
                    "Time=" + SafeTime() +
                    " Pos=" + Position.MarketPosition +
                    " Close=" + close.ToString("F2") +
                    " Bid=" + bid.ToString("F2") +
                    " Ask=" + ask.ToString("F2") +
                    " SpreadTicks=" + spreadTicks.ToString("F1") +
                    " VelocityPts=" + velocityPoints.ToString("F2") +
                    " dailyPnL=" + dailyRealizedPnl.ToString("F2") +
                    " tradedThisSession=" + tradedThisSession +
                    " entryWorking=" + entryWorking);
            }

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (entryWorking)
                return;

            SignalDirection signal = GetForcedSmokeSignal(spreadTicks, velocityPoints);

            if (signal == SignalDirection.None)
                return;

            string reason;
            if (!CanSubmitNewEntry(out reason))
            {
                rejectedSignals++;
                DPrint("REJECT", "Signal=" + signal + " reason=" + reason);
                return;
            }

            acceptedSignals++;
            SubmitEntry(signal, spreadTicks, velocityPoints);
        }

        private SignalDirection GetForcedSmokeSignal(double spreadTicks, double velocityPoints)
        {
            int now = ToTime(Time[0]);

            if (UseTimeWindow && (now < StartTimeHHMMSS || now > EndTimeHHMMSS))
                return SignalDirection.None;

            if (OneTradePerSession && tradedThisSession)
                return SignalDirection.None;

            if (lastEntrySubmitTime != Core.Globals.MinDate &&
                Time[0] < lastEntrySubmitTime.AddSeconds(EntryCooldownSeconds))
                return SignalDirection.None;

            if (lastExitTime != Core.Globals.MinDate && Time[0] <= lastExitTime)
                return SignalDirection.None;

            if (spreadTicks > MaxSpreadTicks)
            {
                DPrint("SKIP", "Spread too wide. spreadTicks=" + spreadTicks.ToString("F1") + " max=" + MaxSpreadTicks);
                return SignalDirection.None;
            }

            if (velocityPoints > MaxVelocityPoints)
            {
                DPrint("SKIP", "Velocity too high. velocityPoints=" + velocityPoints.ToString("F2") + " max=" + MaxVelocityPoints.ToString("F2"));
                return SignalDirection.None;
            }

            if (ForceLongOnly && !ForceShortOnly)
                return SignalDirection.Long;

            if (ForceShortOnly && !ForceLongOnly)
                return SignalDirection.Short;

            return (Time[0].Day % 2 == 0) ? SignalDirection.Long : SignalDirection.Short;
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

        private void SubmitEntry(SignalDirection signal, double spreadTicks, double velocityPoints)
        {
            entryAttempts++;
            entryWorking = true;
            bracketObserved = false;
            lastEntrySubmitTime = Time[0];

            if (signal == SignalDirection.Long)
            {
                DPrint("ENTRY_SUBMIT",
                    "FORCED LONG market qty=" + Quantity +
                    " Time=" + SafeTime() +
                    " Close=" + Close[0].ToString("F2") +
                    " spreadTicks=" + spreadTicks.ToString("F1") +
                    " velocityPoints=" + velocityPoints.ToString("F2") +
                    " StopTicks=" + StopTicks +
                    " TargetTicks=" + TargetTicks);

                EnterLong(Quantity, "T2Smoke_Long");
            }
            else if (signal == SignalDirection.Short)
            {
                DPrint("ENTRY_SUBMIT",
                    "FORCED SHORT market qty=" + Quantity +
                    " Time=" + SafeTime() +
                    " Close=" + Close[0].ToString("F2") +
                    " spreadTicks=" + spreadTicks.ToString("F1") +
                    " velocityPoints=" + velocityPoints.ToString("F2") +
                    " StopTicks=" + StopTicks +
                    " TargetTicks=" + TargetTicks);

                EnterShort(Quantity, "T2Smoke_Short");
            }
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
                bracketObserved = false;
                tradedThisSession = false;
                lastEntrySubmitTime = Core.Globals.MinDate;
                lastExitTime = Core.Globals.MinDate;

                PrintBanner("NEW SESSION " + currentSessionDate.ToString("yyyy-MM-dd"));
                DPrint("SESSION",
                    "sessionStartCumProfit=" + sessionStartCumProfit.ToString("F2") +
                    " Account=" + (Account != null ? Account.Name : "NULL") +
                    " ExpectedAccount=" + ExpectedAccountName +
                    " Instrument=" + (Instrument != null ? Instrument.FullName : "NULL"));

                if (Account == null || Account.Name != ExpectedAccountName)
                {
                    DPrint("ACCOUNT_WARNING",
                        "Strategy cannot force account selection. Select account '" + ExpectedAccountName +
                        "' in the strategy UI. Current account is '" + (Account != null ? Account.Name : "NULL") + "'.");
                }
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

            if (error != ErrorCode.NoError)
                DPrint("ORDER_ERROR", "error=" + error + " native='" + nativeError + "'");

            if ((order.Name == "T2Smoke_Long" || order.Name == "T2Smoke_Short") &&
                (orderState == OrderState.Cancelled || orderState == OrderState.Rejected))
            {
                entryWorking = false;
                DPrint("ENTRY_RESET", "Entry order no longer working due to state=" + orderState);
            }
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

            if ((execution.Order.Name == "T2Smoke_Long" || execution.Order.Name == "T2Smoke_Short") &&
                execution.Order.OrderState == OrderState.Filled)
            {
                tradedThisSession = true;
                entryWorking = false;
                bracketObserved = true;
                bracketObservations++;

                DPrint("OCO++",
                    "Entry filled for " + execution.Order.Name +
                    ". Managed stop/target templates should now be active. " +
                    "StopTicks=" + StopTicks +
                    " TargetTicks=" + TargetTicks +
                    ". Confirm stop/target orders in Orders tab.");
            }

            if (execution.Order.Name.Contains("Profit target") ||
                execution.Order.Name.Contains("Stop loss") ||
                execution.Order.Name.Contains("StopCancelClose") ||
                execution.Order.Name.Contains("Target") ||
                execution.Order.Name.Contains("Stop"))
            {
                lastExitTime = time;
                bracketObserved = false;

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

            Print("[CG_T2_FORCED_SMOKE][" + tag + "] " +
                  DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                  " | " + message);
        }

        private void PrintBanner(string text)
        {
            if (!EnableDebugPrints)
                return;

            Print("");
            Print("============================================================");
            Print("[CG_T2_FORCED_SMOKE] " + text);
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
