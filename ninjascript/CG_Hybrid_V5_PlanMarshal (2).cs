#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

// ============================================================================
// CG_Hybrid_V5_PlanMarshal
// T2 ClanMarshal / PlanMarshal execution harness for SIM / Playback testing.
//
// Purpose:
//   This file is designed to validate the live order-state machine, OCO++ safety,
//   daily governors, opening-range/session logic, timeout/fallback behavior, and
//   audit logging before wiring in the final ClickHouse-derived alpha signal.
//
// Important:
//   The included signal generator is a conservative PLACEHOLDER for testing only.
//   It is NOT the proven T2 alpha. Wire your real T2 order-flow signal into
//   GetPlanMarshalSignal() before judging strategy edge.
//
// OCO++ doctrine:
//   - Entry logic may be local/advisory.
//   - Protective stop and target are installed immediately via NinjaTrader managed
//     SetStopLoss/SetProfitTarget bracket behavior after entry execution.
//   - Trailing/breakeven logic only tightens the working stop; it does not replace
//     the requirement for immediate protective coverage.
//
// Recommended test mode:
//   - Sim101 / Playback / Market Replay first.
//   - 1 MNQ contract.
//   - Calculate.OnEachTick.
//   - Confirm logs, order states, OCO behavior, lockouts, and fills.
// ============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_Hybrid_V5_PlanMarshal : Strategy
    {
        private enum PMState
        {
            Flat,
            LimitWorking,
            PositionOpen,
            SoftLocked,
            HardLocked,
            ProfitLocked,
            FailSafeLocked
        }

        private enum SignalSide
        {
            None,
            Long,
            Short
        }

        private PMState pmState = PMState.Flat;
        private Order entryOrder;
        private DateTime entrySubmitTime = Core.Globals.MinDate;
        private string activeEntrySignal = string.Empty;
        private double openingRangeHigh = double.MinValue;
        private double openingRangeLow = double.MaxValue;
        private bool openingRangeReady = false;
        private DateTime currentSessionDate = Core.Globals.MinDate;
        private double sessionStartCumProfit = 0.0;
        private double dailyRealizedPnL = 0.0;
        private double dailyPeakPnL = 0.0;
        private int consecutiveLosses = 0;
        private int lastTradeCountSeen = 0;
        private double currentEntryPrice = 0.0;
        private double bestFavorablePrice = 0.0;
        private bool movedToBreakeven = false;
        private StreamWriter auditWriter;

        // --------------------------------------------------------------------
        // Parameters
        // --------------------------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Contracts", GroupName = "01. Execution", Order = 1)]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Sim Account", GroupName = "01. Execution", Order = 2)]
        public bool RequireSimAccount { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Placeholder Signals", GroupName = "02. Signal", Order = 1)]
        public bool EnablePlaceholderSignals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Limit Timeout Seconds", GroupName = "03. Hybrid Entry", Order = 1)]
        public double LimitTimeoutSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Low Flow Threshold", GroupName = "03. Hybrid Entry", Order = 2)]
        public double LowFlowThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Medium Flow Threshold", GroupName = "03. Hybrid Entry", Order = 3)]
        public double MediumFlowThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop Ticks", GroupName = "04. OCO++ Bracket", Order = 1)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Target Ticks", GroupName = "04. OCO++ Bracket", Order = 2)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Breakeven", GroupName = "05. Stop Tightening", Order = 1)]
        public bool UseBreakeven { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Breakeven Trigger Ticks", GroupName = "05. Stop Tightening", Order = 2)]
        public int BreakevenTriggerTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Breakeven Plus Ticks", GroupName = "05. Stop Tightening", Order = 3)]
        public int BreakevenPlusTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Trail", GroupName = "05. Stop Tightening", Order = 4)]
        public bool UseTrail { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail Trigger Ticks", GroupName = "05. Stop Tightening", Order = 5)]
        public int TrailTriggerTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail Distance Ticks", GroupName = "05. Stop Tightening", Order = 6)]
        public int TrailDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Soft Daily Stop", GroupName = "06. Risk", Order = 1)]
        public double SoftDailyStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hard Daily Stop", GroupName = "06. Risk", Order = 2)]
        public double HardDailyStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Consecutive Losses", GroupName = "06. Risk", Order = 3)]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Profit Lock Trigger", GroupName = "06. Risk", Order = 4)]
        public double ProfitLockTrigger { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Profit Lock Giveback", GroupName = "06. Risk", Order = 5)]
        public double ProfitLockGiveback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Block Open 15", GroupName = "07. Session", Order = 1)]
        public bool BlockOpen15 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Block FOMC Wednesday Window", GroupName = "07. Session", Order = 2)]
        public bool BlockFomcWindow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Audit Log", GroupName = "08. Audit", Order = 1)]
        public bool EnableAuditLog { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_Hybrid_V5_PlanMarshal";
                Description = "T2 PlanMarshal / ClanMarshal execution harness with OCO++, governors, and hybrid limit/market fallback.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsUnmanaged = false;
                TraceOrders = true;
                BarsRequiredToTrade = 50;

                Contracts = 1;
                RequireSimAccount = true;
                EnablePlaceholderSignals = false;

                LimitTimeoutSeconds = 1.0;
                LowFlowThreshold = 100.0;
                MediumFlowThreshold = 200.0;

                StopTicks = 20;
                TargetTicks = 40;

                UseBreakeven = true;
                BreakevenTriggerTicks = 20;
                BreakevenPlusTicks = 2;
                UseTrail = false;
                TrailTriggerTicks = 30;
                TrailDistanceTicks = 12;

                SoftDailyStop = -60.0;
                HardDailyStop = -200.0;
                MaxConsecutiveLosses = 4;
                ProfitLockTrigger = 3000.0;
                ProfitLockGiveback = 500.0;

                BlockOpen15 = true;
                BlockFomcWindow = true;
                EnableAuditLog = true;
            }
            else if (State == State.Configure)
            {
                // Managed OCO++ bracket. NinjaTrader submits these protective orders
                // after the entry execution. Trailing only modifies this stop tighter.
                SetStopLoss(CalculationMode.Ticks, StopTicks);
                SetProfitTarget(CalculationMode.Ticks, TargetTicks);
            }
            else if (State == State.DataLoaded)
            {
                currentSessionDate = Core.Globals.MinDate;
                lastTradeCountSeen = SystemPerformance.AllTrades.Count;

                if (EnableAuditLog)
                {
                    string path = Path.Combine(Core.Globals.UserDataDir, "CG_Hybrid_V5_PlanMarshal_Audit.csv");
                    auditWriter = new StreamWriter(path, true);
                    auditWriter.WriteLine("timestamp,event,state,side,price,qty,dailyPnL,consecLosses,notes");
                    auditWriter.Flush();
                }
            }
            else if (State == State.Terminated)
            {
                if (auditWriter != null)
                {
                    auditWriter.Flush();
                    auditWriter.Dispose();
                    auditWriter = null;
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            if (RequireSimAccount && Account != null && !Account.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase))
            {
                if (pmState != PMState.FailSafeLocked)
                {
                    pmState = PMState.FailSafeLocked;
                    LogAudit("FAILSAFE_SIM_REQUIRED", SignalSide.None, Close[0], 0, "Account=" + Account.Name);
                }
                return;
            }

            ResetSessionIfNeeded();
            UpdateOpeningRange();
            UpdateRealizedPnLFromClosedTrades();
            ApplyRiskGovernors();
            ManageWorkingLimitTimeout();
            ManageProtectiveStopTightening();

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (pmState == PMState.SoftLocked || pmState == PMState.HardLocked || pmState == PMState.ProfitLocked || pmState == PMState.FailSafeLocked)
                return;

            if (!IsTradeTimeAllowed())
                return;

            SignalSide signal = GetPlanMarshalSignal();
            if (signal == SignalSide.None)
                return;

            if (!ManipulationFilterPasses(signal))
                return;

            SubmitHybridLimit(signal);
        }

        private void SubmitHybridLimit(SignalSide signal)
        {
            if (entryOrder != null || Position.MarketPosition != MarketPosition.Flat)
                return;

            activeEntrySignal = signal == SignalSide.Long ? "PM_Long" : "PM_Short";
            double limitPrice = signal == SignalSide.Long ? GetCurrentBid() : GetCurrentAsk();
            if (limitPrice <= 0)
                limitPrice = Close[0];

            entrySubmitTime = Time[0];
            movedToBreakeven = false;
            bestFavorablePrice = Close[0];

            if (signal == SignalSide.Long)
                EnterLongLimit(Contracts, limitPrice, activeEntrySignal);
            else
                EnterShortLimit(Contracts, limitPrice, activeEntrySignal);

            pmState = PMState.LimitWorking;
            LogAudit("LIMIT_SUBMIT", signal, limitPrice, Contracts, "timeoutSec=" + LimitTimeoutSeconds.ToString("0.00"));
        }

        private void ManageWorkingLimitTimeout()
        {
            if (pmState != PMState.LimitWorking || entryOrder == null)
                return;

            if ((Time[0] - entrySubmitTime).TotalSeconds < LimitTimeoutSeconds)
                return;

            SignalSide side = activeEntrySignal == "PM_Long" ? SignalSide.Long : SignalSide.Short;
            CancelOrder(entryOrder);
            LogAudit("LIMIT_TIMEOUT_CANCEL", side, Close[0], Contracts, string.Empty);

            if (FallbackAllowed(side))
            {
                if (side == SignalSide.Long)
                    EnterLong(Contracts, "PM_Long_Fallback");
                else
                    EnterShort(Contracts, "PM_Short_Fallback");

                LogAudit("MARKET_FALLBACK", side, Close[0], Contracts, "flow=" + EstimateFlowScore().ToString("0.00"));
            }
            else
            {
                entryOrder = null;
                activeEntrySignal = string.Empty;
                pmState = PMState.Flat;
                LogAudit("FALLBACK_SKIP", side, Close[0], 0, "flow=" + EstimateFlowScore().ToString("0.00"));
            }
        }

        private bool FallbackAllowed(SignalSide side)
        {
            double flow = EstimateFlowScore();
            bool momentumConfirms = MomentumConfirms(side);

            if (flow <= LowFlowThreshold)
                return true;

            if (flow <= MediumFlowThreshold && momentumConfirms)
                return true;

            return false;
        }

        // --------------------------------------------------------------------
        // Signal placeholder. Replace this with the real T2 order-flow signal.
        // --------------------------------------------------------------------
        private SignalSide GetPlanMarshalSignal()
        {
            if (!EnablePlaceholderSignals)
                return SignalSide.None;

            // Methodology placeholder:
            //   This is a minimal momentum/OR test signal to exercise execution.
            //   It is intentionally simple and should not be evaluated as alpha.
            double fast = EMA(8)[0];
            double slow = EMA(21)[0];
            double priorFast = EMA(8)[1];
            double priorSlow = EMA(21)[1];

            if (priorFast <= priorSlow && fast > slow && MomentumConfirms(SignalSide.Long))
                return SignalSide.Long;

            if (priorFast >= priorSlow && fast < slow && MomentumConfirms(SignalSide.Short))
                return SignalSide.Short;

            return SignalSide.None;
        }

        private bool MomentumConfirms(SignalSide side)
        {
            if (CurrentBar < 6)
                return false;

            double momentumTicks = (Close[0] - Close[5]) / TickSize;
            if (side == SignalSide.Long)
                return momentumTicks > 0;
            if (side == SignalSide.Short)
                return momentumTicks < 0;
            return false;
        }

        private double EstimateFlowScore()
        {
            // Placeholder for total_event_size analogue.
            // In final T2 wiring, replace with real MBO/L2-derived total_event_size.
            return Math.Min(1000.0, Math.Max(0.0, Volume[0]));
        }

        private void ResetSessionIfNeeded()
        {
            DateTime sessionDate = Time[0].Date;
            if (sessionDate == currentSessionDate)
                return;

            currentSessionDate = sessionDate;
            openingRangeHigh = double.MinValue;
            openingRangeLow = double.MaxValue;
            openingRangeReady = false;
            dailyRealizedPnL = 0.0;
            dailyPeakPnL = 0.0;
            consecutiveLosses = 0;
            sessionStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            pmState = PMState.Flat;
            entryOrder = null;
            activeEntrySignal = string.Empty;

            LogAudit("SESSION_RESET", SignalSide.None, Close[0], 0, sessionDate.ToString("yyyy-MM-dd"));
        }

        private void UpdateOpeningRange()
        {
            int t = ToTime(Time[0]);
            if (t >= 93000 && t < 94500)
            {
                openingRangeHigh = Math.Max(openingRangeHigh, High[0]);
                openingRangeLow = Math.Min(openingRangeLow, Low[0]);
            }
            else if (t >= 94500 && openingRangeHigh > double.MinValue && openingRangeLow < double.MaxValue)
            {
                openingRangeReady = true;
            }
        }

        private bool IsTradeTimeAllowed()
        {
            int t = ToTime(Time[0]);

            if (t < 93000 || t >= 160000)
                return false;

            if (BlockOpen15 && t >= 93000 && t < 94500)
                return false;

            if (BlockFomcWindow && Time[0].DayOfWeek == DayOfWeek.Wednesday && t >= 134500 && t <= 141500)
                return false;

            return true;
        }

        private bool ManipulationFilterPasses(SignalSide signal)
        {
            int t = ToTime(Time[0]);

            if (!openingRangeReady)
                return true;

            bool aboveOR = Close[0] > openingRangeHigh;
            bool belowOR = Close[0] < openingRangeLow;
            bool close30 = t >= 153000 && t < 160000;
            bool postOpen = t >= 94500 && t < 103000;

            // Close continuation bias from research:
            // above OR -> long only; below OR -> short only.
            if (close30)
            {
                if (aboveOR && signal == SignalSide.Short)
                    return false;
                if (belowOR && signal == SignalSide.Long)
                    return false;
            }

            // Post-open manipulation defense: avoid obvious above-OR short traps.
            if (postOpen && aboveOR && signal == SignalSide.Short)
                return false;

            return true;
        }

        private void UpdateRealizedPnLFromClosedTrades()
        {
            int tradeCount = SystemPerformance.AllTrades.Count;
            if (tradeCount <= lastTradeCountSeen)
            {
                dailyRealizedPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartCumProfit;
                dailyPeakPnL = Math.Max(dailyPeakPnL, dailyRealizedPnL);
                return;
            }

            for (int i = lastTradeCountSeen; i < tradeCount; i++)
            {
                double tradePnl = SystemPerformance.AllTrades[i].ProfitCurrency;
                if (tradePnl < 0)
                    consecutiveLosses++;
                else
                    consecutiveLosses = 0;

                LogAudit("TRADE_CLOSED", SignalSide.None, Close[0], 0, "tradePnL=" + tradePnl.ToString("0.00"));
            }

            lastTradeCountSeen = tradeCount;
            dailyRealizedPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartCumProfit;
            dailyPeakPnL = Math.Max(dailyPeakPnL, dailyRealizedPnL);
        }

        private void ApplyRiskGovernors()
        {
            if (pmState == PMState.HardLocked || pmState == PMState.FailSafeLocked)
                return;

            if (dailyRealizedPnL <= HardDailyStop)
            {
                pmState = PMState.HardLocked;
                CancelWorkingEntry();
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("PM_HardStop_Flatten", activeEntrySignal);
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("PM_HardStop_Flatten", activeEntrySignal);

                LogAudit("HARD_LOCK", SignalSide.None, Close[0], 0, "dailyPnL=" + dailyRealizedPnL.ToString("0.00"));
                return;
            }

            if (dailyRealizedPnL <= SoftDailyStop)
            {
                pmState = PMState.SoftLocked;
                CancelWorkingEntry();
                LogAudit("SOFT_LOCK", SignalSide.None, Close[0], 0, "dailyPnL=" + dailyRealizedPnL.ToString("0.00"));
                return;
            }

            if (MaxConsecutiveLosses > 0 && consecutiveLosses >= MaxConsecutiveLosses)
            {
                pmState = PMState.SoftLocked;
                CancelWorkingEntry();
                LogAudit("LOSS_STREAK_LOCK", SignalSide.None, Close[0], 0, "consecLosses=" + consecutiveLosses);
                return;
            }

            if (dailyPeakPnL >= ProfitLockTrigger && dailyPeakPnL - dailyRealizedPnL >= ProfitLockGiveback)
            {
                pmState = PMState.ProfitLocked;
                CancelWorkingEntry();
                LogAudit("PROFIT_LOCK", SignalSide.None, Close[0], 0, "peak=" + dailyPeakPnL.ToString("0.00") + " daily=" + dailyRealizedPnL.ToString("0.00"));
            }
        }

        private void ManageProtectiveStopTightening()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            double last = Close[0];
            if (Position.MarketPosition == MarketPosition.Long)
            {
                bestFavorablePrice = Math.Max(bestFavorablePrice <= 0 ? last : bestFavorablePrice, High[0]);
                double profitTicks = (last - Position.AveragePrice) / TickSize;

                if (UseBreakeven && !movedToBreakeven && profitTicks >= BreakevenTriggerTicks)
                {
                    double newStop = Position.AveragePrice + BreakevenPlusTicks * TickSize;
                    SetStopLoss(CalculationMode.Price, newStop);
                    movedToBreakeven = true;
                    LogAudit("STOP_TO_BREAKEVEN", SignalSide.Long, newStop, Position.Quantity, string.Empty);
                }

                if (UseTrail && profitTicks >= TrailTriggerTicks)
                {
                    double trailStop = bestFavorablePrice - TrailDistanceTicks * TickSize;
                    SetStopLoss(CalculationMode.Price, trailStop);
                    LogAudit("TRAIL_STOP_TIGHTEN", SignalSide.Long, trailStop, Position.Quantity, string.Empty);
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                bestFavorablePrice = Math.Min(bestFavorablePrice <= 0 ? last : bestFavorablePrice, Low[0]);
                double profitTicks = (Position.AveragePrice - last) / TickSize;

                if (UseBreakeven && !movedToBreakeven && profitTicks >= BreakevenTriggerTicks)
                {
                    double newStop = Position.AveragePrice - BreakevenPlusTicks * TickSize;
                    SetStopLoss(CalculationMode.Price, newStop);
                    movedToBreakeven = true;
                    LogAudit("STOP_TO_BREAKEVEN", SignalSide.Short, newStop, Position.Quantity, string.Empty);
                }

                if (UseTrail && profitTicks >= TrailTriggerTicks)
                {
                    double trailStop = bestFavorablePrice + TrailDistanceTicks * TickSize;
                    SetStopLoss(CalculationMode.Price, trailStop);
                    LogAudit("TRAIL_STOP_TIGHTEN", SignalSide.Short, trailStop, Position.Quantity, string.Empty);
                }
            }
        }

        private void CancelWorkingEntry()
        {
            if (entryOrder != null && (entryOrder.OrderState == OrderState.Working || entryOrder.OrderState == OrderState.Accepted || entryOrder.OrderState == OrderState.Submitted))
                CancelOrder(entryOrder);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null)
                return;

            if (order.Name == "PM_Long" || order.Name == "PM_Short")
            {
                entryOrder = order;

                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                {
                    if (pmState == PMState.LimitWorking)
                    {
                        entryOrder = null;
                        if (orderState == OrderState.Rejected)
                            pmState = PMState.FailSafeLocked;
                        else
                            pmState = PMState.Flat;
                    }
                    LogAudit("ENTRY_ORDER_" + orderState.ToString().ToUpperInvariant(), order.Name == "PM_Long" ? SignalSide.Long : SignalSide.Short, limitPrice, quantity, nativeError);
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            string name = execution.Order.Name ?? string.Empty;

            if (name.StartsWith("PM_Long", StringComparison.OrdinalIgnoreCase) || name.StartsWith("PM_Short", StringComparison.OrdinalIgnoreCase))
            {
                currentEntryPrice = price;
                bestFavorablePrice = price;
                movedToBreakeven = false;
                pmState = PMState.PositionOpen;
                entryOrder = null;
                LogAudit("ENTRY_FILL_OCO_ARMED", name.StartsWith("PM_Long") ? SignalSide.Long : SignalSide.Short, price, quantity, "OCO++ bracket expected active");
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat && pmState == PMState.PositionOpen)
            {
                pmState = PMState.Flat;
                activeEntrySignal = string.Empty;
                currentEntryPrice = 0.0;
                bestFavorablePrice = 0.0;
                movedToBreakeven = false;
                LogAudit("POSITION_FLAT", SignalSide.None, Close[0], 0, string.Empty);
            }
        }

        private void LogAudit(string evt, SignalSide side, double price, int qty, string notes)
        {
            string line = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff},{1},{2},{3},{4:0.00},{5},{6:0.00},{7},{8}",
                Time[0], evt, pmState, side, price, qty, dailyRealizedPnL, consecutiveLosses, notes == null ? string.Empty : notes.Replace(',', ';'));

            Print(line);
            if (auditWriter != null)
            {
                auditWriter.WriteLine(line);
                auditWriter.Flush();
            }
        }
    }
}
