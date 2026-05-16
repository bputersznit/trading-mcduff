// CG_T2_ClanMarshal_v9_2_LIGHT_B6_2_NoEmergencyLockout.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-06 16:20:00 America/New_York
//
// MNQ Intraday Strategy — v9.2 LIGHT B6.2 No-Emergency-Lockout Hybrid
//
// PURPOSE
//   Lightweight MNQ auction-state tactical execution engine.
//   This version removes emergency behavioral lockout / emergency flatten logic
//   so development playback can expose the raw behavior of the auction/fade engine.
//   It keeps structural risk protection: one MNQ only, managed broker-side bracket
//   stop/target orders, RTH gating, cooldown throttling, and optional daily loss cap.
//
// CRITICAL OPERATIONAL NOTES
//   1. Intended instrument: MNQ.
//   2. Intended chart: 1 Tick primary chart preferred.
//      The strategy does not AddDataSeries; it synthesizes trend bars internally
//      from the incoming primary stream.
//   3. Quantity is hard-capped to 1.
//   4. No pyramiding, no averaging down, no overlap.
//   5. Emergency lockout is intentionally removed in this version.
//   6. Protective brackets are still submitted through NinjaTrader managed orders.
//
// STRATEGY PHILOSOPHY
//   Strong persistent auction:
//       do not blind-fade; join controlled pullback/resume continuation.
//   Neutral/weak auction:
//       allow compact micro-fade entries after local exhaustion.
//   Violent/cascading transition:
//       this version does not emergency-lockout; it simply relies on bracket stops
//       and state logic so failures are visible during playback research.
//
// DEVELOPMENT CHANGE FROM B6.1
//   - Removed emergency lockout / emergency flatten behavior.
//   - Added sticky auction-state hysteresis to reduce DownTrend/None/DownTrend thrashing.
//   - Added persistence score and minimum state hold.
//   - Added explicit trend/fade target separation.
//   - Added diagnostics for auction state, score, blocks, and submissions.
//   - Preserved one-contract/no-overlap governance.
//
// DISCLAIMER
//   Research code. Use in SIM/playback first. Futures trading involves substantial risk.

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_T2_ClanMarshal_v9_2_LIGHT_B6_2_NoEmergencyLockout : Strategy
    {
        private enum AuctionMode
        {
            None,
            UpTrend,
            DownTrend
        }

        // Synthetic bar state
        private bool syntheticInitialized;
        private DateTime syntheticStart;
        private double syntheticOpen;
        private double syntheticHigh;
        private double syntheticLow;
        private double syntheticClose;

        private const int MaxSyntheticBars = 12;
        private readonly double[] synOpen = new double[MaxSyntheticBars];
        private readonly double[] synHigh = new double[MaxSyntheticBars];
        private readonly double[] synLow = new double[MaxSyntheticBars];
        private readonly double[] synClose = new double[MaxSyntheticBars];
        private int synCount;

        // Auction state
        private AuctionMode auctionMode = AuctionMode.None;
        private AuctionMode lastPrintedMode = AuctionMode.None;
        private DateTime auctionModeSince = Core.Globals.MinDate;
        private DateTime lastModePrint = Core.Globals.MinDate;
        private double auctionScore;
        private double lastAuctionScore;

        // Tick-local structure
        private double sessionHigh;
        private double sessionLow;
        private double recentHigh;
        private double recentLow;
        private double lastPrice;
        private double priorPrice;
        private int upTicksWindow;
        private int downTicksWindow;
        private int sameTicksWindow;
        private int tickWindowCount;

        // Entry management
        private bool pendingEntry;
        private int lastEntryBar = -1;
        private int lastExitBar = -1;
        private DateTime lastEntryTime = Core.Globals.MinDate;
        private DateTime lastExitTime = Core.Globals.MinDate;
        private MarketPosition lastPosition = MarketPosition.Flat;

        // Trend pullback state
        private bool upPullbackArmed;
        private bool downPullbackArmed;
        private double upTrendExtreme;
        private double downTrendExtreme;
        private double upPullbackLow;
        private double downPullbackHigh;

        // Fade state
        private int ticksSinceRecentHigh;
        private int ticksSinceRecentLow;
        private double lastLocalHigh;
        private double lastLocalLow;

        // Session/governance
        private DateTime currentSessionDate = Core.Globals.MinDate;
        private double sessionStartCumProfit;
        private bool dailyLossLocked;

        // Diagnostics
        private long totalTicks;
        private long submittedTrendLong;
        private long submittedTrendShort;
        private long submittedFadeLong;
        private long submittedFadeShort;
        private long blockSession;
        private long blockCooldown;
        private long blockPosition;
        private long blockPending;
        private long blockDailyLoss;
        private long blockTrendSuppress;
        private long blockSameBar;
        private long modeChanges;

        private string SignalTrendLong  { get { return "B62_Trend_Long"; } }
        private string SignalTrendShort { get { return "B62_Trend_Short"; } }
        private string SignalFadeLong   { get { return "B62_Fade_Long"; } }
        private string SignalFadeShort  { get { return "B62_Fade_Short"; } }

        #region User parameters

        [NinjaScriptProperty]
        [Range(1, 1)]
        [Display(Name = "QuantityFixed", Order = 1, GroupName = "01. Position")]
        public int QuantityFixed { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "TrendTargetTicks", Order = 10, GroupName = "02. Brackets")]
        public int TrendTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "TrendStopTicks", Order = 11, GroupName = "02. Brackets")]
        public int TrendStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "FadeTargetTicks", Order = 12, GroupName = "02. Brackets")]
        public int FadeTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "FadeStopTicks", Order = 13, GroupName = "02. Brackets")]
        public int FadeStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(5, 300)]
        [Display(Name = "SyntheticTrendSeconds", Order = 20, GroupName = "03. Synthetic Auction")]
        public int SyntheticTrendSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(2, 10)]
        [Display(Name = "TrendBarsRequired", Order = 21, GroupName = "03. Synthetic Auction")]
        public int TrendBarsRequired { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 100.0)]
        [Display(Name = "TrendEnterScore", Order = 22, GroupName = "03. Synthetic Auction")]
        public double TrendEnterScore { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "TrendExitScore", Order = 23, GroupName = "03. Synthetic Auction")]
        public double TrendExitScore { get; set; }

        [NinjaScriptProperty]
        [Range(0, 300)]
        [Display(Name = "MinAuctionStateHoldSeconds", Order = 24, GroupName = "03. Synthetic Auction")]
        public int MinAuctionStateHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 100.0)]
        [Display(Name = "MinTrendMovePoints", Order = 25, GroupName = "03. Synthetic Auction")]
        public double MinTrendMovePoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "TrendPullbackTicks", Order = 30, GroupName = "04. Trend Entry")]
        public int TrendPullbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "TrendReclaimTicks", Order = 31, GroupName = "04. Trend Entry")]
        public int TrendReclaimTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "MaxPullbackPoints", Order = 32, GroupName = "04. Trend Entry")]
        public double MaxPullbackPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "FadeExtremeTicks", Order = 40, GroupName = "05. Fade Entry")]
        public int FadeExtremeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "FadeReclaimTicks", Order = 41, GroupName = "05. Fade Entry")]
        public int FadeReclaimTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "FadeLookbackTicks", Order = 42, GroupName = "05. Fade Entry")]
        public int FadeLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 300)]
        [Display(Name = "PostEntryCooldownSeconds", Order = 50, GroupName = "06. Throttle")]
        public int PostEntryCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 300)]
        [Display(Name = "PostExitCooldownSeconds", Order = 51, GroupName = "06. Throttle")]
        public int PostExitCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AvoidSameBarReentry", Order = 52, GroupName = "06. Throttle")]
        public bool AvoidSameBarReentry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseRthOnly", Order = 60, GroupName = "07. Session")]
        public bool UseRthOnly { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "StartTime", Order = 61, GroupName = "07. Session")]
        public int StartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EndTime", Order = 62, GroupName = "07. Session")]
        public int EndTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseDailyLossCap", Order = 70, GroupName = "08. Governance")]
        public bool UseDailyLossCap { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10000.0)]
        [Display(Name = "DailyLossCapDollars", Order = 71, GroupName = "08. Governance")]
        public double DailyLossCapDollars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", Order = 80, GroupName = "09. Diagnostics")]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(100, 100000)]
        [Display(Name = "DiagEveryTicks", Order = 81, GroupName = "09. Diagnostics")]
        public int DiagEveryTicks { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_2_LIGHT_B6_2_NoEmergencyLockout";
                Description = "MNQ LIGHT hybrid auction/fade strategy. No emergency lockout. Sticky auction hysteresis. One MNQ only.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Day;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 1;
                IsInstantiatedOnEachOptimizationIteration = false;

                QuantityFixed = 1;

                TrendTargetTicks = 16;
                TrendStopTicks = 8;
                FadeTargetTicks = 10;
                FadeStopTicks = 8;

                SyntheticTrendSeconds = 60;
                TrendBarsRequired = 2;
                TrendEnterScore = 10.0;
                TrendExitScore = 4.0;
                MinAuctionStateHoldSeconds = 20;
                MinTrendMovePoints = 10.0;

                TrendPullbackTicks = 8;
                TrendReclaimTicks = 3;
                MaxPullbackPoints = 12.0;

                FadeExtremeTicks = 8;
                FadeReclaimTicks = 3;
                FadeLookbackTicks = 120;

                PostEntryCooldownSeconds = 3;
                PostExitCooldownSeconds = 2;
                AvoidSameBarReentry = true;

                UseRthOnly = true;
                StartTime = 93000;
                EndTime = 155900;

                UseDailyLossCap = false;
                DailyLossCapDollars = 250.0;

                PrintDiagnostics = true;
                DiagEveryTicks = 1000;
            }
            else if (State == State.Configure)
            {
                // Managed bracket protection. These are not emergency lockouts.
                // They are per-entry protective OCO-style stop/target orders managed by NT.
                SetProfitTarget(SignalTrendLong,  CalculationMode.Ticks, TrendTargetTicks);
                SetStopLoss(SignalTrendLong,      CalculationMode.Ticks, TrendStopTicks, false);
                SetProfitTarget(SignalTrendShort, CalculationMode.Ticks, TrendTargetTicks);
                SetStopLoss(SignalTrendShort,     CalculationMode.Ticks, TrendStopTicks, false);

                SetProfitTarget(SignalFadeLong,   CalculationMode.Ticks, FadeTargetTicks);
                SetStopLoss(SignalFadeLong,       CalculationMode.Ticks, FadeStopTicks, false);
                SetProfitTarget(SignalFadeShort,  CalculationMode.Ticks, FadeTargetTicks);
                SetStopLoss(SignalFadeShort,      CalculationMode.Ticks, FadeStopTicks, false);
            }
            else if (State == State.DataLoaded)
            {
                ResetAllRuntimeState();
                Print(Name + " loaded. Hybrid trend/fade. NO emergency lockout. Use on MNQ 1 tick chart preferred. One MNQ only.");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            double price = Close[0];
            DateTime now = Time[0];
            totalTicks++;

            ResetSessionIfNeeded(now, price);
            UpdateTickWindow(price);
            UpdateSyntheticAuction(price, now);
            UpdateAuctionMode(now);
            UpdateLocalStructure(price);

            if (PrintDiagnostics && DiagEveryTicks > 0 && totalTicks % DiagEveryTicks == 0)
                PrintDiagnostic(now, price);

            if (Position.MarketPosition != lastPosition)
            {
                if (lastPosition != MarketPosition.Flat && Position.MarketPosition == MarketPosition.Flat)
                {
                    lastExitBar = CurrentBar;
                    lastExitTime = now;
                    pendingEntry = false;
                }

                lastPosition = Position.MarketPosition;
            }

            if (!CanConsiderEntry(now))
                return;

            // Priority order:
            //   1. In confirmed trend: controlled continuation only.
            //   2. In neutral: compact exhaustion fades.
            if (auctionMode == AuctionMode.UpTrend)
            {
                blockTrendSuppress++;
                if (TryTrendLong(price, now))
                    return;
            }
            else if (auctionMode == AuctionMode.DownTrend)
            {
                blockTrendSuppress++;
                if (TryTrendShort(price, now))
                    return;
            }
            else
            {
                if (TryFadeLong(price, now))
                    return;

                if (TryFadeShort(price, now))
                    return;
            }
        }

        private void ResetAllRuntimeState()
        {
            syntheticInitialized = false;
            synCount = 0;

            auctionMode = AuctionMode.None;
            lastPrintedMode = AuctionMode.None;
            auctionModeSince = Core.Globals.MinDate;
            lastModePrint = Core.Globals.MinDate;
            auctionScore = 0;
            lastAuctionScore = 0;

            sessionHigh = double.MinValue;
            sessionLow = double.MaxValue;
            recentHigh = double.MinValue;
            recentLow = double.MaxValue;
            lastPrice = 0;
            priorPrice = 0;

            upTicksWindow = 0;
            downTicksWindow = 0;
            sameTicksWindow = 0;
            tickWindowCount = 0;

            pendingEntry = false;
            lastEntryBar = -1;
            lastExitBar = -1;
            lastEntryTime = Core.Globals.MinDate;
            lastExitTime = Core.Globals.MinDate;
            lastPosition = MarketPosition.Flat;

            upPullbackArmed = false;
            downPullbackArmed = false;
            upTrendExtreme = double.MinValue;
            downTrendExtreme = double.MaxValue;
            upPullbackLow = double.MaxValue;
            downPullbackHigh = double.MinValue;

            ticksSinceRecentHigh = 0;
            ticksSinceRecentLow = 0;
            lastLocalHigh = double.MinValue;
            lastLocalLow = double.MaxValue;

            currentSessionDate = Core.Globals.MinDate;
            sessionStartCumProfit = 0;
            dailyLossLocked = false;

            totalTicks = 0;
            submittedTrendLong = 0;
            submittedTrendShort = 0;
            submittedFadeLong = 0;
            submittedFadeShort = 0;
            blockSession = 0;
            blockCooldown = 0;
            blockPosition = 0;
            blockPending = 0;
            blockDailyLoss = 0;
            blockTrendSuppress = 0;
            blockSameBar = 0;
            modeChanges = 0;
        }

        private void ResetSessionIfNeeded(DateTime now, double price)
        {
            if (currentSessionDate != now.Date)
            {
                currentSessionDate = now.Date;
                sessionStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                dailyLossLocked = false;

                sessionHigh = price;
                sessionLow = price;
                recentHigh = price;
                recentLow = price;
                lastLocalHigh = price;
                lastLocalLow = price;

                upPullbackArmed = false;
                downPullbackArmed = false;
                upTrendExtreme = price;
                downTrendExtreme = price;
                upPullbackLow = price;
                downPullbackHigh = price;
            }

            if (price > sessionHigh)
                sessionHigh = price;
            if (price < sessionLow)
                sessionLow = price;

            if (UseDailyLossCap)
            {
                double dayPnl = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartCumProfit;
                if (dayPnl <= -Math.Abs(DailyLossCapDollars))
                    dailyLossLocked = true;
            }
        }

        private void UpdateTickWindow(double price)
        {
            priorPrice = lastPrice;
            if (lastPrice > 0)
            {
                if (price > lastPrice)
                    upTicksWindow++;
                else if (price < lastPrice)
                    downTicksWindow++;
                else
                    sameTicksWindow++;

                tickWindowCount++;

                // Keep this deliberately lightweight: exponential-ish decay every 64 ticks.
                if (tickWindowCount >= 64)
                {
                    upTicksWindow = upTicksWindow / 2;
                    downTicksWindow = downTicksWindow / 2;
                    sameTicksWindow = sameTicksWindow / 2;
                    tickWindowCount = upTicksWindow + downTicksWindow + sameTicksWindow;
                }
            }

            lastPrice = price;
        }

        private void UpdateSyntheticAuction(double price, DateTime now)
        {
            if (!syntheticInitialized)
            {
                syntheticInitialized = true;
                syntheticStart = now;
                syntheticOpen = price;
                syntheticHigh = price;
                syntheticLow = price;
                syntheticClose = price;
                return;
            }

            if (price > syntheticHigh)
                syntheticHigh = price;
            if (price < syntheticLow)
                syntheticLow = price;
            syntheticClose = price;

            if ((now - syntheticStart).TotalSeconds >= SyntheticTrendSeconds)
            {
                PushSyntheticBar(syntheticOpen, syntheticHigh, syntheticLow, syntheticClose);

                syntheticStart = now;
                syntheticOpen = price;
                syntheticHigh = price;
                syntheticLow = price;
                syntheticClose = price;
            }
        }

        private void PushSyntheticBar(double o, double h, double l, double c)
        {
            int maxIndex = MaxSyntheticBars - 1;
            for (int i = maxIndex; i >= 1; i--)
            {
                synOpen[i] = synOpen[i - 1];
                synHigh[i] = synHigh[i - 1];
                synLow[i] = synLow[i - 1];
                synClose[i] = synClose[i - 1];
            }

            synOpen[0] = o;
            synHigh[0] = h;
            synLow[0] = l;
            synClose[0] = c;

            if (synCount < MaxSyntheticBars)
                synCount++;
        }

        private void UpdateAuctionMode(DateTime now)
        {
            lastAuctionScore = auctionScore;
            auctionScore = ComputeAuctionScore();

            AuctionMode desired = auctionMode;

            // Hysteresis:
            //   enter trend on stronger threshold;
            //   exit trend only after weaker threshold AND minimum state hold.
            bool canLeaveState = auctionModeSince == Core.Globals.MinDate ||
                                 (now - auctionModeSince).TotalSeconds >= MinAuctionStateHoldSeconds;

            if (auctionMode == AuctionMode.None)
            {
                if (auctionScore >= TrendEnterScore)
                    desired = AuctionMode.UpTrend;
                else if (auctionScore <= -TrendEnterScore)
                    desired = AuctionMode.DownTrend;
            }
            else if (auctionMode == AuctionMode.UpTrend)
            {
                if (canLeaveState && auctionScore <= TrendExitScore)
                    desired = AuctionMode.None;
            }
            else if (auctionMode == AuctionMode.DownTrend)
            {
                if (canLeaveState && auctionScore >= -TrendExitScore)
                    desired = AuctionMode.None;
            }

            if (desired != auctionMode)
            {
                auctionMode = desired;
                auctionModeSince = now;
                modeChanges++;

                if (auctionMode == AuctionMode.UpTrend)
                {
                    upPullbackArmed = false;
                    upTrendExtreme = Close[0];
                    upPullbackLow = Close[0];
                }
                else if (auctionMode == AuctionMode.DownTrend)
                {
                    downPullbackArmed = false;
                    downTrendExtreme = Close[0];
                    downPullbackHigh = Close[0];
                }

                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} AUCTION MODE {1} score={2:F2}", now, auctionMode, auctionScore));
            }
        }

        private double ComputeAuctionScore()
        {
            if (synCount < TrendBarsRequired)
                return 0.0;

            int bars = Math.Min(TrendBarsRequired, synCount);
            double directionalBodies = 0.0;
            double totalRange = 0.0;
            int green = 0;
            int red = 0;

            for (int i = 0; i < bars; i++)
            {
                double body = synClose[i] - synOpen[i];
                directionalBodies += body;
                totalRange += Math.Max(TickSize, synHigh[i] - synLow[i]);

                if (body > 0)
                    green++;
                else if (body < 0)
                    red++;
            }

            double spanMove = synClose[0] - synOpen[bars - 1];
            double persistence = 0.0;

            if (bars > 0)
                persistence = 10.0 * ((double)(green - red) / bars);

            double efficiency = 0.0;
            if (totalRange > 0)
                efficiency = 10.0 * (directionalBodies / totalRange);

            double moveComponent = 0.0;
            if (Math.Abs(spanMove) >= MinTrendMovePoints)
                moveComponent = Math.Sign(spanMove) * Math.Min(15.0, Math.Abs(spanMove));

            double tickPressure = 0.0;
            int activeTicks = Math.Max(1, upTicksWindow + downTicksWindow);
            tickPressure = 6.0 * ((double)(upTicksWindow - downTicksWindow) / activeTicks);

            return persistence + efficiency + moveComponent + tickPressure;
        }

        private void UpdateLocalStructure(double price)
        {
            ticksSinceRecentHigh++;
            ticksSinceRecentLow++;

            if (price > recentHigh || recentHigh == double.MinValue)
            {
                recentHigh = price;
                ticksSinceRecentHigh = 0;
            }

            if (price < recentLow || recentLow == double.MaxValue)
            {
                recentLow = price;
                ticksSinceRecentLow = 0;
            }

            // Slowly relax recent extremes so fade logic does not anchor forever.
            if (ticksSinceRecentHigh > FadeLookbackTicks)
            {
                recentHigh = price;
                ticksSinceRecentHigh = 0;
            }

            if (ticksSinceRecentLow > FadeLookbackTicks)
            {
                recentLow = price;
                ticksSinceRecentLow = 0;
            }

            if (price > lastLocalHigh || lastLocalHigh == double.MinValue)
                lastLocalHigh = price;
            if (price < lastLocalLow || lastLocalLow == double.MaxValue)
                lastLocalLow = price;
        }

        private bool CanConsiderEntry(DateTime now)
        {
            if (UseRthOnly)
            {
                int tt = ToTime(now);
                if (tt < StartTime || tt > EndTime)
                {
                    blockSession++;
                    return false;
                }
            }

            if (UseDailyLossCap && dailyLossLocked)
            {
                blockDailyLoss++;
                return false;
            }

            if (pendingEntry)
            {
                blockPending++;
                return false;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                blockPosition++;
                return false;
            }

            if (AvoidSameBarReentry && (CurrentBar == lastEntryBar || CurrentBar == lastExitBar))
            {
                blockSameBar++;
                return false;
            }

            if (lastEntryTime != Core.Globals.MinDate && (now - lastEntryTime).TotalSeconds < PostEntryCooldownSeconds)
            {
                blockCooldown++;
                return false;
            }

            if (lastExitTime != Core.Globals.MinDate && (now - lastExitTime).TotalSeconds < PostExitCooldownSeconds)
            {
                blockCooldown++;
                return false;
            }

            return true;
        }

        private bool TryTrendLong(double price, DateTime now)
        {
            if (price > upTrendExtreme || upTrendExtreme == double.MinValue)
            {
                upTrendExtreme = price;
                upPullbackLow = price;
                upPullbackArmed = false;
                return false;
            }

            double pullbackTicks = (upTrendExtreme - price) / TickSize;

            if (!upPullbackArmed && pullbackTicks >= TrendPullbackTicks && pullbackTicks * TickSize <= MaxPullbackPoints)
            {
                upPullbackArmed = true;
                upPullbackLow = price;
                return false;
            }

            if (upPullbackArmed)
            {
                if (price < upPullbackLow)
                    upPullbackLow = price;

                double reclaimTicks = (price - upPullbackLow) / TickSize;
                bool reclaim = reclaimTicks >= TrendReclaimTicks;
                bool notTooDeep = (upTrendExtreme - upPullbackLow) <= MaxPullbackPoints;
                bool tickPressureOk = upTicksWindow >= downTicksWindow;

                if (reclaim && notTooDeep && tickPressureOk)
                {
                    SubmitLong(SignalTrendLong, now);
                    submittedTrendLong++;
                    upPullbackArmed = false;
                    return true;
                }
            }

            return false;
        }

        private bool TryTrendShort(double price, DateTime now)
        {
            if (price < downTrendExtreme || downTrendExtreme == double.MaxValue)
            {
                downTrendExtreme = price;
                downPullbackHigh = price;
                downPullbackArmed = false;
                return false;
            }

            double pullbackTicks = (price - downTrendExtreme) / TickSize;

            if (!downPullbackArmed && pullbackTicks >= TrendPullbackTicks && pullbackTicks * TickSize <= MaxPullbackPoints)
            {
                downPullbackArmed = true;
                downPullbackHigh = price;
                return false;
            }

            if (downPullbackArmed)
            {
                if (price > downPullbackHigh)
                    downPullbackHigh = price;

                double reclaimTicks = (downPullbackHigh - price) / TickSize;
                bool reclaim = reclaimTicks >= TrendReclaimTicks;
                bool notTooDeep = (downPullbackHigh - downTrendExtreme) <= MaxPullbackPoints;
                bool tickPressureOk = downTicksWindow >= upTicksWindow;

                if (reclaim && notTooDeep && tickPressureOk)
                {
                    SubmitShort(SignalTrendShort, now);
                    submittedTrendShort++;
                    downPullbackArmed = false;
                    return true;
                }
            }

            return false;
        }

        private bool TryFadeLong(double price, DateTime now)
        {
            double extensionTicks = (price - recentLow) / TickSize;
            double reclaimTicks = (price - recentLow) / TickSize;

            // Long fade means price made/pushed near a recent low, then reclaimed.
            bool pushedLowEnough = ticksSinceRecentLow > 0 && extensionTicks >= FadeReclaimTicks;
            bool wasExtreme = (recentHigh - recentLow) / TickSize >= FadeExtremeTicks;
            bool sellingDecelerated = upTicksWindow >= Math.Max(1, downTicksWindow / 2);

            if (pushedLowEnough && wasExtreme && sellingDecelerated)
            {
                SubmitLong(SignalFadeLong, now);
                submittedFadeLong++;
                return true;
            }

            return false;
        }

        private bool TryFadeShort(double price, DateTime now)
        {
            double extensionTicks = (recentHigh - price) / TickSize;

            // Short fade means price made/pushed near a recent high, then rejected.
            bool rejectedEnough = ticksSinceRecentHigh > 0 && extensionTicks >= FadeReclaimTicks;
            bool wasExtreme = (recentHigh - recentLow) / TickSize >= FadeExtremeTicks;
            bool buyingDecelerated = downTicksWindow >= Math.Max(1, upTicksWindow / 2);

            if (rejectedEnough && wasExtreme && buyingDecelerated)
            {
                SubmitShort(SignalFadeShort, now);
                submittedFadeShort++;
                return true;
            }

            return false;
        }

        private void SubmitLong(string signalName, DateTime now)
        {
            pendingEntry = true;
            lastEntryBar = CurrentBar;
            lastEntryTime = now;
            EnterLong(QuantityFixed, signalName);

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} SUBMIT LONG {1} px={2:F2} mode={3} score={4:F2}", now, signalName, Close[0], auctionMode, auctionScore));
        }

        private void SubmitShort(string signalName, DateTime now)
        {
            pendingEntry = true;
            lastEntryBar = CurrentBar;
            lastEntryTime = now;
            EnterShort(QuantityFixed, signalName);

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} SUBMIT SHORT {1} px={2:F2} mode={3} score={4:F2}", now, signalName, Close[0], auctionMode, auctionScore));
        }

        protected override void OnOrderUpdate(
            Order order,
            double limitPrice,
            double stopPrice,
            int quantity,
            int filled,
            double averageFillPrice,
            OrderState orderState,
            DateTime time,
            ErrorCode error,
            string nativeError)
        {
            if (order == null)
                return;

            if (orderState == OrderState.Rejected || orderState == OrderState.Cancelled)
            {
                if (order.Name == SignalTrendLong || order.Name == SignalTrendShort || order.Name == SignalFadeLong || order.Name == SignalFadeShort)
                    pendingEntry = false;

                if (PrintDiagnostics && orderState == OrderState.Rejected)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ORDER REJECTED name={1} error={2} native={3}", time, order.Name, error, nativeError));
            }
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

            string n = execution.Order.Name;

            if (n == SignalTrendLong || n == SignalTrendShort || n == SignalFadeLong || n == SignalFadeShort)
            {
                pendingEntry = false;
                lastEntryTime = time;

                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTRY FILL {1} qty={2} px={3:F2} pos={4}", time, n, quantity, price, marketPosition));
            }
            else
            {
                // Any stop/target/managed exit fill updates flat timing once OnBarUpdate sees flat.
                if (marketPosition == MarketPosition.Flat)
                {
                    lastExitTime = time;
                    lastExitBar = CurrentBar;
                    pendingEntry = false;
                }

                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EXIT/BRACKET FILL {1} qty={2} px={3:F2} pos={4}", time, n, quantity, price, marketPosition));
            }
        }

        private void PrintDiagnostic(DateTime now, double price)
        {
            double dayPnl = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartCumProfit;

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm:ss} DIAG ticks={1} px={2:F2} mode={3} score={4:F2} pos={5} trades={6} TL={7} TS={8} FL={9} FS={10} sessBlk={11} coolBlk={12} posBlk={13} pendBlk={14} sameBarBlk={15} dailyBlk={16} trendSeen={17} modeChanges={18} dayPnl={19:F2}",
                now,
                totalTicks,
                price,
                auctionMode,
                auctionScore,
                Position.MarketPosition,
                SystemPerformance.AllTrades.Count,
                submittedTrendLong,
                submittedTrendShort,
                submittedFadeLong,
                submittedFadeShort,
                blockSession,
                blockCooldown,
                blockPosition,
                blockPending,
                blockSameBar,
                blockDailyLoss,
                blockTrendSuppress,
                modeChanges,
                dayPnl));
        }
    }
}
