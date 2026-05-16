// CG_HybridV4_MNQ_Strategy.cs
// NinjaTrader 8 Strategy
//
// Strategy purpose:
//   Execution-aware MNQ intraday strategy based on Hybrid v4 model:
//   1. Generate LONG / SHORT / NONE signal from order-flow features.
//   2. Submit LIMIT order first.
//   3. If not filled after timeout, conditionally fall back to MARKET:
//        - Low flow: market
//        - Medium flow + confirming momentum: market
//        - High flow: skip
//   4. Enforce fixed stop/target, RTH-only trading, one-position-at-a-time,
//      and daily loss lockout.
//
// IMPORTANT:
//   This file contains a working execution skeleton with live-calculated proxy features.
//   You should replace BuildSignal() with the exact signal logic validated in ClickHouse
//   if/when you export the final thresholds.

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_HybridV4_MNQ_Strategy : Strategy
    {
        // =============================
        // Internal enums
        // =============================

        private enum EntrySide
        {
            None,
            Long,
            Short
        }

        private enum ExecutionState
        {
            Flat,
            LimitPending,
            CancelPending,
            FallbackDecision,
            MarketPending,
            InPosition,
            LockedOut
        }

        // =============================
        // User parameters
        // =============================

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Quantity", Order = 1, GroupName = "01. Risk")]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "StopTicks", Order = 2, GroupName = "01. Risk")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "TargetTicks", Order = 3, GroupName = "01. Risk")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SoftDailyLossUsd", Order = 4, GroupName = "01. Risk")]
        public double SoftDailyLossUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HardDailyLossUsd", Order = 5, GroupName = "01. Risk")]
        public double HardDailyLossUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 10.0)]
        [Display(Name = "LimitTimeoutSeconds", Order = 1, GroupName = "02. Execution")]
        public double LimitTimeoutSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "EntryOffsetTicks", Order = 2, GroupName = "02. Execution")]
        public int EntryOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "LowFlowThreshold", Order = 3, GroupName = "02. Execution")]
        public double LowFlowThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MediumFlowThreshold", Order = 4, GroupName = "02. Execution")]
        public double MediumFlowThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MomentumConfirmTicks", Order = 5, GroupName = "02. Execution")]
        public double MomentumConfirmTicks { get; set; }

        [NinjaScriptProperty]
        [Range(100, 5000)]
        [Display(Name = "FeatureWindowMs", Order = 1, GroupName = "03. Features")]
        public int FeatureWindowMs { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MinDeltaForSignal", Order = 2, GroupName = "03. Features")]
        public double MinDeltaForSignal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MinMomentumTicksForSignal", Order = 3, GroupName = "03. Features")]
        public double MinMomentumTicksForSignal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UsePlaceholderSignal", Order = 4, GroupName = "03. Features")]
        public bool UsePlaceholderSignal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseRthOnly", Order = 1, GroupName = "04. Session")]
        public bool UseRthOnly { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RthStart", Order = 2, GroupName = "04. Session")]
        public int RthStart { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RthEnd", Order = 3, GroupName = "04. Session")]
        public int RthEnd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDebug", Order = 1, GroupName = "99. Debug")]
        public bool PrintDebug { get; set; }

        // =============================
        // Internal execution state
        // =============================

        private ExecutionState execState;
        private EntrySide pendingSide;
        private Order entryLimitOrder;
        private Order entryMarketOrder;
        private DateTime limitSubmitTime;
        private DateTime lastSignalTime;
        private double pendingLimitPrice;

        // =============================
        // Intraday risk state
        // =============================

        private DateTime currentSessionDate;
        private double sessionStartCumProfit;
        private double realizedSessionPnl;
        private bool softLocked;
        private bool hardLocked;

        // =============================
        // Lightweight rolling feature state
        // =============================

        private DateTime featureWindowStart;
        private double windowBuyVolume;
        private double windowSellVolume;
        private double windowTotalEventSize;
        private int windowEventCount;
        private double windowStartPrice;
        private double lastTradePrice;
        private double lastBid;
        private double lastAsk;

        // =============================
        // Ninja lifecycle
        // =============================

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_HybridV4_MNQ_Strategy";
                Description = "Hybrid v4 MNQ execution strategy: limit-first, timeout, conditional market fallback, daily loss lockout.";

                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsUnmanaged = false;
                IncludeCommission = true;
                BarsRequiredToTrade = 20;

                Quantity = 1;
                StopTicks = 20;
                TargetTicks = 40;
                SoftDailyLossUsd = -60.0;
                HardDailyLossUsd = -200.0;

                LimitTimeoutSeconds = 1.0;
                EntryOffsetTicks = 0;
                LowFlowThreshold = 100.0;
                MediumFlowThreshold = 200.0;
                MomentumConfirmTicks = 1.0;

                FeatureWindowMs = 500;
                MinDeltaForSignal = 15.0;
                MinMomentumTicksForSignal = 1.0;
                UsePlaceholderSignal = true;

                UseRthOnly = true;
                RthStart = 93000;
                RthEnd = 160000;

                PrintDebug = true;
            }
            else if (State == State.Configure)
            {
                // Fixed risk model from the restart doc.
                // No trailing stop is used because prior trailing tests were rejected as path-leaky.
                SetStopLoss(CalculationMode.Ticks, StopTicks);
                SetProfitTarget(CalculationMode.Ticks, TargetTicks);
            }
            else if (State == State.DataLoaded)
            {
                ResetAllRuntimeState();
            }
        }

        // =============================
        // Market data / feature updates
        // =============================

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType == MarketDataType.Bid)
            {
                lastBid = e.Price;
                return;
            }

            if (e.MarketDataType == MarketDataType.Ask)
            {
                lastAsk = e.Price;
                return;
            }

            if (e.MarketDataType != MarketDataType.Last)
                return;

            UpdateRollingFeatures(e.Time, e.Price, e.Volume);
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            // Placeholder for future L2 gating.
            // For live parity with the ClickHouse/MBO model, this is where you would accumulate:
            //   - bid/ask pulling
            //   - stacking
            //   - depth imbalance
            //   - liquidity vacuum
            // The current v4 model uses total_event_size and short_momentum proxies from tick flow.
        }

        // =============================
        // Main strategy loop
        // =============================

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            UpdateSessionRiskState();

            if (hardLocked)
            {
                execState = ExecutionState.LockedOut;
                return;
            }

            if (UseRthOnly && !IsRth())
                return;

            // Daily soft lockout blocks new entries, but does not interfere with active position management.
            if (softLocked && Position.MarketPosition == MarketPosition.Flat)
            {
                execState = hardLocked ? ExecutionState.LockedOut : ExecutionState.Flat;
                return;
            }

            // Keep state aligned with actual position.
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                execState = ExecutionState.InPosition;
                return;
            }

            // If flat and no orders are active, look for new signal.
            if (execState == ExecutionState.Flat)
            {
                TrySubmitInitialLimit();
                return;
            }

            // Timeout monitor for initial limit order.
            if (execState == ExecutionState.LimitPending)
            {
                MonitorLimitTimeout();
                return;
            }

            // Once cancellation is acknowledged, fallback decision occurs in OnOrderUpdate.
            if (execState == ExecutionState.FallbackDecision)
            {
                ExecuteFallbackDecision();
                return;
            }
        }

        // =============================
        // Order state management
        // =============================

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
            int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (entryLimitOrder != null && order.OrderId == entryLimitOrder.OrderId)
            {
                entryLimitOrder = order;

                if (orderState == OrderState.Filled || orderState == OrderState.PartFilled)
                {
                    DebugPrint("[fill] initial limit filled side=" + pendingSide + " avg=" + averageFillPrice);
                    execState = ExecutionState.InPosition;
                    return;
                }

                if (orderState == OrderState.Cancelled)
                {
                    DebugPrint("[cancelled] initial limit cancelled; deciding fallback");
                    entryLimitOrder = null;
                    execState = ExecutionState.FallbackDecision;
                    return;
                }

                if (orderState == OrderState.Rejected)
                {
                    DebugPrint("[reject] initial limit rejected: " + nativeError);
                    ClearPendingEntryState();
                    execState = ExecutionState.Flat;
                    return;
                }
            }

            if (entryMarketOrder != null && order.OrderId == entryMarketOrder.OrderId)
            {
                entryMarketOrder = order;

                if (orderState == OrderState.Filled || orderState == OrderState.PartFilled)
                {
                    DebugPrint("[fill] fallback market filled side=" + pendingSide + " avg=" + averageFillPrice);
                    execState = ExecutionState.InPosition;
                    return;
                }

                if (orderState == OrderState.Rejected || orderState == OrderState.Cancelled)
                {
                    DebugPrint("[market-failed] fallback order state=" + orderState + " msg=" + nativeError);
                    ClearPendingEntryState();
                    execState = ExecutionState.Flat;
                    return;
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            UpdateSessionRiskState();

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                ClearPendingEntryState();
                execState = hardLocked ? ExecutionState.LockedOut : ExecutionState.Flat;
            }
        }

        // =============================
        // Initial entry logic
        // =============================

        private void TrySubmitInitialLimit()
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (softLocked || hardLocked)
                return;

            EntrySide signal = BuildSignal();
            if (signal == EntrySide.None)
                return;

            // Prevent duplicate submissions on the same timestamp/tick.
            if (lastSignalTime == Time[0])
                return;

            pendingSide = signal;
            lastSignalTime = Time[0];

            pendingLimitPrice = GetLimitEntryPrice(signal);
            limitSubmitTime = Time[0];

            DebugPrint("[entry] signal=" + signal
                + " limit=" + pendingLimitPrice
                + " flow=" + CurrentTotalEventSize().ToString("0.##")
                + " momentumTicks=" + CurrentMomentumTicks().ToString("0.##")
                + " delta=" + CurrentDelta().ToString("0.##"));

            if (signal == EntrySide.Long)
                entryLimitOrder = EnterLongLimit(Quantity, pendingLimitPrice, "CGV4_LimitLong");
            else if (signal == EntrySide.Short)
                entryLimitOrder = EnterShortLimit(Quantity, pendingLimitPrice, "CGV4_LimitShort");

            execState = ExecutionState.LimitPending;
        }

        private void MonitorLimitTimeout()
        {
            if (entryLimitOrder == null)
            {
                execState = ExecutionState.Flat;
                return;
            }

            double ageSeconds = (Time[0] - limitSubmitTime).TotalSeconds;
            if (ageSeconds < LimitTimeoutSeconds)
                return;

            if (entryLimitOrder.OrderState == OrderState.Working
                || entryLimitOrder.OrderState == OrderState.Accepted
                || entryLimitOrder.OrderState == OrderState.Submitted)
            {
                DebugPrint("[timeout] cancelling stale limit ageSeconds=" + ageSeconds.ToString("0.000"));
                execState = ExecutionState.CancelPending;
                CancelOrder(entryLimitOrder);
            }
        }

        private void ExecuteFallbackDecision()
        {
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                execState = ExecutionState.InPosition;
                return;
            }

            if (softLocked || hardLocked)
            {
                DebugPrint("[skip] fallback blocked by daily lockout");
                ClearPendingEntryState();
                execState = hardLocked ? ExecutionState.LockedOut : ExecutionState.Flat;
                return;
            }

            double flow = CurrentTotalEventSize();
            bool momentumOK = MomentumConfirmsSide(pendingSide);

            bool useMarket = false;
            string reason = "";

            // Hybrid v4:
            //   low flow -> market
            //   medium flow + confirming momentum -> market
            //   high flow -> skip
            if (flow <= LowFlowThreshold)
            {
                useMarket = true;
                reason = "low-flow";
            }
            else if (flow <= MediumFlowThreshold && momentumOK)
            {
                useMarket = true;
                reason = "medium-flow-momentum-confirmed";
            }
            else
            {
                useMarket = false;
                reason = "high-flow-or-no-momentum";
            }

            if (!useMarket)
            {
                DebugPrint("[skip] fallback skipped reason=" + reason
                    + " flow=" + flow.ToString("0.##")
                    + " momentumTicks=" + CurrentMomentumTicks().ToString("0.##"));
                ClearPendingEntryState();
                execState = ExecutionState.Flat;
                return;
            }

            DebugPrint("[market] fallback side=" + pendingSide
                + " reason=" + reason
                + " flow=" + flow.ToString("0.##")
                + " momentumTicks=" + CurrentMomentumTicks().ToString("0.##"));

            if (pendingSide == EntrySide.Long)
                entryMarketOrder = EnterLong(Quantity, "CGV4_MarketLong");
            else if (pendingSide == EntrySide.Short)
                entryMarketOrder = EnterShort(Quantity, "CGV4_MarketShort");

            execState = ExecutionState.MarketPending;
        }

        // =============================
        // Signal layer
        // =============================

        private EntrySide BuildSignal()
        {
            // Strategy/methodology note:
            //   This placeholder creates a simple order-flow signal from the same feature family
            //   used in the research notes: delta + short momentum. It is intentionally conservative
            //   and exists so the execution model compiles and runs.
            //
            // Replace this with the ClickHouse-validated LONG/SHORT/NONE signal export when ready.
            // For exact parity, this function should become a direct translation of the query/table
            // that produced CG_mnq_hybrid_model_v4_executed.

            if (!UsePlaceholderSignal)
                return EntrySide.None;

            double delta = CurrentDelta();
            double momentumTicks = CurrentMomentumTicks();

            if (delta >= MinDeltaForSignal && momentumTicks >= MinMomentumTicksForSignal)
                return EntrySide.Long;

            if (delta <= -MinDeltaForSignal && momentumTicks <= -MinMomentumTicksForSignal)
                return EntrySide.Short;

            return EntrySide.None;
        }

        // =============================
        // Feature calculations
        // =============================

        private void UpdateRollingFeatures(DateTime eventTime, double price, double volume)
        {
            if (featureWindowStart == DateTime.MinValue)
            {
                featureWindowStart = eventTime;
                windowStartPrice = price;
            }

            double elapsedMs = (eventTime - featureWindowStart).TotalMilliseconds;
            if (elapsedMs >= FeatureWindowMs)
            {
                featureWindowStart = eventTime;
                windowBuyVolume = 0;
                windowSellVolume = 0;
                windowTotalEventSize = 0;
                windowEventCount = 0;
                windowStartPrice = price;
            }

            // Aggressor-side approximation:
            //   Last at/above ask => buyer initiated
            //   Last at/below bid => seller initiated
            //   Otherwise use price change fallback.
            if (lastAsk > 0 && price >= lastAsk)
                windowBuyVolume += volume;
            else if (lastBid > 0 && price <= lastBid)
                windowSellVolume += volume;
            else if (lastTradePrice > 0 && price > lastTradePrice)
                windowBuyVolume += volume;
            else if (lastTradePrice > 0 && price < lastTradePrice)
                windowSellVolume += volume;
            else
                windowTotalEventSize += volume * 0.25; // neutral activity contribution

            windowTotalEventSize += volume;
            windowEventCount++;
            lastTradePrice = price;
        }

        private double CurrentDelta()
        {
            return windowBuyVolume - windowSellVolume;
        }

        private double CurrentTotalEventSize()
        {
            return windowTotalEventSize;
        }

        private double CurrentMomentumTicks()
        {
            if (windowStartPrice <= 0 || lastTradePrice <= 0 || TickSize <= 0)
                return 0;

            return (lastTradePrice - windowStartPrice) / TickSize;
        }

        private bool MomentumConfirmsSide(EntrySide side)
        {
            double m = CurrentMomentumTicks();

            if (side == EntrySide.Long)
                return m >= MomentumConfirmTicks;

            if (side == EntrySide.Short)
                return m <= -MomentumConfirmTicks;

            return false;
        }

        // =============================
        // Price helpers
        // =============================

        private double GetLimitEntryPrice(EntrySide side)
        {
            // For long, try to buy passively at bid or slightly improved.
            // For short, try to sell passively at ask or slightly improved.
            // EntryOffsetTicks allows experimentation:
            //   0 = bid for long / ask for short
            //   positive offset moves toward/through the spread.

            if (side == EntrySide.Long)
            {
                double basePrice = lastBid > 0 ? lastBid : GetCurrentBid();
                if (basePrice <= 0)
                    basePrice = Close[0];

                return Instrument.MasterInstrument.RoundToTickSize(basePrice + EntryOffsetTicks * TickSize);
            }

            if (side == EntrySide.Short)
            {
                double basePrice = lastAsk > 0 ? lastAsk : GetCurrentAsk();
                if (basePrice <= 0)
                    basePrice = Close[0];

                return Instrument.MasterInstrument.RoundToTickSize(basePrice - EntryOffsetTicks * TickSize);
            }

            return Close[0];
        }

        // =============================
        // Session and risk
        // =============================

        private bool IsRth()
        {
            int t = ToTime(Time[0]);
            return t >= RthStart && t <= RthEnd;
        }

        private void UpdateSessionRiskState()
        {
            DateTime barDate = Time[0].Date;

            if (currentSessionDate != barDate)
            {
                currentSessionDate = barDate;
                sessionStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                realizedSessionPnl = 0;
                softLocked = false;
                hardLocked = false;
                ClearPendingEntryState();
                execState = ExecutionState.Flat;
                DebugPrint("[session] reset date=" + currentSessionDate.ToString("yyyy-MM-dd"));
            }

            realizedSessionPnl = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartCumProfit;

            if (!softLocked && realizedSessionPnl <= SoftDailyLossUsd)
            {
                softLocked = true;
                DebugPrint("[risk] soft daily loss lockout pnl=" + realizedSessionPnl.ToString("0.00"));
            }

            if (!hardLocked && realizedSessionPnl <= HardDailyLossUsd)
            {
                hardLocked = true;
                softLocked = true;
                DebugPrint("[risk] hard daily loss lockout pnl=" + realizedSessionPnl.ToString("0.00"));

                // Flatten active position if the hard stop is reached after a fill/exit update.
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("CGV4_HardStopExit", "CGV4_MarketLong");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("CGV4_HardStopExit", "CGV4_MarketShort");

                if (entryLimitOrder != null)
                    CancelOrder(entryLimitOrder);

                execState = ExecutionState.LockedOut;
            }
        }

        // =============================
        // State cleanup
        // =============================

        private void ResetAllRuntimeState()
        {
            execState = ExecutionState.Flat;
            pendingSide = EntrySide.None;
            entryLimitOrder = null;
            entryMarketOrder = null;
            limitSubmitTime = DateTime.MinValue;
            lastSignalTime = DateTime.MinValue;
            pendingLimitPrice = 0;

            currentSessionDate = DateTime.MinValue;
            sessionStartCumProfit = 0;
            realizedSessionPnl = 0;
            softLocked = false;
            hardLocked = false;

            featureWindowStart = DateTime.MinValue;
            windowBuyVolume = 0;
            windowSellVolume = 0;
            windowTotalEventSize = 0;
            windowEventCount = 0;
            windowStartPrice = 0;
            lastTradePrice = 0;
            lastBid = 0;
            lastAsk = 0;
        }

        private void ClearPendingEntryState()
        {
            pendingSide = EntrySide.None;
            entryLimitOrder = null;
            entryMarketOrder = null;
            limitSubmitTime = DateTime.MinValue;
            pendingLimitPrice = 0;
        }

        private void DebugPrint(string message)
        {
            if (!PrintDebug)
                return;

            Print(Time[0].ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message);
        }
    }
}
