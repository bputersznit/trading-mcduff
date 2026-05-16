// CG_MNQ_RTH_Reclaim_Bagger_v1.cs
// NinjaTrader 8 Strategy
// Purpose: Capture April-24-style RTH bull reversal / resistance-reclaim move.
// Pattern: premarket exhaustion -> opening flush -> failed bear continuation -> OR-high resistance reclaim -> retest/hold -> long continuation.

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_MNQ_RTH_Reclaim_Bagger_v1 : Strategy
    {
        private const string LongSignal = "Reclaim_Long";

        private enum ReclaimState
        {
            PREMARKET_BUILD,
            WAIT_RTH,
            OR_BUILD,
            WAIT_OPENING_FLUSH,
            WAIT_FAILED_BEAR,
            WAIT_RECLAIM,
            WAIT_RETEST,
            LONG_ARMED,
            TRADED,
            LOCKED_OUT
        }

        private ReclaimState state = ReclaimState.PREMARKET_BUILD;

        // Premarket / overnight structure
        private double preHigh = double.MinValue;
        private double preLow = double.MaxValue;
        private double preVwapNum = 0.0;
        private double preVol = 0.0;
        private double preVwap = 0.0;
        private int preHighTests = 0;
        private bool premarketBearishExhaustion = false;

        // Opening range
        private bool orComplete = false;
        private double orHigh = double.MinValue;
        private double orLow = double.MaxValue;
        private double orWidth = 0.0;
        private double orVwapNum = 0.0;
        private double orVol = 0.0;
        private double orVwap = 0.0;
        private int orBars = 0;

        // RTH structure
        private bool openingFlushSeen = false;
        private bool failedBearSeen = false;
        private bool reclaimSeen = false;
        private bool retestSeen = false;

        private double resistanceZoneLow = 0.0;
        private double resistanceZoneHigh = 0.0;
        private double reclaimLevel = 0.0;

        private int reclaimBar = -1;
        private int tradesToday = 0;
        private int lastExitBar = -9999;

        // Diagnostics
        private int rejectTime = 0;
        private int rejectPremarket = 0;
        private int rejectFlush = 0;
        private int rejectFailedBear = 0;
        private int rejectReclaim = 0;
        private int rejectRetest = 0;
        private int rejectVolume = 0;
        private int entries = 0;
        private int exits = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_MNQ_RTH_Reclaim_Bagger_v1";
                Description = "Captures premarket exhaustion -> opening flush -> resistance reclaim -> retest hold -> RTH bull continuation.";

                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                TimeInForce = TimeInForce.Day;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 30;
                IsInstantiatedOnEachOptimizationIteration = false;

                PremarketStartEt = 60000;
                RthStartEt = 93000;
                RthEndEt = 155900;
                OpeningRangeBars = 8;      // 2m chart ~= 16 minutes

                RequirePremarketExhaustion = true;
                PremarketHighTestTolerancePoints = 6.0;
                MinPremarketHighTests = 2;
                PremarketFailureFromHighPoints = 18.0;

                RequireOpeningFlush = true;
                OpeningFlushBelowOrLowPoints = 4.0;
                FailedBearReclaimOrLowBufferPoints = 4.0;

                ReclaimBufferPoints = 2.0;
                ReclaimLookbackBars = 3;
                MinReclaimVolumeMultiplier = 1.00;
                RequireBullishReclaimCandle = true;

                RetestTolerancePoints = 6.0;
                RetestHoldBufferPoints = 1.0;
                MaxBarsAfterReclaimForRetest = 12;
                RequireBullishRetestCandle = false;

                StopTicks = 32;            // 8 MNQ points
                TargetTicks = 160;         // 40 MNQ points
                UseDynamicStopFromRetestLow = true;
                DynamicStopExtraTicks = 8;
                MaxStopTicks = 60;
                CooldownBarsAfterExit = 3;
                MaxTradesPerSession = 1;
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

            if (Bars.IsFirstBarOfSession)
                ResetSession();

            int t = ToTime(Time[0]);

            if (t >= PremarketStartEt && t < RthStartEt)
            {
                BuildPremarket();
                return;
            }

            if (t < RthStartEt || t > RthEndEt)
            {
                rejectTime++;
                return;
            }

            if (state == ReclaimState.PREMARKET_BUILD || state == ReclaimState.WAIT_RTH)
                FinalizePremarket();

            if (!orComplete)
            {
                BuildOpeningRange();
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (tradesToday >= MaxTradesPerSession)
            {
                state = ReclaimState.LOCKED_OUT;
                return;
            }

            if (CurrentBar - lastExitBar < CooldownBarsAfterExit)
                return;

            UpdateStructuralState();

            if (state == ReclaimState.LONG_ARMED)
                TryEnterLong();
        }

        private void BuildPremarket()
        {
            state = ReclaimState.PREMARKET_BUILD;

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

            if (Low[0] < preLow)
                preLow = Low[0];

            preVwapNum += Close[0] * Volume[0];
            preVol += Volume[0];
        }

        private void FinalizePremarket()
        {
            preVwap = preVol > 0.0 ? preVwapNum / preVol : Close[0];

            premarketBearishExhaustion =
                preHighTests >= MinPremarketHighTests &&
                Close[0] <= preHigh - PremarketFailureFromHighPoints;

            if (PrintDiagnostics)
                Print(string.Format("[BAGGER][PRE] high={0:F2} low={1:F2} vwap={2:F2} highTests={3} bearishExhaustion={4}",
                    preHigh, preLow, preVwap, preHighTests, premarketBearishExhaustion));

            state = ReclaimState.OR_BUILD;
        }

        private void BuildOpeningRange()
        {
            state = ReclaimState.OR_BUILD;
            orHigh = Math.Max(orHigh, High[0]);
            orLow = Math.Min(orLow, Low[0]);
            orVwapNum += Close[0] * Volume[0];
            orVol += Volume[0];
            orBars++;

            if (orBars >= OpeningRangeBars)
            {
                orComplete = true;
                orWidth = orHigh - orLow;
                orVwap = orVol > 0.0 ? orVwapNum / orVol : (orHigh + orLow) / 2.0;

                resistanceZoneLow = orHigh;
                resistanceZoneHigh = Math.Max(orHigh, preHigh);
                reclaimLevel = resistanceZoneLow + ReclaimBufferPoints;
                state = ReclaimState.WAIT_OPENING_FLUSH;

                if (PrintDiagnostics)
                    Print(string.Format("[BAGGER][OR] H={0:F2} L={1:F2} W={2:F2} VWAP={3:F2} ReclaimLevel={4:F2} Zone={5:F2}-{6:F2}",
                        orHigh, orLow, orWidth, orVwap, reclaimLevel, resistanceZoneLow, resistanceZoneHigh));
            }
        }

        private void UpdateStructuralState()
        {
            if (RequirePremarketExhaustion && !premarketBearishExhaustion)
            {
                rejectPremarket++;
                return;
            }

            if (!openingFlushSeen)
            {
                if (!RequireOpeningFlush || Low[0] <= orLow - OpeningFlushBelowOrLowPoints)
                {
                    openingFlushSeen = true;
                    state = ReclaimState.WAIT_FAILED_BEAR;
                    if (PrintDiagnostics) Print(string.Format("[BAGGER][FLUSH] {0:HH:mm:ss} low={1:F2}", Time[0], Low[0]));
                }
                else { rejectFlush++; return; }
            }

            if (!failedBearSeen)
            {
                if (Close[0] >= orLow + FailedBearReclaimOrLowBufferPoints)
                {
                    failedBearSeen = true;
                    state = ReclaimState.WAIT_RECLAIM;
                    if (PrintDiagnostics) Print(string.Format("[BAGGER][FAILED_BEAR] {0:HH:mm:ss} close={1:F2}", Time[0], Close[0]));
                }
                else { rejectFailedBear++; return; }
            }

            if (!reclaimSeen)
            {
                if (ValidResistanceReclaim())
                {
                    reclaimSeen = true;
                    reclaimBar = CurrentBar;
                    state = ReclaimState.WAIT_RETEST;
                    if (PrintDiagnostics) Print(string.Format("[BAGGER][RECLAIM] {0:HH:mm:ss} close={1:F2}", Time[0], Close[0]));
                }
                else { rejectReclaim++; return; }
            }

            if (reclaimSeen && !retestSeen && CurrentBar - reclaimBar > MaxBarsAfterReclaimForRetest)
            {
                if (PrintDiagnostics) Print("[BAGGER][EXPIRE] Reclaim expired without retest.");
                state = ReclaimState.LOCKED_OUT;
                return;
            }

            if (!retestSeen)
            {
                if (ValidRetestHold())
                {
                    retestSeen = true;
                    state = ReclaimState.LONG_ARMED;
                    if (PrintDiagnostics) Print(string.Format("[BAGGER][RETEST] {0:HH:mm:ss} close={1:F2}", Time[0], Close[0]));
                }
                else { rejectRetest++; return; }
            }
        }

        private bool ValidResistanceReclaim()
        {
            bool reclaimClose = Close[0] >= reclaimLevel;
            bool bullish = !RequireBullishReclaimCandle || Close[0] > Open[0];
            bool aboveVwap = Close[0] > orVwap && Close[0] > preVwap;
            double avgVol = SMA(Volume, Math.Max(2, ReclaimLookbackBars))[0];
            bool volumeOk = avgVol <= 0.0 || Volume[0] >= avgVol * MinReclaimVolumeMultiplier;
            if (!volumeOk) rejectVolume++;
            return reclaimClose && bullish && aboveVwap && volumeOk;
        }

        private bool ValidRetestHold()
        {
            bool tagsReclaimZone = Low[0] <= reclaimLevel + RetestTolerancePoints;
            bool holdsAboveOrHigh = Close[0] >= orHigh + RetestHoldBufferPoints;
            bool aboveVwap = Close[0] > orVwap;
            bool candleOk = !RequireBullishRetestCandle || Close[0] >= Open[0];
            return tagsReclaimZone && holdsAboveOrHigh && aboveVwap && candleOk;
        }

        private void TryEnterLong()
        {
            int stopTicks = StopTicks;
            if (UseDynamicStopFromRetestLow)
            {
                double riskPoints = Math.Max(StopTicks * TickSize, Close[0] - Low[0] + DynamicStopExtraTicks * TickSize);
                stopTicks = Math.Min(MaxStopTicks, Math.Max(StopTicks, (int)Math.Ceiling(riskPoints / TickSize)));
            }

            SetStopLoss(LongSignal, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(LongSignal, CalculationMode.Ticks, TargetTicks);

            if (PrintDiagnostics)
                Print(string.Format("[BAGGER][ENTRY] LONG close={0:F2} stopTicks={1} targetTicks={2} time={3:HH:mm:ss}",
                    Close[0], stopTicks, TargetTicks, Time[0]));

            EnterLong(1, LongSignal);
            entries++;
            tradesToday++;
            state = ReclaimState.TRADED;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null) return;
            if (execution.Order.OrderState != OrderState.Filled && execution.Order.OrderState != OrderState.PartFilled) return;

            string name = execution.Order.Name ?? "";
            if (name != LongSignal)
            {
                exits++;
                lastExitBar = CurrentBar;
                if (PrintDiagnostics) Print(string.Format("[BAGGER][EXIT] {0} price={1:F2} time={2:HH:mm:ss}", name, price, time));
            }
            else
            {
                if (PrintDiagnostics) Print(string.Format("[BAGGER][FILL] LONG price={0:F2} qty={1} time={2:HH:mm:ss}", price, quantity, time));
            }
        }

        private void ResetSession()
        {
            state = ReclaimState.WAIT_RTH;
            preHigh = double.MinValue; preLow = double.MaxValue; preVwapNum = 0.0; preVol = 0.0; preVwap = 0.0; preHighTests = 0; premarketBearishExhaustion = false;
            orComplete = false; orHigh = double.MinValue; orLow = double.MaxValue; orWidth = 0.0; orVwapNum = 0.0; orVol = 0.0; orVwap = 0.0; orBars = 0;
            openingFlushSeen = false; failedBearSeen = false; reclaimSeen = false; retestSeen = false;
            resistanceZoneLow = 0.0; resistanceZoneHigh = 0.0; reclaimLevel = 0.0; reclaimBar = -1; tradesToday = 0;
            if (PrintDiagnostics) Print("[BAGGER][SESSION] Reset " + Time[0].ToString("yyyy-MM-dd"));
        }

        private void PrintSummary()
        {
            if (!PrintDiagnostics) return;
            Print("╔════════════════════════════════════════════════════════════╗");
            Print("║ CG MNQ RTH RECLAIM BAGGER v1 SUMMARY                      ║");
            Print("╠════════════════════════════════════════════════════════════╣");
            Print(" State: " + state);
            Print(string.Format(" PRE: H={0:F2} L={1:F2} VWAP={2:F2} Tests={3} Exhaustion={4}", preHigh, preLow, preVwap, preHighTests, premarketBearishExhaustion));
            Print(string.Format(" OR: H={0:F2} L={1:F2} W={2:F2} VWAP={3:F2}", orHigh, orLow, orWidth, orVwap));
            Print(string.Format(" ReclaimLevel={0:F2} Zone={1:F2}-{2:F2}", reclaimLevel, resistanceZoneLow, resistanceZoneHigh));
            Print(" openingFlushSeen: " + openingFlushSeen + " failedBearSeen: " + failedBearSeen + " reclaimSeen: " + reclaimSeen + " retestSeen: " + retestSeen);
            Print(" entries: " + entries + " exits: " + exits);
            Print(" rejectTime: " + rejectTime + " rejectPremarket: " + rejectPremarket + " rejectFlush: " + rejectFlush + " rejectFailedBear: " + rejectFailedBear + " rejectReclaim: " + rejectReclaim + " rejectRetest: " + rejectRetest + " rejectVolume: " + rejectVolume);
            Print("╚════════════════════════════════════════════════════════════╝");
        }

        [NinjaScriptProperty, Range(0, 235959)] [Display(Name="PremarketStartEt", Order=1, GroupName="01. Session")] public int PremarketStartEt { get; set; }
        [NinjaScriptProperty, Range(0, 235959)] [Display(Name="RthStartEt", Order=2, GroupName="01. Session")] public int RthStartEt { get; set; }
        [NinjaScriptProperty, Range(0, 235959)] [Display(Name="RthEndEt", Order=3, GroupName="01. Session")] public int RthEndEt { get; set; }
        [NinjaScriptProperty, Range(1, 30)] [Display(Name="OpeningRangeBars", Order=4, GroupName="01. Session")] public int OpeningRangeBars { get; set; }

        [NinjaScriptProperty] [Display(Name="RequirePremarketExhaustion", Order=1, GroupName="02. Premarket")] public bool RequirePremarketExhaustion { get; set; }
        [NinjaScriptProperty, Range(1.0, 30.0)] [Display(Name="PremarketHighTestTolerancePoints", Order=2, GroupName="02. Premarket")] public double PremarketHighTestTolerancePoints { get; set; }
        [NinjaScriptProperty, Range(1, 10)] [Display(Name="MinPremarketHighTests", Order=3, GroupName="02. Premarket")] public int MinPremarketHighTests { get; set; }
        [NinjaScriptProperty, Range(1.0, 100.0)] [Display(Name="PremarketFailureFromHighPoints", Order=4, GroupName="02. Premarket")] public double PremarketFailureFromHighPoints { get; set; }

        [NinjaScriptProperty] [Display(Name="RequireOpeningFlush", Order=1, GroupName="03. Flush")] public bool RequireOpeningFlush { get; set; }
        [NinjaScriptProperty, Range(0.0, 50.0)] [Display(Name="OpeningFlushBelowOrLowPoints", Order=2, GroupName="03. Flush")] public double OpeningFlushBelowOrLowPoints { get; set; }
        [NinjaScriptProperty, Range(0.0, 50.0)] [Display(Name="FailedBearReclaimOrLowBufferPoints", Order=3, GroupName="03. Flush")] public double FailedBearReclaimOrLowBufferPoints { get; set; }

        [NinjaScriptProperty, Range(0.0, 30.0)] [Display(Name="ReclaimBufferPoints", Order=1, GroupName="04. Reclaim")] public double ReclaimBufferPoints { get; set; }
        [NinjaScriptProperty, Range(1, 20)] [Display(Name="ReclaimLookbackBars", Order=2, GroupName="04. Reclaim")] public int ReclaimLookbackBars { get; set; }
        [NinjaScriptProperty, Range(0.1, 10.0)] [Display(Name="MinReclaimVolumeMultiplier", Order=3, GroupName="04. Reclaim")] public double MinReclaimVolumeMultiplier { get; set; }
        [NinjaScriptProperty] [Display(Name="RequireBullishReclaimCandle", Order=4, GroupName="04. Reclaim")] public bool RequireBullishReclaimCandle { get; set; }

        [NinjaScriptProperty, Range(0.0, 30.0)] [Display(Name="RetestTolerancePoints", Order=1, GroupName="05. Retest")] public double RetestTolerancePoints { get; set; }
        [NinjaScriptProperty, Range(0.0, 30.0)] [Display(Name="RetestHoldBufferPoints", Order=2, GroupName="05. Retest")] public double RetestHoldBufferPoints { get; set; }
        [NinjaScriptProperty, Range(1, 50)] [Display(Name="MaxBarsAfterReclaimForRetest", Order=3, GroupName="05. Retest")] public int MaxBarsAfterReclaimForRetest { get; set; }
        [NinjaScriptProperty] [Display(Name="RequireBullishRetestCandle", Order=4, GroupName="05. Retest")] public bool RequireBullishRetestCandle { get; set; }

        [NinjaScriptProperty, Range(1, 200)] [Display(Name="StopTicks", Order=1, GroupName="06. Risk")] public int StopTicks { get; set; }
        [NinjaScriptProperty, Range(1, 400)] [Display(Name="TargetTicks", Order=2, GroupName="06. Risk")] public int TargetTicks { get; set; }
        [NinjaScriptProperty] [Display(Name="UseDynamicStopFromRetestLow", Order=3, GroupName="06. Risk")] public bool UseDynamicStopFromRetestLow { get; set; }
        [NinjaScriptProperty, Range(0, 100)] [Display(Name="DynamicStopExtraTicks", Order=4, GroupName="06. Risk")] public int DynamicStopExtraTicks { get; set; }
        [NinjaScriptProperty, Range(1, 200)] [Display(Name="MaxStopTicks", Order=5, GroupName="06. Risk")] public int MaxStopTicks { get; set; }

        [NinjaScriptProperty, Range(0, 50)] [Display(Name="CooldownBarsAfterExit", Order=1, GroupName="07. Governance")] public int CooldownBarsAfterExit { get; set; }
        [NinjaScriptProperty, Range(1, 10)] [Display(Name="MaxTradesPerSession", Order=2, GroupName="07. Governance")] public int MaxTradesPerSession { get; set; }
        [NinjaScriptProperty] [Display(Name="PrintDiagnostics", Order=1, GroupName="08. Diagnostics")] public bool PrintDiagnostics { get; set; }
    }
}
