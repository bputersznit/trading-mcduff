// =================================================================================================
// CG_T2_ClanMarshal_v9_3_PersistenceEngine.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-06 22:52:00 EDT
//
// B93 PERCENTILE++ SAME-NAME BUILD
//
// Compile fix included:
//   using System.ComponentModel.DataAnnotations;
//
// Design:
//   - Same class/file name requested by user.
//   - Intended for MNQ 200 Tick chart or similar tick chart.
//   - No AddDataSeries.
//   - No fades.
//   - One MNQ only, no overlap.
//   - One trade per auction leg.
//   - Percentile volatility gate.
//   - Directional expansion score.
//   - Anti-late-trend filter.
//   - Fill-price stop/target submitted after actual entry fill.
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
        private enum AuctionState { None, BuildingUp, ConfirmedUp, BuildingDown, ConfirmedDown }
        private enum EntryKind { None, Long, Short }

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
        [Range(10, 500)]
        [Display(Name = "Percentile Lookback Bars", GroupName = "02 Percentile++", Order = 1)]
        public int PercentileLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Min Range Ticks", GroupName = "02 Percentile++", Order = 2)]
        public int MinRangeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.10, 5.00)]
        [Display(Name = "Min Expansion Score", GroupName = "02 Percentile++", Order = 3)]
        public double MinExpansionScore { get; set; }

        [NinjaScriptProperty]
        [Range(0.10, 1.00)]
        [Display(Name = "Expansion Decay Ratio", GroupName = "02 Percentile++", Order = 4)]
        public double ExpansionDecayRatio { get; set; }

        [NinjaScriptProperty]
        [Range(10, 300)]
        [Display(Name = "Max Cumulative Trend Ticks", GroupName = "02 Percentile++", Order = 5)]
        public int MaxCumulativeTrendTicks { get; set; }

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
        [Display(Name = "Energy Decay", GroupName = "03 State", Order = 4)]
        public double EnergyDecay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Target Ticks", GroupName = "04 Brackets", Order = 1)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Stop Ticks", GroupName = "04 Brackets", Order = 2)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 300)]
        [Display(Name = "Post Exit Cooldown Seconds", GroupName = "05 Governance", Order = 1)]
        public int PostExitCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Max Same Direction Stopouts", GroupName = "05 Governance", Order = 2)]
        public int MaxSameDirectionStopouts { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "Loss Embargo Seconds", GroupName = "05 Governance", Order = 3)]
        public int LossEmbargoSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(10, 10000)]
        [Display(Name = "Diagnostic Every Bars", GroupName = "06 Diagnostics", Order = 1)]
        public int DiagnosticEveryBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Print Diagnostics", GroupName = "06 Diagnostics", Order = 2)]
        public bool PrintDiagnostics { get; set; }

        private AuctionState auctionState;
        private EntryKind pendingEntryKind;
        private EntryKind activeEntryKind;

        private Queue<double> rangeQ;
        private Queue<double> expansionQ;

        private double rangeP50;
        private double rangeP80;
        private double currentRangeTicks;
        private double priorExpansionScore;
        private double expansionScore;
        private double energy;

        private double sessionHigh;
        private double sessionLow;
        private DateTime sessionDate;
        private bool sessionInitialized;

        private bool longLegConsumed;
        private bool shortLegConsumed;
        private bool pendingEntry;
        private DateTime lastExitTime;
        private DateTime longEmbargoUntil;
        private DateTime shortEmbargoUntil;
        private int consecutiveLongStopouts;
        private int consecutiveShortStopouts;

        private long longTrades;
        private long shortTrades;
        private long pctBlocked;
        private long lateBlocked;
        private long legBlocked;
        private long cooldownBlocked;
        private long stateChanges;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_3_PersistenceEngine";
                Description = "B93 Percentile++ same-name MNQ persistence strategy. Full compile-safe file.";

                Calculate = Calculate.OnBarClose;
                IsOverlay = true;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                TimeInForce = TimeInForce.Day;
                StartBehavior = StartBehavior.WaitUntilFlat;
                BarsRequiredToTrade = 30;
                TraceOrders = false;

                UseSessionFilter = true;
                SessionStartTime = 83000;
                SessionEndTime = 150000;

                PercentileLookback = 120;
                MinRangeTicks = 12;
                MinExpansionScore = 0.55;
                ExpansionDecayRatio = 0.70;
                MaxCumulativeTrendTicks = 90;

                BuildEnergy = 10.0;
                ConfirmEnergy = 20.0;
                ExitEnergy = 5.0;
                EnergyDecay = 0.72;

                TargetTicks = 32;
                StopTicks = 18;

                PostExitCooldownSeconds = 20;
                MaxSameDirectionStopouts = 2;
                LossEmbargoSeconds = 300;

                DiagnosticEveryBars = 100;
                PrintDiagnostics = true;
            }
            else if (State == State.Configure)
            {
                // No AddDataSeries. Use the chart's own bar series.
                // Brackets are submitted after actual entry fill.
            }
            else if (State == State.DataLoaded)
            {
                ResetRuntime();
                Print(Name + " loaded. B93 PERCENTILE++ compile-safe same-name build.");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            DateTime now = Time[0];
            ResetSession(now);

            if (!InSession(now))
                return;

            UpdatePercentileMetrics();
            UpdateEnergyAndState();
            ResetLegConsumptionIfNeutral();

            if (PrintDiagnostics && CurrentBar % DiagnosticEveryBars == 0)
                PrintDiag(now);

            if (!CanEnter(now))
                return;

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (auctionState == AuctionState.ConfirmedUp)
            {
                if (longLegConsumed)
                {
                    legBlocked++;
                    return;
                }

                if (IsLongEmbargoed(now))
                    return;

                if (!PassesPercentileGate(true))
                {
                    pctBlocked++;
                    return;
                }

                if (IsLateLong())
                {
                    lateBlocked++;
                    return;
                }

                SubmitLong(now);
            }
            else if (auctionState == AuctionState.ConfirmedDown)
            {
                if (shortLegConsumed)
                {
                    legBlocked++;
                    return;
                }

                if (IsShortEmbargoed(now))
                    return;

                if (!PassesPercentileGate(false))
                {
                    pctBlocked++;
                    return;
                }

                if (IsLateShort())
                {
                    lateBlocked++;
                    return;
                }

                SubmitShort(now);
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            if (execution.Order.OrderState != OrderState.Filled)
                return;

            string n = execution.Order.Name ?? string.Empty;

            if (n == "B93_Trend_Long" || n == "B93_Trend_Short")
            {
                pendingEntry = false;
                activeEntryKind = pendingEntryKind;
                pendingEntryKind = EntryKind.None;

                if (activeEntryKind == EntryKind.Long)
                    longLegConsumed = true;
                else if (activeEntryKind == EntryKind.Short)
                    shortLegConsumed = true;

                SubmitBracketsFromFill(n, price, quantity, time);
                return;
            }

            if (n == "B93_Stop_Long" || n == "B93_Stop_Short")
                RegisterStopout(n, time);
            else if (n == "B93_Target_Long" || n == "B93_Target_Short")
                RegisterTarget(n);

            if (n == "B93_Stop_Long" || n == "B93_Stop_Short" || n == "B93_Target_Long" || n == "B93_Target_Short")
            {
                activeEntryKind = EntryKind.None;
                pendingEntry = false;
                lastExitTime = time;
            }
        }

        private void ResetRuntime()
        {
            auctionState = AuctionState.None;
            pendingEntryKind = EntryKind.None;
            activeEntryKind = EntryKind.None;

            rangeQ = new Queue<double>();
            expansionQ = new Queue<double>();

            rangeP50 = 0.0;
            rangeP80 = 0.0;
            currentRangeTicks = 0.0;
            priorExpansionScore = 0.0;
            expansionScore = 0.0;
            energy = 0.0;

            sessionHigh = double.MinValue;
            sessionLow = double.MaxValue;
            sessionDate = Core.Globals.MinDate;
            sessionInitialized = false;

            longLegConsumed = false;
            shortLegConsumed = false;
            pendingEntry = false;
            lastExitTime = Core.Globals.MinDate;
            longEmbargoUntil = Core.Globals.MinDate;
            shortEmbargoUntil = Core.Globals.MinDate;
            consecutiveLongStopouts = 0;
            consecutiveShortStopouts = 0;

            longTrades = 0;
            shortTrades = 0;
            pctBlocked = 0;
            lateBlocked = 0;
            legBlocked = 0;
            cooldownBlocked = 0;
            stateChanges = 0;
        }

        private void ResetSession(DateTime now)
        {
            if (!sessionInitialized || now.Date != sessionDate)
            {
                sessionInitialized = true;
                sessionDate = now.Date;
                sessionHigh = High[0];
                sessionLow = Low[0];
                auctionState = AuctionState.None;
                energy = 0.0;
                longLegConsumed = false;
                shortLegConsumed = false;
                consecutiveLongStopouts = 0;
                consecutiveShortStopouts = 0;
                longEmbargoUntil = Core.Globals.MinDate;
                shortEmbargoUntil = Core.Globals.MinDate;
            }

            if (High[0] > sessionHigh)
                sessionHigh = High[0];

            if (Low[0] < sessionLow)
                sessionLow = Low[0];
        }

        private bool InSession(DateTime now)
        {
            if (!UseSessionFilter)
                return true;

            int t = ToTime(now);
            return t >= SessionStartTime && t <= SessionEndTime;
        }

        private void UpdatePercentileMetrics()
        {
            double rangePoints = Math.Max(TickSize, High[0] - Low[0]);
            currentRangeTicks = rangePoints / TickSize;

            rangeQ.Enqueue(currentRangeTicks);
            while (rangeQ.Count > PercentileLookback)
                rangeQ.Dequeue();

            ComputeRangePercentiles();

            double bodyTicks = Math.Abs(Close[0] - Open[0]) / TickSize;
            double efficiency = bodyTicks / Math.Max(1.0, currentRangeTicks);
            double closeLoc = (Close[0] - Low[0]) / rangePoints;
            if (closeLoc < 0.0) closeLoc = 0.0;
            if (closeLoc > 1.0) closeLoc = 1.0;

            double directionQuality = Close[0] >= Open[0] ? closeLoc : 1.0 - closeLoc;
            double expansionRatio = currentRangeTicks / Math.Max(1.0, rangeP50);

            priorExpansionScore = expansionScore;
            expansionScore = expansionRatio * efficiency * directionQuality;

            expansionQ.Enqueue(expansionScore);
            while (expansionQ.Count > PercentileLookback)
                expansionQ.Dequeue();
        }

        private void ComputeRangePercentiles()
        {
            if (rangeQ.Count < 10)
            {
                rangeP50 = MinRangeTicks;
                rangeP80 = MinRangeTicks;
                return;
            }

            List<double> ranges = new List<double>(rangeQ);
            ranges.Sort();

            int i50 = (int)Math.Floor((ranges.Count - 1) * 0.50);
            int i80 = (int)Math.Floor((ranges.Count - 1) * 0.80);

            rangeP50 = Math.Max(1.0, ranges[i50]);
            rangeP80 = Math.Max(1.0, ranges[i80]);
        }

        private void UpdateEnergyAndState()
        {
            double barMove = Close[0] - Open[0];
            double slope = Close[0] - Close[1];
            double evidence = 0.0;

            if (barMove > 0.0)
                evidence += 6.0;
            else if (barMove < 0.0)
                evidence -= 6.0;

            if (slope > 0.0)
                evidence += 5.0;
            else if (slope < 0.0)
                evidence -= 5.0;

            if (currentRangeTicks >= rangeP80)
                evidence += Math.Sign(barMove == 0.0 ? slope : barMove) * 2.0;

            energy = (energy * EnergyDecay) + evidence;
            if (energy > 100.0) energy = 100.0;
            if (energy < -100.0) energy = -100.0;

            AuctionState old = auctionState;

            switch (auctionState)
            {
                case AuctionState.None:
                    if (energy >= BuildEnergy)
                        auctionState = AuctionState.BuildingUp;
                    else if (energy <= -BuildEnergy)
                        auctionState = AuctionState.BuildingDown;
                    break;

                case AuctionState.BuildingUp:
                    if (energy >= ConfirmEnergy)
                        auctionState = AuctionState.ConfirmedUp;
                    else if (energy <= ExitEnergy)
                        auctionState = AuctionState.None;
                    break;

                case AuctionState.ConfirmedUp:
                    if (energy <= -ExitEnergy)
                        auctionState = AuctionState.None;
                    break;

                case AuctionState.BuildingDown:
                    if (energy <= -ConfirmEnergy)
                        auctionState = AuctionState.ConfirmedDown;
                    else if (energy >= -ExitEnergy)
                        auctionState = AuctionState.None;
                    break;

                case AuctionState.ConfirmedDown:
                    if (energy >= ExitEnergy)
                        auctionState = AuctionState.None;
                    break;
            }

            if (old != auctionState)
                stateChanges++;
        }

        private void ResetLegConsumptionIfNeutral()
        {
            if (auctionState == AuctionState.None || Math.Abs(energy) <= ExitEnergy)
            {
                longLegConsumed = false;
                shortLegConsumed = false;
            }
        }

        private bool PassesPercentileGate(bool isLong)
        {
            if (currentRangeTicks < MinRangeTicks)
                return false;

            if (currentRangeTicks < rangeP80 * 0.80)
                return false;

            if (expansionScore < MinExpansionScore)
                return false;

            if (priorExpansionScore > 0.0 && expansionScore < priorExpansionScore * ExpansionDecayRatio)
                return false;

            if (isLong && Close[0] <= Open[0])
                return false;

            if (!isLong && Close[0] >= Open[0])
                return false;

            return true;
        }

        private bool IsLateLong()
        {
            double cumulativeUpTicks = (Close[0] - sessionLow) / TickSize;
            double upperWickRatio = (High[0] - Close[0]) / Math.Max(TickSize, High[0] - Low[0]);

            return cumulativeUpTicks > MaxCumulativeTrendTicks || upperWickRatio > 0.45;
        }

        private bool IsLateShort()
        {
            double cumulativeDownTicks = (sessionHigh - Close[0]) / TickSize;
            double lowerWickRatio = (Close[0] - Low[0]) / Math.Max(TickSize, High[0] - Low[0]);

            return cumulativeDownTicks > MaxCumulativeTrendTicks || lowerWickRatio > 0.45;
        }

        private bool CanEnter(DateTime now)
        {
            if (pendingEntry)
                return false;

            if (lastExitTime != Core.Globals.MinDate && (now - lastExitTime).TotalSeconds < PostExitCooldownSeconds)
            {
                cooldownBlocked++;
                return false;
            }

            return true;
        }

        private bool IsLongEmbargoed(DateTime now)
        {
            return longEmbargoUntil != Core.Globals.MinDate && now < longEmbargoUntil;
        }

        private bool IsShortEmbargoed(DateTime now)
        {
            return shortEmbargoUntil != Core.Globals.MinDate && now < shortEmbargoUntil;
        }

        private void SubmitLong(DateTime now)
        {
            pendingEntry = true;
            pendingEntryKind = EntryKind.Long;
            longTrades++;

            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER LONG P++ px={1:F2} energy={2:F2} exp={3:F2} range={4:F1} p80={5:F1}",
                now, Close[0], energy, expansionScore, currentRangeTicks, rangeP80));

            EnterLong(1, "B93_Trend_Long");
        }

        private void SubmitShort(DateTime now)
        {
            pendingEntry = true;
            pendingEntryKind = EntryKind.Short;
            shortTrades++;

            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTER SHORT P++ px={1:F2} energy={2:F2} exp={3:F2} range={4:F1} p80={5:F1}",
                now, Close[0], energy, expansionScore, currentRangeTicks, rangeP80));

            EnterShort(1, "B93_Trend_Short");
        }

        private void SubmitBracketsFromFill(string entrySignal, double fillPrice, int qty, DateTime time)
        {
            double stopPrice;
            double targetPrice;

            if (activeEntryKind == EntryKind.Long)
            {
                stopPrice = Instrument.MasterInstrument.RoundToTickSize(fillPrice - StopTicks * TickSize);
                targetPrice = Instrument.MasterInstrument.RoundToTickSize(fillPrice + TargetTicks * TickSize);

                ExitLongStopMarket(0, true, qty, stopPrice, "B93_Stop_Long", entrySignal);
                ExitLongLimit(0, true, qty, targetPrice, "B93_Target_Long", entrySignal);
            }
            else if (activeEntryKind == EntryKind.Short)
            {
                stopPrice = Instrument.MasterInstrument.RoundToTickSize(fillPrice + StopTicks * TickSize);
                targetPrice = Instrument.MasterInstrument.RoundToTickSize(fillPrice - TargetTicks * TickSize);

                ExitShortStopMarket(0, true, qty, stopPrice, "B93_Stop_Short", entrySignal);
                ExitShortLimit(0, true, qty, targetPrice, "B93_Target_Short", entrySignal);
            }
        }

        private void RegisterStopout(string stopName, DateTime time)
        {
            if (stopName == "B93_Stop_Long")
            {
                consecutiveLongStopouts++;
                consecutiveShortStopouts = 0;

                if (MaxSameDirectionStopouts > 0 && consecutiveLongStopouts >= MaxSameDirectionStopouts)
                    longEmbargoUntil = time.AddSeconds(LossEmbargoSeconds);
            }
            else if (stopName == "B93_Stop_Short")
            {
                consecutiveShortStopouts++;
                consecutiveLongStopouts = 0;

                if (MaxSameDirectionStopouts > 0 && consecutiveShortStopouts >= MaxSameDirectionStopouts)
                    shortEmbargoUntil = time.AddSeconds(LossEmbargoSeconds);
            }
        }

        private void RegisterTarget(string targetName)
        {
            if (targetName == "B93_Target_Long")
                consecutiveLongStopouts = 0;
            else if (targetName == "B93_Target_Short")
                consecutiveShortStopouts = 0;
        }

        private void PrintDiag(DateTime now)
        {
            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} DIAG state={1} energy={2:F2} exp={3:F2} range={4:F1} p80={5:F1} pos={6} L={7} S={8} pctBlk={9} lateBlk={10} legBlk={11} coolBlk={12} states={13}",
                now, auctionState, energy, expansionScore, currentRangeTicks, rangeP80, Position.MarketPosition,
                longTrades, shortTrades, pctBlocked, lateBlocked, legBlocked, cooldownBlocked, stateChanges));
        }
    }
}
