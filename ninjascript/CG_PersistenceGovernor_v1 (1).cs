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
// CG_PersistenceGovernor_v1.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-07 12:05:00 EDT
//
// MNQ CG PersistenceGovernor v1 — ROTATION-SUPPRESSED GOVERNED EXECUTION BUILD
//
// PURPOSE
// -------
// This build takes the now-working synthetic auction engine and adds a governance layer intended to stop
// the machine-gun behavior observed after PercentileGateMode=Off proved the execution lifecycle. The
// prior chart showed many rapid flips, frequent stopouts inside rotational noise, and entries against
// the larger auction owner. CG_PersistenceGovernor_v1 therefore adds trend ownership, anti-flip rules,
// post-stop cooldown, rotation suppression, and continuation-only defaults.
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
    public enum CGPGAuctionState
    {
        None,
        BuildingUp,
        ConfirmedUp,
        ExhaustingUp,
        BuildingDown,
        ConfirmedDown,
        ExhaustingDown
    }

    public enum CGPGPercentileGateMode
    {
        Off,
        P50,
        P80
    }

    public enum CGPGTrendRegime
    {
        Rotation,
        TrendUp,
        TrendDown
    }

    public class CG_PersistenceGovernor_v1 : Strategy
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
        private CGPGAuctionState auctionState = CGPGAuctionState.None;
        private CGPGAuctionState priorAuctionState = CGPGAuctionState.None;
        private double auctionEnergy;
        private int stateTransitionCount;
        private int confirmedBarsInState;

        // -----------------------------
        // Persistence governor state
        // -----------------------------
        private double directionalPersistenceScore;
        private CGPGTrendRegime trendRegime = CGPGTrendRegime.Rotation;
        private CGPGTrendRegime priorTrendRegime = CGPGTrendRegime.Rotation;
        private int sameDirectionSyntheticBars;
        private int lastDirectionalSign;
        private readonly Queue<int> directionWindow = new Queue<int>();
        private int directionFlipCount;
        private double rotationScore;
        private DateTime lastStopExitTime = Core.Globals.MinDate;
        private string lastStopSide = string.Empty;
        private long persistenceBlocked;
        private long rotationBlocked;
        private long antiFlipBlocked;
        private long postStopBlocked;

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
                Name = "CG_PersistenceGovernor_v1";
                Description = "MNQ CG PersistenceGovernor v1. Tick-fed synthetic auction engine with trend ownership, anti-flip, stop cooldown, and rotation suppression. Apply to 1-tick MNQ chart.";

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
                SyntheticSeconds = 30;
                PercentileGateMode = CGPGPercentileGateMode.Off;
                RangeLookbackBars = 160;
                WarmupSyntheticBars = 30;
                MinExpansionScore = 0.85;
                MinConfirmedEnergy = 26.0;
                MinBuildingEnergy = 32.0;
                ExhaustionEnergy = 95.0;
                AllowBuildingStateEntries = false;
                AllowPullbackReclaimEntries = false;

                // Persistence governor defaults: these are intentionally stricter than the validation build
                // because execution proof is complete. The goal now is fewer, better, continuation trades.
                UsePersistenceGovernor = true;
                PersistenceDecay = 0.88;
                TrendOwnershipThreshold = 32.0;
                MinEntryPersistenceScore = 38.0;
                AllowRotationEntries = false;
                RequireConfirmedStateForEntry = true;
                RequireTwoBarsSameDirection = true;
                MinSameDirectionBarsForEntry = 4;
                UseRotationDetector = true;
                RotationLookbackBars = 12;
                MaxDirectionFlipsInLookback = 3;
                RotationScoreBlockThreshold = 5.0;
                BlockMiddayRotation = true;
                MiddayStartEt = 110000;
                MiddayEndEt = 140000;
                PostStopCooldownSeconds = 180;
                AntiFlipCooldownSeconds = 300;
                FlipOverrideExpansion = 4.00;

                StartTimeEt = 93000;
                EndTimeEt = 155900;
                UseRthOnly = true;

                TargetTicks = 28;
                StopTicks = 22;
                MaxHoldSeconds = 240;
                CooldownSeconds = 120;
                SameDirectionStopoutsForEmbargo = 1;
                EmbargoMinutes = 20;

                PrintDiagnostics = true;
                PrintEverySyntheticBar = false;
                PrintOnlyWhenAction = true;
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
                lastStopExitTime = time;
                lastStopSide = activeEntrySide;

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
            UpdatePersistenceGovernor();
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
                auctionState = CGPGAuctionState.ExhaustingUp;
            else if (auctionEnergy <= -ExhaustionEnergy)
                auctionState = CGPGAuctionState.ExhaustingDown;
            else if (auctionEnergy >= MinConfirmedEnergy)
                auctionState = CGPGAuctionState.ConfirmedUp;
            else if (auctionEnergy >= Math.Max(5.0, MinBuildingEnergy * 0.55))
                auctionState = CGPGAuctionState.BuildingUp;
            else if (auctionEnergy <= -MinConfirmedEnergy)
                auctionState = CGPGAuctionState.ConfirmedDown;
            else if (auctionEnergy <= -Math.Max(5.0, MinBuildingEnergy * 0.55))
                auctionState = CGPGAuctionState.BuildingDown;
            else
                auctionState = CGPGAuctionState.None;

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
        // Persistence governor
        // =========================================================================================
        private void UpdatePersistenceGovernor()
        {
            int dir = 0;
            if (auctionState == CGPGAuctionState.BuildingUp || auctionState == CGPGAuctionState.ConfirmedUp)
                dir = 1;
            else if (auctionState == CGPGAuctionState.BuildingDown || auctionState == CGPGAuctionState.ConfirmedDown)
                dir = -1;
            else if (Math.Abs(auctionEnergy) >= MinConfirmedEnergy)
                dir = auctionEnergy > 0 ? 1 : -1;

            if (dir == 0)
            {
                sameDirectionSyntheticBars = 0;
                directionalPersistenceScore *= PersistenceDecay;
            }
            else
            {
                if (dir == lastDirectionalSign)
                    sameDirectionSyntheticBars++;
                else
                    sameDirectionSyntheticBars = 1;

                double contribution = Math.Min(12.0, Math.Abs(auctionEnergy) * 0.18)
                                    + Math.Min(8.0, lastExpansionScore * 2.0)
                                    + Math.Min(6.0, sameDirectionSyntheticBars * 1.5);

                directionalPersistenceScore = (directionalPersistenceScore * PersistenceDecay) + (dir * contribution);
            }

            directionalPersistenceScore = Math.Max(-100.0, Math.Min(100.0, directionalPersistenceScore));
            UpdateRotationDetector(dir);
            lastDirectionalSign = dir;

            priorTrendRegime = trendRegime;
            if (directionalPersistenceScore >= TrendOwnershipThreshold)
                trendRegime = CGPGTrendRegime.TrendUp;
            else if (directionalPersistenceScore <= -TrendOwnershipThreshold)
                trendRegime = CGPGTrendRegime.TrendDown;
            else
                trendRegime = CGPGTrendRegime.Rotation;

            if (PrintDiagnostics && trendRegime != priorTrendRegime)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} REGIME {1} syn={2} persist={3:F2} sameDirBars={4} energy={5:F2} exp={6:F2}",
                    syntheticEndTime, trendRegime, syntheticBarId, directionalPersistenceScore, sameDirectionSyntheticBars, auctionEnergy, lastExpansionScore));
            }
        }


        private void UpdateRotationDetector(int dir)
        {
            if (!UseRotationDetector)
            {
                rotationScore = 0.0;
                directionFlipCount = 0;
                return;
            }

            directionWindow.Enqueue(dir);
            while (directionWindow.Count > RotationLookbackBars)
                directionWindow.Dequeue();

            directionFlipCount = 0;
            int prevNonZero = 0;
            int nonZeroCount = 0;
            foreach (int value in directionWindow)
            {
                if (value == 0)
                    continue;

                nonZeroCount++;
                if (prevNonZero != 0 && value != prevNonZero)
                    directionFlipCount++;
                prevNonZero = value;
            }

            // Rotation score is deliberately simple and robust for playback: lots of sign flips, weak ownership,
            // and a lack of same-direction bars define the dead balance zones that were overtraded in testing.
            double flipComponent = directionFlipCount * 1.5;
            double weakOwnershipComponent = Math.Max(0.0, (TrendOwnershipThreshold - Math.Abs(directionalPersistenceScore)) / Math.Max(1.0, TrendOwnershipThreshold)) * 3.0;
            double weakSequenceComponent = sameDirectionSyntheticBars < MinSameDirectionBarsForEntry ? 1.5 : 0.0;
            double lowExpansionComponent = lastExpansionScore < MinExpansionScore ? 1.0 : 0.0;

            rotationScore = flipComponent + weakOwnershipComponent + weakSequenceComponent + lowExpansionComponent;
        }

        private bool IsMiddayWindow(DateTime decisionTime)
        {
            int t = ToTime(decisionTime);
            return t >= MiddayStartEt && t <= MiddayEndEt;
        }

        private bool IsRotationEnvironment()
        {
            if (!UseRotationDetector)
                return false;

            if (directionFlipCount > MaxDirectionFlipsInLookback)
                return true;

            if (rotationScore >= RotationScoreBlockThreshold)
                return true;

            return false;
        }

        private bool PassesPersistenceGovernor(bool longCandidate, bool shortCandidate, DateTime decisionTime)
        {
            if (!UsePersistenceGovernor)
                return true;

            if (PostStopCooldownSeconds > 0 && lastStopExitTime != Core.Globals.MinDate
                && (decisionTime - lastStopExitTime).TotalSeconds < PostStopCooldownSeconds)
            {
                postStopBlocked++;
                PrintBlock(decisionTime, "BLOCK_POST_STOP_COOLDOWN");
                return false;
            }

            if (RequireConfirmedStateForEntry)
            {
                if (longCandidate && auctionState != CGPGAuctionState.ConfirmedUp)
                {
                    persistenceBlocked++;
                    PrintBlock(decisionTime, "BLOCK_NOT_CONFIRMED_LONG");
                    return false;
                }
                if (shortCandidate && auctionState != CGPGAuctionState.ConfirmedDown)
                {
                    persistenceBlocked++;
                    PrintBlock(decisionTime, "BLOCK_NOT_CONFIRMED_SHORT");
                    return false;
                }
            }

            if (RequireTwoBarsSameDirection && sameDirectionSyntheticBars < MinSameDirectionBarsForEntry)
            {
                persistenceBlocked++;
                PrintBlock(decisionTime, "BLOCK_INSUFFICIENT_PERSISTENCE_BARS");
                return false;
            }

            if (UseRotationDetector && IsRotationEnvironment())
            {
                rotationBlocked++;
                PrintBlock(decisionTime, "BLOCK_ROTATION_SCORE");
                return false;
            }

            if (BlockMiddayRotation && IsMiddayWindow(decisionTime) && Math.Abs(directionalPersistenceScore) < (MinEntryPersistenceScore * 1.35))
            {
                rotationBlocked++;
                PrintBlock(decisionTime, "BLOCK_MIDDAY_ROTATION");
                return false;
            }

            if (!AllowRotationEntries && trendRegime == CGPGTrendRegime.Rotation)
            {
                rotationBlocked++;
                PrintBlock(decisionTime, "BLOCK_ROTATION");
                return false;
            }

            if (longCandidate)
            {
                if (trendRegime == CGPGTrendRegime.TrendDown)
                {
                    persistenceBlocked++;
                    PrintBlock(decisionTime, "BLOCK_LONG_IN_SELL_AUCTION");
                    return false;
                }
                if (directionalPersistenceScore < MinEntryPersistenceScore)
                {
                    persistenceBlocked++;
                    PrintBlock(decisionTime, "BLOCK_LONG_WEAK_PERSISTENCE");
                    return false;
                }
                if (lastStopSide == "SHORT" && AntiFlipCooldownSeconds > 0 && (decisionTime - lastStopExitTime).TotalSeconds < AntiFlipCooldownSeconds && lastExpansionScore < FlipOverrideExpansion)
                {
                    antiFlipBlocked++;
                    PrintBlock(decisionTime, "BLOCK_ANTI_FLIP_LONG");
                    return false;
                }
            }

            if (shortCandidate)
            {
                if (trendRegime == CGPGTrendRegime.TrendUp)
                {
                    persistenceBlocked++;
                    PrintBlock(decisionTime, "BLOCK_SHORT_IN_BUY_AUCTION");
                    return false;
                }
                if (directionalPersistenceScore > -MinEntryPersistenceScore)
                {
                    persistenceBlocked++;
                    PrintBlock(decisionTime, "BLOCK_SHORT_WEAK_PERSISTENCE");
                    return false;
                }
                if (lastStopSide == "LONG" && AntiFlipCooldownSeconds > 0 && (decisionTime - lastStopExitTime).TotalSeconds < AntiFlipCooldownSeconds && lastExpansionScore < FlipOverrideExpansion)
                {
                    antiFlipBlocked++;
                    PrintBlock(decisionTime, "BLOCK_ANTI_FLIP_SHORT");
                    return false;
                }
            }

            return true;
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

            if (!PassesPersistenceGovernor(longCandidate, shortCandidate, decisionTime))
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
            bool confirmed = auctionState == CGPGAuctionState.ConfirmedUp;
            bool building = AllowBuildingStateEntries && auctionState == CGPGAuctionState.BuildingUp && auctionEnergy >= MinBuildingEnergy;
            bool reclaim = AllowPullbackReclaimEntries && hasPreviousSynthetic && syntheticClose > syntheticOpen && previousSyntheticClose < previousSyntheticOpen && auctionEnergy > 0;
            return confirmed || building || reclaim;
        }

        private bool IsShortCandidate()
        {
            bool confirmed = auctionState == CGPGAuctionState.ConfirmedDown;
            bool building = AllowBuildingStateEntries && auctionState == CGPGAuctionState.BuildingDown && auctionEnergy <= -MinBuildingEnergy;
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
            if (PercentileGateMode == CGPGPercentileGateMode.Off)
                return true;

            if (PercentileGateMode == CGPGPercentileGateMode.P50)
                return lastSyntheticRange >= Math.Max(TickSize, p50Range);

            if (PercentileGateMode == CGPGPercentileGateMode.P80)
                return lastSyntheticRange >= Math.Max(TickSize, p80Range);

            return true;
        }

        private bool IsExhaustedAgainstEntry(bool longCandidate, bool shortCandidate)
        {
            // Validation build does not forbid confirmed trends. It only blocks the explicit exhaustion states.
            if (longCandidate && auctionState == CGPGAuctionState.ExhaustingUp)
                return true;
            if (shortCandidate && auctionState == CGPGAuctionState.ExhaustingDown)
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

            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} {1} syn={2} state={3} regime={4} persist={5:F2} sameDir={6} flips={28} rotScore={29:F2} energy={7:F2} exp={8:F2} range={9:F1} p50={10:F1} p80={11:F1} pos={12} L={13} S={14} pctBlk={15} expBlk={16} energyBlk={17} stateBlk={18} persistBlk={19} rotBlk={20} flipBlk={21} postStopBlk={22} coolBlk={23} flatBlk={24} rthBlk={25} warmBlk={26} states={27}",
                time,
                label,
                syntheticBarId,
                auctionState,
                trendRegime,
                directionalPersistenceScore,
                sameDirectionSyntheticBars,
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
                persistenceBlocked,
                rotationBlocked,
                antiFlipBlocked,
                postStopBlocked,
                cooldownBlocked,
                flatBlocked,
                rthBlocked,
                warmupBlocked,
                stateTransitionCount,
                directionFlipCount,
                rotationScore));
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

            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} {1} signal={2} syn={3} state={4} regime={5} persist={6:F2} sameDir={7} flips={14} rotScore={15:F2} energy={8:F2} exp={9:F2} range={10:F1} p50={11:F1} p80={12:F1} pos={13}",
                time, action, signal, syntheticBarId, auctionState, trendRegime, directionalPersistenceScore, sameDirectionSyntheticBars, auctionEnergy, lastExpansionScore, lastSyntheticRange, p50Range, p80Range, Position.MarketPosition, directionFlipCount, rotationScore));
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

            auctionState = CGPGAuctionState.None;
            priorAuctionState = CGPGAuctionState.None;
            auctionEnergy = 0;
            stateTransitionCount = 0;
            confirmedBarsInState = 0;

            directionalPersistenceScore = 0;
            trendRegime = CGPGTrendRegime.Rotation;
            priorTrendRegime = CGPGTrendRegime.Rotation;
            sameDirectionSyntheticBars = 0;
            lastDirectionalSign = 0;
            directionWindow.Clear();
            directionFlipCount = 0;
            rotationScore = 0.0;
            lastStopExitTime = Core.Globals.MinDate;
            lastStopSide = string.Empty;

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
            persistenceBlocked = 0;
            rotationBlocked = 0;
            antiFlipBlocked = 0;
            postStopBlocked = 0;
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
        public CGPGPercentileGateMode PercentileGateMode { get; set; }

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
        [Display(Name = "UsePersistenceGovernor", Description = "If true, enforce trend ownership, rotation suppression, post-stop cooldown, and anti-flip governance.", Order = 11, GroupName = "03 Persistence Governor")]
        public bool UsePersistenceGovernor { get; set; }

        [NinjaScriptProperty]
        [Range(0.10, 0.99)]
        [Display(Name = "PersistenceDecay", Description = "Decay factor for directional persistence score. Higher values preserve auction ownership longer.", Order = 12, GroupName = "03 Persistence Governor")]
        public double PersistenceDecay { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 100.0)]
        [Display(Name = "TrendOwnershipThreshold", Description = "Absolute persistence score required to classify the tape as owned by buyers or sellers.", Order = 13, GroupName = "03 Persistence Governor")]
        public double TrendOwnershipThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 100.0)]
        [Display(Name = "MinEntryPersistenceScore", Description = "Minimum same-direction persistence score required for entries.", Order = 14, GroupName = "03 Persistence Governor")]
        public double MinEntryPersistenceScore { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowRotationEntries", Description = "If false, block new entries while the trend regime is Rotation.", Order = 15, GroupName = "03 Persistence Governor")]
        public bool AllowRotationEntries { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireConfirmedStateForEntry", Description = "If true, entries require ConfirmedUp or ConfirmedDown, not merely BuildingUp/BuildingDown.", Order = 16, GroupName = "03 Persistence Governor")]
        public bool RequireConfirmedStateForEntry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireTwoBarsSameDirection", Description = "If true, blocks one-bar impulse entries until at least two same-direction synthetic bars are observed.", Order = 17, GroupName = "03 Persistence Governor")]
        public bool RequireTwoBarsSameDirection { get; set; }

        [NinjaScriptProperty]
        [Range(1, 12)]
        [Display(Name = "MinSameDirectionBarsForEntry", Description = "Minimum same-direction synthetic bars required before a trend entry. Raises the bar above one-bar impulse trading.", Order = 18, GroupName = "03 Persistence Governor")]
        public int MinSameDirectionBarsForEntry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseRotationDetector", Description = "If true, block entries when direction flips and weak ownership indicate rotational chop.", Order = 19, GroupName = "03 Persistence Governor")]
        public bool UseRotationDetector { get; set; }

        [NinjaScriptProperty]
        [Range(4, 40)]
        [Display(Name = "RotationLookbackBars", Description = "Synthetic bar window used to count direction flips for rotation detection.", Order = 20, GroupName = "03 Persistence Governor")]
        public int RotationLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "MaxDirectionFlipsInLookback", Description = "Maximum allowed directional flips in the rotation lookback window.", Order = 21, GroupName = "03 Persistence Governor")]
        public int MaxDirectionFlipsInLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 30.0)]
        [Display(Name = "RotationScoreBlockThreshold", Description = "Composite rotation score threshold above which entries are blocked.", Order = 22, GroupName = "03 Persistence Governor")]
        public double RotationScoreBlockThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BlockMiddayRotation", Description = "If true, adds stricter participation control during 11:00-14:00 ET unless ownership is very strong.", Order = 23, GroupName = "03 Persistence Governor")]
        public bool BlockMiddayRotation { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "MiddayStartEt", Description = "Reduced-participation start time in HHmmss.", Order = 24, GroupName = "03 Persistence Governor")]
        public int MiddayStartEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "MiddayEndEt", Description = "Reduced-participation end time in HHmmss.", Order = 25, GroupName = "03 Persistence Governor")]
        public int MiddayEndEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 600)]
        [Display(Name = "PostStopCooldownSeconds", Description = "Global cooldown after any stop fill. Prevents immediate machine-gun re-entry after a loss.", Order = 18, GroupName = "03 Persistence Governor")]
        public int PostStopCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 900)]
        [Display(Name = "AntiFlipCooldownSeconds", Description = "Blocks immediate opposite-side flip after a stop unless expansion override is met.", Order = 19, GroupName = "03 Persistence Governor")]
        public int AntiFlipCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 20.0)]
        [Display(Name = "FlipOverrideExpansion", Description = "Expansion score required to override anti-flip protection after a stopped trade.", Order = 20, GroupName = "03 Persistence Governor")]
        public double FlipOverrideExpansion { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseRthOnly", Description = "If true, entries are only allowed between StartTimeEt and EndTimeEt using chart time.", Order = 21, GroupName = "04 Session")]
        public bool UseRthOnly { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "StartTimeEt", Description = "RTH start time in HHmmss. Use chart/session time convention.", Order = 22, GroupName = "04 Session")]
        public int StartTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EndTimeEt", Description = "RTH end time in HHmmss. Use chart/session time convention.", Order = 23, GroupName = "04 Session")]
        public int EndTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "TargetTicks", Description = "Profit target ticks submitted after entry fill.", Order = 14, GroupName = "05 Risk")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "StopTicks", Description = "Stop-loss ticks submitted after entry fill.", Order = 15, GroupName = "05 Risk")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(5, 3600)]
        [Display(Name = "MaxHoldSeconds", Description = "Advisory time stop in seconds. OCO stop/target remains primary protection.", Order = 16, GroupName = "05 Risk")]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "CooldownSeconds", Description = "Minimum seconds between new entries.", Order = 17, GroupName = "05 Risk")]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "SameDirectionStopoutsForEmbargo", Description = "Consecutive same-direction stopouts required before temporary directional embargo.", Order = 18, GroupName = "05 Risk")]
        public int SameDirectionStopoutsForEmbargo { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "EmbargoMinutes", Description = "Minutes to embargo a direction after repeated same-direction stopouts.", Order = 19, GroupName = "05 Risk")]
        public int EmbargoMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", Description = "Print synthetic engine diagnostics to NinjaScript Output.", Order = 20, GroupName = "06 Diagnostics")]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintEverySyntheticBar", Description = "Print one DIAG line per synthetic bar.", Order = 21, GroupName = "06 Diagnostics")]
        public bool PrintEverySyntheticBar { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintOnlyWhenAction", Description = "If true, suppress routine DIAG and print mainly actions/blocks.", Order = 22, GroupName = "06 Diagnostics")]
        public bool PrintOnlyWhenAction { get; set; }
    }
}
