// CG_T2_ClanMarshal_v9_2_LIGHT_B4_SmokeLive.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-06 14:15:00 America/New_York
//
// MNQ Intraday Strategy — v9.2 LIGHT B4 Smoke/Live
//
// PURPOSE OF THIS VERSION
//   B3 was mechanically stable but generated no executions in the tested day.
//   B4 is a corrective "smoke/live" version intended to prove that the
//   single-series tick engine actually sees ticks, evaluates setups, and submits orders.
//
// MAJOR FIX VS B3
//   B3 relied primarily on OnMarketData() for the trading engine.
//   In NinjaTrader playback/historical/replay workflows, OnMarketData behavior can be
//   different from the actual OnBarUpdate strategy execution path.
//   B4 moves the trading engine to OnBarUpdate(), which is the correct primary path
//   for a 1-tick chart strategy.
//
// ARCHITECTURE RULES
//   - Single-series only.
//   - NO AddDataSeries().
//   - Designed to be manually applied to an MNQ 1-tick chart.
//   - Calculate.OnEachTick.
//   - Managed OCO brackets through SetStopLoss / SetProfitTarget.
//   - One MNQ contract only.
//   - No L2 processing.
//   - No telemetry file writes.
//   - Lightweight rolling price buffer.
//   - Momentum-cascade suppression retained.
//
// REQUIRED CHART SETUP
//   Instrument: MNQ front contract
//   Bars type:  Tick
//   Value:      1
//
// DEFAULT SESSION NOTE
//   EntryStartTime / EntryEndTime use the chart's displayed time zone.
//   If your NinjaTrader chart is set to Eastern time, use ET values.
//   If your chart is set to Central time, use CT values.
//   B4 defaults to a broad smoke-test window:
//      08:30:00 to 15:00:00
//   Tighten after executions are confirmed.
//
// DIAGNOSTIC GOAL
//   If no trades occur, the Output window should still show periodic diagnostics:
//     - tick count
//     - session pass/fail
//     - cooldown pass/fail
//     - long/short setup counts
//     - suppression state
//
// FILE INSTALL
//   Documents\NinjaTrader 8\bin\Custom\Strategies\CG_T2_ClanMarshal_v9_2_LIGHT_B4_SmokeLive.cs
//
// DISCLAIMER
//   Research strategy. Validate in Playback/Sim before live use.

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
    public class CG_T2_ClanMarshal_v9_2_LIGHT_B4_SmokeLive : Strategy
    {
        // --------------------------------------------------------------------
        // Internal state
        // --------------------------------------------------------------------

        private double[] recentPrices;
        private int recentIndex;
        private int recentCount;

        private DateTime currentSessionDate = Core.Globals.MinDate;
        private DateTime entryTime = Core.Globals.MinDate;
        private DateTime lastExitTime = Core.Globals.MinDate;

        private DateTime longLockoutUntil = Core.Globals.MinDate;
        private DateTime shortLockoutUntil = Core.Globals.MinDate;
        private DateTime longCascadeSuppressUntil = Core.Globals.MinDate;
        private DateTime shortCascadeSuppressUntil = Core.Globals.MinDate;

        private DateTime lastLongEmergencyTime = Core.Globals.MinDate;
        private DateTime lastShortEmergencyTime = Core.Globals.MinDate;

        private double entryPrice = 0.0;
        private double highSinceEntry = 0.0;
        private double lowSinceEntry = 0.0;
        private double lastLongEmergencyLow = 0.0;
        private double lastShortEmergencyHigh = 0.0;

        private int tradesToday = 0;
        private int emergencyExitsToday = 0;
        private int longEmergencyCountWindow = 0;
        private int shortEmergencyCountWindow = 0;

        private long ticksSeenToday = 0;
        private long longSetupsSeenToday = 0;
        private long shortSetupsSeenToday = 0;
        private long sessionBlockedToday = 0;
        private long cooldownBlockedToday = 0;
        private long governanceBlockedToday = 0;
        private long longSuppressedToday = 0;
        private long shortSuppressedToday = 0;

        private int lastDiagBar = -1;

        private const string LongSignalName = "B4_Long";
        private const string ShortSignalName = "B4_Short";

        // --------------------------------------------------------------------
        // User parameters
        // --------------------------------------------------------------------

        [NinjaScriptProperty]
        [Display(Name = "UseSessionFilter", GroupName = "01 Session", Order = 1)]
        public bool UseSessionFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EntryStartTime", GroupName = "01 Session", Order = 2)]
        public int EntryStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EntryEndTime", GroupName = "01 Session", Order = 3)]
        public int EntryEndTime { get; set; }

        [NinjaScriptProperty]
        [Range(3, 200)]
        [Display(Name = "ImpulseLookbackTicks", GroupName = "02 Impulse", Order = 1)]
        public int ImpulseLookbackTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "LongMinImpulseTicks", GroupName = "03 Long", Order = 1)]
        public int LongMinImpulseTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "LongReclaimTicks", GroupName = "03 Long", Order = 2)]
        public int LongReclaimTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "LongMinTicksAfterLow", GroupName = "03 Long", Order = 3)]
        public int LongMinTicksAfterLow { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ShortMinImpulseTicks", GroupName = "04 Short", Order = 1)]
        public int ShortMinImpulseTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ShortRejectTicks", GroupName = "04 Short", Order = 2)]
        public int ShortRejectTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "ShortMinTicksAfterHigh", GroupName = "04 Short", Order = 3)]
        public int ShortMinTicksAfterHigh { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireTurnTick", GroupName = "05 Entry Quality", Order = 1)]
        public bool RequireTurnTick { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "ProfitTargetTicks", GroupName = "06 Exits", Order = 1)]
        public int ProfitTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "StopLossTicks", GroupName = "06 Exits", Order = 2)]
        public int StopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "EmergencyExitTicks", GroupName = "06 Exits", Order = 3)]
        public int EmergencyExitTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 600)]
        [Display(Name = "TimeStopSeconds", GroupName = "06 Exits", Order = 4)]
        public int TimeStopSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 300)]
        [Display(Name = "EarlyFailureSeconds", GroupName = "06 Exits", Order = 5)]
        public int EarlyFailureSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "EarlyFailureMinProgressTicks", GroupName = "06 Exits", Order = 6)]
        public int EarlyFailureMinProgressTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 600)]
        [Display(Name = "CooldownSeconds", GroupName = "07 Governance", Order = 1)]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxTradesPerDay", GroupName = "07 Governance", Order = 2)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxEmergencyExits", GroupName = "07 Governance", Order = 3)]
        public int MaxEmergencyExits { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "SideLockoutMinutes", GroupName = "07 Governance", Order = 4)]
        public int SideLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "CascadeEmergencyThreshold", GroupName = "08 Cascade Suppression", Order = 1)]
        public int CascadeEmergencyThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "CascadeWindowMinutes", GroupName = "08 Cascade Suppression", Order = 2)]
        public int CascadeWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "CascadeSuppressMinutes", GroupName = "08 Cascade Suppression", Order = 3)]
        public int CascadeSuppressMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "CascadeContinuationTicks", GroupName = "08 Cascade Suppression", Order = 4)]
        public int CascadeContinuationTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", GroupName = "09 Diagnostics", Order = 1)]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(50, 100000)]
        [Display(Name = "DiagnosticEveryTicks", GroupName = "09 Diagnostics", Order = 2)]
        public int DiagnosticEveryTicks { get; set; }

        // --------------------------------------------------------------------
        // NinjaTrader lifecycle
        // --------------------------------------------------------------------

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_2_LIGHT_B4_SmokeLive";
                Description = "v9.2 LIGHT B4 smoke/live single-series tick fade engine with diagnostics and cascade suppression.";

                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                TimeInForce = TimeInForce.Day;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;

                // Broad smoke-test window. These are chart-time HHMMSS values.
                UseSessionFilter = true;
                EntryStartTime = 83000;
                EntryEndTime = 150000;

                // Looser defaults to prove executions happen.
                ImpulseLookbackTicks = 16;

                LongMinImpulseTicks = 4;
                LongReclaimTicks = 2;
                LongMinTicksAfterLow = 1;

                ShortMinImpulseTicks = 4;
                ShortRejectTicks = 2;
                ShortMinTicksAfterHigh = 1;

                RequireTurnTick = true;

                // Quick-trade brackets.
                ProfitTargetTicks = 8;
                StopLossTicks = 8;
                EmergencyExitTicks = 7;
                TimeStopSeconds = 45;
                EarlyFailureSeconds = 15;
                EarlyFailureMinProgressTicks = 2;

                // Governance.
                CooldownSeconds = 30;
                MaxTradesPerDay = 25;
                MaxEmergencyExits = 8;
                SideLockoutMinutes = 3;

                // Cascade suppression.
                CascadeEmergencyThreshold = 2;
                CascadeWindowMinutes = 12;
                CascadeSuppressMinutes = 8;
                CascadeContinuationTicks = 4;

                PrintDiagnostics = true;
                DiagnosticEveryTicks = 1000;
            }
            else if (State == State.Configure)
            {
                SetProfitTarget(LongSignalName, CalculationMode.Ticks, ProfitTargetTicks);
                SetStopLoss(LongSignalName, CalculationMode.Ticks, StopLossTicks, false);

                SetProfitTarget(ShortSignalName, CalculationMode.Ticks, ProfitTargetTicks);
                SetStopLoss(ShortSignalName, CalculationMode.Ticks, StopLossTicks, false);
            }
            else if (State == State.DataLoaded)
            {
                int bufferSize = Math.Max(ImpulseLookbackTicks + 32, 128);
                recentPrices = new double[bufferSize];
                recentIndex = 0;
                recentCount = 0;

                if (PrintDiagnostics)
                {
                    Print("------------------------------------------------------------");
                    Print(Name + " loaded.");
                    Print("Required chart: MNQ 1-tick chart.");
                    Print("Actual BarsPeriod: " + BarsPeriod.BarsPeriodType + " value=" + BarsPeriod.Value);
                    Print("Session filter: " + UseSessionFilter + " " + EntryStartTime + " to " + EntryEndTime + " chart time.");
                    Print("No AddDataSeries() is used.");
                    Print("------------------------------------------------------------");
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 2)
                return;

            ResetSessionIfNeeded();

            double px = Close[0];
            DateTime now = Time[0];

            ticksSeenToday++;
            PushRecentPrice(px);

            ManageOpenPosition(px, now);

            if (Position.MarketPosition == MarketPosition.Flat)
                EvaluateEntries(px, now);

            MaybePrintDiagnostics(px, now);
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

            if (execution.Order.OrderState != OrderState.Filled)
                return;

            string orderName = execution.Order.Name ?? string.Empty;

            if (orderName == LongSignalName || orderName == ShortSignalName)
            {
                entryTime = time;
                entryPrice = price;
                highSinceEntry = price;
                lowSinceEntry = price;
                tradesToday++;

                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} FILLED ENTRY {1} @ {2:F2} tradesToday={3}",
                        time, orderName, price, tradesToday));

                return;
            }

            if (marketPosition == MarketPosition.Flat)
            {
                lastExitTime = time;

                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} FILLED EXIT {1} @ {2:F2} cooldownUntil={3:HH:mm:ss}",
                        time, orderName, price, time.AddSeconds(CooldownSeconds)));
            }
        }

        // --------------------------------------------------------------------
        // Entry engine
        // --------------------------------------------------------------------

        private void EvaluateEntries(double px, DateTime now)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (!IsTradableSession(now))
            {
                sessionBlockedToday++;
                return;
            }

            if (tradesToday >= MaxTradesPerDay || emergencyExitsToday >= MaxEmergencyExits)
            {
                governanceBlockedToday++;
                return;
            }

            if (lastExitTime != Core.Globals.MinDate && now < lastExitTime.AddSeconds(CooldownSeconds))
            {
                cooldownBlockedToday++;
                return;
            }

            if (recentCount < Math.Max(ImpulseLookbackTicks, 4))
                return;

            bool longSetup = IsLongFadeSetup(px);
            bool shortSetup = IsShortFadeSetup(px);

            if (longSetup)
                longSetupsSeenToday++;

            if (shortSetup)
                shortSetupsSeenToday++;

            if (longSetup && CanEnterLong(now))
            {
                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} SUBMIT LONG px={1:F2}", now, px));

                EnterLong(1, LongSignalName);
                return;
            }

            if (shortSetup && CanEnterShort(now))
            {
                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} SUBMIT SHORT px={1:F2}", now, px));

                EnterShort(1, ShortSignalName);
                return;
            }

            if (longSetup && !CanEnterLong(now))
                longSuppressedToday++;

            if (shortSetup && !CanEnterShort(now))
                shortSuppressedToday++;
        }

        private bool CanEnterLong(DateTime now)
        {
            return now >= longLockoutUntil && now >= longCascadeSuppressUntil;
        }

        private bool CanEnterShort(DateTime now)
        {
            return now >= shortLockoutUntil && now >= shortCascadeSuppressUntil;
        }

        private bool IsLongFadeSetup(double px)
        {
            double recentHigh = GetRecentHigh();
            double recentLow = GetRecentLow();

            int impulseTicks = ToTicks(recentHigh - recentLow);
            int reclaimTicks = ToTicks(px - recentLow);
            int ticksAfterLow = TicksSinceExtreme(false);

            bool turnOk = true;
            if (RequireTurnTick && recentCount >= 2)
                turnOk = px > GetRecentPriceByAge(1);

            return impulseTicks >= LongMinImpulseTicks
                && reclaimTicks >= LongReclaimTicks
                && ticksAfterLow >= LongMinTicksAfterLow
                && turnOk;
        }

        private bool IsShortFadeSetup(double px)
        {
            double recentHigh = GetRecentHigh();
            double recentLow = GetRecentLow();

            int impulseTicks = ToTicks(recentHigh - recentLow);
            int rejectTicks = ToTicks(recentHigh - px);
            int ticksAfterHigh = TicksSinceExtreme(true);

            bool turnOk = true;
            if (RequireTurnTick && recentCount >= 2)
                turnOk = px < GetRecentPriceByAge(1);

            return impulseTicks >= ShortMinImpulseTicks
                && rejectTicks >= ShortRejectTicks
                && ticksAfterHigh >= ShortMinTicksAfterHigh
                && turnOk;
        }

        // --------------------------------------------------------------------
        // Exit engine
        // --------------------------------------------------------------------

        private void ManageOpenPosition(double px, DateTime now)
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            highSinceEntry = Math.Max(highSinceEntry, px);
            lowSinceEntry = Math.Min(lowSinceEntry, px);

            double heldSeconds = entryTime == Core.Globals.MinDate
                ? 0.0
                : Math.Max(0.0, (now - entryTime).TotalSeconds);

            if (Position.MarketPosition == MarketPosition.Long)
            {
                int adverseTicks = ToTicks(entryPrice - px);
                int favorableTicks = ToTicks(highSinceEntry - entryPrice);

                if (adverseTicks >= EmergencyExitTicks)
                {
                    RegisterEmergencyExit(true, now, px);
                    ExitLong("B4_Emergency_Long", LongSignalName);
                    return;
                }

                if (heldSeconds >= TimeStopSeconds)
                {
                    ExitLong("B4_TimeStop_Long", LongSignalName);
                    return;
                }

                if (heldSeconds >= EarlyFailureSeconds && favorableTicks < EarlyFailureMinProgressTicks)
                {
                    ExitLong("B4_EarlyFailure_Long", LongSignalName);
                    return;
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                int adverseTicks = ToTicks(px - entryPrice);
                int favorableTicks = ToTicks(entryPrice - lowSinceEntry);

                if (adverseTicks >= EmergencyExitTicks)
                {
                    RegisterEmergencyExit(false, now, px);
                    ExitShort("B4_Emergency_Short", ShortSignalName);
                    return;
                }

                if (heldSeconds >= TimeStopSeconds)
                {
                    ExitShort("B4_TimeStop_Short", ShortSignalName);
                    return;
                }

                if (heldSeconds >= EarlyFailureSeconds && favorableTicks < EarlyFailureMinProgressTicks)
                {
                    ExitShort("B4_EarlyFailure_Short", ShortSignalName);
                    return;
                }
            }
        }

        private void RegisterEmergencyExit(bool wasLong, DateTime now, double px)
        {
            emergencyExitsToday++;
            lastExitTime = now;

            if (wasLong)
            {
                longLockoutUntil = now.AddMinutes(SideLockoutMinutes);

                if (lastLongEmergencyTime == Core.Globals.MinDate ||
                    now > lastLongEmergencyTime.AddMinutes(CascadeWindowMinutes))
                    longEmergencyCountWindow = 1;
                else
                    longEmergencyCountWindow++;

                lastLongEmergencyTime = now;
                lastLongEmergencyLow = px;

                if (ShouldSuppressLongCascade(px))
                    longCascadeSuppressUntil = now.AddMinutes(CascadeSuppressMinutes);
            }
            else
            {
                shortLockoutUntil = now.AddMinutes(SideLockoutMinutes);

                if (lastShortEmergencyTime == Core.Globals.MinDate ||
                    now > lastShortEmergencyTime.AddMinutes(CascadeWindowMinutes))
                    shortEmergencyCountWindow = 1;
                else
                    shortEmergencyCountWindow++;

                lastShortEmergencyTime = now;
                lastShortEmergencyHigh = px;

                if (ShouldSuppressShortCascade(px))
                    shortCascadeSuppressUntil = now.AddMinutes(CascadeSuppressMinutes);
            }

            if (PrintDiagnostics)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EMERGENCY {1} px={2:F2} emergencyToday={3} longSuppressUntil={4:HH:mm:ss} shortSuppressUntil={5:HH:mm:ss}",
                    now,
                    wasLong ? "LONG" : "SHORT",
                    px,
                    emergencyExitsToday,
                    longCascadeSuppressUntil,
                    shortCascadeSuppressUntil));
            }
        }

        private bool ShouldSuppressLongCascade(double px)
        {
            if (longEmergencyCountWindow < CascadeEmergencyThreshold)
                return false;

            double recentLow = GetRecentLow();
            int nearRecentLowTicks = ToTicks(px - recentLow);
            int continuationTicks = ToTicks(lastLongEmergencyLow - px);

            return continuationTicks >= CascadeContinuationTicks || nearRecentLowTicks <= 1;
        }

        private bool ShouldSuppressShortCascade(double px)
        {
            if (shortEmergencyCountWindow < CascadeEmergencyThreshold)
                return false;

            double recentHigh = GetRecentHigh();
            int nearRecentHighTicks = ToTicks(recentHigh - px);
            int continuationTicks = ToTicks(px - lastShortEmergencyHigh);

            return continuationTicks >= CascadeContinuationTicks || nearRecentHighTicks <= 1;
        }

        // --------------------------------------------------------------------
        // Rolling buffer helpers
        // --------------------------------------------------------------------

        private void PushRecentPrice(double px)
        {
            if (recentPrices == null || recentPrices.Length == 0)
                return;

            recentPrices[recentIndex] = px;
            recentIndex = (recentIndex + 1) % recentPrices.Length;
            recentCount = Math.Min(recentCount + 1, recentPrices.Length);
        }

        private double GetRecentHigh()
        {
            int n = Math.Min(ImpulseLookbackTicks, recentCount);
            if (n <= 0)
                return Close[0];

            double hi = double.MinValue;
            for (int age = 0; age < n; age++)
            {
                double v = GetRecentPriceByAge(age);
                if (v > hi)
                    hi = v;
            }

            return hi;
        }

        private double GetRecentLow()
        {
            int n = Math.Min(ImpulseLookbackTicks, recentCount);
            if (n <= 0)
                return Close[0];

            double lo = double.MaxValue;
            for (int age = 0; age < n; age++)
            {
                double v = GetRecentPriceByAge(age);
                if (v < lo)
                    lo = v;
            }

            return lo;
        }

        private int TicksSinceExtreme(bool highExtreme)
        {
            int n = Math.Min(ImpulseLookbackTicks, recentCount);
            if (n <= 0)
                return 0;

            double extreme = highExtreme ? double.MinValue : double.MaxValue;
            int extremeAge = 0;

            for (int age = 0; age < n; age++)
            {
                double v = GetRecentPriceByAge(age);

                if (highExtreme)
                {
                    if (v > extreme)
                    {
                        extreme = v;
                        extremeAge = age;
                    }
                }
                else
                {
                    if (v < extreme)
                    {
                        extreme = v;
                        extremeAge = age;
                    }
                }
            }

            return extremeAge;
        }

        private double GetRecentPriceByAge(int age)
        {
            int idx = recentIndex - 1 - age;
            while (idx < 0)
                idx += recentPrices.Length;

            return recentPrices[idx % recentPrices.Length];
        }

        // --------------------------------------------------------------------
        // Session / diagnostics / utilities
        // --------------------------------------------------------------------

        private void ResetSessionIfNeeded()
        {
            DateTime d = Time[0].Date;
            if (currentSessionDate == Core.Globals.MinDate)
            {
                currentSessionDate = d;
                return;
            }

            if (d == currentSessionDate)
                return;

            if (PrintDiagnostics)
                PrintDailySummary(Time[0]);

            currentSessionDate = d;

            entryTime = Core.Globals.MinDate;
            lastExitTime = Core.Globals.MinDate;

            longLockoutUntil = Core.Globals.MinDate;
            shortLockoutUntil = Core.Globals.MinDate;
            longCascadeSuppressUntil = Core.Globals.MinDate;
            shortCascadeSuppressUntil = Core.Globals.MinDate;

            lastLongEmergencyTime = Core.Globals.MinDate;
            lastShortEmergencyTime = Core.Globals.MinDate;

            entryPrice = 0.0;
            highSinceEntry = 0.0;
            lowSinceEntry = 0.0;
            lastLongEmergencyLow = 0.0;
            lastShortEmergencyHigh = 0.0;

            tradesToday = 0;
            emergencyExitsToday = 0;
            longEmergencyCountWindow = 0;
            shortEmergencyCountWindow = 0;

            ticksSeenToday = 0;
            longSetupsSeenToday = 0;
            shortSetupsSeenToday = 0;
            sessionBlockedToday = 0;
            cooldownBlockedToday = 0;
            governanceBlockedToday = 0;
            longSuppressedToday = 0;
            shortSuppressedToday = 0;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} NEW SESSION reset", Time[0]));
        }

        private bool IsTradableSession(DateTime now)
        {
            if (!UseSessionFilter)
                return true;

            int t = ToTime(now);
            return t >= EntryStartTime && t <= EntryEndTime;
        }

        private int ToTicks(double points)
        {
            if (TickSize <= 0.0)
                return 0;

            return (int)Math.Round(points / TickSize, MidpointRounding.AwayFromZero);
        }

        private void MaybePrintDiagnostics(double px, DateTime now)
        {
            if (!PrintDiagnostics)
                return;

            if (CurrentBar == lastDiagBar)
                return;

            if (DiagnosticEveryTicks <= 0)
                return;

            if (ticksSeenToday % DiagnosticEveryTicks != 0)
                return;

            lastDiagBar = CurrentBar;

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm:ss} DIAG ticks={1} px={2:F2} pos={3} trades={4} Lsetup={5} Ssetup={6} sessBlk={7} coolBlk={8} govBlk={9} Lsup={10} Ssup={11} recentHigh={12:F2} recentLow={13:F2}",
                now,
                ticksSeenToday,
                px,
                Position.MarketPosition,
                tradesToday,
                longSetupsSeenToday,
                shortSetupsSeenToday,
                sessionBlockedToday,
                cooldownBlockedToday,
                governanceBlockedToday,
                longSuppressedToday,
                shortSuppressedToday,
                GetRecentHigh(),
                GetRecentLow()));
        }

        private void PrintDailySummary(DateTime now)
        {
            Print("------------------------------------------------------------");
            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} B4 DAILY SUMMARY", now));
            Print(string.Format("ticksSeen={0} trades={1} emergencies={2}", ticksSeenToday, tradesToday, emergencyExitsToday));
            Print(string.Format("longSetups={0} shortSetups={1}", longSetupsSeenToday, shortSetupsSeenToday));
            Print(string.Format("sessionBlocked={0} cooldownBlocked={1} governanceBlocked={2}", sessionBlockedToday, cooldownBlockedToday, governanceBlockedToday));
            Print(string.Format("longSuppressed={0} shortSuppressed={1}", longSuppressedToday, shortSuppressedToday));
            Print("------------------------------------------------------------");
        }
    }
}
