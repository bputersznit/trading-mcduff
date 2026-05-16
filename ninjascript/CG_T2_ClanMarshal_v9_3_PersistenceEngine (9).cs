// =================================================================================================
// CG_T2_ClanMarshal_v9_3_PersistenceEngine.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-07 01:52:00 EDT
//
// B93 PERCENTILE++ 1-TICK SYNTHETIC BAR ENGINE — SAME NAME
//
// Apply to a 1 Tick MNQ chart.
// The strategy builds synthetic time bars internally from ticks and ONLY evaluates state/entries
// when a synthetic bar closes. This fixes the prior no-trade behavior where diagnostics showed
// exp=0.00, range=1.0, p80=1.0 and pctBlk rising.
//
// Features:
// - One MNQ only.
// - No overlap.
// - No fades.
// - One trade per auction leg.
// - Percentile++ expansion gate.
// - Anti-late-trend filter.
// - Fill-price stop/target brackets submitted after entry fill.
// =================================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_T2_ClanMarshal_v9_3_PersistenceEngine : Strategy
    {
        private enum AuctionState { None, BuildingUp, ConfirmedUp, ExhaustingUp, BuildingDown, ConfirmedDown, ExhaustingDown }
        private enum EntryKind { None, TrendLong, TrendShort }

        [NinjaScriptProperty]
        [Display(Name="Use Session Filter", GroupName="01 Session", Order=1)]
        public bool UseSessionFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0,235959)]
        [Display(Name="Session Start HHmmss", GroupName="01 Session", Order=2)]
        public int SessionStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0,235959)]
        [Display(Name="Session End HHmmss", GroupName="01 Session", Order=3)]
        public int SessionEndTime { get; set; }

        [NinjaScriptProperty]
        [Range(10,600)]
        [Display(Name="Synthetic Bar Seconds", GroupName="02 Synthetic", Order=1)]
        public int SyntheticBarSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(10,300)]
        [Display(Name="Percentile Lookback Synthetic Bars", GroupName="02 Synthetic", Order=2)]
        public int PercentileLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1,100)]
        [Display(Name="Min Synthetic Range Ticks", GroupName="03 Percentile++", Order=1)]
        public int MinSyntheticRangeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.1,5.0)]
        [Display(Name="Min Expansion Score", GroupName="03 Percentile++", Order=2)]
        public double MinExpansionScore { get; set; }

        [NinjaScriptProperty]
        [Range(0.1,1.0)]
        [Display(Name="Expansion Decay Ratio", GroupName="03 Percentile++", Order=3)]
        public double ExpansionDecayRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1,300)]
        [Display(Name="Max Cumulative Trend Ticks", GroupName="03 Percentile++", Order=4)]
        public int MaxCumulativeTrendTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1,100)]
        [Display(Name="Build Energy", GroupName="04 State", Order=1)]
        public double BuildEnergy { get; set; }

        [NinjaScriptProperty]
        [Range(1,100)]
        [Display(Name="Confirm Energy", GroupName="04 State", Order=2)]
        public double ConfirmEnergy { get; set; }

        [NinjaScriptProperty]
        [Range(0,100)]
        [Display(Name="Exit Energy", GroupName="04 State", Order=3)]
        public double ExitEnergy { get; set; }

        [NinjaScriptProperty]
        [Range(0.1,0.99)]
        [Display(Name="Energy Decay", GroupName="04 State", Order=4)]
        public double EnergyDecay { get; set; }

        [NinjaScriptProperty]
        [Range(1,100)]
        [Display(Name="Exhaustion Threshold", GroupName="04 State", Order=5)]
        public double ExhaustionThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1,200)]
        [Display(Name="Trend Target Ticks", GroupName="05 Bracket", Order=1)]
        public int TrendTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1,200)]
        [Display(Name="Trend Stop Ticks", GroupName="05 Bracket", Order=2)]
        public int TrendStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1,100)]
        [Display(Name="Pullback Min Ticks", GroupName="06 Entry", Order=1)]
        public int PullbackMinTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1,200)]
        [Display(Name="Pullback Max Ticks", GroupName="06 Entry", Order=2)]
        public int PullbackMaxTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1,100)]
        [Display(Name="Reclaim Ticks", GroupName="06 Entry", Order=3)]
        public int ReclaimTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1,100)]
        [Display(Name="Momentum Near Extreme Ticks", GroupName="06 Entry", Order=4)]
        public int MomentumNearExtremeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0,20)]
        [Display(Name="Max Same Direction Stopouts", GroupName="07 Governance", Order=1)]
        public int MaxSameDirectionStopouts { get; set; }

        [NinjaScriptProperty]
        [Range(0,100)]
        [Display(Name="Loss Embargo Synthetic Bars", GroupName="07 Governance", Order=2)]
        public int LossEmbargoSyntheticBars { get; set; }

        [NinjaScriptProperty]
        [Range(0,100)]
        [Display(Name="Post Exit Cooldown Synthetic Bars", GroupName="07 Governance", Order=3)]
        public int PostExitCooldownSyntheticBars { get; set; }

        [NinjaScriptProperty]
        [Range(1,500)]
        [Display(Name="Diagnostic Every Synthetic Bars", GroupName="08 Diagnostics", Order=1)]
        public int DiagnosticEverySyntheticBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Print Mode Changes", GroupName="08 Diagnostics", Order=2)]
        public bool PrintModeChanges { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Print Entries", GroupName="08 Diagnostics", Order=3)]
        public bool PrintEntries { get; set; }

        private AuctionState state;
        private EntryKind pendingEntryKind, activeEntryKind;

        private bool synthInit, synthClosedThisTick;
        private DateTime synthStart;
        private double so, sh, sl, sc;
        private double o1, h1, l1, c1;
        private double o2, h2, l2, c2;
        private int synSerial;

        private Queue<double> rangeHist;
        private double rangeTicks, p50, p80, expScore, priorExpScore, energy, exhaust;
        private bool pctOk, freshLong, freshShort, lateLong, lateShort;

        private DateTime sessionDate;
        private bool sessionInit;
        private double sessionHigh, sessionLow, trendHigh, trendLow;

        private bool longUsed, shortUsed, pullbackArmed, pendingEntry;
        private double pullbackExtreme;
        private int lastEntryBar, lastExitSyn;
        private int longStops, shortStops, longEmbargoUntil, shortEmbargoUntil;

        private long tickCount, longCount, shortCount, stateChanges, pctBlk, lateBlk, legBlk, coolBlk, posBlk, rngBlk, embBlk, sessBlk, brackets;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_3_PersistenceEngine";
                Description = "B93 Percentile++ 1-tick synthetic-bar engine. Same name. Decision logic only on synthetic close.";

                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
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

                UseSessionFilter = true;
                SessionStartTime = 83000;
                SessionEndTime = 150000;

                SyntheticBarSeconds = 60;
                PercentileLookbackBars = 60;

                MinSyntheticRangeTicks = 8;
                MinExpansionScore = 0.45;
                ExpansionDecayRatio = 0.60;
                MaxCumulativeTrendTicks = 100;

                BuildEnergy = 12.0;
                ConfirmEnergy = 24.0;
                ExitEnergy = 6.0;
                EnergyDecay = 0.78;
                ExhaustionThreshold = 28.0;

                TrendTargetTicks = 32;
                TrendStopTicks = 18;

                PullbackMinTicks = 8;
                PullbackMaxTicks = 44;
                ReclaimTicks = 4;
                MomentumNearExtremeTicks = 10;

                MaxSameDirectionStopouts = 2;
                LossEmbargoSyntheticBars = 12;
                PostExitCooldownSyntheticBars = 2;

                DiagnosticEverySyntheticBars = 1;
                PrintModeChanges = true;
                PrintEntries = true;
            }
            else if (State == State.Configure)
            {
                // Brackets are submitted after actual entry fill.
            }
            else if (State == State.DataLoaded)
            {
                ResetRuntime();
                Print(Name + " loaded. B93 PERCENTILE++ 1-TICK SYNTHETIC ENGINE. Apply to 1 Tick chart.");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            tickCount++;
            synthClosedThisTick = false;

            DateTime now = Time[0];
            double px = Close[0];

            ResetSessionIfNeeded(now, px);
            UpdateSynthetic(now, px);

            if (!synthClosedThisTick)
                return;

            UpdateMetrics();
            UpdateEnergy();
            UpdateState(now);
            UpdateWatermarks();
            UpdatePercentile();
            ResetLegsIfNeutral();

            if (!InSession(now))
            {
                sessBlk++;
                Diag(now);
                return;
            }

            if (!CanEnter())
            {
                Diag(now);
                return;
            }

            if (state == AuctionState.ConfirmedUp)
            {
                if (longUsed) legBlk++;
                else if (IsLongEmbargoed()) embBlk++;
                else if (rangeTicks < MinSyntheticRangeTicks) rngBlk++;
                else if (!pctOk || !freshLong) pctBlk++;
                else if (lateLong) lateBlk++;
                else if (!TryPullbackLong(now)) TryMomentumLong(now);
            }
            else if (state == AuctionState.ConfirmedDown)
            {
                if (shortUsed) legBlk++;
                else if (IsShortEmbargoed()) embBlk++;
                else if (rangeTicks < MinSyntheticRangeTicks) rngBlk++;
                else if (!pctOk || !freshShort) pctBlk++;
                else if (lateShort) lateBlk++;
                else if (!TryPullbackShort(now)) TryMomentumShort(now);
            }

            Diag(now);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null || execution.Order.OrderState != OrderState.Filled)
                return;

            string name = execution.Order.Name ?? string.Empty;
            bool isEntry = name == "B93_Trend_Long" || name == "B93_Trend_Short";

            if (isEntry)
            {
                pendingEntry = false;
                activeEntryKind = pendingEntryKind;
                pendingEntryKind = EntryKind.None;
                lastEntryBar = CurrentBar;

                if (activeEntryKind == EntryKind.TrendLong) longUsed = true;
                if (activeEntryKind == EntryKind.TrendShort) shortUsed = true;

                SubmitBrackets(name, price, quantity, time);
                return;
            }

            bool isStop = name == "B93_Stop_Long" || name == "B93_Stop_Short";
            bool isTarget = name == "B93_Target_Long" || name == "B93_Target_Short";

            if (isStop || isTarget)
            {
                if (isStop) RegisterStop(name);
                if (isTarget) RegisterTarget(name);

                activeEntryKind = EntryKind.None;
                pendingEntry = false;
                lastExitSyn = synSerial;
                pullbackArmed = false;
            }

            if (Position.MarketPosition == MarketPosition.Flat && !isEntry)
            {
                activeEntryKind = EntryKind.None;
                pendingEntry = false;
                lastExitSyn = synSerial;
                pullbackArmed = false;
            }
        }

        private void UpdateSynthetic(DateTime now, double px)
        {
            if (!synthInit)
            {
                synthInit = true;
                synthStart = now;
                so = sh = sl = sc = px;
                return;
            }

            if ((now - synthStart).TotalSeconds >= SyntheticBarSeconds)
            {
                o2 = o1; h2 = h1; l2 = l1; c2 = c1;
                o1 = so; h1 = sh; l1 = sl; c1 = sc;
                synSerial++;
                synthClosedThisTick = true;

                synthStart = now;
                so = sh = sl = sc = px;
                return;
            }

            if (px > sh) sh = px;
            if (px < sl) sl = px;
            sc = px;
        }

        private void SubmitBrackets(string entrySignal, double fill, int qty, DateTime time)
        {
            double stop, target;

            if (activeEntryKind == EntryKind.TrendLong)
            {
                stop = Instrument.MasterInstrument.RoundToTickSize(fill - TrendStopTicks * TickSize);
                target = Instrument.MasterInstrument.RoundToTickSize(fill + TrendTargetTicks * TickSize);
                brackets++;
                if (PrintEntries) Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} BRACKET LONG fill={1:F2} stop={2:F2} target={3:F2}", time, fill, stop, target));
                ExitLongStopMarket(0, true, qty, stop, "B93_Stop_Long", entrySignal);
                ExitLongLimit(0, true, qty, target, "B93_Target_Long", entrySignal);
            }
            else if (activeEntryKind == EntryKind.TrendShort)
            {
                stop = Instrument.MasterInstrument.RoundToTickSize(fill + TrendStopTicks * TickSize);
                target = Instrument.MasterInstrument.RoundToTickSize(fill - TrendTargetTicks * TickSize);
                brackets++;
                if (PrintEntries) Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} BRACKET SHORT fill={1:F2} stop={2:F2} target={3:F2}", time, fill, stop, target));
                ExitShortStopMarket(0, true, qty, stop, "B93_Stop_Short", entrySignal);
                ExitShortLimit(0, true, qty, target, "B93_Target_Short", entrySignal);
            }
        }

        private void UpdateMetrics()
        {
            rangeTicks = Math.Max(1.0, (h1 - l1) / TickSize);
            rangeHist.Enqueue(rangeTicks);
            if (rangeHist.Count > PercentileLookbackBars) rangeHist.Dequeue();
            ComputePercentiles();

            if (h1 > sessionHigh) sessionHigh = h1;
            if (l1 < sessionLow) sessionLow = l1;
        }

        private void ComputePercentiles()
        {
            if (rangeHist.Count < 10)
            {
                p50 = MinSyntheticRangeTicks;
                p80 = MinSyntheticRangeTicks;
                return;
            }

            List<double> v = new List<double>(rangeHist);
            v.Sort();
            p50 = Math.Max(1.0, v[(int)Math.Floor((v.Count - 1) * 0.50)]);
            p80 = Math.Max(1.0, v[(int)Math.Floor((v.Count - 1) * 0.80)]);
        }

        private void UpdateEnergy()
        {
            double move = c1 - o1;
            double priorMove = c2 - o2;
            double slope = c1 - c2;
            double eff = Math.Abs(move) / Math.Max(TickSize, h1 - l1);
            double ev = 0;

            if (move > 0) ev += 6 + 4 * eff;
            else if (move < 0) ev -= 6 + 4 * eff;

            if (priorMove > 0) ev += 3;
            else if (priorMove < 0) ev -= 3;

            if (slope > 0) ev += 5;
            else if (slope < 0) ev -= 5;

            if (rangeTicks >= p80)
                ev += Math.Sign(move == 0 ? slope : move) * 2;

            energy = energy * EnergyDecay + ev;
            if (energy > 100) energy = 100;
            if (energy < -100) energy = -100;

            if (rangeTicks >= p80 * 1.5 && eff < 0.40) exhaust += 4;
            else if (rangeTicks >= p80 * 2.0) exhaust += 3;
            else exhaust *= 0.72;

            exhaust *= 0.86;
            if (exhaust > 60) exhaust = 60;
            if (exhaust < 0) exhaust = 0;
        }

        private void UpdatePercentile()
        {
            double moveTicks = Math.Abs(c1 - o1) / TickSize;
            double eff = moveTicks / Math.Max(1.0, rangeTicks);
            double clv = (c1 - l1) / Math.Max(TickSize, h1 - l1);
            clv = Math.Max(0.0, Math.Min(1.0, clv));

            priorExpScore = expScore;
            double quality = energy >= 0 ? clv : 1.0 - clv;
            expScore = (rangeTicks / Math.Max(1.0, p50)) * eff * quality;

            bool decaying = priorExpScore > 0 && expScore < priorExpScore * ExpansionDecayRatio;
            double upTicks = (c1 - sessionLow) / TickSize;
            double downTicks = (sessionHigh - c1) / TickSize;
            bool upperWick = (h1 - c1) / Math.Max(TickSize, h1 - l1) > 0.42;
            bool lowerWick = (c1 - l1) / Math.Max(TickSize, h1 - l1) > 0.42;

            pctOk = rangeTicks >= Math.Max(MinSyntheticRangeTicks, p80 * 0.70) && expScore >= MinExpansionScore;
            lateLong = upTicks > MaxCumulativeTrendTicks || (upperWick && decaying);
            lateShort = downTicks > MaxCumulativeTrendTicks || (lowerWick && decaying);
            freshLong = energy >= ConfirmEnergy && pctOk && !decaying && !lateLong;
            freshShort = energy <= -ConfirmEnergy && pctOk && !decaying && !lateShort;
        }

        private void UpdateState(DateTime now)
        {
            AuctionState old = state;

            switch (state)
            {
                case AuctionState.None:
                    if (energy >= BuildEnergy) state = AuctionState.BuildingUp;
                    else if (energy <= -BuildEnergy) state = AuctionState.BuildingDown;
                    break;
                case AuctionState.BuildingUp:
                    if (energy >= ConfirmEnergy) state = AuctionState.ConfirmedUp;
                    else if (energy <= ExitEnergy) state = AuctionState.None;
                    break;
                case AuctionState.ConfirmedUp:
                    if (exhaust >= ExhaustionThreshold) state = AuctionState.ExhaustingUp;
                    else if (energy <= -ExitEnergy) state = AuctionState.None;
                    break;
                case AuctionState.ExhaustingUp:
                    if (energy >= ConfirmEnergy && exhaust < ExhaustionThreshold * 0.5) state = AuctionState.ConfirmedUp;
                    else if (energy <= -BuildEnergy) state = AuctionState.BuildingDown;
                    else if (Math.Abs(energy) <= ExitEnergy) state = AuctionState.None;
                    break;
                case AuctionState.BuildingDown:
                    if (energy <= -ConfirmEnergy) state = AuctionState.ConfirmedDown;
                    else if (energy >= -ExitEnergy) state = AuctionState.None;
                    break;
                case AuctionState.ConfirmedDown:
                    if (exhaust >= ExhaustionThreshold) state = AuctionState.ExhaustingDown;
                    else if (energy >= ExitEnergy) state = AuctionState.None;
                    break;
                case AuctionState.ExhaustingDown:
                    if (energy <= -ConfirmEnergy && exhaust < ExhaustionThreshold * 0.5) state = AuctionState.ConfirmedDown;
                    else if (energy >= BuildEnergy) state = AuctionState.BuildingUp;
                    else if (Math.Abs(energy) <= ExitEnergy) state = AuctionState.None;
                    break;
            }

            if (state != old)
            {
                stateChanges++;
                pullbackArmed = false;
                if (PrintModeChanges)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} AUCTION {1} syn={2} energy={3:F2} exp={4:F2} range={5:F1} p80={6:F1}", now, state, synSerial, energy, expScore, rangeTicks, p80));
            }
        }

        private void UpdateWatermarks()
        {
            if (state == AuctionState.BuildingUp || state == AuctionState.ConfirmedUp || state == AuctionState.ExhaustingUp)
            {
                if (h1 > trendHigh || trendHigh == double.MinValue) trendHigh = h1;
            }
            else trendHigh = c1;

            if (state == AuctionState.BuildingDown || state == AuctionState.ConfirmedDown || state == AuctionState.ExhaustingDown)
            {
                if (l1 < trendLow || trendLow == double.MaxValue) trendLow = l1;
            }
            else trendLow = c1;
        }

        private bool TryMomentumLong(DateTime now)
        {
            if (energy >= ConfirmEnergy && exhaust < ExhaustionThreshold && c1 >= c2 && (trendHigh - c1) / TickSize <= MomentumNearExtremeTicks)
            {
                SubmitLong(now, "MomentumLong");
                return true;
            }
            return false;
        }

        private bool TryMomentumShort(DateTime now)
        {
            if (energy <= -ConfirmEnergy && exhaust < ExhaustionThreshold && c1 <= c2 && (c1 - trendLow) / TickSize <= MomentumNearExtremeTicks)
            {
                SubmitShort(now, "MomentumShort");
                return true;
            }
            return false;
        }

        private bool TryPullbackLong(DateTime now)
        {
            double pb = (trendHigh - c1) / TickSize;
            if (!pullbackArmed)
            {
                if (pb >= PullbackMinTicks && pb <= PullbackMaxTicks) { pullbackArmed = true; pullbackExtreme = c1; }
                return false;
            }
            if (c1 < pullbackExtreme) pullbackExtreme = c1;
            if ((c1 - pullbackExtreme) / TickSize >= ReclaimTicks && pb <= PullbackMaxTicks)
            {
                SubmitLong(now, "PullbackLong");
                return true;
            }
            if (pb > PullbackMaxTicks) pullbackArmed = false;
            return false;
        }

        private bool TryPullbackShort(DateTime now)
        {
            double pb = (c1 - trendLow) / TickSize;
            if (!pullbackArmed)
            {
                if (pb >= PullbackMinTicks && pb <= PullbackMaxTicks) { pullbackArmed = true; pullbackExtreme = c1; }
                return false;
            }
            if (c1 > pullbackExtreme) pullbackExtreme = c1;
            if ((pullbackExtreme - c1) / TickSize >= ReclaimTicks && pb <= PullbackMaxTicks)
            {
                SubmitShort(now, "PullbackShort");
                return true;
            }
            if (pb > PullbackMaxTicks) pullbackArmed = false;
            return false;
        }

        private void SubmitLong(DateTime now, string reason)
        {
            pendingEntry = true;
            pendingEntryKind = EntryKind.TrendLong;
            lastEntryBar = CurrentBar;
            pullbackArmed = false;
            longCount++;
            if (PrintEntries) Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER LONG {1} syn={2} px={3:F2} energy={4:F2} exp={5:F2} range={6:F1} p80={7:F1}", now, reason, synSerial, c1, energy, expScore, rangeTicks, p80));
            EnterLong(1, "B93_Trend_Long");
        }

        private void SubmitShort(DateTime now, string reason)
        {
            pendingEntry = true;
            pendingEntryKind = EntryKind.TrendShort;
            lastEntryBar = CurrentBar;
            pullbackArmed = false;
            shortCount++;
            if (PrintEntries) Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER SHORT {1} syn={2} px={3:F2} energy={4:F2} exp={5:F2} range={6:F1} p80={7:F1}", now, reason, synSerial, c1, energy, expScore, rangeTicks, p80));
            EnterShort(1, "B93_Trend_Short");
        }

        private bool CanEnter()
        {
            if (pendingEntry) { posBlk++; return false; }
            if (Position.MarketPosition != MarketPosition.Flat) { posBlk++; return false; }
            if (CurrentBar == lastEntryBar) { coolBlk++; return false; }
            if (synSerial - lastExitSyn <= PostExitCooldownSyntheticBars) { coolBlk++; return false; }
            return true;
        }

        private void ResetLegsIfNeutral()
        {
            if (Math.Abs(energy) <= ExitEnergy || state == AuctionState.None)
            {
                longUsed = false;
                shortUsed = false;
            }
        }

        private bool IsLongEmbargoed() { return longEmbargoUntil > 0 && synSerial < longEmbargoUntil; }
        private bool IsShortEmbargoed() { return shortEmbargoUntil > 0 && synSerial < shortEmbargoUntil; }

        private void RegisterStop(string stopName)
        {
            if (stopName == "B93_Stop_Long")
            {
                longStops++;
                shortStops = 0;
                if (MaxSameDirectionStopouts > 0 && longStops >= MaxSameDirectionStopouts)
                    longEmbargoUntil = synSerial + LossEmbargoSyntheticBars;
            }
            else if (stopName == "B93_Stop_Short")
            {
                shortStops++;
                longStops = 0;
                if (MaxSameDirectionStopouts > 0 && shortStops >= MaxSameDirectionStopouts)
                    shortEmbargoUntil = synSerial + LossEmbargoSyntheticBars;
            }
        }

        private void RegisterTarget(string targetName)
        {
            if (targetName == "B93_Target_Long") longStops = 0;
            if (targetName == "B93_Target_Short") shortStops = 0;
        }

        private void ResetRuntime()
        {
            state = AuctionState.None;
            pendingEntryKind = EntryKind.None;
            activeEntryKind = EntryKind.None;
            energy = exhaust = 0;

            synthInit = false;
            synthStart = Core.Globals.MinDate;
            so = sh = sl = sc = 0;
            o1 = h1 = l1 = c1 = 0;
            o2 = h2 = l2 = c2 = 0;
            synSerial = 0;
            synthClosedThisTick = false;

            rangeHist = new Queue<double>();
            rangeTicks = 0;
            p50 = p80 = MinSyntheticRangeTicks;
            expScore = priorExpScore = 0;
            pctOk = freshLong = freshShort = lateLong = lateShort = false;

            sessionDate = Core.Globals.MinDate;
            sessionInit = false;
            sessionHigh = double.MinValue;
            sessionLow = double.MaxValue;
            trendHigh = double.MinValue;
            trendLow = double.MaxValue;

            longUsed = shortUsed = pullbackArmed = pendingEntry = false;
            pullbackExtreme = 0;
            lastEntryBar = -999999;
            lastExitSyn = -999999;
            longStops = shortStops = 0;
            longEmbargoUntil = shortEmbargoUntil = -1;

            tickCount = longCount = shortCount = stateChanges = pctBlk = lateBlk = legBlk = coolBlk = posBlk = rngBlk = embBlk = sessBlk = brackets = 0;
        }

        private void ResetSessionIfNeeded(DateTime now, double px)
        {
            if (!sessionInit || now.Date != sessionDate)
            {
                sessionInit = true;
                sessionDate = now.Date;
                sessionHigh = sessionLow = trendHigh = trendLow = px;
                state = AuctionState.None;
                energy = exhaust = 0;
                longUsed = shortUsed = pullbackArmed = false;
                longStops = shortStops = 0;
                longEmbargoUntil = shortEmbargoUntil = -1;
            }
        }

        private bool InSession(DateTime now)
        {
            if (!UseSessionFilter) return true;
            int t = ToTime(now);
            return t >= SessionStartTime && t <= SessionEndTime;
        }

        private void Diag(DateTime now)
        {
            if (DiagnosticEverySyntheticBars <= 0) return;
            if (synSerial % DiagnosticEverySyntheticBars != 0) return;

            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} DIAG syn={1} state={2} energy={3:F2} exp={4:F2} range={5:F1} p50={6:F1} p80={7:F1} pos={8} L={9} S={10} pctBlk={11} lateBlk={12} legBlk={13} coolBlk={14} states={15}",
                now, synSerial, state, energy, expScore, rangeTicks, p50, p80, Position.MarketPosition, longCount, shortCount, pctBlk, lateBlk, legBlk, coolBlk, stateChanges));
        }
    }
}
