// CG_MNQ_CHBlueprintExec_v1.cs
// NinjaTrader 8 Strategy
// Generated: 2026-05-09 13:05:00 America/New_York
//
// PURPOSE
//   Return the NinjaTrader work to the successful ClickHouse deployment blueprint.
//
// BACKGROUND
//   The NT v1.13-v1.16 path proved the execution shell, but the signal logic drifted
//   into improvised proxy alpha:
//     - raw momentum proxy
//     - opening impulse proxy
//     - rolling BOS/CHOCH proxy
//
//   That drift was wrong. The ClickHouse research already produced the deployable
//   blueprint we are trying to implement:
//
//     - 1 MNQ only
//     - no overlapping positions
//     - RTH-only entries
//     - spread-filtered executable model
//     - session/side allowlist
//     - 80 tick target
//     - 16 tick stop
//     - 120 second timeout
//     - CH-style trade/decision telemetry
//
//   This file makes that the mainline again.
//
// IMPORTANT HONESTY NOTE
//   This NinjaScript cannot magically access ClickHouse tables from inside NinjaTrader.
//   Therefore v1 has two modes:
//
//   1. CsvBlueprintSignals
//      Reads an exported ClickHouse signal CSV and executes those blueprint signals.
//      This is the closest possible match to the CH deploy candidate when the replay
//      date/instrument matches the exported signal file.
//
//   2. LiveBlueprintProxy
//      Uses a deliberately minimal live proxy for the CH signal layer while preserving
//      the CH execution/risk/governance contract. This is NOT new alpha. It is a
//      placeholder for the unavailable live CH feature stack.
//
//   The intended final path is to replace LiveBlueprintProxy with exact live features
//   derived from the successful CH tables, not to invent a separate BOS strategy.
//
// CH BLUEPRINT CONTRACT IMPLEMENTED HERE
//   - Quantity hardcoded to 1.
//   - No overlap / no scale-in.
//   - RTH entry gate.
//   - Optional premarket context observation only.
//   - Session-side allowlist.
//   - Spread gate at entry.
//   - Runtime spread emergency exit.
//   - TargetTicks default 80.
//   - StopTicks default 16.
//   - MaxHoldSeconds default 120.
//   - Daily loss lock.
//   - Stop-streak lockout.
//   - Max trades per day and per phase.
//   - Telemetry with CH-like field names.
//
// RECOMMENDED FIRST RUN
//   Strategy tab:
//     Instrument     = MNQ JUN26
//     Account        = Playback101
//     Data series    = 1 Minute
//     Trading hours  = CME US Index Futures ETH or Default 24x7
//     Playback start = 08:00 or 08:30
//
//   Parameters:
//     SignalSource = LiveBlueprintProxy
//     TargetTicks = 80
//     StopTicks = 16
//     MaxHoldSeconds = 120
//     MaxEntrySpreadTicks = 3
//     MaxRuntimeSpreadTicks = 6
//     RequireRth = true
//     RthStartTime = 93000
//     RthEndTime = 160000
//     PremarketStartTime = 40000
//     UsePremarketContextFilter = false for first mechanical run
//
// CSV MODE EXPECTED COLUMNS
//   If SignalSource = CsvBlueprintSignals, CSV must have a header and at least:
//     entry_time_ny,signal_side
//
//   Optional columns:
//     model_variant,session_phase,entry_price_exec,target_price,stop_price
//
//   Example entry_time_ny format:
//     2026-04-28 10:51:26.000
//     2026-04-28 10:51:26
//
// TELEMETRY LOCATION
//   Documents\NinjaTrader 8\bin\Custom\Strategies\CG_MNQ_CHBlueprintExec_v1_telemetry.csv
//
// NOTES FOR NEXT VERSION
//   v2 should port exact ClickHouse feature names and thresholds into NT:
//     - deploy table source
//     - model_variant
//     - session_phase side logic
//     - spread model
//     - signal-side rules
//     - regime/rank filters
//
//   Until then, the execution contract is the blueprint; the live signal proxy is
//   explicitly subordinate.
//

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_MNQ_CHBlueprintExec_v1 : Strategy
    {
        private const int HardQuantity = 1;
        private const double MnqTickSize = 0.25;

        public enum SignalSourceMode
        {
            LiveBlueprintProxy,
            CsvBlueprintSignals,
            Disabled
        }

        private class CsvSignal
        {
            public DateTime EntryTimeNy;
            public string SignalSide;
            public string ModelVariant;
            public string SessionPhase;
            public double EntryPriceExec;
            public double TargetPrice;
            public double StopPrice;
            public bool Fired;
        }

        private readonly List<CsvSignal> csvSignals = new List<CsvSignal>();
        private int nextCsvIndex = 0;

        private Order activeEntryOrder;
        private DateTime activeEntryTime = Core.Globals.MinDate;
        private DateTime lastEntryTime = Core.Globals.MinDate;
        private DateTime lastExitTime = Core.Globals.MinDate;
        private DateTime stopStreakLockedUntil = Core.Globals.MinDate;
        private DateTime lastStatusPrintTime = Core.Globals.MinDate;
        private DateTime currentSessionDate = Core.Globals.MinDate;

        private double sessionStartCumProfit = 0.0;
        private int sessionTradeCount = 0;
        private int openTradeCount = 0;
        private int morningTradeCount = 0;
        private int middayTradeCount = 0;
        private int afternoonTradeCount = 0;
        private int powerTradeCount = 0;
        private int consecutiveStopLosses = 0;
        private bool dailyLocked = false;
        private bool telemetryHeaderWritten = false;

        private double premarketHigh = double.MinValue;
        private double premarketLow = double.MaxValue;
        private double premarketMid = 0.0;
        private double premarketOpen = 0.0;
        private double premarketLast = 0.0;
        private bool premarketSeen = false;

        private long blockedNotRth = 0;
        private long blockedSpread = 0;
        private long blockedNotFlat = 0;
        private long blockedCooldown = 0;
        private long blockedSession = 0;
        private long blockedSignal = 0;
        private long blockedAccount = 0;
        private long entriesSubmitted = 0;
        private long csvSignalsLoaded = 0;
        private long csvSignalsSkipped = 0;

        [NinjaScriptProperty]
        [Display(Name = "SignalSource", GroupName = "01 Signal Source", Order = 1)]
        public SignalSourceMode SignalSource { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CsvSignalPath", GroupName = "01 Signal Source", Order = 2)]
        public string CsvSignalPath { get; set; }

        [NinjaScriptProperty]
        [Range(0, 600)]
        [Display(Name = "CsvEntryToleranceSeconds", GroupName = "01 Signal Source", Order = 3)]
        public int CsvEntryToleranceSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseCsvSideOnlyIgnorePrice", GroupName = "01 Signal Source", Order = 4)]
        public bool UseCsvSideOnlyIgnorePrice { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "PremarketStartTime", GroupName = "02 Time", Order = 10)]
        public int PremarketStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RthStartTime", GroupName = "02 Time", Order = 11)]
        public int RthStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RthEndTime", GroupName = "02 Time", Order = 12)]
        public int RthEndTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireRth", GroupName = "02 Time", Order = 13)]
        public bool RequireRth { get; set; }

        [NinjaScriptProperty]
        [Range(0, 30)]
        [Display(Name = "NoTradeFirstMinutes", GroupName = "02 Time", Order = 14)]
        public int NoTradeFirstMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UsePremarketContextFilter", GroupName = "03 Live Proxy", Order = 20)]
        public bool UsePremarketContextFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "ProxyMomentumLookbackMinutes", GroupName = "03 Live Proxy", Order = 21)]
        public int ProxyMomentumLookbackMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 300)]
        [Display(Name = "ProxyMinMomentumTicks", GroupName = "03 Live Proxy", Order = 22)]
        public int ProxyMinMomentumTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 300)]
        [Display(Name = "ProxyMinRangeExpansionTicks", GroupName = "03 Live Proxy", Order = 23)]
        public int ProxyMinRangeExpansionTicks { get; set; }

        [NinjaScriptProperty]
        [Range(5, 120)]
        [Display(Name = "ProxyRecentRangeMinutes", GroupName = "03 Live Proxy", Order = 24)]
        public int ProxyRecentRangeMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowOpenLong", GroupName = "04 Session Side Allowlist", Order = 30)]
        public bool AllowOpenLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowOpenShort", GroupName = "04 Session Side Allowlist", Order = 31)]
        public bool AllowOpenShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMorningLong", GroupName = "04 Session Side Allowlist", Order = 32)]
        public bool AllowMorningLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMorningShort", GroupName = "04 Session Side Allowlist", Order = 33)]
        public bool AllowMorningShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMiddayLong", GroupName = "04 Session Side Allowlist", Order = 34)]
        public bool AllowMiddayLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMiddayShort", GroupName = "04 Session Side Allowlist", Order = 35)]
        public bool AllowMiddayShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowAfternoonLong", GroupName = "04 Session Side Allowlist", Order = 36)]
        public bool AllowAfternoonLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowAfternoonShort", GroupName = "04 Session Side Allowlist", Order = 37)]
        public bool AllowAfternoonShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowPowerLong", GroupName = "04 Session Side Allowlist", Order = 38)]
        public bool AllowPowerLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowPowerShort", GroupName = "04 Session Side Allowlist", Order = 39)]
        public bool AllowPowerShort { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "TargetTicks", GroupName = "05 Risk", Order = 40)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "StopTicks", GroupName = "05 Risk", Order = 41)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 900)]
        [Display(Name = "MaxHoldSeconds", GroupName = "05 Risk", Order = 42)]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxEntrySpreadTicks", GroupName = "06 Execution", Order = 50)]
        public int MaxEntrySpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxRuntimeSpreadTicks", GroupName = "06 Execution", Order = 51)]
        public int MaxRuntimeSpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1800)]
        [Display(Name = "CooldownSecondsAfterExit", GroupName = "07 Anti-Churn", Order = 60)]
        public int CooldownSecondsAfterExit { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1800)]
        [Display(Name = "MinSecondsBetweenEntries", GroupName = "07 Anti-Churn", Order = 61)]
        public int MinSecondsBetweenEntries { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxTradesPerDay", GroupName = "08 Daily Governance", Order = 70)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MaxTradesPerSessionPhase", GroupName = "08 Daily Governance", Order = 71)]
        public int MaxTradesPerSessionPhase { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxConsecutiveStopLosses", GroupName = "08 Daily Governance", Order = 72)]
        public int MaxConsecutiveStopLosses { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name = "StopStreakLockoutMinutes", GroupName = "08 Daily Governance", Order = 73)]
        public int StopStreakLockoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "DailyLossLockUsd", GroupName = "08 Daily Governance", Order = 74)]
        public double DailyLossLockUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableDailyLossLock", GroupName = "08 Daily Governance", Order = 75)]
        public bool EnableDailyLossLock { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequirePlaybackOrSimAccount", GroupName = "09 Safety", Order = 80)]
        public bool RequirePlaybackOrSimAccount { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TelemetryCsvPath", GroupName = "10 Telemetry", Order = 90)]
        public string TelemetryCsvPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "WriteTelemetry", GroupName = "10 Telemetry", Order = 91)]
        public bool WriteTelemetry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", GroupName = "10 Telemetry", Order = 92)]
        public bool PrintDiagnostics { get; set; }

        [NinjaScriptProperty]
        [Range(5, 300)]
        [Display(Name = "StatusPrintSeconds", GroupName = "10 Telemetry", Order = 93)]
        public int StatusPrintSeconds { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CG_MNQ_CHBlueprintExec_v1";
                Description = "CH blueprint execution implementation. One MNQ, no overlap, RTH, session-side allowlist, 80/16/120, CH-style telemetry.";
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
                TimeInForce = TimeInForce.Day;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;

                SignalSource = SignalSourceMode.LiveBlueprintProxy;
                CsvSignalPath = "";
                CsvEntryToleranceSeconds = 5;
                UseCsvSideOnlyIgnorePrice = true;

                PremarketStartTime = 40000;
                RthStartTime = 93000;
                RthEndTime = 160000;
                RequireRth = true;
                NoTradeFirstMinutes = 0;

                UsePremarketContextFilter = false;
                ProxyMomentumLookbackMinutes = 3;
                ProxyMinMomentumTicks = 40;
                ProxyMinRangeExpansionTicks = 50;
                ProxyRecentRangeMinutes = 10;

                // These defaults mirror the CH idea that side/session buckets matter.
                // They are permissive at first so the execution layer can be tested.
                AllowOpenLong = true;
                AllowOpenShort = true;
                AllowMorningLong = true;
                AllowMorningShort = true;
                AllowMiddayLong = false;
                AllowMiddayShort = true;
                AllowAfternoonLong = false;
                AllowAfternoonShort = false;
                AllowPowerLong = false;
                AllowPowerShort = true;

                TargetTicks = 80;
                StopTicks = 16;
                MaxHoldSeconds = 120;

                MaxEntrySpreadTicks = 3;
                MaxRuntimeSpreadTicks = 6;

                CooldownSecondsAfterExit = 180;
                MinSecondsBetweenEntries = 240;

                MaxTradesPerDay = 6;
                MaxTradesPerSessionPhase = 2;
                MaxConsecutiveStopLosses = 2;
                StopStreakLockoutMinutes = 30;
                DailyLossLockUsd = 250.0;
                EnableDailyLossLock = true;

                RequirePlaybackOrSimAccount = true;

                WriteTelemetry = true;
                PrintDiagnostics = true;
                StatusPrintSeconds = 30;

                string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "bin", "Custom", "Strategies");
                TelemetryCsvPath = Path.Combine(defaultDir, "CG_MNQ_CHBlueprintExec_v1_telemetry.csv");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
                AddDataSeries(BarsPeriodType.Minute, 1);

                SetProfitTarget("CHB_LONG", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CHB_LONG", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("CHB_SHORT", CalculationMode.Ticks, TargetTicks);
                SetStopLoss("CHB_SHORT", CalculationMode.Ticks, StopTicks, false);
            }
            else if (State == State.DataLoaded)
            {
                WriteTelemetryHeader();

                if (SignalSource == SignalSourceMode.CsvBlueprintSignals)
                    LoadCsvSignals();

                LogTelemetry("STRATEGY_LOADED", Core.Globals.Now, "", "", "initialization");
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 1)
                return;

            if (CurrentBars[1] < 50 || CurrentBars[2] < Math.Max(ProxyMomentumLookbackMinutes + 2, ProxyRecentRangeMinutes + 2))
                return;

            DateTime now = Times[1][0];
            ResetSessionIfNeeded(now);

            string phase = GetSessionPhase(now);
            double bid = GetCurrentBidSafe();
            double ask = GetCurrentAskSafe();
            double spreadTicks = GetSpreadTicks(bid, ask);

            UpdatePremarketContext(now);
            MaybePrintStatus(now, phase, spreadTicks);

            if (!AccountIsAllowed())
            {
                blockedAccount++;
                return;
            }

            if (Position.Quantity > HardQuantity)
            {
                LogTelemetry("EMERGENCY_FLATTEN_QTY_GT_1", now, phase, "", "position_qty_gt_1");
                ExitLong("CHB_EmergencyQty_Long", "CHB_LONG");
                ExitShort("CHB_EmergencyQty_Short", "CHB_SHORT");
                return;
            }

            if (EnableDailyLossLock && !dailyLocked)
            {
                double realizedToday = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartCumProfit;
                if (realizedToday <= -Math.Abs(DailyLossLockUsd))
                {
                    dailyLocked = true;
                    LogTelemetry("DAILY_LOCK_TRIGGERED", now, phase, "", "realized_loss_limit");
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong("CHB_DailyLock_Long", "CHB_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CHB_DailyLock_Short", "CHB_SHORT");
                    return;
                }
            }

            if (Position.MarketPosition != MarketPosition.Flat && spreadTicks > MaxRuntimeSpreadTicks)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("CHB_RuntimeSpread_Long", "CHB_LONG");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("CHB_RuntimeSpread_Short", "CHB_SHORT");

                LogTelemetry("RUNTIME_SPREAD_EXIT", now, phase, "", "spread_gt_runtime_max");
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat && activeEntryTime != Core.Globals.MinDate)
            {
                double heldSeconds = (now - activeEntryTime).TotalSeconds;
                if (heldSeconds >= MaxHoldSeconds)
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong("CHB_TimeExit_Long", "CHB_LONG");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort("CHB_TimeExit_Short", "CHB_SHORT");

                    LogTelemetry("TIME_EXIT_SUBMITTED", now, phase, "", "max_hold_seconds");
                    return;
                }
            }

            if (!CanConsiderEntry(now, phase, spreadTicks))
                return;

            string signalSide = GetBlueprintSignal(now, phase);

            if (signalSide == "NONE")
            {
                blockedSignal++;
                return;
            }

            if (signalSide == "LONG" && !IsLongAllowed(phase))
            {
                blockedSession++;
                LogTelemetry("SIGNAL_SIDE_BLOCKED", now, phase, signalSide, "long_not_allowed_in_phase");
                return;
            }

            if (signalSide == "SHORT" && !IsShortAllowed(phase))
            {
                blockedSession++;
                LogTelemetry("SIGNAL_SIDE_BLOCKED", now, phase, signalSide, "short_not_allowed_in_phase");
                return;
            }

            SubmitEntry(now, phase, signalSide);
        }

        private string GetBlueprintSignal(DateTime now, string phase)
        {
            if (SignalSource == SignalSourceMode.Disabled)
                return "NONE";

            if (SignalSource == SignalSourceMode.CsvBlueprintSignals)
                return GetCsvSignal(now, phase);

            return GetLiveBlueprintProxySignal(now, phase);
        }

        private string GetCsvSignal(DateTime now, string phase)
        {
            if (csvSignals.Count == 0)
                return "NONE";

            while (nextCsvIndex < csvSignals.Count)
            {
                CsvSignal s = csvSignals[nextCsvIndex];

                if (s.Fired)
                {
                    nextCsvIndex++;
                    continue;
                }

                double deltaSeconds = (now - s.EntryTimeNy).TotalSeconds;

                if (deltaSeconds < -CsvEntryToleranceSeconds)
                    return "NONE";

                if (deltaSeconds > CsvEntryToleranceSeconds)
                {
                    csvSignalsSkipped++;
                    s.Fired = true;
                    nextCsvIndex++;
                    LogTelemetry("CSV_SIGNAL_SKIPPED_LATE", now, phase, s.SignalSide,
                        string.Format(CultureInfo.InvariantCulture, "signal_time={0};delta_seconds={1}",
                            s.EntryTimeNy.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                            deltaSeconds));
                    continue;
                }

                s.Fired = true;
                nextCsvIndex++;
                LogTelemetry("CSV_SIGNAL_MATCHED", now, phase, s.SignalSide,
                    string.Format(CultureInfo.InvariantCulture, "signal_time={0};model_variant={1};source_phase={2}",
                        s.EntryTimeNy.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        s.ModelVariant,
                        s.SessionPhase));

                return NormalizeSide(s.SignalSide);
            }

            return "NONE";
        }

        private string GetLiveBlueprintProxySignal(DateTime now, string phase)
        {
            // Minimal live proxy, not freehand alpha:
            // It only tries to approximate the CH deploy candidate's executable
            // "directional continuation with session/side/spread/time contract".
            //
            // This is intentionally simple and telemetry-visible. Exact CH fields
            // should replace this method in v2.

            double momentumTicks = GetMinuteMomentumTicks(ProxyMomentumLookbackMinutes);
            double recentRangeTicks = GetRecentMinuteRangeTicks(ProxyRecentRangeMinutes);
            double current = Closes[1][0];

            bool rangeExpanded = recentRangeTicks >= ProxyMinRangeExpansionTicks;
            bool longContext = !UsePremarketContextFilter || !premarketSeen || current >= premarketMid;
            bool shortContext = !UsePremarketContextFilter || !premarketSeen || current <= premarketMid;

            if (rangeExpanded && momentumTicks >= ProxyMinMomentumTicks && longContext)
            {
                LogTelemetry("LIVE_PROXY_SIGNAL", now, phase, "LONG",
                    string.Format(CultureInfo.InvariantCulture, "mom_ticks={0};range_ticks={1};pm_mid={2}",
                        momentumTicks, recentRangeTicks, premarketSeen ? premarketMid : 0.0));
                return "LONG";
            }

            if (rangeExpanded && momentumTicks <= -ProxyMinMomentumTicks && shortContext)
            {
                LogTelemetry("LIVE_PROXY_SIGNAL", now, phase, "SHORT",
                    string.Format(CultureInfo.InvariantCulture, "mom_ticks={0};range_ticks={1};pm_mid={2}",
                        momentumTicks, recentRangeTicks, premarketSeen ? premarketMid : 0.0));
                return "SHORT";
            }

            return "NONE";
        }

        private bool CanConsiderEntry(DateTime now, string phase, double spreadTicks)
        {
            if (dailyLocked)
            {
                blockedSignal++;
                return false;
            }

            if (now < stopStreakLockedUntil)
            {
                blockedSignal++;
                return false;
            }

            if (RequireRth && !IsRth(now))
            {
                blockedNotRth++;
                return false;
            }

            if (MinutesSinceRthOpen(now) < NoTradeFirstMinutes)
            {
                blockedSignal++;
                return false;
            }

            if (sessionTradeCount >= MaxTradesPerDay)
            {
                blockedSession++;
                return false;
            }

            if (GetPhaseTradeCount(phase) >= MaxTradesPerSessionPhase)
            {
                blockedSession++;
                return false;
            }

            if (Position.MarketPosition != MarketPosition.Flat || activeEntryOrder != null)
            {
                blockedNotFlat++;
                return false;
            }

            if (lastExitTime != Core.Globals.MinDate && (now - lastExitTime).TotalSeconds < CooldownSecondsAfterExit)
            {
                blockedCooldown++;
                return false;
            }

            if (lastEntryTime != Core.Globals.MinDate && (now - lastEntryTime).TotalSeconds < MinSecondsBetweenEntries)
            {
                blockedCooldown++;
                return false;
            }

            if (spreadTicks > MaxEntrySpreadTicks)
            {
                blockedSpread++;
                return false;
            }

            return true;
        }

        private void SubmitEntry(DateTime now, string phase, string side)
        {
            lastEntryTime = now;
            activeEntryTime = now;
            IncrementPhaseTradeCount(phase);
            sessionTradeCount++;
            entriesSubmitted++;

            if (side == "LONG")
            {
                EnterLong(HardQuantity, "CHB_LONG");
                LogTelemetry("ENTRY_SUBMITTED", now, phase, "LONG", "ch_blueprint_contract");
            }
            else if (side == "SHORT")
            {
                EnterShort(HardQuantity, "CHB_SHORT");
                LogTelemetry("ENTRY_SUBMITTED", now, phase, "SHORT", "ch_blueprint_contract");
            }
        }

        private bool AccountIsAllowed()
        {
            if (!RequirePlaybackOrSimAccount)
                return true;

            try
            {
                if (Account == null || string.IsNullOrEmpty(Account.Name))
                    return false;

                string name = Account.Name.ToUpperInvariant();
                return name.Contains("PLAYBACK") || name.Contains("SIM");
            }
            catch
            {
                return false;
            }
        }

        private void LoadCsvSignals()
        {
            csvSignals.Clear();
            nextCsvIndex = 0;

            if (string.IsNullOrWhiteSpace(CsvSignalPath))
            {
                LogTelemetry("CSV_LOAD_SKIPPED", Core.Globals.Now, "", "", "CsvSignalPath_empty");
                return;
            }

            if (!File.Exists(CsvSignalPath))
            {
                LogTelemetry("CSV_LOAD_FAILED", Core.Globals.Now, "", "", "file_not_found=" + CsvSignalPath);
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(CsvSignalPath);
                if (lines.Length <= 1)
                {
                    LogTelemetry("CSV_LOAD_FAILED", Core.Globals.Now, "", "", "no_data_rows");
                    return;
                }

                string[] headers = SplitCsvLine(lines[0]);
                int idxTime = FindHeader(headers, "entry_time_ny");
                int idxSide = FindHeader(headers, "signal_side");
                int idxModel = FindHeader(headers, "model_variant");
                int idxPhase = FindHeader(headers, "session_phase");
                int idxEntry = FindHeader(headers, "entry_price_exec");
                int idxTarget = FindHeader(headers, "target_price");
                int idxStop = FindHeader(headers, "stop_price");

                if (idxTime < 0 || idxSide < 0)
                {
                    LogTelemetry("CSV_LOAD_FAILED", Core.Globals.Now, "", "", "required_headers_missing_entry_time_ny_signal_side");
                    return;
                }

                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                        continue;

                    string[] parts = SplitCsvLine(lines[i]);
                    if (parts.Length <= Math.Max(idxTime, idxSide))
                        continue;

                    DateTime t;
                    if (!TryParseDateTime(parts[idxTime], out t))
                        continue;

                    string side = NormalizeSide(parts[idxSide]);
                    if (side != "LONG" && side != "SHORT")
                        continue;

                    CsvSignal s = new CsvSignal();
                    s.EntryTimeNy = t;
                    s.SignalSide = side;
                    s.ModelVariant = idxModel >= 0 && idxModel < parts.Length ? parts[idxModel] : "";
                    s.SessionPhase = idxPhase >= 0 && idxPhase < parts.Length ? parts[idxPhase] : "";
                    s.EntryPriceExec = idxEntry >= 0 && idxEntry < parts.Length ? ParseDoubleOrZero(parts[idxEntry]) : 0.0;
                    s.TargetPrice = idxTarget >= 0 && idxTarget < parts.Length ? ParseDoubleOrZero(parts[idxTarget]) : 0.0;
                    s.StopPrice = idxStop >= 0 && idxStop < parts.Length ? ParseDoubleOrZero(parts[idxStop]) : 0.0;
                    s.Fired = false;
                    csvSignals.Add(s);
                }

                csvSignals.Sort((a, b) => a.EntryTimeNy.CompareTo(b.EntryTimeNy));
                csvSignalsLoaded = csvSignals.Count;

                LogTelemetry("CSV_LOAD_OK", Core.Globals.Now, "", "",
                    string.Format(CultureInfo.InvariantCulture, "rows={0};path={1}", csvSignalsLoaded, CsvSignalPath));
            }
            catch (Exception ex)
            {
                LogTelemetry("CSV_LOAD_FAILED", Core.Globals.Now, "", "", ex.Message);
            }
        }

        private static int FindHeader(string[] headers, string name)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                if (string.Equals((headers[i] ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static string[] SplitCsvLine(string line)
        {
            List<string> fields = new List<string>();
            if (line == null)
                return fields.ToArray();

            bool inQuotes = false;
            string current = "";

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current += '"';
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(current);
                    current = "";
                }
                else
                {
                    current += c;
                }
            }

            fields.Add(current);
            return fields.ToArray();
        }

        private static bool TryParseDateTime(string s, out DateTime t)
        {
            string[] formats = new string[]
            {
                "yyyy-MM-dd HH:mm:ss.fff",
                "yyyy-MM-dd HH:mm:ss",
                "M/d/yyyy h:mm:ss tt",
                "M/d/yyyy H:mm:ss",
                "MM/dd/yyyy HH:mm:ss"
            };

            if (DateTime.TryParseExact((s ?? "").Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                return true;

            return DateTime.TryParse((s ?? "").Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
        }

        private static double ParseDoubleOrZero(string s)
        {
            double v;
            if (double.TryParse((s ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                return v;
            return 0.0;
        }

        private static string NormalizeSide(string side)
        {
            string s = (side ?? "").Trim().ToUpperInvariant();
            if (s == "BUY" || s == "BULL" || s == "LONG")
                return "LONG";
            if (s == "SELL" || s == "BEAR" || s == "SHORT")
                return "SHORT";
            return "NONE";
        }

        private void UpdatePremarketContext(DateTime now)
        {
            int hms = ToHms(now);
            if (hms < PremarketStartTime || hms >= RthStartTime)
                return;

            double c = Closes[1][0];

            if (!premarketSeen)
            {
                premarketOpen = c;
                premarketHigh = Highs[1][0];
                premarketLow = Lows[1][0];
                premarketSeen = true;
                LogTelemetry("PREMARKET_CONTEXT_STARTED", now, GetSessionPhase(now), "", string.Format(CultureInfo.InvariantCulture, "open={0}", premarketOpen));
            }

            premarketHigh = Math.Max(premarketHigh, Highs[1][0]);
            premarketLow = Math.Min(premarketLow, Lows[1][0]);
            premarketLast = c;
            premarketMid = (premarketHigh + premarketLow) * 0.5;
        }

        private double GetMinuteMomentumTicks(int lookbackMinutes)
        {
            try
            {
                if (CurrentBars[2] < lookbackMinutes + 1)
                    return 0.0;

                return (Closes[2][0] - Closes[2][lookbackMinutes]) / MnqTickSize;
            }
            catch
            {
                return 0.0;
            }
        }

        private double GetRecentMinuteRangeTicks(int lookbackMinutes)
        {
            try
            {
                if (CurrentBars[2] < lookbackMinutes + 1)
                    return 0.0;

                double h = Highs[2][0];
                double l = Lows[2][0];

                for (int i = 1; i <= lookbackMinutes; i++)
                {
                    h = Math.Max(h, Highs[2][i]);
                    l = Math.Min(l, Lows[2][i]);
                }

                return (h - l) / MnqTickSize;
            }
            catch
            {
                return 0.0;
            }
        }

        private bool IsRth(DateTime t)
        {
            int hms = ToHms(t);
            return hms >= RthStartTime && hms < RthEndTime;
        }

        private int ToHms(DateTime t)
        {
            return t.Hour * 10000 + t.Minute * 100 + t.Second;
        }

        private int MinutesSinceRthOpen(DateTime t)
        {
            DateTime open = HmsToDateTime(t, RthStartTime);
            return (int)Math.Floor((t - open).TotalMinutes);
        }

        private DateTime HmsToDateTime(DateTime baseDate, int hms)
        {
            int h = hms / 10000;
            int m = (hms / 100) % 100;
            int s = hms % 100;
            return new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, h, m, s);
        }

        private string GetSessionPhase(DateTime t)
        {
            int hms = ToHms(t);

            if (hms >= 93000 && hms < 100000)
                return "OPEN_0930_1000";
            if (hms >= 100000 && hms < 113000)
                return "MORNING_1000_1130";
            if (hms >= 113000 && hms < 133000)
                return "MIDDAY_1130_1330";
            if (hms >= 133000 && hms < 150000)
                return "AFTERNOON_1330_1500";
            if (hms >= 150000 && hms < 160000)
                return "POWER_1500_1600";

            return "OUTSIDE_RTH";
        }

        private bool IsLongAllowed(string phase)
        {
            if (phase == "OPEN_0930_1000") return AllowOpenLong;
            if (phase == "MORNING_1000_1130") return AllowMorningLong;
            if (phase == "MIDDAY_1130_1330") return AllowMiddayLong;
            if (phase == "AFTERNOON_1330_1500") return AllowAfternoonLong;
            if (phase == "POWER_1500_1600") return AllowPowerLong;
            return false;
        }

        private bool IsShortAllowed(string phase)
        {
            if (phase == "OPEN_0930_1000") return AllowOpenShort;
            if (phase == "MORNING_1000_1130") return AllowMorningShort;
            if (phase == "MIDDAY_1130_1330") return AllowMiddayShort;
            if (phase == "AFTERNOON_1330_1500") return AllowAfternoonShort;
            if (phase == "POWER_1500_1600") return AllowPowerShort;
            return false;
        }

        private int GetPhaseTradeCount(string phase)
        {
            if (phase == "OPEN_0930_1000") return openTradeCount;
            if (phase == "MORNING_1000_1130") return morningTradeCount;
            if (phase == "MIDDAY_1130_1330") return middayTradeCount;
            if (phase == "AFTERNOON_1330_1500") return afternoonTradeCount;
            if (phase == "POWER_1500_1600") return powerTradeCount;
            return 999999;
        }

        private void IncrementPhaseTradeCount(string phase)
        {
            if (phase == "OPEN_0930_1000") openTradeCount++;
            else if (phase == "MORNING_1000_1130") morningTradeCount++;
            else if (phase == "MIDDAY_1130_1330") middayTradeCount++;
            else if (phase == "AFTERNOON_1330_1500") afternoonTradeCount++;
            else if (phase == "POWER_1500_1600") powerTradeCount++;
        }

        private void ResetSessionIfNeeded(DateTime now)
        {
            if (currentSessionDate.Date == now.Date)
                return;

            currentSessionDate = now.Date;
            sessionStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;

            sessionTradeCount = 0;
            openTradeCount = 0;
            morningTradeCount = 0;
            middayTradeCount = 0;
            afternoonTradeCount = 0;
            powerTradeCount = 0;
            consecutiveStopLosses = 0;
            dailyLocked = false;
            stopStreakLockedUntil = Core.Globals.MinDate;
            activeEntryTime = Core.Globals.MinDate;
            activeEntryOrder = null;

            premarketHigh = double.MinValue;
            premarketLow = double.MaxValue;
            premarketMid = 0.0;
            premarketOpen = 0.0;
            premarketLast = 0.0;
            premarketSeen = false;

            ResetBlockCounters();

            LogTelemetry("SESSION_RESET", now, "", "", "new_session");
        }

        private void ResetBlockCounters()
        {
            blockedNotRth = 0;
            blockedSpread = 0;
            blockedNotFlat = 0;
            blockedCooldown = 0;
            blockedSession = 0;
            blockedSignal = 0;
            blockedAccount = 0;
            entriesSubmitted = 0;
        }

        private double GetCurrentBidSafe()
        {
            try
            {
                double bid = GetCurrentBid();
                if (!double.IsNaN(bid) && bid > 0)
                    return bid;
            }
            catch { }

            try { return Closes[1][0]; } catch { return 0.0; }
        }

        private double GetCurrentAskSafe()
        {
            try
            {
                double ask = GetCurrentAsk();
                if (!double.IsNaN(ask) && ask > 0)
                    return ask;
            }
            catch { }

            try { return Closes[1][0]; } catch { return 0.0; }
        }

        private double GetSpreadTicks(double bid, double ask)
        {
            if (ask <= 0 || bid <= 0 || ask < bid)
                return 999.0;
            return (ask - bid) / MnqTickSize;
        }

        private void MaybePrintStatus(DateTime now, string phase, double spreadTicks)
        {
            if (!PrintDiagnostics)
                return;

            if (lastStatusPrintTime != Core.Globals.MinDate && (now - lastStatusPrintTime).TotalSeconds < StatusPrintSeconds)
                return;

            lastStatusPrintTime = now;

            double mom = GetMinuteMomentumTicks(ProxyMomentumLookbackMinutes);
            double rng = GetRecentMinuteRangeTicks(ProxyRecentRangeMinutes);

            string msg = string.Format(CultureInfo.InvariantCulture,
                "CHB STATUS {0} source={1} phase={2} acct={3} pos={4} trades={5} spread={6:F1} mom={7:F1} range={8:F1} pmSeen={9} pmH={10:F2} pmL={11:F2} pmMid={12:F2} csvLoaded={13} csvNext={14} blocks[acct={15},rth={16},spr={17},flat={18},cool={19},sess={20},sig={21}] entries={22}",
                now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                SignalSource,
                phase,
                SafeAccountName(),
                Position.MarketPosition,
                sessionTradeCount,
                spreadTicks,
                mom,
                rng,
                premarketSeen ? 1 : 0,
                premarketSeen ? premarketHigh : 0.0,
                premarketSeen ? premarketLow : 0.0,
                premarketSeen ? premarketMid : 0.0,
                csvSignalsLoaded,
                nextCsvIndex,
                blockedAccount,
                blockedNotRth,
                blockedSpread,
                blockedNotFlat,
                blockedCooldown,
                blockedSession,
                blockedSignal,
                entriesSubmitted);

            Print(msg);
            LogTelemetry("STATUS", now, phase, "", msg);
        }

        private string SafeAccountName()
        {
            try
            {
                return Account == null ? "NULL" : Account.Name;
            }
            catch
            {
                return "UNKNOWN";
            }
        }

        protected override void OnOrderUpdate(
            Order order,
            double limitPrice,
            double stopPrice,
            int quantity,
            int filled,
            double averageFillPrice,
            OrderState orderState,
            DateTime time,
            ErrorCode error,
            string nativeError)
        {
            if (order == null)
                return;

            if (order.Name == "CHB_LONG" || order.Name == "CHB_SHORT")
            {
                activeEntryOrder = order;

                if (orderState == OrderState.Filled)
                {
                    activeEntryTime = time;
                    LogTelemetry("ENTRY_FILLED", time, GetSessionPhase(time), order.Name,
                        string.Format(CultureInfo.InvariantCulture, "avgFill={0};qty={1}", averageFillPrice, filled));
                }
                else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                {
                    LogTelemetry("ENTRY_" + orderState.ToString().ToUpperInvariant(), time, GetSessionPhase(time), order.Name, nativeError ?? string.Empty);
                    activeEntryOrder = null;
                    activeEntryTime = Core.Globals.MinDate;
                }
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

            string orderName = execution.Order.Name ?? string.Empty;
            LogTelemetry("EXECUTION", time, GetSessionPhase(time), orderName,
                string.Format(CultureInfo.InvariantCulture, "price={0};qty={1};mp={2};orderId={3}", price, quantity, marketPosition, orderId));

            bool looksLikeStop = orderName.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0
                                || orderName.IndexOf("loss", StringComparison.OrdinalIgnoreCase) >= 0;

            bool looksLikeTarget = orderName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                                  || orderName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            if (marketPosition == MarketPosition.Flat || Position.MarketPosition == MarketPosition.Flat)
            {
                lastExitTime = time;

                if (looksLikeStop)
                {
                    consecutiveStopLosses++;
                    if (consecutiveStopLosses >= MaxConsecutiveStopLosses)
                    {
                        stopStreakLockedUntil = time.AddMinutes(StopStreakLockoutMinutes);
                        LogTelemetry("STOP_STREAK_LOCKOUT", time, GetSessionPhase(time), orderName,
                            string.Format(CultureInfo.InvariantCulture, "consecutiveStops={0};lockedUntil={1}",
                                consecutiveStopLosses,
                                stopStreakLockedUntil.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                    }
                }
                else if (looksLikeTarget)
                {
                    consecutiveStopLosses = 0;
                }

                activeEntryOrder = null;
                activeEntryTime = Core.Globals.MinDate;
            }
        }

        private void WriteTelemetryHeader()
        {
            if (!WriteTelemetry || telemetryHeaderWritten)
                return;

            try
            {
                string dir = Path.GetDirectoryName(TelemetryCsvPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (!File.Exists(TelemetryCsvPath))
                {
                    File.AppendAllText(TelemetryCsvPath,
                        "timestamp,event,source,phase,signal_side,account,position,bid,ask,spread_ticks,momentum_ticks,recent_range_ticks,premarket_seen,premarket_high,premarket_low,premarket_mid,session_trades,open_trades,morning_trades,midday_trades,afternoon_trades,power_trades,consecutive_stops,daily_locked,csv_loaded,csv_next,blocked_account,blocked_rth,blocked_spread,blocked_flat,blocked_cooldown,blocked_session,blocked_signal,entries_submitted,note" + Environment.NewLine);
                }

                telemetryHeaderWritten = true;
            }
            catch (Exception ex)
            {
                Print("Telemetry header write failed: " + ex.Message);
            }
        }

        private void LogTelemetry(string eventName, DateTime timestamp, string phase, string signalSide, string note)
        {
            if (!WriteTelemetry)
                return;

            try
            {
                WriteTelemetryHeader();

                double bid = GetCurrentBidSafe();
                double ask = GetCurrentAskSafe();
                double spreadTicks = GetSpreadTicks(bid, ask);
                double mom = GetMinuteMomentumTicks(ProxyMomentumLookbackMinutes);
                double rng = GetRecentMinuteRangeTicks(ProxyRecentRangeMinutes);
                string pos = "UNKNOWN";
                try { pos = Position == null ? "UNKNOWN" : Position.MarketPosition.ToString(); } catch { }

                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6},{7:F2},{8:F2},{9:F2},{10:F2},{11:F2},{12},{13:F2},{14:F2},{15:F2},{16},{17},{18},{19},{20},{21},{22},{23},{24},{25},{26},{27},{28},{29},{30},{31},{32},{33},{34}",
                    timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    EscapeCsv(eventName),
                    SignalSource,
                    EscapeCsv(phase ?? string.Empty),
                    EscapeCsv(signalSide ?? string.Empty),
                    EscapeCsv(SafeAccountName()),
                    EscapeCsv(pos),
                    bid,
                    ask,
                    spreadTicks,
                    mom,
                    rng,
                    premarketSeen ? 1 : 0,
                    premarketSeen ? premarketHigh : 0.0,
                    premarketSeen ? premarketLow : 0.0,
                    premarketSeen ? premarketMid : 0.0,
                    sessionTradeCount,
                    openTradeCount,
                    morningTradeCount,
                    middayTradeCount,
                    afternoonTradeCount,
                    powerTradeCount,
                    consecutiveStopLosses,
                    dailyLocked ? 1 : 0,
                    csvSignalsLoaded,
                    nextCsvIndex,
                    blockedAccount,
                    blockedNotRth,
                    blockedSpread,
                    blockedNotFlat,
                    blockedCooldown,
                    blockedSession,
                    blockedSignal,
                    entriesSubmitted,
                    EscapeCsv(note ?? string.Empty));

                File.AppendAllText(TelemetryCsvPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                try { Print("Telemetry write failed: " + ex.Message); } catch { }
            }
        }

        private static string EscapeCsv(string s)
        {
            if (s == null)
                return string.Empty;

            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";

            return s;
        }
    }
}
