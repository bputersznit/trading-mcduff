// CG_MNQ_ORB_T2_PlaybackSmoke_v1.cs
// NinjaTrader 8 Strategy
// Purpose: lightweight NT Playback smoke-test strategy for imported MNQ 12-25 tick/minute data.
// Built as a reduced validation harness before enabling CG_MNQ_Flagship_Hybrid_v1_1.
// Layers enabled: ORB + basic T2 momentum proxy + OCO stop/target.
// Layers disabled by design: T3 wall logic, Padder, telemetry, L2 depth, heavy event loops.
//
// Install:
//   Documents\NinjaTrader 8\bin\Custom\Strategies\CG_MNQ_ORB_T2_PlaybackSmoke_v1.cs
//
// Test target:
//   CH Sep/Oct 2025 MNQZ5 export imported into NinjaTrader as MNQ 12-25.
//
// Methodology:
//   1. Build the opening range from 09:30:00 to 09:45:00 ET.
//   2. After OR completes, allow long signals above OR high + buffer and short signals below OR low - buffer.
//   3. Confirm with a small, cheap T2 proxy: recent signed close-to-close movement.
//   4. Submit exactly one MNQ contract and immediately arm broker-side stop/target via SetStopLoss / SetProfitTarget.
//   5. Keep logic intentionally simple to validate NT Playback, timestamps, imports, and OCO behavior.

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
    public class CG_MNQ_ORB_T2_PlaybackSmoke_v1 : Strategy
    {
        private const string LongSignalName  = "Smoke_Long";
        private const string ShortSignalName = "Smoke_Short";

        private enum SmokeState
        {
            PRE_OR,
            BUILDING_OR,
            OR_COMPLETE,
            LONG_PERMISSION,
            SHORT_PERMISSION,
            FLAT_LOCK
        }

        private SmokeState currentState = SmokeState.PRE_OR;

        private bool orActive = false;
        private bool orComplete = false;
        private double orHigh = double.MinValue;
        private double orLow = double.MaxValue;
        private double orWidth = 0.0;
        private DateTime orEnd = Core.Globals.MinDate;

        private int lastSessionDate = -1;
        private int tradesToday = 0;

        private DateTime entryTime = Core.Globals.MinDate;
        private double entryPrice = 0.0;
        private MarketPosition entrySide = MarketPosition.Flat;

        private long longSignals = 0;
        private long shortSignals = 0;
        private long entries = 0;
        private long timeoutExits = 0;
        private long rejectNotRth = 0;
        private long rejectBeforeOr = 0;
        private long rejectSmallOr = 0;
        private long rejectPosition = 0;
        private long rejectMaxTrades = 0;
        private long rejectT2 = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "CG MNQ ORB+T2 Playback Smoke v1: minimal ORB/T2/OCO validation strategy for NT Playback.";
                Name = "CG_MNQ_ORB_T2_PlaybackSmoke_v1";

                // Default to OnBarClose for playback stability.
                // Use a 1-minute, 5-second, or imported tick chart; switch to OnEachTick only after smoke success.
                Calculate = Calculate.OnBarClose;

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

                // Session / ORB
                StartTimeEt = 93000;
                EndTimeEt = 155900;
                OpeningRangeMinutes = 15;
                MinRangeWidthPoints = 5.0;
                BreakoutBufferPoints = 1.0;

                // T2 proxy
                T2LookbackBars = 5;
                MinSignedMoveTicks = 4;

                // Execution / risk
                StopTicks = 20;
                TargetTicks = 40;
                MaxHoldSeconds = 600;
                MaxTradesPerDay = 3;

                // Smoke mode
                PrintDiagnostics = true;
            }
            else if (State == State.Configure)
            {
                // OCO++ smoke doctrine: set protection by entry signal name before any entry can occur.
                SetStopLoss(LongSignalName, CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget(LongSignalName, CalculationMode.Ticks, TargetTicks);

                SetStopLoss(ShortSignalName, CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget(ShortSignalName, CalculationMode.Ticks, TargetTicks);
            }
            else if (State == State.Terminated)
            {
                if (PrintDiagnostics)
                    PrintSummary();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            DateTime now = Time[0];
            int sessionDate = now.Year * 10000 + now.Month * 100 + now.Day;

            if (sessionDate != lastSessionDate)
            {
                ResetDailyState(now);
                lastSessionDate = sessionDate;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                ManageOpenPosition(now);
                rejectPosition++;
                return;
            }

            if (!IsWithinRth(now))
            {
                rejectNotRth++;
                return;
            }

            UpdateOpeningRange(now);

            if (!orComplete)
            {
                rejectBeforeOr++;
                return;
            }

            if (orWidth < MinRangeWidthPoints)
            {
                currentState = SmokeState.FLAT_LOCK;
                rejectSmallOr++;
                return;
            }

            if (tradesToday >= MaxTradesPerDay)
            {
                currentState = SmokeState.FLAT_LOCK;
                rejectMaxTrades++;
                return;
            }

            bool longPermitted = Close[0] >= orHigh + BreakoutBufferPoints;
            bool shortPermitted = Close[0] <= orLow - BreakoutBufferPoints;

            if (longPermitted && !shortPermitted)
            {
                currentState = SmokeState.LONG_PERMISSION;

                if (PassesT2Long())
                {
                    longSignals++;
                    SubmitLong(now);
                }
                else
                {
                    rejectT2++;
                }
            }
            else if (shortPermitted && !longPermitted)
            {
                currentState = SmokeState.SHORT_PERMISSION;

                if (PassesT2Short())
                {
                    shortSignals++;
                    SubmitShort(now);
                }
                else
                {
                    rejectT2++;
                }
            }
            else
            {
                currentState = SmokeState.OR_COMPLETE;
            }
        }

        private void UpdateOpeningRange(DateTime now)
        {
            int hhmmss = ToHhMmSs(now);

            if (!orActive && !orComplete && hhmmss >= StartTimeEt)
            {
                orActive = true;
                currentState = SmokeState.BUILDING_OR;
                orEnd = now.Date.AddHours(9).AddMinutes(30 + OpeningRangeMinutes);

                orHigh = High[0];
                orLow = Low[0];

                if (PrintDiagnostics)
                    Print(string.Format("[SMOKE][OR] Start {0:yyyy-MM-dd HH:mm:ss}", now));
            }

            if (!orActive)
                return;

            orHigh = Math.Max(orHigh, High[0]);
            orLow = Math.Min(orLow, Low[0]);

            if (now >= orEnd || hhmmss >= 94500)
            {
                orActive = false;
                orComplete = true;
                currentState = SmokeState.OR_COMPLETE;
                orWidth = orHigh - orLow;

                if (PrintDiagnostics)
                    Print(string.Format("[SMOKE][OR] Complete H={0:F2} L={1:F2} W={2:F2}", orHigh, orLow, orWidth));
            }
        }

        private bool PassesT2Long()
        {
            if (CurrentBar < T2LookbackBars + 1)
                return false;

            double signedTicks = 0.0;

            for (int i = 0; i < T2LookbackBars; i++)
                signedTicks += (Close[i] - Close[i + 1]) / TickSize;

            return signedTicks >= MinSignedMoveTicks;
        }

        private bool PassesT2Short()
        {
            if (CurrentBar < T2LookbackBars + 1)
                return false;

            double signedTicks = 0.0;

            for (int i = 0; i < T2LookbackBars; i++)
                signedTicks += (Close[i] - Close[i + 1]) / TickSize;

            return signedTicks <= -MinSignedMoveTicks;
        }

        private void SubmitLong(DateTime now)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            entryTime = now;
            entryPrice = Close[0];
            entrySide = MarketPosition.Long;
            tradesToday++;
            entries++;

            if (PrintDiagnostics)
                Print(string.Format("[SMOKE][ENTRY] LONG approx={0:F2} {1:HH:mm:ss}", Close[0], now));

            EnterLong(1, LongSignalName);
        }

        private void SubmitShort(DateTime now)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            entryTime = now;
            entryPrice = Close[0];
            entrySide = MarketPosition.Short;
            tradesToday++;
            entries++;

            if (PrintDiagnostics)
                Print(string.Format("[SMOKE][ENTRY] SHORT approx={0:F2} {1:HH:mm:ss}", Close[0], now));

            EnterShort(1, ShortSignalName);
        }

        private void ManageOpenPosition(DateTime now)
        {
            if (entryTime == Core.Globals.MinDate)
                return;

            if ((now - entryTime).TotalSeconds >= MaxHoldSeconds)
            {
                timeoutExits++;

                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("Smoke_Timeout_Long", LongSignalName);
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("Smoke_Timeout_Short", ShortSignalName);

                if (PrintDiagnostics)
                    Print(string.Format("[SMOKE][TIMEOUT] Exit requested at {0:HH:mm:ss}", now));

                entryTime = Core.Globals.MinDate;
                entryPrice = 0.0;
                entrySide = MarketPosition.Flat;
            }
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

            if (name == LongSignalName || name == ShortSignalName)
            {
                entryPrice = price;
                entrySide = marketPosition;
                entryTime = time;

                if (PrintDiagnostics)
                    Print(string.Format("[SMOKE][FILL] {0} price={1:F2} qty={2} time={3:HH:mm:ss}", name, price, quantity, time));
            }
            else if (Position.MarketPosition == MarketPosition.Flat)
            {
                entryTime = Core.Globals.MinDate;
                entryPrice = 0.0;
                entrySide = MarketPosition.Flat;

                if (PrintDiagnostics)
                    Print(string.Format("[SMOKE][EXIT] {0} price={1:F2} time={2:HH:mm:ss}", name, price, time));
            }
        }

        private void ResetDailyState(DateTime now)
        {
            currentState = SmokeState.PRE_OR;
            orActive = false;
            orComplete = false;
            orHigh = double.MinValue;
            orLow = double.MaxValue;
            orWidth = 0.0;
            orEnd = Core.Globals.MinDate;

            tradesToday = 0;
            entryTime = Core.Globals.MinDate;
            entryPrice = 0.0;
            entrySide = MarketPosition.Flat;

            if (PrintDiagnostics)
                Print(string.Format("[SMOKE][SESSION] Reset {0:yyyy-MM-dd}", now));
        }

        private bool IsWithinRth(DateTime t)
        {
            int hhmmss = ToHhMmSs(t);
            return hhmmss >= StartTimeEt && hhmmss <= EndTimeEt;
        }

        private int ToHhMmSs(DateTime t)
        {
            return t.Hour * 10000 + t.Minute * 100 + t.Second;
        }

        private void PrintSummary()
        {
            Print("╔════════════════════════════════════════════════════════════╗");
            Print("║ CG MNQ ORB+T2 PLAYBACK SMOKE v1 SUMMARY                   ║");
            Print("╠════════════════════════════════════════════════════════════╣");
            Print(string.Format(" State: {0}", currentState));
            Print(string.Format(" OR: H={0:F2} L={1:F2} W={2:F2} Complete={3}", orHigh, orLow, orWidth, orComplete));
            Print(string.Format(" Long signals: {0}", longSignals));
            Print(string.Format(" Short signals: {0}", shortSignals));
            Print(string.Format(" Entries: {0}", entries));
            Print(string.Format(" Timeout exits: {0}", timeoutExits));
            Print("╟────────────────────────────────────────────────────────────╢");
            Print(string.Format(" Reject not RTH: {0}", rejectNotRth));
            Print(string.Format(" Reject before OR complete: {0}", rejectBeforeOr));
            Print(string.Format(" Reject small OR: {0}", rejectSmallOr));
            Print(string.Format(" Reject position open: {0}", rejectPosition));
            Print(string.Format(" Reject max trades: {0}", rejectMaxTrades));
            Print(string.Format(" Reject T2: {0}", rejectT2));
            Print("╚════════════════════════════════════════════════════════════╝");
        }

        // ================================================================
        // Parameters
        // ================================================================

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Start Time ET", Order = 1, GroupName = "01. Session")]
        public int StartTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "End Time ET", Order = 2, GroupName = "01. Session")]
        public int EndTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(5, 60)]
        [Display(Name = "Opening Range Minutes", Order = 1, GroupName = "02. ORB")]
        public int OpeningRangeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Min Range Width Points", Order = 2, GroupName = "02. ORB")]
        public double MinRangeWidthPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 20.0)]
        [Display(Name = "Breakout Buffer Points", Order = 3, GroupName = "02. ORB")]
        public double BreakoutBufferPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "T2 Lookback Bars", Order = 1, GroupName = "03. Basic T2")]
        public int T2LookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Min Signed Move Ticks", Order = 2, GroupName = "03. Basic T2")]
        public int MinSignedMoveTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Stop Ticks", Order = 1, GroupName = "04. Execution")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Target Ticks", Order = 2, GroupName = "04. Execution")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(10, 3600)]
        [Display(Name = "Max Hold Seconds", Order = 3, GroupName = "04. Execution")]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max Trades Per Day", Order = 4, GroupName = "04. Execution")]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Print Diagnostics", Order = 1, GroupName = "05. Diagnostics")]
        public bool PrintDiagnostics { get; set; }
    }
}
