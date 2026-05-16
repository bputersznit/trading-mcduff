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

// =================================================================================================
// CG_PersistenceGovernor_v1.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-08 04:34:00 UTC
// Project: MNQ Intraday Strategy — Persistence Governor / Auction Intelligence Upgrade
//
// PURPOSE
// -------
// This is a full replacement NinjaScript strategy implementing the next-step diagnosis from the
// 2026-04-24 chart review:
//   1. Stop impulse-chasing inside rotational balance.
//   2. Add AuctionQualityScore to block low-efficiency / high-overlap environments.
//   3. Add Acceptance/Rejection classification.
//   4. Add Impulse -> Pullback -> Reclaim -> Continuation entry logic.
//   5. Add late-chase / exhaustion suppression.
//
// DESIGN INTENT
// -------------
// The strategy is intentionally conservative. The prior system was functionally alive but buying
// local highs and selling local lows inside two-sided rotation. This version refuses immediate
// impulse entries and waits for a pullback/reclaim confirmation after an accepted directional impulse.
//
// LIVE/PLAYBACK NOTES
// -------------------
// - Apply to MNQ chart. The strategy internally adds a synthetic tick series for tactical processing.
// - One MNQ contract only. No overlapping positions.
// - Uses SetStopLoss/SetProfitTarget to submit broker-side protective OCO brackets after entry.
// - Best tested first in Playback101, not live.
// - Default RTH window is 09:30:00 to 15:59:00 Eastern Time converted from local chart time.
//
// IMPORTANT LIMITATIONS
// ---------------------
// This strategy uses chart/tick OHLCV as a live proxy for auction state. It does not read Bookmap
// heatmap/L2 wall tables directly. The intent is to implement the behavioral correction in NT8:
// trade less, avoid rotation, avoid late impulse chase, and require continuation acceptance.
// =================================================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_PersistenceGovernor_v1 : Strategy
    {
        // -----------------------------------------------------------------------------------------
        // Internal enums
        // -----------------------------------------------------------------------------------------
        private enum AuctionState
        {
            Unknown,
            Balance,
            Rotation,
            Compression,
            DiscoveryUp,
            DiscoveryDown,
            TrendUp,
            TrendDown,
            RejectedUp,
            RejectedDown,
            ExhaustionUp,
            ExhaustionDown
        }

        private enum BiasState
        {
            None,
            LongOnly,
            ShortOnly,
            Both
        }

        private enum SetupState
        {
            Idle,
            LongImpulseSeen,
            LongPullbackSeen,
            ShortImpulseSeen,
            ShortPullbackSeen
        }

        // -----------------------------------------------------------------------------------------
        // User parameters
        // -----------------------------------------------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Use Internal Tick Series", GroupName = "01. Series", Order = 0)]
        public bool UseInternalTickSeries { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "Internal Tick Bars", GroupName = "01. Series", Order = 1)]
        public int InternalTickBars { get; set; }

        [NinjaScriptProperty]
        [Range(5, 300)]
        [Display(Name = "Synthetic Seconds", GroupName = "02. Synthetic Auction", Order = 0)]
        public int SyntheticSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(20, 400)]
        [Display(Name = "Range Lookback Bars", GroupName = "02. Synthetic Auction", Order = 1)]
        public int RangeLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Warmup Synthetic Bars", GroupName = "02. Synthetic Auction", Order = 2)]
        public int WarmupSyntheticBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.50, 0.99)]
        [Display(Name = "Persistence Decay", GroupName = "03. Persistence", Order = 0)]
        public double PersistenceDecay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Trend Ownership Threshold", GroupName = "03. Persistence", Order = 1)]
        public double TrendOwnershipThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Min Entry Persistence Score", GroupName = "03. Persistence", Order = 2)]
        public double MinEntryPersistenceScore { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Min Same Direction Synthetic Bars", GroupName = "03. Persistence", Order = 3)]
        public int MinSameDirectionBarsForEntry { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "Min Auction Quality", GroupName = "04. Auction Quality", Order = 0)]
        public double MinAuctionQuality { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "Min Directional Efficiency", GroupName = "04. Auction Quality", Order = 1)]
        public double MinDirectionalEfficiency { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "Max Overlap Ratio", GroupName = "04. Auction Quality", Order = 2)]
        public double MaxOverlapRatio { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "Max Wick Ratio", GroupName = "04. Auction Quality", Order = 3)]
        public double MaxWickRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Quality Lookback Synthetic Bars", GroupName = "04. Auction Quality", Order = 4)]
        public int QualityLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Impulse Bars", GroupName = "05. Continuation Entry", Order = 0)]
        public int ImpulseBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 100.0)]
        [Display(Name = "Min Impulse Points", GroupName = "05. Continuation Entry", Order = 1)]
        public double MinImpulsePoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.10, 0.95)]
        [Display(Name = "Pullback Min Fraction", GroupName = "05. Continuation Entry", Order = 2)]
        public double PullbackMinFraction { get; set; }

        [NinjaScriptProperty]
        [Range(0.10, 0.95)]
        [Display(Name = "Pullback Max Fraction", GroupName = "05. Continuation Entry", Order = 3)]
        public double PullbackMaxFraction { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 50.0)]
        [Display(Name = "Reclaim Buffer Points", GroupName = "05. Continuation Entry", Order = 4)]
        public double ReclaimBufferPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 40)]
        [Display(Name = "Max Setup Age Synthetic Bars", GroupName = "05. Continuation Entry", Order = 5)]
        public int MaxSetupAgeBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Late Chase Block Ticks", GroupName = "06. Exhaustion Controls", Order = 0)]
        public int LateChaseBlockTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Exhaustion Lookback Bars", GroupName = "06. Exhaustion Controls", Order = 1)]
        public int ExhaustionLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.10, 1.00)]
        [Display(Name = "Exhaustion Efficiency Collapse", GroupName = "06. Exhaustion Controls", Order = 2)]
        public double ExhaustionEfficiencyCollapse { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Target Ticks", GroupName = "07. Risk", Order = 0)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Stop Ticks", GroupName = "07. Risk", Order = 1)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(10, 1800)]
        [Display(Name = "Max Hold Seconds", GroupName = "07. Risk", Order = 2)]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1800)]
        [Display(Name = "Cooldown Seconds", GroupName = "07. Risk", Order = 3)]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "Post Stop Cooldown Seconds", GroupName = "07. Risk", Order = 4)]
        public int PostStopCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Start Time ET", GroupName = "08. Time", Order = 0)]
        public int StartTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "End Time ET", GroupName = "08. Time", Order = 1)]
        public int EndTimeEt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Block Midday If Rotational", GroupName = "08. Time", Order = 2)]
        public bool BlockMiddayIfRotational { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Midday Start ET", GroupName = "08. Time", Order = 3)]
        public int MiddayStartEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Midday End ET", GroupName = "08. Time", Order = 4)]
        public int MiddayEndEt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Diagnostics", GroupName = "09. Diagnostics", Order = 0)]
        public bool EnableDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Diagnostic Print Every N Synthetic Bars", GroupName = "09. Diagnostics", Order = 1)]
        public int DiagnosticEveryNBars { get; set; }

        // -----------------------------------------------------------------------------------------
        // Runtime fields
        // -----------------------------------------------------------------------------------------
        private int tradeBip;
        private DateTime currentSyntheticStart;
        private bool syntheticInitialized;
        private double synOpen;
        private double synHigh;
        private double synLow;
        private double synClose;
        private double synVolume;
        private int syntheticBarCount;

        private const int MaxSyntheticHistory = 512;
        private readonly double[] synO = new double[MaxSyntheticHistory];
        private readonly double[] synH = new double[MaxSyntheticHistory];
        private readonly double[] synL = new double[MaxSyntheticHistory];
        private readonly double[] synC = new double[MaxSyntheticHistory];
        private readonly double[] synV = new double[MaxSyntheticHistory];
        private readonly int[] synDir = new int[MaxSyntheticHistory];

        private double longPersistence;
        private double shortPersistence;
        private int sameDirectionCount;
        private int lastSyntheticDirection;

        private AuctionState auctionState;
        private BiasState biasState;
        private SetupState setupState;
        private int setupAgeBars;
        private double impulseStartPrice;
        private double impulseExtremePrice;
        private double pullbackExtremePrice;
        private double reclaimTriggerPrice;

        private double auctionQualityScore;
        private double directionalEfficiencyScore;
        private double overlapRatioScore;
        private double wickRatioScore;
        private double alternatingScore;

        private DateTime lastEntryTime;
        private DateTime lastExitTime;
        private DateTime lastStopTime;
        private double entryPriceRuntime;
        private MarketPosition lastMarketPosition;

        private TimeZoneInfo easternTimeZone;

        // -----------------------------------------------------------------------------------------
        // Ninja lifecycle
        // -----------------------------------------------------------------------------------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "CG PersistenceGovernor v1 with AuctionQualityScore, Acceptance/Rejection, and Pullback/Reclaim continuation entries.";
                Name = "CG_PersistenceGovernor_v1";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StartBehavior = StartBehavior.WaitUntilFlat;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                TimeInForce = TimeInForce.Day;
                TraceOrders = false;
                BarsRequiredToTrade = 50;

                UseInternalTickSeries = true;
                InternalTickBars = 1;

                SyntheticSeconds = 30;
                RangeLookbackBars = 160;
                WarmupSyntheticBars = 30;

                PersistenceDecay = 0.88;
                TrendOwnershipThreshold = 32;
                MinEntryPersistenceScore = 42;
                MinSameDirectionBarsForEntry = 3;

                MinAuctionQuality = 0.58;
                MinDirectionalEfficiency = 0.38;
                MaxOverlapRatio = 0.62;
                MaxWickRatio = 0.58;
                QualityLookbackBars = 8;

                ImpulseBars = 4;
                MinImpulsePoints = 8.0;
                PullbackMinFraction = 0.22;
                PullbackMaxFraction = 0.62;
                ReclaimBufferPoints = 1.25;
                MaxSetupAgeBars = 12;

                LateChaseBlockTicks = 18;
                ExhaustionLookbackBars = 4;
                ExhaustionEfficiencyCollapse = 0.28;

                TargetTicks = 28;
                StopTicks = 22;
                MaxHoldSeconds = 240;
                CooldownSeconds = 120;
                PostStopCooldownSeconds = 180;

                StartTimeEt = 93000;
                EndTimeEt = 155900;
                BlockMiddayIfRotational = true;
                MiddayStartEt = 110000;
                MiddayEndEt = 140000;

                EnableDiagnostics = true;
                DiagnosticEveryNBars = 10;
            }
            else if (State == State.Configure)
            {
                if (UseInternalTickSeries)
                    AddDataSeries(BarsPeriodType.Tick, Math.Max(1, InternalTickBars));

                SetStopLoss("CG_LONG", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("CG_LONG", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CG_SHORT", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("CG_SHORT", CalculationMode.Ticks, TargetTicks);
            }
            else if (State == State.DataLoaded)
            {
                tradeBip = UseInternalTickSeries ? 1 : 0;
                syntheticInitialized = false;
                syntheticBarCount = 0;
                longPersistence = 0;
                shortPersistence = 0;
                sameDirectionCount = 0;
                lastSyntheticDirection = 0;
                auctionState = AuctionState.Unknown;
                biasState = BiasState.None;
                setupState = SetupState.Idle;
                lastEntryTime = Core.Globals.MinDate;
                lastExitTime = Core.Globals.MinDate;
                lastStopTime = Core.Globals.MinDate;
                lastMarketPosition = MarketPosition.Flat;

                try
                {
                    easternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                }
                catch
                {
                    try { easternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
                    catch { easternTimeZone = TimeZoneInfo.Local; }
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != tradeBip)
                return;

            if (CurrentBars[tradeBip] < BarsRequiredToTrade)
                return;

            DateTime now = Times[tradeBip][0];
            double price = Closes[tradeBip][0];
            double high = Highs[tradeBip][0];
            double low = Lows[tradeBip][0];
            double volume = Volumes[tradeBip][0];

            UpdateSyntheticAuction(now, price, high, low, volume);
            ManageOpenPosition(now, price);

            if (!IsRthEt(now))
                return;

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (!CooldownSatisfied(now))
                return;

            if (syntheticBarCount < WarmupSyntheticBars)
                return;

            TryEnterContinuation(now, price);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            string orderName = execution.Order.Name ?? string.Empty;

            if (execution.Order.OrderState == OrderState.Filled)
            {
                if (orderName == "CG_LONG" || orderName == "CG_SHORT")
                {
                    entryPriceRuntime = price;
                    lastEntryTime = time;
                }
                else if (orderName.Contains("Stop loss") || orderName.Contains("STOP") || orderName.Contains("Stop"))
                {
                    lastExitTime = time;
                    lastStopTime = time;
                    ResetSetup();
                }
                else if (orderName.Contains("Profit target") || orderName.Contains("TARGET") || orderName.Contains("Target"))
                {
                    lastExitTime = time;
                    ResetSetup();
                }
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (lastMarketPosition != MarketPosition.Flat && marketPosition == MarketPosition.Flat)
                lastExitTime = Time[0];

            lastMarketPosition = marketPosition;
        }

        // -----------------------------------------------------------------------------------------
        // Synthetic auction construction
        // -----------------------------------------------------------------------------------------
        private void UpdateSyntheticAuction(DateTime now, double price, double high, double low, double volume)
        {
            DateTime bucketStart = AlignToSyntheticBucket(now);

            if (!syntheticInitialized)
            {
                syntheticInitialized = true;
                currentSyntheticStart = bucketStart;
                synOpen = price;
                synHigh = high;
                synLow = low;
                synClose = price;
                synVolume = Math.Max(1, volume);
                return;
            }

            if (bucketStart > currentSyntheticStart)
            {
                FinalizeSyntheticBar();

                currentSyntheticStart = bucketStart;
                synOpen = price;
                synHigh = high;
                synLow = low;
                synClose = price;
                synVolume = Math.Max(1, volume);
            }
            else
            {
                synHigh = Math.Max(synHigh, high);
                synLow = Math.Min(synLow, low);
                synClose = price;
                synVolume += Math.Max(1, volume);
            }
        }

        private DateTime AlignToSyntheticBucket(DateTime dt)
        {
            long ticks = TimeSpan.FromSeconds(SyntheticSeconds).Ticks;
            return new DateTime(dt.Ticks - (dt.Ticks % ticks), dt.Kind);
        }

        private void FinalizeSyntheticBar()
        {
            int idx = syntheticBarCount % MaxSyntheticHistory;
            synO[idx] = synOpen;
            synH[idx] = synHigh;
            synL[idx] = synLow;
            synC[idx] = synClose;
            synV[idx] = synVolume;

            int dir = 0;
            if (synClose > synOpen) dir = 1;
            else if (synClose < synOpen) dir = -1;
            synDir[idx] = dir;

            syntheticBarCount++;

            UpdatePersistence(dir, Math.Abs(synClose - synOpen), Math.Max(TickSize, synHigh - synLow));
            UpdateAuctionClassification();
            UpdateContinuationSetup();

            if (EnableDiagnostics && DiagnosticEveryNBars > 0 && syntheticBarCount % DiagnosticEveryNBars == 0)
            {
                Print(string.Format("{0} CG_PersistenceGovernor_v1 syn={1} state={2} bias={3} q={4:F2} eff={5:F2} overlap={6:F2} wick={7:F2} LP={8:F1} SP={9:F1} setup={10}",
                    currentSyntheticStart, syntheticBarCount, auctionState, biasState, auctionQualityScore,
                    directionalEfficiencyScore, overlapRatioScore, wickRatioScore, longPersistence, shortPersistence, setupState));
            }
        }

        private void UpdatePersistence(int dir, double bodyPoints, double rangePoints)
        {
            double bodyEfficiency = rangePoints <= 0 ? 0 : bodyPoints / rangePoints;
            double impulseWeight = 10.0 + 40.0 * bodyEfficiency;

            longPersistence *= PersistenceDecay;
            shortPersistence *= PersistenceDecay;

            if (dir > 0)
                longPersistence += impulseWeight;
            else if (dir < 0)
                shortPersistence += impulseWeight;

            if (dir != 0 && dir == lastSyntheticDirection)
                sameDirectionCount++;
            else if (dir != 0)
                sameDirectionCount = 1;
            else
                sameDirectionCount = 0;

            if (dir != 0)
                lastSyntheticDirection = dir;
        }

        // -----------------------------------------------------------------------------------------
        // Auction classification and quality scoring
        // -----------------------------------------------------------------------------------------
        private void UpdateAuctionClassification()
        {
            if (syntheticBarCount < Math.Max(QualityLookbackBars + 2, ImpulseBars + 2))
            {
                auctionState = AuctionState.Unknown;
                biasState = BiasState.None;
                return;
            }

            directionalEfficiencyScore = ComputeDirectionalEfficiency(QualityLookbackBars);
            overlapRatioScore = ComputeOverlapRatio(QualityLookbackBars);
            wickRatioScore = ComputeWickRatio(QualityLookbackBars);
            alternatingScore = ComputeAlternatingScore(QualityLookbackBars);

            double overlapQuality = 1.0 - Clamp01(overlapRatioScore);
            double wickQuality = 1.0 - Clamp01(wickRatioScore);
            double alternationQuality = 1.0 - Clamp01(alternatingScore);

            auctionQualityScore = Clamp01(
                directionalEfficiencyScore * 0.42 +
                overlapQuality * 0.28 +
                wickQuality * 0.18 +
                alternationQuality * 0.12);

            bool lowQuality = auctionQualityScore < MinAuctionQuality ||
                              directionalEfficiencyScore < MinDirectionalEfficiency ||
                              overlapRatioScore > MaxOverlapRatio ||
                              wickRatioScore > MaxWickRatio;

            bool longOwned = longPersistence >= TrendOwnershipThreshold && longPersistence > shortPersistence * 1.20;
            bool shortOwned = shortPersistence >= TrendOwnershipThreshold && shortPersistence > longPersistence * 1.20;

            bool exhaustionUp = IsExhaustion(+1);
            bool exhaustionDown = IsExhaustion(-1);

            if (exhaustionUp)
                auctionState = AuctionState.ExhaustionUp;
            else if (exhaustionDown)
                auctionState = AuctionState.ExhaustionDown;
            else if (lowQuality && alternatingScore > 0.45)
                auctionState = AuctionState.Rotation;
            else if (lowQuality)
                auctionState = AuctionState.Balance;
            else if (longOwned && directionalEfficiencyScore >= MinDirectionalEfficiency)
                auctionState = sameDirectionCount >= MinSameDirectionBarsForEntry ? AuctionState.TrendUp : AuctionState.DiscoveryUp;
            else if (shortOwned && directionalEfficiencyScore >= MinDirectionalEfficiency)
                auctionState = sameDirectionCount >= MinSameDirectionBarsForEntry ? AuctionState.TrendDown : AuctionState.DiscoveryDown;
            else
                auctionState = AuctionState.Compression;

            if (auctionState == AuctionState.TrendUp || auctionState == AuctionState.DiscoveryUp)
                biasState = BiasState.LongOnly;
            else if (auctionState == AuctionState.TrendDown || auctionState == AuctionState.DiscoveryDown)
                biasState = BiasState.ShortOnly;
            else
                biasState = BiasState.None;
        }

        private double ComputeDirectionalEfficiency(int lookback)
        {
            if (syntheticBarCount < lookback + 1)
                return 0;

            double firstClose = GetSynC(lookback);
            double lastClose = GetSynC(1);
            double net = Math.Abs(lastClose - firstClose);
            double gross = 0;

            for (int i = lookback; i >= 1; i--)
                gross += Math.Abs(GetSynC(i) - GetSynO(i));

            if (gross <= TickSize)
                return 0;

            return Clamp01(net / gross);
        }

        private double ComputeOverlapRatio(int lookback)
        {
            if (syntheticBarCount < lookback + 1)
                return 1;

            double overlapSum = 0;
            double rangeSum = 0;
            int pairs = 0;

            for (int i = lookback; i >= 2; i--)
            {
                double h1 = GetSynH(i);
                double l1 = GetSynL(i);
                double h2 = GetSynH(i - 1);
                double l2 = GetSynL(i - 1);
                double overlap = Math.Max(0, Math.Min(h1, h2) - Math.Max(l1, l2));
                double avgRange = Math.Max(TickSize, ((h1 - l1) + (h2 - l2)) * 0.5);
                overlapSum += overlap / avgRange;
                rangeSum += 1.0;
                pairs++;
            }

            return pairs == 0 ? 1 : Clamp01(overlapSum / rangeSum);
        }

        private double ComputeWickRatio(int lookback)
        {
            if (syntheticBarCount < lookback + 1)
                return 1;

            double wickSum = 0;
            int n = 0;

            for (int i = lookback; i >= 1; i--)
            {
                double o = GetSynO(i);
                double h = GetSynH(i);
                double l = GetSynL(i);
                double c = GetSynC(i);
                double range = Math.Max(TickSize, h - l);
                double body = Math.Abs(c - o);
                double wick = Math.Max(0, range - body);
                wickSum += wick / range;
                n++;
            }

            return n == 0 ? 1 : Clamp01(wickSum / n);
        }

        private double ComputeAlternatingScore(int lookback)
        {
            if (syntheticBarCount < lookback + 1)
                return 1;

            int flips = 0;
            int comparisons = 0;
            int prev = GetSynDir(lookback);

            for (int i = lookback - 1; i >= 1; i--)
            {
                int cur = GetSynDir(i);
                if (prev != 0 && cur != 0)
                {
                    comparisons++;
                    if (cur != prev)
                        flips++;
                }
                if (cur != 0)
                    prev = cur;
            }

            return comparisons == 0 ? 0 : Clamp01((double)flips / comparisons);
        }

        private bool IsExhaustion(int direction)
        {
            if (syntheticBarCount < ExhaustionLookbackBars + 2)
                return false;

            double recentEfficiency = ComputeDirectionalEfficiency(Math.Max(2, ExhaustionLookbackBars));
            double move = GetSynC(1) - GetSynC(ExhaustionLookbackBars);
            bool movedInDirection = direction > 0 ? move > MinImpulsePoints * 0.75 : move < -MinImpulsePoints * 0.75;
            bool efficiencyCollapsed = recentEfficiency <= ExhaustionEfficiencyCollapse;
            bool bigWicks = ComputeWickRatio(Math.Max(2, ExhaustionLookbackBars)) > MaxWickRatio;

            return movedInDirection && efficiencyCollapsed && bigWicks;
        }

        // -----------------------------------------------------------------------------------------
        // Continuation setup state machine
        // -----------------------------------------------------------------------------------------
        private void UpdateContinuationSetup()
        {
            if (auctionState == AuctionState.Rotation || auctionState == AuctionState.Balance ||
                auctionState == AuctionState.ExhaustionUp || auctionState == AuctionState.ExhaustionDown ||
                biasState == BiasState.None)
            {
                ResetSetup();
                return;
            }

            if (setupState != SetupState.Idle)
            {
                setupAgeBars++;
                if (setupAgeBars > MaxSetupAgeBars)
                {
                    ResetSetup();
                    return;
                }
            }

            double impulseMove = GetSynC(1) - GetSynC(ImpulseBars);
            double recentHigh = HighestSynHigh(ImpulseBars);
            double recentLow = LowestSynLow(ImpulseBars);

            if (biasState == BiasState.LongOnly)
            {
                if (setupState == SetupState.Idle && impulseMove >= MinImpulsePoints && longPersistence >= MinEntryPersistenceScore)
                {
                    setupState = SetupState.LongImpulseSeen;
                    setupAgeBars = 0;
                    impulseStartPrice = GetSynC(ImpulseBars);
                    impulseExtremePrice = recentHigh;
                    pullbackExtremePrice = GetSynC(1);
                    reclaimTriggerPrice = 0;
                    return;
                }

                if (setupState == SetupState.LongImpulseSeen || setupState == SetupState.LongPullbackSeen)
                {
                    impulseExtremePrice = Math.Max(impulseExtremePrice, recentHigh);
                    pullbackExtremePrice = Math.Min(pullbackExtremePrice, GetSynL(1));
                    double impulseSize = Math.Max(TickSize, impulseExtremePrice - impulseStartPrice);
                    double pullback = impulseExtremePrice - pullbackExtremePrice;
                    double pullbackFraction = pullback / impulseSize;

                    if (pullbackFraction >= PullbackMinFraction && pullbackFraction <= PullbackMaxFraction)
                    {
                        setupState = SetupState.LongPullbackSeen;
                        reclaimTriggerPrice = GetSynH(1) + ReclaimBufferPoints;
                    }
                    else if (pullbackFraction > PullbackMaxFraction)
                    {
                        ResetSetup();
                    }
                }
            }
            else if (biasState == BiasState.ShortOnly)
            {
                if (setupState == SetupState.Idle && impulseMove <= -MinImpulsePoints && shortPersistence >= MinEntryPersistenceScore)
                {
                    setupState = SetupState.ShortImpulseSeen;
                    setupAgeBars = 0;
                    impulseStartPrice = GetSynC(ImpulseBars);
                    impulseExtremePrice = recentLow;
                    pullbackExtremePrice = GetSynC(1);
                    reclaimTriggerPrice = 0;
                    return;
                }

                if (setupState == SetupState.ShortImpulseSeen || setupState == SetupState.ShortPullbackSeen)
                {
                    impulseExtremePrice = Math.Min(impulseExtremePrice, recentLow);
                    pullbackExtremePrice = Math.Max(pullbackExtremePrice, GetSynH(1));
                    double impulseSize = Math.Max(TickSize, impulseStartPrice - impulseExtremePrice);
                    double pullback = pullbackExtremePrice - impulseExtremePrice;
                    double pullbackFraction = pullback / impulseSize;

                    if (pullbackFraction >= PullbackMinFraction && pullbackFraction <= PullbackMaxFraction)
                    {
                        setupState = SetupState.ShortPullbackSeen;
                        reclaimTriggerPrice = GetSynL(1) - ReclaimBufferPoints;
                    }
                    else if (pullbackFraction > PullbackMaxFraction)
                    {
                        ResetSetup();
                    }
                }
            }
        }

        private void TryEnterContinuation(DateTime now, double price)
        {
            if (auctionQualityScore < MinAuctionQuality)
                return;

            if (directionalEfficiencyScore < MinDirectionalEfficiency)
                return;

            if (overlapRatioScore > MaxOverlapRatio || wickRatioScore > MaxWickRatio)
                return;

            if (BlockMiddayIfRotational && IsMiddayEt(now) &&
                (auctionState == AuctionState.Rotation || auctionState == AuctionState.Balance || auctionQualityScore < MinAuctionQuality + 0.10))
                return;

            if (IsLateChase(+1, price) && biasState == BiasState.LongOnly)
                return;

            if (IsLateChase(-1, price) && biasState == BiasState.ShortOnly)
                return;

            if (setupState == SetupState.LongPullbackSeen && biasState == BiasState.LongOnly)
            {
                if (price >= reclaimTriggerPrice && longPersistence >= MinEntryPersistenceScore && sameDirectionCount >= MinSameDirectionBarsForEntry)
                {
                    EnterLong(1, "CG_LONG");
                    ResetSetup();
                }
            }
            else if (setupState == SetupState.ShortPullbackSeen && biasState == BiasState.ShortOnly)
            {
                if (price <= reclaimTriggerPrice && shortPersistence >= MinEntryPersistenceScore && sameDirectionCount >= MinSameDirectionBarsForEntry)
                {
                    EnterShort(1, "CG_SHORT");
                    ResetSetup();
                }
            }
        }

        private bool IsLateChase(int direction, double price)
        {
            if (syntheticBarCount < ExhaustionLookbackBars + 2)
                return false;

            double recentHigh = HighestSynHigh(ExhaustionLookbackBars);
            double recentLow = LowestSynLow(ExhaustionLookbackBars);
            double chaseDistanceTicks = direction > 0
                ? (price - recentLow) / TickSize
                : (recentHigh - price) / TickSize;

            return chaseDistanceTicks >= LateChaseBlockTicks &&
                   (auctionState == AuctionState.ExhaustionUp || auctionState == AuctionState.ExhaustionDown || wickRatioScore > MaxWickRatio * 0.95);
        }

        private void ManageOpenPosition(DateTime now, double price)
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            if (lastEntryTime != Core.Globals.MinDate && (now - lastEntryTime).TotalSeconds >= MaxHoldSeconds)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("CG_TIMEOUT_LONG", "CG_LONG");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("CG_TIMEOUT_SHORT", "CG_SHORT");
                return;
            }

            // Advisory early exit: if auction quality collapses while in position, flatten.
            // Protective OCO remains the primary crash-safe risk layer.
            if (syntheticBarCount >= WarmupSyntheticBars && auctionQualityScore < Math.Max(0.20, MinAuctionQuality - 0.20))
            {
                if (Position.MarketPosition == MarketPosition.Long && auctionState != AuctionState.TrendUp && auctionState != AuctionState.DiscoveryUp)
                    ExitLong("CG_QUALITY_EXIT_LONG", "CG_LONG");
                else if (Position.MarketPosition == MarketPosition.Short && auctionState != AuctionState.TrendDown && auctionState != AuctionState.DiscoveryDown)
                    ExitShort("CG_QUALITY_EXIT_SHORT", "CG_SHORT");
            }
        }

        // -----------------------------------------------------------------------------------------
        // Time/cooldown helpers
        // -----------------------------------------------------------------------------------------
        private bool CooldownSatisfied(DateTime now)
        {
            if (lastExitTime != Core.Globals.MinDate && (now - lastExitTime).TotalSeconds < CooldownSeconds)
                return false;

            if (lastStopTime != Core.Globals.MinDate && (now - lastStopTime).TotalSeconds < PostStopCooldownSeconds)
                return false;

            return true;
        }

        private bool IsRthEt(DateTime localTime)
        {
            int t = ToEtTimeInt(localTime);
            return t >= StartTimeEt && t <= EndTimeEt;
        }

        private bool IsMiddayEt(DateTime localTime)
        {
            int t = ToEtTimeInt(localTime);
            return t >= MiddayStartEt && t <= MiddayEndEt;
        }

        private int ToEtTimeInt(DateTime localTime)
        {
            DateTime et;
            try
            {
                et = TimeZoneInfo.ConvertTime(localTime, TimeZoneInfo.Local, easternTimeZone);
            }
            catch
            {
                et = localTime;
            }
            return et.Hour * 10000 + et.Minute * 100 + et.Second;
        }

        // -----------------------------------------------------------------------------------------
        // Synthetic history helpers. barsAgo=1 means most recently finalized synthetic bar.
        // -----------------------------------------------------------------------------------------
        private int HistIndex(int barsAgo)
        {
            int raw = syntheticBarCount - barsAgo;
            while (raw < 0) raw += MaxSyntheticHistory;
            return raw % MaxSyntheticHistory;
        }

        private double GetSynO(int barsAgo) { return synO[HistIndex(barsAgo)]; }
        private double GetSynH(int barsAgo) { return synH[HistIndex(barsAgo)]; }
        private double GetSynL(int barsAgo) { return synL[HistIndex(barsAgo)]; }
        private double GetSynC(int barsAgo) { return synC[HistIndex(barsAgo)]; }
        private int GetSynDir(int barsAgo) { return synDir[HistIndex(barsAgo)]; }

        private double HighestSynHigh(int bars)
        {
            double v = double.MinValue;
            for (int i = 1; i <= bars; i++)
                v = Math.Max(v, GetSynH(i));
            return v;
        }

        private double LowestSynLow(int bars)
        {
            double v = double.MaxValue;
            for (int i = 1; i <= bars; i++)
                v = Math.Min(v, GetSynL(i));
            return v;
        }

        private void ResetSetup()
        {
            setupState = SetupState.Idle;
            setupAgeBars = 0;
            impulseStartPrice = 0;
            impulseExtremePrice = 0;
            pullbackExtremePrice = 0;
            reclaimTriggerPrice = 0;
        }

        private double Clamp01(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x))
                return 0;
            if (x < 0) return 0;
            if (x > 1) return 1;
            return x;
        }
    }
}
