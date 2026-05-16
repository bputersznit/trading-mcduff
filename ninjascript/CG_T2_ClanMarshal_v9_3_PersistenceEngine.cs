// CG_T2_ClanMarshal_v9_3_PersistenceEngine.cs
// Generated: 2026-05-06
//
// MNQ LIGHT HYBRID PERSISTENCE ENGINE
// ----------------------------------
// Key changes:
// - Persistent auction-state model
// - Sticky hysteresis transitions
// - No emergency lockout
// - One MNQ only
// - OCO-style protective brackets
// - Pullback/reclaim continuation logic
//
// NOTE:
// This is a strategic skeleton/reference implementation intended
// to replace the unstable regime-thrashing behavior observed in B6.2.

using System;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_T2_ClanMarshal_v9_3_PersistenceEngine : Strategy
    {
        private enum AuctionState
        {
            None,
            BuildingUp,
            ConfirmedUp,
            ExhaustingUp,
            BuildingDown,
            ConfirmedDown,
            ExhaustingDown,
            Chop
        }

        private AuctionState state = AuctionState.None;

        private double auctionEnergy = 0;
        private double exhaustion = 0;

        private double lastPrice = 0;
        private double sessionHigh = double.MinValue;
        private double sessionLow  = double.MaxValue;

        private int pullbackCounter = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_T2_ClanMarshal_v9_3_PersistenceEngine";

                Calculate = Calculate.OnEachTick;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;

                TraceOrders = false;
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 20)
                return;

            double px = Close[0];

            if (px > sessionHigh)
                sessionHigh = px;

            if (px < sessionLow)
                sessionLow = px;

            double delta = px - lastPrice;
            lastPrice = px;

            // ---------------------------------------------------
            // AUCTION ENERGY MODEL
            // ---------------------------------------------------

            if (delta > 0)
                auctionEnergy += 1.25;
            else if (delta < 0)
                auctionEnergy -= 1.25;

            auctionEnergy *= 0.985;

            // ---------------------------------------------------
            // EXHAUSTION MODEL
            // ---------------------------------------------------

            if (Math.Abs(delta) > 4 * TickSize)
                exhaustion += 0.5;
            else
                exhaustion *= 0.96;

            // ---------------------------------------------------
            // STICKY HYSTERESIS STATE MACHINE
            // ---------------------------------------------------

            switch (state)
            {
                case AuctionState.None:

                    if (auctionEnergy > 20)
                        state = AuctionState.BuildingUp;

                    if (auctionEnergy < -20)
                        state = AuctionState.BuildingDown;

                    break;

                case AuctionState.BuildingUp:

                    if (auctionEnergy > 35)
                        state = AuctionState.ConfirmedUp;

                    if (auctionEnergy < 5)
                        state = AuctionState.None;

                    break;

                case AuctionState.ConfirmedUp:

                    if (exhaustion > 12)
                        state = AuctionState.ExhaustingUp;

                    if (auctionEnergy < -10)
                        state = AuctionState.None;

                    break;

                case AuctionState.ExhaustingUp:

                    if (auctionEnergy < -15)
                        state = AuctionState.BuildingDown;

                    if (auctionEnergy > 10)
                        state = AuctionState.ConfirmedUp;

                    break;

                case AuctionState.BuildingDown:

                    if (auctionEnergy < -35)
                        state = AuctionState.ConfirmedDown;

                    if (auctionEnergy > -5)
                        state = AuctionState.None;

                    break;

                case AuctionState.ConfirmedDown:

                    if (exhaustion > 12)
                        state = AuctionState.ExhaustingDown;

                    if (auctionEnergy > 10)
                        state = AuctionState.None;

                    break;

                case AuctionState.ExhaustingDown:

                    if (auctionEnergy > 15)
                        state = AuctionState.BuildingUp;

                    if (auctionEnergy < -10)
                        state = AuctionState.ConfirmedDown;

                    break;
            }

            // ---------------------------------------------------
            // PULLBACK DETECTION
            // ---------------------------------------------------

            if (state == AuctionState.ConfirmedUp)
            {
                if (delta < 0)
                    pullbackCounter++;
                else if (delta > 0 && pullbackCounter >= 3)
                {
                    if (Position.MarketPosition == MarketPosition.Flat)
                    {
                        EnterLong(1, "TrendLong");

                        SetStopLoss("TrendLong", CalculationMode.Ticks, 20, false);
                        SetProfitTarget("TrendLong", CalculationMode.Ticks, 40);
                    }

                    pullbackCounter = 0;
                }
            }

            if (state == AuctionState.ConfirmedDown)
            {
                if (delta > 0)
                    pullbackCounter++;
                else if (delta < 0 && pullbackCounter >= 3)
                {
                    if (Position.MarketPosition == MarketPosition.Flat)
                    {
                        EnterShort(1, "TrendShort");

                        SetStopLoss("TrendShort", CalculationMode.Ticks, 20, false);
                        SetProfitTarget("TrendShort", CalculationMode.Ticks, 40);
                    }

                    pullbackCounter = 0;
                }
            }

            // ---------------------------------------------------
            // FADE LOGIC ONLY IN NEUTRAL
            // ---------------------------------------------------

            if (state == AuctionState.None)
            {
                if (px <= sessionLow + (6 * TickSize))
                {
                    if (Position.MarketPosition == MarketPosition.Flat)
                    {
                        EnterLong(1, "FadeLong");

                        SetStopLoss("FadeLong", CalculationMode.Ticks, 12, false);
                        SetProfitTarget("FadeLong", CalculationMode.Ticks, 10);
                    }
                }

                if (px >= sessionHigh - (6 * TickSize))
                {
                    if (Position.MarketPosition == MarketPosition.Flat)
                    {
                        EnterShort(1, "FadeShort");

                        SetStopLoss("FadeShort", CalculationMode.Ticks, 12, false);
                        SetProfitTarget("FadeShort", CalculationMode.Ticks, 10);
                    }
                }
            }

            // ---------------------------------------------------
            // DIAGNOSTICS
            // ---------------------------------------------------

            if (CurrentBar % 100 == 0)
            {
                Print(string.Format(
                    "{0} STATE={1} energy={2:F2} exhaustion={3:F2} px={4:F2}",
                    Time[0],
                    state,
                    auctionEnergy,
                    exhaustion,
                    px
                ));
            }
        }
    }
}
