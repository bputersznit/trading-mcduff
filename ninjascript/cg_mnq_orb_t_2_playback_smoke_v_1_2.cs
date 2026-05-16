// CG_MNQ_ORB_T2_PlaybackSmoke_v1_2.cs
// NinjaTrader 8 Strategy
// v1.2 upgrades:
// - Breakout leg expiration timer
// - Stronger OR boundary reclaim rules
// - One trade per breakout leg default
// - Adaptive stop sizing based on OR width
// - Midday suppression window
// - Improved retest quality filters
//
// This file is an upgrade framework from v1.1 for NT8 playback testing.
// Core architecture and methodology comments preserved for iterative development.

#region Using declarations
using System;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_MNQ_ORB_T2_PlaybackSmoke_v1_2 : Strategy
    {
        // ============================================================
        // STRATEGIC CONFIG
        // ============================================================
        private double orHigh;
        private double orLow;
        private double orWidth;
        private double orVwap;
        private bool orComplete;

        private bool breakoutLongArmed;
        private bool breakoutShortArmed;
        private int breakoutBarsElapsed;
        private bool breakoutLegTraded;

        private int openingRangeMinutes = 15;
        private int breakoutExpirationBars = 6;
        private int cooldownBars = 3;
        private int maxTradesPerSession = 2;
        private int tradeCountSession = 0;

        private int stopTicksBase = 20;
        private int targetTicksBase = 40;

        private int lastExitBar = -999;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_MNQ_ORB_T2_PlaybackSmoke_v1_2";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
            }
            else if (State == State.Configure)
            {
                SetStopLoss(CalculationMode.Ticks, stopTicksBase);
                SetProfitTarget(CalculationMode.Ticks, targetTicksBase);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 30)
                return;

            // ========================================================
            // SESSION RESET
            // ========================================================
            if (Bars.IsFirstBarOfSession)
            {
                ResetSession();
            }

            // ========================================================
            // MIDDAY SUPPRESSION
            // ========================================================
            int time = ToTime(Time[0]);
            if (time >= 113000 && time <= 133000)
                return;

            // ========================================================
            // BUILD OPENING RANGE
            // ========================================================
            if (!orComplete)
            {
                BuildOpeningRange();
                return;
            }

            // ========================================================
            // COOLDOWN
            // ========================================================
            if (CurrentBar - lastExitBar < cooldownBars)
                return;

            if (tradeCountSession >= maxTradesPerSession)
                return;

            // ========================================================
            // BREAKOUT DETECTION
            // ========================================================
            DetectBreakouts();

            // ========================================================
            // BREAKOUT EXPIRATION
            // ========================================================
            if ((breakoutLongArmed || breakoutShortArmed) && breakoutBarsElapsed > breakoutExpirationBars)
            {
                ResetBreakoutLeg();
            }

            breakoutBarsElapsed++;

            // ========================================================
            // ENTRY LOGIC
            // ========================================================
            if (!breakoutLegTraded)
            {
                if (breakoutLongArmed && ValidLongRetest())
                {
                    EnterLong("Smoke_Long");
                    breakoutLegTraded = true;
                }
                else if (breakoutShortArmed && ValidShortRetest())
                {
                    EnterShort("Smoke_Short");
                    breakoutLegTraded = true;
                }
            }
        }

        // ============================================================
        // OPENING RANGE LOGIC
        // ============================================================
        private void BuildOpeningRange()
        {
            if (CurrentBar < openingRangeMinutes)
                return;

            orHigh = MAX(High, openingRangeMinutes)[0];
            orLow = MIN(Low, openingRangeMinutes)[0];
            orWidth = orHigh - orLow;
            orVwap = (orHigh + orLow) / 2.0;
            orComplete = true;

            // Adaptive stop sizing
            int adaptiveStop = Math.Max(stopTicksBase, (int)(orWidth * 0.25));
            SetStopLoss(CalculationMode.Ticks, adaptiveStop);
        }

        // ============================================================
        // BREAKOUT ARMING
        // ============================================================
        private void DetectBreakouts()
        {
            if (Close[0] > orHigh + 2)
            {
                breakoutLongArmed = true;
                breakoutShortArmed = false;
                breakoutBarsElapsed = 0;
            }
            else if (Close[0] < orLow - 2)
            {
                breakoutShortArmed = true;
                breakoutLongArmed = false;
                breakoutBarsElapsed = 0;
            }
        }

        // ============================================================
        // RETEST VALIDATION
        // ============================================================
        private bool ValidLongRetest()
        {
            bool taggedBoundary = Low[0] <= orHigh + 1;
            bool reclaimedBoundary = Close[0] > orHigh;
            bool bullishCandle = Close[0] > Open[0];
            bool aboveVwap = Close[0] > orVwap;

            return taggedBoundary && reclaimedBoundary && bullishCandle && aboveVwap;
        }

        private bool ValidShortRetest()
        {
            bool taggedBoundary = High[0] >= orLow - 1;
            bool reclaimedBoundary = Close[0] < orLow;
            bool bearishCandle = Close[0] < Open[0];
            bool belowVwap = Close[0] < orVwap;

            return taggedBoundary && reclaimedBoundary && bearishCandle && belowVwap;
        }

        // ============================================================
        // SESSION UTILITIES
        // ============================================================
        private void ResetSession()
        {
            orComplete = false;
            breakoutLongArmed = false;
            breakoutShortArmed = false;
            breakoutLegTraded = false;
            breakoutBarsElapsed = 0;
            tradeCountSession = 0;
        }

        private void ResetBreakoutLeg()
        {
            breakoutLongArmed = false;
            breakoutShortArmed = false;
            breakoutBarsElapsed = 0;
        }

        // ============================================================
        // EXECUTION TRACKING
        // ============================================================
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
            int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            if (execution.Order.OrderState == OrderState.Filled)
            {
                if (execution.Order.Name.Contains("Profit") || execution.Order.Name.Contains("Stop") || execution.Order.Name.Contains("Exit"))
                {
                    lastExitBar = CurrentBar;
                    tradeCountSession++;
                }
            }
        }
    }
}
