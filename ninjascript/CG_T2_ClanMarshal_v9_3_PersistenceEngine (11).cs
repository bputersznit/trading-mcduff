#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

// =================================================================================================
// CG_T2_ClanMarshal_v9_3_PersistenceEngine.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-07 05:06:00 EDT
//
// MNQ ClanMarshal v9.3C PersistenceEngine — VALIDATION BUILD
//
// PURPOSE
// -------
// This build is intentionally designed to make the engine trade in replay so that execution lifecycle,
// timing, trade density, bracket placement, and anti-giveback behavior can be evaluated. It is not the
// final alpha-optimized build. The previous diagnostic stream showed the synthetic auction engine was
// alive, but Percentile++ gating was suppressing entries through pctBlk. This version softens that gate.
//
// CORE DEPLOYMENT RULES
// ---------------------
// 1. Apply to a 1-tick MNQ chart.
// 2. Quantity is hard-capped at 1 contract.
// 3. No overlapping positions.
// 4. Synthetic auction bars are built internally from incoming ticks.
// 5. Entry logic is evaluated only when a synthetic bar closes.
// 6. Protective stop/target brackets are submitted only after the actual entry fill price is known.
// 7. No global emergency lockout. Same-direction stopout embargo remains available.
//
// VALIDATION CHANGES FROM PRIOR BUILD
// -----------------------------------
// 1. SyntheticSeconds default changed from 60 to 15.
// 2. PercentileGateMode added: Off / P50 / P80.
// 3. Default PercentileGateMode = Off for execution proof. Use P50 after entries are confirmed.
// 4. Explicit diagnostic block reasons added:
//      BLOCK_NOT_RTH, BLOCK_WARMUP, BLOCK_FLAT_REQUIRED, BLOCK_COOLDOWN,
//      BLOCK_EMBARGO_LONG, BLOCK_EMBARGO_SHORT, BLOCK_STATE, BLOCK_ENERGY,
//      BLOCK_EXPANSION, BLOCK_PCT, BLOCK_EXHAUSTION, ENTER_LONG, ENTER_SHORT.
// 5. More permissive validation entries:
//      - Momentum continuation from ConfirmedUp/ConfirmedDown.
//      - Early continuation from BuildingUp/BuildingDown if energy/expansion are acceptable.
//      - Pullback reclaim on a directional synthetic close after a prior counter bar.
//
// STRATEGY METHODOLOGY COMMENTS
// -----------------------------
// The engine models a synthetic auction state from tick-fed bars. Every synthetic bar measures range,
// direction, close-to-close movement, and simple energy persistence. Percentiles are rolling over recent
// synthetic bar ranges, not over tick noise. The v9.3 pathology was fixed by updating state only at real
// synthetic closes. The v9.3B objective is to prevent adaptive percentile thresholds from normalizing the
// engine out of all valid trades.
//
// IMPORTANT
// ---------
// This file intentionally uses managed NinjaScript orders, but submits stop/target exits only from
// OnExecutionUpdate after an entry fill. That avoids pre-fill invalid stop placement such as buy stops
// below market or sell stops above market. The exit orders are tied to the entry signal name.
// =================================================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum CGAuctionState
    {
        None,
        BuildingUp,
        ConfirmedUp,
        ExhaustingUp,
        BuildingDown,
        ConfirmedDown,
        ExhaustingDown
    }

    public enum CGPercentileGateMode
    {
        Off,
        P50,
        P80
    }

    public class CG_T2_ClanMarshal_v9_3_PersistenceEngine : Strategy
    {
        // -----------------------------
        // Synthetic bar state
        // -----------------------------
        private bool syntheticActive;
        private DateTime syntheticStartTime;
        private DateTime syntheticEndTime;
        private double syntheticOpen;
        private double syntheticHigh;
        private double syntheticLow;
        private double syntheticClose;
        private double previousSyntheticClose;
        private double previousSyntheticOpen;
        private bool hasPreviousSynthetic;
        private int syntheticTickCount;
        private long syntheticBarId;

        // -----------------------------
        // Rolling percentile state
        // -----------------------------
        private readonly Queue<double> rangeWindow = new Queue<double>();
        private readonly List<double> rangeSortBuffer = new List<double>();
        private double p50Range;
        private double p80Range;
        private double lastExpansionScore;
        private double lastSyntheticRange;

        // -----------------------------
        // Auction state
        // -----------------------------
        private CGAuctionState auctionState = CGAuctionState.None;
        private CGAuctionState priorAuctionState = CGAuctionState.None;
        private double auctionEnergy;
        private int stateTransitionCount;
        private int confirmedBarsInState;

        // -----------------------------
        // Trading governance
        // -----------------------------
        private DateTime lastEntryTime = Core.Globals.MinDate;
        private int lastEntrySyntheticId = -1;
        private int longStopoutStreak;
        private int shortStopoutStreak;
        private DateTime longEmbargoUntil = Core.Globals.MinDate;
        private DateTime shortEmbargoUntil = Core.Globals.MinDate;
        private string activeEntrySignal = string.Empty;
        private string activeEntrySide = string.Empty;
        private double activeEntryPrice;
        private bool protectiveOrdersSubmitted;
        private Order activeEntryOrder;
        private Order activeStopOrder;
        private Order activeTargetOrder;

        // -----------------------------
        // Diagnostics counters
        // -----------------------------
        private long longSignals;
        private long shortSignals;
        private long pctBlocked;
        private long expansionBlocked;
        private long energyBlocked;
        private long stateBlocked;
        private long cooldownBlocked;
        private long flatBlocked;
        private long embargoBlocked;
        private long rthBlocked;
        private long warmupBlocked;
        private long exhaustionBlocked;
        private long entriesSubmitted;
        private long fillsObserved;

        // =========================================================================================
        // NinjaScript lifecycle
        // =========================================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_3_PersistenceEngine";
                Description = "MNQ ClanMarshal v9.3B tick-fed synthetic auction validation engine. Apply to 1-tick MNQ chart.";

                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                StartBehavior = StartBehavior.WaitUntilFlat;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                TimeInForce = TimeInForce.Day;
                TraceOrders = false;

                // Validation defaults. These are intentionally permissive to prove execution lifecycle.
                SyntheticSeconds = 15;
                PercentileGateMode = CGPercentileGateMode.Off;
                RangeLookbackBars = 120;
                WarmupSyntheticBars = 20;
                MinExpansionScore = 0.10;
                MinConfirmedEnergy = 18.0;
                MinBuildingEnergy = 22.0;
                ExhaustionEnergy = 75.0;
                AllowBuildingStateEntries = true;
                AllowPullbackReclaimEntries = true;

                StartTimeEt = 93000;
                EndTimeEt = 155900;
                UseRthOnly = true;

                TargetTicks = 24;
                StopTicks = 12;
                MaxHoldSeconds = 180;
                CooldownSeconds = 20;
                SameDirectionStopoutsForEmbargo = 2;
                EmbargoMinutes = 10;

                PrintDiagnostics = true;
                PrintEverySyntheticBar = true;
                PrintOnlyWhenAction = false;
            }
            else if (State == State.Configure)
            {
                // No secondary series. The strategy is fed by the primary 1-tick chart and creates its
                // own synthetic bars internally. This avoids BarsInProgress complexity in playback.
            }
            else if (State == State.DataLoaded)
            {
                ResetRuntimeState();
            }
            else if (State == State.Terminated)
            {
                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} TERMINATED entries={1} fills={2} pctBlk={3} expBlk={4} energyBlk={5} stateBlk={6} coolBlk={7}",
                        NowSafe(), entriesSubmitted, fillsObserved, pctBlocked, expansionBlocked, energyBlocked, stateBlocked, cooldownBlocked));
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < 2)
                return;

            double price = Close[0];
            DateTime tickTime = Time[0];

            UpdateSyntheticBar(tickTime, price);
            EnforceTimeStop(tickTime);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null)
                return;

            if (activeEntryOrder != null && order.OrderId == activeEntryOrder.OrderId)
                activeEntryOrder = order;
            if (activeStopOrder != null && order.OrderId == activeStopOrder.OrderId)
                activeStopOrder = order;
            if (activeTargetOrder != null && order.OrderId == activeTargetOrder.OrderId)
                activeTargetOrder = order;

            if (error != ErrorCode.NoError && PrintDiagnostics)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ORDER_ERROR name={1} state={2} error={3} native={4}",
                    time, order.Name, orderState, error, nativeError));
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            Order order = execution.Order;

            if (order.OrderState != OrderState.Filled && order.OrderState != OrderState.PartFilled)
                return;

            // Entry fill: now and only now submit broker-side protective exits from actual fill price.
            if (order.Name != null && (order.Name.StartsWith("CG_LONG_") || order.Name.StartsWith("CG_SHORT_")))
            {
                fillsObserved++;
                activeEntrySignal = order.Name;
                activeEntrySide = order.Name.StartsWith("CG_LONG_") ? "LONG" : "SHORT";
                activeEntryPrice = price;
                protectiveOrdersSubmitted = false;
                SubmitProtectiveBracket(price, activeEntrySide, activeEntrySignal, time);

                if (PrintDiagnostics)
                {
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} FILL_ENTRY side={1} signal={2} fill={3:F2} qty={4} syn={5}",
                        time, activeEntrySide, activeEntrySignal, price, quantity, syntheticBarId));
                }
                return;
            }

            // Exit fill: update same-direction stopout embargo logic.
            if (order.Name != null && order.Name.StartsWith("CG_STOP_"))
            {
                if (activeEntrySide == "LONG")
                {
                    longStopoutStreak++;
                    shortStopoutStreak = 0;
                    if (longStopoutStreak >= SameDirectionStopoutsForEmbargo)
                        longEmbargoUntil = time.AddMinutes(EmbargoMinutes);
                }
                else if (activeEntrySide == "SHORT")
                {
                    shortStopoutStreak++;
                    longStopoutStreak = 0;
                    if (shortStopoutStreak >= SameDirectionStopoutsForEmbargo)
                        shortEmbargoUntil = time.AddMinutes(EmbargoMinutes);
                }

                if (PrintDiagnostics)
                {
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EXIT_STOP side={1} price={2:F2} longStopStreak={3} shortStopStreak={4} longEmbargoUntil={5:HH:mm:ss} shortEmbargoUntil={6:HH:mm:ss}",
                        time, activeEntrySide, price, longStopoutStreak, shortStopoutStreak, longEmbargoUntil, shortEmbargoUntil));
                }
                ClearActiveTradeTrackingIfFlat();
                return;
            }

            if (order.Name != null && order.Name.StartsWith("CG_TARGET_"))
            {
                if (activeEntrySide == "LONG")
                    longStopoutStreak = 0;
                else if (activeEntrySide == "SHORT")
                    shortStopoutStreak = 0;

                if (PrintDiagnostics)
                {
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EXIT_TARGET side={1} price={2:F2}",
                        time, activeEntrySide, price));
                }
                ClearActiveTradeTrackingIfFlat();
            }
        }

        // =========================================================================================
        // Synthetic bar engine
        // =========================================================================================
        private void UpdateSyntheticBar(DateTime tickTime, double price)
        {
            if (!syntheticActive)
            {
                StartSyntheticBar(tickTime, price);
                return;
            }

            syntheticHigh = Math.Max(syntheticHigh, price);
            syntheticLow = Math.Min(syntheticLow, price);
            syntheticClose = price;
            syntheticTickCount++;

            if ((tickTime - syntheticStartTime).TotalSeconds >= SyntheticSeconds)
            {
                CloseSyntheticBar(tickTime);
                StartSyntheticBar(tickTime, price);
            }
        }

        private void StartSyntheticBar(DateTime tickTime, double price)
        {
            syntheticActive = true;
            syntheticStartTime = tickTime;
            syntheticEndTime = tickTime;
            syntheticOpen = price;
            syntheticHigh = price;
            syntheticLow = price;
            syntheticClose = price;
            syntheticTickCount = 1;
        }

        private void CloseSyntheticBar(DateTime closeTime)
        {
            syntheticEndTime = closeTime;
            syntheticBarId++;

            previousSyntheticOpen = hasPreviousSynthetic ? previousSyntheticOpen : syntheticOpen;
            lastSyntheticRange = Math.Max(TickSize, syntheticHigh - syntheticLow);

            UpdateRangePercentiles(lastSyntheticRange);
            UpdateAuctionState();
            EvaluateEntryAtSyntheticClose(closeTime);

            previousSyntheticClose = syntheticClose;
            previousSyntheticOpen = syntheticOpen;
            hasPreviousSynthetic = true;
        }

        private void UpdateRangePercentiles(double range)
        {
            rangeWindow.Enqueue(range);
            while (rangeWindow.Count > RangeLookbackBars)
                rangeWindow.Dequeue();

            rangeSortBuffer.Clear();
            foreach (double value in rangeWindow)
                rangeSortBuffer.Add(value);
            rangeSortBuffer.Sort();

            p50Range = PercentileFromSorted(rangeSortBuffer, 0.50);
            p80Range = PercentileFromSorted(rangeSortBuffer, 0.80);

            double denom = Math.Max(TickSize, p80Range - p50Range);
            lastExpansionScore = Math.Max(0.0, (range - p50Range) / denom);
        }

        private double PercentileFromSorted(List<double> sorted, double p)
        {
            if (sorted == null || sorted.Count == 0)
                return TickSize;

            if (sorted.Count == 1)
                return sorted[0];

            double rawIndex = (sorted.Count - 1) * p;
            int lower = (int)Math.Floor(rawIndex);
            int upper = (int)Math.Ceiling(rawIndex);
            if (lower == upper)
                return sorted[lower];

            double weight = rawIndex - lower;
            return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
        }

        private void UpdateAuctionState()
        {
            double body = syntheticClose - syntheticOpen;
            double closeToClose = hasPreviousSynthetic ? syntheticClose - previousSyntheticClose : body;
            double signedImpulse = (body * 0.60) + (closeToClose * 0.40);
            double impulseTicks = signedImpulse / TickSize;

            // Energy is intentionally persistent but decays enough to allow reversals.
            auctionEnergy = (auctionEnergy * 0.68) + impulseTicks;

            priorAuctionState = auctionState;

            if (auctionEnergy >= ExhaustionEnergy)
                auctionState = CGAuctionState.ExhaustingUp;
            else if (auctionEnergy <= -ExhaustionEnergy)
                auctionState = CGAuctionState.ExhaustingDown;
            else if (auctionEnergy >= MinConfirmedEnergy)
                auctionState = CGAuctionState.ConfirmedUp;
            else if (auctionEnergy >= Math.Max(5.0, MinBuildingEnergy * 0.55))
                auctionState = CGAuctionState.BuildingUp;
            else if (auctionEnergy <= -MinConfirmedEnergy)
                auctionState = CGAuctionState.ConfirmedDown;
            else if (auctionEnergy <= -Math.Max(5.0, MinBuildingEnergy * 0.55))
                auctionState = CGAuctionState.BuildingDown;
            else
                auctionState = CGAuctionState.None;

            if (auctionState != priorAuctionState)
            {
                stateTransitionCount++;
                confirmedBarsInState = 1;
                if (PrintDiagnostics)
                {
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} AUCTION {1} syn={2} energy={3:F2} exp={4:F2} range={5:F1} p80={6:F1}",
                        syntheticEndTime, auctionState, syntheticBarId, auctionEnergy, lastExpansionScore, lastSyntheticRange, p80Range));
                }
            }
            else
            {
                confirmedBarsInState++;
            }
        }

        // =========================================================================================
        // Entry logic
        // =========================================================================================
        private void EvaluateEntryAtSyntheticClose(DateTime decisionTime)
        {
            string diagReason = "DIAG";

            if (PrintEverySyntheticBar && (PrintDiagnostics || PrintOnlyWhenAction == false))
                PrintDiagnostic(decisionTime, diagReason);

            if (UseRthOnly && !IsWithinRth(decisionTime))
            {
                rthBlocked++;
                PrintBlock(decisionTime, "BLOCK_NOT_RTH");
                return;
            }

            if (syntheticBarId < WarmupSyntheticBars || rangeWindow.Count < Math.Min(WarmupSyntheticBars, RangeLookbackBars))
            {
                warmupBlocked++;
                PrintBlock(decisionTime, "BLOCK_WARMUP");
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                flatBlocked++;
                PrintBlock(decisionTime, "BLOCK_FLAT_REQUIRED");
                return;
            }

            if ((decisionTime - lastEntryTime).TotalSeconds < CooldownSeconds)
            {
                cooldownBlocked++;
                PrintBlock(decisionTime, "BLOCK_COOLDOWN");
                return;
            }

            bool longCandidate = IsLongCandidate();
            bool shortCandidate = IsShortCandidate();

            if (!longCandidate && !shortCandidate)
            {
                stateBlocked++;
                PrintBlock(decisionTime, "BLOCK_STATE");
                return;
            }

            if (longCandidate && decisionTime < longEmbargoUntil)
            {
                embargoBlocked++;
                PrintBlock(decisionTime, "BLOCK_EMBARGO_LONG");
                longCandidate = false;
            }

            if (shortCandidate && decisionTime < shortEmbargoUntil)
            {
                embargoBlocked++;
                PrintBlock(decisionTime, "BLOCK_EMBARGO_SHORT");
                shortCandidate = false;
            }

            if (!longCandidate && !shortCandidate)
                return;

            if (!PassesEnergyGate(longCandidate, shortCandidate))
            {
                energyBlocked++;
                PrintBlock(decisionTime, "BLOCK_ENERGY");
                return;
            }

            if (lastExpansionScore < MinExpansionScore)
            {
                expansionBlocked++;
                PrintBlock(decisionTime, "BLOCK_EXPANSION");
                return;
            }

            if (!PassesPercentileGate())
            {
                pctBlocked++;
                PrintBlock(decisionTime, "BLOCK_PCT");
                return;
            }

            if (IsExhaustedAgainstEntry(longCandidate, shortCandidate))
            {
                exhaustionBlocked++;
                PrintBlock(decisionTime, "BLOCK_EXHAUSTION");
                return;
            }

            if (longCandidate && shortCandidate)
            {
                // Resolve rare simultaneous signals by energy sign.
                if (auctionEnergy >= 0)
                    shortCandidate = false;
                else
                    longCandidate = false;
            }

            if (longCandidate)
                SubmitLong(decisionTime);
            else if (shortCandidate)
                SubmitShort(decisionTime);
        }

        private bool IsLongCandidate()
        {
            bool confirmed = auctionState == CGAuctionState.ConfirmedUp;
            bool building = AllowBuildingStateEntries && auctionState == CGAuctionState.BuildingUp && auctionEnergy >= MinBuildingEnergy;
            bool reclaim = AllowPullbackReclaimEntries && hasPreviousSynthetic && syntheticClose > syntheticOpen && previousSyntheticClose < previousSyntheticOpen && auctionEnergy > 0;
            return confirmed || building || reclaim;
        }

        private bool IsShortCandidate()
        {
            bool confirmed = auctionState == CGAuctionState.ConfirmedDown;
            bool building = AllowBuildingStateEntries && auctionState == CGAuctionState.BuildingDown && auctionEnergy <= -MinBuildingEnergy;
            bool reclaim = AllowPullbackReclaimEntries && hasPreviousSynthetic && syntheticClose < syntheticOpen && previousSyntheticClose > previousSyntheticOpen && auctionEnergy < 0;
            return confirmed || building || reclaim;
        }

        private bool PassesEnergyGate(bool longCandidate, bool shortCandidate)
        {
            if (longCandidate)
                return auctionEnergy >= MinConfirmedEnergy || (AllowBuildingStateEntries && auctionEnergy >= MinBuildingEnergy);
            if (shortCandidate)
                return auctionEnergy <= -MinConfirmedEnergy || (AllowBuildingStateEntries && auctionEnergy <= -MinBuildingEnergy);
            return false;
        }

        private bool PassesPercentileGate()
        {
            if (PercentileGateMode == CGPercentileGateMode.Off)
                return true;

            if (PercentileGateMode == CGPercentileGateMode.P50)
                return lastSyntheticRange >= Math.Max(TickSize, p50Range);

            if (PercentileGateMode == CGPercentileGateMode.P80)
                return lastSyntheticRange >= Math.Max(TickSize, p80Range);

            return true;
        }

        private bool IsExhaustedAgainstEntry(bool longCandidate, bool shortCandidate)
        {
            // Validation build does not forbid confirmed trends. It only blocks the explicit exhaustion states.
            if (longCandidate && auctionState == CGAuctionState.ExhaustingUp)
                return true;
            if (shortCandidate && auctionState == CGAuctionState.ExhaustingDown)
                return true;
            return false;
        }

        private void SubmitLong(DateTime decisionTime)
        {
            if (lastEntrySyntheticId == (int)syntheticBarId)
                return;

            string signal = "CG_LONG_" + syntheticBarId.ToString();
            longSignals++;
            entriesSubmitted++;
            lastEntryTime = decisionTime;
            lastEntrySyntheticId = (int)syntheticBarId;
            activeEntrySignal = signal;
            activeEntrySide = "LONG";
            protectiveOrdersSubmitted = false;

            PrintAction(decisionTime, "ENTER_LONG", signal);
            EnterLong(1, signal);
        }

        private void SubmitShort(DateTime decisionTime)
        {
            if (lastEntrySyntheticId == (int)syntheticBarId)
                return;

            string signal = "CG_SHORT_" + syntheticBarId.ToString();
            shortSignals++;
            entriesSubmitted++;
            lastEntryTime = decisionTime;
            lastEntrySyntheticId = (int)syntheticBarId;
            activeEntrySignal = signal;
            activeEntrySide = "SHORT";
            protectiveOrdersSubmitted = false;

            PrintAction(decisionTime, "ENTER_SHORT", signal);
            EnterShort(1, signal);
        }

        // =========================================================================================
        // Protective exits and time stop
        // =========================================================================================
        private void SubmitProtectiveBracket(double fillPrice, string side, string fromEntrySignal, DateTime time)
        {
            if (protectiveOrdersSubmitted)
                return;

            double stopPrice;
            double targetPrice;

            if (side == "LONG")
            {
                stopPrice = Instrument.MasterInstrument.RoundToTickSize(fillPrice - (StopTicks * TickSize));
                targetPrice = Instrument.MasterInstrument.RoundToTickSize(fillPrice + (TargetTicks * TickSize));
                activeStopOrder = ExitLongStopMarket(0, true, 1, stopPrice, "CG_STOP_LONG_" + syntheticBarId, fromEntrySignal);
                activeTargetOrder = ExitLongLimit(0, true, 1, targetPrice, "CG_TARGET_LONG_" + syntheticBarId, fromEntrySignal);
            }
            else
            {
                stopPrice = Instrument.MasterInstrument.RoundToTickSize(fillPrice + (StopTicks * TickSize));
                targetPrice = Instrument.MasterInstrument.RoundToTickSize(fillPrice - (TargetTicks * TickSize));
                activeStopOrder = ExitShortStopMarket(0, true, 1, stopPrice, "CG_STOP_SHORT_" + syntheticBarId, fromEntrySignal);
                activeTargetOrder = ExitShortLimit(0, true, 1, targetPrice, "CG_TARGET_SHORT_" + syntheticBarId, fromEntrySignal);
            }

            protectiveOrdersSubmitted = true;

            if (PrintDiagnostics)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} OCO_PLUS side={1} from={2} fill={3:F2} stop={4:F2} target={5:F2} stopTicks={6} targetTicks={7}",
                    time, side, fromEntrySignal, fillPrice, stopPrice, targetPrice, StopTicks, TargetTicks));
            }
        }

        private void EnforceTimeStop(DateTime tickTime)
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            if (lastEntryTime == Core.Globals.MinDate)
                return;

            if ((tickTime - lastEntryTime).TotalSeconds < MaxHoldSeconds)
                return;

            if (Position.MarketPosition == MarketPosition.Long)
            {
                PrintAction(tickTime, "EXIT_TIME_LONG", activeEntrySignal);
                ExitLong("CG_TIME_LONG_" + syntheticBarId, activeEntrySignal);
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                PrintAction(tickTime, "EXIT_TIME_SHORT", activeEntrySignal);
                ExitShort("CG_TIME_SHORT_" + syntheticBarId, activeEntrySignal);
            }
        }

        // =========================================================================================
        // Diagnostics
        // =========================================================================================
        private void PrintDiagnostic(DateTime time, string label)
        {
            if (!PrintDiagnostics)
                return;

            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} {1} syn={2} state={3} energy={4:F2} exp={5:F2} range={6:F1} p50={7:F1} p80={8:F1} pos={9} L={10} S={11} pctBlk={12} expBlk={13} energyBlk={14} stateBlk={15} coolBlk={16} flatBlk={17} rthBlk={18} warmBlk={19} states={20}",
                time,
                label,
                syntheticBarId,
                auctionState,
                auctionEnergy,
                lastExpansionScore,
                lastSyntheticRange,
                p50Range,
                p80Range,
                Position.MarketPosition,
                longSignals,
                shortSignals,
                pctBlocked,
                expansionBlocked,
                energyBlocked,
                stateBlocked,
                cooldownBlocked,
                flatBlocked,
                rthBlocked,
                warmupBlocked,
                stateTransitionCount));
        }

        private void PrintBlock(DateTime time, string reason)
        {
            if (!PrintDiagnostics)
                return;

            if (PrintOnlyWhenAction || !PrintEverySyntheticBar)
                PrintDiagnostic(time, reason);
        }

        private void PrintAction(DateTime time, string action, string signal)
        {
            if (!PrintDiagnostics)
                return;

            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} {1} signal={2} syn={3} state={4} energy={5:F2} exp={6:F2} range={7:F1} p50={8:F1} p80={9:F1} pos={10}",
                time, action, signal, syntheticBarId, auctionState, auctionEnergy, lastExpansionScore, lastSyntheticRange, p50Range, p80Range, Position.MarketPosition));
        }

        // =========================================================================================
        // Helpers
        // =========================================================================================
        private bool IsWithinRth(DateTime time)
        {
            int t = ToTime(time);
            return t >= StartTimeEt && t <= EndTimeEt;
        }

        private DateTime NowSafe()
        {
            try
            {
                if (CurrentBar >= 0)
                    return Time[0];
            }
            catch { }
            return DateTime.Now;
        }

        private void ClearActiveTradeTrackingIfFlat()
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            activeEntrySignal = string.Empty;
            activeEntrySide = string.Empty;
            activeEntryPrice = 0.0;
            protectiveOrdersSubmitted = false;
            activeEntryOrder = null;
            activeStopOrder = null;
            activeTargetOrder = null;
        }

        private void ResetRuntimeState()
        {
            syntheticActive = false;
            syntheticStartTime = Core.Globals.MinDate;
            syntheticEndTime = Core.Globals.MinDate;
            syntheticOpen = 0;
            syntheticHigh = 0;
            syntheticLow = 0;
            syntheticClose = 0;
            previousSyntheticClose = 0;
            previousSyntheticOpen = 0;
            hasPreviousSynthetic = false;
            syntheticTickCount = 0;
            syntheticBarId = 0;

            rangeWindow.Clear();
            rangeSortBuffer.Clear();
            p50Range = TickSize;
            p80Range = TickSize;
            lastExpansionScore = 0;
            lastSyntheticRange = TickSize;

            auctionState = CGAuctionState.None;
            priorAuctionState = CGAuctionState.None;
            auctionEnergy = 0;
            stateTransitionCount = 0;
            confirmedBarsInState = 0;

            lastEntryTime = Core.Globals.MinDate;
            lastEntrySyntheticId = -1;
            longStopoutStreak = 0;
            shortStopoutStreak = 0;
            longEmbargoUntil = Core.Globals.MinDate;
            shortEmbargoUntil = Core.Globals.MinDate;
            activeEntrySignal = string.Empty;
            activeEntrySide = string.Empty;
            activeEntryPrice = 0;
            protectiveOrdersSubmitted = false;
            activeEntryOrder = null;
            activeStopOrder = null;
            activeTargetOrder = null;

            longSignals = 0;
            shortSignals = 0;
            pctBlocked = 0;
            expansionBlocked = 0;
            energyBlocked = 0;
            stateBlocked = 0;
            cooldownBlocked = 0;
            flatBlocked = 0;
            embargoBlocked = 0;
            rthBlocked = 0;
            warmupBlocked = 0;
            exhaustionBlocked = 0;
            entriesSubmitted = 0;
            fillsObserved = 0;
        }

        // =========================================================================================
        // Parameters
        // =========================================================================================
        [NinjaScriptProperty]
        [Range(1, 300)]
        [Display(Name = "SyntheticSeconds", Description = "Internal synthetic auction bar duration in seconds. Validation default is 15.", Order = 1, GroupName = "01 Synthetic Auction")]
        public int SyntheticSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PercentileGateMode", Description = "Percentile gate mode. Off makes it easiest to prove execution. P50 is validation follow-up. P80 is stricter.", Order = 2, GroupName = "01 Synthetic Auction")]
        public CGPercentileGateMode PercentileGateMode { get; set; }

        [NinjaScriptProperty]
        [Range(10, 1000)]
        [Display(Name = "RangeLookbackBars", Description = "Rolling synthetic range lookback used for p50/p80 percentile estimates.", Order = 3, GroupName = "01 Synthetic Auction")]
        public int RangeLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "WarmupSyntheticBars", Description = "Minimum synthetic bars before trading is allowed.", Order = 4, GroupName = "01 Synthetic Auction")]
        public int WarmupSyntheticBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "MinExpansionScore", Description = "Minimum expansion score required. Validation default is permissive.", Order = 5, GroupName = "02 Signal Gates")]
        public double MinExpansionScore { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 200.0)]
        [Display(Name = "MinConfirmedEnergy", Description = "Energy threshold for confirmed trend state.", Order = 6, GroupName = "02 Signal Gates")]
        public double MinConfirmedEnergy { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 200.0)]
        [Display(Name = "MinBuildingEnergy", Description = "Energy threshold for early building-state entries.", Order = 7, GroupName = "02 Signal Gates")]
        public double MinBuildingEnergy { get; set; }

        [NinjaScriptProperty]
        [Range(10.0, 500.0)]
        [Display(Name = "ExhaustionEnergy", Description = "Energy level considered exhausted. Exhaustion states are blocked for new entries.", Order = 8, GroupName = "02 Signal Gates")]
        public double ExhaustionEnergy { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowBuildingStateEntries", Description = "Allow entries from BuildingUp/BuildingDown when energy is strong enough.", Order = 9, GroupName = "02 Signal Gates")]
        public bool AllowBuildingStateEntries { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowPullbackReclaimEntries", Description = "Allow simple pullback reclaim entries after a one-bar counter move.", Order = 10, GroupName = "02 Signal Gates")]
        public bool AllowPullbackReclaimEntries { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseRthOnly", Description = "If true, entries are only allowed between StartTimeEt and EndTimeEt using chart time.", Order = 11, GroupName = "03 Session")]
        public bool UseRthOnly { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "StartTimeEt", Description = "RTH start time in HHmmss. Use chart/session time convention.", Order = 12, GroupName = "03 Session")]
        public int StartTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EndTimeEt", Description = "RTH end time in HHmmss. Use chart/session time convention.", Order = 13, GroupName = "03 Session")]
        public int EndTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "TargetTicks", Description = "Profit target ticks submitted after entry fill.", Order = 14, GroupName = "04 Risk")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "StopTicks", Description = "Stop-loss ticks submitted after entry fill.", Order = 15, GroupName = "04 Risk")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(5, 3600)]
        [Display(Name = "MaxHoldSeconds", Description = "Advisory time stop in seconds. OCO stop/target remains primary protection.", Order = 16, GroupName = "04 Risk")]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "CooldownSeconds", Description = "Minimum seconds between new entries.", Order = 17, GroupName = "04 Risk")]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "SameDirectionStopoutsForEmbargo", Description = "Consecutive same-direction stopouts required before temporary directional embargo.", Order = 18, GroupName = "04 Risk")]
        public int SameDirectionStopoutsForEmbargo { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "EmbargoMinutes", Description = "Minutes to embargo a direction after repeated same-direction stopouts.", Order = 19, GroupName = "04 Risk")]
        public int EmbargoMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", Description = "Print synthetic engine diagnostics to NinjaScript Output.", Order = 20, GroupName = "05 Diagnostics")]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintEverySyntheticBar", Description = "Print one DIAG line per synthetic bar.", Order = 21, GroupName = "05 Diagnostics")]
        public bool PrintEverySyntheticBar { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintOnlyWhenAction", Description = "If true, suppress routine DIAG and print mainly actions/blocks.", Order = 22, GroupName = "05 Diagnostics")]
        public bool PrintOnlyWhenAction { get; set; }
    }
}
