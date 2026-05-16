
#region Using declarations
using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // =====================================================================
    // CG_L2_MC_Live
    //
    // ChatGPT-merged live strategy derived from the user's AbsorptionL2Live.
    //
    // Methodology:
    // 1) Preserve the original absorption / iceberg live entry framework.
    // 2) Add an internal Level II market-condition engine that classifies the
    //    microstructure regime from depth changes + aggressive trades.
    // 3) Use regime scores as a gating layer for entries:
    //      - block chaotic / spoofy conditions,
    //      - prefer balanced / absorptive conditions for reversal-style entries,
    //      - require stronger directional evidence in thin markets,
    //      - block entries when the regime strongly points the opposite way.
    //
    // Important:
    // - Original source file remains untouched.
    // - This file is a new strategy variant.
    // - File name uses hyphens per request, but the C# class name cannot.
    // =====================================================================
    public class CG_L2_MC_Live : Strategy
    {
        // =============================================================
        // CONSTANTS — original strategy params
        // =============================================================

        // --- Absorption signal ---
        private const int    ABS_WINDOW_SECONDS    = 30;     // rolling window for trade accumulation
        private const int    ABS_MIN_VOLUME        = 50;     // min aggressive contracts at level
        private const int    ABS_MIN_TRADES        = 20;     // min aggressive trade count at level
        private const int    ABS_HOLD_SECONDS      = 3;      // min duration of activity at level
        private const int    ABS_MAX_LEVELS        = 2;      // scan ±N ticks from BBO
        private const int    ABS_MIN_BOOK_DEPTH    = 35;     // min resting contracts in L2 book

        // --- Iceberg detection ---
        private const int    ICE_CONSUME_THRESHOLD = 3;      // depth below this = consumed
        private const int    ICE_REFILL_THRESHOLD  = 10;     // depth above this = refilled
        private const int    ICE_MIN_CYCLES        = 3;      // min consume+refill cycles for signal
        private const int    ICE_WINDOW_SECONDS    = 60;     // time window for tracking cycles
        private const int    ICE_MAX_LEVELS        = 2;      // scan ±N ticks from BBO

        // --- Condition-based entry gates (original) ---
        private const int    GATE_BOOK_LEVELS      = 5;      // sum depth across top N levels each side
        private const double GATE_BOOK_IMBAL_MIN   = 1.5;    // min bid/ask depth ratio for directional conviction
        private const int    GATE_RANGE_MIN_TICKS  = 4;      // min price range in last 30s (skip chop)
        private const int    GATE_DELTA_MIN        = 15;     // min |net delta| in last 30s for directional flow
        private const int    GATE_MIN_SECONDS_FLAT = 5;      // absolute min seconds between entries (anti-spam)

        // --- ATR exits ---
        private const int    ATR_PERIOD            = 14;
        private const double ATR_SL_MULT           = 0.10;
        private const double ATR_TP_MULT           = 3.0;
        private const double MAX_SL_TICKS          = 40.0;
        private const double MIN_BROKER_SL_TICKS   = 40.0;

        // --- 3-stage profit-lock trail ---
        private const int    TRAIL_BE_TRIGGER_TICKS    = 4;
        private const int    TRAIL_BE_PLUS_TICKS       = 4;
        private const int    TRAIL_LOCK1_TRIGGER_TICKS = 10;
        private const int    TRAIL_LOCK1_PROFIT_TICKS  = 8;
        private const double TRAIL_RUNNER_ATR_MULT     = 0.10;

        // --- Session filter (RTH Eastern) ---
        private const int SESSION_START_HOUR   = 9;
        private const int SESSION_START_MINUTE = 30;
        private const int SESSION_END_HOUR     = 16;
        private const int SESSION_END_MINUTE   = 0;

        // --- Lunch blackout ---
        private const int LUNCH_START_HOUR   = 12;
        private const int LUNCH_START_MINUTE = 0;
        private const int LUNCH_END_HOUR     = 14;
        private const int LUNCH_END_MINUTE   = 0;

        // --- Risk controls ---
        private const double MAX_DRAWDOWN_USD = -150.0;
        private const double STARTING_CAPITAL = 1500.0;

        // --- Limit entry ---
        private const int LIMIT_ENTRY_MAX_SLIP_TICKS = 1;
        private const int LIMIT_ENTRY_TIMEOUT_MS     = 2000;

        // --- Fees ---
        private const double COMMISSION_PER_RT = 0.70;

        // --- Ring buffer ---
        private const int RING_CAPACITY = 5000;

        // --- Level accumulator ---
        private const int LEVEL_COUNT = 2000;

        // =============================================================
        // CONSTANTS — new L2 market-condition engine
        // =============================================================
        private const int    MC_WINDOW_MS                     = 1000;
        private const double MC_HIGH_DEPTH_THRESHOLD          = 300.0;
        private const double MC_ABSORPTION_RATIO_THRESHOLD    = 5.0;
        private const double MC_SPOOF_CHURN_THRESHOLD         = 2.0;
        private const double MC_CHAOS_EVENT_RATE_THRESHOLD    = 200.0;

        // Entry gating thresholds derived from market-condition snapshot
        private const double MC_BLOCK_OPPOSING_TREND_SCORE    = 0.75;
        private const double MC_ALLOW_DIRECTIONAL_SCORE       = 0.55;
        private const double MC_ALLOW_ABSORPTION_SCORE        = 0.60;
        private const double MC_BLOCK_SPOOF_SCORE             = 0.80;
        private const double MC_BLOCK_CHAOS_SCORE             = 0.80;
        private const double MC_THIN_EXTRA_IMBAL_MULT         = 1.25;
        private const double MC_THIN_REQUIRED_TREND_SCORE     = 0.65;

        // =============================================================
        // Types
        // =============================================================
        private enum TradeState { Flat, EntryPending, Long, Short, ExitPending }
        private enum SignalKind { None, Absorption, Iceberg }

        private struct RingEntry
        {
            public DateTime TimeUtc;
            public double   Price;
            public int      Volume;
            public bool     IsAggressiveBuy;  // true = buy aggressor (hit ask), false = sell aggressor (hit bid)
            public int      LevelIdx;         // index into _levels, or -1
        }

        private struct LevelAccum
        {
            public int      BuyContracts;    // aggressive buy volume (hit ask)
            public int      BuyCount;        // aggressive buy trade count
            public int      SellContracts;   // aggressive sell volume (hit bid)
            public int      SellCount;       // aggressive sell trade count
            public DateTime FirstEvent;
            public DateTime LastEvent;
        }

        private struct IcebergState
        {
            public int      RefillCount;     // consume+refill cycles
            public DateTime FirstCycle;      // time of first refill
            public DateTime LastCycle;       // time of most recent refill
            public bool     IsConsumed;      // currently in consumed state
            public long     PeakDepth;       // depth before last consume
        }

        private enum MarketRegime
        {
            Unknown,
            Balanced,
            DirectionalUp,
            DirectionalDown,
            AbsorptiveBid,
            AbsorptiveAsk,
            Thin,
            Spoofy,
            Chaotic
        }

        private class L2MarketConditionConfig
        {
            public int WindowMs = MC_WINDOW_MS;
            public double HighDepthThreshold = MC_HIGH_DEPTH_THRESHOLD;
            public double AbsorptionRatioThreshold = MC_ABSORPTION_RATIO_THRESHOLD;
            public double SpoofChurnThreshold = MC_SPOOF_CHURN_THRESHOLD;
            public double ChaosEventRateThreshold = MC_CHAOS_EVENT_RATE_THRESHOLD;
            public double ScoreClamp = 1.0;
        }

        private class L2MarketConditionSnapshot
        {
            public MarketRegime Regime = MarketRegime.Unknown;
            public double TrendUpScore;
            public double TrendDownScore;
            public double BalanceScore;
            public double AbsorptionBidScore;
            public double AbsorptionAskScore;
            public double ThinScore;
            public double SpoofScore;
            public double ChaosScore;

            public override string ToString()
            {
                return string.Format(
                    "Regime={0} Up={1:F2} Dn={2:F2} Bal={3:F2} AbsBid={4:F2} AbsAsk={5:F2} Thin={6:F2} Spoof={7:F2} Chaos={8:F2}",
                    Regime, TrendUpScore, TrendDownScore, BalanceScore, AbsorptionBidScore, AbsorptionAskScore, ThinScore, SpoofScore, ChaosScore
                );
            }
        }

        private class L2MarketConditionEngine
        {
            private readonly L2MarketConditionConfig cfg;

            private double bidSize;
            private double askSize;

            private double bidAdds;
            private double bidCancels;
            private double askAdds;
            private double askCancels;

            private double aggressiveBuyVol;
            private double aggressiveSellVol;

            private int eventCount;
            private DateTime windowStartUtc;
            private double lastTradePrice;
            private double priceDeltaAbs;

            public L2MarketConditionEngine(L2MarketConditionConfig config)
            {
                cfg = config;
                windowStartUtc = DateTime.UtcNow;
            }

            public void OnDepth(bool isBid, double size, double prevSize, DateTime nowUtc)
            {
                if (windowStartUtc == DateTime.MinValue)
                    windowStartUtc = nowUtc;

                double delta = size - prevSize;

                if (isBid)
                {
                    bidSize = size;
                    if (delta > 0) bidAdds += delta;
                    else if (delta < 0) bidCancels += -delta;
                }
                else
                {
                    askSize = size;
                    if (delta > 0) askAdds += delta;
                    else if (delta < 0) askCancels += -delta;
                }

                eventCount++;
            }

            public void OnTrade(double price, double volume, bool isAggressiveBuy, DateTime nowUtc)
            {
                if (windowStartUtc == DateTime.MinValue)
                    windowStartUtc = nowUtc;

                if (isAggressiveBuy)
                    aggressiveBuyVol += volume;
                else
                    aggressiveSellVol += volume;

                if (lastTradePrice > 0.0)
                    priceDeltaAbs += Math.Abs(price - lastTradePrice);

                lastTradePrice = price;
                eventCount++;
            }

            public L2MarketConditionSnapshot Compute(DateTime nowUtc)
            {
                if (windowStartUtc == DateTime.MinValue)
                    windowStartUtc = nowUtc;

                double elapsedMs = (nowUtc - windowStartUtc).TotalMilliseconds;
                if (elapsedMs < cfg.WindowMs)
                    return null;

                var snap = new L2MarketConditionSnapshot();

                double totalDepth = bidSize + askSize;
                double netPressure = (bidAdds - bidCancels) - (askAdds - askCancels);
                double totalVol = aggressiveBuyVol + aggressiveSellVol;
                double absorption = priceDeltaAbs > 0.0 ? totalVol / priceDeltaAbs : totalVol;
                double churn = (bidCancels + askCancels) / Math.Max(1.0, bidAdds + askAdds);
                double eventRate = elapsedMs > 0.0 ? eventCount / (elapsedMs / 1000.0) : 0.0;

                snap.ThinScore = Clamp(1.0 - totalDepth / cfg.HighDepthThreshold);
                snap.BalanceScore = Clamp(1.0 - Math.Abs(netPressure) / 100.0);

                snap.TrendUpScore = Clamp(netPressure > 0.0 ? netPressure / 100.0 : 0.0);
                snap.TrendDownScore = Clamp(netPressure < 0.0 ? -netPressure / 100.0 : 0.0);

                // Bid absorption = aggressive sells are failing to move price.
                snap.AbsorptionBidScore = Clamp(aggressiveSellVol > aggressiveBuyVol ? absorption / cfg.AbsorptionRatioThreshold : 0.0);

                // Ask absorption = aggressive buys are failing to move price.
                snap.AbsorptionAskScore = Clamp(aggressiveBuyVol > aggressiveSellVol ? absorption / cfg.AbsorptionRatioThreshold : 0.0);

                snap.SpoofScore = Clamp(churn / cfg.SpoofChurnThreshold);
                snap.ChaosScore = Clamp(eventRate / cfg.ChaosEventRateThreshold);

                snap.Regime = SelectRegime(snap);

                Reset(nowUtc);
                return snap;
            }

            private MarketRegime SelectRegime(L2MarketConditionSnapshot s)
            {
                if (s.ChaosScore > 0.80) return MarketRegime.Chaotic;
                if (s.SpoofScore > 0.85) return MarketRegime.Spoofy;
                if (s.ThinScore > 0.70) return MarketRegime.Thin;
                if (s.AbsorptionBidScore > 0.70) return MarketRegime.AbsorptiveBid;
                if (s.AbsorptionAskScore > 0.70) return MarketRegime.AbsorptiveAsk;
                if (s.TrendUpScore > 0.70) return MarketRegime.DirectionalUp;
                if (s.TrendDownScore > 0.70) return MarketRegime.DirectionalDown;
                return MarketRegime.Balanced;
            }

            private void Reset(DateTime nowUtc)
            {
                bidAdds = bidCancels = 0.0;
                askAdds = askCancels = 0.0;
                aggressiveBuyVol = aggressiveSellVol = 0.0;
                eventCount = 0;
                priceDeltaAbs = 0.0;
                windowStartUtc = nowUtc;
            }

            private double Clamp(double v)
            {
                if (v < 0.0) return 0.0;
                if (v > cfg.ScoreClamp) return cfg.ScoreClamp;
                return v;
            }
        }

        // =============================================================
        // State — trade management
        // =============================================================
        private TradeState tradeState = TradeState.Flat;
        private Order  entryOrder;
        private string activeSignal = string.Empty;
        private int    signalCounter = 0;
        private int    filledQty = 0;
        private double avgFillPrice = 0.0;
        private int    positionDirection = 0;
        private double equityHighWater = 0.0;
        private bool   circuitBreakerTripped = false;

        private double lastBid = double.NaN;
        private double lastAsk = double.NaN;
        private double lastTradePrice = double.NaN;

        private ATR    atrSlow;
        private double entryAtr = 0.0;
        private double entrySlTicks = 0;
        private double entryTpTicks = 0;

        // 3-stage trail state
        private double trailHighPx = double.NaN;
        private bool   trailBeActivated = false;
        private bool   trailLock1Activated = false;
        private bool   trailActive = false;

        // Risk
        private DateTime currentTradeDay = DateTime.MinValue;
        private int      tradesToday = 0;
        private double   dailyPnlUsd = 0.0;
        private double   cumNetPnl = 0.0;
        private int      tradeCount = 0;

        // Limit entry
        private double   signalPrice = double.NaN;
        private DateTime entrySubmitTimeUtc = DateTime.MinValue;
        private DateTime entryTimeUtc = DateTime.MinValue;
        private DateTime lastEventTimeUtc = DateTime.MinValue;
        private bool     needStopTighten = false;
        private bool     cancelPendingEntry = false;
        private bool     limitCancelRequested = false;
        private bool     brokerStopAtRealSl = false;

        private TimeZoneInfo easternTz;

        // =============================================================
        // State — book snapshot / original strategy
        // =============================================================
        private Dictionary<double, long> bidBook;
        private Dictionary<double, long> askBook;
        private Dictionary<double, IcebergState> bidIcebergs;
        private Dictionary<double, IcebergState> askIcebergs;

        private RingEntry[] ring;
        private int ringHead;
        private int ringCount;

        private LevelAccum[] levels;
        private double levelBasePrice;
        private bool   levelBaseSet;

        private DateTime lastEntryTimeUtc = DateTime.MinValue;
        private int      lastLossDirection = 0;
        private SignalKind pendingSignalKind = SignalKind.None;

        // =============================================================
        // State — new market-condition regime engine
        // =============================================================
        private L2MarketConditionConfig mcConfig;
        private L2MarketConditionEngine mcEngine;
        private L2MarketConditionSnapshot lastMcSnapshot;
        private MarketRegime lastPrintedRegime = MarketRegime.Unknown;

        // =============================================================
        // Lifecycle
        // =============================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_L2_MC_Live";
                Description = "AbsorptionL2Live merged with L2 market-condition regime gating.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;
                IsUnmanaged = false;
            }
            else if (State == State.Configure)
            {
                ClearOutputWindow();
                Print("=== CG_L2_MC_Live starting (Configure) ===");

                // BarsArray[1]: 60-second bars for slow ATR
                AddDataSeries(BarsPeriodType.Second, 60);
            }
            else if (State == State.DataLoaded)
            {
                atrSlow = ATR(BarsArray[1], ATR_PERIOD);

                bidBook = new Dictionary<double, long>(256);
                askBook = new Dictionary<double, long>(256);

                bidIcebergs = new Dictionary<double, IcebergState>(64);
                askIcebergs = new Dictionary<double, IcebergState>(64);

                ring = new RingEntry[RING_CAPACITY];
                ringHead = 0;
                ringCount = 0;

                levels = new LevelAccum[LEVEL_COUNT];
                levelBaseSet = false;

                mcConfig = new L2MarketConditionConfig();
                mcEngine = new L2MarketConditionEngine(mcConfig);
                lastMcSnapshot = null;
                lastPrintedRegime = MarketRegime.Unknown;

                try
                {
                    easternTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                }
                catch
                {
                    try { easternTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
                    catch { easternTz = TimeZoneInfo.Local; }
                }

                Print("=== CG_L2_MC_Live DataLoaded OK ===");
            }
            else if (State == State.Realtime)
            {
                Print("=== CG_L2_MC_Live REALTIME — ready to trade ===");
            }
            else if (State == State.Terminated)
            {
                Print("=== CG_L2_MC_Live Terminated ===");
            }
        }

        // =============================================================
        // OnMarketDepth — maintain L2 book + feed regime engine
        // =============================================================
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e == null) return;

            bool isAsk = e.MarketDataType == MarketDataType.Ask;
            var book = isAsk ? askBook : bidBook;
            var icebergs = isAsk ? askIcebergs : bidIcebergs;
            double price = e.Price;

            long oldDepth = 0;
            book.TryGetValue(price, out oldDepth);

            long newDepth = 0;
            if (e.Operation == Operation.Add || e.Operation == Operation.Update)
            {
                newDepth = e.Volume > 0 ? e.Volume : 0;
                if (newDepth > 0)
                    book[price] = newDepth;
                else
                    book.Remove(price);
            }
            else if (e.Operation == Operation.Remove)
            {
                book.Remove(price);
                newDepth = 0;
            }

            DateTime nowUtc = lastEventTimeUtc != DateTime.MinValue
                ? lastEventTimeUtc
                : DateTime.UtcNow;

            if (mcEngine != null)
                mcEngine.OnDepth(!isAsk, newDepth, oldDepth, nowUtc);

            // --- Iceberg cycle detection ---
            if (icebergs == null) return;

            IcebergState ice;
            if (!icebergs.TryGetValue(price, out ice))
                ice = new IcebergState();

            // Consumed: depth dropped below threshold
            if (oldDepth >= ICE_REFILL_THRESHOLD && newDepth <= ICE_CONSUME_THRESHOLD)
            {
                ice.IsConsumed = true;
                ice.PeakDepth = oldDepth;
            }
            // Refilled: was consumed, now depth restored
            else if (ice.IsConsumed && newDepth >= ICE_REFILL_THRESHOLD)
            {
                ice.IsConsumed = false;
                ice.RefillCount++;
                if (ice.RefillCount == 1)
                    ice.FirstCycle = nowUtc;
                ice.LastCycle = nowUtc;
            }

            // Expire stale iceberg state
            if (ice.RefillCount > 0 && nowUtc > DateTime.MinValue && ice.LastCycle > DateTime.MinValue)
            {
                double ageSec = (nowUtc - ice.LastCycle).TotalSeconds;
                if (ageSec > ICE_WINDOW_SECONDS)
                    ice = new IcebergState();
            }

            icebergs[price] = ice;
        }

        // =============================================================
        // OnMarketData — tick-driven core
        // =============================================================
        private bool firstMarketDataPrinted = false;

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e == null) return;

            if (!firstMarketDataPrinted && e.MarketDataType == MarketDataType.Last)
            {
                firstMarketDataPrinted = true;
                Print(string.Format("[FIRST_TICK] price={0} vol={1} state={2} | bidBook={3} askBook={4}",
                    e.Price, e.Volume, State, bidBook.Count, askBook.Count));
            }

            if (e.MarketDataType == MarketDataType.Bid)
            {
                lastBid = e.Price;
                return;
            }
            if (e.MarketDataType == MarketDataType.Ask)
            {
                lastAsk = e.Price;
                return;
            }
            if (e.MarketDataType != MarketDataType.Last)
                return;
            if (e.Volume <= 0)
                return;

            lastEventTimeUtc = e.Time.Kind == DateTimeKind.Utc
                ? e.Time
                : e.Time.ToUniversalTime();

            CheckDayRollover();
            lastTradePrice = e.Price;

            bool isAggressiveBuy = ClassifyTradeAsAggressiveBuy(e.Price);

            // Feed market-condition engine first so the latest trade contributes to the next snapshot.
            if (mcEngine != null)
            {
                mcEngine.OnTrade(e.Price, e.Volume, isAggressiveBuy, lastEventTimeUtc);
                var snap = mcEngine.Compute(lastEventTimeUtc);
                if (snap != null)
                {
                    lastMcSnapshot = snap;
                    if (snap.Regime != lastPrintedRegime)
                    {
                        lastPrintedRegime = snap.Regime;
                        Print("[MC_REGIME] " + snap.ToString() + " | " + FormatEventTime());
                    }
                }
            }

            // Feed into original ring buffer + level accumulators
            EvictExpiredRing(lastEventTimeUtc);
            int levelIdx = GetLevelIndex(e.Price);
            AddToRing(lastEventTimeUtc, e.Price, (int)e.Volume, isAggressiveBuy, levelIdx);
            if (levelIdx >= 0)
                AddToLevel(levelIdx, lastEventTimeUtc, (int)e.Volume, isAggressiveBuy);

            // Circuit breaker: flatten and stop
            if (circuitBreakerTripped)
            {
                if (tradeState == TradeState.Long || tradeState == TradeState.Short)
                {
                    tradeState = TradeState.ExitPending;
                    if (positionDirection == 1)
                        ExitLong("CIRCUIT_BRK_L", activeSignal);
                    else
                        ExitShort("CIRCUIT_BRK_S", activeSignal);
                }
                if (tradeState == TradeState.EntryPending && entryOrder != null)
                    cancelPendingEntry = true;
                return;
            }

            // Check unrealized DD while in position
            if (MAX_DRAWDOWN_USD < 0 && !circuitBreakerTripped
                && (tradeState == TradeState.Long || tradeState == TradeState.Short)
                && avgFillPrice > 0 && TickSize > 0)
            {
                double unrealizedPnl = (e.Price - avgFillPrice) * positionDirection
                    * Instrument.MasterInstrument.PointValue * 1;
                double liveEquity = STARTING_CAPITAL + cumNetPnl + unrealizedPnl;
                double ddFromPeak = liveEquity - equityHighWater;
                if (ddFromPeak <= MAX_DRAWDOWN_USD)
                {
                    circuitBreakerTripped = true;
                    Print(string.Format("[CIRCUIT_BREAKER] TRIPPED on unrealized DD ${0:F2} | {1}", ddFromPeak, FormatEventTime()));
                    tradeState = TradeState.ExitPending;
                    if (positionDirection == 1)
                        ExitLong("CIRCUIT_BRK_L", activeSignal);
                    else
                        ExitShort("CIRCUIT_BRK_S", activeSignal);
                    return;
                }
            }

            // Manage exits on every tick while in position
            if (tradeState == TradeState.Long || tradeState == TradeState.Short)
                ManageExitOnTick(e.Price);

            // Check signals when flat
            if (tradeState == TradeState.Flat && !circuitBreakerTripped)
            {
                pendingSignalKind = SignalKind.None;

                int signal = CheckAbsorption(lastEventTimeUtc);
                if (signal != 0)
                    pendingSignalKind = SignalKind.Absorption;

                if (signal == 0)
                {
                    signal = CheckIceberg();
                    if (signal != 0)
                        pendingSignalKind = SignalKind.Iceberg;
                }

                if (signal != 0)
                    TryEntry(signal, pendingSignalKind);
            }
        }

        // =============================================================
        // Trade aggression classification
        // =============================================================
        private bool ClassifyTradeAsAggressiveBuy(double tradePrice)
        {
            if (!double.IsNaN(lastAsk) && tradePrice >= lastAsk)
                return true;
            if (!double.IsNaN(lastBid) && tradePrice <= lastBid)
                return false;
            if (!double.IsNaN(lastBid) && !double.IsNaN(lastAsk))
            {
                double mid = 0.5 * (lastBid + lastAsk);
                return tradePrice >= mid;
            }
            return true;
        }

        // =============================================================
        // Ring buffer management
        // =============================================================
        private void EvictExpiredRing(DateTime now)
        {
            long nowTicks = now.Ticks;
            double windowTicks = ABS_WINDOW_SECONDS * (double)TimeSpan.TicksPerSecond;

            while (ringCount > 0)
            {
                int tailIdx = (ringHead - ringCount + RING_CAPACITY) % RING_CAPACITY;
                ref RingEntry entry = ref ring[tailIdx];

                double ageTicks = nowTicks - entry.TimeUtc.Ticks;
                if (ageTicks <= windowTicks)
                    break;

                if (entry.LevelIdx >= 0)
                    RemoveFromLevel(entry.LevelIdx, entry.Volume, entry.IsAggressiveBuy);

                ringCount--;
            }
        }

        private void AddToRing(DateTime timeUtc, double price, int volume, bool isAggressiveBuy, int levelIdx)
        {
            if (ringCount >= RING_CAPACITY)
            {
                int tailIdx = (ringHead - ringCount + RING_CAPACITY) % RING_CAPACITY;
                ref RingEntry old = ref ring[tailIdx];
                if (old.LevelIdx >= 0)
                    RemoveFromLevel(old.LevelIdx, old.Volume, old.IsAggressiveBuy);
                ringCount--;
            }

            ring[ringHead] = new RingEntry
            {
                TimeUtc         = timeUtc,
                Price           = price,
                Volume          = volume,
                IsAggressiveBuy = isAggressiveBuy,
                LevelIdx        = levelIdx,
            };

            ringHead = (ringHead + 1) % RING_CAPACITY;
            ringCount++;
        }

        // =============================================================
        // Per-level accumulators
        // =============================================================
        private int GetLevelIndex(double price)
        {
            if (!levelBaseSet)
            {
                levelBasePrice = price;
                levelBaseSet = true;
            }

            int offset = (int)Math.Round((price - levelBasePrice) / TickSize);
            int idx = offset + LEVEL_COUNT / 2;

            if (idx < 0 || idx >= LEVEL_COUNT)
            {
                RecenterLevels(price);
                offset = (int)Math.Round((price - levelBasePrice) / TickSize);
                idx = offset + LEVEL_COUNT / 2;
                if (idx < 0 || idx >= LEVEL_COUNT)
                    return -1;
            }

            return idx;
        }

        private void RecenterLevels(double newBasePrice)
        {
            Array.Clear(levels, 0, LEVEL_COUNT);
            levelBasePrice = newBasePrice;

            for (int i = 0; i < ringCount; i++)
            {
                int ringIdx = (ringHead - ringCount + i + RING_CAPACITY) % RING_CAPACITY;
                ref RingEntry entry = ref ring[ringIdx];

                int offset = (int)Math.Round((entry.Price - levelBasePrice) / TickSize);
                int idx = offset + LEVEL_COUNT / 2;

                if (idx >= 0 && idx < LEVEL_COUNT)
                {
                    entry.LevelIdx = idx;
                    AddToLevel(idx, entry.TimeUtc, entry.Volume, entry.IsAggressiveBuy);
                }
                else
                {
                    entry.LevelIdx = -1;
                }
            }
        }

        private void AddToLevel(int idx, DateTime timeUtc, int volume, bool isAggressiveBuy)
        {
            ref LevelAccum lvl = ref levels[idx];
            if (isAggressiveBuy)
            {
                lvl.BuyContracts += volume;
                lvl.BuyCount++;
            }
            else
            {
                lvl.SellContracts += volume;
                lvl.SellCount++;
            }

            if (lvl.FirstEvent == default(DateTime) || timeUtc < lvl.FirstEvent)
                lvl.FirstEvent = timeUtc;
            if (timeUtc > lvl.LastEvent)
                lvl.LastEvent = timeUtc;
        }

        private void RemoveFromLevel(int idx, int volume, bool isAggressiveBuy)
        {
            ref LevelAccum lvl = ref levels[idx];
            if (isAggressiveBuy)
            {
                lvl.BuyContracts -= volume;
                lvl.BuyCount--;
            }
            else
            {
                lvl.SellContracts -= volume;
                lvl.SellCount--;
            }
        }

        private int GetLevelIndexDirect(double price)
        {
            if (!levelBaseSet) return -1;
            int offset = (int)Math.Round((price - levelBasePrice) / TickSize);
            int idx = offset + LEVEL_COUNT / 2;
            return (idx >= 0 && idx < LEVEL_COUNT) ? idx : -1;
        }

        // =============================================================
        // Absorption detection — original
        // =============================================================
        private int CheckAbsorption(DateTime now)
        {
            if (double.IsNaN(lastBid) || double.IsNaN(lastAsk))
                return 0;

            for (int tickOff = 0; tickOff <= ABS_MAX_LEVELS; tickOff++)
            {
                double checkPrice = lastBid - tickOff * TickSize;
                int idx = GetLevelIndexDirect(checkPrice);
                if (idx < 0) continue;

                int sig = EvaluateLevel(idx, checkPrice, now, +1);
                if (sig != 0) return sig;
            }

            for (int tickOff = 0; tickOff <= ABS_MAX_LEVELS; tickOff++)
            {
                double checkPrice = lastAsk + tickOff * TickSize;
                int idx = GetLevelIndexDirect(checkPrice);
                if (idx < 0) continue;

                int sig = EvaluateLevel(idx, checkPrice, now, -1);
                if (sig != 0) return sig;
            }

            return 0;
        }

        private int EvaluateLevel(int idx, double price, DateTime now, int direction)
        {
            ref LevelAccum lvl = ref levels[idx];

            int contracts = direction == +1 ? lvl.SellContracts : lvl.BuyContracts;
            int count     = direction == +1 ? lvl.SellCount     : lvl.BuyCount;

            if (contracts < ABS_MIN_VOLUME)
                return 0;

            if (count < ABS_MIN_TRADES)
                return 0;

            if (lvl.FirstEvent == default(DateTime) || lvl.LastEvent == default(DateTime))
                return 0;

            double holdSec = (lvl.LastEvent - lvl.FirstEvent).TotalSeconds;
            if (holdSec < ABS_HOLD_SECONDS)
                return 0;

            if (direction == +1)
            {
                long bidDepth;
                if (!bidBook.TryGetValue(price, out bidDepth) || bidDepth < ABS_MIN_BOOK_DEPTH)
                    return 0;

                if (lastBid < price - TickSize)
                    return 0;
            }
            else
            {
                long askDepth;
                if (!askBook.TryGetValue(price, out askDepth) || askDepth < ABS_MIN_BOOK_DEPTH)
                    return 0;

                if (lastAsk > price + TickSize)
                    return 0;
            }

            return direction;
        }

        // =============================================================
        // Iceberg detection — original
        // =============================================================
        private int CheckIceberg()
        {
            if (double.IsNaN(lastBid) || double.IsNaN(lastAsk))
                return 0;

            for (int tickOff = 0; tickOff <= ICE_MAX_LEVELS; tickOff++)
            {
                double checkPrice = lastBid - tickOff * TickSize;
                IcebergState ice;
                if (bidIcebergs.TryGetValue(checkPrice, out ice) && ice.RefillCount >= ICE_MIN_CYCLES)
                {
                    if (lastBid >= checkPrice - TickSize)
                    {
                        Print(string.Format("[ICEBERG_BID] {0} cycles @ {1} | depth peak={2} | {3}",
                            ice.RefillCount, checkPrice, ice.PeakDepth, FormatEventTime()));
                        bidIcebergs[checkPrice] = new IcebergState();
                        return +1;
                    }
                }
            }

            for (int tickOff = 0; tickOff <= ICE_MAX_LEVELS; tickOff++)
            {
                double checkPrice = lastAsk + tickOff * TickSize;
                IcebergState ice;
                if (askIcebergs.TryGetValue(checkPrice, out ice) && ice.RefillCount >= ICE_MIN_CYCLES)
                {
                    if (lastAsk <= checkPrice + TickSize)
                    {
                        Print(string.Format("[ICEBERG_ASK] {0} cycles @ {1} | depth peak={2} | {3}",
                            ice.RefillCount, checkPrice, ice.PeakDepth, FormatEventTime()));
                        askIcebergs[checkPrice] = new IcebergState();
                        return -1;
                    }
                }
            }

            return 0;
        }

        // =============================================================
        // TryEntry — original gates + new regime gate
        // =============================================================
        private void TryEntry(int direction, SignalKind signalKind)
        {
            if (!IsInSessionWindow())
                return;

            if (circuitBreakerTripped)
                return;

            if (lastEntryTimeUtc != DateTime.MinValue)
            {
                double secsSinceEntry = (lastEventTimeUtc - lastEntryTimeUtc).TotalSeconds;
                if (secsSinceEntry < GATE_MIN_SECONDS_FLAT)
                    return;
            }

            // --- Original condition gate 1: Book imbalance confirms direction ---
            double bidDepthSum = SumBookDepth(bidBook, lastBid, GATE_BOOK_LEVELS, -1);
            double askDepthSum = SumBookDepth(askBook, lastAsk, GATE_BOOK_LEVELS, +1);

            if (bidDepthSum > 0 && askDepthSum > 0)
            {
                if (direction == +1)
                {
                    double ratio = bidDepthSum / askDepthSum;
                    if (ratio < GATE_BOOK_IMBAL_MIN)
                        return;
                }
                else
                {
                    double ratio = askDepthSum / bidDepthSum;
                    if (ratio < GATE_BOOK_IMBAL_MIN)
                        return;
                }
            }

            // --- Original condition gate 2: Price range — skip chop ---
            double rangeHigh = double.MinValue;
            double rangeLow  = double.MaxValue;
            for (int i = 0; i < ringCount; i++)
            {
                int ringIdx = (ringHead - ringCount + i + RING_CAPACITY) % RING_CAPACITY;
                ref RingEntry entry = ref ring[ringIdx];
                if (entry.Price > rangeHigh) rangeHigh = entry.Price;
                if (entry.Price < rangeLow)  rangeLow  = entry.Price;
            }
            if (rangeHigh > double.MinValue && rangeLow < double.MaxValue)
            {
                double rangeTicks = (rangeHigh - rangeLow) / TickSize;
                if (rangeTicks < GATE_RANGE_MIN_TICKS)
                    return;
            }

            // --- Original condition gate 3: Delta flow confirms direction ---
            int netDelta = 0;
            for (int i = 0; i < ringCount; i++)
            {
                int ringIdx = (ringHead - ringCount + i + RING_CAPACITY) % RING_CAPACITY;
                ref RingEntry entry = ref ring[ringIdx];
                if (entry.IsAggressiveBuy)
                    netDelta += entry.Volume;
                else
                    netDelta -= entry.Volume;
            }

            if (direction == +1 && netDelta < GATE_DELTA_MIN)
                return;
            if (direction == -1 && netDelta > -GATE_DELTA_MIN)
                return;

            // --- Original condition gate 4: Post-loss — require book imbalance flip ---
            if (lastLossDirection != 0 && lastLossDirection == direction)
            {
                double imbalRatio = direction == +1
                    ? (askDepthSum > 0 ? bidDepthSum / askDepthSum : 0)
                    : (bidDepthSum > 0 ? askDepthSum / bidDepthSum : 0);
                if (imbalRatio < GATE_BOOK_IMBAL_MIN * 2.0)
                    return;
            }

            // --- New market-condition gate ---
            if (!PassMarketConditionGate(direction, signalKind, bidDepthSum, askDepthSum, netDelta))
                return;

            Print(string.Format(
                "[GATE_PASS] dir={0} sig={1} bidDepth={2:F0} askDepth={3:F0} delta={4} range={5:F0}t regime={6} | {7}",
                direction,
                signalKind,
                bidDepthSum,
                askDepthSum,
                netDelta,
                (rangeHigh - rangeLow) / TickSize,
                lastMcSnapshot != null ? lastMcSnapshot.Regime.ToString() : "Unknown",
                FormatEventTime()
            ));

            SubmitEntry(direction > 0 ? MarketPosition.Long : MarketPosition.Short);
        }

        private bool PassMarketConditionGate(int direction, SignalKind signalKind, double bidDepthSum, double askDepthSum, int netDelta)
        {
            // Warm-up behavior: until a snapshot exists, preserve original strategy behavior.
            if (lastMcSnapshot == null)
                return true;

            double alignedTrend = direction > 0 ? lastMcSnapshot.TrendUpScore : lastMcSnapshot.TrendDownScore;
            double opposingTrend = direction > 0 ? lastMcSnapshot.TrendDownScore : lastMcSnapshot.TrendUpScore;
            double alignedAbsorption = direction > 0 ? lastMcSnapshot.AbsorptionBidScore : lastMcSnapshot.AbsorptionAskScore;
            double imbalanceRatio = direction > 0
                ? (askDepthSum > 0 ? bidDepthSum / askDepthSum : 0.0)
                : (bidDepthSum > 0 ? askDepthSum / bidDepthSum : 0.0);

            if (lastMcSnapshot.ChaosScore >= MC_BLOCK_CHAOS_SCORE || lastMcSnapshot.Regime == MarketRegime.Chaotic)
            {
                Print("[MC_BLOCK] chaotic regime | " + lastMcSnapshot.ToString() + " | " + FormatEventTime());
                return false;
            }

            if (lastMcSnapshot.SpoofScore >= MC_BLOCK_SPOOF_SCORE || lastMcSnapshot.Regime == MarketRegime.Spoofy)
            {
                Print("[MC_BLOCK] spoofy regime | " + lastMcSnapshot.ToString() + " | " + FormatEventTime());
                return false;
            }

            if (opposingTrend >= MC_BLOCK_OPPOSING_TREND_SCORE)
            {
                Print("[MC_BLOCK] strong opposing directional pressure | " + lastMcSnapshot.ToString() + " | " + FormatEventTime());
                return false;
            }

            // Thin markets are fragile: require stronger directional alignment + stronger book imbalance.
            if (lastMcSnapshot.Regime == MarketRegime.Thin || lastMcSnapshot.ThinScore >= 0.70)
            {
                if (alignedTrend < MC_THIN_REQUIRED_TREND_SCORE)
                {
                    Print("[MC_BLOCK] thin market without aligned directional pressure | " + lastMcSnapshot.ToString() + " | " + FormatEventTime());
                    return false;
                }

                if (imbalanceRatio < GATE_BOOK_IMBAL_MIN * MC_THIN_EXTRA_IMBAL_MULT)
                {
                    Print("[MC_BLOCK] thin market requires stronger book imbalance | " + lastMcSnapshot.ToString() + " | " + FormatEventTime());
                    return false;
                }
            }

            // Absorption / iceberg are reversal-ish signals. Best regimes:
            // - balanced,
            // - aligned absorptive,
            // - aligned directional if the book is leaning that way.
            if (signalKind == SignalKind.Absorption || signalKind == SignalKind.Iceberg)
            {
                bool balancedOkay = lastMcSnapshot.BalanceScore >= 0.45;
                bool absorptionOkay = alignedAbsorption >= MC_ALLOW_ABSORPTION_SCORE;
                bool directionalOkay = alignedTrend >= MC_ALLOW_DIRECTIONAL_SCORE;

                if (!(balancedOkay || absorptionOkay || directionalOkay))
                {
                    Print("[MC_BLOCK] signal not supported by regime context | " + lastMcSnapshot.ToString() + " | " + FormatEventTime());
                    return false;
                }
            }

            // Direction-specific sanity check using recent delta direction.
            if (direction > 0 && netDelta <= 0 && alignedTrend < MC_ALLOW_DIRECTIONAL_SCORE && alignedAbsorption < MC_ALLOW_ABSORPTION_SCORE)
            {
                Print("[MC_BLOCK] long lacks net-flow / regime confirmation | " + lastMcSnapshot.ToString() + " | " + FormatEventTime());
                return false;
            }

            if (direction < 0 && netDelta >= 0 && alignedTrend < MC_ALLOW_DIRECTIONAL_SCORE && alignedAbsorption < MC_ALLOW_ABSORPTION_SCORE)
            {
                Print("[MC_BLOCK] short lacks net-flow / regime confirmation | " + lastMcSnapshot.ToString() + " | " + FormatEventTime());
                return false;
            }

            return true;
        }

        // =============================================================
        // SumBookDepth — original
        // =============================================================
        private double SumBookDepth(Dictionary<double, long> book, double startPrice, int numLevels, int stepDir)
        {
            if (double.IsNaN(startPrice)) return 0;
            double total = 0;
            for (int i = 0; i < numLevels; i++)
            {
                double px = startPrice + i * stepDir * TickSize;
                long depth;
                if (book.TryGetValue(px, out depth))
                    total += depth;
            }
            return total;
        }

        // =============================================================
        // SubmitEntry — original
        // =============================================================
        private void SubmitEntry(MarketPosition side)
        {
            activeSignal = string.Format(
                "CGMC_{0}_{1}_{2:HHmmssfff}",
                side == MarketPosition.Long ? "L" : "S",
                ++signalCounter,
                DateTime.UtcNow
            );

            signalPrice = lastTradePrice;

            double slowAtr = atrSlow != null ? atrSlow[0] : 0;

            if (slowAtr > 0)
            {
                entrySlTicks = Math.Round(slowAtr * ATR_SL_MULT / TickSize);
                entryTpTicks = Math.Round(slowAtr * ATR_TP_MULT / TickSize);

                if (MAX_SL_TICKS > 0 && entrySlTicks > MAX_SL_TICKS)
                    entrySlTicks = MAX_SL_TICKS;

                entryAtr = slowAtr;
            }
            else
            {
                entrySlTicks = 20;
                entryTpTicks = 60;
                entryAtr = 0;
            }

            if (entrySlTicks < 1) entrySlTicks = 1;
            if (entryTpTicks < 1) entryTpTicks = 1;

            double brokerSlTicks = Math.Max(entrySlTicks, MIN_BROKER_SL_TICKS);
            SetStopLoss(CalculationMode.Ticks, brokerSlTicks);
            SetProfitTarget(CalculationMode.Ticks, entryTpTicks);

            entryOrder = null;
            filledQty = 0;
            avgFillPrice = 0.0;
            needStopTighten = false;

            tradeState = TradeState.EntryPending;

            if (!double.IsNaN(lastAsk) && !double.IsNaN(lastBid))
            {
                double limitPrice;
                if (side == MarketPosition.Long)
                {
                    limitPrice = lastAsk + LIMIT_ENTRY_MAX_SLIP_TICKS * TickSize;
                    limitPrice = Instrument.MasterInstrument.RoundToTickSize(limitPrice);
                    entryOrder = EnterLongLimit(1, limitPrice, activeSignal);
                }
                else
                {
                    limitPrice = lastBid - LIMIT_ENTRY_MAX_SLIP_TICKS * TickSize;
                    limitPrice = Instrument.MasterInstrument.RoundToTickSize(limitPrice);
                    entryOrder = EnterShortLimit(1, limitPrice, activeSignal);
                }
                entrySubmitTimeUtc = lastEventTimeUtc;
                Print(string.Format("[LIMIT_ENTRY] {0} limit@{1} | ask={2} bid={3} | bidBook={4} askBook={5} | {6}",
                    side == MarketPosition.Long ? "BUY" : "SELL",
                    limitPrice,
                    lastAsk,
                    lastBid,
                    bidBook.Count,
                    askBook.Count,
                    FormatEventTime()));
            }
            else
            {
                if (side == MarketPosition.Long)
                    entryOrder = EnterLong(1, activeSignal);
                else
                    entryOrder = EnterShort(1, activeSignal);
                entrySubmitTimeUtc = lastEventTimeUtc;
            }
        }

        // =============================================================
        // OnBarUpdate — original
        // =============================================================
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (cancelPendingEntry && tradeState == TradeState.EntryPending && entryOrder != null)
            {
                CancelOrder(entryOrder);
                cancelPendingEntry = false;
                return;
            }
            cancelPendingEntry = false;

            if (tradeState == TradeState.EntryPending && entryOrder != null
                && LIMIT_ENTRY_TIMEOUT_MS > 0
                && !limitCancelRequested
                && entrySubmitTimeUtc > DateTime.MinValue
                && lastEventTimeUtc > DateTime.MinValue)
            {
                double elapsedMs = (lastEventTimeUtc - entrySubmitTimeUtc).TotalMilliseconds;
                if (elapsedMs >= LIMIT_ENTRY_TIMEOUT_MS)
                {
                    limitCancelRequested = true;
                    CancelOrder(entryOrder);
                    return;
                }
            }

            if (tradeState != TradeState.Long && tradeState != TradeState.Short)
                return;

            if (needStopTighten)
            {
                double actualSlPrice;
                if (positionDirection == 1)
                    actualSlPrice = Instrument.MasterInstrument.RoundToTickSize(avgFillPrice - entrySlTicks * TickSize);
                else
                    actualSlPrice = Instrument.MasterInstrument.RoundToTickSize(avgFillPrice + entrySlTicks * TickSize);

                SetStopLoss(CalculationMode.Price, actualSlPrice);
                Print(string.Format("[STOP_TIGHTEN] Broker stop -> {0} ({1}t from fill) | {2}",
                    actualSlPrice, entrySlTicks, FormatEventTime()));
                needStopTighten = false;
                brokerStopAtRealSl = true;
            }

            double price = Close[0];
            int position = positionDirection;
            if (position == 0) return;

            if (position == 1)
            {
                if (double.IsNaN(trailHighPx) || price > trailHighPx)
                    trailHighPx = price;
            }
            else
            {
                if (double.IsNaN(trailHighPx) || price < trailHighPx)
                    trailHighPx = price;
            }

            double profitTicks = (trailHighPx - avgFillPrice) * position / TickSize;

            if (!trailBeActivated && profitTicks >= TRAIL_BE_TRIGGER_TICKS)
            {
                double bePx;
                if (position == 1)
                    bePx = Instrument.MasterInstrument.RoundToTickSize(avgFillPrice + TRAIL_BE_PLUS_TICKS * TickSize);
                else
                    bePx = Instrument.MasterInstrument.RoundToTickSize(avgFillPrice - TRAIL_BE_PLUS_TICKS * TickSize);
                SetStopLoss(CalculationMode.Price, bePx);
                trailBeActivated = true;
                trailActive = true;
                Print(string.Format("[TRAIL_BE] Activated at {0:F1}t profit, SL -> BE+{1}t @ {2} | {3}",
                    profitTicks, TRAIL_BE_PLUS_TICKS, bePx, FormatEventTime()));
            }

            if (!trailLock1Activated && profitTicks >= TRAIL_LOCK1_TRIGGER_TICKS)
            {
                double lockPx;
                if (position == 1)
                    lockPx = Instrument.MasterInstrument.RoundToTickSize(avgFillPrice + TRAIL_LOCK1_PROFIT_TICKS * TickSize);
                else
                    lockPx = Instrument.MasterInstrument.RoundToTickSize(avgFillPrice - TRAIL_LOCK1_PROFIT_TICKS * TickSize);

                SetStopLoss(CalculationMode.Price, lockPx);
                trailLock1Activated = true;
                Print(string.Format("[TRAIL_LOCK1] Activated at {0:F1}t profit, SL -> {1} (lock {2}t) | {3}",
                    profitTicks, lockPx, TRAIL_LOCK1_PROFIT_TICKS, FormatEventTime()));
            }

            if (trailLock1Activated && entryAtr > 0)
            {
                double trailDist = entryAtr * TRAIL_RUNNER_ATR_MULT;
                double trailSlPx;

                if (position == 1)
                {
                    trailSlPx = Instrument.MasterInstrument.RoundToTickSize(trailHighPx - trailDist);
                    if (trailSlPx > avgFillPrice + TRAIL_LOCK1_PROFIT_TICKS * TickSize)
                        SetStopLoss(CalculationMode.Price, trailSlPx);
                }
                else
                {
                    trailSlPx = Instrument.MasterInstrument.RoundToTickSize(trailHighPx + trailDist);
                    if (trailSlPx < avgFillPrice - TRAIL_LOCK1_PROFIT_TICKS * TickSize)
                        SetStopLoss(CalculationMode.Price, trailSlPx);
                }
            }
        }

        // =============================================================
        // ManageExitOnTick — original
        // =============================================================
        private void ManageExitOnTick(double price)
        {
            if (tradeState != TradeState.Long && tradeState != TradeState.Short)
                return;

            int position = positionDirection;

            if (!brokerStopAtRealSl && entrySlTicks > 0 && avgFillPrice > 0 && TickSize > 0)
            {
                double adverseTicks = (price - avgFillPrice) * -position / TickSize;
                if (adverseTicks >= entrySlTicks)
                {
                    Print(string.Format("[SOFT_SL] {0:F0}t adverse >= {1}t SL | {2}", adverseTicks, entrySlTicks, FormatEventTime()));
                    tradeState = TradeState.ExitPending;
                    if (position == 1)
                        ExitLong("SOFT_SL_L", activeSignal);
                    else
                        ExitShort("SOFT_SL_S", activeSignal);
                    return;
                }
            }

            if (entryTpTicks > 0 && avgFillPrice > 0 && TickSize > 0)
            {
                double favorTicks = (price - avgFillPrice) * position / TickSize;
                if (favorTicks >= entryTpTicks)
                {
                    Print(string.Format("[SOFT_TP] {0:F0}t favorable >= {1}t target | {2}", favorTicks, entryTpTicks, FormatEventTime()));
                    tradeState = TradeState.ExitPending;
                    if (position == 1)
                        ExitLong("SOFT_TP_L", activeSignal);
                    else
                        ExitShort("SOFT_TP_S", activeSignal);
                    return;
                }
            }
        }

        // =============================================================
        // OnOrderUpdate — original
        // =============================================================
        protected override void OnOrderUpdate(
            Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice,
            OrderState orderState, DateTime time,
            ErrorCode error, string comment)
        {
            if (order == null)
                return;

            if (entryOrder == null
                && !string.IsNullOrEmpty(order.Name)
                && order.Name == activeSignal)
            {
                entryOrder = order;
            }

            if (entryOrder != null && order.OrderId == entryOrder.OrderId)
            {
                if (orderState == OrderState.Rejected || error != ErrorCode.NoError)
                {
                    Print(string.Format("[ENTRY_ERR] {0} state={1} err={2} {3}", activeSignal, orderState, error, comment));
                    ResetTradeState();
                    return;
                }
                if (orderState == OrderState.Cancelled && filled == 0)
                {
                    ResetTradeState();
                    return;
                }
            }

            if (orderState == OrderState.Rejected && order.Name != null
                && order.Name != activeSignal)
            {
                Print(string.Format("[BRACKET_REJECTED] {0} {1} err={2} {3} | {4}",
                    order.Name, order.OrderType, error, comment, FormatEventTime()));
                if (order.OrderType == OrderType.StopMarket && brokerStopAtRealSl)
                {
                    Print("[STOP_TIGHTEN_FAILED] Re-enabling SOFT_SL | " + FormatEventTime());
                    brokerStopAtRealSl = false;
                }
            }
        }

        // =============================================================
        // OnExecutionUpdate — original
        // =============================================================
        protected override void OnExecutionUpdate(
            Execution execution, string executionId,
            double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            Order order = execution.Order;

            if (order.Name == activeSignal)
            {
                double gross = avgFillPrice * filledQty + price * quantity;
                filledQty += quantity;
                avgFillPrice = filledQty > 0 ? gross / filledQty : 0.0;

                if (filledQty >= order.Quantity)
                {
                    tradeState = order.OrderAction == OrderAction.Buy
                        ? TradeState.Long
                        : TradeState.Short;
                    positionDirection = tradeState == TradeState.Long ? 1 : -1;

                    lastEntryTimeUtc = lastEventTimeUtc;
                    entryTimeUtc = lastEventTimeUtc;
                    tradesToday++;

                    if (entrySlTicks < MIN_BROKER_SL_TICKS)
                        needStopTighten = true;

                    string side = tradeState == TradeState.Long ? "LONG" : "SHORT";
                    double slPx = positionDirection == 1
                        ? avgFillPrice - entrySlTicks * TickSize
                        : avgFillPrice + entrySlTicks * TickSize;
                    double tpPx = positionDirection == 1
                        ? avgFillPrice + entryTpTicks * TickSize
                        : avgFillPrice - entryTpTicks * TickSize;

                    Print(string.Format("[OPEN] {0} @ {1} | SL={2} ({3}t) TP={4} ({5}t) ATR={6:F2} regime={7} | #{8} {9}",
                        side, avgFillPrice, slPx, entrySlTicks, tpPx, entryTpTicks, entryAtr,
                        lastMcSnapshot != null ? lastMcSnapshot.Regime.ToString() : "Unknown",
                        tradesToday, FormatEventTime()));

                    if (!double.IsNaN(signalPrice) && TickSize > 0)
                    {
                        double slipTicks = (avgFillPrice - signalPrice) * positionDirection / TickSize;
                        Print(string.Format("[FILL_QUALITY] slip={0:F1}t signal@{1} fill@{2} | {3}",
                            slipTicks, signalPrice, avgFillPrice, FormatEventTime()));
                    }
                }
                return;
            }

            if (order.Name != null
                && (order.Name.StartsWith("SOFT_SL") || order.Name.StartsWith("SOFT_TP")
                    || order.Name.StartsWith("CIRCUIT_BRK")))
            {
                string reason = order.Name.StartsWith("CIRCUIT_BRK") ? "CIRCUIT_BRK"
                    : order.Name.StartsWith("SOFT_SL") ? "SOFT_SL"
                    : "SOFT_TP";
                LogClose(reason, price);
                ResetTradeState();
                return;
            }

            if (tradeState == TradeState.Long || tradeState == TradeState.Short
                || tradeState == TradeState.ExitPending)
            {
                string reason;
                if (order.OrderType == OrderType.StopMarket)
                    reason = trailActive ? "TRAIL" : "STOP";
                else if (order.OrderType == OrderType.Limit)
                    reason = "TARGET";
                else
                    reason = "EXIT";

                LogClose(reason, price);
                ResetTradeState();
                return;
            }
        }

        // =============================================================
        // Time helpers
        // =============================================================
        private DateTime GetEasternTime()
        {
            if (lastEventTimeUtc == DateTime.MinValue)
                return DateTime.MinValue;
            if (lastEventTimeUtc.Kind == DateTimeKind.Utc)
                return TimeZoneInfo.ConvertTimeFromUtc(lastEventTimeUtc, easternTz);
            return TimeZoneInfo.ConvertTime(lastEventTimeUtc, easternTz);
        }

        private string FormatEventTime()
        {
            if (lastEventTimeUtc == DateTime.MinValue) return "";
            return GetEasternTime().ToString("HH:mm:ss");
        }

        // =============================================================
        // Session window check
        // =============================================================
        private bool IsInSessionWindow()
        {
            DateTime eastern = GetEasternTime();
            if (eastern == DateTime.MinValue)
                return false;

            TimeSpan localTime = eastern.TimeOfDay;
            TimeSpan start = new TimeSpan(SESSION_START_HOUR, SESSION_START_MINUTE, 0);
            TimeSpan end = new TimeSpan(SESSION_END_HOUR, SESSION_END_MINUTE, 0);

            if (localTime < start || localTime > end)
                return false;

            TimeSpan lunchStart = new TimeSpan(LUNCH_START_HOUR, LUNCH_START_MINUTE, 0);
            TimeSpan lunchEnd = new TimeSpan(LUNCH_END_HOUR, LUNCH_END_MINUTE, 0);
            if (localTime >= lunchStart && localTime <= lunchEnd)
                return false;

            return true;
        }

        // =============================================================
        // Daily risk rollover
        // =============================================================
        private void CheckDayRollover()
        {
            DateTime eastern = GetEasternTime();
            if (eastern == DateTime.MinValue) return;

            DateTime today = eastern.Date;
            if (today != currentTradeDay)
            {
                if (currentTradeDay != DateTime.MinValue)
                    Print(string.Format("[DAY_RESET] {0:yyyy-MM-dd} trades={1} pnl=${2:F2}",
                        currentTradeDay, tradesToday, dailyPnlUsd));

                currentTradeDay = today;
                tradesToday = 0;
                dailyPnlUsd = 0.0;
            }
        }

        // =============================================================
        // Helpers
        // =============================================================
        private void ResetTradeState()
        {
            tradeState = TradeState.Flat;
            entryOrder = null;
            filledQty = 0;
            avgFillPrice = 0.0;
            activeSignal = string.Empty;
            entryAtr = 0.0;
            entrySlTicks = 0;
            entryTpTicks = 0;
            positionDirection = 0;
            entryTimeUtc = DateTime.MinValue;
            trailHighPx = double.NaN;
            trailBeActivated = false;
            trailLock1Activated = false;
            trailActive = false;
            signalPrice = double.NaN;
            entrySubmitTimeUtc = DateTime.MinValue;
            needStopTighten = false;
            cancelPendingEntry = false;
            limitCancelRequested = false;
            brokerStopAtRealSl = false;
            pendingSignalKind = SignalKind.None;
        }

        private void CheckCircuitBreaker(double equity)
        {
            if (MAX_DRAWDOWN_USD >= 0 || circuitBreakerTripped)
                return;

            equityHighWater = Math.Max(equityHighWater, equity);
            double ddFromPeak = equity - equityHighWater;
            if (ddFromPeak <= MAX_DRAWDOWN_USD)
            {
                circuitBreakerTripped = true;
                Print(string.Format("[CIRCUIT_BREAKER] TRIPPED! DD ${0:F2} | peak ${1:F0} current ${2:F0} | {3}",
                    ddFromPeak, equityHighWater, equity, FormatEventTime()));
            }
        }

        private double ComputePnlTicks(double exitPrice)
        {
            if (avgFillPrice == 0 || TickSize == 0) return 0;
            return (exitPrice - avgFillPrice) * positionDirection / TickSize;
        }

        private void LogClose(string reason, double exitPrice)
        {
            double pnlTicks = ComputePnlTicks(exitPrice);
            double tickValue = Instrument.MasterInstrument.PointValue * TickSize;
            double grossPnl = pnlTicks * tickValue * 1;
            double netPnl = grossPnl - COMMISSION_PER_RT;
            tradeCount++;
            cumNetPnl += netPnl;
            dailyPnlUsd += netPnl;

            if (netPnl < 0)
                lastLossDirection = positionDirection;
            else
                lastLossDirection = 0;

            double equity = STARTING_CAPITAL + cumNetPnl;
            equityHighWater = Math.Max(equityHighWater, equity);
            Print(string.Format("[CLOSE] {0} @ {1} | {2:F1}t ${3:F2} | cum ${4:F2} eq ${5:F0} #{6} day ${7:F2} regime={8} | {9}",
                reason,
                exitPrice,
                pnlTicks,
                netPnl,
                cumNetPnl,
                equity,
                tradeCount,
                dailyPnlUsd,
                lastMcSnapshot != null ? lastMcSnapshot.Regime.ToString() : "Unknown",
                FormatEventTime()));

            CheckCircuitBreaker(equity);
        }
    }
}
