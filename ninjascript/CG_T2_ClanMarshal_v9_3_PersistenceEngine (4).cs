// =================================================================================================
// CG_T2_ClanMarshal_v9_3_PersistenceEngine.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-06 17:58:00 EDT
//
// B93 FIXED SAME-NAME BUILD
//
// Fixes:
//   1. Default session start is 08:30:00 chart time, not 09:30:00.
//   2. Auction energy updates only when an internal synthetic bar completes.
//   3. Energy/exhaustion are clamped to prevent runaway values inside one minute.
//   4. Momentum entries are allowed during confirmed expansion.
//   5. No emergency lockout. One MNQ only. No overlap.
//
// Preferred chart: MNQ 1 Tick. Also usable for playback checking on 1 Minute with Tick Replay.
// =================================================================================================

#region Using declarations
using System;
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

        [NinjaScriptProperty]
        [Display(Name = "Use Session Filter", GroupName = "01 Session", Order = 1)]
        public bool UseSessionFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Session Start HHmmss", GroupName = "01 Session", Order = 2)]
        public int SessionStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Session End HHmmss", GroupName = "01 Session", Order = 3)]
        public int SessionEndTime { get; set; }

        [NinjaScriptProperty]
        [Range(10, 300)]
        [Display(Name = "Synthetic Bar Seconds", GroupName = "02 Auction", Order = 1)]
        public int SyntheticBarSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Synthetic Bars Required", GroupName = "02 Auction", Order = 2)]
        public int SyntheticBarsRequired { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Build Energy", GroupName = "03 State", Order = 1)]
        public double BuildEnergy { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Confirm Energy", GroupName = "03 State", Order = 2)]
        public double ConfirmEnergy { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Exit Energy", GroupName = "03 State", Order = 3)]
        public double ExitEnergy { get; set; }

        [NinjaScriptProperty]
        [Range(0.50, 0.99)]
        [Display(Name = "Energy Decay Per Synthetic Bar", GroupName = "03 State", Order = 4)]
        public double EnergyDecay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Exhaustion Threshold", GroupName = "03 State", Order = 5)]
        public double ExhaustionThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Trend Target Ticks", GroupName = "04 Brackets", Order = 1)]
        public int TrendTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Trend Stop Ticks", GroupName = "04 Brackets", Order = 2)]
        public int TrendStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Fade Target Ticks", GroupName = "04 Brackets", Order = 3)]
        public int FadeTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Fade Stop Ticks", GroupName = "04 Brackets", Order = 4)]
        public int FadeStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Pullback Min Ticks", GroupName = "05 Entries", Order = 1)]
        public int PullbackMinTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Pullback Max Ticks", GroupName = "05 Entries", Order = 2)]
        public int PullbackMaxTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Reclaim Ticks", GroupName = "05 Entries", Order = 3)]
        public int ReclaimTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Momentum Trigger Ticks", GroupName = "05 Entries", Order = 4)]
        public int MomentumTriggerTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Fade Extreme Ticks", GroupName = "05 Entries", Order = 5)]
        public int FadeExtremeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 300)]
        [Display(Name = "Entry Cooldown Seconds", GroupName = "06 Governance", Order = 1)]
        public int EntryCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 300)]
        [Display(Name = "Post Exit Cooldown Seconds", GroupName = "06 Governance", Order = 2)]
        public int PostExitCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(100, 100000)]
        [Display(Name = "Diagnostic Every Ticks", GroupName = "07 Diagnostics", Order = 1)]
        public int DiagnosticEveryTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Print Mode Changes", GroupName = "07 Diagnostics", Order = 2)]
        public bool PrintModeChanges { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Print Entries", GroupName = "07 Diagnostics", Order = 3)]
        public bool PrintEntries { get; set; }

        private AuctionState state;
        private double energy, exhaust;
        private bool synthInit, justClosedSynth;
        private DateTime synthStart, sessionDate, lastEntryTime, lastExitTime;
        private double so, sh, sl, sc, o1, h1, l1, c1, o2, h2, l2, c2;
        private int synthDone, lastEntryBar;
        private bool sessionInit;
        private double sessionHigh, sessionLow, trendHigh, trendLow;
        private bool pullbackArmed, pendingEntry;
        private double pullbackExtreme;
        private long tickCounter, modeChanges, tl, ts, fl, fs, sessBlk, coolBlk, posBlk;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_3_PersistenceEngine";
                Description = "B93 fixed same-name MNQ persistence engine. 08:30 chart-time session. Synthetic-bar energy only.";
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
                BarsRequiredToTrade = 10;
                TraceOrders = false;

                UseSessionFilter = true;
                SessionStartTime = 83000;
                SessionEndTime = 150000;
                SyntheticBarSeconds = 60;
                SyntheticBarsRequired = 2;
                BuildEnergy = 10.0;
                ConfirmEnergy = 20.0;
                ExitEnergy = 5.0;
                EnergyDecay = 0.72;
                ExhaustionThreshold = 24.0;
                TrendTargetTicks = 24;
                TrendStopTicks = 16;
                FadeTargetTicks = 10;
                FadeStopTicks = 12;
                PullbackMinTicks = 3;
                PullbackMaxTicks = 28;
                ReclaimTicks = 2;
                MomentumTriggerTicks = 6;
                FadeExtremeTicks = 8;
                EntryCooldownSeconds = 2;
                PostExitCooldownSeconds = 2;
                DiagnosticEveryTicks = 1000;
                PrintModeChanges = true;
                PrintEntries = true;
            }
            else if (State == State.Configure)
            {
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
                ResetRuntime();
                Print(Name + " loaded. B93 FIXED. SessionStart=083000 chart time. Energy per synthetic bar only. One MNQ only.");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;
            tickCounter++;
            justClosedSynth = false;
            DateTime now = Time[0];
            double px = Close[0];
            ResetSessionIfNeeded(now, px);
            UpdateSynthetic(now, px);
            if (justClosedSynth && synthDone >= SyntheticBarsRequired)
            {
                UpdateEnergy();
                UpdateState(now);
                UpdateWatermarks();
            }
            if (!InSession(now)) { sessBlk++; Diag(now, px); return; }
            if (!CanEnter(now)) { Diag(now, px); return; }
            bool submitted = false;
            if (state == AuctionState.ConfirmedUp)
            {
                submitted = TryMomentumLong(now, px);
                if (!submitted) submitted = TryPullbackLong(now, px);
            }
            else if (state == AuctionState.ConfirmedDown)
            {
                submitted = TryMomentumShort(now, px);
                if (!submitted) submitted = TryPullbackShort(now, px);
            }
            else if (state == AuctionState.None)
            {
                submitted = TryFadeLong(now, px);
                if (!submitted) submitted = TryFadeShort(now, px);
            }
            Diag(now, px);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null || execution.Order.OrderState != OrderState.Filled) return;
            string n = execution.Order.Name ?? string.Empty;
            if (n == "B93_Trend_Long" || n == "B93_Trend_Short" || n == "B93_Fade_Long" || n == "B93_Fade_Short")
            {
                pendingEntry = false;
                lastEntryTime = time;
                lastEntryBar = CurrentBar;
            }
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                pendingEntry = false;
                lastExitTime = time;
                pullbackArmed = false;
            }
        }

        private void ResetRuntime()
        {
            state = AuctionState.None;
            energy = exhaust = 0;
            synthInit = false;
            synthStart = Core.Globals.MinDate;
            so = sh = sl = sc = o1 = h1 = l1 = c1 = o2 = h2 = l2 = c2 = 0;
            synthDone = 0;
            sessionInit = false;
            sessionDate = Core.Globals.MinDate;
            sessionHigh = double.MinValue;
            sessionLow = double.MaxValue;
            trendHigh = double.MinValue;
            trendLow = double.MaxValue;
            pullbackArmed = pendingEntry = false;
            pullbackExtreme = 0;
            lastEntryBar = -999999;
            lastEntryTime = Core.Globals.MinDate;
            lastExitTime = Core.Globals.MinDate;
            tickCounter = modeChanges = tl = ts = fl = fs = sessBlk = coolBlk = posBlk = 0;
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
                pullbackArmed = false;
            }
            if (px > sessionHigh) sessionHigh = px;
            if (px < sessionLow) sessionLow = px;
        }

        private bool InSession(DateTime now)
        {
            if (!UseSessionFilter) return true;
            int t = ToTime(now);
            return t >= SessionStartTime && t <= SessionEndTime;
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
                synthDone++;
                justClosedSynth = true;
                synthStart = now;
                so = sh = sl = sc = px;
                return;
            }
            if (px > sh) sh = px;
            if (px < sl) sl = px;
            sc = px;
        }

        private void UpdateEnergy()
        {
            double move1 = c1 - o1;
            double move2 = c2 - o2;
            double slope = c1 - c2;
            double range = Math.Max(TickSize, h1 - l1);
            double eff = Math.Abs(move1) / range;
            double rangeTicks = range / TickSize;
            double ev = 0;
            if (move1 > 0) ev += 6.0 + 4.0 * eff; else if (move1 < 0) ev -= 6.0 + 4.0 * eff;
            if (move2 > 0) ev += 3.0; else if (move2 < 0) ev -= 3.0;
            if (slope > 0) ev += 5.0; else if (slope < 0) ev -= 5.0;
            if (rangeTicks >= 20) ev += Math.Sign(move1 == 0 ? slope : move1) * 2.0;
            energy = energy * EnergyDecay + ev;
            if (energy > 100) energy = 100;
            if (energy < -100) energy = -100;
            if (rangeTicks >= 32 && eff < 0.40) exhaust += 4.0;
            else if (rangeTicks >= 48) exhaust += 3.0;
            else exhaust *= 0.55;
            exhaust *= 0.82;
            if (exhaust > 60) exhaust = 60;
            if (exhaust < 0) exhaust = 0;
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
                    if (energy >= ConfirmEnergy && exhaust < ExhaustionThreshold * 0.50) state = AuctionState.ConfirmedUp;
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
                    if (energy <= -ConfirmEnergy && exhaust < ExhaustionThreshold * 0.50) state = AuctionState.ConfirmedDown;
                    else if (energy >= BuildEnergy) state = AuctionState.BuildingUp;
                    else if (Math.Abs(energy) <= ExitEnergy) state = AuctionState.None;
                    break;
            }
            if (state != old)
            {
                modeChanges++;
                pullbackArmed = false;
                if (PrintModeChanges)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} AUCTION MODE {1} energy={2:F2} exhaustion={3:F2}", now, state, energy, exhaust));
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

        private bool CanEnter(DateTime now)
        {
            if (pendingEntry) { posBlk++; return false; }
            if (Position.MarketPosition != MarketPosition.Flat) { posBlk++; return false; }
            if (CurrentBar == lastEntryBar) { coolBlk++; return false; }
            if (lastEntryTime != Core.Globals.MinDate && (now - lastEntryTime).TotalSeconds < EntryCooldownSeconds) { coolBlk++; return false; }
            if (lastExitTime != Core.Globals.MinDate && (now - lastExitTime).TotalSeconds < PostExitCooldownSeconds) { coolBlk++; return false; }
            return true;
        }

        private bool TryMomentumLong(DateTime now, double px)
        {
            double nearHighTicks = (trendHigh - px) / TickSize;
            if (energy >= ConfirmEnergy && exhaust < ExhaustionThreshold && px >= c1 && nearHighTicks <= MomentumTriggerTicks)
            {
                SubmitLong(now, px, "MomentumLong"); return true;
            }
            return false;
        }

        private bool TryMomentumShort(DateTime now, double px)
        {
            double nearLowTicks = (px - trendLow) / TickSize;
            if (energy <= -ConfirmEnergy && exhaust < ExhaustionThreshold && px <= c1 && nearLowTicks <= MomentumTriggerTicks)
            {
                SubmitShort(now, px, "MomentumShort"); return true;
            }
            return false;
        }

        private bool TryPullbackLong(DateTime now, double px)
        {
            double pb = (trendHigh - px) / TickSize;
            if (!pullbackArmed)
            {
                if (pb >= PullbackMinTicks && pb <= PullbackMaxTicks) { pullbackArmed = true; pullbackExtreme = px; }
                return false;
            }
            if (px < pullbackExtreme) pullbackExtreme = px;
            if ((px - pullbackExtreme) / TickSize >= ReclaimTicks && pb <= PullbackMaxTicks) { SubmitLong(now, px, "PullbackLong"); return true; }
            if (pb > PullbackMaxTicks) pullbackArmed = false;
            return false;
        }

        private bool TryPullbackShort(DateTime now, double px)
        {
            double pb = (px - trendLow) / TickSize;
            if (!pullbackArmed)
            {
                if (pb >= PullbackMinTicks && pb <= PullbackMaxTicks) { pullbackArmed = true; pullbackExtreme = px; }
                return false;
            }
            if (px > pullbackExtreme) pullbackExtreme = px;
            if ((pullbackExtreme - px) / TickSize >= ReclaimTicks && pb <= PullbackMaxTicks) { SubmitShort(now, px, "PullbackShort"); return true; }
            if (pb > PullbackMaxTicks) pullbackArmed = false;
            return false;
        }

        private bool TryFadeLong(DateTime now, double px)
        {
            double d = (px - sessionLow) / TickSize;
            if (d <= FadeExtremeTicks)
            {
                SetStopLoss("B93_Fade_Long", CalculationMode.Ticks, FadeStopTicks, false);
                SetProfitTarget("B93_Fade_Long", CalculationMode.Ticks, FadeTargetTicks);
                pendingEntry = true; lastEntryBar = CurrentBar; lastEntryTime = now; fl++;
                if (PrintEntries) Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER LONG Fade px={1:F2} distLowTicks={2:F1}", now, px, d));
                EnterLong(1, "B93_Fade_Long"); return true;
            }
            return false;
        }

        private bool TryFadeShort(DateTime now, double px)
        {
            double d = (sessionHigh - px) / TickSize;
            if (d <= FadeExtremeTicks)
            {
                SetStopLoss("B93_Fade_Short", CalculationMode.Ticks, FadeStopTicks, false);
                SetProfitTarget("B93_Fade_Short", CalculationMode.Ticks, FadeTargetTicks);
                pendingEntry = true; lastEntryBar = CurrentBar; lastEntryTime = now; fs++;
                if (PrintEntries) Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER SHORT Fade px={1:F2} distHighTicks={2:F1}", now, px, d));
                EnterShort(1, "B93_Fade_Short"); return true;
            }
            return false;
        }

        private void SubmitLong(DateTime now, double px, string reason)
        {
            SetStopLoss("B93_Trend_Long", CalculationMode.Ticks, TrendStopTicks, false);
            SetProfitTarget("B93_Trend_Long", CalculationMode.Ticks, TrendTargetTicks);
            pendingEntry = true; lastEntryBar = CurrentBar; lastEntryTime = now; pullbackArmed = false; tl++;
            if (PrintEntries) Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER LONG {1} px={2:F2} energy={3:F2} exhaust={4:F2} state={5}", now, reason, px, energy, exhaust, state));
            EnterLong(1, "B93_Trend_Long");
        }

        private void SubmitShort(DateTime now, double px, string reason)
        {
            SetStopLoss("B93_Trend_Short", CalculationMode.Ticks, TrendStopTicks, false);
            SetProfitTarget("B93_Trend_Short", CalculationMode.Ticks, TrendTargetTicks);
            pendingEntry = true; lastEntryBar = CurrentBar; lastEntryTime = now; pullbackArmed = false; ts++;
            if (PrintEntries) Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER SHORT {1} px={2:F2} energy={3:F2} exhaust={4:F2} state={5}", now, reason, px, energy, exhaust, state));
            EnterShort(1, "B93_Trend_Short");
        }

        private void Diag(DateTime now, double px)
        {
            if (DiagnosticEveryTicks <= 0 || tickCounter % DiagnosticEveryTicks != 0) return;
            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} DIAG ticks={1} px={2:F2} state={3} energy={4:F2} exhaust={5:F2} pos={6} TL={7} TS={8} FL={9} FS={10} sessBlk={11} coolBlk={12} posBlk={13} modes={14}", now, tickCounter, px, state, energy, exhaust, Position.MarketPosition, tl, ts, fl, fs, sessBlk, coolBlk, posBlk, modeChanges));
        }
    }
}
