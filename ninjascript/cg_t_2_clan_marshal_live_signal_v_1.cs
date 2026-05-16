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

// ============================================================================
// CG_T2_ClanMarshal_LiveSignal_v1
// ============================================================================
// Mission:
//   First live-signal scaffold for the T2 / ClanMarshal MNQ strategy line.
//
// Design doctrine:
//   1. Survival first.
//   2. Execution integrity second.
//   3. Profit scale third.
//
// This file is intentionally structured as a production-style execution harness
// with NT8-accessible proxy features. It is NOT a finished alpha-equivalent
// reproduction of the ClickHouse T2 model. The true CH feature parity work must
// validate and tune each proxy against historical CG_* research tables.
//
// Core execution doctrine:
//   Signal -> LIMIT first -> timeout -> conditional MARKET fallback or SKIP
//   Entry fill -> immediately submit broker-side protective stop + target
//   If protection cannot be placed safely -> flatten immediately
//
// OCO++ doctrine:
//   Broker-side SetStopLoss / SetProfitTarget are configured before entry.
//   Local logic may flatten for emergency risk, but local logic is not the only
//   protection mechanism.
//
// Operational assumptions:
//   Instrument: MNQ futures
//   Size: 1 contract default
//   RTH focus: 09:30-16:00 ET chart/session expected
//   Account selection: must be manually selected in NT8 UI
//   Initial validation: Playback101, then Sim101, then VPS, then micro-live
// ============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_T2_ClanMarshal_LiveSignal_v1 : Strategy
    {
        // --------------------------------------------------------------------
        // Internal enums
        // --------------------------------------------------------------------
        private enum SignalSide
        {
            None = 0,
            Long = 1,
            Short = -1
        }

        private enum PendingEntryKind
        {
            None,
            LimitFirst,
            MarketFallback
        }

        // --------------------------------------------------------------------
        // Orders / execution state
        // --------------------------------------------------------------------
        private Order entryOrder;
        private Order emergencyExitOrder;
        private Order stopLossOrder;
        private Order profitTargetOrder;

        private PendingEntryKind pendingEntryKind = PendingEntryKind.None;
        private SignalSide pendingSignalSide = SignalSide.None;

        private DateTime pendingEntrySubmitTime = Core.Globals.MinDate;
        private DateTime lastSignalTime = Core.Globals.MinDate;
        private DateTime lastEntryTime = Core.Globals.MinDate;
        private DateTime lastEmergencyFlatTime = Core.Globals.MinDate;
        private DateTime currentSessionDate = Core.Globals.MinDate;

        private double entryLimitPrice = 0.0;
        private double intendedStopPrice = 0.0;
        private double intendedTargetPrice = 0.0;
        private double realizedPnLToday = 0.0;
        private double sessionStartCumProfit = 0.0;

        private bool dailySoftLocked = false;
        private bool dailyHardLocked = false;
        private bool entryProtectionArmed = false;
        private bool emergencyFlattenSubmitted = false;
        private bool hasPrintedAccountWarning = false;
        private bool hasWorkingStopProtection = false;
        private bool hasWorkingTargetProtection = false;

        // --------------------------------------------------------------------
        // Proxy feature state
        // --------------------------------------------------------------------
        private double recentBuyVolume = 0.0;
        private double recentSellVolume = 0.0;
        private double recentTotalVolume = 0.0;
        private double previousTotalVolume = 0.0;
        private double recentDelta = 0.0;
        private double previousDelta = 0.0;
        private double recentTickCount = 0.0;
        private double previousTickCount = 0.0;
        private double recentPriceChangeTicks = 0.0;
        private double recentRangeTicks = 0.0;
        private double recentAbsorptionProxy = 0.0;
        private double wallScoreProxy = 0.0;
        private double totalEventSizeProxy = 0.0;
        private double eventCountDeltaProxy = 0.0;
        private double shortMomentumProxy = 0.0;

        private Series<double> tickDirectionSeries;
        private Series<double> tickVolumeSeries;
        private Series<double> signedVolumeSeries;
        private Series<double> priceSeries;

        // ====================================================================
        // User parameters
        // ====================================================================

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Quantity", Order = 1, GroupName = "01. Sizing")]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "StopTicks", Order = 2, GroupName = "02. Protection")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 400)]
        [Display(Name = "TargetTicks", Order = 3, GroupName = "02. Protection")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "LimitOffsetTicks", Order = 4, GroupName = "03. Execution")]
        public int LimitOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(100, 5000)]
        [Display(Name = "LimitTimeoutMs", Order = 5, GroupName = "03. Execution")]
        public int LimitTimeoutMs { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "LowFlowMarketThreshold", Order = 6, GroupName = "04. Fallback")]
        public double LowFlowMarketThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "MediumFlowMarketThreshold", Order = 7, GroupName = "04. Fallback")]
        public double MediumFlowMarketThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "MomentumConfirmTicks", Order = 8, GroupName = "04. Fallback")]
        public double MomentumConfirmTicks { get; set; }

        [NinjaScriptProperty]
        [Range(5, 500)]
        [Display(Name = "FeatureWindowTicks", Order = 9, GroupName = "05. Signal Proxies")]
        public int FeatureWindowTicks { get; set; }

        [NinjaScriptProperty]
        [Range(5, 500)]
        [Display(Name = "PriorFeatureWindowTicks", Order = 10, GroupName = "05. Signal Proxies")]
        public int PriorFeatureWindowTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "MinWallScoreProxy", Order = 11, GroupName = "05. Signal Proxies")]
        public double MinWallScoreProxy { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "MinDeltaAbs", Order = 12, GroupName = "05. Signal Proxies")]
        public double MinDeltaAbs { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "MinEventCountDeltaProxy", Order = 13, GroupName = "05. Signal Proxies")]
        public double MinEventCountDeltaProxy { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "MaxSpreadTicks", Order = 14, GroupName = "06. Guards")]
        public int MaxSpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "SoftDailyLossUsd", Order = 15, GroupName = "07. Daily Governors")]
        public double SoftDailyLossUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "HardDailyLossUsd", Order = 16, GroupName = "07. Daily Governors")]
        public double HardDailyLossUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(Name = "DailyProfitLockUsd", Order = 17, GroupName = "07. Daily Governors")]
        public double DailyProfitLockUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "CooldownSeconds", Order = 18, GroupName = "08. Throttle")]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "EmergencyCooldownSeconds", Order = 19, GroupName = "08. Throttle")]
        public int EmergencyCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OneTradePerSession", Order = 20, GroupName = "08. Throttle")]
        public bool OneTradePerSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseRthFilter", Order = 21, GroupName = "09. Session")]
        public bool UseRthFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "FlattenOutsideRth", Order = 21, GroupName = "09. Session")]
        public bool FlattenOutsideRth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "StartTimeEt", Description = "HHmmss, e.g. 093000", Order = 22, GroupName = "09. Session")]
        public int StartTimeEt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EndTimeEt", Description = "HHmmss, e.g. 160000", Order = 23, GroupName = "09. Session")]
        public int EndTimeEt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ExpectedAccountName", Order = 24, GroupName = "10. Diagnostics")]
        public string ExpectedAccountName { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VerboseDiagnostics", Order = 25, GroupName = "10. Diagnostics")]
        public bool VerboseDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TraceOrders", Order = 26, GroupName = "10. Diagnostics")]
        public bool TraceOrdersParam { get; set; }

        // ====================================================================
        // Lifecycle
        // ====================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_LiveSignal_v1";
                Description = "T2 ClanMarshal live-signal scaffold with limit-first execution, conditional market fallback, and OCO++ protection.";

                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Day;

                Quantity = 1;
                StopTicks = 20;
                TargetTicks = 40;
                LimitOffsetTicks = 1;
                LimitTimeoutMs = 1000;

                // These default thresholds are conservative placeholders.
                // They must be calibrated against CG_* ClickHouse research tables.
                LowFlowMarketThreshold = 100.0;
                MediumFlowMarketThreshold = 200.0;
                MomentumConfirmTicks = 2.0;
                FeatureWindowTicks = 70;
                PriorFeatureWindowTicks = 70;
                MinWallScoreProxy = 50.0;
                MinDeltaAbs = 15.0;
                MinEventCountDeltaProxy = 0.0;

                MaxSpreadTicks = 4;
                SoftDailyLossUsd = 60.0;
                HardDailyLossUsd = 200.0;
                DailyProfitLockUsd = 0.0;

                CooldownSeconds = 30;
                EmergencyCooldownSeconds = 120;
                OneTradePerSession = false;

                UseRthFilter = true;
                FlattenOutsideRth = true;
                StartTimeEt = 93000;
                EndTimeEt = 160000;

                ExpectedAccountName = "Playback101";
                VerboseDiagnostics = true;
                TraceOrdersParam = true;
            }
            else if (State == State.Configure)
            {
                TraceOrders = TraceOrdersParam;
            }
            else if (State == State.DataLoaded)
            {
                tickDirectionSeries = new Series<double>(this, MaximumBarsLookBack.TwoHundredFiftySix);
                tickVolumeSeries = new Series<double>(this, MaximumBarsLookBack.TwoHundredFiftySix);
                signedVolumeSeries = new Series<double>(this, MaximumBarsLookBack.TwoHundredFiftySix);
                priceSeries = new Series<double>(this, MaximumBarsLookBack.TwoHundredFiftySix);

                ResetSessionState();
                Log("INIT", "Strategy loaded. Validate on Playback101/Sim101 before any live use.");
            }
            else if (State == State.Realtime)
            {
                Log("REALTIME", "Strategy entered realtime state. Confirm account, instrument, quantity, stop, target, and session template.");
            }
            else if (State == State.Terminated)
            {
                Log("TERM", "Strategy terminated.");
            }
        }

        // ====================================================================
        // Main tick loop
        // ====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(FeatureWindowTicks + PriorFeatureWindowTicks + 5, 50))
                return;

            CheckSessionReset();
            UpdateDailyPnL();
            UpdateFeatureProxies();
            CheckDailyGovernors();
            PrintAccountWarningOnce();

            if (FlattenOutsideRth && UseRthFilter && !IsWithinRth() && Position.MarketPosition != MarketPosition.Flat)
            {
                SubmitEmergencyFlatten("OUTSIDE_RTH_POSITION");
                return;
            }

            if (dailyHardLocked)
            {
                if (Position.MarketPosition != MarketPosition.Flat)
                    SubmitEmergencyFlatten("DAILY_HARD_LOCK_WITH_POSITION");
                return;
            }

            ManagePendingLimitTimeout();
            ValidateOpenPositionProtection();

            if (!CanEvaluateNewSignal())
                return;

            SignalSide signal = EvaluateSignal();
            if (signal == SignalSide.None)
                return;

            SubmitLimitFirstEntry(signal);
        }

        // ====================================================================
        // Proxy feature model
        // ====================================================================
        private void UpdateFeatureProxies()
        {
            double lastPrice = Close[0];
            double prevPrice = Close[1];
            double tickDirection = Math.Sign(lastPrice - prevPrice);
            double vol = Math.Max(1.0, Volume[0]);
            double signedVol = tickDirection * vol;

            tickDirectionSeries[0] = tickDirection;
            tickVolumeSeries[0] = vol;
            signedVolumeSeries[0] = signedVol;
            priceSeries[0] = lastPrice;

            recentBuyVolume = 0.0;
            recentSellVolume = 0.0;
            recentTotalVolume = 0.0;
            previousTotalVolume = 0.0;
            recentDelta = 0.0;
            previousDelta = 0.0;
            recentTickCount = 0.0;
            previousTickCount = 0.0;

            double recentHigh = double.MinValue;
            double recentLow = double.MaxValue;

            for (int i = 0; i < FeatureWindowTicks && i <= CurrentBar; i++)
            {
                double sv = signedVolumeSeries[i];
                double tv = tickVolumeSeries[i];
                recentDelta += sv;
                recentTotalVolume += tv;
                recentTickCount += 1.0;

                if (sv > 0)
                    recentBuyVolume += tv;
                else if (sv < 0)
                    recentSellVolume += tv;

                recentHigh = Math.Max(recentHigh, priceSeries[i]);
                recentLow = Math.Min(recentLow, priceSeries[i]);
            }

            for (int i = FeatureWindowTicks; i < FeatureWindowTicks + PriorFeatureWindowTicks && i <= CurrentBar; i++)
            {
                previousDelta += signedVolumeSeries[i];
                previousTotalVolume += tickVolumeSeries[i];
                previousTickCount += 1.0;
            }

            recentPriceChangeTicks = (Close[0] - Close[Math.Min(FeatureWindowTicks, CurrentBar)]) / TickSize;
            recentRangeTicks = (recentHigh - recentLow) / TickSize;

            // Approximate absorption/wall behavior:
            //   High volume + small range implies activity concentrated without
            //   much price progress. This is only a proxy for the CH wall logic.
            recentAbsorptionProxy = recentTotalVolume / Math.Max(1.0, recentRangeTicks);

            // Approximate wall score:
            //   concentration * directional imbalance. Tunable placeholder.
            wallScoreProxy = recentAbsorptionProxy * (Math.Abs(recentDelta) / Math.Max(1.0, recentTotalVolume)) * 100.0;

            // Approximate total_event_size from CH:
            //   recent activity volume plus tick count contribution.
            totalEventSizeProxy = recentTotalVolume + recentTickCount;

            // Approximate event_count_delta:
            //   acceleration of recent activity versus prior activity.
            eventCountDeltaProxy = recentTickCount - previousTickCount;
            if (PriorFeatureWindowTicks > 0)
                eventCountDeltaProxy += (recentTotalVolume - previousTotalVolume) / Math.Max(1.0, PriorFeatureWindowTicks);

            // Approximate short momentum:
            //   short price displacement in ticks.
            shortMomentumProxy = recentPriceChangeTicks;
        }

        private SignalSide EvaluateSignal()
        {
            if (wallScoreProxy < MinWallScoreProxy)
                return SignalSide.None;

            if (Math.Abs(recentDelta) < MinDeltaAbs)
                return SignalSide.None;

            if (eventCountDeltaProxy < MinEventCountDeltaProxy)
                return SignalSide.None;

            if (recentRangeTicks <= 0)
                return SignalSide.None;

            // Directional model:
            //   Positive delta + positive momentum -> LONG
            //   Negative delta + negative momentum -> SHORT
            // This is a placeholder live proxy for the CH alpha layer.
            if (recentDelta > 0 && shortMomentumProxy >= MomentumConfirmTicks)
            {
                LogSignal("SIGNAL_LONG");
                return SignalSide.Long;
            }

            if (recentDelta < 0 && shortMomentumProxy <= -MomentumConfirmTicks)
            {
                LogSignal("SIGNAL_SHORT");
                return SignalSide.Short;
            }

            return SignalSide.None;
        }

        // ====================================================================
        // Entry execution
        // ====================================================================
        private void SubmitLimitFirstEntry(SignalSide side)
        {
            if (side == SignalSide.None)
                return;

            ArmProtectionForPotentialEntry(side);

            pendingSignalSide = side;
            pendingEntryKind = PendingEntryKind.LimitFirst;
            pendingEntrySubmitTime = Time[0];
            lastSignalTime = Time[0];
            entryProtectionArmed = true;
            emergencyFlattenSubmitted = false;

            if (side == SignalSide.Long)
            {
                entryLimitPrice = GetCurrentBidSafe() - LimitOffsetTicks * TickSize;
                Log("ENTRY_LIMIT_SUBMIT", $"LONG limit={entryLimitPrice:F2} stop={intendedStopPrice:F2} target={intendedTargetPrice:F2}");
                EnterLongLimit(Quantity, entryLimitPrice, "T2_Long_Limit");
            }
            else
            {
                entryLimitPrice = GetCurrentAskSafe() + LimitOffsetTicks * TickSize;
                Log("ENTRY_LIMIT_SUBMIT", $"SHORT limit={entryLimitPrice:F2} stop={intendedStopPrice:F2} target={intendedTargetPrice:F2}");
                EnterShortLimit(Quantity, entryLimitPrice, "T2_Short_Limit");
            }
        }

        private void ManagePendingLimitTimeout()
        {
            if (pendingEntryKind != PendingEntryKind.LimitFirst)
                return;

            if (entryOrder == null)
                return;

            if (entryOrder.OrderState == OrderState.Filled || entryOrder.OrderState == OrderState.PartFilled)
                return;

            double elapsedMs = (Time[0] - pendingEntrySubmitTime).TotalMilliseconds;
            if (elapsedMs < LimitTimeoutMs)
                return;

            Log("LIMIT_TIMEOUT", $"elapsedMs={elapsedMs:F0} flow={totalEventSizeProxy:F2} momentum={shortMomentumProxy:F2}");

            CancelOrder(entryOrder);

            if (ShouldUseMarketFallback(pendingSignalSide))
                SubmitMarketFallback(pendingSignalSide);
            else
                SkipPendingEntry("FALLBACK_REJECTED_HIGH_FLOW_OR_NO_MOMENTUM");
        }

        private bool ShouldUseMarketFallback(SignalSide side)
        {
            if (side == SignalSide.None)
                return false;

            if (totalEventSizeProxy <= LowFlowMarketThreshold)
                return true;

            if (totalEventSizeProxy <= MediumFlowMarketThreshold)
            {
                if (side == SignalSide.Long && shortMomentumProxy >= MomentumConfirmTicks)
                    return true;

                if (side == SignalSide.Short && shortMomentumProxy <= -MomentumConfirmTicks)
                    return true;
            }

            return false;
        }

        private void SubmitMarketFallback(SignalSide side)
        {
            if (side == SignalSide.None)
                return;

            ArmProtectionForPotentialEntry(side);

            pendingEntryKind = PendingEntryKind.MarketFallback;
            pendingEntrySubmitTime = Time[0];
            entryProtectionArmed = true;

            if (side == SignalSide.Long)
            {
                Log("ENTRY_MARKET_FALLBACK", $"LONG flow={totalEventSizeProxy:F2} momentum={shortMomentumProxy:F2}");
                EnterLong(Quantity, "T2_Long_MarketFallback");
            }
            else
            {
                Log("ENTRY_MARKET_FALLBACK", $"SHORT flow={totalEventSizeProxy:F2} momentum={shortMomentumProxy:F2}");
                EnterShort(Quantity, "T2_Short_MarketFallback");
            }
        }

        private void SkipPendingEntry(string reason)
        {
            Log("ENTRY_SKIP", reason);
            pendingEntryKind = PendingEntryKind.None;
            pendingSignalSide = SignalSide.None;
            pendingEntrySubmitTime = Core.Globals.MinDate;
            entryProtectionArmed = false;
            entryOrder = null;
        }

        // ====================================================================
        // Protection / OCO++
        // ====================================================================
        private void ArmProtectionForPotentialEntry(SignalSide side)
        {
            double referencePrice = Close[0];

            if (side == SignalSide.Long)
            {
                intendedStopPrice = referencePrice - StopTicks * TickSize;
                intendedTargetPrice = referencePrice + TargetTicks * TickSize;
                SetStopLoss("T2_Long_Limit", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("T2_Long_Limit", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("T2_Long_MarketFallback", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("T2_Long_MarketFallback", CalculationMode.Ticks, TargetTicks);
            }
            else if (side == SignalSide.Short)
            {
                intendedStopPrice = referencePrice + StopTicks * TickSize;
                intendedTargetPrice = referencePrice - TargetTicks * TickSize;
                SetStopLoss("T2_Short_Limit", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("T2_Short_Limit", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("T2_Short_MarketFallback", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("T2_Short_MarketFallback", CalculationMode.Ticks, TargetTicks);
            }
        }

        private void ValidateOpenPositionProtection()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            // Managed SetStopLoss/SetProfitTarget orders are the primary OCO++
            // protection. Do NOT flatten merely because price has approached or
            // touched the stop; in fast playback/live feeds the managed stop is
            // supposed to handle that. Emergency flatten is reserved for missing,
            // rejected, cancelled, or obviously absent protection.
            if (!entryProtectionArmed)
            {
                SubmitEmergencyFlatten("POSITION_WITHOUT_ARMED_PROTECTION_FLAG");
                return;
            }

            if (!hasWorkingStopProtection)
            {
                SubmitEmergencyFlatten("POSITION_WITHOUT_WORKING_STOP_ORDER");
                return;
            }
        }

        private void SubmitEmergencyFlatten(string reason)
        {
            if (emergencyFlattenSubmitted)
                return;

            emergencyFlattenSubmitted = true;
            lastEmergencyFlatTime = Time[0];

            Log("EMERGENCY_FLAT", reason);

            if (Position.MarketPosition == MarketPosition.Long)
                emergencyExitOrder = ExitLong(Position.Quantity, "T2_EmergencyFlat_Long", "");
            else if (Position.MarketPosition == MarketPosition.Short)
                emergencyExitOrder = ExitShort(Position.Quantity, "T2_EmergencyFlat_Short", "");
        }

        // ====================================================================
        // Order / execution callbacks
        // ====================================================================
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
            int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null)
                return;

            if (order.Name == "T2_Long_Limit" || order.Name == "T2_Short_Limit" ||
                order.Name == "T2_Long_MarketFallback" || order.Name == "T2_Short_MarketFallback")
            {
                entryOrder = order;
            }

            if (order.Name == "Stop loss")
            {
                stopLossOrder = order;
                hasWorkingStopProtection = orderState == OrderState.Accepted ||
                                           orderState == OrderState.Working ||
                                           orderState == OrderState.PartFilled;
            }

            if (order.Name == "Profit target")
            {
                profitTargetOrder = order;
                hasWorkingTargetProtection = orderState == OrderState.Accepted ||
                                             orderState == OrderState.Working ||
                                             orderState == OrderState.PartFilled;
            }

            if (VerboseDiagnostics)
            {
                Log("ORDER_UPDATE", $"name={order.Name} state={orderState} qty={quantity} filled={filled} avg={averageFillPrice:F2} err={error} native={nativeError}");
            }

            if (error != ErrorCode.NoError)
            {
                Log("ORDER_ERROR", $"name={order.Name} error={error} native={nativeError}");

                if (Position.MarketPosition != MarketPosition.Flat)
                    SubmitEmergencyFlatten("ORDER_ERROR_WITH_POSITION");
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            string orderName = execution.Order.Name;
            Log("EXECUTION", $"name={orderName} price={price:F2} qty={quantity} mp={marketPosition}");

            if (orderName == "T2_Long_Limit" || orderName == "T2_Short_Limit" ||
                orderName == "T2_Long_MarketFallback" || orderName == "T2_Short_MarketFallback")
            {
                lastEntryTime = time;
                pendingEntryKind = PendingEntryKind.None;
                entryProtectionArmed = true;
                emergencyFlattenSubmitted = false;

                // Recompute intended protective geometry using actual fill price.
                if (marketPosition == MarketPosition.Long)
                {
                    intendedStopPrice = price - StopTicks * TickSize;
                    intendedTargetPrice = price + TargetTicks * TickSize;
                }
                else if (marketPosition == MarketPosition.Short)
                {
                    intendedStopPrice = price + StopTicks * TickSize;
                    intendedTargetPrice = price - TargetTicks * TickSize;
                }

                Log("ENTRY_FILLED", $"fill={price:F2} intendedStop={intendedStopPrice:F2} intendedTarget={intendedTargetPrice:F2}");
            }

            if (orderName.Contains("Stop") || orderName.Contains("Target") || orderName.Contains("EmergencyFlat"))
            {
                Log("EXIT_EXECUTION", $"name={orderName} price={price:F2} qty={quantity}");
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            Log("POSITION", $"mp={marketPosition} qty={quantity} avg={averagePrice:F2}");

            if (marketPosition == MarketPosition.Flat)
            {
                pendingEntryKind = PendingEntryKind.None;
                pendingSignalSide = SignalSide.None;
                entryOrder = null;
                emergencyExitOrder = null;
                stopLossOrder = null;
                profitTargetOrder = null;
                entryProtectionArmed = false;
                hasWorkingStopProtection = false;
                hasWorkingTargetProtection = false;
                emergencyFlattenSubmitted = false;
                intendedStopPrice = 0.0;
                intendedTargetPrice = 0.0;
            }
        }

        // ====================================================================
        // Guards / governance
        // ====================================================================
        private bool CanEvaluateNewSignal()
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return false;

            if (pendingEntryKind != PendingEntryKind.None)
                return false;

            if (dailySoftLocked || dailyHardLocked)
                return false;

            if (UseRthFilter && !IsWithinRth())
                return false;

            if (OneTradePerSession && lastEntryTime.Date == Time[0].Date)
                return false;

            if ((Time[0] - lastEntryTime).TotalSeconds < CooldownSeconds)
                return false;

            if ((Time[0] - lastEmergencyFlatTime).TotalSeconds < EmergencyCooldownSeconds)
                return false;

            if (!SpreadGuardPasses())
                return false;

            return true;
        }

        private bool SpreadGuardPasses()
        {
            double bid = GetCurrentBidSafe();
            double ask = GetCurrentAskSafe();
            double spreadTicks = (ask - bid) / TickSize;

            if (spreadTicks < 0)
                return false;

            if (spreadTicks > MaxSpreadTicks)
            {
                if (VerboseDiagnostics)
                    Log("SPREAD_REJECT", $"spreadTicks={spreadTicks:F1} max={MaxSpreadTicks}");
                return false;
            }

            return true;
        }

        private bool IsWithinRth()
        {
            int now = ToTime(Time[0]);
            return now >= StartTimeEt && now <= EndTimeEt;
        }

        private void CheckDailyGovernors()
        {
            if (!dailySoftLocked && realizedPnLToday <= -Math.Abs(SoftDailyLossUsd))
            {
                dailySoftLocked = true;
                Log("DAILY_SOFT_LOCK", $"realizedPnLToday={realizedPnLToday:F2}");
            }

            if (!dailyHardLocked && realizedPnLToday <= -Math.Abs(HardDailyLossUsd))
            {
                dailyHardLocked = true;
                Log("DAILY_HARD_LOCK", $"realizedPnLToday={realizedPnLToday:F2}");
            }

            if (DailyProfitLockUsd > 0 && realizedPnLToday >= DailyProfitLockUsd)
            {
                dailySoftLocked = true;
                Log("DAILY_PROFIT_LOCK", $"realizedPnLToday={realizedPnLToday:F2}");
            }
        }

        private void UpdateDailyPnL()
        {
            realizedPnLToday = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartCumProfit;
        }

        private void CheckSessionReset()
        {
            if (currentSessionDate == Core.Globals.MinDate || currentSessionDate.Date != Time[0].Date)
            {
                currentSessionDate = Time[0].Date;
                ResetSessionState();
                Log("SESSION_RESET", $"date={currentSessionDate:yyyy-MM-dd}");
            }
        }

        private void ResetSessionState()
        {
            dailySoftLocked = false;
            dailyHardLocked = false;
            realizedPnLToday = 0.0;
            sessionStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            pendingEntryKind = PendingEntryKind.None;
            pendingSignalSide = SignalSide.None;
            entryOrder = null;
            emergencyExitOrder = null;
            stopLossOrder = null;
            profitTargetOrder = null;
            entryProtectionArmed = false;
            hasWorkingStopProtection = false;
            hasWorkingTargetProtection = false;
            emergencyFlattenSubmitted = false;
        }

        // ====================================================================
        // Market data helpers
        // ====================================================================
        private double GetCurrentBidSafe()
        {
            double bid = GetCurrentBid();
            if (bid <= 0 || double.IsNaN(bid))
                bid = Close[0] - TickSize;
            return bid;
        }

        private double GetCurrentAskSafe()
        {
            double ask = GetCurrentAsk();
            if (ask <= 0 || double.IsNaN(ask))
                ask = Close[0] + TickSize;
            return ask;
        }

        // ====================================================================
        // Diagnostics
        // ====================================================================
        private void PrintAccountWarningOnce()
        {
            if (hasPrintedAccountWarning)
                return;

            hasPrintedAccountWarning = true;

            string accountName = Account != null ? Account.Name : "UNKNOWN";
            if (!string.IsNullOrWhiteSpace(ExpectedAccountName) && accountName != ExpectedAccountName)
            {
                Log("ACCOUNT_WARNING", $"Selected account is '{accountName}', expected '{ExpectedAccountName}'. NT8 account selection must be done manually in strategy UI.");
            }
            else
            {
                Log("ACCOUNT_OK", $"Selected account='{accountName}'.");
            }
        }

        private void LogSignal(string tag)
        {
            Log(tag, $"wall={wallScoreProxy:F2} flow={totalEventSizeProxy:F2} eventDelta={eventCountDeltaProxy:F2} delta={recentDelta:F2} momentum={shortMomentumProxy:F2} rangeTicks={recentRangeTicks:F2}");
        }

        private void Log(string tag, string message)
        {
            if (!VerboseDiagnostics && tag != "ACCOUNT_WARNING" && tag != "EMERGENCY_FLAT" && tag != "ORDER_ERROR")
                return;

            Print($"[{Time[0]:yyyy-MM-dd HH:mm:ss.fff}] [{Name}] [{tag}] {message}");
        }
    }
}
