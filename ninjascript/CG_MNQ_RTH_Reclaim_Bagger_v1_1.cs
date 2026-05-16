// CG_MNQ_RTH_Reclaim_Bagger_v1_1.cs
// NinjaTrader 8 Strategy
//
// Fixes from v1:
// - Does NOT require premarket bars to exist.
// - Explicitly builds OR from 09:30-09:45 ET clock time.
// - Uses RTH fallback context when Playback/chart only has RTH data.
// - Adds phase diagnostics: premarket bars, OR bars, flush, failed bear, reclaim, retest.
// - Captures April-24-style move:
//      opening flush -> failed bear -> reclaim of OR high/resistance -> retest/hold -> long continuation.
//
// Intended chart:
// - MNQ JUN26
// - 1-minute or 2-minute chart
// - Playback101
//
// Notes:
// - This is a targeted prototype, not the full flagship.
// - One contract only.
// - Managed OCO stop/target.
// - Default allows trading even with no premarket bars.

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_MNQ_RTH_Reclaim_Bagger_v1_1 : Strategy
    {
        private const string LongSignal = "Reclaim_Long";

        private enum Phase
        {
            WAITING,
            BUILDING_PREMARKET,
            BUILDING_OR,
            WAITING_FLUSH,
            WAITING_FAILED_BEAR,
            WAITING_RECLAIM,
            WAITING_RETEST,
            LONG_ARMED,
            TRADED,
            LOCKED_OUT
        }

        private Phase phase = Phase.WAITING;

        // Premarket
        private int preBars = 0;
        private double preHigh = double.MinValue;
        private double preLow = double.MaxValue;
        private double preVwapNum = 0.0;
        private double preVol = 0.0;
        private double preVwap = 0.0;
        private int preHighTests = 0;
        private bool premarketAvailable = false;
        private bool premarketBearishExhaustion = false;

        // OR
        private bool orComplete = false;
        private int orBars = 0;
        private double orHigh = double.MinValue;
        private double orLow = double.MaxValue;
        private double orVwapNum = 0.0;
        private double orVol = 0.0;
        private double orVwap = 0.0;
        private double orWidth = 0.0;

        // RTH fallback/context
        private int rthBarsSeen = 0;
        private double rthEarlyHigh = double.MinValue;
        private double rthEarlyLow = double.MaxValue;

        // Structure state
        private bool openingFlushSeen = false;
        private bool failedBearSeen = false;
        private bool reclaimSeen = false;
        private bool retestSeen = false;

        private double reclaimLevel = 0.0;
        private double retestReferenceLow = 0.0;
        private int reclaimBar = -1;
        private int retestBar = -1;

        // Governance
        private int tradesToday = 0;
        private int entries = 0;
        private int exits = 0;
        private int lastExitBar = -9999;
        private int sessionDate = -1;

        // Rejection counters
        private int rejectTime = 0;
        private int rejectPremarket = 0;
        private int rejectOr = 0;
        private int rejectFlush = 0;
        private int rejectFailedBear = 0;
        private int rejectReclaim = 0;
        private int rejectRetest = 0;
        private int rejectVolume = 0;
        private int rejectGovernance = 0;

        // One-time diagnostics
        private bool printedNoPremarketFallback = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_MNQ_RTH_Reclaim_Bagger_v1_1";
                Description = "RTH Reclaim Bagger v1.1: explicit OR, premarket-optional fallback, failed bear -> OR high reclaim -> retest hold.";

                Calculate = Calculate.OnBarClose;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                TimeInForce = TimeInForce.Day;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 20;
                IsInstantiatedOnEachOptimizationIteration = false;

                // Session
                PremarketStartEt = 60000;
                RthStartEt = 93000;
                OrEndEt = 94500;
                RthEndEt = 155900;
                EarlyRthContextEndEt = 100000;

                // Premarket / fallback
                RequirePremarketIfAvailable = false;
                AllowNoPremarketFallback = true;
                PremarketHighTestTolerancePoints = 6.0;
                MinPremarketHighTests = 2;
                PremarketFailureFromHighPoints = 18.0;

                // Opening flush / failed bear
                RequireOpeningFlush = true;
                OpeningFlushBelowOrLowPoints = 2.0;
                FailedBearReclaimOrLowBufferPoints = 6.0;

                // Reclaim / retest
                ReclaimBufferPoints = 2.0;
                RetestTolerancePoints = 10.0;
                RetestHoldBufferPoints = 1.0;
                MaxBarsAfterReclaimForRetest = 20;
                RequireBullishReclaimCandle = false;
                RequireBullishRetestCandle = false;

                // Volume
                UseReclaimVolumeFilter = false;
                ReclaimVolumeLookbackBars = 5;
                MinReclaimVolumeMultiplier = 1.0;

                // Risk
                StopTicks = 32;        // 8 points
                TargetTicks = 160;     // 40 points
                UseDynamicStopFromRetestLow = true;
                DynamicStopExtraTicks = 8;
                MaxStopTicks = 80;

                // Governance
                MaxTradesPerSession = 1;
                CooldownBarsAfterExit = 3;

                // Diagnostics
                PrintDiagnostics = true;
            }
            else if (State == State.Configure)
            {
                SetStopLoss(LongSignal, CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget(LongSignal, CalculationMode.Ticks, TargetTicks);
            }
            else if (State == State.Terminated)
            {
                PrintSummary();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            DateTime now = Time[0];
            int today = now.Year * 10000 + now.Month * 100 + now.Day;
            int t = ToTime(now);

            if (today != sessionDate)
                ResetSession(today, now);

            if (t < PremarketStartEt || t > RthEndEt)
            {
                rejectTime++;
                return;
            }

            // Build premarket only if chart/playback has those bars.
            if (t >= PremarketStartEt && t < RthStartEt)
            {
                BuildPremarket();
                return;
            }

            // At/after RTH start, finalize premarket if any exists.
            if (t >= RthStartEt && phase == Phase.BUILDING_PREMARKET)
                FinalizePremarket(now);

            // RTH fallback if no premarket bars were loaded.
            if (t >= RthStartEt && preBars == 0 && AllowNoPremarketFallback && !printedNoPremarketFallback)
            {
                printedNoPremarketFallback = true;
                if (PrintDiagnostics)
                    Print("[BAGGER v1.1][FALLBACK] No premarket bars detected. Using RTH-only structure.");
                phase = Phase.BUILDING_OR;
            }

            // Build OR explicitly from clock time.
            if (t >= RthStartEt && t < OrEndEt)
            {
                BuildOpeningRange(now);
                return;
            }

            // Complete OR once after 09:45.
            if (!orComplete && t >= OrEndEt)
                CompleteOpeningRange(now);

            if (!orComplete)
            {
                rejectOr++;
                return;
            }

            // Track early RTH context for fallback diagnostics.
            if (t >= RthStartEt && t <= EarlyRthContextEndEt)
            {
                rthBarsSeen++;
                rthEarlyHigh = Math.Max(rthEarlyHigh, High[0]);
                rthEarlyLow = Math.Min(rthEarlyLow, Low[0]);
            }

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (tradesToday >= MaxTradesPerSession || CurrentBar - lastExitBar < CooldownBarsAfterExit)
            {
                rejectGovernance++;
                return;
            }

            UpdateStructure(now, t);

            if (phase == Phase.LONG_ARMED)
                EnterReclaimLong(now);
        }

        // ============================================================
        // Premarket Layer
        // ============================================================

        private void BuildPremarket()
        {
            phase = Phase.BUILDING_PREMARKET;
            preBars++;
            premarketAvailable = true;

            if (High[0] > preHigh)
            {
                if (preHigh != double.MinValue && Math.Abs(High[0] - preHigh) <= PremarketHighTestTolerancePoints)
                    preHighTests++;
                preHigh = High[0];
            }
            else if (preHigh != double.MinValue && Math.Abs(High[0] - preHigh) <= PremarketHighTestTolerancePoints)
            {
                preHighTests++;
            }

            preLow = Math.Min(preLow, Low[0]);
            preVwapNum += Close[0] * Volume[0];
            preVol += Volume[0];
        }

        private void FinalizePremarket(DateTime now)
        {
            preVwap = preVol > 0 ? preVwapNum / preVol : Close[0];

            premarketBearishExhaustion =
                preHighTests >= MinPremarketHighTests &&
                Close[0] <= preHigh - PremarketFailureFromHighPoints;

            if (PrintDiagnostics)
            {
                Print(string.Format(
                    "[BAGGER v1.1][PRE] bars={0} H={1:F2} L={2:F2} VWAP={3:F2} highTests={4} bearishExhaustion={5}",
                    preBars, preHigh, preLow, preVwap, preHighTests, premarketBearishExhaustion));
            }

            phase = Phase.BUILDING_OR;
        }

        // ============================================================
        // Opening Range
        // ============================================================

        private void BuildOpeningRange(DateTime now)
        {
            phase = Phase.BUILDING_OR;
            orBars++;

            orHigh = Math.Max(orHigh, High[0]);
            orLow = Math.Min(orLow, Low[0]);
            orVwapNum += Close[0] * Volume[0];
            orVol += Volume[0];

            if (PrintDiagnostics && orBars == 1)
                Print("[BAGGER v1.1][OR] Start " + now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        private void CompleteOpeningRange(DateTime now)
        {
            if (orBars <= 0 || orHigh == double.MinValue || orLow == double.MaxValue)
            {
                rejectOr++;
                if (PrintDiagnostics)
                    Print("[BAGGER v1.1][OR ERROR] No OR bars available.");
                return;
            }

            orComplete = true;
            orWidth = orHigh - orLow;
            orVwap = orVol > 0 ? orVwapNum / orVol : (orHigh + orLow) / 2.0;

            reclaimLevel = orHigh + ReclaimBufferPoints;
            phase = Phase.WAITING_FLUSH;

            if (PrintDiagnostics)
            {
                Print(string.Format(
                    "[BAGGER v1.1][OR] Complete bars={0} H={1:F2} L={2:F2} W={3:F2} VWAP={4:F2} reclaimLevel={5:F2}",
                    orBars, orHigh, orLow, orWidth, orVwap, reclaimLevel));
            }
        }

        // ============================================================
        // Structural State Machine
        // ============================================================

        private void UpdateStructure(DateTime now, int t)
        {
            if (premarketAvailable && RequirePremarketIfAvailable && !premarketBearishExhaustion)
            {
                rejectPremarket++;
                return;
            }

            // Phase 1: Opening flush below OR low.
            if (!openingFlushSeen)
            {
                if (!RequireOpeningFlush || Low[0] <= orLow - OpeningFlushBelowOrLowPoints)
                {
                    openingFlushSeen = true;
                    phase = Phase.WAITING_FAILED_BEAR;
                    if (PrintDiagnostics)
                        Print(string.Format("[BAGGER v1.1][FLUSH] {0:HH:mm:ss} Low={1:F2} ORLow={2:F2}", now, Low[0], orLow));
                }
                else
                {
                    rejectFlush++;
                    return;
                }
            }

            // Phase 2: Failed bear = reclaim above OR low + buffer.
            if (!failedBearSeen)
            {
                if (Close[0] >= orLow + FailedBearReclaimOrLowBufferPoints)
                {
                    failedBearSeen = true;
                    phase = Phase.WAITING_RECLAIM;
                    if (PrintDiagnostics)
                        Print(string.Format("[BAGGER v1.1][FAILED_BEAR] {0:HH:mm:ss} Close={1:F2}", now, Close[0]));
                }
                else
                {
                    rejectFailedBear++;
                    return;
                }
            }

            // Phase 3: Reclaim OR high + buffer.
            if (!reclaimSeen)
            {
                if (ValidReclaim())
                {
                    reclaimSeen = true;
                    reclaimBar = CurrentBar;
                    phase = Phase.WAITING_RETEST;
                    if (PrintDiagnostics)
                        Print(string.Format("[BAGGER v1.1][RECLAIM] {0:HH:mm:ss} Close={1:F2} Level={2:F2}", now, Close[0], reclaimLevel));
                }
                else
                {
                    rejectReclaim++;
                    return;
                }
            }

            // Expire if retest not seen.
            if (reclaimSeen && !retestSeen && CurrentBar - reclaimBar > MaxBarsAfterReclaimForRetest)
            {
                phase = Phase.LOCKED_OUT;
                if (PrintDiagnostics)
                    Print("[BAGGER v1.1][EXPIRE] Reclaim expired without retest.");
                return;
            }

            // Phase 4: Retest/hold of reclaimed OR high zone.
            if (!retestSeen)
            {
                if (ValidRetestHold())
                {
                    retestSeen = true;
                    retestReferenceLow = Low[0];
                    retestBar = CurrentBar;
                    phase = Phase.LONG_ARMED;
                    if (PrintDiagnostics)
                        Print(string.Format("[BAGGER v1.1][RETEST] {0:HH:mm:ss} Low={1:F2} Close={2:F2}", now, Low[0], Close[0]));
                }
                else
                {
                    rejectRetest++;
                    return;
                }
            }
        }

        private bool ValidReclaim()
        {
            bool priceOk = Close[0] >= reclaimLevel;
            bool candleOk = !RequireBullishReclaimCandle || Close[0] > Open[0];
            bool vwapOk = Close[0] > orVwap;
            bool volumeOk = true;

            if (UseReclaimVolumeFilter && CurrentBar > ReclaimVolumeLookbackBars)
            {
                double avg = SMA(Volume, ReclaimVolumeLookbackBars)[0];
                volumeOk = avg <= 0 || Volume[0] >= avg * MinReclaimVolumeMultiplier;
                if (!volumeOk)
                    rejectVolume++;
            }

            return priceOk && candleOk && vwapOk && volumeOk;
        }

        private bool ValidRetestHold()
        {
            bool tagged = Low[0] <= reclaimLevel + RetestTolerancePoints;
            bool held = Close[0] >= orHigh + RetestHoldBufferPoints;
            bool vwapOk = Close[0] > orVwap;
            bool candleOk = !RequireBullishRetestCandle || Close[0] >= Open[0];

            return tagged && held && vwapOk && candleOk;
        }

        // ============================================================
        // Entry / execution
        // ============================================================

        private void EnterReclaimLong(DateTime now)
        {
            int stopTicks = StopTicks;

            if (UseDynamicStopFromRetestLow && retestReferenceLow > 0)
            {
                double riskPoints = Math.Max(StopTicks * TickSize, Close[0] - retestReferenceLow + DynamicStopExtraTicks * TickSize);
                stopTicks = Math.Min(MaxStopTicks, Math.Max(StopTicks, (int)Math.Ceiling(riskPoints / TickSize)));
            }

            SetStopLoss(LongSignal, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(LongSignal, CalculationMode.Ticks, TargetTicks);

            if (PrintDiagnostics)
            {
                Print(string.Format(
                    "[BAGGER v1.1][ENTRY] LONG {0:HH:mm:ss} Close={1:F2} StopTicks={2} TargetTicks={3}",
                    now, Close[0], stopTicks, TargetTicks));
            }

            EnterLong(1, LongSignal);
            entries++;
            tradesToday++;
            phase = Phase.TRADED;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            if (execution.Order.OrderState != OrderState.Filled &&
                execution.Order.OrderState != OrderState.PartFilled)
                return;

            string name = execution.Order.Name ?? "";

            if (name == LongSignal)
            {
                if (PrintDiagnostics)
                    Print(string.Format("[BAGGER v1.1][FILL] LONG price={0:F2} qty={1} time={2:HH:mm:ss}", price, quantity, time));
            }
            else
            {
                exits++;
                lastExitBar = CurrentBar;
                if (PrintDiagnostics)
                    Print(string.Format("[BAGGER v1.1][EXIT] {0} price={1:F2} time={2:HH:mm:ss}", name, price, time));
            }
        }

        // ============================================================
        // Reset / diagnostics
        // ============================================================

        private void ResetSession(int today, DateTime now)
        {
            sessionDate = today;
            phase = Phase.WAITING;

            preBars = 0;
            preHigh = double.MinValue;
            preLow = double.MaxValue;
            preVwapNum = 0;
            preVol = 0;
            preVwap = 0;
            preHighTests = 0;
            premarketAvailable = false;
            premarketBearishExhaustion = false;

            orComplete = false;
            orBars = 0;
            orHigh = double.MinValue;
            orLow = double.MaxValue;
            orVwapNum = 0;
            orVol = 0;
            orVwap = 0;
            orWidth = 0;

            rthBarsSeen = 0;
            rthEarlyHigh = double.MinValue;
            rthEarlyLow = double.MaxValue;

            openingFlushSeen = false;
            failedBearSeen = false;
            reclaimSeen = false;
            retestSeen = false;
            reclaimLevel = 0;
            retestReferenceLow = 0;
            reclaimBar = -1;
            retestBar = -1;

            tradesToday = 0;
            printedNoPremarketFallback = false;

            if (PrintDiagnostics)
                Print("[BAGGER v1.1][SESSION] Reset " + now.ToString("yyyy-MM-dd"));
        }

        private void PrintSummary()
        {
            if (!PrintDiagnostics)
                return;

            Print("╔════════════════════════════════════════════════════════════╗");
            Print("║ CG MNQ RTH RECLAIM BAGGER v1.1 SUMMARY                    ║");
            Print("╠════════════════════════════════════════════════════════════╣");
            Print(" Phase: " + phase);
            Print(string.Format(" PRE: bars={0} H={1:F2} L={2:F2} VWAP={3:F2} Tests={4} Exhaustion={5}",
                preBars, preHigh, preLow, preVwap, preHighTests, premarketBearishExhaustion));
            Print(string.Format(" OR: bars={0} H={1:F2} L={2:F2} W={3:F2} VWAP={4:F2} Complete={5}",
                orBars, orHigh, orLow, orWidth, orVwap, orComplete));
            Print(string.Format(" ReclaimLevel={0:F2}", reclaimLevel));
            Print(" openingFlushSeen: " + openingFlushSeen);
            Print(" failedBearSeen: " + failedBearSeen);
            Print(" reclaimSeen: " + reclaimSeen);
            Print(" retestSeen: " + retestSeen);
            Print(" entries: " + entries);
            Print(" exits: " + exits);
            Print("╟────────────────────────────────────────────────────────────╢");
            Print(" rejectTime: " + rejectTime);
            Print(" rejectPremarket: " + rejectPremarket);
            Print(" rejectOr: " + rejectOr);
            Print(" rejectFlush: " + rejectFlush);
            Print(" rejectFailedBear: " + rejectFailedBear);
            Print(" rejectReclaim: " + rejectReclaim);
            Print(" rejectRetest: " + rejectRetest);
            Print(" rejectVolume: " + rejectVolume);
            Print(" rejectGovernance: " + rejectGovernance);
            Print("╚════════════════════════════════════════════════════════════╝");
        }

        // ============================================================
        // Parameters
        // ============================================================

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "PremarketStartEt", Order = 1, GroupName = "01. Session")]
        public int PremarketStartEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RthStartEt", Order = 2, GroupName = "01. Session")]
        public int RthStartEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "OrEndEt", Order = 3, GroupName = "01. Session")]
        public int OrEndEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RthEndEt", Order = 4, GroupName = "01. Session")]
        public int RthEndEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EarlyRthContextEndEt", Order = 5, GroupName = "01. Session")]
        public int EarlyRthContextEndEt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequirePremarketIfAvailable", Order = 1, GroupName = "02. Premarket")]
        public bool RequirePremarketIfAvailable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowNoPremarketFallback", Order = 2, GroupName = "02. Premarket")]
        public bool AllowNoPremarketFallback { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 30.0)]
        [Display(Name = "PremarketHighTestTolerancePoints", Order = 3, GroupName = "02. Premarket")]
        public double PremarketHighTestTolerancePoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MinPremarketHighTests", Order = 4, GroupName = "02. Premarket")]
        public int MinPremarketHighTests { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 100.0)]
        [Display(Name = "PremarketFailureFromHighPoints", Order = 5, GroupName = "02. Premarket")]
        public double PremarketFailureFromHighPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireOpeningFlush", Order = 1, GroupName = "03. Structure")]
        public bool RequireOpeningFlush { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 50.0)]
        [Display(Name = "OpeningFlushBelowOrLowPoints", Order = 2, GroupName = "03. Structure")]
        public double OpeningFlushBelowOrLowPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 50.0)]
        [Display(Name = "FailedBearReclaimOrLowBufferPoints", Order = 3, GroupName = "03. Structure")]
        public double FailedBearReclaimOrLowBufferPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 30.0)]
        [Display(Name = "ReclaimBufferPoints", Order = 4, GroupName = "03. Structure")]
        public double ReclaimBufferPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 50.0)]
        [Display(Name = "RetestTolerancePoints", Order = 5, GroupName = "03. Structure")]
        public double RetestTolerancePoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 30.0)]
        [Display(Name = "RetestHoldBufferPoints", Order = 6, GroupName = "03. Structure")]
        public double RetestHoldBufferPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxBarsAfterReclaimForRetest", Order = 7, GroupName = "03. Structure")]
        public int MaxBarsAfterReclaimForRetest { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireBullishReclaimCandle", Order = 8, GroupName = "03. Structure")]
        public bool RequireBullishReclaimCandle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireBullishRetestCandle", Order = 9, GroupName = "03. Structure")]
        public bool RequireBullishRetestCandle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseReclaimVolumeFilter", Order = 1, GroupName = "04. Volume")]
        public bool UseReclaimVolumeFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "ReclaimVolumeLookbackBars", Order = 2, GroupName = "04. Volume")]
        public int ReclaimVolumeLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "MinReclaimVolumeMultiplier", Order = 3, GroupName = "04. Volume")]
        public double MinReclaimVolumeMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "StopTicks", Order = 1, GroupName = "05. Risk")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "TargetTicks", Order = 2, GroupName = "05. Risk")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseDynamicStopFromRetestLow", Order = 3, GroupName = "05. Risk")]
        public bool UseDynamicStopFromRetestLow { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "DynamicStopExtraTicks", Order = 4, GroupName = "05. Risk")]
        public int DynamicStopExtraTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MaxStopTicks", Order = 5, GroupName = "05. Risk")]
        public int MaxStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxTradesPerSession", Order = 1, GroupName = "06. Governance")]
        public int MaxTradesPerSession { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "CooldownBarsAfterExit", Order = 2, GroupName = "06. Governance")]
        public int CooldownBarsAfterExit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", Order = 1, GroupName = "07. Diagnostics")]
        public bool PrintDiagnostics { get; set; }
    }
}
