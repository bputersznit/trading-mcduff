// CG_MNQ_Flagship_Hybrid_v1_2_L2Validation.cs
// NinjaTrader 8 Strategy
// Last Modified: 2026-05-03 EDT
//
// v1.2 L2 Validation Branch
// Adds L2 replay-feed diagnostics to CG_MNQ_Flagship_Hybrid_v1_1:
//   - Raw OnMarketDepth prints: [L2]
//   - Once-per-minute health prints: [L2_HEALTH]
//   - L2 event counters by bid/ask/top-of-book/add/update/remove
//   - Telemetry row type: L2_HEALTH
//
// ⚠️ CRITICAL POSITION LIMIT: ONLY ONE MNQ CONTRACT AT A TIME ⚠️
//   - Entry calls are hardcoded to 1 contract.
//   - This branch is for replay validation and monitoring, not final production parity.
//
// Install:
//   Documents\NinjaTrader 8\bin\Custom\Strategies\CG_MNQ_Flagship_Hybrid_v1_2_L2Validation.cs

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_MNQ_Flagship_Hybrid_v1_2_L2Validation : Strategy
    {
        // ================================================================
        // Constants
        // ================================================================

        private const string LongSignalName  = "Flagship_Long";
        private const string ShortSignalName = "Flagship_Short";

        private const double MNQ_TICK_VALUE_USD = 0.50;
        private const double COMMISSION_RT_USD  = 0.70;

        private const int OR_START_HOUR   = 9;
        private const int OR_START_MINUTE = 30;

        private TimeZoneInfo easternTimeZone = null;

        // ================================================================
        // Layer 1: ORB State
        // ================================================================

        private bool orActive = false;
        private bool orComplete = false;
        private DateTime orStartTime = Core.Globals.MinDate;
        private DateTime orEndTime = Core.Globals.MinDate;

        private double orHigh = double.MinValue;
        private double orLow = double.MaxValue;
        private double orWidth = 0.0;
        private double orVwap = 0.0;
        private double orVwapSum = 0.0;
        private double orVolumeSum = 0.0;

        private enum OrbState
        {
            PRE_OR,
            BUILDING_OR,
            NEUTRAL,
            LONG_BREAKOUT,
            SHORT_BREAKOUT,
            FAILED_LONG,
            FAILED_SHORT,
            CHOP,
            FLAT_LOCK
        }

        private OrbState currentOrbState = OrbState.PRE_OR;
        private OrbState previousOrbState = OrbState.PRE_OR;
        private string currentOrbTransitionReason = "INIT";
        private DateTime lastOrbStateEvalTime = Core.Globals.MinDate;
        private DateTime lastOrbTransitionTime = Core.Globals.MinDate;
        private DateTime lastLongBreakoutTime = Core.Globals.MinDate;
        private DateTime lastShortBreakoutTime = Core.Globals.MinDate;
        private long orbStateTransitions = 0;

        private string currentRegime = "UNKNOWN";

        // ================================================================
        // Layer 2: T2 Event State
        // ================================================================

        private double currentEventDelta = 0.0;
        private double currentEventImbalance = 0.0;
        private double currentTotalEvents = 0.0;
        private double currentSpreadTicks = 1.0;

        // ================================================================
        // Layer 3: T3 Wall State
        // ================================================================

        private double currentBidWallScore = 0.0;
        private double currentAskWallScore = 0.0;
        private double currentAggressorBuyVol = 0.0;
        private double currentAggressorSellVol = 0.0;

        private double lastBestBid = 0.0;
        private double lastBestAsk = 0.0;
        private long bidWallSize = 0;
        private long askWallSize = 0;

        // ================================================================
        // L2 Replay Feed Validation Diagnostics
        // ================================================================

        private long l2Events = 0;
        private long l2BidEvents = 0;
        private long l2AskEvents = 0;
        private long l2TopBidEvents = 0;
        private long l2TopAskEvents = 0;
        private long l2InsertEvents = 0;
        private long l2UpdateEvents = 0;
        private long l2RemoveEvents = 0;
        private DateTime lastL2HealthPrintTime = Core.Globals.MinDate;

        // ================================================================
        // Layer 4: Padder State
        // ================================================================

        private bool failedBreakoutDetected = false;
        private string manipulationReason = "";
        private double priorDayHigh = 0.0;
        private double priorDayLow = 0.0;
        private bool priorDayDataAvailable = false;

        // ================================================================
        // Trade State
        // ================================================================

        private long tradeIdCounter = 0;
        private long activeTradeId = 0;

        private DateTime lastEntryAttemptTime = Core.Globals.MinDate;
        private int lastSessionDate = -1;

        private bool pendingEntry = false;
        private bool protectionArmed = false;

        private double entryPrice = 0.0;
        private MarketPosition entryPosition = MarketPosition.Flat;
        private string activeSide = "";
        private DateTime activeEntryTime = Core.Globals.MinDate;

        private double activeHighSinceEntry = 0.0;
        private double activeLowSinceEntry = 0.0;
        private double activeMfeTicks = 0.0;
        private double activeMaeTicks = 0.0;

        // ================================================================
        // Protection State
        // ================================================================

        private int consecutiveLosses = 0;
        private bool choppyDayDetected = false;

        private bool dailyMaxLossHit = false;
        private double sessionPnL = 0.0;
        private double sessionRealisticPnL = 0.0;
        private int sessionTrades = 0;

        private bool emergencyStopTriggered = false;
        private double cumulativePnL = 0.0;
        private double peakCumulativePnL = 0.0;

        // ================================================================
        // Diagnostics
        // ================================================================

        private long rthRejects = 0;
        private long orNotCompleteRejects = 0;
        private long orbBiasRejects = 0;
        private long minRangeRejects = 0;
        private long spreadRejects = 0;
        private long eventDeltaRejects = 0;
        private long imbalanceRejects = 0;
        private long wallScoreRejects = 0;
        private long volumeRejects = 0;
        private long manipulationRejects = 0;
        private long positionRejects = 0;
        private long pendingRejects = 0;
        private long choppyRejects = 0;
        private long dailyLossRejects = 0;
        private long emergencyRejects = 0;
        private long longSignals = 0;
        private long shortSignals = 0;
        private long entries = 0;
        private long exits = 0;

        private long orbLongSignals = 0;
        private long orbShortSignals = 0;
        private long t2LongSignals = 0;
        private long t2ShortSignals = 0;
        private long t3ConfirmedLongs = 0;
        private long t3ConfirmedShorts = 0;
        private long padderBlocks = 0;

        // ================================================================
        // Telemetry
        // ================================================================

        private StreamWriter telemetryWriter = null;
        private string telemetryPath = "";

        // ================================================================
        // NT Lifecycle
        // ================================================================

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "CG MNQ Flagship Hybrid v1.2 L2 Validation: ORB + T2 + T3 Wall + Padder with L2 replay diagnostics";
                Name = "CG_MNQ_Flagship_Hybrid_v1_2_L2Validation";

                Calculate = Calculate.OnEachTick;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                TimeInForce = TimeInForce.Day;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                BarsRequiredToTrade = 20;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;

                Quantity = 1;

                OpeningRangeMinutes = 15;
                MinRangeWidth = 5.0;
                OrbBreakoutBuffer = 2.0;
                OrbStateEvalSeconds = 1;
                EnableLunchSuppression = true;
                BlockChopState = true;
                AllowFadeAfterFailure = true;

                MinEventDelta = 20.0;
                MinEventImbalance = 0.15;
                EventLookbackBars = 200;

                MinWallSize = 100;
                MinAggressorVolume = 50;

                EnableManipulationFilter = true;
                FailedBreakoutBars = 5;

                StopTicks = 20;
                TargetTicks = 40;
                MaxHoldSeconds = 600;

                StartTimeEt = 93000;
                EndTimeEt = 155900;

                EnableChoppyFilter = true;
                MaxConsecutiveLosses = 3;

                EnableDailyMaxLoss = true;
                DailyMaxLoss = 200.0;

                EnableEmergencyStop = true;
                EmergencyStopDD = 400.0;

                MaxSpreadTicks = 8;
                SlippageTicks = 2;

                EnableTelemetry = true;
                TelemetryFilePrefix = "CG_MNQ_Flagship_Hybrid_v1_2_L2Validation";
                PrintDiagnostics = true;
                EnableL2VerbosePrint = true;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);

                if (PrintDiagnostics)
                    Print("[FLAGSHIP] Added 1-tick data series for T2 event calculations");
            }
            else if (State == State.DataLoaded)
            {
                InitializeTimeZone();
                ResetSessionState();

                if (EnableTelemetry)
                    OpenTelemetry();

                if (PrintDiagnostics)
                {
                    Print("╔════════════════════════════════════════════════════════════════╗");
                    Print("║  CG MNQ FLAGSHIP HYBRID v1.2 L2 VALIDATION                    ║");
                    Print("║  Multi-Layer System + L2 Replay Feed Diagnostics              ║");
                    Print("╚════════════════════════════════════════════════════════════════╝");
                }
            }
            else if (State == State.Terminated)
            {
                PrintSummary();
                CloseTelemetry();
            }
        }

        // ================================================================
        // Main Loop
        // ================================================================

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < BarsRequiredToTrade)
                return;

            DateTime now = Time[0];
            DateTime etNow = ToEastern(now);
            int sessionDate = etNow.Year * 10000 + etNow.Month * 100 + etNow.Day;

            if (sessionDate != lastSessionDate)
            {
                ResetSessionState();
                lastSessionDate = sessionDate;
            }

            if (orActive)
                UpdateOpeningRange(etNow);

            if (!orActive && !orComplete)
            {
                if (etNow.Hour == OR_START_HOUR && etNow.Minute == OR_START_MINUTE)
                    StartOpeningRange(etNow);
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                UpdateMfeMae();
                positionRejects++;
                return;
            }

            if (!IsWithinRth(etNow))
            {
                rthRejects++;
                return;
            }

            currentRegime = GetSessionRegime(etNow);

            // L2 replay validation health summary.
            PrintL2Health(etNow);

            if (EnableEmergencyStop && emergencyStopTriggered)
            {
                emergencyRejects++;
                return;
            }

            if (EnableDailyMaxLoss && dailyMaxLossHit)
            {
                dailyLossRejects++;
                return;
            }

            if (EnableChoppyFilter && choppyDayDetected)
            {
                choppyRejects++;
                return;
            }

            if (!orComplete)
            {
                orNotCompleteRejects++;
                return;
            }

            if (pendingEntry)
            {
                pendingRejects++;
                return;
            }

            ComputeT2EventFeatures();
            ComputeT3WallFeatures();
            CheckManipulation(etNow);
            UpdateOrbState(etNow);

            if (BlockChopState && currentOrbState == OrbState.CHOP)
            {
                minRangeRejects++;
                return;
            }

            if (currentOrbState == OrbState.FLAT_LOCK)
            {
                dailyLossRejects++;
                return;
            }

            if (EnableLunchSuppression && currentRegime == "LUNCH")
            {
                choppyRejects++;
                return;
            }

            if (currentSpreadTicks > MaxSpreadTicks)
            {
                spreadRejects++;
                return;
            }

            bool longSignal = EvaluateLongSignal();
            bool shortSignal = EvaluateShortSignal();

            if (longSignal && !shortSignal)
            {
                longSignals++;
                SubmitLong(now);
            }
            else if (shortSignal && !longSignal)
            {
                shortSignals++;
                SubmitShort(now);
            }
        }

        // ================================================================
        // Market Depth Handler + L2 Diagnostics
        // ================================================================

        protected override void OnMarketDepth(MarketDepthEventArgs marketDepthUpdate)
        {
            if (marketDepthUpdate == null)
                return;

            l2Events++;

            if (marketDepthUpdate.MarketDataType == MarketDataType.Bid)
            {
                l2BidEvents++;

                if (marketDepthUpdate.Position == 0)
                    l2TopBidEvents++;
            }
            else if (marketDepthUpdate.MarketDataType == MarketDataType.Ask)
            {
                l2AskEvents++;

                if (marketDepthUpdate.Position == 0)
                    l2TopAskEvents++;
            }

            if (marketDepthUpdate.Operation == Operation.Add)
                l2InsertEvents++;
            else if (marketDepthUpdate.Operation == Operation.Update)
                l2UpdateEvents++;
            else if (marketDepthUpdate.Operation == Operation.Remove)
                l2RemoveEvents++;

            if (EnableL2VerbosePrint && PrintDiagnostics)
            {
                Print(string.Format(
                    "[L2] {0:yyyy-MM-dd HH:mm:ss.fff} Type={1} Pos={2} Op={3} Price={4:F2} Vol={5}",
                    Time[0],
                    marketDepthUpdate.MarketDataType,
                    marketDepthUpdate.Position,
                    marketDepthUpdate.Operation,
                    marketDepthUpdate.Price,
                    marketDepthUpdate.Volume
                ));
            }

            if (marketDepthUpdate.MarketDataType == MarketDataType.Ask &&
                marketDepthUpdate.Position == 0)
            {
                lastBestAsk = marketDepthUpdate.Price;
                askWallSize = marketDepthUpdate.Volume;
            }
            else if (marketDepthUpdate.MarketDataType == MarketDataType.Bid &&
                     marketDepthUpdate.Position == 0)
            {
                lastBestBid = marketDepthUpdate.Price;
                bidWallSize = marketDepthUpdate.Volume;
            }
        }

        private void PrintL2Health(DateTime etNow)
        {
            if (!PrintDiagnostics)
                return;

            if (lastL2HealthPrintTime != Core.Globals.MinDate &&
                (etNow - lastL2HealthPrintTime).TotalSeconds < 60)
                return;

            lastL2HealthPrintTime = etNow;

            Print(string.Format(
                "[L2_HEALTH] {0:yyyy-MM-dd HH:mm:ss} all={1} bid={2} ask={3} topBid={4} topAsk={5} add={6} upd={7} rem={8} bidWall={9} askWall={10} bestBid={11:F2} bestAsk={12:F2}",
                etNow,
                l2Events,
                l2BidEvents,
                l2AskEvents,
                l2TopBidEvents,
                l2TopAskEvents,
                l2InsertEvents,
                l2UpdateEvents,
                l2RemoveEvents,
                bidWallSize,
                askWallSize,
                lastBestBid,
                lastBestAsk
            ));

            WriteTelemetry(
                "L2_HEALTH",
                string.Format(
                    "all:{0}|bid:{1}|ask:{2}|topBid:{3}|topAsk:{4}|add:{5}|upd:{6}|rem:{7}|bidWall:{8}|askWall:{9}|bestBid:{10:F2}|bestAsk:{11:F2}",
                    l2Events,
                    l2BidEvents,
                    l2AskEvents,
                    l2TopBidEvents,
                    l2TopAskEvents,
                    l2InsertEvents,
                    l2UpdateEvents,
                    l2RemoveEvents,
                    bidWallSize,
                    askWallSize,
                    lastBestBid,
                    lastBestAsk
                )
            );
        }

        // ================================================================
        // Market Data Handler
        // ================================================================

        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            if (marketDataUpdate.MarketDataType == MarketDataType.Last)
            {
                double price = marketDataUpdate.Price;
                long volume = marketDataUpdate.Volume;

                if (price >= lastBestAsk && lastBestAsk > 0)
                    currentAggressorBuyVol += volume;
                else if (price <= lastBestBid && lastBestBid > 0)
                    currentAggressorSellVol += volume;
            }
        }

        // ================================================================
        // ORB Management
        // ================================================================

        private void StartOpeningRange(DateTime etNow)
        {
            orActive = true;
            orComplete = false;
            orStartTime = etNow;
            orEndTime = etNow.AddMinutes(OpeningRangeMinutes);

            orHigh = High[0];
            orLow = Low[0];
            orVwapSum = Close[0] * Volume[0];
            orVolumeSum = Volume[0];

            SetOrbState(OrbState.BUILDING_OR, "OR_START", etNow);

            if (PrintDiagnostics)
                Print(string.Format("[ORB] Opening range started at {0:HH:mm:ss}", etNow));

            WriteTelemetry("OR_START", "Opening range tracking started");
        }

        private void UpdateOpeningRange(DateTime etNow)
        {
            if (!orActive)
                return;

            if (High[0] > orHigh)
                orHigh = High[0];
            if (Low[0] < orLow)
                orLow = Low[0];

            orVwapSum += Close[0] * Volume[0];
            orVolumeSum += Volume[0];

            if (etNow >= orEndTime)
                CompleteOpeningRange(etNow);
        }

        private void CompleteOpeningRange(DateTime etNow)
        {
            orActive = false;
            orComplete = true;

            orWidth = orHigh - orLow;
            orVwap = orVolumeSum > 0 ? orVwapSum / orVolumeSum : (orHigh + orLow) / 2.0;

            if (orWidth < MinRangeWidth)
                SetOrbState(OrbState.CHOP, "OR_WIDTH_BELOW_MIN", etNow);
            else
                SetOrbState(OrbState.NEUTRAL, "OR_COMPLETE_INSIDE_RANGE", etNow);

            if (PrintDiagnostics)
            {
                Print(string.Format("[ORB] Range complete: High={0:F2}, Low={1:F2}, Width={2:F2}, VWAP={3:F2}",
                    orHigh, orLow, orWidth, orVwap));
                Print(string.Format("[ORB] Dynamic state: {0} ({1})", currentOrbState, currentOrbTransitionReason));
            }

            WriteTelemetry("OR_COMPLETE",
                string.Format("H:{0:F2}|L:{1:F2}|W:{2:F2}|VWAP:{3:F2}|State:{4}|Reason:{5}",
                    orHigh, orLow, orWidth, orVwap, currentOrbState, currentOrbTransitionReason));
        }

        private void UpdateOrbState(DateTime etNow)
        {
            if (!orComplete)
                return;

            if (lastOrbStateEvalTime != Core.Globals.MinDate &&
                (etNow - lastOrbStateEvalTime).TotalSeconds < OrbStateEvalSeconds)
                return;

            lastOrbStateEvalTime = etNow;

            if (dailyMaxLossHit || emergencyStopTriggered)
            {
                SetOrbState(OrbState.FLAT_LOCK, "TREASURY_LOCKOUT", etNow);
                return;
            }

            if (orWidth < MinRangeWidth)
            {
                SetOrbState(OrbState.CHOP, "OR_WIDTH_BELOW_MIN", etNow);
                return;
            }

            double currentPrice = Close[0];
            bool aboveBreakout = currentPrice > orHigh + OrbBreakoutBuffer;
            bool belowBreakdown = currentPrice < orLow - OrbBreakoutBuffer;
            bool reclaimedHighInside = currentPrice < orHigh;
            bool reclaimedLowInside = currentPrice > orLow;

            if (failedBreakoutDetected && manipulationReason == "FAILED_BREAKOUT_HIGH")
            {
                SetOrbState(OrbState.FAILED_LONG, manipulationReason, etNow);
                return;
            }

            if (failedBreakoutDetected && manipulationReason == "FAILED_BREAKDOWN_LOW")
            {
                SetOrbState(OrbState.FAILED_SHORT, manipulationReason, etNow);
                return;
            }

            if (aboveBreakout && currentPrice >= orVwap)
            {
                if (currentOrbState != OrbState.LONG_BREAKOUT)
                {
                    orbLongSignals++;
                    lastLongBreakoutTime = etNow;
                }
                SetOrbState(OrbState.LONG_BREAKOUT, "PRICE_ABOVE_OR_HIGH_BUFFER", etNow);
                return;
            }

            if (belowBreakdown && currentPrice <= orVwap)
            {
                if (currentOrbState != OrbState.SHORT_BREAKOUT)
                {
                    orbShortSignals++;
                    lastShortBreakoutTime = etNow;
                }
                SetOrbState(OrbState.SHORT_BREAKOUT, "PRICE_BELOW_OR_LOW_BUFFER", etNow);
                return;
            }

            if (currentOrbState == OrbState.LONG_BREAKOUT && reclaimedHighInside)
            {
                SetOrbState(OrbState.FAILED_LONG, "LONG_BREAKOUT_RECLAIMED_INSIDE_OR", etNow);
                return;
            }

            if (currentOrbState == OrbState.SHORT_BREAKOUT && reclaimedLowInside)
            {
                SetOrbState(OrbState.FAILED_SHORT, "SHORT_BREAKOUT_RECLAIMED_INSIDE_OR", etNow);
                return;
            }

            SetOrbState(OrbState.NEUTRAL, "PRICE_INSIDE_OR", etNow);
        }

        private void SetOrbState(OrbState nextState, string reason, DateTime etNow)
        {
            if (currentOrbState == nextState && currentOrbTransitionReason == reason)
                return;

            previousOrbState = currentOrbState;
            currentOrbState = nextState;
            currentOrbTransitionReason = reason ?? "";
            lastOrbTransitionTime = etNow;
            orbStateTransitions++;

            if (PrintDiagnostics)
                Print(string.Format("[ORB_STATE] {0} -> {1} at {2:HH:mm:ss} reason={3}",
                    previousOrbState, currentOrbState, etNow, currentOrbTransitionReason));

            WriteTelemetry("ORB_STATE", currentOrbTransitionReason);
        }

        private bool AllowsLong()
        {
            if (currentOrbState == OrbState.LONG_BREAKOUT)
                return true;

            if (AllowFadeAfterFailure && currentOrbState == OrbState.FAILED_SHORT)
                return true;

            return false;
        }

        private bool AllowsShort()
        {
            if (currentOrbState == OrbState.SHORT_BREAKOUT)
                return true;

            if (AllowFadeAfterFailure && currentOrbState == OrbState.FAILED_LONG)
                return true;

            return false;
        }

        // ================================================================
        // T2 Event Features
        // ================================================================

        private void ComputeT2EventFeatures()
        {
            double bidEvents = 0.0;
            double askEvents = 0.0;

            int tickBars = BarsArray.Length > 1 ? BarsArray[1].Count : 0;
            if (tickBars == 0)
            {
                currentEventDelta = 0.0;
                currentEventImbalance = 0.0;
                currentTotalEvents = 0.0;
                return;
            }

            int n = Math.Min(EventLookbackBars, tickBars - 1);

            for (int i = 0; i < n; i++)
            {
                if (Closes[1][i] > Closes[1][i + 1])
                    bidEvents += 1.0;
                else if (Closes[1][i] < Closes[1][i + 1])
                    askEvents += 1.0;
                else
                {
                    bidEvents += 0.5;
                    askEvents += 0.5;
                }
            }

            currentTotalEvents = bidEvents + askEvents;
            currentEventDelta = bidEvents - askEvents;

            if (currentTotalEvents > 0)
                currentEventImbalance = currentEventDelta / currentTotalEvents;
            else
                currentEventImbalance = 0.0;

            double bid = GetCurrentBid(0);
            double ask = GetCurrentAsk(0);
            if (bid > 0 && ask > 0 && ask >= bid)
                currentSpreadTicks = Math.Max(1.0, (ask - bid) / TickSize);
            else
                currentSpreadTicks = 1.0;
        }

        // ================================================================
        // T3 Wall Features
        // ================================================================

        private void ComputeT3WallFeatures()
        {
            currentBidWallScore = bidWallSize;
            currentAskWallScore = askWallSize;

            currentAggressorBuyVol *= 0.95;
            currentAggressorSellVol *= 0.95;
        }

        // ================================================================
        // Padder Manipulation Detection
        // ================================================================

        private void CheckManipulation(DateTime etNow)
        {
            if (!EnableManipulationFilter)
                return;

            failedBreakoutDetected = false;
            manipulationReason = "";

            if (orComplete && CurrentBar >= FailedBreakoutBars)
            {
                bool brokeHigh = false;
                bool brokeHighThenReversed = false;

                for (int i = 0; i < FailedBreakoutBars; i++)
                {
                    if (High[i] > orHigh + OrbBreakoutBuffer)
                        brokeHigh = true;

                    if (brokeHigh && Close[0] < orHigh)
                    {
                        brokeHighThenReversed = true;
                        break;
                    }
                }

                if (brokeHighThenReversed)
                {
                    failedBreakoutDetected = true;
                    manipulationReason = "FAILED_BREAKOUT_HIGH";
                }

                bool brokeLow = false;
                bool brokeLowThenReversed = false;

                for (int i = 0; i < FailedBreakoutBars; i++)
                {
                    if (Low[i] < orLow - OrbBreakoutBuffer)
                        brokeLow = true;

                    if (brokeLow && Close[0] > orLow)
                    {
                        brokeLowThenReversed = true;
                        break;
                    }
                }

                if (brokeLowThenReversed)
                {
                    failedBreakoutDetected = true;
                    manipulationReason = "FAILED_BREAKDOWN_LOW";
                }
            }

            if (failedBreakoutDetected && PrintDiagnostics)
                Print(string.Format("[PADDER] Manipulation detected: {0}", manipulationReason));
        }

        // ================================================================
        // Multi-Layer Signal Evaluation
        // ================================================================

        private bool EvaluateLongSignal()
        {
            if (!AllowsLong())
            {
                orbBiasRejects++;
                return false;
            }

            if (currentEventDelta <= MinEventDelta)
            {
                eventDeltaRejects++;
                return false;
            }

            if (currentEventImbalance <= MinEventImbalance)
            {
                imbalanceRejects++;
                return false;
            }

            t2LongSignals++;

            if (currentBidWallScore < MinWallSize)
            {
                wallScoreRejects++;
                return false;
            }

            if (currentAggressorBuyVol < MinAggressorVolume)
            {
                volumeRejects++;
                return false;
            }

            t3ConfirmedLongs++;

            if (failedBreakoutDetected && manipulationReason == "FAILED_BREAKOUT_HIGH")
            {
                manipulationRejects++;
                padderBlocks++;
                return false;
            }

            return true;
        }

        private bool EvaluateShortSignal()
        {
            if (!AllowsShort())
            {
                orbBiasRejects++;
                return false;
            }

            if (currentEventDelta >= -MinEventDelta)
            {
                eventDeltaRejects++;
                return false;
            }

            if (currentEventImbalance >= -MinEventImbalance)
            {
                imbalanceRejects++;
                return false;
            }

            t2ShortSignals++;

            if (currentAskWallScore < MinWallSize)
            {
                wallScoreRejects++;
                return false;
            }

            if (currentAggressorSellVol < MinAggressorVolume)
            {
                volumeRejects++;
                return false;
            }

            t3ConfirmedShorts++;

            if (failedBreakoutDetected && manipulationReason == "FAILED_BREAKDOWN_LOW")
            {
                manipulationRejects++;
                padderBlocks++;
                return false;
            }

            return true;
        }

        // ================================================================
        // Entry/Exit Logic
        // ================================================================

        private void SubmitLong(DateTime now)
        {
            if (Position.MarketPosition != MarketPosition.Flat || pendingEntry)
                return;

            ArmProtection(LongSignalName);

            activeTradeId = ++tradeIdCounter;
            activeSide = "LONG";
            lastEntryAttemptTime = now;
            pendingEntry = true;
            protectionArmed = true;

            WriteTelemetry("SIGNAL", "LONG_FLAGSHIP_APPROVED");

            EnterLong(1, LongSignalName);
        }

        private void SubmitShort(DateTime now)
        {
            if (Position.MarketPosition != MarketPosition.Flat || pendingEntry)
                return;

            ArmProtection(ShortSignalName);

            activeTradeId = ++tradeIdCounter;
            activeSide = "SHORT";
            lastEntryAttemptTime = now;
            pendingEntry = true;
            protectionArmed = true;

            WriteTelemetry("SIGNAL", "SHORT_FLAGSHIP_APPROVED");

            EnterShort(1, ShortSignalName);
        }

        private void ArmProtection(string signalName)
        {
            SetStopLoss(signalName, CalculationMode.Ticks, StopTicks, false);
            SetProfitTarget(signalName, CalculationMode.Ticks, TargetTicks);
        }

        // ================================================================
        // Execution Handling
        // ================================================================

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            string name = execution.Order.Name ?? "";
            bool isEntry = name == LongSignalName || name == ShortSignalName;

            if (!isEntry)
            {
                if (Position.MarketPosition == MarketPosition.Flat && entryPosition != MarketPosition.Flat)
                    HandleExitExecution(price, time, name);
                return;
            }

            if (execution.Order.OrderState != OrderState.Filled &&
                execution.Order.OrderState != OrderState.PartFilled)
                return;

            if (marketPosition != MarketPosition.Flat)
                HandleEntryExecution(price, marketPosition, time);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null)
                return;

            string name = order.Name ?? "";

            if ((name == LongSignalName || name == ShortSignalName) &&
                (orderState == OrderState.Rejected || orderState == OrderState.Cancelled))
            {
                pendingEntry = false;

                if (orderState == OrderState.Rejected)
                {
                    WriteTelemetry("ORDER_REJECT", nativeError ?? "ENTRY_REJECTED");

                    if (Position.MarketPosition != MarketPosition.Flat)
                    {
                        if (Position.MarketPosition == MarketPosition.Long)
                            ExitLong("EntryRejectFlatten", LongSignalName);
                        else if (Position.MarketPosition == MarketPosition.Short)
                            ExitShort("EntryRejectFlatten", ShortSignalName);
                    }
                }
            }
        }

        private void HandleEntryExecution(double price, MarketPosition marketPosition, DateTime time)
        {
            entryPrice = price;
            entryPosition = marketPosition;
            activeEntryTime = time;
            pendingEntry = false;
            entries++;

            activeHighSinceEntry = price;
            activeLowSinceEntry = price;
            activeMfeTicks = 0.0;
            activeMaeTicks = 0.0;

            WriteTelemetry("ENTRY", "FLAGSHIP_FILLED");
        }

        private void HandleExitExecution(double exitPrice, DateTime time, string exitOrderName)
        {
            double tradePnlNt = ComputeNetPnL(entryPrice, exitPrice, entryPosition == MarketPosition.Long);

            double slippageAdjustedEntry = entryPrice;
            if (activeSide == "LONG")
                slippageAdjustedEntry += (SlippageTicks * TickSize);
            else if (activeSide == "SHORT")
                slippageAdjustedEntry -= (SlippageTicks * TickSize);

            double tradePnlRealistic = ComputeNetPnL(slippageAdjustedEntry, exitPrice, entryPosition == MarketPosition.Long);

            sessionTrades++;
            exits++;

            sessionPnL += tradePnlNt;
            sessionRealisticPnL += tradePnlRealistic;
            cumulativePnL += tradePnlNt;

            if (cumulativePnL > peakCumulativePnL)
                peakCumulativePnL = cumulativePnL;

            CheckProtectionLayers(tradePnlNt);

            WriteTelemetry("EXIT",
                string.Format("EXIT_{0}|NT_PNL:{1:F2}|REAL_PNL:{2:F2}|MFE:{3:F2}|MAE:{4:F2}",
                    exitOrderName, tradePnlNt, tradePnlRealistic, activeMfeTicks, activeMaeTicks));

            ResetTradeState();
        }

        private double ComputeNetPnL(double entry, double exit, bool isLong)
        {
            double ticks = isLong ? (exit - entry) / TickSize : (entry - exit) / TickSize;
            double gross = ticks * MNQ_TICK_VALUE_USD;
            return gross - COMMISSION_RT_USD;
        }

        private void ResetTradeState()
        {
            activeTradeId = 0;
            activeSide = "";
            activeEntryTime = Core.Globals.MinDate;

            entryPrice = 0.0;
            entryPosition = MarketPosition.Flat;

            activeHighSinceEntry = 0.0;
            activeLowSinceEntry = 0.0;
            activeMfeTicks = 0.0;
            activeMaeTicks = 0.0;

            pendingEntry = false;
            protectionArmed = false;
        }

        private void UpdateMfeMae()
        {
            if (entryPosition == MarketPosition.Flat || entryPrice <= 0.0)
                return;

            activeHighSinceEntry = Math.Max(activeHighSinceEntry, High[0]);
            activeLowSinceEntry = Math.Min(activeLowSinceEntry, Low[0]);

            if (entryPosition == MarketPosition.Long)
            {
                activeMfeTicks = Math.Max(activeMfeTicks, (activeHighSinceEntry - entryPrice) / TickSize);
                activeMaeTicks = Math.Max(activeMaeTicks, (entryPrice - activeLowSinceEntry) / TickSize);
            }
            else if (entryPosition == MarketPosition.Short)
            {
                activeMfeTicks = Math.Max(activeMfeTicks, (entryPrice - activeLowSinceEntry) / TickSize);
                activeMaeTicks = Math.Max(activeMaeTicks, (activeHighSinceEntry - entryPrice) / TickSize);
            }
        }

        // ================================================================
        // Protection Layers
        // ================================================================

        private void CheckProtectionLayers(double tradePnL)
        {
            if (EnableChoppyFilter && !choppyDayDetected)
            {
                if (tradePnL < 0.0)
                    consecutiveLosses++;
                else
                    consecutiveLosses = 0;

                if (consecutiveLosses >= MaxConsecutiveLosses)
                {
                    choppyDayDetected = true;
                    WriteTelemetry("PROTECTION", "CHOPPY_FILTER_TRIGGERED");
                    if (PrintDiagnostics)
                        Print("[PROTECTION] Choppy day filter triggered: " + consecutiveLosses + " consecutive losses");
                }
            }

            if (EnableDailyMaxLoss && !dailyMaxLossHit)
            {
                if (sessionPnL <= -DailyMaxLoss)
                {
                    dailyMaxLossHit = true;
                    WriteTelemetry("PROTECTION", "DAILY_MAX_LOSS_TRIGGERED");
                    if (PrintDiagnostics)
                        Print("[PROTECTION] Daily max loss triggered: " + sessionPnL.ToString("0.00"));
                }
            }

            if (EnableEmergencyStop && !emergencyStopTriggered)
            {
                double ddFromPeak = peakCumulativePnL - cumulativePnL;
                if (ddFromPeak >= EmergencyStopDD)
                {
                    emergencyStopTriggered = true;
                    WriteTelemetry("PROTECTION", "EMERGENCY_STOP_TRIGGERED");
                    if (PrintDiagnostics)
                        Print("[PROTECTION] Emergency stop triggered: DD from peak $" + ddFromPeak.ToString("0.00"));
                }
            }
        }

        private void ResetSessionState()
        {
            orActive = false;
            orComplete = false;
            orStartTime = Core.Globals.MinDate;
            orEndTime = Core.Globals.MinDate;
            orHigh = double.MinValue;
            orLow = double.MaxValue;
            orWidth = 0.0;
            orVwap = 0.0;
            orVwapSum = 0.0;
            orVolumeSum = 0.0;
            currentOrbState = OrbState.PRE_OR;
            previousOrbState = OrbState.PRE_OR;
            currentOrbTransitionReason = "SESSION_RESET";
            lastOrbStateEvalTime = Core.Globals.MinDate;
            lastOrbTransitionTime = Core.Globals.MinDate;
            lastLongBreakoutTime = Core.Globals.MinDate;
            lastShortBreakoutTime = Core.Globals.MinDate;

            consecutiveLosses = 0;
            choppyDayDetected = false;
            dailyMaxLossHit = false;
            sessionPnL = 0.0;
            sessionRealisticPnL = 0.0;
            sessionTrades = 0;

            failedBreakoutDetected = false;
            manipulationReason = "";

            pendingEntry = false;

            // Reset L2 validation counters each session.
            l2Events = 0;
            l2BidEvents = 0;
            l2AskEvents = 0;
            l2TopBidEvents = 0;
            l2TopAskEvents = 0;
            l2InsertEvents = 0;
            l2UpdateEvents = 0;
            l2RemoveEvents = 0;
            lastL2HealthPrintTime = Core.Globals.MinDate;

            if (PrintDiagnostics)
                Print("[SESSION] Reset Flagship Hybrid session state.");
        }

        // ================================================================
        // Time Management
        // ================================================================

        private bool IsWithinRth(DateTime et)
        {
            int hhmmss = et.Hour * 10000 + et.Minute * 100 + et.Second;
            return hhmmss >= StartTimeEt && hhmmss <= EndTimeEt;
        }

        private void InitializeTimeZone()
        {
            try
            {
                easternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            catch
            {
                easternTimeZone = null;
            }
        }

        private DateTime ToEastern(DateTime t)
        {
            try
            {
                if (easternTimeZone == null)
                    return t;

                if (t.Kind == DateTimeKind.Utc)
                    return TimeZoneInfo.ConvertTimeFromUtc(t, easternTimeZone);

                return t;
            }
            catch
            {
                return t;
            }
        }

        private string GetSessionRegime(DateTime et)
        {
            int hhmmss = et.Hour * 10000 + et.Minute * 100 + et.Second;

            if (hhmmss < 94500) return "OPEN_15";
            if (hhmmss < 103000) return "POST_OPEN";
            if (hhmmss >= 113000 && hhmmss < 133000) return "LUNCH";
            if (hhmmss >= 153000) return "CLOSE_30";
            return "NORMAL";
        }

        // ================================================================
        // Telemetry
        // ================================================================

        private void OpenTelemetry()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "trace");

                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                telemetryPath = Path.Combine(dir,
                    TelemetryFilePrefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");

                telemetryWriter = new StreamWriter(telemetryPath, false);
                telemetryWriter.WriteLine(
                    "record_type,trade_id,time,side,regime,orb_state,orb_reason,orb_high,orb_low,orb_width,orb_vwap," +
                    "event_delta,event_imbalance,bid_wall,ask_wall,aggr_buy,aggr_sell," +
                    "manipulation,spread_ticks,entry_price,mfe_ticks,mae_ticks," +
                    "session_pnl,session_realistic_pnl,cumulative_pnl,consecutive_losses," +
                    "choppy,daily_loss_hit,emergency_stop,diagnostic");
                telemetryWriter.Flush();

                if (PrintDiagnostics)
                    Print("[TELEMETRY] " + telemetryPath);
            }
            catch (Exception ex)
            {
                Print("[TELEMETRY ERROR] " + ex.Message);
            }
        }

        private void WriteTelemetry(string recordType, string diagnostic)
        {
            if (!EnableTelemetry || telemetryWriter == null)
                return;

            try
            {
                string line = string.Join(",",
                    Csv(recordType),
                    activeTradeId.ToString(CultureInfo.InvariantCulture),
                    Csv(Time[0].ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                    Csv(activeSide),
                    Csv(currentRegime),
                    Csv(currentOrbState.ToString()),
                    Csv(currentOrbTransitionReason),
                    orHigh.ToString("0.####", CultureInfo.InvariantCulture),
                    orLow.ToString("0.####", CultureInfo.InvariantCulture),
                    orWidth.ToString("0.####", CultureInfo.InvariantCulture),
                    orVwap.ToString("0.####", CultureInfo.InvariantCulture),
                    currentEventDelta.ToString("0.####", CultureInfo.InvariantCulture),
                    currentEventImbalance.ToString("0.####", CultureInfo.InvariantCulture),
                    currentBidWallScore.ToString("0.####", CultureInfo.InvariantCulture),
                    currentAskWallScore.ToString("0.####", CultureInfo.InvariantCulture),
                    currentAggressorBuyVol.ToString("0.####", CultureInfo.InvariantCulture),
                    currentAggressorSellVol.ToString("0.####", CultureInfo.InvariantCulture),
                    Csv(failedBreakoutDetected ? manipulationReason : "NONE"),
                    currentSpreadTicks.ToString("0.####", CultureInfo.InvariantCulture),
                    entryPrice.ToString("0.####", CultureInfo.InvariantCulture),
                    activeMfeTicks.ToString("0.####", CultureInfo.InvariantCulture),
                    activeMaeTicks.ToString("0.####", CultureInfo.InvariantCulture),
                    sessionPnL.ToString("0.####", CultureInfo.InvariantCulture),
                    sessionRealisticPnL.ToString("0.####", CultureInfo.InvariantCulture),
                    cumulativePnL.ToString("0.####", CultureInfo.InvariantCulture),
                    consecutiveLosses.ToString(CultureInfo.InvariantCulture),
                    choppyDayDetected ? "1" : "0",
                    dailyMaxLossHit ? "1" : "0",
                    emergencyStopTriggered ? "1" : "0",
                    Csv(diagnostic)
                );

                telemetryWriter.WriteLine(line);
                telemetryWriter.Flush();
            }
            catch { }
        }

        private string Csv(string s)
        {
            if (s == null)
                s = "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private void CloseTelemetry()
        {
            try
            {
                if (telemetryWriter != null)
                {
                    telemetryWriter.Flush();
                    telemetryWriter.Close();
                    telemetryWriter.Dispose();
                    telemetryWriter = null;
                }
            }
            catch { }
        }

        private void PrintSummary()
        {
            if (!PrintDiagnostics)
                return;

            Print("╔════════════════════════════════════════════════════════════════╗");
            Print("║  CG MNQ FLAGSHIP HYBRID v1.2 L2 VALIDATION - SESSION SUMMARY  ║");
            Print("╠════════════════════════════════════════════════════════════════╣");
            Print(string.Format("  Entries: {0}", entries));
            Print(string.Format("  Exits: {0}", exits));
            Print(string.Format("  Session trades: {0}", sessionTrades));
            Print(string.Format("  Session PnL (NT-style): ${0:F2}", sessionPnL));
            Print(string.Format("  Session PnL (Realistic): ${0:F2}", sessionRealisticPnL));
            Print(string.Format("  Cumulative PnL: ${0:F2}", cumulativePnL));
            Print("╟────────────────────────────────────────────────────────────────╢");
            Print("║  L2 REPLAY HEALTH                                              ║");
            Print("╟────────────────────────────────────────────────────────────────╢");
            Print(string.Format("  L2 all events: {0}", l2Events));
            Print(string.Format("  L2 bid events: {0}", l2BidEvents));
            Print(string.Format("  L2 ask events: {0}", l2AskEvents));
            Print(string.Format("  L2 top bid events: {0}", l2TopBidEvents));
            Print(string.Format("  L2 top ask events: {0}", l2TopAskEvents));
            Print(string.Format("  L2 add/update/remove: {0}/{1}/{2}", l2InsertEvents, l2UpdateEvents, l2RemoveEvents));
            Print(string.Format("  Last bidWall/askWall: {0}/{1}", bidWallSize, askWallSize));
            Print(string.Format("  Last bestBid/bestAsk: {0:F2}/{1:F2}", lastBestBid, lastBestAsk));
            Print("╟────────────────────────────────────────────────────────────────╢");
            Print("║  LAYER COUNTS                                                  ║");
            Print("╟────────────────────────────────────────────────────────────────╢");
            Print(string.Format("  ORB State Transitions: {0}", orbStateTransitions));
            Print(string.Format("  T2 Long/Short signals: {0}/{1}", t2LongSignals, t2ShortSignals));
            Print(string.Format("  T3 Confirmed Long/Short: {0}/{1}", t3ConfirmedLongs, t3ConfirmedShorts));
            Print(string.Format("  Padder blocks: {0}", padderBlocks));
            Print("╟────────────────────────────────────────────────────────────────╢");
            Print("║  REJECTION ANALYSIS                                            ║");
            Print("╟────────────────────────────────────────────────────────────────╢");
            Print(string.Format("  RTH rejects: {0}", rthRejects));
            Print(string.Format("  OR not complete: {0}", orNotCompleteRejects));
            Print(string.Format("  ORB bias rejects: {0}", orbBiasRejects));
            Print(string.Format("  Min range rejects: {0}", minRangeRejects));
            Print(string.Format("  Spread rejects: {0}", spreadRejects));
            Print(string.Format("  Event delta rejects: {0}", eventDeltaRejects));
            Print(string.Format("  Imbalance rejects: {0}", imbalanceRejects));
            Print(string.Format("  Wall score rejects: {0}", wallScoreRejects));
            Print(string.Format("  Volume rejects: {0}", volumeRejects));
            Print(string.Format("  Manipulation rejects: {0}", manipulationRejects));
            Print(string.Format("  Position rejects: {0}", positionRejects));
            Print(string.Format("  Pending rejects: {0}", pendingRejects));
            Print(string.Format("  Choppy rejects: {0}", choppyRejects));
            Print(string.Format("  Daily loss rejects: {0}", dailyLossRejects));
            Print(string.Format("  Emergency rejects: {0}", emergencyRejects));
            Print("╚════════════════════════════════════════════════════════════════╝");
        }

        // ================================================================
        // Parameters
        // ================================================================

        [NinjaScriptProperty]
        [Range(1, 1)]
        [Display(Name = "Quantity", Order = 1, GroupName = "01. Execution")]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "StopTicks", Order = 2, GroupName = "01. Execution")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "TargetTicks", Order = 3, GroupName = "01. Execution")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(10, 3600)]
        [Display(Name = "MaxHoldSeconds", Order = 4, GroupName = "01. Execution")]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(5, 60)]
        [Display(Name = "Opening Range Minutes", Order = 1, GroupName = "02. Layer 1: ORB")]
        public int OpeningRangeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 20.0)]
        [Display(Name = "Min Range Width", Order = 2, GroupName = "02. Layer 1: ORB")]
        public double MinRangeWidth { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "ORB Breakout Buffer", Order = 3, GroupName = "02. Layer 1: ORB")]
        public double OrbBreakoutBuffer { get; set; }

        [NinjaScriptProperty]
        [Range(0, 60)]
        [Display(Name = "ORB State Eval Seconds", Order = 4, GroupName = "02. Layer 1: ORB")]
        public int OrbStateEvalSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Block CHOP State", Order = 5, GroupName = "02. Layer 1: ORB")]
        public bool BlockChopState { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Fade After Failure", Order = 6, GroupName = "02. Layer 1: ORB")]
        public bool AllowFadeAfterFailure { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Lunch Suppression", Order = 7, GroupName = "02. Layer 1: ORB")]
        public bool EnableLunchSuppression { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Min Event Delta", Order = 1, GroupName = "03. Layer 2: T2")]
        public double MinEventDelta { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1.0)]
        [Display(Name = "Min Event Imbalance", Order = 2, GroupName = "03. Layer 2: T2")]
        public double MinEventImbalance { get; set; }

        [NinjaScriptProperty]
        [Range(10, 1000)]
        [Display(Name = "Event Lookback Bars", Order = 3, GroupName = "03. Layer 2: T2")]
        public int EventLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(10, 10000)]
        [Display(Name = "Min Wall Size", Order = 1, GroupName = "04. Layer 3: T3 Wall")]
        public long MinWallSize { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Min Aggressor Volume", Order = 2, GroupName = "04. Layer 3: T3 Wall")]
        public double MinAggressorVolume { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Manipulation Filter", Order = 1, GroupName = "05. Layer 4: Padder")]
        public bool EnableManipulationFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Failed Breakout Bars", Order = 2, GroupName = "05. Layer 4: Padder")]
        public int FailedBreakoutBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Start Time ET", Order = 1, GroupName = "06. Session")]
        public int StartTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "End Time ET", Order = 2, GroupName = "06. Session")]
        public int EndTimeEt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Choppy Filter", Order = 1, GroupName = "07. Protection")]
        public bool EnableChoppyFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Consecutive Losses", Order = 2, GroupName = "07. Protection")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Daily Max Loss", Order = 3, GroupName = "07. Protection")]
        public bool EnableDailyMaxLoss { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Daily Max Loss", Order = 4, GroupName = "07. Protection")]
        public double DailyMaxLoss { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Emergency Stop", Order = 5, GroupName = "07. Protection")]
        public bool EnableEmergencyStop { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Emergency Stop DD", Order = 6, GroupName = "07. Protection")]
        public double EmergencyStopDD { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Max Spread Ticks", Order = 1, GroupName = "08. Filters")]
        public double MaxSpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Slippage Ticks", Order = 2, GroupName = "08. Filters")]
        public int SlippageTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Telemetry", Order = 1, GroupName = "09. Telemetry")]
        public bool EnableTelemetry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Telemetry File Prefix", Order = 2, GroupName = "09. Telemetry")]
        public string TelemetryFilePrefix { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Print Diagnostics", Order = 3, GroupName = "09. Telemetry")]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable L2 Verbose Print", Order = 4, GroupName = "09. Telemetry")]
        public bool EnableL2VerbosePrint { get; set; }
    }
}
