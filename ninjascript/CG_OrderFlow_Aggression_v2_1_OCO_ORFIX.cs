#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// =================================================================================================
// File: CG_OrderFlow_Aggression_v2_1_OCO_ORFIX.cs
// Generated: 2026-05-15 11:00:00 America/New_York
// Project: Bookmap Emulation / MNQ Microstructure Strategy Lab
// Instrument intent: MNQ, single contract, RTH-only, no overlapping positions.
//
// Strategy methodology:
// - Uses actual trade events from OnMarketData(MarketDataType.Last), not passive depth updates, as the
//   primary order-flow aggression source.
// - Classifies aggressive buys/sells using current best ask/bid when fresh, with corrected prior-trade
//   fallback classification when executions are not exactly at bid/ask.
// - Aggregates execution aggression across 100ms, 1s, and 5s windows.
// - Requires configurable 1s/5s confirmation using both delta and imbalance thresholds, not just sign.
// - Builds the Opening Range from an internally added 1-minute series, so OR logic no longer depends on
//   the hosting chart's bar type.
// - Uses managed SetStopLoss/SetProfitTarget brackets tied to entry signal names so NT submits linked
//   protective orders after entry fill. OnOrderUpdate records order state for diagnostics.
// - Tracks actual entry fill price from execution events, then computes realized PnL from actual fill prices.
// - Uses event/market time for timeout and cooldown logic; does not use DateTime.Now.
// - Enforces spread and quote-freshness checks to avoid stale/crossed-book entries.
//
// Deployment notes:
// - Attach to an MNQ chart. A 1-tick chart is preferred for playback fidelity, but OR is internal 1-minute.
// - Requires playback/live data feed that supplies Last trades and best bid/ask/depth events.
// - Keep playback speed reasonable; if trade/depth diagnostics show starvation, reduce playback speed.
// - This is still research-grade. Validate in Playback101 before Sim101/live.
// =================================================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_OrderFlow_Aggression_v2_1_OCO_ORFIX : Strategy
    {
        #region Variables

        // Bucket timing
        private DateTime bucket100msStart;
        private DateTime bucket1sStart;
        private DateTime bucket5sStart;

        // Execution aggression volumes
        private long aggBuyVol100ms;
        private long aggSellVol100ms;
        private long aggBuyVol1s;
        private long aggSellVol1s;
        private long aggBuyVol5s;
        private long aggSellVol5s;

        // Book pull detection state: track level size by side/price so Update is treated as delta, not full add.
        private Dictionary<double, long> bidBook;
        private Dictionary<double, long> askBook;
        private long bidAddVol100ms;
        private long askAddVol100ms;
        private long bidRemoveVol100ms;
        private long askRemoveVol100ms;

        // Current quote/trade state
        private double currentBestBid;
        private double currentBestAsk;
        private DateTime lastBidUpdateTime;
        private DateTime lastAskUpdateTime;
        private double lastTradePrice;
        private bool havePriorTradePrice;
        private DateTime currentMarketTime;

        // Signal state
        private string lastSignal;

        // Opening Range from internal 1-minute series
        private double orHigh;
        private double orLow;
        private bool orCalculated;
        private DateTime orTradeDate;

        // Daily governance
        private double dailyPnL;
        private int consecutiveLosses;
        private double dailyPeakPnL;
        private DateTime lastTradeDate;
        private bool dailyLimitHit;

        // Position/trade tracking
        private DateTime entryTime;
        private double actualEntryPrice;
        private int actualEntryQuantity;
        private MarketPosition entryDirection;
        private int tradesCountToday;
        private DateTime lastTradeExitTime;
        private string lastExitKind;

        // Managed bracket/order diagnostics
        private Order longEntryOrder;
        private Order shortEntryOrder;
        private Order targetOrder;
        private Order stopOrder;
        private bool protectiveOrdersSeenForCurrentTrade;

        // Diagnostics
        private int depthEventCount;
        private int tradeEventCount;
        private DateTime lastDiagnosticPrintTime;

        private const string LONG_SIGNAL = "OFI_Long";
        private const string SHORT_SIGNAL = "OFI_Short";
        private const string LONG_TARGET_SIGNAL = "OFI_Long_Target";
        private const string SHORT_TARGET_SIGNAL = "OFI_Short_Target";
        private const string LONG_STOP_SIGNAL = "OFI_Long_Stop";
        private const string SHORT_STOP_SIGNAL = "OFI_Short_Stop";
        private const string TIMEOUT_EXIT_SIGNAL = "OFI_Timeout";

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Order Flow Aggression v2.1 - execution aggression, internal OR, actual-fill PnL, quote freshness, managed OCO bracket diagnostics";
                Name = "CG_OrderFlow_Aggression_v2_1_OCO_ORFIX";

                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 3;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 20;

                // Signal parameters
                BucketSizeMs = 100;
                MinAggressionDelta = 50;
                MinAggressionImbalance = 0.60;
                Require1sConfirmation = true;
                Min1sAggressionDelta = 50;
                Min1sAggressionImbalance = 0.20;
                Require5sConfirmation = false;
                Min5sAggressionDelta = 75;
                Min5sAggressionImbalance = 0.10;

                // Risk parameters
                TargetTicks = 40;
                StopTicks = 20;
                TimeoutMinutes = 10;

                // Execution hygiene
                MaxSpreadTicks = 3;
                MaxQuoteAgeMs = 500;
                CooldownSeconds = 30;
                PostStopCooldownSeconds = 60;
                PostTimeoutCooldownSeconds = 30;

                // Daily limits. Keep disabled for broad playback testing, enable for governance tests.
                MaxDailyLoss = 500;
                MaxConsecutiveLosses = 10;
                ProfitLockPeak = 10000;
                ProfitLockDrawdown = 2000;
                EnableDailyLimits = false;

                // Filters
                EnableOpeningRangeFilter = true;
                EnableManipulationFilters = true;
                EnableSpreadFilter = true;
                EnableQuoteFreshnessFilter = true;
                EnableCooldown = true;
                EnableBookPullDetection = false;
                BookPullCancelThreshold = 100;

                EnableVerboseLogging = false;
                EnablePeriodicDiagnostics = true;
                DiagnosticIntervalSeconds = 60;

                // RTH hours in ET
                RTHStartHour = 9;
                RTHStartMinute = 30;
                RTHEndHour = 16;
                RTHEndMinute = 0;
            }
            else if (State == State.Configure)
            {
                // Internal 1-minute series used only for chart-independent Opening Range calculation.
                AddDataSeries(BarsPeriodType.Minute, 1);

                // Managed bracket setup. NT ties protective orders to the entry signal names.
                SetProfitTarget(LONG_SIGNAL, CalculationMode.Ticks, TargetTicks);
                SetStopLoss(LONG_SIGNAL, CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget(SHORT_SIGNAL, CalculationMode.Ticks, TargetTicks);
                SetStopLoss(SHORT_SIGNAL, CalculationMode.Ticks, StopTicks, false);
            }
            else if (State == State.DataLoaded)
            {
                bucket100msStart = DateTime.MinValue;
                bucket1sStart = DateTime.MinValue;
                bucket5sStart = DateTime.MinValue;

                currentMarketTime = DateTime.MinValue;
                lastBidUpdateTime = DateTime.MinValue;
                lastAskUpdateTime = DateTime.MinValue;
                lastTradeExitTime = DateTime.MinValue;
                lastDiagnosticPrintTime = DateTime.MinValue;

                lastSignal = "NONE";
                lastExitKind = "NONE";
                entryDirection = MarketPosition.Flat;

                bidBook = new Dictionary<double, long>();
                askBook = new Dictionary<double, long>();

                ResetAllBuckets();
            }
        }

        #endregion

        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                UpdateOpeningRangeFromOneMinuteSeries();
                return;
            }

            if (BarsInProgress != 0)
                return;

            if (CurrentBars[0] < BarsRequiredToTrade)
                return;

            if (currentMarketTime == DateTime.MinValue)
                currentMarketTime = Time[0];

            DateTime tradeDateEt = ToEasternTime(Time[0]).Date;
            if (tradeDateEt != lastTradeDate)
            {
                ResetDailyTracking(tradeDateEt);
                lastTradeDate = tradeDateEt;
            }

            if (EnableDailyLimits && dailyLimitHit)
                return;

            if (Position.MarketPosition != MarketPosition.Flat)
                CheckTimeout();

            if (EnablePeriodicDiagnostics)
                MaybePrintDiagnostics();
        }

        #endregion

        #region OnMarketData - execution aggression

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (State != State.Realtime || CurrentBars[0] < BarsRequiredToTrade)
                return;

            if (e.Time > currentMarketTime)
                currentMarketTime = e.Time;

            if (e.MarketDataType == MarketDataType.Bid)
            {
                currentBestBid = e.Price;
                lastBidUpdateTime = e.Time;
                return;
            }

            if (e.MarketDataType == MarketDataType.Ask)
            {
                currentBestAsk = e.Price;
                lastAskUpdateTime = e.Time;
                return;
            }

            if (e.MarketDataType != MarketDataType.Last)
                return;

            tradeEventCount++;
            long volume = Math.Max(0, e.Volume);
            if (volume <= 0)
                return;

            if (bucket100msStart == DateTime.MinValue)
            {
                bucket100msStart = e.Time;
                bucket1sStart = e.Time;
                bucket5sStart = e.Time;
            }

            double priorTradePrice = lastTradePrice;
            bool hadPriorTradePrice = havePriorTradePrice;

            bool isAggressiveBuy;
            bool isAggressiveSell;
            ClassifyTradeAggression(e.Price, priorTradePrice, hadPriorTradePrice, out isAggressiveBuy, out isAggressiveSell);

            lastTradePrice = e.Price;
            havePriorTradePrice = true;

            if (isAggressiveBuy)
            {
                aggBuyVol100ms += volume;
                aggBuyVol1s += volume;
                aggBuyVol5s += volume;
            }
            else if (isAggressiveSell)
            {
                aggSellVol100ms += volume;
                aggSellVol1s += volume;
                aggSellVol5s += volume;
            }

            ProcessElapsedBuckets(e.Time);
        }

        private void ClassifyTradeAggression(double tradePrice, double priorTradePrice, bool hadPriorTradePrice, out bool isAggressiveBuy, out bool isAggressiveSell)
        {
            isAggressiveBuy = false;
            isAggressiveSell = false;

            bool quoteFresh = AreQuotesFresh();

            if (quoteFresh && currentBestAsk > 0 && Math.Abs(tradePrice - currentBestAsk) <= TickSize * 0.5)
            {
                isAggressiveBuy = true;
                return;
            }

            if (quoteFresh && currentBestBid > 0 && Math.Abs(tradePrice - currentBestBid) <= TickSize * 0.5)
            {
                isAggressiveSell = true;
                return;
            }

            // Corrected fallback: compare against prior trade price, not a just-overwritten lastTradePrice.
            if (hadPriorTradePrice)
            {
                if (tradePrice > priorTradePrice)
                    isAggressiveBuy = true;
                else if (tradePrice < priorTradePrice)
                    isAggressiveSell = true;
            }
        }

        private void ProcessElapsedBuckets(DateTime eventTime)
        {
            if ((eventTime - bucket100msStart).TotalMilliseconds >= BucketSizeMs)
            {
                ProcessBucket100ms();
                bucket100msStart = eventTime;
                Reset100msBucket();
            }

            if ((eventTime - bucket1sStart).TotalSeconds >= 1.0)
            {
                bucket1sStart = eventTime;
                Reset1sBucket();
            }

            if ((eventTime - bucket5sStart).TotalSeconds >= 5.0)
            {
                bucket5sStart = eventTime;
                Reset5sBucket();
            }
        }

        #endregion

        #region OnMarketDepth - book state and pull detection

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (State != State.Realtime || CurrentBars[0] < BarsRequiredToTrade)
                return;

            depthEventCount++;

            if (e.Time > currentMarketTime)
                currentMarketTime = e.Time;

            if (e.MarketDataType == MarketDataType.Ask && e.Position == 0)
            {
                currentBestAsk = e.Price;
                lastAskUpdateTime = e.Time;
            }
            else if (e.MarketDataType == MarketDataType.Bid && e.Position == 0)
            {
                currentBestBid = e.Price;
                lastBidUpdateTime = e.Time;
            }

            if (EnableBookPullDetection)
                UpdateBookPullState(e);
        }

        private void UpdateBookPullState(MarketDepthEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Bid && e.MarketDataType != MarketDataType.Ask)
                return;

            Dictionary<double, long> book = e.MarketDataType == MarketDataType.Bid ? bidBook : askBook;
            long newSize = Math.Max(0, e.Volume);
            long oldSize = 0;
            book.TryGetValue(e.Price, out oldSize);

            if (e.Operation == Operation.Remove)
            {
                if (oldSize > 0)
                {
                    if (e.MarketDataType == MarketDataType.Bid)
                        bidRemoveVol100ms += oldSize;
                    else
                        askRemoveVol100ms += oldSize;
                }
                book.Remove(e.Price);
                return;
            }

            if (e.Operation == Operation.Add || e.Operation == Operation.Update)
            {
                long delta = newSize - oldSize;

                if (delta > 0)
                {
                    if (e.MarketDataType == MarketDataType.Bid)
                        bidAddVol100ms += delta;
                    else
                        askAddVol100ms += delta;
                }
                else if (delta < 0)
                {
                    long removed = Math.Abs(delta);
                    if (e.MarketDataType == MarketDataType.Bid)
                        bidRemoveVol100ms += removed;
                    else
                        askRemoveVol100ms += removed;
                }

                if (newSize > 0)
                    book[e.Price] = newSize;
                else
                    book.Remove(e.Price);
            }
        }

        #endregion

        #region Bucket Processing

        private void ProcessBucket100ms()
        {
            long totalAggVol = aggBuyVol100ms + aggSellVol100ms;
            if (totalAggVol <= 0)
                return;

            long aggDelta = aggBuyVol100ms - aggSellVol100ms;
            double aggImbalance = (double)aggDelta / totalAggVol;

            string signal = "NONE";

            if (aggDelta >= MinAggressionDelta && aggImbalance >= MinAggressionImbalance)
                signal = "LONG";
            else if (aggDelta <= -MinAggressionDelta && aggImbalance <= -MinAggressionImbalance)
                signal = "SHORT";

            if (signal != "NONE" && !PassesMultiScaleConfirmation(signal))
                signal = "NONE";

            if (signal != "NONE" && EnableBookPullDetection && !PassesBookPullFilter(signal))
                signal = "NONE";

            if (signal != "NONE" && signal != lastSignal)
            {
                if (CanTakeSignal(signal))
                    EnterPosition(signal, totalAggVol, aggDelta, aggImbalance);

                lastSignal = signal;
            }
            else if (signal == "NONE")
            {
                lastSignal = "NONE";
            }
        }

        private bool PassesMultiScaleConfirmation(string signal)
        {
            if (Require1sConfirmation)
            {
                if (!PassesAggressionThreshold(signal, aggBuyVol1s, aggSellVol1s, Min1sAggressionDelta, Min1sAggressionImbalance))
                    return false;
            }

            if (Require5sConfirmation)
            {
                if (!PassesAggressionThreshold(signal, aggBuyVol5s, aggSellVol5s, Min5sAggressionDelta, Min5sAggressionImbalance))
                    return false;
            }

            return true;
        }

        private bool PassesAggressionThreshold(string signal, long buyVol, long sellVol, int minDelta, double minImbalance)
        {
            long total = buyVol + sellVol;
            if (total <= 0)
                return false;

            long delta = buyVol - sellVol;
            double imbalance = (double)delta / total;

            if (signal == "LONG")
                return delta >= minDelta && imbalance >= minImbalance;

            if (signal == "SHORT")
                return delta <= -minDelta && imbalance <= -minImbalance;

            return false;
        }

        private bool PassesBookPullFilter(string signal)
        {
            long bidNetAdd = bidAddVol100ms - bidRemoveVol100ms;
            long askNetAdd = askAddVol100ms - askRemoveVol100ms;

            if (signal == "LONG" && bidNetAdd <= -BookPullCancelThreshold)
                return false;

            if (signal == "SHORT" && askNetAdd <= -BookPullCancelThreshold)
                return false;

            return true;
        }

        private void ResetAllBuckets()
        {
            Reset100msBucket();
            Reset1sBucket();
            Reset5sBucket();
        }

        private void Reset100msBucket()
        {
            aggBuyVol100ms = 0;
            aggSellVol100ms = 0;
            bidAddVol100ms = 0;
            askAddVol100ms = 0;
            bidRemoveVol100ms = 0;
            askRemoveVol100ms = 0;
        }

        private void Reset1sBucket()
        {
            aggBuyVol1s = 0;
            aggSellVol1s = 0;
        }

        private void Reset5sBucket()
        {
            aggBuyVol5s = 0;
            aggSellVol5s = 0;
        }

        #endregion

        #region Signal Validation

        private bool CanTakeSignal(string signal)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return false;

            if (!IsRTH())
                return false;

            if (EnableDailyLimits && dailyLimitHit)
                return false;

            if (EnableOpeningRangeFilter && !orCalculated)
                return false;

            if (EnableSpreadFilter && !PassesSpreadFilter())
                return false;

            if (EnableQuoteFreshnessFilter && !AreQuotesFresh())
                return false;

            if (EnableCooldown && !PassesCooldown())
                return false;

            if (EnableManipulationFilters && !PassesManipulationFilters(signal))
                return false;

            return true;
        }

        private bool PassesSpreadFilter()
        {
            if (currentBestBid <= 0 || currentBestAsk <= 0)
                return false;

            double spreadTicks = (currentBestAsk - currentBestBid) / TickSize;
            return spreadTicks >= 0.5 && spreadTicks <= MaxSpreadTicks;
        }

        private bool AreQuotesFresh()
        {
            if (currentMarketTime == DateTime.MinValue || lastBidUpdateTime == DateTime.MinValue || lastAskUpdateTime == DateTime.MinValue)
                return false;

            double bidAgeMs = Math.Abs((currentMarketTime - lastBidUpdateTime).TotalMilliseconds);
            double askAgeMs = Math.Abs((currentMarketTime - lastAskUpdateTime).TotalMilliseconds);

            return bidAgeMs <= MaxQuoteAgeMs && askAgeMs <= MaxQuoteAgeMs;
        }

        private bool PassesCooldown()
        {
            if (lastTradeExitTime == DateTime.MinValue)
                return true;

            double requiredCooldown = CooldownSeconds;

            if (lastExitKind == "STOP")
                requiredCooldown = PostStopCooldownSeconds;
            else if (lastExitKind == "TIMEOUT")
                requiredCooldown = PostTimeoutCooldownSeconds;

            return (currentMarketTime - lastTradeExitTime).TotalSeconds >= requiredCooldown;
        }

        private bool PassesManipulationFilters(string signal)
        {
            string timeZone = GetTimeZone();
            string orLocation = GetORLocation();

            // These are still session-bias filters, not full manipulation detection. They remain deliberately
            // explicit and conservative until wall-pull/sweep/absorption logic is validated.
            if (timeZone == "OPEN_15" && signal == "SHORT")
                return false;

            if (timeZone == "POST_OPEN" && orLocation == "ABOVE_OR" && signal == "SHORT")
                return false;

            if (timeZone == "POST_OPEN" && orLocation == "INSIDE_OR" && signal == "SHORT")
                return false;

            if (timeZone == "NORMAL" && orLocation == "INSIDE_OR" && signal == "LONG")
                return false;

            if (timeZone == "CLOSE_30" && orLocation == "ABOVE_OR" && signal == "SHORT")
                return false;

            if (timeZone == "CLOSE_30" && orLocation == "BELOW_OR" && signal == "LONG")
                return false;

            return true;
        }

        #endregion

        #region Entry, timeout, and executions

        private void EnterPosition(string signal, long totalAggVol, long aggDelta, double aggImbalance)
        {
            if (signal == "LONG")
            {
                tradesCountToday++;
                entryTime = currentMarketTime;
                entryDirection = MarketPosition.Long;
                actualEntryPrice = 0;
                actualEntryQuantity = 0;
                protectiveOrdersSeenForCurrentTrade = false;
                EnterLong(1, LONG_SIGNAL);
            }
            else if (signal == "SHORT")
            {
                tradesCountToday++;
                entryTime = currentMarketTime;
                entryDirection = MarketPosition.Short;
                actualEntryPrice = 0;
                actualEntryQuantity = 0;
                protectiveOrdersSeenForCurrentTrade = false;
                EnterShort(1, SHORT_SIGNAL);
            }

            if (EnableVerboseLogging)
            {
                double spreadTicks = currentBestBid > 0 && currentBestAsk > 0 ? (currentBestAsk - currentBestBid) / TickSize : -1;
                Print(string.Format("{0} | ENTRY REQUEST {1} | AggVol:{2} Delta:{3} Imb:{4:F2} Spread:{5:F1} TZ:{6} OR:{7}",
                    FormatTime(currentMarketTime), signal, totalAggVol, aggDelta, aggImbalance, spreadTicks, GetTimeZone(), GetORLocation()));
            }
        }

        private void CheckTimeout()
        {
            if (entryTime == DateTime.MinValue || currentMarketTime == DateTime.MinValue)
                return;

            TimeSpan holdTime = currentMarketTime - entryTime;
            if (holdTime.TotalMinutes < TimeoutMinutes)
                return;

            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(TIMEOUT_EXIT_SIGNAL, LONG_SIGNAL);
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort(TIMEOUT_EXIT_SIGNAL, SHORT_SIGNAL);

            if (EnableVerboseLogging)
                Print(string.Format("{0} | TIMEOUT EXIT REQUEST after {1:F1} min", FormatTime(currentMarketTime), holdTime.TotalMinutes));
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            string orderName = execution.Order.Name ?? string.Empty;

            if (execution.Order.OrderState != OrderState.Filled && execution.Order.OrderState != OrderState.PartFilled)
                return;

            if (orderName == LONG_SIGNAL || orderName == SHORT_SIGNAL)
            {
                actualEntryPrice = execution.Price;
                actualEntryQuantity = execution.Quantity;
                entryTime = time;
                entryDirection = orderName == LONG_SIGNAL ? MarketPosition.Long : MarketPosition.Short;

                Print(string.Format("{0} | #{1} ENTRY FILL {2} @ {3:F2} qty={4} | bracket target={5}t stop={6}t",
                    FormatTime(time), tradesCountToday, entryDirection, actualEntryPrice, actualEntryQuantity, TargetTicks, StopTicks));
                return;
            }

            if (IsExitOrderName(orderName))
            {
                double tradePnL = CalculateActualTradePnL(execution.Price, execution.Quantity);
                lastTradeExitTime = time;
                lastExitKind = ClassifyExitKind(orderName);

                UpdateDailyTracking(tradePnL, orderName);

                Print(string.Format("{0} | #{1} EXIT {2} @ {3:F2} qty={4} | PnL:${5:F2} | Daily:${6:F2} | ConsLoss:{7} | Limits:{8}",
                    FormatTime(time), tradesCountToday, lastExitKind, execution.Price, execution.Quantity,
                    tradePnL, dailyPnL, consecutiveLosses, EnableDailyLimits ? "ON" : "OFF"));

                actualEntryPrice = 0;
                actualEntryQuantity = 0;
                entryDirection = MarketPosition.Flat;
                protectiveOrdersSeenForCurrentTrade = false;
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null || string.IsNullOrEmpty(order.Name))
                return;

            if (order.Name == LONG_SIGNAL)
                longEntryOrder = order;
            else if (order.Name == SHORT_SIGNAL)
                shortEntryOrder = order;
            else if (IsTargetOrderName(order.Name))
                targetOrder = order;
            else if (IsStopOrderName(order.Name))
                stopOrder = order;

            if (IsTargetOrderName(order.Name) || IsStopOrderName(order.Name))
                protectiveOrdersSeenForCurrentTrade = true;

            if (error != ErrorCode.NoError || orderState == OrderState.Rejected)
            {
                Print(string.Format("*** ORDER ERROR {0} | state={1} error={2} native={3} ***",
                    order.Name, orderState, error, nativeError));
            }

            if (EnableVerboseLogging && (orderState == OrderState.Accepted || orderState == OrderState.Working || orderState == OrderState.Rejected))
            {
                Print(string.Format("{0} | ORDER {1} state={2} qty={3} filled={4} avg={5:F2} limit={6:F2} stop={7:F2}",
                    FormatTime(time), order.Name, orderState, quantity, filled, averageFillPrice, limitPrice, stopPrice));
            }
        }

        private double CalculateActualTradePnL(double exitPrice, int exitQuantity)
        {
            if (actualEntryPrice <= 0 || exitQuantity <= 0)
                return 0;

            int qty = actualEntryQuantity > 0 ? Math.Min(actualEntryQuantity, exitQuantity) : exitQuantity;

            if (entryDirection == MarketPosition.Long)
                return (exitPrice - actualEntryPrice) * qty * Instrument.MasterInstrument.PointValue;

            if (entryDirection == MarketPosition.Short)
                return (actualEntryPrice - exitPrice) * qty * Instrument.MasterInstrument.PointValue;

            return 0;
        }

        private bool IsExitOrderName(string orderName)
        {
            return IsTargetOrderName(orderName) || IsStopOrderName(orderName) || orderName == TIMEOUT_EXIT_SIGNAL || orderName == "Close position";
        }

        private bool IsTargetOrderName(string orderName)
        {
            if (string.IsNullOrEmpty(orderName))
                return false;

            return orderName.Contains("Profit target") || orderName.Contains("Target") || orderName == LONG_TARGET_SIGNAL || orderName == SHORT_TARGET_SIGNAL;
        }

        private bool IsStopOrderName(string orderName)
        {
            if (string.IsNullOrEmpty(orderName))
                return false;

            return orderName.Contains("Stop loss") || orderName.Contains("Stop") || orderName == LONG_STOP_SIGNAL || orderName == SHORT_STOP_SIGNAL;
        }

        private string ClassifyExitKind(string orderName)
        {
            if (IsStopOrderName(orderName))
                return "STOP";

            if (IsTargetOrderName(orderName))
                return "TARGET";

            if (orderName == TIMEOUT_EXIT_SIGNAL)
                return "TIMEOUT";

            if (orderName == "Close position")
                return "SESSION_CLOSE";

            return "EXIT";
        }

        #endregion

        #region Daily Tracking

        private void ResetDailyTracking(DateTime tradeDateEt)
        {
            dailyPnL = 0;
            consecutiveLosses = 0;
            dailyPeakPnL = 0;
            dailyLimitHit = false;
            tradesCountToday = 0;
            depthEventCount = 0;
            tradeEventCount = 0;
            lastDiagnosticPrintTime = DateTime.MinValue;
            lastSignal = "NONE";
            lastExitKind = "NONE";
            lastTradeExitTime = DateTime.MinValue;

            ResetOpeningRange(tradeDateEt);
            ResetAllBuckets();

            Print(string.Format("========== {0:yyyy-MM-dd} ET - NEW TRADING DAY ==========" , tradeDateEt));
        }

        private void UpdateDailyTracking(double tradePnL, string exitReason)
        {
            dailyPnL += tradePnL;

            if (dailyPnL > dailyPeakPnL)
                dailyPeakPnL = dailyPnL;

            if (tradePnL < 0)
                consecutiveLosses++;
            else
                consecutiveLosses = 0;

            if (!EnableDailyLimits)
                return;

            if (dailyPnL <= -MaxDailyLoss)
            {
                dailyLimitHit = true;
                Print(string.Format("*** DAILY LOSS LIMIT HIT: ${0:F2} ***", dailyPnL));
            }

            if (consecutiveLosses >= MaxConsecutiveLosses)
            {
                dailyLimitHit = true;
                Print(string.Format("*** CONSECUTIVE LOSS LIMIT HIT: {0} losses ***", consecutiveLosses));
            }

            double drawdown = dailyPnL - dailyPeakPnL;
            if (dailyPeakPnL >= ProfitLockPeak && drawdown <= -ProfitLockDrawdown)
            {
                dailyLimitHit = true;
                Print(string.Format("*** PROFIT LOCK TRIGGERED: Peak ${0:F2}, DD ${1:F2} ***", dailyPeakPnL, drawdown));
            }
        }

        #endregion

        #region Opening Range

        private void UpdateOpeningRangeFromOneMinuteSeries()
        {
            if (CurrentBars.Length < 2 || CurrentBars[1] < 1)
                return;

            DateTime barTimeEt = ToEasternTime(Times[1][0]);
            DateTime tradeDateEt = barTimeEt.Date;

            if (orTradeDate != tradeDateEt)
                ResetOpeningRange(tradeDateEt);

            DateTime orStart = new DateTime(tradeDateEt.Year, tradeDateEt.Month, tradeDateEt.Day, 9, 30, 0);
            DateTime orEnd = new DateTime(tradeDateEt.Year, tradeDateEt.Month, tradeDateEt.Day, 9, 45, 0);

            if (barTimeEt >= orStart && barTimeEt < orEnd)
            {
                if (orHigh == 0 || Highs[1][0] > orHigh)
                    orHigh = Highs[1][0];

                if (orLow == 0 || Lows[1][0] < orLow)
                    orLow = Lows[1][0];
            }

            if (!orCalculated && barTimeEt >= orEnd && orHigh > 0 && orLow > 0)
            {
                orCalculated = true;
                Print(string.Format("{0} | OR CALCULATED FROM INTERNAL 1M: High={1:F2} Low={2:F2}", FormatTime(Times[1][0]), orHigh, orLow));
            }
        }

        private void ResetOpeningRange(DateTime tradeDateEt)
        {
            orTradeDate = tradeDateEt;
            orHigh = 0;
            orLow = 0;
            orCalculated = false;
        }

        #endregion

        #region Helpers

        private bool IsRTH()
        {
            DateTime nowEt = ToEasternTime(currentMarketTime == DateTime.MinValue ? Time[0] : currentMarketTime);
            TimeSpan now = nowEt.TimeOfDay;
            TimeSpan rthStart = new TimeSpan(RTHStartHour, RTHStartMinute, 0);
            TimeSpan rthEnd = new TimeSpan(RTHEndHour, RTHEndMinute, 0);
            return now >= rthStart && now < rthEnd;
        }

        private string GetTimeZone()
        {
            DateTime nowEt = ToEasternTime(currentMarketTime == DateTime.MinValue ? Time[0] : currentMarketTime);
            int hour = nowEt.Hour;
            int minute = nowEt.Minute;

            if (hour == 9 && minute < 45)
                return "OPEN_15";

            if ((hour == 9 && minute >= 45) || (hour == 10 && minute < 30))
                return "POST_OPEN";

            if (hour == 15 && minute >= 30)
                return "CLOSE_30";

            return "NORMAL";
        }

        private string GetORLocation()
        {
            if (!orCalculated || orHigh == 0 || orLow == 0)
                return "UNKNOWN";

            double currentPrice = Close[0];

            if (currentPrice > orHigh)
                return "ABOVE_OR";

            if (currentPrice < orLow)
                return "BELOW_OR";

            return "INSIDE_OR";
        }

        private DateTime ToEasternTime(DateTime dt)
        {
            try
            {
                TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                return TimeZoneInfo.ConvertTime(dt, eastern);
            }
            catch
            {
                return dt;
            }
        }

        private string FormatTime(DateTime dt)
        {
            if (dt == DateTime.MinValue)
                return "NA";

            return dt.ToString("HH:mm:ss.fff");
        }

        private void MaybePrintDiagnostics()
        {
            if (currentMarketTime == DateTime.MinValue)
                return;

            if (lastDiagnosticPrintTime == DateTime.MinValue)
            {
                lastDiagnosticPrintTime = currentMarketTime;
                return;
            }

            if ((currentMarketTime - lastDiagnosticPrintTime).TotalSeconds < DiagnosticIntervalSeconds)
                return;

            lastDiagnosticPrintTime = currentMarketTime;

            if (!EnableVerboseLogging)
                return;

            double spreadTicks = currentBestAsk > 0 && currentBestBid > 0 ? (currentBestAsk - currentBestBid) / TickSize : -1;
            Print(string.Format("{0} | DIAG trades={1} depth={2} bid={3:F2} ask={4:F2} spread={5:F1} OR={6}/{7:F2}-{8:F2} pos={9} protectiveSeen={10}",
                FormatTime(currentMarketTime), tradeEventCount, depthEventCount, currentBestBid, currentBestAsk, spreadTicks,
                orCalculated, orLow, orHigh, Position.MarketPosition, protectiveOrdersSeenForCurrentTrade));
        }

        #endregion

        #region Properties

        [NinjaScriptProperty]
        [Range(50, 1000)]
        [Display(Name = "Bucket Size (ms)", Order = 1, GroupName = "1. Signal")]
        public int BucketSizeMs { get; set; }

        [NinjaScriptProperty]
        [Range(10, 500)]
        [Display(Name = "Min 100ms Aggression Delta", Order = 2, GroupName = "1. Signal")]
        public int MinAggressionDelta { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 0.95)]
        [Display(Name = "Min 100ms Aggression Imbalance", Order = 3, GroupName = "1. Signal")]
        public double MinAggressionImbalance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require 1s Confirmation", Order = 4, GroupName = "1. Signal")]
        public bool Require1sConfirmation { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Min 1s Aggression Delta", Order = 5, GroupName = "1. Signal")]
        public int Min1sAggressionDelta { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 0.95)]
        [Display(Name = "Min 1s Aggression Imbalance", Order = 6, GroupName = "1. Signal")]
        public double Min1sAggressionImbalance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require 5s Confirmation", Order = 7, GroupName = "1. Signal")]
        public bool Require5sConfirmation { get; set; }

        [NinjaScriptProperty]
        [Range(1, 2000)]
        [Display(Name = "Min 5s Aggression Delta", Order = 8, GroupName = "1. Signal")]
        public int Min5sAggressionDelta { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 0.95)]
        [Display(Name = "Min 5s Aggression Imbalance", Order = 9, GroupName = "1. Signal")]
        public double Min5sAggressionImbalance { get; set; }

        [NinjaScriptProperty]
        [Range(4, 200)]
        [Display(Name = "Target (ticks)", Order = 1, GroupName = "2. Risk")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(4, 100)]
        [Display(Name = "Stop (ticks)", Order = 2, GroupName = "2. Risk")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "Timeout (minutes)", Order = 3, GroupName = "2. Risk")]
        public int TimeoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Spread (ticks)", Order = 4, GroupName = "2. Risk")]
        public int MaxSpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Range(50, 5000)]
        [Display(Name = "Max Quote Age (ms)", Order = 5, GroupName = "2. Risk")]
        public int MaxQuoteAgeMs { get; set; }

        [NinjaScriptProperty]
        [Range(5, 600)]
        [Display(Name = "Cooldown (seconds)", Order = 1, GroupName = "3. Cooldown")]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(10, 1200)]
        [Display(Name = "Post-Stop Cooldown (seconds)", Order = 2, GroupName = "3. Cooldown")]
        public int PostStopCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(5, 1200)]
        [Display(Name = "Post-Timeout Cooldown (seconds)", Order = 3, GroupName = "3. Cooldown")]
        public int PostTimeoutCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(30, 5000)]
        [Display(Name = "Max Daily Loss ($)", Order = 1, GroupName = "4. Daily Limits")]
        public double MaxDailyLoss { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Max Consecutive Losses", Order = 2, GroupName = "4. Daily Limits")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Range(100, 50000)]
        [Display(Name = "Profit Lock Peak ($)", Order = 3, GroupName = "4. Daily Limits")]
        public double ProfitLockPeak { get; set; }

        [NinjaScriptProperty]
        [Range(50, 10000)]
        [Display(Name = "Profit Lock Drawdown ($)", Order = 4, GroupName = "4. Daily Limits")]
        public double ProfitLockDrawdown { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Daily Limits", Order = 1, GroupName = "5. Filters")]
        public bool EnableDailyLimits { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Opening Range Filter", Order = 2, GroupName = "5. Filters")]
        public bool EnableOpeningRangeFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Manipulation Filters", Order = 3, GroupName = "5. Filters")]
        public bool EnableManipulationFilters { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Spread Filter", Order = 4, GroupName = "5. Filters")]
        public bool EnableSpreadFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Quote Freshness Filter", Order = 5, GroupName = "5. Filters")]
        public bool EnableQuoteFreshnessFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Cooldown", Order = 6, GroupName = "5. Filters")]
        public bool EnableCooldown { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Book Pull Detection", Order = 7, GroupName = "5. Filters")]
        public bool EnableBookPullDetection { get; set; }

        [NinjaScriptProperty]
        [Range(1, 2000)]
        [Display(Name = "Book Pull Cancel Threshold", Order = 8, GroupName = "5. Filters")]
        public int BookPullCancelThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Verbose Logging", Order = 9, GroupName = "5. Filters")]
        public bool EnableVerboseLogging { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Periodic Diagnostics", Order = 10, GroupName = "5. Filters")]
        public bool EnablePeriodicDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(10, 600)]
        [Display(Name = "Diagnostic Interval Seconds", Order = 11, GroupName = "5. Filters")]
        public int DiagnosticIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "RTH Start Hour", Order = 1, GroupName = "6. RTH")]
        public int RTHStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "RTH Start Minute", Order = 2, GroupName = "6. RTH")]
        public int RTHStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "RTH End Hour", Order = 3, GroupName = "6. RTH")]
        public int RTHEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "RTH End Minute", Order = 4, GroupName = "6. RTH")]
        public int RTHEndMinute { get; set; }

        #endregion
    }
}
