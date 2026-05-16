// CG_T2_ClanMarshal_v9_2_LIGHT_B3_Cascade.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-06 13:58:00 America/New_York
//
// MNQ Intraday Strategy — v9.2 LIGHT B3 Cascade
//
// STRATEGY IDENTITY
//   Lightweight tick-speed microstructure fade engine.
//   This is NOT a trend-following, swing, breakout-continuation, or indicator-stack strategy.
//
// CORE HYPOTHESIS
//   Short-term directional impulses often temporarily overextend before micro-reverting.
//   The strategy fades those impulses only after a reclaim/reject sequence.
//
// B3 EVOLUTION
//   Adds lightweight momentum-cascade suppression:
//     - If repeated fade attempts on one side end in emergency exits,
//       and price continues auctioning directionally against that side,
//       temporarily disable that side.
//     - This prevents repeated dip-buying into real sell cascades
//       and repeated shorting into real buy cascades.
//
// ARCHITECTURE RULES
//   - Single-series only.
//   - Designed for tick-chart playback/live operation.
//   - Calculate.OnEachTick.
//   - No AddDataSeries().
//   - No L2 processing.
//   - No file telemetry.
//   - No heavy indicators.
//   - Managed SetStopLoss / SetProfitTarget protective brackets.
//   - One MNQ contract only.
//   - RTH morning window default: 08:32–11:00 CT.
//
// IMPORTANT NT8 NOTES
//   1. Apply this to an MNQ tick chart.
//   2. The chart/session time should match the intended CT parameters.
//   3. This strategy uses Time[0] only for session gating and coarse bar context;
//      trade-hold timing uses market-data timestamps where available.
//   4. Managed OCO brackets are submitted through SetStopLoss / SetProfitTarget.
//   5. Emergency and time/failure exits are advisory managed exits layered on top.
//
// FILE INSTALL
//   Documents\NinjaTrader 8\bin\Custom\Strategies\CG_T2_ClanMarshal_v9_2_LIGHT_B3_Cascade.cs
//
// DISCLAIMER
//   Research strategy. Validate in Playback/Sim before live use.

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_T2_ClanMarshal_v9_2_LIGHT_B3_Cascade : Strategy
    {
        // --------------------------------------------------------------------
        // Internal state
        // --------------------------------------------------------------------

        private double[] recentPrices;
        private int recentIndex;
        private int recentCount;

        private DateTime lastMarketDataTime = Core.Globals.MinDate;
        private DateTime entryMarketDataTime = Core.Globals.MinDate;
        private DateTime lastExitMarketDataTime = Core.Globals.MinDate;
        private DateTime longLockoutUntil = Core.Globals.MinDate;
        private DateTime shortLockoutUntil = Core.Globals.MinDate;

        private DateTime longCascadeSuppressUntil = Core.Globals.MinDate;
        private DateTime shortCascadeSuppressUntil = Core.Globals.MinDate;

        private double entryPrice = 0.0;
        private double highSinceEntry = 0.0;
        private double lowSinceEntry = 0.0;

        private double lastLongEmergencyLow = 0.0;
        private double lastShortEmergencyHigh = 0.0;

        private int tradesToday = 0;
        private int emergencyExitsToday = 0;
        private int longEmergencyCountWindow = 0;
        private int shortEmergencyCountWindow = 0;

        private DateTime lastLongEmergencyTime = Core.Globals.MinDate;
        private DateTime lastShortEmergencyTime = Core.Globals.MinDate;

        private DateTime currentSessionDate = Core.Globals.MinDate;

        private bool entryOrderSubmittedThisTick = false;

        private const string LongSignalName = "LONG";
        private const string ShortSignalName = "SHORT";

        // --------------------------------------------------------------------
        // User parameters
        // --------------------------------------------------------------------

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EntryStartTime", GroupName = "01 Session", Order = 1)]
        public int EntryStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EntryEndTime", GroupName = "01 Session", Order = 2)]
        public int EntryEndTime { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
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
        [Display(Name = "LongMinTicksAfterExtreme", GroupName = "03 Long", Order = 3)]
        public int LongMinTicksAfterExtreme { get; set; }

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
        [Display(Name = "ShortMinTicksAfterExtreme", GroupName = "04 Short", Order = 3)]
        public int ShortMinTicksAfterExtreme { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "ProfitTargetTicks", GroupName = "05 Exits", Order = 1)]
        public int ProfitTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "StopLossTicks", GroupName = "05 Exits", Order = 2)]
        public int StopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "EmergencyExitTicks", GroupName = "05 Exits", Order = 3)]
        public int EmergencyExitTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 600)]
        [Display(Name = "TimeStopSeconds", GroupName = "05 Exits", Order = 4)]
        public int TimeStopSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 300)]
        [Display(Name = "EarlyFailureSeconds", GroupName = "05 Exits", Order = 5)]
        public int EarlyFailureSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "EarlyFailureMinProgressTicks", GroupName = "05 Exits", Order = 6)]
        public int EarlyFailureMinProgressTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 600)]
        [Display(Name = "CooldownSeconds", GroupName = "06 Governance", Order = 1)]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxTradesPerDay", GroupName = "06 Governance", Order = 2)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxEmergencyExits", GroupName = "06 Governance", Order = 3)]
        public int MaxEmergencyExits { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "SideLockoutMinutes", GroupName = "06 Governance", Order = 4)]
        public int SideLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "CascadeEmergencyThreshold", GroupName = "07 Cascade Suppression", Order = 1)]
        public int CascadeEmergencyThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "CascadeWindowMinutes", GroupName = "07 Cascade Suppression", Order = 2)]
        public int CascadeWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "CascadeSuppressMinutes", GroupName = "07 Cascade Suppression", Order = 3)]
        public int CascadeSuppressMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "CascadeContinuationTicks", GroupName = "07 Cascade Suppression", Order = 4)]
        public int CascadeContinuationTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", GroupName = "08 Diagnostics", Order = 1)]
        public bool PrintDiagnostics { get; set; }

        // --------------------------------------------------------------------
        // NT8 lifecycle
        // --------------------------------------------------------------------

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_2_LIGHT_B3_Cascade";
                Description = "v9.2 LIGHT B3 single-series MNQ microstructure fade engine with momentum-cascade suppression.";

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

                // Session defaults: CT-style HHMMSS as specified in restart doc.
                EntryStartTime = 83200;
                EntryEndTime = 110000;

                // Impulse/reclaim/reject parameters.
                ImpulseLookbackTicks = 12;

                LongMinImpulseTicks = 7;
                LongReclaimTicks = 5;
                LongMinTicksAfterExtreme = 3;

                ShortMinImpulseTicks = 6;
                ShortRejectTicks = 5;
                ShortMinTicksAfterExtreme = 3;

                // Tight quick-trade brackets.
                ProfitTargetTicks = 12;
                StopLossTicks = 10;
                EmergencyExitTicks = 8;
                TimeStopSeconds = 45;
                EarlyFailureSeconds = 15;
                EarlyFailureMinProgressTicks = 3;

                // Governance.
                CooldownSeconds = 60;
                MaxTradesPerDay = 15;
                MaxEmergencyExits = 6;
                SideLockoutMinutes = 5;

                // B3 cascade suppression.
                CascadeEmergencyThreshold = 2;
                CascadeWindowMinutes = 12;
                CascadeSuppressMinutes = 8;
                CascadeContinuationTicks = 4;

                PrintDiagnostics = true;
            }
            else if (State == State.Configure)
            {
                // Managed OCO brackets. These are submitted by NT8 when an entry fills.
                SetProfitTarget(LongSignalName, CalculationMode.Ticks, ProfitTargetTicks);
                SetStopLoss(LongSignalName, CalculationMode.Ticks, StopLossTicks, false);

                SetProfitTarget(ShortSignalName, CalculationMode.Ticks, ProfitTargetTicks);
                SetStopLoss(ShortSignalName, CalculationMode.Ticks, StopLossTicks, false);
            }
            else if (State == State.DataLoaded)
            {
                int bufferSize = Math.Max(ImpulseLookbackTicks + 20, 64);
                recentPrices = new double[bufferSize];
                recentIndex = 0;
                recentCount = 0;
            }
        }

        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            if (marketDataUpdate == null)
                return;

            if (marketDataUpdate.MarketDataType != MarketDataType.Last)
                return;

            lastMarketDataTime = marketDataUpdate.Time;
            entryOrderSubmittedThisTick = false;

            double px = marketDataUpdate.Price;
            PushRecentPrice(px);

            ManageOpenPosition(px);

            if (Position.MarketPosition == MarketPosition.Flat)
                EvaluateEntries(px);
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1)
                return;

            ResetSessionIfNeeded();

            // Fallback path for historical/backtest contexts where OnMarketData may not drive.
            // In Playback/live tick charts, OnMarketData is the primary path.
            if (State == State.Historical)
            {
                lastMarketDataTime = Time[0];
                double px = Close[0];
                PushRecentPrice(px);
                ManageOpenPosition(px);

                if (Position.MarketPosition == MarketPosition.Flat)
                    EvaluateEntries(px);
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

            if (execution.Order.OrderState != OrderState.Filled)
                return;

            string orderName = execution.Order.Name ?? string.Empty;
            DateTime now = SafeNow(time);

            if (orderName == LongSignalName || orderName == ShortSignalName)
            {
                entryMarketDataTime = now;
                entryPrice = price;
                highSinceEntry = price;
                lowSinceEntry = price;
                tradesToday++;

                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ENTRY {1} @ {2:F2} tradesToday={3}",
                        now, orderName, price, tradesToday));

                return;
            }

            // Any filled non-entry order while returning flat is treated as an exit for cooldown purposes.
            if (marketPosition == MarketPosition.Flat)
            {
                lastExitMarketDataTime = now;

                if (PrintDiagnostics)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EXIT {1} @ {2:F2} cooldownUntil={3:HH:mm:ss}",
                        now, orderName, price, now.AddSeconds(CooldownSeconds)));
            }
        }

        // --------------------------------------------------------------------
        // Entry logic
        // --------------------------------------------------------------------

        private void EvaluateEntries(double px)
        {
            if (entryOrderSubmittedThisTick)
                return;

            DateTime now = SafeNow(Time[0]);

            if (!IsTradableSession(now))
                return;

            if (tradesToday >= MaxTradesPerDay)
                return;

            if (emergencyExitsToday >= MaxEmergencyExits)
                return;

            if (lastExitMarketDataTime != Core.Globals.MinDate &&
                now < lastExitMarketDataTime.AddSeconds(CooldownSeconds))
                return;

            if (recentCount < Math.Max(ImpulseLookbackTicks, 4))
                return;

            if (CanEnterLong(now) && IsLongFadeSetup(px))
            {
                EnterLong(1, LongSignalName);
                entryOrderSubmittedThisTick = true;
                return;
            }

            if (CanEnterShort(now) && IsShortFadeSetup(px))
            {
                EnterShort(1, ShortSignalName);
                entryOrderSubmittedThisTick = true;
                return;
            }
        }

        private bool CanEnterLong(DateTime now)
        {
            if (now < longLockoutUntil)
                return false;

            if (now < longCascadeSuppressUntil)
                return false;

            return true;
        }

        private bool CanEnterShort(DateTime now)
        {
            if (now < shortLockoutUntil)
                return false;

            if (now < shortCascadeSuppressUntil)
                return false;

            return true;
        }

        private bool IsLongFadeSetup(double px)
        {
            double lookbackHigh = GetRecentHigh();
            double lookbackLow = GetRecentLow();

            double downImpulseTicks = ToTicks(lookbackHigh - lookbackLow);
            double reclaimTicks = ToTicks(px - lookbackLow);
            double ticksAfterExtreme = TicksSinceExtreme(false);

            return downImpulseTicks >= LongMinImpulseTicks
                && reclaimTicks >= LongReclaimTicks
                && ticksAfterExtreme >= LongMinTicksAfterExtreme;
        }

        private bool IsShortFadeSetup(double px)
        {
            double lookbackHigh = GetRecentHigh();
            double lookbackLow = GetRecentLow();

            double upImpulseTicks = ToTicks(lookbackHigh - lookbackLow);
            double rejectTicks = ToTicks(lookbackHigh - px);
            double ticksAfterExtreme = TicksSinceExtreme(true);

            return upImpulseTicks >= ShortMinImpulseTicks
                && rejectTicks >= ShortRejectTicks
                && ticksAfterExtreme >= ShortMinTicksAfterExtreme;
        }

        // --------------------------------------------------------------------
        // Open-position management
        // --------------------------------------------------------------------

        private void ManageOpenPosition(double px)
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            DateTime now = SafeNow(Time[0]);

            highSinceEntry = Math.Max(highSinceEntry, px);
            lowSinceEntry = Math.Min(lowSinceEntry, px);

            double adverseTicks = 0.0;
            double favorableTicks = 0.0;

            if (Position.MarketPosition == MarketPosition.Long)
            {
                adverseTicks = ToTicks(entryPrice - px);
                favorableTicks = ToTicks(highSinceEntry - entryPrice);

                if (adverseTicks >= EmergencyExitTicks)
                {
                    RegisterEmergencyExit(true, now, px);
                    ExitLong("Emergency_Long", LongSignalName);
                    return;
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                adverseTicks = ToTicks(px - entryPrice);
                favorableTicks = ToTicks(entryPrice - lowSinceEntry);

                if (adverseTicks >= EmergencyExitTicks)
                {
                    RegisterEmergencyExit(false, now, px);
                    ExitShort("Emergency_Short", ShortSignalName);
                    return;
                }
            }

            double heldSeconds = 0.0;
            if (entryMarketDataTime != Core.Globals.MinDate)
                heldSeconds = Math.Max(0.0, (now - entryMarketDataTime).TotalSeconds);

            if (heldSeconds >= TimeStopSeconds)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("TimeStop_Long", LongSignalName);
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("TimeStop_Short", ShortSignalName);

                return;
            }

            if (heldSeconds >= EarlyFailureSeconds && favorableTicks < EarlyFailureMinProgressTicks)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("EarlyFailure_Long", LongSignalName);
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("EarlyFailure_Short", ShortSignalName);

                return;
            }
        }

        private void RegisterEmergencyExit(bool wasLong, DateTime now, double px)
        {
            emergencyExitsToday++;
            lastExitMarketDataTime = now;

            if (wasLong)
            {
                longLockoutUntil = now.AddMinutes(SideLockoutMinutes);

                if (lastLongEmergencyTime == Core.Globals.MinDate ||
                    now > lastLongEmergencyTime.AddMinutes(CascadeWindowMinutes))
                {
                    longEmergencyCountWindow = 1;
                }
                else
                {
                    longEmergencyCountWindow++;
                }

                lastLongEmergencyTime = now;
                lastLongEmergencyLow = px;

                if (ShouldSuppressLongCascade(px, now))
                {
                    longCascadeSuppressUntil = now.AddMinutes(CascadeSuppressMinutes);

                    if (PrintDiagnostics)
                        Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} LONG CASCADE SUPPRESSION until {1:HH:mm:ss} count={2} px={3:F2}",
                            now, longCascadeSuppressUntil, longEmergencyCountWindow, px));
                }
            }
            else
            {
                shortLockoutUntil = now.AddMinutes(SideLockoutMinutes);

                if (lastShortEmergencyTime == Core.Globals.MinDate ||
                    now > lastShortEmergencyTime.AddMinutes(CascadeWindowMinutes))
                {
                    shortEmergencyCountWindow = 1;
                }
                else
                {
                    shortEmergencyCountWindow++;
                }

                lastShortEmergencyTime = now;
                lastShortEmergencyHigh = px;

                if (ShouldSuppressShortCascade(px, now))
                {
                    shortCascadeSuppressUntil = now.AddMinutes(CascadeSuppressMinutes);

                    if (PrintDiagnostics)
                        Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} SHORT CASCADE SUPPRESSION until {1:HH:mm:ss} count={2} px={3:F2}",
                            now, shortCascadeSuppressUntil, shortEmergencyCountWindow, px));
                }
            }

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} EMERGENCY {1} px={2:F2} emergencyToday={3}",
                    now, wasLong ? "LONG" : "SHORT", px, emergencyExitsToday));
        }

        private bool ShouldSuppressLongCascade(double px, DateTime now)
        {
            if (longEmergencyCountWindow < CascadeEmergencyThreshold)
                return false;

            // A long emergency means the strategy bought a dip failure.
            // Suppress further longs if the auction continues making lower lows.
            double continuationTicks = ToTicks(lastLongEmergencyLow - px);
            if (continuationTicks >= CascadeContinuationTicks)
                return true;

            // Also suppress if current px is at/near the recent low after repeated long failures.
            double recentLow = GetRecentLow();
            if (ToTicks(px - recentLow) <= 1)
                return true;

            return false;
        }

        private bool ShouldSuppressShortCascade(double px, DateTime now)
        {
            if (shortEmergencyCountWindow < CascadeEmergencyThreshold)
                return false;

            // A short emergency means the strategy shorted into a buy cascade.
            // Suppress further shorts if the auction continues making higher highs.
            double continuationTicks = ToTicks(px - lastShortEmergencyHigh);
            if (continuationTicks >= CascadeContinuationTicks)
                return true;

            // Also suppress if current px is at/near the recent high after repeated short failures.
            double recentHigh = GetRecentHigh();
            if (ToTicks(recentHigh - px) <= 1)
                return true;

            return false;
        }

        // --------------------------------------------------------------------
        // Rolling price buffer
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
            if (recentCount <= 0)
                return Close[0];

            int n = Math.Min(ImpulseLookbackTicks, recentCount);
            double hi = double.MinValue;

            for (int i = 0; i < n; i++)
            {
                double v = GetRecentPriceByAge(i);
                if (v > hi)
                    hi = v;
            }

            return hi;
        }

        private double GetRecentLow()
        {
            if (recentCount <= 0)
                return Close[0];

            int n = Math.Min(ImpulseLookbackTicks, recentCount);
            double lo = double.MaxValue;

            for (int i = 0; i < n; i++)
            {
                double v = GetRecentPriceByAge(i);
                if (v < lo)
                    lo = v;
            }

            return lo;
        }

        private int TicksSinceExtreme(bool highExtreme)
        {
            if (recentCount <= 0)
                return 0;

            int n = Math.Min(ImpulseLookbackTicks, recentCount);
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

            // Age 0 is most recent. If extremeAge is 3, then 3 ticks have occurred since extreme.
            return extremeAge;
        }

        private double GetRecentPriceByAge(int age)
        {
            // age 0 = most recent pushed price.
            int idx = recentIndex - 1 - age;
            while (idx < 0)
                idx += recentPrices.Length;

            return recentPrices[idx % recentPrices.Length];
        }

        // --------------------------------------------------------------------
        // Utilities
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

            currentSessionDate = d;

            tradesToday = 0;
            emergencyExitsToday = 0;

            longEmergencyCountWindow = 0;
            shortEmergencyCountWindow = 0;

            lastLongEmergencyTime = Core.Globals.MinDate;
            lastShortEmergencyTime = Core.Globals.MinDate;

            longLockoutUntil = Core.Globals.MinDate;
            shortLockoutUntil = Core.Globals.MinDate;
            longCascadeSuppressUntil = Core.Globals.MinDate;
            shortCascadeSuppressUntil = Core.Globals.MinDate;

            lastExitMarketDataTime = Core.Globals.MinDate;
            entryMarketDataTime = Core.Globals.MinDate;

            if (PrintDiagnostics)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} NEW SESSION reset governance counters", Time[0]));
        }

        private bool IsTradableSession(DateTime now)
        {
            int t = ToTime(now);
            return t >= EntryStartTime && t <= EntryEndTime;
        }

        private DateTime SafeNow(DateTime fallback)
        {
            if (lastMarketDataTime != Core.Globals.MinDate)
                return lastMarketDataTime;

            if (fallback != Core.Globals.MinDate)
                return fallback;

            return Time[0];
        }

        private int ToTicks(double points)
        {
            if (TickSize <= 0)
                return 0;

            return (int)Math.Round(points / TickSize, MidpointRounding.AwayFromZero);
        }
    }
}
