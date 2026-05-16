// CG_MNQ_ORB_T2_PlaybackSmoke_v1_3.cs
// NinjaTrader 8 Strategy
// v1.3 upgrades:
// - Layer 0 Premarket Structural Bias Engine
// - Overnight high/low and premarket VWAP
// - Triple-top / triple-bottom style repeated test detection
// - Premarket exhaustion / failed breakout bias weighting
// - ORB + T2 execution only in direction of approved macro bias unless override
// - Adaptive stops, cooldown, breakout expiration, midday suppression preserved
//
// Purpose:
//   Bridge from simple ORB breakout model toward institutional session-structure model
//   while remaining light enough for NT8 playback validation.

#region Using declarations
using System;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_MNQ_ORB_T2_PlaybackSmoke_v1_3 : Strategy
    {
        private enum PremarketBias
        {
            Neutral,
            Bullish,
            Bearish,
            ExhaustionLong,
            ExhaustionShort
        }

        private PremarketBias premarketBias = PremarketBias.Neutral;

        // Session / ORB
        private double orHigh;
        private double orLow;
        private double orWidth;
        private double orVwap;
        private bool orComplete;

        // Premarket
        private double preHigh;
        private double preLow;
        private double preVwap;
        private double preVol;
        private double preVwapNum;
        private int preHighTests;
        private int preLowTests;
        private bool premarketComplete;

        // Breakout state
        private bool breakoutLongArmed;
        private bool breakoutShortArmed;
        private bool breakoutLegTraded;
        private int breakoutBarsElapsed;

        // Controls
        private int openingRangeBars = 8; // 2m chart ≈ 16 min
        private int breakoutExpirationBars = 6;
        private int cooldownBars = 3;
        private int maxTradesPerSession = 2;
        private int tradeCountSession = 0;
        private int lastExitBar = -999;

        private int baseStopTicks = 20;
        private int baseTargetTicks = 40;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_MNQ_ORB_T2_PlaybackSmoke_v1_3";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
            }
            else if (State == State.Configure)
            {
                SetStopLoss(CalculationMode.Ticks, baseStopTicks);
                SetProfitTarget(CalculationMode.Ticks, baseTargetTicks);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 50)
                return;

            int time = ToTime(Time[0]);

            if (Bars.IsFirstBarOfSession)
                ResetSession();

            // --------------------------------------------------------
            // PREMARKET BUILD (roughly 06:00–09:29 ET)
            // --------------------------------------------------------
            if (time >= 60000 && time < 93000)
            {
                BuildPremarket();
                return;
            }

            if (!premarketComplete && time >= 93000)
                FinalizePremarket();

            // --------------------------------------------------------
            // MIDDAY SUPPRESSION
            // --------------------------------------------------------
            if (time >= 113000 && time <= 133000)
                return;

            // --------------------------------------------------------
            // OR BUILD
            // --------------------------------------------------------
            if (!orComplete)
            {
                BuildOpeningRange();
                return;
            }

            // --------------------------------------------------------
            // Cooldown / trade caps
            // --------------------------------------------------------
            if (CurrentBar - lastExitBar < cooldownBars)
                return;

            if (tradeCountSession >= maxTradesPerSession)
                return;

            // --------------------------------------------------------
            // Breakout logic
            // --------------------------------------------------------
            DetectBreakouts();

            if ((breakoutLongArmed || breakoutShortArmed) && breakoutBarsElapsed > breakoutExpirationBars)
                ResetBreakoutLeg();

            breakoutBarsElapsed++;

            // --------------------------------------------------------
            // Entries gated by Layer 0 bias
            // --------------------------------------------------------
            if (!breakoutLegTraded)
            {
                if (breakoutLongArmed && ValidLongRetest() && AllowLongByPremarketBias())
                {
                    EnterLong("Smoke_Long");
                    breakoutLegTraded = true;
                }
                else if (breakoutShortArmed && ValidShortRetest() && AllowShortByPremarketBias())
                {
                    EnterShort("Smoke_Short");
                    breakoutLegTraded = true;
                }
            }
        }

        // ============================================================
        // LAYER 0 PREMARKET
        // ============================================================
        private void BuildPremarket()
        {
            if (!premarketComplete)
            {
                if (preHigh == 0 || High[0] > preHigh)
                {
                    if (Math.Abs(High[0] - preHigh) <= 4) preHighTests++;
                    preHigh = High[0];
                }

                if (preLow == 0 || Low[0] < preLow)
                {
                    if (Math.Abs(Low[0] - preLow) <= 4) preLowTests++;
                    preLow = Low[0];
                }

                preVwapNum += Close[0] * Volume[0];
                preVol += Volume[0];
            }
        }

        private void FinalizePremarket()
        {
            premarketComplete = true;
            preVwap = preVol > 0 ? preVwapNum / preVol : Close[0];

            double preRange = preHigh - preLow;
            double current = Close[0];

            // Triple-top / exhaustion short
            if (preHighTests >= 2 && current < preHigh - 15)
                premarketBias = PremarketBias.ExhaustionShort;

            // Triple-bottom / exhaustion long
            else if (preLowTests >= 2 && current > preLow + 15)
                premarketBias = PremarketBias.ExhaustionLong;

            else if (current > preVwap)
                premarketBias = PremarketBias.Bullish;

            else if (current < preVwap)
                premarketBias = PremarketBias.Bearish;

            else
                premarketBias = PremarketBias.Neutral;
        }

        private bool AllowLongByPremarketBias()
        {
            return premarketBias == PremarketBias.Bullish ||
                   premarketBias == PremarketBias.ExhaustionLong ||
                   premarketBias == PremarketBias.Neutral;
        }

        private bool AllowShortByPremarketBias()
        {
            return premarketBias == PremarketBias.Bearish ||
                   premarketBias == PremarketBias.ExhaustionShort ||
                   premarketBias == PremarketBias.Neutral;
        }

        // ============================================================
        // ORB
        // ============================================================
        private void BuildOpeningRange()
        {
            if (CurrentBar < openingRangeBars)
                return;

            orHigh = MAX(High, openingRangeBars)[0];
            orLow = MIN(Low, openingRangeBars)[0];
            orWidth = orHigh - orLow;
            orVwap = (orHigh + orLow) / 2.0;
            orComplete = true;

            int adaptiveStop = Math.Max(baseStopTicks, (int)(orWidth * 0.25));
            SetStopLoss(CalculationMode.Ticks, adaptiveStop);
        }

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
        // RETESTS
        // ============================================================
        private bool ValidLongRetest()
        {
            return Low[0] <= orHigh + 1 &&
                   Close[0] > orHigh &&
                   Close[0] > Open[0] &&
                   Close[0] > orVwap;
        }

        private bool ValidShortRetest()
        {
            return High[0] >= orLow - 1 &&
                   Close[0] < orLow &&
                   Close[0] < Open[0] &&
                   Close[0] < orVwap;
        }

        // ============================================================
        // UTILITIES
        // ============================================================
        private void ResetSession()
        {
            orComplete = false;
            premarketComplete = false;

            orHigh = 0;
            orLow = 0;
            orWidth = 0;
            orVwap = 0;

            preHigh = 0;
            preLow = 0;
            preVwap = 0;
            preVol = 0;
            preVwapNum = 0;
            preHighTests = 0;
            preLowTests = 0;
            premarketBias = PremarketBias.Neutral;

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
