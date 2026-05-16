#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

// =================================================================================================
// CG_OrderFlow_Aggression_v2_13_RTH_FULL_DAY_DISCOVERY.cs
// Generated: 2026-05-15 20:35:00 ET
//
// Purpose
// -------
// Execution-aggression MNQ strategy upgraded from v2.1 with the requested "implement all" changes:
//   1. Persistence engine: requires repeated aligned 100ms buckets / weighted pressure score.
//   2. Directional state machine: TREND_UP / TREND_DOWN / RANGE / REVERSAL / EXHAUSTION.
//   3. Post-sweep delay: avoids immediate chase into sweep/climax bars.
//   4. Price acceptance filter: requires price to move/hold beyond trigger reference.
//   5. Hard frequency reduction: max trades/day, cooldowns, minimum time between entries.
//   6. Structure: internal 1-minute series, OR, VWAP, local swing/BOS context.
//   7. Absorption detection: optional reversal veto/confirm logic when aggression fails to move price.
//
// Deployment notes
// ----------------
// Attach to an MNQ chart. The script internally adds a 1-minute series for OR/VWAP/structure.
// Keep one MNQ contract only. Managed protective exits are submitted immediately after entry fill.
// Test in Playback first. Confirm OnMarketData/OnMarketDepth is active for your data source.
//
// Important design stance
// -----------------------
// v2.13 keeps the context-first design, enforces RTH-only execution, and removes all safety/panic trading stoppages for full-day discovery.
// Default behavior: pre/post-RTH data may still be inspected and used for diagnostics, but actual order entry is enabled only inside the configured RTH window. No daily loss, profit lock, max trade, consecutive-loss, or cooldown stoppages are enabled by default.
// =================================================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_OrderFlow_Aggression_v2_13_RTH_FULL_DAY_DISCOVERY : Strategy
    {
        #region Enums
        private enum DirectionSignal { None = 0, Long = 1, Short = -1 }
        private enum AuctionState { Unknown, TrendUp, TrendDown, Range, ReversalUp, ReversalDown, ExhaustionUp, ExhaustionDown }
        private enum ExitKind { None, Target, Stop, Timeout, Session, Manual }
        #endregion

        #region Internal state
        private DateTime bucket100msStart = DateTime.MinValue;
        private DateTime bucket1sStart = DateTime.MinValue;
        private DateTime bucket5sStart = DateTime.MinValue;
        private DateTime currentMarketTime = DateTime.MinValue;
        private DateTime lastBidAskUpdateTime = DateTime.MinValue;
        private DateTime lastTradeExitTime = DateTime.MinValue;
        private DateTime lastEntryTime = DateTime.MinValue;
        private DateTime lastSweepTime = DateTime.MinValue;
        private DateTime pendingSignalStartTime = DateTime.MinValue;

        private double currentBestBid = 0.0;
        private double currentBestAsk = 0.0;
        private double lastTradePrice = 0.0;
        private double priorTradePrice = 0.0;

        private long aggBuyVol100ms = 0;
        private long aggSellVol100ms = 0;
        private long aggBuyVol1s = 0;
        private long aggSellVol1s = 0;
        private long aggBuyVol5s = 0;
        private long aggSellVol5s = 0;

        private readonly Queue<int> persistenceSigns = new Queue<int>();
        private readonly Queue<double> persistenceScores = new Queue<double>();
        private int consecutiveLongBuckets = 0;
        private int consecutiveShortBuckets = 0;
        private double weightedPersistenceScore = 0.0;

        private DirectionSignal pendingDirection = DirectionSignal.None;
        private double pendingTriggerPrice = 0.0;
        private double pendingReferencePrice = 0.0;
        private int pendingAcceptanceBuckets = 0;

        private double orHigh = 0.0;
        private double orLow = 0.0;
        private bool orCalculated = false;
        private DateTime orDate = DateTime.MinValue;

        private double vwapCumPV = 0.0;
        private double vwapCumVol = 0.0;
        private double sessionVwap = 0.0;

        private readonly Queue<double> recentMinuteHighs = new Queue<double>();
        private readonly Queue<double> recentMinuteLows = new Queue<double>();
        private readonly Queue<double> recentMinuteCloses = new Queue<double>();
        private readonly Queue<double> recentFiveMinuteCloses = new Queue<double>();
        private double localSwingHigh = 0.0;
        private double localSwingLow = 0.0;
        private DirectionSignal htfBias = DirectionSignal.None;
        private double htfBiasScoreTicks = 0.0;
        private DateTime lastFailedLongTime = DateTime.MinValue;
        private DateTime lastFailedShortTime = DateTime.MinValue;
        private double lastFailedLongPrice = 0.0;
        private double lastFailedShortPrice = 0.0;

        private AuctionState auctionState = AuctionState.Unknown;
        private AuctionState previousAuctionState = AuctionState.Unknown;

        private double entryPrice = 0.0;
        private DateTime entryTime = DateTime.MinValue;
        private MarketPosition entryDirection = MarketPosition.Flat;
        private int entryQuantity = 0;
        private int tradesCountToday = 0;
        private int rejectedSignalCount = 0;

        private double dailyPnL = 0.0;
        private double dailyPeakPnL = 0.0;
        private int consecutiveLosses = 0;
        private bool dailyLimitHit = false;
        private DateTime lastTradeDateEt = DateTime.MinValue;
        private ExitKind lastExitKind = ExitKind.None;

        private Order entryOrder = null;
        private Order stopOrder = null;
        private Order targetOrder = null;

        private int marketDataEvents = 0;
        private int depthEvents = 0;
        private int processedBuckets = 0;
        private bool fatalSeriesConfiguration = false;
        private string fatalSeriesMessage = string.Empty;
        private bool rthExecutionGateWasOpen = false;
        private DateTime lastRthGateDateEt = DateTime.MinValue;
        private DateTime lastRthFlattenDateEt = DateTime.MinValue;

        private struct ResponseBucket
        {
            public DateTime Time;
            public double Price;
            public long Delta;
            public double Imbalance;
            public ResponseBucket(DateTime time, double price, long delta, double imbalance)
            {
                Time = time;
                Price = price;
                Delta = delta;
                Imbalance = imbalance;
            }
        }

        private readonly Queue<ResponseBucket> responseBuckets = new Queue<ResponseBucket>();
        private DateTime lastStage2SweepTime = DateTime.MinValue;
        private DateTime lastStage2RejectionTime = DateTime.MinValue;
        private DirectionSignal lastRejectionDirection = DirectionSignal.None;
        private int acceptedLongBuckets = 0;
        private int acceptedShortBuckets = 0;
        private int rangeAbsorptionLongBuckets = 0;
        private int rangeAbsorptionShortBuckets = 0;
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "CG OrderFlow Aggression v2.13 - RTH-only execution, full-day destructive discovery with no panic stoppages";
                Name = "CG_OrderFlow_Aggression_v2_13_RTH_FULL_DAY_DISCOVERY";

                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 3;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // Signal / persistence
                // v2.3 UNBLOCKED: relaxed defaults because v2.2 proved over-gated and produced zero trades.
                MinAggressionDelta100ms = 40;
                MinAggressionImbalance100ms = 0.55;
                MinAggressionDelta1s = 50;
                MinAggressionImbalance1s = 0.05;
                MinAggressionDelta5s = 100;
                MinAggressionImbalance5s = 0.05;
                ConsecutiveBucketsRequired = 2;
                PersistenceWindowBuckets = 10;
                MinPersistenceScore = 1.15;
                Require1sConfirmation = true;
                Require5sNonOpposition = false;

                // Auction / structure
                EnableOpeningRangeFilter = true;
                EnableVWAPFilter = false;
                EnableStructureFilter = true;
                EnableAuctionStateMachine = true;
                SwingLookbackMinutes = 6;
                MinDistanceFromVWAPTicks = 0;
                TrendSlopeTicks = 6;
                ORBreakoutBufferTicks = 4;
                AllowReversalTrades = false;
                AllowRangeDiscoveryTrades = true;
                MinRangeDiscoveryPersistenceScore = 3.25;

                // Sweep / acceptance / absorption
                EnablePostSweepDelay = true;
                SweepDeltaThreshold = 250;
                SweepImbalanceThreshold = 0.85;
                PostSweepDelayMs = 500;
                EnablePriceAcceptance = false; // legacy gate; Stage 2 response engine supersedes this
                AcceptanceTicks = 6;
                AcceptanceBucketsRequired = 2;
                EnableAbsorptionFilter = false; // legacy veto; Stage 2 response engine supersedes this
                AbsorptionPriceMoveMaxTicks = 2;
                AbsorptionAggressionDelta = 200;

                // Stage 2 response engine
                EnableStage2ResponseEngine = true;
                Stage2ResponseWindowMs = 3000;
                Stage2AcceptanceTicks = 6;
                Stage2AcceptanceBucketsRequired = 2;
                Stage2MaxAdverseTicks = 3;
                Stage2RangeRequiresAbsorption = false;
                Stage2AbsorptionLookbackMs = 5000;
                Stage2AbsorptionDelta = 250;
                Stage2AbsorptionMaxProgressTicks = 6;
                Stage2RejectionTicks = 5;
                Stage2RejectionCooldownMs = 750;
                Stage2SweepCooldownMs = 750;
                EnableStage2DirectionalAgreement = true;
                Stage2MinDirectionalPersistence = 2.75;
                Stage2MinAcceptanceDominance = 2;
                Stage2MinContinuationBuckets = 2;
                Stage2RangeStrongAcceptanceBuckets = 12;

                // Risk
                TargetTicks = 40;
                StopTicks = 20;
                TimeoutMinutes = 10;
                MaxSpreadTicks = 2;
                MaxQuoteAgeMs = 750;
                Quantity = 1;

                // Frequency control
                EnableCooldown = false;
                CooldownSeconds = 0;
                PostStopCooldownSeconds = 0;
                MinimumSecondsBetweenEntries = 0;
                MaxTradesPerDay = 0;

                // Full-day destructive discovery branch: no panic/daily/consecutive/profit-lock/max-trade/cooldown shutdowns.
                // Let the strategy continue through all RTH loss streaks so replay reveals full-session behavior.
                EnableDailyLimits = false;
                MaxDailyLoss = 999999;
                MaxConsecutiveLosses = 999;
                ProfitLockPeak = 999999;
                ProfitLockDrawdown = 999999;

                // RTH defaults. Set StartHour=8/StartMinute=45 if deliberately testing premarket.
                RTHStartHour = 9;
                RTHStartMinute = 30;
                RTHEndHour = 16;
                RTHEndMinute = 0;
                EnforceRTHExecutionOnly = true;
                FlattenAtRTHEnd = true;

                PrintDiagnostics = true;
                DiagnosticEveryBuckets = 100;
                            // Context-first suppression defaults
                RequireORCompleteBeforeTrading = true;
                EnableHTFBiasFilter = true;
                HTFBiasSlopeTicks = 18;
                CounterBiasOverridePersistence = 5.25;
                EnableRangeCenterSuppression = true;
                RangeCenterBandPct = 0.40;
                RangePowerOverridePersistence = 4.75;
                RangeEdgeBandPct = 0.22;
                EnableFailedDirectionCooldown = true;
                FailedDirectionCooldownSeconds = 360;
                FailedDirectionZoneTicks = 16;
                FailedDirectionOverridePersistence = 5.50;
            }
            else if (State == State.Configure)
            {
                // Headless enforced internal structure series. Primary must be MNQ 1 Tick.
                // NinjaTrader cannot create the primary series from inside a strategy,
                // so we validate it in DataLoaded and refuse to trade if it is wrong.
                AddDataSeries(BarsPeriodType.Minute, 1);
                AddDataSeries(BarsPeriodType.Minute, 5);
            }
            else if (State == State.DataLoaded)
            {
                ValidateHeadlessSeriesConfiguration();
                ResetSessionState(true);
            }
        }
        #endregion

        private void ValidateHeadlessSeriesConfiguration()
        {
            fatalSeriesConfiguration = false;
            fatalSeriesMessage = string.Empty;

            bool primaryIsOneTick = false;
            bool hasOneMinute = false;
            bool hasFiveMinute = false;

            try
            {
                primaryIsOneTick = BarsArray != null
                    && BarsArray.Length >= 1
                    && BarsArray[0] != null
                    && BarsArray[0].BarsPeriod.BarsPeriodType == BarsPeriodType.Tick
                    && BarsArray[0].BarsPeriod.Value == 1;

                hasOneMinute = BarsArray != null
                    && BarsArray.Length >= 2
                    && BarsArray[1] != null
                    && BarsArray[1].BarsPeriod.BarsPeriodType == BarsPeriodType.Minute
                    && BarsArray[1].BarsPeriod.Value == 1;

                hasFiveMinute = BarsArray != null
                    && BarsArray.Length >= 3
                    && BarsArray[2] != null
                    && BarsArray[2].BarsPeriod.BarsPeriodType == BarsPeriodType.Minute
                    && BarsArray[2].BarsPeriod.Value == 5;
            }
            catch (Exception ex)
            {
                fatalSeriesConfiguration = true;
                fatalSeriesMessage = "SERIES VALIDATION EXCEPTION: " + ex.Message;
            }

            if (!primaryIsOneTick || !hasOneMinute || !hasFiveMinute)
            {
                fatalSeriesConfiguration = true;
                fatalSeriesMessage = "FATAL SERIES CONFIG: attach to MNQ 1 Tick primary. Strategy internally requires BIP0=1 Tick, BIP1=1 Minute, BIP2=5 Minute. "
                    + "Detected: " + DescribeSeriesLayout();
            }

            if (fatalSeriesConfiguration)
                Print("*** " + fatalSeriesMessage + " ***");
            else
                Print("HEADLESS SERIES OK: " + DescribeSeriesLayout());
        }

        private string DescribeSeriesLayout()
        {
            if (BarsArray == null)
                return "BarsArray=null";

            List<string> parts = new List<string>();
            for (int i = 0; i < BarsArray.Length; i++)
            {
                try
                {
                    if (BarsArray[i] == null)
                    {
                        parts.Add("BIP" + i + "=null");
                        continue;
                    }

                    parts.Add("BIP" + i + "="
                        + BarsArray[i].BarsPeriod.Value.ToString()
                        + " "
                        + BarsArray[i].BarsPeriod.BarsPeriodType.ToString());
                }
                catch
                {
                    parts.Add("BIP" + i + "=unreadable");
                }
            }
            return string.Join(", ", parts);
        }

        #region Bar processing
        protected override void OnBarUpdate()
        {
            if (fatalSeriesConfiguration)
                return;

            if (CurrentBars[0] < BarsRequiredToTrade)
                return;

            if (BarsInProgress >= 0 && CurrentBars[BarsInProgress] < 0)
                return;

            DateTime eventTime = Times[BarsInProgress][0];
            currentMarketTime = eventTime;
            if (BarsInProgress == 0)
                HandleRTHExecutionBoundary(eventTime);

            DateTime etDate = ToEastern(eventTime).Date;
            if (lastTradeDateEt == DateTime.MinValue || etDate != lastTradeDateEt)
            {
                lastTradeDateEt = etDate;
                ResetSessionState(false);
                Print("========== " + etDate.ToString("yyyy-MM-dd") + " ET - NEW TRADING DAY ==========");
            }

            // Internal 1-minute structure series.
            if (BarsInProgress == 1)
            {
                UpdateOneMinuteStructure();
                return;
            }

            // Internal 5-minute bias series. Currently used as a headless enforced data rail;
            // reserved for stricter higher-timeframe bias in later versions.
            if (BarsInProgress == 2)
            {
                UpdateFiveMinuteBias();
                return;
            }

            if (BarsInProgress != 0)
                return;

            if (Position.MarketPosition != MarketPosition.Flat)
                CheckTimeout();
        }

        private void UpdateOneMinuteStructure()
        {
            if (CurrentBars[1] < 2)
                return;

            DateTime tEt = ToEastern(Times[1][0]);
            double h = Highs[1][0];
            double l = Lows[1][0];
            double c = Closes[1][0];
            double typical = (Highs[1][0] + Lows[1][0] + Closes[1][0]) / 3.0;
            double vol = Math.Max(1.0, Volumes[1][0]);

            // Reset VWAP at ET date boundary handled by ResetSessionState.
            vwapCumPV += typical * vol;
            vwapCumVol += vol;
            sessionVwap = vwapCumVol > 0 ? vwapCumPV / vwapCumVol : c;

            UpdateRolling(recentMinuteHighs, h, SwingLookbackMinutes);
            UpdateRolling(recentMinuteLows, l, SwingLookbackMinutes);
            UpdateRolling(recentMinuteCloses, c, SwingLookbackMinutes);

            localSwingHigh = MaxQueue(recentMinuteHighs);
            localSwingLow = MinQueue(recentMinuteLows);

            // Opening range 09:30-09:45 ET by default, based on internal 1-minute bars.
            if (EnableOpeningRangeFilter)
            {
                DateTime orStart = new DateTime(tEt.Year, tEt.Month, tEt.Day, 9, 30, 0);
                DateTime orEnd = new DateTime(tEt.Year, tEt.Month, tEt.Day, 9, 45, 0);

                if (tEt >= orStart && tEt < orEnd)
                {
                    if (orDate.Date != tEt.Date)
                    {
                        orHigh = h;
                        orLow = l;
                        orDate = tEt.Date;
                        orCalculated = false;
                    }
                    else
                    {
                        orHigh = orHigh == 0.0 ? h : Math.Max(orHigh, h);
                        orLow = orLow == 0.0 ? l : Math.Min(orLow, l);
                    }
                }

                if (!orCalculated && tEt >= orEnd && orHigh > 0.0 && orLow > 0.0)
                {
                    orCalculated = true;
                    Print(tEt.ToString("HH:mm:ss.fff") + " | OR CALCULATED INTERNAL 1M: High=" + orHigh.ToString("F2") + " Low=" + orLow.ToString("F2"));
                }
            }

            UpdateAuctionState(c);
        }

        private void UpdateFiveMinuteBias()
        {
            if (CurrentBars[2] < 2)
                return;

            double c = Closes[2][0];
            UpdateRolling(recentFiveMinuteCloses, c, Math.Max(3, SwingLookbackMinutes));

            if (recentFiveMinuteCloses.Count < 3)
            {
                htfBias = DirectionSignal.None;
                htfBiasScoreTicks = 0.0;
                return;
            }

            double first = FirstQueue(recentFiveMinuteCloses);
            htfBiasScoreTicks = (c - first) / TickSize;
            double threshold = Math.Max(4.0, HTFBiasSlopeTicks);

            if (htfBiasScoreTicks >= threshold)
                htfBias = DirectionSignal.Long;
            else if (htfBiasScoreTicks <= -threshold)
                htfBias = DirectionSignal.Short;
            else
                htfBias = DirectionSignal.None;
        }
        #endregion

        #region Market data processing
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (fatalSeriesConfiguration || State != State.Realtime || CurrentBars[0] < BarsRequiredToTrade)
                return;

            currentMarketTime = e.Time;
            HandleRTHExecutionBoundary(e.Time);
            marketDataEvents++;

            if (e.MarketDataType == MarketDataType.Bid)
            {
                currentBestBid = e.Price;
                lastBidAskUpdateTime = e.Time;
                return;
            }
            if (e.MarketDataType == MarketDataType.Ask)
            {
                currentBestAsk = e.Price;
                lastBidAskUpdateTime = e.Time;
                return;
            }
            if (e.MarketDataType != MarketDataType.Last)
                return;

            long volume = Math.Max(1L, e.Volume);
            priorTradePrice = lastTradePrice;

            if (bucket100msStart == DateTime.MinValue)
            {
                bucket100msStart = e.Time;
                bucket1sStart = e.Time;
                bucket5sStart = e.Time;
            }

            bool isAggressiveBuy = false;
            bool isAggressiveSell = false;

            // Classify actual executions against current quotes.
            if (currentBestAsk > 0.0 && Math.Abs(e.Price - currentBestAsk) <= TickSize * 0.5)
                isAggressiveBuy = true;
            else if (currentBestBid > 0.0 && Math.Abs(e.Price - currentBestBid) <= TickSize * 0.5)
                isAggressiveSell = true;
            else if (priorTradePrice > 0.0)
            {
                if (e.Price > priorTradePrice)
                    isAggressiveBuy = true;
                else if (e.Price < priorTradePrice)
                    isAggressiveSell = true;
            }

            lastTradePrice = e.Price;

            if (isAggressiveBuy)
            {
                aggBuyVol100ms += volume;
                aggBuyVol1s += volume;
                aggBuyVol5s += volume;
            }
            else if (isAggressiveSell)
            {
                aggSellVol100ms += volume;
                aggSellVol1s += volume;
                aggSellVol5s += volume;
            }

            if ((e.Time - bucket100msStart).TotalMilliseconds >= 100.0)
            {
                ProcessBucket100ms(e.Price);
                bucket100msStart = e.Time;
                Reset100msBucket();
            }

            if ((e.Time - bucket1sStart).TotalSeconds >= 1.0)
            {
                bucket1sStart = e.Time;
                Reset1sBucket();
            }

            if ((e.Time - bucket5sStart).TotalSeconds >= 5.0)
            {
                bucket5sStart = e.Time;
                Reset5sBucket();
            }
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (fatalSeriesConfiguration || State != State.Realtime || CurrentBars[0] < BarsRequiredToTrade)
                return;

            depthEvents++;
            if (e.Time > currentMarketTime)
                currentMarketTime = e.Time;

            if (e.Position == 0 && e.MarketDataType == MarketDataType.Bid)
            {
                currentBestBid = e.Price;
                lastBidAskUpdateTime = e.Time;
            }
            else if (e.Position == 0 && e.MarketDataType == MarketDataType.Ask)
            {
                currentBestAsk = e.Price;
                lastBidAskUpdateTime = e.Time;
            }
        }
        #endregion

        #region Signal processing
        private void ProcessBucket100ms(double lastPrice)
        {
            processedBuckets++;
            long delta100 = aggBuyVol100ms - aggSellVol100ms;
            long total100 = aggBuyVol100ms + aggSellVol100ms;
            if (total100 <= 0)
                return;

            double imb100 = (double)delta100 / (double)total100;
            UpdateResponseHistory(lastPrice, delta100, imb100);
            DirectionSignal raw = DirectionSignal.None;
            if (delta100 >= MinAggressionDelta100ms && imb100 >= MinAggressionImbalance100ms)
                raw = DirectionSignal.Long;
            else if (delta100 <= -MinAggressionDelta100ms && imb100 <= -MinAggressionImbalance100ms)
                raw = DirectionSignal.Short;

            bool sweepDetected = Math.Abs(delta100) >= SweepDeltaThreshold && Math.Abs(imb100) >= SweepImbalanceThreshold;
            if (sweepDetected)
            {
                lastSweepTime = currentMarketTime;
                lastStage2SweepTime = currentMarketTime;
            }
            UpdateStage2ResponseState(lastPrice, delta100, imb100);

            UpdatePersistence(raw, delta100, imb100);

            if (EnablePriceAcceptance)
                UpdatePendingAcceptance(lastPrice);

            DirectionSignal candidate = BuildCandidateSignal(raw, delta100, imb100, lastPrice);

            if (candidate != DirectionSignal.None)
            {
                if (CanTakeSignal(candidate, lastPrice, delta100, imb100))
                    EnterPosition(candidate, lastPrice);
                else
                    rejectedSignalCount++;
            }

            if (PrintDiagnostics && DiagnosticEveryBuckets > 0 && processedBuckets % DiagnosticEveryBuckets == 0)
            {
                Print(string.Format("{0} | DIAG buckets={1} md={2} depth={3} state={4} pers={5:F2} cL={6} cS={7} accL={8} accS={9} absL={10} absS={11} trades={12} rejectEvents={13} pnl={14:F2}",
                    currentMarketTime.ToString("HH:mm:ss.fff"), processedBuckets, marketDataEvents, depthEvents, auctionState,
                    weightedPersistenceScore, consecutiveLongBuckets, consecutiveShortBuckets, acceptedLongBuckets, acceptedShortBuckets,
                    rangeAbsorptionLongBuckets, rangeAbsorptionShortBuckets, tradesCountToday, rejectedSignalCount, dailyPnL));
            }
        }

        private DirectionSignal BuildCandidateSignal(DirectionSignal raw, long delta100, double imb100, double lastPrice)
        {
            if (raw == DirectionSignal.None)
                return DirectionSignal.None;

            bool persistenceOk = false;
            if (raw == DirectionSignal.Long)
                persistenceOk = consecutiveLongBuckets >= ConsecutiveBucketsRequired && weightedPersistenceScore >= MinPersistenceScore;
            else if (raw == DirectionSignal.Short)
                persistenceOk = consecutiveShortBuckets >= ConsecutiveBucketsRequired && weightedPersistenceScore <= -MinPersistenceScore;

            if (!persistenceOk)
                return DirectionSignal.None;

            if (Require1sConfirmation)
            {
                long d1 = aggBuyVol1s - aggSellVol1s;
                long t1 = aggBuyVol1s + aggSellVol1s;
                double i1 = t1 > 0 ? (double)d1 / (double)t1 : 0.0;
                if (raw == DirectionSignal.Long && !(d1 >= MinAggressionDelta1s && i1 >= MinAggressionImbalance1s))
                    return DirectionSignal.None;
                if (raw == DirectionSignal.Short && !(d1 <= -MinAggressionDelta1s && i1 <= -MinAggressionImbalance1s))
                    return DirectionSignal.None;
            }

            if (Require5sNonOpposition)
            {
                long d5 = aggBuyVol5s - aggSellVol5s;
                long t5 = aggBuyVol5s + aggSellVol5s;
                double i5 = t5 > 0 ? (double)d5 / (double)t5 : 0.0;
                if (raw == DirectionSignal.Long && (d5 < -MinAggressionDelta5s || i5 < -MinAggressionImbalance5s))
                    return DirectionSignal.None;
                if (raw == DirectionSignal.Short && (d5 > MinAggressionDelta5s || i5 > MinAggressionImbalance5s))
                    return DirectionSignal.None;
            }

            // Start/continue price acceptance qualification.
            if (EnablePriceAcceptance)
            {
                if (pendingDirection != raw)
                {
                    pendingDirection = raw;
                    pendingSignalStartTime = currentMarketTime;
                    pendingReferencePrice = lastPrice;
                    pendingTriggerPrice = raw == DirectionSignal.Long ? lastPrice + AcceptanceTicks * TickSize : lastPrice - AcceptanceTicks * TickSize;
                    pendingAcceptanceBuckets = 0;
                    return DirectionSignal.None;
                }

                if (pendingAcceptanceBuckets < AcceptanceBucketsRequired)
                    return DirectionSignal.None;
            }

            return raw;
        }

        private bool CanTakeSignal(DirectionSignal signal, double lastPrice, long delta100, double imb100)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return false;

            if (Quantity != 1)
                return false;

            if (EnforceRTHExecutionOnly && !IsRTH(currentMarketTime))
                return false;

            if (EnableDailyLimits && dailyLimitHit)
                return false;

            if (MaxTradesPerDay > 0 && tradesCountToday >= MaxTradesPerDay)
                return false;

            if (EnableOpeningRangeFilter && !orCalculated)
                return false;

            if (!QuoteIsFreshAndSane())
                return false;

            if (!ContextFirstAllows(signal, lastPrice))
                return false;

            if (EnableCooldown)
            {
                if (lastTradeExitTime != DateTime.MinValue)
                {
                    double required = lastExitKind == ExitKind.Stop ? PostStopCooldownSeconds : CooldownSeconds;
                    if ((currentMarketTime - lastTradeExitTime).TotalSeconds < required)
                        return false;
                }

                if (lastEntryTime != DateTime.MinValue && (currentMarketTime - lastEntryTime).TotalSeconds < MinimumSecondsBetweenEntries)
                    return false;
            }

            if (EnablePostSweepDelay && lastSweepTime != DateTime.MinValue)
            {
                if ((currentMarketTime - lastSweepTime).TotalMilliseconds < PostSweepDelayMs)
                    return false;
            }

            if (EnableAuctionStateMachine && !AuctionAllows(signal))
                return false;

            if (EnableStage2ResponseEngine && !Stage2Allows(signal, lastPrice))
                return false;

            if (EnableVWAPFilter && !VWAPAllows(signal, lastPrice))
                return false;

            if (EnableStructureFilter && !StructureAllows(signal, lastPrice))
                return false;

            if (EnableAbsorptionFilter && AbsorptionVeto(signal, lastPrice, delta100))
                return false;

            return true;
        }
        #endregion

        private bool ContextFirstAllows(DirectionSignal signal, double price)
        {
            // Context-first gate: decide whether this part of the auction is worth trading before
            // allowing the microburst trigger to dominate. This is intentionally separate from
            // Stage2Allows(), which validates the immediate response.
            if (RequireORCompleteBeforeTrading && EnableOpeningRangeFilter && !orCalculated)
                return false;

            if (EnableFailedDirectionCooldown && IsInsideFailedDirectionCooldown(signal, price))
                return false;

            if (EnableHTFBiasFilter && htfBias != DirectionSignal.None && htfBias != signal)
            {
                if (Math.Abs(weightedPersistenceScore) < CounterBiasOverridePersistence)
                    return false;
            }

            if (EnableRangeCenterSuppression && auctionState == AuctionState.Range && localSwingHigh > localSwingLow)
            {
                double rangeTicks = (localSwingHigh - localSwingLow) / TickSize;
                if (rangeTicks >= Math.Max(20, AcceptanceTicks * 2))
                {
                    double rel = (price - localSwingLow) / (localSwingHigh - localSwingLow);
                    bool center = rel >= (0.5 - RangeCenterBandPct / 2.0) && rel <= (0.5 + RangeCenterBandPct / 2.0);
                    bool edgeLong = rel <= RangeEdgeBandPct;
                    bool edgeShort = rel >= 1.0 - RangeEdgeBandPct;
                    bool powerOverride = Math.Abs(weightedPersistenceScore) >= RangePowerOverridePersistence
                        && (htfBias == DirectionSignal.None || htfBias == signal);

                    // Do not pepper the middle of balance. Let genuine power moves override, but
                    // otherwise only allow range participation at favorable edges.
                    if (center && !powerOverride)
                        return false;
                    if (signal == DirectionSignal.Long && !edgeLong && !powerOverride)
                        return false;
                    if (signal == DirectionSignal.Short && !edgeShort && !powerOverride)
                        return false;
                }
            }

            return true;
        }

        private bool IsInsideFailedDirectionCooldown(DirectionSignal signal, double price)
        {
            if (signal == DirectionSignal.Long && lastFailedLongTime != DateTime.MinValue)
            {
                bool recent = (currentMarketTime - lastFailedLongTime).TotalSeconds < FailedDirectionCooldownSeconds;
                bool nearby = lastFailedLongPrice <= 0.0 || Math.Abs(price - lastFailedLongPrice) / TickSize <= FailedDirectionZoneTicks;
                bool overridePower = weightedPersistenceScore >= FailedDirectionOverridePersistence;
                if (recent && nearby && !overridePower)
                    return true;
            }
            else if (signal == DirectionSignal.Short && lastFailedShortTime != DateTime.MinValue)
            {
                bool recent = (currentMarketTime - lastFailedShortTime).TotalSeconds < FailedDirectionCooldownSeconds;
                bool nearby = lastFailedShortPrice <= 0.0 || Math.Abs(price - lastFailedShortPrice) / TickSize <= FailedDirectionZoneTicks;
                bool overridePower = weightedPersistenceScore <= -FailedDirectionOverridePersistence;
                if (recent && nearby && !overridePower)
                    return true;
            }

            return false;
        }

        #region Filters
        private bool QuoteIsFreshAndSane()
        {
            if (currentBestBid <= 0.0 || currentBestAsk <= 0.0)
                return false;

            double spreadTicks = (currentBestAsk - currentBestBid) / TickSize;
            if (spreadTicks < 0.5 || spreadTicks > MaxSpreadTicks)
                return false;

            if (MaxQuoteAgeMs > 0 && lastBidAskUpdateTime != DateTime.MinValue)
            {
                if ((currentMarketTime - lastBidAskUpdateTime).TotalMilliseconds > MaxQuoteAgeMs)
                    return false;
            }

            return true;
        }

        private bool AuctionAllows(DirectionSignal signal)
        {
            if (auctionState == AuctionState.Unknown)
                return false;

            // Stage 2.8: Range remains discovery-only, but no longer requires absorption by default.
            // Stage2Allows() must still confirm post-aggression price acceptance.
            if (auctionState == AuctionState.Range && AllowRangeDiscoveryTrades)
            {
                if (signal == DirectionSignal.Long && weightedPersistenceScore >= MinRangeDiscoveryPersistenceScore)
                    return true;
                if (signal == DirectionSignal.Short && weightedPersistenceScore <= -MinRangeDiscoveryPersistenceScore)
                    return true;
                return false;
            }

            if (signal == DirectionSignal.Long)
            {
                if (auctionState == AuctionState.TrendUp)
                    return true;
                if (AllowReversalTrades && auctionState == AuctionState.ReversalUp)
                    return true;
                return false;
            }

            if (signal == DirectionSignal.Short)
            {
                if (auctionState == AuctionState.TrendDown)
                    return true;
                if (AllowReversalTrades && auctionState == AuctionState.ReversalDown)
                    return true;
                return false;
            }

            return false;
        }

        private bool VWAPAllows(DirectionSignal signal, double price)
        {
            if (sessionVwap <= 0.0)
                return true;

            double distTicks = (price - sessionVwap) / TickSize;
            if (signal == DirectionSignal.Long)
                return distTicks >= MinDistanceFromVWAPTicks;
            if (signal == DirectionSignal.Short)
                return distTicks <= -MinDistanceFromVWAPTicks;
            return false;
        }

        private bool StructureAllows(DirectionSignal signal, double price)
        {
            if (localSwingHigh <= 0.0 || localSwingLow <= 0.0)
                return true;

            // Avoid buying directly under local swing resistance or selling directly above support.
            double ticksBelowSwingHigh = (localSwingHigh - price) / TickSize;
            double ticksAboveSwingLow = (price - localSwingLow) / TickSize;

            if (signal == DirectionSignal.Long)
            {
                if (ticksBelowSwingHigh >= 0 && ticksBelowSwingHigh < AcceptanceTicks)
                    return false;
                if (EnableOpeningRangeFilter && orCalculated && price < orHigh && GetORLocation(price) == "INSIDE_OR")
                    return false;
            }
            else if (signal == DirectionSignal.Short)
            {
                if (ticksAboveSwingLow >= 0 && ticksAboveSwingLow < AcceptanceTicks)
                    return false;
                if (EnableOpeningRangeFilter && orCalculated && price > orLow && GetORLocation(price) == "INSIDE_OR")
                    return false;
            }

            return true;
        }

        private bool AbsorptionVeto(DirectionSignal signal, double price, long delta100)
        {
            // Simple absorption veto: strong aggression in one direction but price has not escaped pending reference.
            if (pendingDirection != signal || pendingReferencePrice <= 0.0)
                return false;

            double movedTicks = Math.Abs(price - pendingReferencePrice) / TickSize;
            if (Math.Abs(delta100) >= AbsorptionAggressionDelta && movedTicks <= AbsorptionPriceMoveMaxTicks)
                return true;

            return false;
        }
        #endregion


        private void UpdateResponseHistory(double price, long delta100, double imb100)
        {
            responseBuckets.Enqueue(new ResponseBucket(currentMarketTime, price, delta100, imb100));
            int keepMs = Math.Max(Math.Max(Stage2AbsorptionLookbackMs, Stage2ResponseWindowMs), 15000);
            while (responseBuckets.Count > 0 && (currentMarketTime - responseBuckets.Peek().Time).TotalMilliseconds > keepMs)
                responseBuckets.Dequeue();
        }

        private void UpdateStage2ResponseState(double price, long delta100, double imb100)
        {
            if (!EnableStage2ResponseEngine)
                return;

            bool longAccepted = HasDirectionalAcceptance(DirectionSignal.Long, price);
            bool shortAccepted = HasDirectionalAcceptance(DirectionSignal.Short, price);
            acceptedLongBuckets = longAccepted ? acceptedLongBuckets + 1 : 0;
            acceptedShortBuckets = shortAccepted ? acceptedShortBuckets + 1 : 0;

            bool absLong = HasAbsorptionReversal(DirectionSignal.Long, price);
            bool absShort = HasAbsorptionReversal(DirectionSignal.Short, price);
            rangeAbsorptionLongBuckets = absLong ? rangeAbsorptionLongBuckets + 1 : 0;
            rangeAbsorptionShortBuckets = absShort ? rangeAbsorptionShortBuckets + 1 : 0;

            DirectionSignal rejected = DetectImmediateRejection(price);
            if (rejected != DirectionSignal.None)
            {
                lastStage2RejectionTime = currentMarketTime;
                lastRejectionDirection = rejected;
            }
        }

        private bool Stage2Allows(DirectionSignal signal, double price)
        {
            if (signal == DirectionSignal.None)
                return false;

            if (lastStage2SweepTime != DateTime.MinValue && (currentMarketTime - lastStage2SweepTime).TotalMilliseconds < Stage2SweepCooldownMs)
                return false;

            if (lastStage2RejectionTime != DateTime.MinValue
                && lastRejectionDirection == signal
                && (currentMarketTime - lastStage2RejectionTime).TotalMilliseconds < Stage2RejectionCooldownMs)
                return false;

            if (EnableStage2DirectionalAgreement && !Stage2DirectionalAgreementConfirmed(signal))
                return false;

            if (auctionState == AuctionState.Range)
            {
                if (Stage2RangeRequiresAbsorption)
                    return Stage2AbsorptionConfirmed(signal);

                // v2.9: Range is allowed only when acceptance is directional AND quality is confirmed.
                // This prevents v2.8's accL/accS noise from producing raw range-chase entries.
                return Stage2AcceptanceConfirmed(signal, price) && Stage2LightweightConfirmation(signal);
            }

            if (auctionState == AuctionState.TrendUp && signal == DirectionSignal.Long)
                return Stage2AcceptanceConfirmed(signal, price) && Stage2LightweightConfirmation(signal);

            if (auctionState == AuctionState.TrendDown && signal == DirectionSignal.Short)
                return Stage2AcceptanceConfirmed(signal, price) && Stage2LightweightConfirmation(signal);

            if (AllowReversalTrades && (auctionState == AuctionState.ReversalUp || auctionState == AuctionState.ReversalDown))
                return Stage2AbsorptionConfirmed(signal);

            return false;
        }

        private bool Stage2AcceptanceConfirmed(DirectionSignal signal, double price)
        {
            return signal == DirectionSignal.Long
                ? acceptedLongBuckets >= Stage2AcceptanceBucketsRequired
                : acceptedShortBuckets >= Stage2AcceptanceBucketsRequired;
        }

        private bool Stage2DirectionalAgreementConfirmed(DirectionSignal signal)
        {
            if (signal == DirectionSignal.Long)
            {
                if (weightedPersistenceScore < Stage2MinDirectionalPersistence)
                    return false;
                if (acceptedLongBuckets < acceptedShortBuckets + Stage2MinAcceptanceDominance)
                    return false;
                return true;
            }

            if (signal == DirectionSignal.Short)
            {
                if (weightedPersistenceScore > -Stage2MinDirectionalPersistence)
                    return false;
                if (acceptedShortBuckets < acceptedLongBuckets + Stage2MinAcceptanceDominance)
                    return false;
                return true;
            }

            return false;
        }

        private bool Stage2LightweightConfirmation(DirectionSignal signal)
        {
            if (signal == DirectionSignal.Long)
            {
                if (consecutiveLongBuckets >= Stage2MinContinuationBuckets)
                    return true;
                if (rangeAbsorptionLongBuckets > 0)
                    return true;
                if (acceptedLongBuckets >= Stage2RangeStrongAcceptanceBuckets && acceptedLongBuckets > acceptedShortBuckets)
                    return true;
                return false;
            }

            if (signal == DirectionSignal.Short)
            {
                if (consecutiveShortBuckets >= Stage2MinContinuationBuckets)
                    return true;
                if (rangeAbsorptionShortBuckets > 0)
                    return true;
                if (acceptedShortBuckets >= Stage2RangeStrongAcceptanceBuckets && acceptedShortBuckets > acceptedLongBuckets)
                    return true;
                return false;
            }

            return false;
        }

        private bool Stage2AbsorptionConfirmed(DirectionSignal signal)
        {
            return signal == DirectionSignal.Long
                ? rangeAbsorptionLongBuckets >= Stage2AcceptanceBucketsRequired
                : rangeAbsorptionShortBuckets >= Stage2AcceptanceBucketsRequired;
        }

        private bool HasDirectionalAcceptance(DirectionSignal signal, double price)
        {
            if (responseBuckets.Count < 3)
                return false;

            DateTime cutoff = currentMarketTime.AddMilliseconds(-Math.Max(250, Stage2ResponseWindowMs));
            double first = 0.0;
            double minPrice = double.MaxValue;
            double maxPrice = double.MinValue;
            long netDelta = 0;
            int count = 0;

            foreach (ResponseBucket b in responseBuckets)
            {
                if (b.Time < cutoff)
                    continue;
                if (first <= 0.0)
                    first = b.Price;
                minPrice = Math.Min(minPrice, b.Price);
                maxPrice = Math.Max(maxPrice, b.Price);
                netDelta += b.Delta;
                count++;
            }

            if (count < 2 || first <= 0.0)
                return false;

            double progressTicks = (price - first) / TickSize;
            double adverseTicks;

            if (signal == DirectionSignal.Long)
            {
                adverseTicks = (first - minPrice) / TickSize;
                return progressTicks >= Stage2AcceptanceTicks
                    && adverseTicks <= Stage2MaxAdverseTicks
                    && netDelta > 0;
            }

            if (signal == DirectionSignal.Short)
            {
                progressTicks = (first - price) / TickSize;
                adverseTicks = (maxPrice - first) / TickSize;
                return progressTicks >= Stage2AcceptanceTicks
                    && adverseTicks <= Stage2MaxAdverseTicks
                    && netDelta < 0;
            }

            return false;
        }

        private bool HasAbsorptionReversal(DirectionSignal desiredSignal, double price)
        {
            if (responseBuckets.Count < 5)
                return false;

            DateTime cutoff = currentMarketTime.AddMilliseconds(-Math.Max(500, Stage2AbsorptionLookbackMs));
            double first = 0.0;
            double minPrice = double.MaxValue;
            double maxPrice = double.MinValue;
            long netDelta = 0;
            int count = 0;

            foreach (ResponseBucket b in responseBuckets)
            {
                if (b.Time < cutoff)
                    continue;
                if (first <= 0.0)
                    first = b.Price;
                minPrice = Math.Min(minPrice, b.Price);
                maxPrice = Math.Max(maxPrice, b.Price);
                netDelta += b.Delta;
                count++;
            }

            if (count < 4 || first <= 0.0)
                return false;

            // Long absorption: sellers attacked, but downside progress failed, and current price has reclaimed.
            if (desiredSignal == DirectionSignal.Long)
            {
                double downsideProgress = (first - minPrice) / TickSize;
                bool sellerAttackFailed = netDelta <= -Stage2AbsorptionDelta && downsideProgress <= Stage2AbsorptionMaxProgressTicks;
                bool reclaimed = price >= first + Math.Max(1, Stage2AcceptanceTicks / 2) * TickSize;
                return sellerAttackFailed && reclaimed;
            }

            // Short absorption: buyers attacked, but upside progress failed, and current price has rejected.
            if (desiredSignal == DirectionSignal.Short)
            {
                double upsideProgress = (maxPrice - first) / TickSize;
                bool buyerAttackFailed = netDelta >= Stage2AbsorptionDelta && upsideProgress <= Stage2AbsorptionMaxProgressTicks;
                bool rejected = price <= first - Math.Max(1, Stage2AcceptanceTicks / 2) * TickSize;
                return buyerAttackFailed && rejected;
            }

            return false;
        }

        private DirectionSignal DetectImmediateRejection(double price)
        {
            if (responseBuckets.Count < 3)
                return DirectionSignal.None;

            DateTime cutoff = currentMarketTime.AddMilliseconds(-Math.Max(250, Stage2ResponseWindowMs));
            double first = 0.0;
            double minPrice = double.MaxValue;
            double maxPrice = double.MinValue;
            long netDelta = 0;
            int count = 0;

            foreach (ResponseBucket b in responseBuckets)
            {
                if (b.Time < cutoff)
                    continue;
                if (first <= 0.0)
                    first = b.Price;
                minPrice = Math.Min(minPrice, b.Price);
                maxPrice = Math.Max(maxPrice, b.Price);
                netDelta += b.Delta;
                count++;
            }

            if (count < 3 || first <= 0.0)
                return DirectionSignal.None;

            // If buy aggression pushed up but price snapped back, reject longs.
            if (netDelta >= Stage2AbsorptionDelta && (maxPrice - price) / TickSize >= Stage2RejectionTicks)
                return DirectionSignal.Long;

            // If sell aggression pushed down but price snapped back, reject shorts.
            if (netDelta <= -Stage2AbsorptionDelta && (price - minPrice) / TickSize >= Stage2RejectionTicks)
                return DirectionSignal.Short;

            return DirectionSignal.None;
        }

        #region Entry/exit/order handling
        private void EnterPosition(DirectionSignal signal, double lastPrice)
        {
            if (signal == DirectionSignal.Long)
            {
                entryOrder = EnterLong(Quantity, "OFI_Long");
            }
            else if (signal == DirectionSignal.Short)
            {
                entryOrder = EnterShort(Quantity, "OFI_Short");
            }

            lastEntryTime = currentMarketTime;
            tradesCountToday++;
            pendingDirection = DirectionSignal.None;
            pendingAcceptanceBuckets = 0;
            responseBuckets.Clear();
            lastStage2SweepTime = DateTime.MinValue;
            lastStage2RejectionTime = DateTime.MinValue;
            lastRejectionDirection = DirectionSignal.None;
            acceptedLongBuckets = 0;
            acceptedShortBuckets = 0;
            rangeAbsorptionLongBuckets = 0;
            rangeAbsorptionShortBuckets = 0;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            string name = execution.Order.Name ?? string.Empty;
            if (!(name.StartsWith("OFI_") || name == "Profit target" || name == "Stop loss" || name == "Close position"))
                return;

            if (execution.Order.OrderState != OrderState.Filled && execution.Order.OrderState != OrderState.PartFilled)
                return;

            if (name == "OFI_Long" || name == "OFI_Short")
            {
                entryPrice = execution.Price;
                entryTime = time;
                entryQuantity = quantity;
                entryDirection = name == "OFI_Long" ? MarketPosition.Long : MarketPosition.Short;

                double targetPrice;
                double stopPrice;

                if (entryDirection == MarketPosition.Long)
                {
                    targetPrice = execution.Price + TargetTicks * TickSize;
                    stopPrice = execution.Price - StopTicks * TickSize;
                    targetOrder = ExitLongLimit(0, true, quantity, targetPrice, "OFI_Target", "OFI_Long");
                    stopOrder = ExitLongStopMarket(0, true, quantity, stopPrice, "OFI_Stop", "OFI_Long");
                }
                else
                {
                    targetPrice = execution.Price - TargetTicks * TickSize;
                    stopPrice = execution.Price + StopTicks * TickSize;
                    targetOrder = ExitShortLimit(0, true, quantity, targetPrice, "OFI_Target", "OFI_Short");
                    stopOrder = ExitShortStopMarket(0, true, quantity, stopPrice, "OFI_Stop", "OFI_Short");
                }

                Print(string.Format("{0} | #{1} ENTRY FILL {2} @ {3:F2} qty={4} | bracket target={5}t stop={6}t | state={7} pers={8:F2}",
                    time.ToString("HH:mm:ss.fff"), tradesCountToday, entryDirection, execution.Price, quantity, TargetTicks, StopTicks, auctionState, weightedPersistenceScore));
                return;
            }

            bool isExit = name == "OFI_Target" || name == "OFI_Stop" || name == "OFI_Timeout" || name == "Profit target" || name == "Stop loss" || name == "Close position";
            if (isExit && entryDirection != MarketPosition.Flat && entryPrice > 0.0)
            {
                ExitKind kind = ExitKind.Manual;
                if (name == "OFI_Target" || name == "Profit target") kind = ExitKind.Target;
                else if (name == "OFI_Stop" || name == "Stop loss") kind = ExitKind.Stop;
                else if (name == "OFI_Timeout") kind = ExitKind.Timeout;

                double pnl;
                if (entryDirection == MarketPosition.Long)
                    pnl = (execution.Price - entryPrice) * quantity * Instrument.MasterInstrument.PointValue;
                else
                    pnl = (entryPrice - execution.Price) * quantity * Instrument.MasterInstrument.PointValue;

                if (kind == ExitKind.Stop)
                {
                    if (entryDirection == MarketPosition.Long)
                    {
                        lastFailedLongTime = time;
                        lastFailedLongPrice = entryPrice;
                    }
                    else if (entryDirection == MarketPosition.Short)
                    {
                        lastFailedShortTime = time;
                        lastFailedShortPrice = entryPrice;
                    }
                }

                lastExitKind = kind;
                lastTradeExitTime = time;
                UpdateDailyTracking(pnl);

                Print(string.Format("{0} | #{1} EXIT {2} @ {3:F2} qty={4} | PnL:${5:F2} | Daily:${6:F2} | ConsLoss:{7} | Limits:{8}",
                    time.ToString("HH:mm:ss.fff"), tradesCountToday, kind, execution.Price, quantity, pnl, dailyPnL, consecutiveLosses,
                    EnableDailyLimits ? "ON" : "OFF"));

                entryDirection = MarketPosition.Flat;
                entryPrice = 0.0;
                entryQuantity = 0;
                entryTime = DateTime.MinValue;
                entryOrder = null;
                stopOrder = null;
                targetOrder = null;
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null)
                return;

            string n = order.Name ?? string.Empty;
            if (!(n.StartsWith("OFI_") || n == "Profit target" || n == "Stop loss"))
                return;

            if (orderState == OrderState.Rejected || orderState == OrderState.Cancelled)
            {
                Print(string.Format("{0} | ORDER {1}: {2} state={3} error={4} native={5}",
                    time.ToString("HH:mm:ss.fff"), order.OrderId, n, orderState, error, nativeError));
            }
        }

        private void CheckTimeout()
        {
            if (entryTime == DateTime.MinValue)
                return;

            TimeSpan hold = currentMarketTime - entryTime;
            if (hold.TotalMinutes < TimeoutMinutes)
                return;

            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong("OFI_Timeout", "OFI_Long");
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort("OFI_Timeout", "OFI_Short");
        }
        #endregion

        #region State helpers
        private void UpdatePersistence(DirectionSignal raw, long delta, double imb)
        {
            int sign = raw == DirectionSignal.Long ? 1 : raw == DirectionSignal.Short ? -1 : 0;
            double score = 0.0;
            if (sign != 0)
            {
                double deltaComponent = Math.Min(2.0, Math.Abs(delta) / Math.Max(1.0, MinAggressionDelta100ms));
                double imbComponent = Math.Min(1.5, Math.Abs(imb) / Math.Max(0.01, MinAggressionImbalance100ms));
                score = sign * (0.65 * deltaComponent + 0.35 * imbComponent);
            }

            persistenceSigns.Enqueue(sign);
            persistenceScores.Enqueue(score);
            while (persistenceSigns.Count > PersistenceWindowBuckets) persistenceSigns.Dequeue();
            while (persistenceScores.Count > PersistenceWindowBuckets) persistenceScores.Dequeue();

            if (sign > 0)
            {
                consecutiveLongBuckets++;
                consecutiveShortBuckets = 0;
            }
            else if (sign < 0)
            {
                consecutiveShortBuckets++;
                consecutiveLongBuckets = 0;
            }
            else
            {
                consecutiveLongBuckets = 0;
                consecutiveShortBuckets = 0;
            }

            weightedPersistenceScore = SumQueue(persistenceScores);
        }

        private void UpdatePendingAcceptance(double price)
        {
            if (pendingDirection == DirectionSignal.None)
                return;

            if (pendingDirection == DirectionSignal.Long)
            {
                if (price >= pendingTriggerPrice)
                    pendingAcceptanceBuckets++;
                else if (price <= pendingReferencePrice - AcceptanceTicks * TickSize)
                {
                    pendingDirection = DirectionSignal.None;
                    pendingAcceptanceBuckets = 0;
                }
            }
            else if (pendingDirection == DirectionSignal.Short)
            {
                if (price <= pendingTriggerPrice)
                    pendingAcceptanceBuckets++;
                else if (price >= pendingReferencePrice + AcceptanceTicks * TickSize)
                {
                    pendingDirection = DirectionSignal.None;
                    pendingAcceptanceBuckets = 0;
                }
            }
        }

        private void UpdateAuctionState(double close)
        {
            previousAuctionState = auctionState;

            if (recentMinuteCloses.Count < Math.Max(3, SwingLookbackMinutes))
            {
                auctionState = AuctionState.Unknown;
                return;
            }

            double firstClose = FirstQueue(recentMinuteCloses);
            double slopeTicks = (close - firstClose) / TickSize;
            string orLoc = GetORLocation(close);
            bool aboveVwap = sessionVwap > 0.0 && close > sessionVwap;
            bool belowVwap = sessionVwap > 0.0 && close < sessionVwap;

            double trendSlope = Math.Max(2, TrendSlopeTicks);
            double orBuffer = Math.Max(0, ORBreakoutBufferTicks) * TickSize;

            if (EnableOpeningRangeFilter && orCalculated)
            {
                bool acceptedAboveOr = close >= orHigh + orBuffer;
                bool acceptedBelowOr = close <= orLow - orBuffer;

                if ((acceptedAboveOr || close > localSwingHigh) && slopeTicks >= trendSlope && aboveVwap)
                    auctionState = AuctionState.TrendUp;
                else if ((acceptedBelowOr || close < localSwingLow) && slopeTicks <= -trendSlope && belowVwap)
                    auctionState = AuctionState.TrendDown;
                else if (slopeTicks >= trendSlope * 1.5 && aboveVwap)
                    auctionState = AuctionState.TrendUp;
                else if (slopeTicks <= -trendSlope * 1.5 && belowVwap)
                    auctionState = AuctionState.TrendDown;
                else
                    auctionState = AuctionState.Range;
            }
            else
            {
                if (slopeTicks >= trendSlope && aboveVwap)
                    auctionState = AuctionState.TrendUp;
                else if (slopeTicks <= -trendSlope && belowVwap)
                    auctionState = AuctionState.TrendDown;
                else
                    auctionState = AuctionState.Range;
            }
        }

        private void UpdateDailyTracking(double pnl)
        {
            dailyPnL += pnl;
            if (dailyPnL > dailyPeakPnL) dailyPeakPnL = dailyPnL;
            if (pnl < 0.0) consecutiveLosses++; else consecutiveLosses = 0;

            if (!EnableDailyLimits)
                return;

            double dd = dailyPnL - dailyPeakPnL;
            if (dailyPnL <= -MaxDailyLoss)
            {
                dailyLimitHit = true;
                Print("*** DAILY LOSS LIMIT HIT: $" + dailyPnL.ToString("F2") + " ***");
            }
            if (consecutiveLosses >= MaxConsecutiveLosses)
            {
                dailyLimitHit = true;
                Print("*** CONSECUTIVE LOSS LIMIT HIT: " + consecutiveLosses + " ***");
            }
            if (dailyPeakPnL >= ProfitLockPeak && dd <= -ProfitLockDrawdown)
            {
                dailyLimitHit = true;
                Print("*** PROFIT LOCK HIT: peak=$" + dailyPeakPnL.ToString("F2") + " dd=$" + dd.ToString("F2") + " ***");
            }
        }

        private void ResetSessionState(bool full)
        {
            bucket100msStart = DateTime.MinValue;
            bucket1sStart = DateTime.MinValue;
            bucket5sStart = DateTime.MinValue;
            Reset100msBucket();
            Reset1sBucket();
            Reset5sBucket();

            persistenceSigns.Clear();
            persistenceScores.Clear();
            consecutiveLongBuckets = 0;
            consecutiveShortBuckets = 0;
            weightedPersistenceScore = 0.0;
            pendingDirection = DirectionSignal.None;
            pendingAcceptanceBuckets = 0;
            responseBuckets.Clear();
            lastStage2SweepTime = DateTime.MinValue;
            lastStage2RejectionTime = DateTime.MinValue;
            lastRejectionDirection = DirectionSignal.None;
            acceptedLongBuckets = 0;
            acceptedShortBuckets = 0;
            rangeAbsorptionLongBuckets = 0;
            rangeAbsorptionShortBuckets = 0;

            orHigh = 0.0;
            orLow = 0.0;
            orCalculated = false;
            orDate = DateTime.MinValue;
            vwapCumPV = 0.0;
            vwapCumVol = 0.0;
            sessionVwap = 0.0;
            recentMinuteHighs.Clear();
            recentMinuteLows.Clear();
            recentMinuteCloses.Clear();
            recentFiveMinuteCloses.Clear();
            localSwingHigh = 0.0;
            localSwingLow = 0.0;
            htfBias = DirectionSignal.None;
            htfBiasScoreTicks = 0.0;
            lastFailedLongTime = DateTime.MinValue;
            lastFailedShortTime = DateTime.MinValue;
            lastFailedLongPrice = 0.0;
            lastFailedShortPrice = 0.0;
            auctionState = AuctionState.Unknown;
            previousAuctionState = AuctionState.Unknown;

            dailyPnL = 0.0;
            dailyPeakPnL = 0.0;
            consecutiveLosses = 0;
            dailyLimitHit = false;
            tradesCountToday = 0;
            rejectedSignalCount = 0;
            lastExitKind = ExitKind.None;
            lastTradeExitTime = DateTime.MinValue;
            lastEntryTime = DateTime.MinValue;
            lastSweepTime = DateTime.MinValue;

            marketDataEvents = 0;
            depthEvents = 0;
            processedBuckets = 0;
            rthExecutionGateWasOpen = false;
            lastRthGateDateEt = DateTime.MinValue;
            lastRthFlattenDateEt = DateTime.MinValue;

            if (full)
            {
                currentBestBid = 0.0;
                currentBestAsk = 0.0;
                lastTradePrice = 0.0;
                priorTradePrice = 0.0;
                currentMarketTime = DateTime.MinValue;
                lastBidAskUpdateTime = DateTime.MinValue;
            }
        }

        private void Reset100msBucket() { aggBuyVol100ms = 0; aggSellVol100ms = 0; }
        private void Reset1sBucket() { aggBuyVol1s = 0; aggSellVol1s = 0; }
        private void Reset5sBucket() { aggBuyVol5s = 0; aggSellVol5s = 0; }
        #endregion

        #region Utility
        private DateTime ToEastern(DateTime t)
        {
            try { return TimeZoneInfo.ConvertTime(t, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); }
            catch { return t; }
        }

        private void HandleRTHExecutionBoundary(DateTime t)
        {
            if (!EnforceRTHExecutionOnly)
                return;

            DateTime et = ToEastern(t);
            DateTime etDate = et.Date;
            TimeSpan now = et.TimeOfDay;
            TimeSpan rthEnd = new TimeSpan(RTHEndHour, RTHEndMinute, 0);
            bool insideRth = IsRTH(t);

            if (lastRthGateDateEt.Date != etDate)
            {
                lastRthGateDateEt = etDate;
                rthExecutionGateWasOpen = false;
                lastRthFlattenDateEt = DateTime.MinValue;
            }

            if (insideRth && !rthExecutionGateWasOpen)
            {
                rthExecutionGateWasOpen = true;
                pendingDirection = DirectionSignal.None;
                pendingAcceptanceBuckets = 0;
                lastStage2RejectionTime = DateTime.MinValue;
                lastStage2SweepTime = DateTime.MinValue;
                lastRejectionDirection = DirectionSignal.None;
                if (PrintDiagnostics)
                    Print(et.ToString("HH:mm:ss.fff") + " | RTH EXECUTION GATE OPEN: entries enabled");
            }

            if (!insideRth && rthExecutionGateWasOpen && now >= rthEnd)
            {
                rthExecutionGateWasOpen = false;
                pendingDirection = DirectionSignal.None;
                pendingAcceptanceBuckets = 0;

                if (FlattenAtRTHEnd && lastRthFlattenDateEt.Date != etDate)
                {
                    lastRthFlattenDateEt = etDate;
                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        ExitLong("RTH_Close", "OFI_Long");
                        if (PrintDiagnostics) Print(et.ToString("HH:mm:ss.fff") + " | RTH EXECUTION GATE CLOSED: flattening long");
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        ExitShort("RTH_Close", "OFI_Short");
                        if (PrintDiagnostics) Print(et.ToString("HH:mm:ss.fff") + " | RTH EXECUTION GATE CLOSED: flattening short");
                    }
                    else if (PrintDiagnostics)
                    {
                        Print(et.ToString("HH:mm:ss.fff") + " | RTH EXECUTION GATE CLOSED: entries disabled");
                    }
                }
            }
        }

        private bool IsRTH(DateTime t)
        {
            DateTime et = ToEastern(t);
            TimeSpan now = et.TimeOfDay;
            return now >= new TimeSpan(RTHStartHour, RTHStartMinute, 0) && now < new TimeSpan(RTHEndHour, RTHEndMinute, 0);
        }

        private string GetORLocation(double price)
        {
            if (!orCalculated || orHigh <= 0.0 || orLow <= 0.0) return "UNKNOWN";
            if (price > orHigh) return "ABOVE_OR";
            if (price < orLow) return "BELOW_OR";
            return "INSIDE_OR";
        }

        private void UpdateRolling(Queue<double> q, double v, int max)
        {
            q.Enqueue(v);
            while (q.Count > Math.Max(1, max)) q.Dequeue();
        }

        private double MaxQueue(Queue<double> q)
        {
            double m = double.MinValue;
            foreach (double v in q) if (v > m) m = v;
            return m == double.MinValue ? 0.0 : m;
        }

        private double MinQueue(Queue<double> q)
        {
            double m = double.MaxValue;
            foreach (double v in q) if (v < m) m = v;
            return m == double.MaxValue ? 0.0 : m;
        }

        private double SumQueue(Queue<double> q)
        {
            double s = 0.0;
            foreach (double v in q) s += v;
            return s;
        }

        private double FirstQueue(Queue<double> q)
        {
            foreach (double v in q) return v;
            return 0.0;
        }
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Range(10, 500)]
        [Display(Name = "Min Agg Delta 100ms", Order = 1, GroupName = "1. Signal")]
        public int MinAggressionDelta100ms { get; set; }

        [NinjaScriptProperty]
        [Range(0.30, 0.95)]
        [Display(Name = "Min Agg Imbalance 100ms", Order = 2, GroupName = "1. Signal")]
        public double MinAggressionImbalance100ms { get; set; }

        [NinjaScriptProperty]
        [Range(10, 1000)]
        [Display(Name = "Min Agg Delta 1s", Order = 3, GroupName = "1. Signal")]
        public int MinAggressionDelta1s { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 0.90)]
        [Display(Name = "Min Agg Imbalance 1s", Order = 4, GroupName = "1. Signal")]
        public double MinAggressionImbalance1s { get; set; }

        [NinjaScriptProperty]
        [Range(10, 3000)]
        [Display(Name = "Min Agg Delta 5s", Order = 5, GroupName = "1. Signal")]
        public int MinAggressionDelta5s { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 0.90)]
        [Display(Name = "Min Agg Imbalance 5s", Order = 6, GroupName = "1. Signal")]
        public double MinAggressionImbalance5s { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Consecutive Buckets Required", Order = 7, GroupName = "1. Signal")]
        public int ConsecutiveBucketsRequired { get; set; }

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "Persistence Window Buckets", Order = 8, GroupName = "1. Signal")]
        public int PersistenceWindowBuckets { get; set; }

        [NinjaScriptProperty]
        [Range(0.50, 20.00)]
        [Display(Name = "Min Persistence Score", Order = 9, GroupName = "1. Signal")]
        public double MinPersistenceScore { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require 1s Confirmation", Order = 10, GroupName = "1. Signal")]
        public bool Require1sConfirmation { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require 5s Non-Opposition", Order = 11, GroupName = "1. Signal")]
        public bool Require5sNonOpposition { get; set; }

        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name = "Target Ticks", Order = 1, GroupName = "2. Risk")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Stop Ticks", Order = 2, GroupName = "2. Risk")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "Timeout Minutes", Order = 3, GroupName = "2. Risk")]
        public int TimeoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Spread Ticks", Order = 4, GroupName = "2. Risk")]
        public int MaxSpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Range(100, 5000)]
        [Display(Name = "Max Quote Age Ms", Order = 5, GroupName = "2. Risk")]
        public int MaxQuoteAgeMs { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1)]
        [Display(Name = "Quantity", Order = 6, GroupName = "2. Risk")]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable OR Filter", Order = 1, GroupName = "3. Structure")]
        public bool EnableOpeningRangeFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable VWAP Filter", Order = 2, GroupName = "3. Structure")]
        public bool EnableVWAPFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Structure Filter", Order = 3, GroupName = "3. Structure")]
        public bool EnableStructureFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Auction State Machine", Order = 4, GroupName = "3. Structure")]
        public bool EnableAuctionStateMachine { get; set; }

        [NinjaScriptProperty]
        [Range(3, 30)]
        [Display(Name = "Swing Lookback Minutes", Order = 5, GroupName = "3. Structure")]
        public int SwingLookbackMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Min Distance From VWAP Ticks", Order = 6, GroupName = "3. Structure")]
        public int MinDistanceFromVWAPTicks { get; set; }

        [NinjaScriptProperty]
        [Range(2, 50)]
        [Display(Name = "Trend Slope Ticks", Order = 7, GroupName = "3. Structure")]
        public int TrendSlopeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 40)]
        [Display(Name = "OR Breakout Buffer Ticks", Order = 8, GroupName = "3. Structure")]
        public int ORBreakoutBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Reversal Trades", Order = 9, GroupName = "3. Structure")]
        public bool AllowReversalTrades { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Range Discovery Trades", Order = 10, GroupName = "3. Structure")]
        public bool AllowRangeDiscoveryTrades { get; set; }

        [NinjaScriptProperty]
        [Range(0.50, 20.00)]
        [Display(Name = "Min Range Discovery Persistence", Order = 11, GroupName = "3. Structure")]
        public double MinRangeDiscoveryPersistenceScore { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Post Sweep Delay", Order = 1, GroupName = "4. Sweep/Acceptance")]
        public bool EnablePostSweepDelay { get; set; }

        [NinjaScriptProperty]
        [Range(20, 2000)]
        [Display(Name = "Sweep Delta Threshold", Order = 2, GroupName = "4. Sweep/Acceptance")]
        public int SweepDeltaThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.50, 0.99)]
        [Display(Name = "Sweep Imbalance Threshold", Order = 3, GroupName = "4. Sweep/Acceptance")]
        public double SweepImbalanceThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0, 5000)]
        [Display(Name = "Post Sweep Delay Ms", Order = 4, GroupName = "4. Sweep/Acceptance")]
        public int PostSweepDelayMs { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Price Acceptance", Order = 5, GroupName = "4. Sweep/Acceptance")]
        public bool EnablePriceAcceptance { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Acceptance Ticks", Order = 6, GroupName = "4. Sweep/Acceptance")]
        public int AcceptanceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Acceptance Buckets Required", Order = 7, GroupName = "4. Sweep/Acceptance")]
        public int AcceptanceBucketsRequired { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Absorption Filter", Order = 8, GroupName = "4. Sweep/Acceptance")]
        public bool EnableAbsorptionFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Absorption Price Move Max Ticks", Order = 9, GroupName = "4. Sweep/Acceptance")]
        public int AbsorptionPriceMoveMaxTicks { get; set; }

        [NinjaScriptProperty]
        [Range(20, 3000)]
        [Display(Name = "Absorption Aggression Delta", Order = 10, GroupName = "4. Sweep/Acceptance")]
        public int AbsorptionAggressionDelta { get; set; }


        [NinjaScriptProperty]
        [Display(Name = "Enable Stage2 Response Engine", Order = 11, GroupName = "4. Sweep/Acceptance")]
        public bool EnableStage2ResponseEngine { get; set; }

        [NinjaScriptProperty]
        [Range(500, 10000)]
        [Display(Name = "Stage2 Response Window Ms", Order = 12, GroupName = "4. Sweep/Acceptance")]
        public int Stage2ResponseWindowMs { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Stage2 Acceptance Ticks", Order = 13, GroupName = "4. Sweep/Acceptance")]
        public int Stage2AcceptanceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Stage2 Acceptance Buckets Required", Order = 14, GroupName = "4. Sweep/Acceptance")]
        public int Stage2AcceptanceBucketsRequired { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Stage2 Max Adverse Ticks", Order = 15, GroupName = "4. Sweep/Acceptance")]
        public int Stage2MaxAdverseTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stage2 Range Requires Absorption", Order = 16, GroupName = "4. Sweep/Acceptance")]
        public bool Stage2RangeRequiresAbsorption { get; set; }

        [NinjaScriptProperty]
        [Range(500, 15000)]
        [Display(Name = "Stage2 Absorption Lookback Ms", Order = 17, GroupName = "4. Sweep/Acceptance")]
        public int Stage2AbsorptionLookbackMs { get; set; }

        [NinjaScriptProperty]
        [Range(20, 5000)]
        [Display(Name = "Stage2 Absorption Delta", Order = 18, GroupName = "4. Sweep/Acceptance")]
        public int Stage2AbsorptionDelta { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Stage2 Absorption Max Progress Ticks", Order = 19, GroupName = "4. Sweep/Acceptance")]
        public int Stage2AbsorptionMaxProgressTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Stage2 Rejection Ticks", Order = 20, GroupName = "4. Sweep/Acceptance")]
        public int Stage2RejectionTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Stage2 Rejection Cooldown Ms", Order = 21, GroupName = "4. Sweep/Acceptance")]
        public int Stage2RejectionCooldownMs { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Stage2 Sweep Cooldown Ms", Order = 22, GroupName = "4. Sweep/Acceptance")]
        public int Stage2SweepCooldownMs { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Stage2 Directional Agreement", Order = 23, GroupName = "4. Sweep/Acceptance")]
        public bool EnableStage2DirectionalAgreement { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "Stage2 Min Directional Persistence", Order = 24, GroupName = "4. Sweep/Acceptance")]
        public double Stage2MinDirectionalPersistence { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Stage2 Min Acceptance Dominance", Order = 25, GroupName = "4. Sweep/Acceptance")]
        public int Stage2MinAcceptanceDominance { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Stage2 Min Continuation Buckets", Order = 26, GroupName = "4. Sweep/Acceptance")]
        public int Stage2MinContinuationBuckets { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Stage2 Range Strong Acceptance Buckets", Order = 27, GroupName = "4. Sweep/Acceptance")]
        public int Stage2RangeStrongAcceptanceBuckets { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require OR Complete Before Trading", Order = 30, GroupName = "4. Sweep/Acceptance")]
        public bool RequireORCompleteBeforeTrading { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable HTF Bias Filter", Order = 31, GroupName = "4. Sweep/Acceptance")]
        public bool EnableHTFBiasFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "HTF Bias Slope Ticks", Order = 32, GroupName = "4. Sweep/Acceptance")]
        public int HTFBiasSlopeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.50, 20.00)]
        [Display(Name = "Counter Bias Override Persistence", Order = 33, GroupName = "4. Sweep/Acceptance")]
        public double CounterBiasOverridePersistence { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Range Center Suppression", Order = 34, GroupName = "4. Sweep/Acceptance")]
        public bool EnableRangeCenterSuppression { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.90)]
        [Display(Name = "Range Center Band Pct", Order = 35, GroupName = "4. Sweep/Acceptance")]
        public double RangeCenterBandPct { get; set; }

        [NinjaScriptProperty]
        [Range(0.50, 20.00)]
        [Display(Name = "Range Power Override Persistence", Order = 36, GroupName = "4. Sweep/Acceptance")]
        public double RangePowerOverridePersistence { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.45)]
        [Display(Name = "Range Edge Band Pct", Order = 37, GroupName = "4. Sweep/Acceptance")]
        public double RangeEdgeBandPct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Failed Direction Cooldown", Order = 38, GroupName = "4. Sweep/Acceptance")]
        public bool EnableFailedDirectionCooldown { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "Failed Direction Cooldown Seconds", Order = 39, GroupName = "4. Sweep/Acceptance")]
        public int FailedDirectionCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Failed Direction Zone Ticks", Order = 40, GroupName = "4. Sweep/Acceptance")]
        public int FailedDirectionZoneTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.50, 20.00)]
        [Display(Name = "Failed Direction Override Persistence", Order = 41, GroupName = "4. Sweep/Acceptance")]
        public double FailedDirectionOverridePersistence { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Cooldown", Order = 1, GroupName = "5. Frequency")]
        public bool EnableCooldown { get; set; }

        [NinjaScriptProperty]
        [Range(0, 600)]
        [Display(Name = "Cooldown Seconds", Order = 2, GroupName = "5. Frequency")]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1200)]
        [Display(Name = "Post Stop Cooldown Seconds", Order = 3, GroupName = "5. Frequency")]
        public int PostStopCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 600)]
        [Display(Name = "Minimum Seconds Between Entries", Order = 4, GroupName = "5. Frequency")]
        public int MinimumSecondsBetweenEntries { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Max Trades Per Day", Order = 5, GroupName = "5. Frequency")]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Daily Limits", Order = 1, GroupName = "6. Daily Limits")]
        public bool EnableDailyLimits { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(Name = "Max Daily Loss", Order = 2, GroupName = "6. Daily Limits")]
        public double MaxDailyLoss { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Max Consecutive Losses", Order = 3, GroupName = "6. Daily Limits")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(Name = "Profit Lock Peak", Order = 4, GroupName = "6. Daily Limits")]
        public double ProfitLockPeak { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(Name = "Profit Lock Drawdown", Order = 5, GroupName = "6. Daily Limits")]
        public double ProfitLockDrawdown { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "RTH Start Hour", Order = 1, GroupName = "7. RTH")]
        public int RTHStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "RTH Start Minute", Order = 2, GroupName = "7. RTH")]
        public int RTHStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "RTH End Hour", Order = 3, GroupName = "7. RTH")]
        public int RTHEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "RTH End Minute", Order = 4, GroupName = "7. RTH")]
        public int RTHEndMinute { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enforce RTH Execution Only", Order = 5, GroupName = "7. RTH")]
        public bool EnforceRTHExecutionOnly { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Flatten At RTH End", Order = 6, GroupName = "7. RTH")]
        public bool FlattenAtRTHEnd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Print Diagnostics", Order = 1, GroupName = "8. Diagnostics")]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(10, 10000)]
        [Display(Name = "Diagnostic Every Buckets", Order = 2, GroupName = "8. Diagnostics")]
        public int DiagnosticEveryBuckets { get; set; }
        #endregion
    }
}
