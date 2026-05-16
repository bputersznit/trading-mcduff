// CG_MNQ_ORB_T2_PlaybackSmoke_v1_1.cs
// NinjaTrader 8 Strategy
// Purpose: Minimal NT Playback smoke-test strategy upgraded from v1.
// Adds: retest-only entries, cooldown after exits, max-trade limits,
// relative-volume confirmation, wide-OR blocking, one-contract OCO protection.

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
    public class CG_MNQ_ORB_T2_PlaybackSmoke_v1_1 : Strategy
    {
        private const string LongSignalName  = "Smoke_Long";
        private const string ShortSignalName = "Smoke_Short";

        private enum OrbState
        {
            PRE_OR,
            BUILDING_OR,
            NEUTRAL,
            LONG_BREAKOUT_ARMED,
            SHORT_BREAKOUT_ARMED,
            LONG_RETEST_CONFIRMED,
            SHORT_RETEST_CONFIRMED,
            CHOP,
            FLAT_LOCK
        }

        private OrbState orbState = OrbState.PRE_OR;

        private DateTime lastSessionDate = Core.Globals.MinDate;
        private DateTime orStartEt = Core.Globals.MinDate;
        private DateTime orEndEt = Core.Globals.MinDate;

        private bool orActive = false;
        private bool orComplete = false;

        private double orHigh = double.MinValue;
        private double orLow = double.MaxValue;
        private double orWidth = 0.0;
        private double orVwapNumerator = 0.0;
        private double orVolume = 0.0;
        private double orVwap = 0.0;
        private double orAvgBarVolume = 0.0;
        private int orBarCount = 0;

        private bool longBreakoutSeen = false;
        private bool shortBreakoutSeen = false;
        private bool longRetestSeen = false;
        private bool shortRetestSeen = false;

        private DateTime longBreakoutTime = Core.Globals.MinDate;
        private DateTime shortBreakoutTime = Core.Globals.MinDate;

        private int sessionTrades = 0;
        private int longTrades = 0;
        private int shortTrades = 0;
        private int entries = 0;
        private int exits = 0;
        private int timeoutExits = 0;

        private int rejectNotRth = 0;
        private int rejectBeforeOrComplete = 0;
        private int rejectSmallOr = 0;
        private int rejectWideOr = 0;
        private int rejectPositionOpen = 0;
        private int rejectCooldown = 0;
        private int rejectMaxTrades = 0;
        private int rejectRetest = 0;
        private int rejectVolume = 0;
        private int rejectT2 = 0;

        private double entryPrice = 0.0;
        private DateTime entryTime = Core.Globals.MinDate;
        private MarketPosition entryPosition = MarketPosition.Flat;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_MNQ_ORB_T2_PlaybackSmoke_v1_1";
                Description = "MNQ ORB + Basic T2 Playback Smoke v1.1: retest-only entries, cooldown, trade caps, relative volume.";

                Calculate = Calculate.OnBarClose;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                TimeInForce = TimeInForce.Day;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                BarsRequiredToTrade = 20;

                StopTicks = 20;
                TargetTicks = 40;
                MaxHoldSeconds = 600;

                StartTimeEt = 93000;
                EndTimeEt = 155900;
                OpeningRangeMinutes = 15;
                MinRangeWidthPoints = 5.0;
                MaxRangeWidthPoints = 100.0;
                BreakoutBufferPoints = 2.0;
                RetestTolerancePoints = 3.0;
                RetestHoldBufferPoints = 1.0;
                MaxBarsAfterBreakoutForRetest = 8;

                MinT2Bars = 3;
                MinDirectionalBars = 2;
                UseRelativeVolumeFilter = true;
                RelativeVolumeMultiplier = 1.10;

                CooldownBarsAfterExit = 3;
                MaxTradesPerSession = 3;
                MaxTradesPerDirection = 2;
                OneTradePerBreakoutLeg = false;

                PrintDiagnostics = true;
            }
            else if (State == State.Configure)
            {
                SetStopLoss(LongSignalName, CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget(LongSignalName, CalculationMode.Ticks, TargetTicks);

                SetStopLoss(ShortSignalName, CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget(ShortSignalName, CalculationMode.Ticks, TargetTicks);
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

            DateTime et = ToEastern(Time[0]);

            if (lastSessionDate.Date != et.Date)
                ResetSession(et);

            if (!IsWithinRth(et))
            {
                rejectNotRth++;
                return;
            }

            ManageOpeningRange(et);

            if (!orComplete)
            {
                rejectBeforeOrComplete++;
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                rejectPositionOpen++;
                CheckTimeoutExit(et);
                return;
            }

            if (orWidth < MinRangeWidthPoints)
            {
                orbState = OrbState.CHOP;
                rejectSmallOr++;
                return;
            }

            if (orWidth > MaxRangeWidthPoints)
            {
                orbState = OrbState.FLAT_LOCK;
                rejectWideOr++;
                return;
            }

            if (sessionTrades >= MaxTradesPerSession)
            {
                rejectMaxTrades++;
                return;
            }

            int barsSinceExit = BarsSinceExitExecution(0, "", 0);
            if (barsSinceExit >= 0 && barsSinceExit < CooldownBarsAfterExit)
            {
                rejectCooldown++;
                return;
            }

            UpdateOrbState(et);

            bool longOk = ShouldEnterLong();
            bool shortOk = ShouldEnterShort();

            if (longOk && !shortOk)
                SubmitLong(et);
            else if (shortOk && !longOk)
                SubmitShort(et);
        }

        private void ManageOpeningRange(DateTime et)
        {
            int hhmmss = ToHhMmSs(et);

            if (!orActive && !orComplete && hhmmss >= StartTimeEt)
                StartOpeningRange(et);

            if (orActive)
            {
                UpdateOpeningRange();

                if (et >= orEndEt)
                    CompleteOpeningRange(et);
            }
        }

        private void StartOpeningRange(DateTime et)
        {
            orActive = true;
            orComplete = false;
            orbState = OrbState.BUILDING_OR;

            orStartEt = et.Date.AddHours(9).AddMinutes(30);
            orEndEt = orStartEt.AddMinutes(OpeningRangeMinutes);

            orHigh = High[0];
            orLow = Low[0];
            orWidth = 0.0;
            orVwapNumerator = Close[0] * Volume[0];
            orVolume = Volume[0];
            orBarCount = 1;

            if (PrintDiagnostics)
                Print("[SMOKE v1.1][OR] Start " + et.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        private void UpdateOpeningRange()
        {
            orHigh = Math.Max(orHigh, High[0]);
            orLow = Math.Min(orLow, Low[0]);
            orVwapNumerator += Close[0] * Volume[0];
            orVolume += Volume[0];
            orBarCount++;
        }

        private void CompleteOpeningRange(DateTime et)
        {
            orActive = false;
            orComplete = true;
            orbState = OrbState.NEUTRAL;

            orWidth = orHigh - orLow;
            orVwap = orVolume > 0 ? orVwapNumerator / orVolume : (orHigh + orLow) / 2.0;
            orAvgBarVolume = orBarCount > 0 ? orVolume / orBarCount : 0.0;

            if (orWidth < MinRangeWidthPoints)
                orbState = OrbState.CHOP;
            else if (orWidth > MaxRangeWidthPoints)
                orbState = OrbState.FLAT_LOCK;

            if (PrintDiagnostics)
                Print(string.Format("[SMOKE v1.1][OR] Complete H={0:F2} L={1:F2} W={2:F2} VWAP={3:F2} ORAvgVol={4:F0} State={5}",
                    orHigh, orLow, orWidth, orVwap, orAvgBarVolume, orbState));
        }

        private void UpdateOrbState(DateTime et)
        {
            if (!orComplete || orbState == OrbState.CHOP || orbState == OrbState.FLAT_LOCK)
                return;

            double longBreakoutLevel = orHigh + BreakoutBufferPoints;
            double shortBreakoutLevel = orLow - BreakoutBufferPoints;

            if (!longBreakoutSeen && Close[0] > longBreakoutLevel)
            {
                longBreakoutSeen = true;
                longBreakoutTime = et;
                orbState = OrbState.LONG_BREAKOUT_ARMED;

                if (PrintDiagnostics)
                    Print("[SMOKE v1.1][ORB] LONG breakout armed " + et.ToString("HH:mm:ss"));
            }

            if (!shortBreakoutSeen && Close[0] < shortBreakoutLevel)
            {
                shortBreakoutSeen = true;
                shortBreakoutTime = et;
                orbState = OrbState.SHORT_BREAKOUT_ARMED;

                if (PrintDiagnostics)
                    Print("[SMOKE v1.1][ORB] SHORT breakout armed " + et.ToString("HH:mm:ss"));
            }

            if (longBreakoutSeen && !longRetestSeen)
            {
                int barsSinceBreakout = BarsSinceTime(longBreakoutTime);
                bool withinWindow = barsSinceBreakout >= 0 && barsSinceBreakout <= MaxBarsAfterBreakoutForRetest;
                bool taggedOrHigh = Low[0] <= orHigh + RetestTolerancePoints;
                bool heldOrHigh = Close[0] >= orHigh + RetestHoldBufferPoints;

                if (withinWindow && taggedOrHigh && heldOrHigh)
                {
                    longRetestSeen = true;
                    orbState = OrbState.LONG_RETEST_CONFIRMED;

                    if (PrintDiagnostics)
                        Print("[SMOKE v1.1][ORB] LONG retest confirmed " + et.ToString("HH:mm:ss"));
                }
            }

            if (shortBreakoutSeen && !shortRetestSeen)
            {
                int barsSinceBreakout = BarsSinceTime(shortBreakoutTime);
                bool withinWindow = barsSinceBreakout >= 0 && barsSinceBreakout <= MaxBarsAfterBreakoutForRetest;
                bool taggedOrLow = High[0] >= orLow - RetestTolerancePoints;
                bool heldOrLow = Close[0] <= orLow - RetestHoldBufferPoints;

                if (withinWindow && taggedOrLow && heldOrLow)
                {
                    shortRetestSeen = true;
                    orbState = OrbState.SHORT_RETEST_CONFIRMED;

                    if (PrintDiagnostics)
                        Print("[SMOKE v1.1][ORB] SHORT retest confirmed " + et.ToString("HH:mm:ss"));
                }
            }
        }

        private bool ShouldEnterLong()
        {
            if (orbState != OrbState.LONG_RETEST_CONFIRMED)
            {
                rejectRetest++;
                return false;
            }

            if (longTrades >= MaxTradesPerDirection || (OneTradePerBreakoutLeg && longTrades > 0))
            {
                rejectMaxTrades++;
                return false;
            }

            if (UseRelativeVolumeFilter && !VolumeConfirmed())
            {
                rejectVolume++;
                return false;
            }

            if (!T2LongConfirmed())
            {
                rejectT2++;
                return false;
            }

            return true;
        }

        private bool ShouldEnterShort()
        {
            if (orbState != OrbState.SHORT_RETEST_CONFIRMED)
            {
                rejectRetest++;
                return false;
            }

            if (shortTrades >= MaxTradesPerDirection || (OneTradePerBreakoutLeg && shortTrades > 0))
            {
                rejectMaxTrades++;
                return false;
            }

            if (UseRelativeVolumeFilter && !VolumeConfirmed())
            {
                rejectVolume++;
                return false;
            }

            if (!T2ShortConfirmed())
            {
                rejectT2++;
                return false;
            }

            return true;
        }

        private bool VolumeConfirmed()
        {
            if (orAvgBarVolume <= 0)
                return true;

            return Volume[0] >= orAvgBarVolume * RelativeVolumeMultiplier;
        }

        private bool T2LongConfirmed()
        {
            if (CurrentBar < MinT2Bars + 1)
                return false;

            int upBars = 0;
            for (int i = 0; i < MinT2Bars; i++)
            {
                if (Close[i] > Close[i + 1])
                    upBars++;
            }

            return upBars >= MinDirectionalBars && Close[0] > orVwap;
        }

        private bool T2ShortConfirmed()
        {
            if (CurrentBar < MinT2Bars + 1)
                return false;

            int downBars = 0;
            for (int i = 0; i < MinT2Bars; i++)
            {
                if (Close[i] < Close[i + 1])
                    downBars++;
            }

            return downBars >= MinDirectionalBars && Close[0] < orVwap;
        }

        private void SubmitLong(DateTime et)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (PrintDiagnostics)
                Print(string.Format("[SMOKE v1.1][ENTRY] LONG approx={0:F2} {1:HH:mm:ss}", Close[0], et));

            EnterLong(1, LongSignalName);
            entries++;
            sessionTrades++;
            longTrades++;
        }

        private void SubmitShort(DateTime et)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (PrintDiagnostics)
                Print(string.Format("[SMOKE v1.1][ENTRY] SHORT approx={0:F2} {1:HH:mm:ss}", Close[0], et));

            EnterShort(1, ShortSignalName);
            entries++;
            sessionTrades++;
            shortTrades++;
        }

        private void CheckTimeoutExit(DateTime et)
        {
            if (entryTime == Core.Globals.MinDate)
                return;

            if ((et - ToEastern(entryTime)).TotalSeconds < MaxHoldSeconds)
                return;

            if (Position.MarketPosition == MarketPosition.Long)
            {
                ExitLong("TimeoutExit", LongSignalName);
                timeoutExits++;
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                ExitShort("TimeoutExit", ShortSignalName);
                timeoutExits++;
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            if (execution.Order.OrderState != OrderState.Filled && execution.Order.OrderState != OrderState.PartFilled)
                return;

            string name = execution.Order.Name ?? "";
            DateTime et = ToEastern(time);

            if (name == LongSignalName || name == ShortSignalName)
            {
                entryPrice = price;
                entryPosition = marketPosition;
                entryTime = time;

                if (PrintDiagnostics)
                    Print(string.Format("[SMOKE v1.1][FILL] {0} price={1:F2} qty={2} time={3:HH:mm:ss}",
                        name, price, quantity, et));
            }
            else
            {
                exits++;

                if (PrintDiagnostics)
                    Print(string.Format("[SMOKE v1.1][EXIT] {0} price={1:F2} time={2:HH:mm:ss}",
                        name, price, et));

                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    entryPrice = 0.0;
                    entryPosition = MarketPosition.Flat;
                    entryTime = Core.Globals.MinDate;
                }
            }
        }

        private void ResetSession(DateTime et)
        {
            lastSessionDate = et.Date;

            orbState = OrbState.PRE_OR;
            orActive = false;
            orComplete = false;
            orStartEt = Core.Globals.MinDate;
            orEndEt = Core.Globals.MinDate;

            orHigh = double.MinValue;
            orLow = double.MaxValue;
            orWidth = 0.0;
            orVwapNumerator = 0.0;
            orVolume = 0.0;
            orVwap = 0.0;
            orAvgBarVolume = 0.0;
            orBarCount = 0;

            longBreakoutSeen = false;
            shortBreakoutSeen = false;
            longRetestSeen = false;
            shortRetestSeen = false;
            longBreakoutTime = Core.Globals.MinDate;
            shortBreakoutTime = Core.Globals.MinDate;

            sessionTrades = 0;
            longTrades = 0;
            shortTrades = 0;

            entryPrice = 0.0;
            entryPosition = MarketPosition.Flat;
            entryTime = Core.Globals.MinDate;

            if (PrintDiagnostics)
                Print("[SMOKE v1.1][SESSION] Reset " + et.ToString("yyyy-MM-dd"));
        }

        private bool IsWithinRth(DateTime et)
        {
            int hhmmss = ToHhMmSs(et);
            return hhmmss >= StartTimeEt && hhmmss <= EndTimeEt;
        }

        private int ToHhMmSs(DateTime et)
        {
            return et.Hour * 10000 + et.Minute * 100 + et.Second;
        }

        private DateTime ToEastern(DateTime t)
        {
            if (t.Kind != DateTimeKind.Utc)
                return t;

            try
            {
                TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(t, eastern);
            }
            catch
            {
                return t;
            }
        }

        private int BarsSinceTime(DateTime eventTime)
        {
            if (eventTime == Core.Globals.MinDate)
                return -1;

            DateTime eventEt = ToEastern(eventTime);

            for (int i = 0; i <= CurrentBar && i < 1000; i++)
            {
                if (ToEastern(Time[i]) <= eventEt)
                    return i;
            }

            return -1;
        }

        private void PrintSummary()
        {
            if (!PrintDiagnostics)
                return;

            Print("╔════════════════════════════════════════════════════════════╗");
            Print("║ CG MNQ ORB+T2 PLAYBACK SMOKE v1.1 SUMMARY                 ║");
            Print("╠════════════════════════════════════════════════════════════╣");
            Print(" State: " + orbState);
            Print(string.Format(" OR: H={0:F2} L={1:F2} W={2:F2} VWAP={3:F2} Complete={4}",
                orHigh, orLow, orWidth, orVwap, orComplete));
            Print(" Entries: " + entries);
            Print(" Exits: " + exits);
            Print(" Timeout exits: " + timeoutExits);
            Print(" Session trades: " + sessionTrades);
            Print(" Long trades: " + longTrades);
            Print(" Short trades: " + shortTrades);
            Print("╟────────────────────────────────────────────────────────────╢");
            Print(" Reject not RTH: " + rejectNotRth);
            Print(" Reject before OR complete: " + rejectBeforeOrComplete);
            Print(" Reject small OR: " + rejectSmallOr);
            Print(" Reject wide OR: " + rejectWideOr);
            Print(" Reject position open: " + rejectPositionOpen);
            Print(" Reject cooldown: " + rejectCooldown);
            Print(" Reject max trades: " + rejectMaxTrades);
            Print(" Reject retest: " + rejectRetest);
            Print(" Reject volume: " + rejectVolume);
            Print(" Reject T2: " + rejectT2);
            Print("╚════════════════════════════════════════════════════════════╝");
        }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "StopTicks", Order = 1, GroupName = "01. Execution")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "TargetTicks", Order = 2, GroupName = "01. Execution")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(30, 3600)]
        [Display(Name = "MaxHoldSeconds", Order = 3, GroupName = "01. Execution")]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "StartTimeEt", Order = 1, GroupName = "02. Session ORB")]
        public int StartTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EndTimeEt", Order = 2, GroupName = "02. Session ORB")]
        public int EndTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(5, 60)]
        [Display(Name = "OpeningRangeMinutes", Order = 3, GroupName = "02. Session ORB")]
        public int OpeningRangeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 50.0)]
        [Display(Name = "MinRangeWidthPoints", Order = 4, GroupName = "02. Session ORB")]
        public double MinRangeWidthPoints { get; set; }

        [NinjaScriptProperty]
        [Range(10.0, 300.0)]
        [Display(Name = "MaxRangeWidthPoints", Order = 5, GroupName = "02. Session ORB")]
        public double MaxRangeWidthPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 20.0)]
        [Display(Name = "BreakoutBufferPoints", Order = 6, GroupName = "02. Session ORB")]
        public double BreakoutBufferPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 20.0)]
        [Display(Name = "RetestTolerancePoints", Order = 7, GroupName = "02. Session ORB")]
        public double RetestTolerancePoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 20.0)]
        [Display(Name = "RetestHoldBufferPoints", Order = 8, GroupName = "02. Session ORB")]
        public double RetestHoldBufferPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "MaxBarsAfterBreakoutForRetest", Order = 9, GroupName = "02. Session ORB")]
        public int MaxBarsAfterBreakoutForRetest { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MinT2Bars", Order = 1, GroupName = "03. Basic T2")]
        public int MinT2Bars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MinDirectionalBars", Order = 2, GroupName = "03. Basic T2")]
        public int MinDirectionalBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseRelativeVolumeFilter", Order = 3, GroupName = "03. Basic T2")]
        public bool UseRelativeVolumeFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "RelativeVolumeMultiplier", Order = 4, GroupName = "03. Basic T2")]
        public double RelativeVolumeMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "CooldownBarsAfterExit", Order = 1, GroupName = "04. Throttles")]
        public int CooldownBarsAfterExit { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxTradesPerSession", Order = 2, GroupName = "04. Throttles")]
        public int MaxTradesPerSession { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxTradesPerDirection", Order = 3, GroupName = "04. Throttles")]
        public int MaxTradesPerDirection { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OneTradePerBreakoutLeg", Order = 4, GroupName = "04. Throttles")]
        public bool OneTradePerBreakoutLeg { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", Order = 1, GroupName = "05. Diagnostics")]
        public bool PrintDiagnostics { get; set; }
    }
}
