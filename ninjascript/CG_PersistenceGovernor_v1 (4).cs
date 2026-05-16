#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

// =================================================================================================
// CG_PersistenceGovernor_v1.cs
// NinjaTrader 8 Strategy
// Full generated file timestamp: 2026-05-08 05:18:00 UTC
//
// MNQ Intraday Strategy — Persistence Governor + Auction Quality + BOS Campaign + Execution Cost Model
//
// WHY THIS VERSION EXISTS
// -----------------------
// The prior auction-quality/pullback version became too selective: it blocked chop better, but it
// also failed to participate in a visible morning break-of-structure rally after one early short.
// This version keeps the rotation/exhaustion protections, but adds a dedicated structural campaign
// engine so the strategy can enter obvious trend/BOS sequences without waiting forever for a perfect
// textbook pullback.
//
// CORE DESIGN
// -----------
// 1. Synthetic auction bars are built internally from the selected trade series.
// 2. Auction quality still blocks low-efficiency rotational chop.
// 3. Swing structure is detected from finalized synthetic bars.
// 4. Break-of-Structure (BOS) establishes campaign direction:
//      - break above prior synthetic swing high => LONG campaign
//      - break below prior synthetic swing low  => SHORT campaign
// 5. Entries can occur by either path:
//      A. Pullback/Reclaim continuation entry.
//      B. BOS campaign entry when structure is breaking cleanly and quality/persistence are adequate.
// 6. One MNQ only. No overlap. OCO stop/target protection via SetStopLoss/SetProfitTarget.
// 7. Execution reality layer adds conservative bracket penalties plus friction-adjusted telemetry for
//    commission, entry slippage, target-exit slippage, stop-exit slippage, and fast-market extra slippage.
//
// PRACTICAL DEFAULTS
// ------------------
// The defaults are intentionally more permissive than the prior file so the strategy will actually
// participate in strong morning structure. Suggested starting chart: MNQ 1000 Tick or 1 Tick. The
// strategy also adds a 1-tick internal series by default and processes there.
//
// IMPORTANT
// ---------
// Test only in Playback101 first. This is not financial advice and not production-ready.
// =================================================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_PersistenceGovernor_v1 : Strategy
    {
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

        private enum CampaignState
        {
            None,
            LongCampaign,
            ShortCampaign
        }

        // -----------------------------------------------------------------------------------------
        // 01. Series / execution parameters
        // -----------------------------------------------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Use Internal Tick Series", GroupName = "01. Series", Order = 0)]
        public bool UseInternalTickSeries { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "Internal Tick Bars", GroupName = "01. Series", Order = 1)]
        public int InternalTickBars { get; set; }

        // -----------------------------------------------------------------------------------------
        // 02. Synthetic auction parameters
        // -----------------------------------------------------------------------------------------
        [NinjaScriptProperty]
        [Range(5, 300)]
        [Display(Name = "Synthetic Seconds", GroupName = "02. Synthetic Auction", Order = 0)]
        public int SyntheticSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Warmup Synthetic Bars", GroupName = "02. Synthetic Auction", Order = 1)]
        public int WarmupSyntheticBars { get; set; }

        // -----------------------------------------------------------------------------------------
        // 03. Persistence parameters
        // -----------------------------------------------------------------------------------------
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

        // -----------------------------------------------------------------------------------------
        // 04. Auction quality parameters
        // -----------------------------------------------------------------------------------------
        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "Min Auction Quality", GroupName = "04. Auction Quality", Order = 0)]
        public double MinAuctionQuality { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "BOS Min Auction Quality", GroupName = "04. Auction Quality", Order = 1)]
        public double BosMinAuctionQuality { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "Min Directional Efficiency", GroupName = "04. Auction Quality", Order = 2)]
        public double MinDirectionalEfficiency { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "Max Overlap Ratio", GroupName = "04. Auction Quality", Order = 3)]
        public double MaxOverlapRatio { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "Max Wick Ratio", GroupName = "04. Auction Quality", Order = 4)]
        public double MaxWickRatio { get; set; }

        [NinjaScriptProperty]
        [Range(2, 30)]
        [Display(Name = "Quality Lookback Synthetic Bars", GroupName = "04. Auction Quality", Order = 5)]
        public int QualityLookbackBars { get; set; }

        // -----------------------------------------------------------------------------------------
        // 05. Continuation setup parameters
        // -----------------------------------------------------------------------------------------
        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Impulse Bars", GroupName = "05. Pullback/Reclaim", Order = 0)]
        public int ImpulseBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 100.0)]
        [Display(Name = "Min Impulse Points", GroupName = "05. Pullback/Reclaim", Order = 1)]
        public double MinImpulsePoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.10, 0.95)]
        [Display(Name = "Pullback Min Fraction", GroupName = "05. Pullback/Reclaim", Order = 2)]
        public double PullbackMinFraction { get; set; }

        [NinjaScriptProperty]
        [Range(0.10, 0.95)]
        [Display(Name = "Pullback Max Fraction", GroupName = "05. Pullback/Reclaim", Order = 3)]
        public double PullbackMaxFraction { get; set; }

        [NinjaScriptProperty]
        [Range(0.00, 20.0)]
        [Display(Name = "Reclaim Buffer Points", GroupName = "05. Pullback/Reclaim", Order = 4)]
        public double ReclaimBufferPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 40)]
        [Display(Name = "Max Setup Age Synthetic Bars", GroupName = "05. Pullback/Reclaim", Order = 5)]
        public int MaxSetupAgeBars { get; set; }

        // -----------------------------------------------------------------------------------------
        // 06. Break-of-structure campaign parameters
        // -----------------------------------------------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Enable BOS Campaign Entries", GroupName = "06. BOS Campaign", Order = 0)]
        public bool EnableBosCampaignEntries { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Swing Strength Synthetic Bars", GroupName = "06. BOS Campaign", Order = 1)]
        public int SwingStrengthBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.00, 20.0)]
        [Display(Name = "BOS Break Buffer Points", GroupName = "06. BOS Campaign", Order = 2)]
        public double BosBreakBufferPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 100.0)]
        [Display(Name = "Min BOS Swing Size Points", GroupName = "06. BOS Campaign", Order = 3)]
        public double MinBosSwingSizePoints { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "BOS Cooldown Synthetic Bars", GroupName = "06. BOS Campaign", Order = 4)]
        public int BosCooldownSyntheticBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Min BOS Count For Campaign", GroupName = "06. BOS Campaign", Order = 5)]
        public int MinBosCountForCampaign { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max Entry Distance From BOS Ticks", GroupName = "06. BOS Campaign", Order = 6)]
        public int MaxEntryDistanceFromBosTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow BOS Stop And Reverse", GroupName = "06. BOS Campaign", Order = 7)]
        public bool AllowBosStopAndReverse { get; set; }

        [NinjaScriptProperty]
        [Range(0, 600)]
        [Display(Name = "BOS Stop Reverse Cooldown Seconds", GroupName = "06. BOS Campaign", Order = 8)]
        public int BosStopReverseCooldownSeconds { get; set; }

        // -----------------------------------------------------------------------------------------
        // 07. Exhaustion controls
        // -----------------------------------------------------------------------------------------
        [NinjaScriptProperty]
        [Range(1, 80)]
        [Display(Name = "Late Chase Block Ticks", GroupName = "07. Exhaustion", Order = 0)]
        public int LateChaseBlockTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Exhaustion Lookback Bars", GroupName = "07. Exhaustion", Order = 1)]
        public int ExhaustionLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "Exhaustion Efficiency Collapse", GroupName = "07. Exhaustion", Order = 2)]
        public double ExhaustionEfficiencyCollapse { get; set; }

        // -----------------------------------------------------------------------------------------
        // 08. Risk parameters
        // -----------------------------------------------------------------------------------------
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Target Ticks", GroupName = "08. Risk", Order = 0)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Stop Ticks", GroupName = "08. Risk", Order = 1)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(10, 1800)]
        [Display(Name = "Max Hold Seconds", GroupName = "08. Risk", Order = 2)]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1800)]
        [Display(Name = "Cooldown Seconds", GroupName = "08. Risk", Order = 3)]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "Post Stop Cooldown Seconds", GroupName = "08. Risk", Order = 4)]
        public int PostStopCooldownSeconds { get; set; }

        // -----------------------------------------------------------------------------------------
        // 09. Execution cost model
        // -----------------------------------------------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Enable Execution Cost Model", GroupName = "09. Execution Costs", Order = 0)]
        public bool EnableExecutionCostModel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Apply Conservative Bracket Costs", GroupName = "09. Execution Costs", Order = 1)]
        public bool ApplyConservativeBracketCosts { get; set; }

        [NinjaScriptProperty]
        [Range(0.00, 10.00)]
        [Display(Name = "Commission Round Turn USD", GroupName = "09. Execution Costs", Order = 2)]
        public double CommissionRoundTurnUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0.10, 10.00)]
        [Display(Name = "MNQ Tick Value USD", GroupName = "09. Execution Costs", Order = 3)]
        public double MnqTickValueUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Entry Slippage Ticks", GroupName = "09. Execution Costs", Order = 4)]
        public int EntrySlippageTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Target Exit Slippage Ticks", GroupName = "09. Execution Costs", Order = 5)]
        public int TargetExitSlippageTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 30)]
        [Display(Name = "Stop Exit Slippage Ticks", GroupName = "09. Execution Costs", Order = 6)]
        public int StopExitSlippageTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 30)]
        [Display(Name = "Fast Market Extra Slippage Ticks", GroupName = "09. Execution Costs", Order = 7)]
        public int FastMarketExtraSlippageTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "Fast Market Efficiency Threshold", GroupName = "09. Execution Costs", Order = 8)]
        public double FastMarketEfficiencyThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.00)]
        [Display(Name = "Fast Market Quality Threshold", GroupName = "09. Execution Costs", Order = 9)]
        public double FastMarketQualityThreshold { get; set; }

        // -----------------------------------------------------------------------------------------
        // 10. Time controls
        // -----------------------------------------------------------------------------------------
        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Start Time ET", GroupName = "09. Time", Order = 0)]
        public int StartTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "End Time ET", GroupName = "09. Time", Order = 1)]
        public int EndTimeEt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Block Midday If Rotational", GroupName = "09. Time", Order = 2)]
        public bool BlockMiddayIfRotational { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Midday Start ET", GroupName = "09. Time", Order = 3)]
        public int MiddayStartEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Midday End ET", GroupName = "09. Time", Order = 4)]
        public int MiddayEndEt { get; set; }

        // -----------------------------------------------------------------------------------------
        // 11. Diagnostics
        // -----------------------------------------------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Enable Diagnostics", GroupName = "11. Diagnostics", Order = 0)]
        public bool EnableDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Diagnostic Print Every N Synthetic Bars", GroupName = "11. Diagnostics", Order = 1)]
        public int DiagnosticEveryNBars { get; set; }

        // Runtime fields
        private int tradeBip;
        private DateTime currentSyntheticStart;
        private bool syntheticInitialized;
        private double synOpen;
        private double synHigh;
        private double synLow;
        private double synClose;
        private double synVolume;
        private int syntheticBarCount;

        private const int MaxSyntheticHistory = 1024;
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
        private CampaignState campaignState;

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

        private double lastSwingHigh;
        private double lastSwingLow;
        private int lastSwingHighBar;
        private int lastSwingLowBar;
        private double lastBosLevel;
        private int lastBosDirection;
        private int lastBosSyntheticBar;
        private int longBosCount;
        private int shortBosCount;

        private DateTime lastEntryTime;
        private DateTime lastExitTime;
        private DateTime lastStopTime;
        private double entryPriceRuntime;
        private int entryDirectionRuntime;
        private double modelCumulativeNetUsd;
        private double modelDailyNetUsd;
        private DateTime modelDailyDate;
        private int modelTradeCount;
        private int effectiveTargetTicksRuntime;
        private int effectiveStopTicksRuntime;
        private MarketPosition lastMarketPosition;
        private TimeZoneInfo easternTimeZone;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "PersistenceGovernor with auction quality, pullback/reclaim, and BOS campaign entries.";
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
                WarmupSyntheticBars = 12;

                PersistenceDecay = 0.88;
                TrendOwnershipThreshold = 26;
                MinEntryPersistenceScore = 30;
                MinSameDirectionBarsForEntry = 2;

                MinAuctionQuality = 0.46;
                BosMinAuctionQuality = 0.38;
                MinDirectionalEfficiency = 0.28;
                MaxOverlapRatio = 0.76;
                MaxWickRatio = 0.72;
                QualityLookbackBars = 8;

                ImpulseBars = 4;
                MinImpulsePoints = 6.0;
                PullbackMinFraction = 0.18;
                PullbackMaxFraction = 0.72;
                ReclaimBufferPoints = 0.75;
                MaxSetupAgeBars = 14;

                EnableBosCampaignEntries = true;
                SwingStrengthBars = 2;
                BosBreakBufferPoints = 0.75;
                MinBosSwingSizePoints = 5.0;
                BosCooldownSyntheticBars = 2;
                MinBosCountForCampaign = 1;
                MaxEntryDistanceFromBosTicks = 14;
                AllowBosStopAndReverse = true;
                BosStopReverseCooldownSeconds = 45;

                LateChaseBlockTicks = 28;
                ExhaustionLookbackBars = 4;
                ExhaustionEfficiencyCollapse = 0.22;

                TargetTicks = 28;
                StopTicks = 22;
                MaxHoldSeconds = 240;
                CooldownSeconds = 45;
                PostStopCooldownSeconds = 75;

                EnableExecutionCostModel = true;
                ApplyConservativeBracketCosts = true;
                CommissionRoundTurnUsd = 1.00;
                MnqTickValueUsd = 0.50;
                EntrySlippageTicks = 1;
                TargetExitSlippageTicks = 1;
                StopExitSlippageTicks = 2;
                FastMarketExtraSlippageTicks = 2;
                FastMarketEfficiencyThreshold = 0.72;
                FastMarketQualityThreshold = 0.62;

                StartTimeEt = 93000;
                EndTimeEt = 155900;
                BlockMiddayIfRotational = true;
                MiddayStartEt = 110000;
                MiddayEndEt = 140000;

                EnableDiagnostics = true;
                DiagnosticEveryNBars = 5;
            }
            else if (State == State.Configure)
            {
                if (UseInternalTickSeries)
                    AddDataSeries(BarsPeriodType.Tick, Math.Max(1, InternalTickBars));

                effectiveTargetTicksRuntime = EffectiveTargetTicks();
                effectiveStopTicksRuntime = EffectiveStopTicks();

                // NT's built-in Slippage property is honored mainly by historical/backtest fill engines.
                // Playback fills may still look idealized, so this strategy also keeps a separate
                // friction-adjusted model ledger in OnExecutionUpdate.
                Slippage = EnableExecutionCostModel ? Math.Max(0, EntrySlippageTicks) : 0;

                SetStopLoss("CG_LONG", CalculationMode.Ticks, effectiveStopTicksRuntime, false);
                SetProfitTarget("CG_LONG", CalculationMode.Ticks, effectiveTargetTicksRuntime);
                SetStopLoss("CG_SHORT", CalculationMode.Ticks, effectiveStopTicksRuntime, false);
                SetProfitTarget("CG_SHORT", CalculationMode.Ticks, effectiveTargetTicksRuntime);
            }
            else if (State == State.DataLoaded)
            {
                tradeBip = UseInternalTickSeries ? 1 : 0;
                ResetRuntime();

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

        private void ResetRuntime()
        {
            syntheticInitialized = false;
            syntheticBarCount = 0;
            longPersistence = 0;
            shortPersistence = 0;
            sameDirectionCount = 0;
            lastSyntheticDirection = 0;
            auctionState = AuctionState.Unknown;
            biasState = BiasState.None;
            setupState = SetupState.Idle;
            campaignState = CampaignState.None;
            lastSwingHigh = 0;
            lastSwingLow = 0;
            lastSwingHighBar = -1;
            lastSwingLowBar = -1;
            lastBosLevel = 0;
            lastBosDirection = 0;
            lastBosSyntheticBar = -999999;
            longBosCount = 0;
            shortBosCount = 0;
            lastEntryTime = Core.Globals.MinDate;
            lastExitTime = Core.Globals.MinDate;
            lastStopTime = Core.Globals.MinDate;
            entryPriceRuntime = 0;
            entryDirectionRuntime = 0;
            modelCumulativeNetUsd = 0;
            modelDailyNetUsd = 0;
            modelDailyDate = Core.Globals.MinDate;
            modelTradeCount = 0;
            effectiveTargetTicksRuntime = EffectiveTargetTicks();
            effectiveStopTicksRuntime = EffectiveStopTicks();
            lastMarketPosition = MarketPosition.Flat;
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

            TryEnter(now, price);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            if (execution.Order.OrderState != OrderState.Filled)
                return;

            string orderName = execution.Order.Name ?? string.Empty;

            if (orderName == "CG_LONG" || orderName == "CG_SHORT")
            {
                entryPriceRuntime = price;
                entryDirectionRuntime = orderName == "CG_LONG" ? 1 : -1;
                lastEntryTime = time;
            }
            else if (orderName.Contains("Stop") || orderName.Contains("STOP") || orderName.Contains("loss"))
            {
                UpdateExecutionCostLedger(price, time, true);
                lastExitTime = time;
                lastStopTime = time;
                ResetSetup();
            }
            else if (orderName.Contains("Target") || orderName.Contains("TARGET") || orderName.Contains("Profit"))
            {
                UpdateExecutionCostLedger(price, time, false);
                lastExitTime = time;
                ResetSetup();
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (lastMarketPosition != MarketPosition.Flat && marketPosition == MarketPosition.Flat)
                lastExitTime = Time[0];

            lastMarketPosition = marketPosition;
        }

        // -----------------------------------------------------------------------------------------
        // Execution cost model
        // -----------------------------------------------------------------------------------------
        private int EffectiveTargetTicks()
        {
            if (!EnableExecutionCostModel || !ApplyConservativeBracketCosts)
                return Math.Max(1, TargetTicks);

            int frictionTicks = Math.Max(0, EntrySlippageTicks) + Math.Max(0, TargetExitSlippageTicks);
            return Math.Max(2, TargetTicks - frictionTicks);
        }

        private int EffectiveStopTicks()
        {
            if (!EnableExecutionCostModel || !ApplyConservativeBracketCosts)
                return Math.Max(1, StopTicks);

            int frictionTicks = Math.Max(0, EntrySlippageTicks) + Math.Max(0, StopExitSlippageTicks);
            return Math.Max(2, StopTicks + frictionTicks);
        }

        private void UpdateExecutionCostLedger(double exitPrice, DateTime exitTime, bool isStopExit)
        {
            if (!EnableExecutionCostModel || entryDirectionRuntime == 0 || entryPriceRuntime <= 0 || TickSize <= 0)
                return;

            DateTime exitEt = ToEastern(exitTime);
            if (modelDailyDate == Core.Globals.MinDate || modelDailyDate.Date != exitEt.Date)
            {
                modelDailyDate = exitEt.Date;
                modelDailyNetUsd = 0;
            }

            double grossTicks = entryDirectionRuntime * (exitPrice - entryPriceRuntime) / TickSize;
            int dynamicExtraTicks = IsFastMarketForCostModel() ? Math.Max(0, FastMarketExtraSlippageTicks) : 0;
            int exitSlipTicks = isStopExit ? Math.Max(0, StopExitSlippageTicks) : Math.Max(0, TargetExitSlippageTicks);
            double commissionTicks = MnqTickValueUsd > 0 ? CommissionRoundTurnUsd / MnqTickValueUsd : 0.0;
            double totalFrictionTicks = Math.Max(0, EntrySlippageTicks) + exitSlipTicks + dynamicExtraTicks + commissionTicks;
            double modeledNetTicks = grossTicks - totalFrictionTicks;
            double modeledNetUsd = modeledNetTicks * Math.Max(0.01, MnqTickValueUsd);

            modelTradeCount++;
            modelCumulativeNetUsd += modeledNetUsd;
            modelDailyNetUsd += modeledNetUsd;

            if (EnableDiagnostics)
            {
                Print(string.Format(
                    "{0} CG_COST_MODEL trade={1} side={2} exit={3} grossTicks={4:F1} frictionTicks={5:F1} netTicks={6:F1} netUsd={7:F2} dayUsd={8:F2} cumUsd={9:F2} targetTicks={10} stopTicks={11} q={12:F2} eff={13:F2}",
                    exitTime,
                    modelTradeCount,
                    entryDirectionRuntime > 0 ? "LONG" : "SHORT",
                    isStopExit ? "STOP" : "TARGET",
                    grossTicks,
                    totalFrictionTicks,
                    modeledNetTicks,
                    modeledNetUsd,
                    modelDailyNetUsd,
                    modelCumulativeNetUsd,
                    effectiveTargetTicksRuntime,
                    effectiveStopTicksRuntime,
                    auctionQualityScore,
                    directionalEfficiencyScore));
            }

            entryDirectionRuntime = 0;
            entryPriceRuntime = 0;
        }

        private bool IsFastMarketForCostModel()
        {
            return auctionQualityScore >= FastMarketQualityThreshold ||
                   directionalEfficiencyScore >= FastMarketEfficiencyThreshold ||
                   auctionState == AuctionState.DiscoveryUp ||
                   auctionState == AuctionState.DiscoveryDown ||
                   auctionState == AuctionState.TrendUp ||
                   auctionState == AuctionState.TrendDown;
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
            UpdateSwingStructure();
            UpdateAuctionClassification();
            UpdateContinuationSetup();

            if (EnableDiagnostics && DiagnosticEveryNBars > 0 && syntheticBarCount % DiagnosticEveryNBars == 0)
            {
                Print(string.Format("{0} CG_PersistenceGovernor_v1 syn={1} state={2} bias={3} campaign={4} q={5:F2} eff={6:F2} ov={7:F2} wick={8:F2} LP={9:F1} SP={10:F1} sh={11:F2} sl={12:F2} bosDir={13} setup={14}",
                    currentSyntheticStart, syntheticBarCount, auctionState, biasState, campaignState,
                    auctionQualityScore, directionalEfficiencyScore, overlapRatioScore, wickRatioScore,
                    longPersistence, shortPersistence, lastSwingHigh, lastSwingLow, lastBosDirection, setupState));
            }
        }

        private void UpdatePersistence(int dir, double bodyPoints, double rangePoints)
        {
            double bodyEfficiency = rangePoints <= 0 ? 0 : bodyPoints / rangePoints;
            double impulseWeight = 8.0 + 38.0 * bodyEfficiency;

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
        // Swing/BOS structure engine
        // -----------------------------------------------------------------------------------------
        private void UpdateSwingStructure()
        {
            int s = Math.Max(1, SwingStrengthBars);
            if (syntheticBarCount < s * 2 + 3)
                return;

            int pivotBarsAgo = s + 1;
            double pivotHigh = GetSynH(pivotBarsAgo);
            double pivotLow = GetSynL(pivotBarsAgo);
            bool isSwingHigh = true;
            bool isSwingLow = true;

            for (int i = 1; i <= s * 2 + 1; i++)
            {
                if (i == pivotBarsAgo)
                    continue;

                if (GetSynH(i) >= pivotHigh)
                    isSwingHigh = false;
                if (GetSynL(i) <= pivotLow)
                    isSwingLow = false;
            }

            if (isSwingHigh)
            {
                lastSwingHigh = pivotHigh;
                lastSwingHighBar = syntheticBarCount - pivotBarsAgo;
            }

            if (isSwingLow)
            {
                lastSwingLow = pivotLow;
                lastSwingLowBar = syntheticBarCount - pivotBarsAgo;
            }

            DetectBreakOfStructure();
        }

        private void DetectBreakOfStructure()
        {
            if (lastSwingHigh <= 0 || lastSwingLow <= 0)
                return;

            if (syntheticBarCount - lastBosSyntheticBar <= BosCooldownSyntheticBars)
                return;

            double lastClose = GetSynC(1);
            double priorClose = GetSynC(2);
            double swingSize = Math.Abs(lastSwingHigh - lastSwingLow);

            if (swingSize < MinBosSwingSizePoints)
                return;

            bool longBos = priorClose <= lastSwingHigh + BosBreakBufferPoints && lastClose > lastSwingHigh + BosBreakBufferPoints;
            bool shortBos = priorClose >= lastSwingLow - BosBreakBufferPoints && lastClose < lastSwingLow - BosBreakBufferPoints;

            if (longBos)
            {
                lastBosLevel = lastSwingHigh;
                lastBosDirection = 1;
                lastBosSyntheticBar = syntheticBarCount;
                longBosCount++;
                shortBosCount = 0;
                campaignState = CampaignState.LongCampaign;
            }
            else if (shortBos)
            {
                lastBosLevel = lastSwingLow;
                lastBosDirection = -1;
                lastBosSyntheticBar = syntheticBarCount;
                shortBosCount++;
                longBosCount = 0;
                campaignState = CampaignState.ShortCampaign;
            }
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
                directionalEfficiencyScore * 0.44 +
                overlapQuality * 0.26 +
                wickQuality * 0.18 +
                alternationQuality * 0.12);

            bool lowQuality = auctionQualityScore < MinAuctionQuality ||
                              directionalEfficiencyScore < MinDirectionalEfficiency ||
                              overlapRatioScore > MaxOverlapRatio ||
                              wickRatioScore > MaxWickRatio;

            bool longOwned = longPersistence >= TrendOwnershipThreshold && longPersistence > shortPersistence * 1.10;
            bool shortOwned = shortPersistence >= TrendOwnershipThreshold && shortPersistence > longPersistence * 1.10;

            bool exhaustionUp = IsExhaustion(+1);
            bool exhaustionDown = IsExhaustion(-1);

            if (exhaustionUp)
                auctionState = AuctionState.ExhaustionUp;
            else if (exhaustionDown)
                auctionState = AuctionState.ExhaustionDown;
            else if (lowQuality && alternatingScore > 0.50)
                auctionState = AuctionState.Rotation;
            else if (lowQuality)
                auctionState = AuctionState.Balance;
            else if (longOwned)
                auctionState = sameDirectionCount >= MinSameDirectionBarsForEntry ? AuctionState.TrendUp : AuctionState.DiscoveryUp;
            else if (shortOwned)
                auctionState = sameDirectionCount >= MinSameDirectionBarsForEntry ? AuctionState.TrendDown : AuctionState.DiscoveryDown;
            else
                auctionState = AuctionState.Compression;

            if (auctionState == AuctionState.TrendUp || auctionState == AuctionState.DiscoveryUp || campaignState == CampaignState.LongCampaign)
                biasState = BiasState.LongOnly;
            else if (auctionState == AuctionState.TrendDown || auctionState == AuctionState.DiscoveryDown || campaignState == CampaignState.ShortCampaign)
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
                pairs++;
            }

            return pairs == 0 ? 1 : Clamp01(overlapSum / pairs);
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
            bool movedInDirection = direction > 0 ? move > MinImpulsePoints * 0.90 : move < -MinImpulsePoints * 0.90;
            bool efficiencyCollapsed = recentEfficiency <= ExhaustionEfficiencyCollapse;
            bool bigWicks = ComputeWickRatio(Math.Max(2, ExhaustionLookbackBars)) > MaxWickRatio;

            return movedInDirection && efficiencyCollapsed && bigWicks;
        }

        // -----------------------------------------------------------------------------------------
        // Pullback/reclaim continuation engine
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

            if (syntheticBarCount < ImpulseBars + 2)
                return;

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
                        ResetSetup();
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
                        ResetSetup();
                }
            }
        }

        private void TryEnter(DateTime now, double price)
        {
            // First: structural BOS campaign path. This is the new component that should catch the
            // obvious stair-step rally/decline when auction quality is merely adequate rather than perfect.
            if (EnableBosCampaignEntries && TryEnterBosCampaign(now, price))
                return;

            // Second: stricter pullback/reclaim path.
            TryEnterPullbackReclaim(now, price);
        }

        private bool TryEnterBosCampaign(DateTime now, double price)
        {
            if (lastBosDirection == 0 || lastBosSyntheticBar < 0 || lastBosLevel <= 0)
                return false;

            if (syntheticBarCount - lastBosSyntheticBar > Math.Max(3, MaxSetupAgeBars))
                return false;

            if (auctionQualityScore < BosMinAuctionQuality)
                return false;

            if (BlockMiddayIfRotational && IsMiddayEt(now) && auctionState == AuctionState.Rotation)
                return false;

            bool stopReverseWindow = AllowBosStopAndReverse && lastStopTime != Core.Globals.MinDate &&
                (now - lastStopTime).TotalSeconds >= BosStopReverseCooldownSeconds;

            if (lastBosDirection > 0)
            {
                if (longBosCount < MinBosCountForCampaign)
                    return false;

                double distanceTicks = Math.Abs(price - lastBosLevel) / TickSize;
                if (distanceTicks > MaxEntryDistanceFromBosTicks)
                    return false;

                if (price <= lastBosLevel + BosBreakBufferPoints)
                    return false;

                if (IsLateChase(+1, price))
                    return false;

                if (longPersistence < MinEntryPersistenceScore && !stopReverseWindow)
                    return false;

                if (shortPersistence > longPersistence * 1.35 && !stopReverseWindow)
                    return false;

                EnterLong(1, "CG_LONG");
                ResetSetup();
                return true;
            }

            if (lastBosDirection < 0)
            {
                if (shortBosCount < MinBosCountForCampaign)
                    return false;

                double distanceTicks = Math.Abs(price - lastBosLevel) / TickSize;
                if (distanceTicks > MaxEntryDistanceFromBosTicks)
                    return false;

                if (price >= lastBosLevel - BosBreakBufferPoints)
                    return false;

                if (IsLateChase(-1, price))
                    return false;

                if (shortPersistence < MinEntryPersistenceScore && !stopReverseWindow)
                    return false;

                if (longPersistence > shortPersistence * 1.35 && !stopReverseWindow)
                    return false;

                EnterShort(1, "CG_SHORT");
                ResetSetup();
                return true;
            }

            return false;
        }

        private void TryEnterPullbackReclaim(DateTime now, double price)
        {
            if (auctionQualityScore < MinAuctionQuality)
                return;

            if (directionalEfficiencyScore < MinDirectionalEfficiency)
                return;

            if (overlapRatioScore > MaxOverlapRatio || wickRatioScore > MaxWickRatio)
                return;

            if (BlockMiddayIfRotational && IsMiddayEt(now) &&
                (auctionState == AuctionState.Rotation || auctionState == AuctionState.Balance || auctionQualityScore < MinAuctionQuality + 0.08))
                return;

            if (setupState == SetupState.LongPullbackSeen && biasState == BiasState.LongOnly)
            {
                if (price >= reclaimTriggerPrice && longPersistence >= MinEntryPersistenceScore)
                {
                    EnterLong(1, "CG_LONG");
                    ResetSetup();
                }
            }
            else if (setupState == SetupState.ShortPullbackSeen && biasState == BiasState.ShortOnly)
            {
                if (price <= reclaimTriggerPrice && shortPersistence >= MinEntryPersistenceScore)
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

            bool exhaustionState = direction > 0 ? auctionState == AuctionState.ExhaustionUp : auctionState == AuctionState.ExhaustionDown;
            bool severeWick = wickRatioScore > Math.Min(0.95, MaxWickRatio + 0.12);
            return chaseDistanceTicks >= LateChaseBlockTicks && (exhaustionState || severeWick);
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

            // Advisory quality exit. OCO stop/target remains primary crash-safe protection.
            if (syntheticBarCount >= WarmupSyntheticBars && auctionQualityScore < Math.Max(0.16, MinAuctionQuality - 0.24))
            {
                if (Position.MarketPosition == MarketPosition.Long && auctionState != AuctionState.TrendUp && auctionState != AuctionState.DiscoveryUp)
                    ExitLong("CG_QUALITY_EXIT_LONG", "CG_LONG");
                else if (Position.MarketPosition == MarketPosition.Short && auctionState != AuctionState.TrendDown && auctionState != AuctionState.DiscoveryDown)
                    ExitShort("CG_QUALITY_EXIT_SHORT", "CG_SHORT");
            }
        }

        private bool CooldownSatisfied(DateTime now)
        {
            if (lastExitTime != Core.Globals.MinDate && (now - lastExitTime).TotalSeconds < CooldownSeconds)
                return false;

            if (lastStopTime != Core.Globals.MinDate && (now - lastStopTime).TotalSeconds < PostStopCooldownSeconds)
            {
                // Allow quicker participation if a fresh opposite BOS appears after a stop.
                if (!AllowBosStopAndReverse)
                    return false;

                if (lastBosDirection == 0)
                    return false;

                if ((now - lastStopTime).TotalSeconds < BosStopReverseCooldownSeconds)
                    return false;
            }

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
            DateTime et = ToEastern(localTime);
            return et.Hour * 10000 + et.Minute * 100 + et.Second;
        }

        private DateTime ToEastern(DateTime localTime)
        {
            try
            {
                return TimeZoneInfo.ConvertTime(localTime, TimeZoneInfo.Local, easternTimeZone);
            }
            catch
            {
                return localTime;
            }
        }

        private int HistIndex(int barsAgo)
        {
            int raw = syntheticBarCount - barsAgo;
            while (raw < 0)
                raw += MaxSyntheticHistory;
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
            int n = Math.Min(bars, syntheticBarCount);
            for (int i = 1; i <= n; i++)
                v = Math.Max(v, GetSynH(i));
            return v;
        }

        private double LowestSynLow(int bars)
        {
            double v = double.MaxValue;
            int n = Math.Min(bars, syntheticBarCount);
            for (int i = 1; i <= n; i++)
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
