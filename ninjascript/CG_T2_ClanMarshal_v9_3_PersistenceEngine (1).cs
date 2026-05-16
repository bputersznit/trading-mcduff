// =================================================================================================
// CG_T2_ClanMarshal_v9_3_PersistenceEngine.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-06 16:10:00 EDT
//
// PROJECT:
//   MNQ Intraday Strategy — LIGHT HYBRID Persistence Engine
//
// PURPOSE:
//   Complete standalone NinjaScript strategy intended to replace the smaller skeleton file.
//   This version is designed for MNQ 1-tick chart playback/live simulation with:
//     - one MNQ contract maximum
//     - no overlapping positions
//     - no emergency lockout
//     - broker/strategy protective stop and target brackets
//     - internal synthetic auction bars from tick stream
//     - persistent auction-energy model to reduce UpTrend/None/DownTrend thrashing
//     - continuation entries during persistent auctions
//     - fade entries only during neutral auction conditions
//
// REQUIRED CHART:
//   Instrument: MNQ
//   Primary chart: 1 Tick
//   Do NOT add secondary data series manually.
//   This strategy synthesizes its own internal auction bars from the 1-tick stream.
//
// STRATEGY PHILOSOPHY:
//   Strong expanding auction:
//      Join continuation on controlled pullback and reclaim.
//   Neutral / rotational auction:
//      Fade exhausted local extremes.
//   Violent disorder / unstable state:
//      Stand aside.
//
// CRITICAL GOVERNANCE:
//   - Quantity is hard-capped to 1.
//   - Entries only when Position.MarketPosition == Flat.
//   - pendingEntry lock prevents duplicate submissions.
//   - same-bar lock prevents immediate re-entry loops.
//   - post-exit cooldown reduces same-price churn.
//   - NO emergency lockout is used.
//   - Protective stop and target are set before entries through SetStopLoss/SetProfitTarget.
//
// NOTES:
//   This file intentionally avoids AddDataSeries to reduce NT8 playback instability.
//   It also avoids complex external dependencies and is intended to compile as a single Strategy file.
//
// =================================================================================================

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
    public class CG_T2_ClanMarshal_v9_3_PersistenceEngine : Strategy
    {
        // =========================================================================================
        // ENUMS
        // =========================================================================================

        private enum AuctionState
        {
            None,
            BuildingUp,
            ConfirmedUp,
            ExhaustingUp,
            BuildingDown,
            ConfirmedDown,
            ExhaustingDown,
            Chop
        }

        // =========================================================================================
        // USER PARAMETERS
        // =========================================================================================

        [NinjaScriptProperty]
        [Display(Name = "Use RTH Filter", GroupName = "01. Session", Order = 1)]
        public bool UseRthFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RTH Start HHmmss", GroupName = "01. Session", Order = 2)]
        public int RthStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RTH End HHmmss", GroupName = "01. Session", Order = 3)]
        public int RthEndTime { get; set; }

        [NinjaScriptProperty]
        [Range(1, 300)]
        [Display(Name = "Synthetic Bar Seconds", GroupName = "02. Auction Bars", Order = 1)]
        public int SyntheticBarSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Synthetic Bars Required", GroupName = "02. Auction Bars", Order = 2)]
        public int SyntheticBarsRequired { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Energy Confirm Threshold", GroupName = "03. Persistence", Order = 1)]
        public double EnergyConfirmThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Energy Build Threshold", GroupName = "03. Persistence", Order = 2)]
        public double EnergyBuildThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Energy Exit Threshold", GroupName = "03. Persistence", Order = 3)]
        public double EnergyExitThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.900, 0.999)]
        [Display(Name = "Energy Decay", GroupName = "03. Persistence", Order = 4)]
        public double EnergyDecay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Exhaustion Threshold", GroupName = "03. Persistence", Order = 5)]
        public double ExhaustionThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Trend Target Ticks", GroupName = "04. Brackets", Order = 1)]
        public int TrendTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Trend Stop Ticks", GroupName = "04. Brackets", Order = 2)]
        public int TrendStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Fade Target Ticks", GroupName = "04. Brackets", Order = 3)]
        public int FadeTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Fade Stop Ticks", GroupName = "04. Brackets", Order = 4)]
        public int FadeStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Pullback Min Ticks", GroupName = "05. Continuation", Order = 1)]
        public int PullbackMinTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Pullback Max Ticks", GroupName = "05. Continuation", Order = 2)]
        public int PullbackMaxTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Reclaim Ticks", GroupName = "05. Continuation", Order = 3)]
        public int ReclaimTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Fade Extreme Ticks", GroupName = "06. Fade", Order = 1)]
        public int FadeExtremeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 300)]
        [Display(Name = "Entry Cooldown Seconds", GroupName = "07. Governance", Order = 1)]
        public int EntryCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 300)]
        [Display(Name = "Post Exit Cooldown Seconds", GroupName = "07. Governance", Order = 2)]
        public int PostExitCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Diagnostic Every Ticks", GroupName = "08. Diagnostics", Order = 1)]
        public int DiagnosticEveryTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Print Mode Changes", GroupName = "08. Diagnostics", Order = 2)]
        public bool PrintModeChanges { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Print Entries", GroupName = "08. Diagnostics", Order = 3)]
        public bool PrintEntries { get; set; }

        // =========================================================================================
        // INTERNAL STATE
        // =========================================================================================

        private AuctionState auctionState;
        private AuctionState previousPrintedState;

        private double auctionEnergy;
        private double exhaustionScore;

        private double syntheticOpen;
        private double syntheticHigh;
        private double syntheticLow;
        private double syntheticClose;
        private DateTime syntheticStartTime;
        private bool syntheticInitialized;

        private double priorSyntheticClose1;
        private double priorSyntheticClose2;
        private double priorSyntheticOpen1;
        private double priorSyntheticOpen2;
        private double priorSyntheticHigh1;
        private double priorSyntheticHigh2;
        private double priorSyntheticLow1;
        private double priorSyntheticLow2;
        private int completedSyntheticBars;

        private double sessionHigh;
        private double sessionLow;
        private DateTime currentSessionDate;
        private bool sessionInitialized;

        private double trendHighWater;
        private double trendLowWater;
        private double pullbackExtreme;
        private bool pullbackArmed;

        private bool pendingEntry;
        private int lastEntryBar;
        private DateTime lastEntryTime;
        private DateTime lastExitTime;

        private long tickCounter;
        private long trendLongSignals;
        private long trendShortSignals;
        private long fadeLongSignals;
        private long fadeShortSignals;
        private long rthBlocked;
        private long cooldownBlocked;
        private long positionBlocked;

        // =========================================================================================
        // NINJASCRIPT LIFECYCLE
        // =========================================================================================

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_3_PersistenceEngine";
                Description = "MNQ LIGHT HYBRID complete persistence-engine strategy. One MNQ only. No emergency lockout. Internal synthetic auction bars.";

                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayName = Name;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;

                TimeInForce = TimeInForce.Day;
                StartBehavior = StartBehavior.WaitUntilFlat;
                BarsRequiredToTrade = 20;

                TraceOrders = false;

                UseRthFilter = true;
                RthStartTime = 93000;
                RthEndTime = 160000;

                SyntheticBarSeconds = 60;
                SyntheticBarsRequired = 2;

                EnergyBuildThreshold = 14.0;
                EnergyConfirmThreshold = 28.0;
                EnergyExitThreshold = 6.0;
                EnergyDecay = 0.985;
                ExhaustionThreshold = 18.0;

                TrendTargetTicks = 24;
                TrendStopTicks = 16;
                FadeTargetTicks = 10;
                FadeStopTicks = 12;

                PullbackMinTicks = 4;
                PullbackMaxTicks = 24;
                ReclaimTicks = 2;

                FadeExtremeTicks = 8;

                EntryCooldownSeconds = 5;
                PostExitCooldownSeconds = 3;

                DiagnosticEveryTicks = 1000;
                PrintModeChanges = true;
                PrintEntries = true;
            }
            else if (State == State.Configure)
            {
                // Protective bracket templates.
                // These are also reset before individual entries for clarity.
                SetStopLoss("B93_Trend_Long", CalculationMode.Ticks, TrendStopTicks, false);
                SetProfitTarget("B93_Trend_Long", CalculationMode.Ticks, TrendTargetTicks);

                SetStopLoss("B93_Trend_Short", CalculationMode.Ticks, TrendStopTicks, false);
                SetProfitTarget("B93_Trend_Short", CalculationMode.Ticks, TrendTargetTicks);

                SetStopLoss("B93_Fade_Long", CalculationMode.Ticks, FadeStopTicks, false);
                SetProfitTarget("B93_Fade_Long", CalculationMode.Ticks, FadeTargetTicks);

                SetStopLoss("B93_Fade_Short", CalculationMode.Ticks, FadeStopTicks, false);
                SetProfitTarget("B93_Fade_Short", CalculationMode.Ticks, FadeTargetTicks);
            }
            else if (State == State.DataLoaded)
            {
                ResetAllRuntimeState();
                Print(Name + " loaded. COMPLETE file. Hybrid persistence trend/fade. NO emergency lockout. MNQ 1-tick chart preferred. One MNQ only.");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            tickCounter++;

            DateTime now = Time[0];
            double px = Close[0];

            ResetSessionIfNeeded(now, px);
            UpdateSyntheticAuctionBar(now, px);
            UpdateAuctionEnergy(px);
            UpdateAuctionState();
            UpdateWatermarks(px);

            if (!IsWithinTradingWindow(now))
            {
                rthBlocked++;
                PrintDiagnosticsIfNeeded(now, px);
                return;
            }

            if (!CanAttemptEntry(now))
            {
                PrintDiagnosticsIfNeeded(now, px);
                return;
            }

            // Entry hierarchy:
            // 1. Persistent trend continuation.
            // 2. Neutral fade only if not in persistent trend.
            bool submitted = false;

            if (auctionState == AuctionState.ConfirmedUp)
                submitted = TryTrendLong(now, px);

            if (!submitted && auctionState == AuctionState.ConfirmedDown)
                submitted = TryTrendShort(now, px);

            if (!submitted && auctionState == AuctionState.None)
            {
                if (!TryFadeLong(now, px))
                    TryFadeShort(now, px);
            }

            PrintDiagnosticsIfNeeded(now, px);
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

            if (execution.Order.OrderState != OrderState.Filled)
                return;

            string orderName = execution.Order.Name ?? string.Empty;

            if (orderName == "B93_Trend_Long" ||
                orderName == "B93_Trend_Short" ||
                orderName == "B93_Fade_Long" ||
                orderName == "B93_Fade_Short")
            {
                pendingEntry = false;
                lastEntryTime = time;
                lastEntryBar = CurrentBar;
            }

            // Any flattening execution clears pending state.
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                pendingEntry = false;
                lastExitTime = time;
                pullbackArmed = false;
            }
        }

        // =========================================================================================
        // SESSION / TIME
        // =========================================================================================

        private void ResetAllRuntimeState()
        {
            auctionState = AuctionState.None;
            previousPrintedState = AuctionState.None;

            auctionEnergy = 0.0;
            exhaustionScore = 0.0;

            syntheticInitialized = false;
            syntheticOpen = 0.0;
            syntheticHigh = 0.0;
            syntheticLow = 0.0;
            syntheticClose = 0.0;
            syntheticStartTime = Core.Globals.MinDate;

            priorSyntheticClose1 = 0.0;
            priorSyntheticClose2 = 0.0;
            priorSyntheticOpen1 = 0.0;
            priorSyntheticOpen2 = 0.0;
            priorSyntheticHigh1 = 0.0;
            priorSyntheticHigh2 = 0.0;
            priorSyntheticLow1 = 0.0;
            priorSyntheticLow2 = 0.0;
            completedSyntheticBars = 0;

            sessionHigh = double.MinValue;
            sessionLow = double.MaxValue;
            currentSessionDate = Core.Globals.MinDate;
            sessionInitialized = false;

            trendHighWater = double.MinValue;
            trendLowWater = double.MaxValue;
            pullbackExtreme = 0.0;
            pullbackArmed = false;

            pendingEntry = false;
            lastEntryBar = -999999;
            lastEntryTime = Core.Globals.MinDate;
            lastExitTime = Core.Globals.MinDate;

            tickCounter = 0;
            trendLongSignals = 0;
            trendShortSignals = 0;
            fadeLongSignals = 0;
            fadeShortSignals = 0;
            rthBlocked = 0;
            cooldownBlocked = 0;
            positionBlocked = 0;
        }

        private void ResetSessionIfNeeded(DateTime now, double px)
        {
            if (!sessionInitialized || now.Date != currentSessionDate)
            {
                currentSessionDate = now.Date;
                sessionInitialized = true;

                sessionHigh = px;
                sessionLow = px;

                auctionEnergy = 0.0;
                exhaustionScore = 0.0;
                auctionState = AuctionState.None;
                previousPrintedState = AuctionState.None;

                trendHighWater = px;
                trendLowWater = px;
                pullbackExtreme = px;
                pullbackArmed = false;
            }

            if (px > sessionHigh)
                sessionHigh = px;

            if (px < sessionLow)
                sessionLow = px;
        }

        private bool IsWithinTradingWindow(DateTime now)
        {
            if (!UseRthFilter)
                return true;

            int t = ToTime(now);
            return t >= RthStartTime && t <= RthEndTime;
        }

        // =========================================================================================
        // SYNTHETIC AUCTION BARS
        // =========================================================================================

        private void UpdateSyntheticAuctionBar(DateTime now, double px)
        {
            if (!syntheticInitialized)
            {
                syntheticInitialized = true;
                syntheticStartTime = now;

                syntheticOpen = px;
                syntheticHigh = px;
                syntheticLow = px;
                syntheticClose = px;
                return;
            }

            if ((now - syntheticStartTime).TotalSeconds >= SyntheticBarSeconds)
            {
                CompleteSyntheticBar();

                syntheticStartTime = now;
                syntheticOpen = px;
                syntheticHigh = px;
                syntheticLow = px;
                syntheticClose = px;
                return;
            }

            if (px > syntheticHigh)
                syntheticHigh = px;

            if (px < syntheticLow)
                syntheticLow = px;

            syntheticClose = px;
        }

        private void CompleteSyntheticBar()
        {
            priorSyntheticOpen2 = priorSyntheticOpen1;
            priorSyntheticHigh2 = priorSyntheticHigh1;
            priorSyntheticLow2 = priorSyntheticLow1;
            priorSyntheticClose2 = priorSyntheticClose1;

            priorSyntheticOpen1 = syntheticOpen;
            priorSyntheticHigh1 = syntheticHigh;
            priorSyntheticLow1 = syntheticLow;
            priorSyntheticClose1 = syntheticClose;

            completedSyntheticBars++;
        }

        // =========================================================================================
        // AUCTION ENERGY / STATE MACHINE
        // =========================================================================================

        private void UpdateAuctionEnergy(double px)
        {
            if (completedSyntheticBars < SyntheticBarsRequired)
                return;

            double currentBarMove = syntheticClose - syntheticOpen;
            double priorMove1 = priorSyntheticClose1 - priorSyntheticOpen1;
            double priorMove2 = priorSyntheticClose2 - priorSyntheticOpen2;

            double slope1 = syntheticClose - priorSyntheticClose1;
            double slope2 = priorSyntheticClose1 - priorSyntheticClose2;

            double range = Math.Max(TickSize, syntheticHigh - syntheticLow);
            double efficiency = Math.Abs(currentBarMove) / range;

            double directionalEvidence = 0.0;

            if (currentBarMove > 0)
                directionalEvidence += 2.0 + efficiency;

            if (priorMove1 > 0)
                directionalEvidence += 1.5;

            if (priorMove2 > 0)
                directionalEvidence += 1.0;

            if (slope1 > 0)
                directionalEvidence += 2.0;

            if (slope2 > 0)
                directionalEvidence += 1.0;

            if (currentBarMove < 0)
                directionalEvidence -= 2.0 + efficiency;

            if (priorMove1 < 0)
                directionalEvidence -= 1.5;

            if (priorMove2 < 0)
                directionalEvidence -= 1.0;

            if (slope1 < 0)
                directionalEvidence -= 2.0;

            if (slope2 < 0)
                directionalEvidence -= 1.0;

            auctionEnergy = (auctionEnergy * EnergyDecay) + directionalEvidence;

            // Exhaustion rises when the current synthetic bar becomes large and inefficient
            // or pushes far beyond the previous synthetic range.
            double expansion = Math.Abs(syntheticClose - priorSyntheticClose1) / TickSize;
            if (expansion >= 20 && efficiency < 0.45)
                exhaustionScore += 1.25;
            else if (expansion >= 32)
                exhaustionScore += 0.75;
            else
                exhaustionScore *= 0.965;

            if (exhaustionScore < 0.0)
                exhaustionScore = 0.0;
        }

        private void UpdateAuctionState()
        {
            AuctionState oldState = auctionState;

            switch (auctionState)
            {
                case AuctionState.None:
                    if (auctionEnergy >= EnergyBuildThreshold)
                        auctionState = AuctionState.BuildingUp;
                    else if (auctionEnergy <= -EnergyBuildThreshold)
                        auctionState = AuctionState.BuildingDown;
                    break;

                case AuctionState.BuildingUp:
                    if (auctionEnergy >= EnergyConfirmThreshold)
                        auctionState = AuctionState.ConfirmedUp;
                    else if (auctionEnergy <= EnergyExitThreshold)
                        auctionState = AuctionState.None;
                    break;

                case AuctionState.ConfirmedUp:
                    if (exhaustionScore >= ExhaustionThreshold)
                        auctionState = AuctionState.ExhaustingUp;
                    else if (auctionEnergy <= -EnergyExitThreshold)
                        auctionState = AuctionState.None;
                    break;

                case AuctionState.ExhaustingUp:
                    if (auctionEnergy >= EnergyBuildThreshold && exhaustionScore < ExhaustionThreshold * 0.50)
                        auctionState = AuctionState.ConfirmedUp;
                    else if (auctionEnergy <= -EnergyBuildThreshold)
                        auctionState = AuctionState.BuildingDown;
                    else if (Math.Abs(auctionEnergy) <= EnergyExitThreshold)
                        auctionState = AuctionState.None;
                    break;

                case AuctionState.BuildingDown:
                    if (auctionEnergy <= -EnergyConfirmThreshold)
                        auctionState = AuctionState.ConfirmedDown;
                    else if (auctionEnergy >= -EnergyExitThreshold)
                        auctionState = AuctionState.None;
                    break;

                case AuctionState.ConfirmedDown:
                    if (exhaustionScore >= ExhaustionThreshold)
                        auctionState = AuctionState.ExhaustingDown;
                    else if (auctionEnergy >= EnergyExitThreshold)
                        auctionState = AuctionState.None;
                    break;

                case AuctionState.ExhaustingDown:
                    if (auctionEnergy <= -EnergyBuildThreshold && exhaustionScore < ExhaustionThreshold * 0.50)
                        auctionState = AuctionState.ConfirmedDown;
                    else if (auctionEnergy >= EnergyBuildThreshold)
                        auctionState = AuctionState.BuildingUp;
                    else if (Math.Abs(auctionEnergy) <= EnergyExitThreshold)
                        auctionState = AuctionState.None;
                    break;

                case AuctionState.Chop:
                    if (auctionEnergy >= EnergyBuildThreshold)
                        auctionState = AuctionState.BuildingUp;
                    else if (auctionEnergy <= -EnergyBuildThreshold)
                        auctionState = AuctionState.BuildingDown;
                    else if (Math.Abs(auctionEnergy) <= EnergyExitThreshold)
                        auctionState = AuctionState.None;
                    break;
            }

            if (auctionState != oldState)
            {
                pullbackArmed = false;
                pullbackExtreme = Close[0];

                if (PrintModeChanges)
                {
                    Print(string.Format(
                        "{0:yyyy-MM-dd HH:mm:ss.fff} AUCTION MODE {1} energy={2:F2} exhaustion={3:F2}",
                        Time[0],
                        auctionState,
                        auctionEnergy,
                        exhaustionScore));
                }
            }
        }

        private void UpdateWatermarks(double px)
        {
            if (auctionState == AuctionState.BuildingUp || auctionState == AuctionState.ConfirmedUp || auctionState == AuctionState.ExhaustingUp)
            {
                if (px > trendHighWater || trendHighWater == double.MinValue)
                    trendHighWater = px;
            }
            else
            {
                trendHighWater = px;
            }

            if (auctionState == AuctionState.BuildingDown || auctionState == AuctionState.ConfirmedDown || auctionState == AuctionState.ExhaustingDown)
            {
                if (px < trendLowWater || trendLowWater == double.MaxValue)
                    trendLowWater = px;
            }
            else
            {
                trendLowWater = px;
            }
        }

        // =========================================================================================
        // ENTRY GOVERNANCE
        // =========================================================================================

        private bool CanAttemptEntry(DateTime now)
        {
            if (pendingEntry)
            {
                positionBlocked++;
                return false;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                positionBlocked++;
                return false;
            }

            if (CurrentBar == lastEntryBar)
            {
                cooldownBlocked++;
                return false;
            }

            if (lastEntryTime != Core.Globals.MinDate && (now - lastEntryTime).TotalSeconds < EntryCooldownSeconds)
            {
                cooldownBlocked++;
                return false;
            }

            if (lastExitTime != Core.Globals.MinDate && (now - lastExitTime).TotalSeconds < PostExitCooldownSeconds)
            {
                cooldownBlocked++;
                return false;
            }

            return true;
        }

        // =========================================================================================
        // TREND CONTINUATION ENTRIES
        // =========================================================================================

        private bool TryTrendLong(DateTime now, double px)
        {
            double pullbackTicks = (trendHighWater - px) / TickSize;

            if (!pullbackArmed)
            {
                if (pullbackTicks >= PullbackMinTicks && pullbackTicks <= PullbackMaxTicks)
                {
                    pullbackArmed = true;
                    pullbackExtreme = px;
                }

                return false;
            }

            if (px < pullbackExtreme)
                pullbackExtreme = px;

            double reclaimTicks = (px - pullbackExtreme) / TickSize;

            if (reclaimTicks >= ReclaimTicks && pullbackTicks <= PullbackMaxTicks)
            {
                SubmitTrendLong(now, px);
                return true;
            }

            if (pullbackTicks > PullbackMaxTicks)
            {
                pullbackArmed = false;
                return false;
            }

            return false;
        }

        private bool TryTrendShort(DateTime now, double px)
        {
            double pullbackTicks = (px - trendLowWater) / TickSize;

            if (!pullbackArmed)
            {
                if (pullbackTicks >= PullbackMinTicks && pullbackTicks <= PullbackMaxTicks)
                {
                    pullbackArmed = true;
                    pullbackExtreme = px;
                }

                return false;
            }

            if (px > pullbackExtreme)
                pullbackExtreme = px;

            double reclaimTicks = (pullbackExtreme - px) / TickSize;

            if (reclaimTicks >= ReclaimTicks && pullbackTicks <= PullbackMaxTicks)
            {
                SubmitTrendShort(now, px);
                return true;
            }

            if (pullbackTicks > PullbackMaxTicks)
            {
                pullbackArmed = false;
                return false;
            }

            return false;
        }

        private void SubmitTrendLong(DateTime now, double px)
        {
            SetStopLoss("B93_Trend_Long", CalculationMode.Ticks, TrendStopTicks, false);
            SetProfitTarget("B93_Trend_Long", CalculationMode.Ticks, TrendTargetTicks);

            pendingEntry = true;
            lastEntryBar = CurrentBar;
            lastEntryTime = now;
            pullbackArmed = false;
            trendLongSignals++;

            if (PrintEntries)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER LONG Trend px={1:F2} energy={2:F2} state={3}", now, px, auctionEnergy, auctionState));

            EnterLong(1, "B93_Trend_Long");
        }

        private void SubmitTrendShort(DateTime now, double px)
        {
            SetStopLoss("B93_Trend_Short", CalculationMode.Ticks, TrendStopTicks, false);
            SetProfitTarget("B93_Trend_Short", CalculationMode.Ticks, TrendTargetTicks);

            pendingEntry = true;
            lastEntryBar = CurrentBar;
            lastEntryTime = now;
            pullbackArmed = false;
            trendShortSignals++;

            if (PrintEntries)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER SHORT Trend px={1:F2} energy={2:F2} state={3}", now, px, auctionEnergy, auctionState));

            EnterShort(1, "B93_Trend_Short");
        }

        // =========================================================================================
        // NEUTRAL FADE ENTRIES
        // =========================================================================================

        private bool TryFadeLong(DateTime now, double px)
        {
            double distanceFromLowTicks = (px - sessionLow) / TickSize;

            if (distanceFromLowTicks <= FadeExtremeTicks)
            {
                SetStopLoss("B93_Fade_Long", CalculationMode.Ticks, FadeStopTicks, false);
                SetProfitTarget("B93_Fade_Long", CalculationMode.Ticks, FadeTargetTicks);

                pendingEntry = true;
                lastEntryBar = CurrentBar;
                lastEntryTime = now;
                fadeLongSignals++;

                if (PrintEntries)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER LONG Fade px={1:F2} distLowTicks={2:F1}", now, px, distanceFromLowTicks));

                EnterLong(1, "B93_Fade_Long");
                return true;
            }

            return false;
        }

        private bool TryFadeShort(DateTime now, double px)
        {
            double distanceFromHighTicks = (sessionHigh - px) / TickSize;

            if (distanceFromHighTicks <= FadeExtremeTicks)
            {
                SetStopLoss("B93_Fade_Short", CalculationMode.Ticks, FadeStopTicks, false);
                SetProfitTarget("B93_Fade_Short", CalculationMode.Ticks, FadeTargetTicks);

                pendingEntry = true;
                lastEntryBar = CurrentBar;
                lastEntryTime = now;
                fadeShortSignals++;

                if (PrintEntries)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER SHORT Fade px={1:F2} distHighTicks={2:F1}", now, px, distanceFromHighTicks));

                EnterShort(1, "B93_Fade_Short");
                return true;
            }

            return false;
        }

        // =========================================================================================
        // DIAGNOSTICS
        // =========================================================================================

        private void PrintDiagnosticsIfNeeded(DateTime now, double px)
        {
            if (DiagnosticEveryTicks <= 0)
                return;

            if (tickCounter % DiagnosticEveryTicks != 0)
                return;

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm:ss.fff} DIAG ticks={1} px={2:F2} state={3} energy={4:F2} exhaust={5:F2} pos={6} TL={7} TS={8} FL={9} FS={10} rthBlk={11} coolBlk={12} posBlk={13}",
                now,
                tickCounter,
                px,
                auctionState,
                auctionEnergy,
                exhaustionScore,
                Position.MarketPosition,
                trendLongSignals,
                trendShortSignals,
                fadeLongSignals,
                fadeShortSignals,
                rthBlocked,
                cooldownBlocked,
                positionBlocked));
        }
    }
}
