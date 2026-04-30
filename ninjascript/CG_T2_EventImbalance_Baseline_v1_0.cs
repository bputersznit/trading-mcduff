// CG_T2_EventImbalance_Baseline_v1_0.cs
// NinjaTrader 8 Strategy
//
// Purpose:
//   Replicates the EXACT signal logic from CH Oct 2025 baseline (908 trades, $71,429)
//   Uses event_delta and event_imbalance matching the original CH query:
//     LONG:  event_delta > 50 AND event_imbalance > 0.60
//     SHORT: event_delta < -50 AND event_imbalance < -0.60
//
// Key Difference from v1_1:
//   - Uses tick-count imbalance (matching CH event counts) instead of volume imbalance
//   - Removes wall_score, momentum_ticks, and other proxy features
//   - Pure event-delta and event-imbalance based signals
//
// Note:
//   NT8 doesn't have true MBO events, so we approximate using tick counts
//   Each bar/tick is classified as bid-side or ask-side based on close movement

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
public class CG_T2_EventImbalance_Baseline_v1_0 : Strategy
{
// ================================================================
// Internal state
// ================================================================

    private const string LongSignalName  = "T2_Long_Limit";
    private const string ShortSignalName = "T2_Short_Limit";

    private long tradeIdCounter = 0;
    private long activeTradeId = 0;

    private DateTime lastEntryAttemptTime = Core.Globals.MinDate;
    private DateTime pendingSubmitTime = Core.Globals.MinDate;
    private DateTime governorResumeTime = Core.Globals.MinDate;
    private DateTime lastFlatTime = Core.Globals.MinDate;

    private int consecutiveLosses = 0;
    private int sessionTrades = 0;
    private int lastSessionDate = -1;

    private bool pendingLong = false;
    private bool pendingShort = false;
    private bool pendingLimitActive = false;
    private bool protectionArmed = false;
    private bool entryTelemetryStarted = false;

    private double entryPrice = 0.0;
    private double activeMfeTicks = 0.0;
    private double activeMaeTicks = 0.0;
    private double activeMaxPrice = 0.0;
    private double activeMinPrice = 0.0;
    private double cumulativePnlAtEntry = 0.0;

    private string activeSide = "";
    private string activeRegime = "";
    private string lastExitReason = "";
    private string lastFallbackReason = "";

    private StreamWriter telemetryWriter;
    private string telemetryPath;

    // Diagnostic counters
    private long rthRejects = 0;
    private long spreadRejects = 0;
    private long eventDeltaRejects = 0;
    private long imbalanceRejects = 0;
    private long cooldownRejects = 0;
    private long pendingRejects = 0;
    private long governorRejects = 0;
    private long limitTimeouts = 0;
    private long fallbackMarkets = 0;
    private long fallbackSkips = 0;
    private long signalLongCount = 0;
    private long signalShortCount = 0;

    // Latest computed event features
    private double currentEventDelta = 0.0;
    private double currentEventImbalance = 0.0;
    private double currentTotalEvents = 0.0;
    private double currentSpreadTicks = 1.0;

// ================================================================
// NT lifecycle
// ================================================================

    protected override void OnStateChange()
    {
        if (State == State.SetDefaults)
        {
            Description = "T2 Event Imbalance Baseline v1.0: Replicates CH Oct 2025 baseline signal logic (event_delta + event_imbalance)";
            Name = "CG_T2_EventImbalance_Baseline_v1_0";

            Calculate = Calculate.OnEachTick;
            EntriesPerDirection = 1;
            EntryHandling = EntryHandling.AllEntries;
            IsExitOnSessionCloseStrategy = true;
            ExitOnSessionCloseSeconds = 30;
            IsInstantiatedOnEachOptimizationIteration = false;

            // Managed protective order behavior
            StopTargetHandling = StopTargetHandling.PerEntryExecution;
            RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
            TimeInForce = TimeInForce.Day;

            // Core execution defaults
            Quantity = 1;
            UseLimitFirst = true;
            LimitOffsetTicks = 1;
            LimitTimeoutSeconds = 3;
            UseMarketFallback = true;

            StopTicks = 20;      // Original CH: 20 ticks
            TargetTicks = 40;    // Original CH: 40 ticks
            MaxHoldSeconds = 600; // 10 minute timeout

            // Session controls
            StartTimeEt = 93000;  // 9:30 AM ET
            EndTimeEt = 155900;   // 3:59 PM ET
            RthBufferSeconds = 0;
            OneTradePerSession = false;

            // Original CH signal thresholds
            CooldownSeconds = 0;   // Original CH had no cooldown (single-position enforcement only)
            EmergencyCooldownSeconds = 60;
            MaxSpreadTicks = 8;

            MinEventDelta = 50.0;         // Original CH: > 50 or < -50
            MinEventImbalance = 0.60;      // Original CH: > 0.60 or < -0.60

            // Event approximation parameters
            EventLookbackBars = 100;       // ~100 bars at 100ms = 10 seconds of events

            // Loss governor (from v4 institutional manipaware)
            MaxConsecutiveLosses = 3;      // Stop after 3 losses in a row
            MaxDailyLoss = 60.0;           // Stop at -$60 daily loss

            // Opening range suppression (from v4)
            UseOpeningRangeSuppression = true;
            OpeningRangeMinutes = 15;      // 9:30-9:45 AM ET

            // Telemetry
            EnableTelemetry = true;
            TelemetryFilePrefix = "CG_T2_EventImbalance_Baseline_v1_0";
            PrintDiagnostics = true;
        }
        else if (State == State.Configure)
        {
            // Add tick data series for high-resolution event tracking
            // This runs regardless of what primary series the user selected
            // BarsArray[0] = primary (user-selected)
            // BarsArray[1] = tick data (for event calculations)
            AddDataSeries(BarsPeriodType.Tick, 1);

            if (PrintDiagnostics)
                Print("Added 1-tick data series for event calculations (BarsArray[1])");
        }
        else if (State == State.DataLoaded)
        {
            // Auto-configure EventLookbackBars for tick data
            // We always use tick data (BarsArray[1]) regardless of primary series
            // Target: ~10 seconds of lookback (matching original CH 100ms × 100 = 10 seconds)
            // For MNQ, ~200 ticks ≈ 10 seconds during active trading
            if (EventLookbackBars == 100)
            {
                EventLookbackBars = 200; // Default for MNQ tick frequency
            }

            if (PrintDiagnostics)
            {
                Print($"Primary data series: {BarsPeriod.BarsPeriodType} {BarsPeriod.Value}");
                Print($"Event calculations use: 1-tick data (BarsArray[1])");
                Print($"EventLookbackBars = {EventLookbackBars} ticks");
            }

            if (EnableTelemetry)
                OpenTelemetry();
        }
        else if (State == State.Terminated)
        {
            CloseTelemetry();
            PrintSummary();
        }
    }

// ================================================================
// Main execution loop
// ================================================================

    protected override void OnBarUpdate()
    {
        // Only execute on primary data series (user-selected)
        // BarsArray[1] (tick data) is used for event calculations only
        if (BarsInProgress != 0)
            return;

        if (CurrentBar < Math.Max(EventLookbackBars, 10))
        {
            if (PrintDiagnostics && CurrentBar == 1)
                Print($"Waiting for primary bars: {CurrentBar}/{Math.Max(EventLookbackBars, 10)} needed");
            return;
        }

        // Also ensure tick data has enough bars
        if (BarsArray[1].Count < Math.Max(EventLookbackBars, 10))
        {
            if (PrintDiagnostics && CurrentBar % 60 == 0) // Print every 60 bars
                Print($"Waiting for tick data: {BarsArray[1].Count}/{Math.Max(EventLookbackBars, 10)} ticks needed");
            return;
        }

        // Log when strategy starts active evaluation (only once)
        if (PrintDiagnostics && CurrentBar == Math.Max(EventLookbackBars, 10))
        {
            Print($"✅ Strategy active! Primary bars: {CurrentBar}, Tick bars: {BarsArray[1].Count}");
            Print($"Now evaluating signals...");
        }

        DateTime now = Time[0];
        string regime = GetSessionRegime(now);

        // Session boundary reset
        int sessionDate = ToTime(now).Date.Year * 10000 +
                          ToTime(now).Date.Month * 100 +
                          ToTime(now).Date.Day;

        if (sessionDate != lastSessionDate)
        {
            consecutiveLosses = 0;
            sessionTrades = 0;
            cumulativePnlAtEntry = 0.0;
            lastSessionDate = sessionDate;
        }

        // RTH check
        int currentTime = ToTime(now).Hour * 10000 + ToTime(now).Minute * 100 + ToTime(now).Second;
        if (currentTime < StartTimeEt || currentTime > EndTimeEt)
        {
            rthRejects++;
            return;
        }

        // Compute event features
        ComputeEventFeatures();

        // Spread filter
        if (currentSpreadTicks > MaxSpreadTicks)
        {
            spreadRejects++;
            return;
        }

        // Check if we have an open position
        bool isFlat = Position.MarketPosition == MarketPosition.Flat;

        if (!isFlat)
        {
            // Track MFE/MAE for telemetry
            UpdateMfeMAE();
            return;
        }

        // Check loss governor
        if (IsLossGovernorActive())
        {
            governorRejects++;
            return;
        }

        // Check cooldown
        if (now < governorResumeTime)
        {
            cooldownRejects++;
            return;
        }

        double secondsSinceLastEntry = (now - lastEntryAttemptTime).TotalSeconds;
        if (secondsSinceLastEntry < CooldownSeconds)
        {
            cooldownRejects++;
            return;
        }

        // Check if pending limit order
        if (pendingLong || pendingShort)
        {
            pendingRejects++;
            return;
        }

        // Opening range suppression (avoid 9:30-9:45 AM shorts per original CH v4 logic)
        if (UseOpeningRangeSuppression && IsOpeningRange(now))
        {
            // Original CH v4 blocked shorts during OPEN_15
            // We'll skip all signals during opening 15 min for safety
            return;
        }

        // Signal evaluation with original CH thresholds
        bool longSignal = IsLongSignal();
        bool shortSignal = IsShortSignal();

        if (longSignal && !shortSignal)
        {
            signalLongCount++;
            SubmitLong(now, regime);
        }
        else if (shortSignal && !longSignal)
        {
            signalShortCount++;
            SubmitShort(now, regime);
        }
    }

// ================================================================
// Event feature computation (matching CH event_delta + event_imbalance)
// ================================================================

    private void ComputeEventFeatures()
    {
        // In CH: event_delta = bid_events - ask_events
        //        event_imbalance = event_delta / total_events
        //
        // In NT8: We use tick data (BarsArray[1]) for high-resolution event tracking
        //         Each tick where close > prior close = bid-side event
        //         Each tick where close < prior close = ask-side event

        double bidEvents = 0.0;
        double askEvents = 0.0;

        // Use tick data series (BarsArray[1]) for event calculations
        int tickBars = BarsArray[1].Count;
        int n = Math.Min(EventLookbackBars, tickBars - 1);

        for (int i = 0; i < n; i++)
        {
            // Access tick data using Closes[1][i]
            if (Closes[1][i] > Closes[1][i + 1])
                bidEvents += 1.0;
            else if (Closes[1][i] < Closes[1][i + 1])
                askEvents += 1.0;
            else
            {
                // Unchanged close - split evenly
                bidEvents += 0.5;
                askEvents += 0.5;
            }
        }

        currentTotalEvents = bidEvents + askEvents;
        currentEventDelta = bidEvents - askEvents;

        if (currentTotalEvents > 0)
            currentEventImbalance = currentEventDelta / currentTotalEvents;
        else
            currentEventImbalance = 0.0;

        // Spread calculation
        double bid = GetCurrentBid();
        double ask = GetCurrentAsk();
        if (bid > 0 && ask > 0 && ask >= bid)
            currentSpreadTicks = Math.Max(1.0, (ask - bid) / TickSize);
        else
            currentSpreadTicks = 1.0;
    }

// ================================================================
// Signal logic - EXACT match to original CH baseline
// ================================================================

    private bool IsLongSignal()
    {
        // Original CH: event_delta > 50 AND event_imbalance > 0.60

        if (currentEventDelta <= MinEventDelta)
        {
            eventDeltaRejects++;
            return false;
        }

        if (currentEventImbalance <= MinEventImbalance)
        {
            imbalanceRejects++;
            return false;
        }

        return true;
    }

    private bool IsShortSignal()
    {
        // Original CH: event_delta < -50 AND event_imbalance < -0.60

        if (currentEventDelta >= -MinEventDelta)
        {
            eventDeltaRejects++;
            return false;
        }

        if (currentEventImbalance >= -MinEventImbalance)
        {
            imbalanceRejects++;
            return false;
        }

        return true;
    }

// ================================================================
// Entry / exit execution
// ================================================================

    private void SubmitLong(DateTime now, string regime)
    {
        ArmProtection(LongSignalName);

        activeTradeId = ++tradeIdCounter;
        activeSide = "LONG";
        activeRegime = regime;
        lastFallbackReason = "";
        lastEntryAttemptTime = now;
        pendingSubmitTime = now;
        pendingLong = true;
        pendingShort = false;
        pendingLimitActive = UseLimitFirst;
        protectionArmed = true;

        if (UseLimitFirst)
        {
            double limitPrice = GetCurrentAsk() - (LimitOffsetTicks * TickSize);
            EnterLongLimit(0, true, Quantity, limitPrice, LongSignalName);
        }
        else
        {
            EnterLong(Quantity, LongSignalName);
            pendingLimitActive = false;
        }

        if (EnableTelemetry)
            WriteTelemetrySignal("LONG", regime);
    }

    private void SubmitShort(DateTime now, string regime)
    {
        ArmProtection(ShortSignalName);

        activeTradeId = ++tradeIdCounter;
        activeSide = "SHORT";
        activeRegime = regime;
        lastFallbackReason = "";
        lastEntryAttemptTime = now;
        pendingSubmitTime = now;
        pendingLong = false;
        pendingShort = true;
        pendingLimitActive = UseLimitFirst;
        protectionArmed = true;

        if (UseLimitFirst)
        {
            double limitPrice = GetCurrentBid() + (LimitOffsetTicks * TickSize);
            EnterShortLimit(0, true, Quantity, limitPrice, ShortSignalName);
        }
        else
        {
            EnterShort(Quantity, ShortSignalName);
            pendingLimitActive = false;
        }

        if (EnableTelemetry)
            WriteTelemetrySignal("SHORT", regime);
    }

    protected override void OnOrderUpdate(Cbi.Order order, double limitPrice, double stopPrice,
                                          int quantity, int filled, double averageFillPrice,
                                          Cbi.OrderState orderState, DateTime time, Cbi.ErrorCode error, string nativeError)
    {
        if (order.Name != LongSignalName && order.Name != ShortSignalName)
            return;

        if (orderState == Cbi.OrderState.Filled)
        {
            if (pendingLimitActive)
            {
                // Limit filled successfully
                pendingLimitActive = false;
                pendingLong = false;
                pendingShort = false;
                entryTelemetryStarted = false;
                entryPrice = averageFillPrice;
                activeMaxPrice = entryPrice;
                activeMinPrice = entryPrice;
            }
        }
        else if (orderState == Cbi.OrderState.Cancelled || orderState == Cbi.OrderState.Rejected)
        {
            // Limit order cancelled or rejected
            if (pendingLimitActive && UseMarketFallback)
            {
                // Fallback to market
                lastFallbackReason = "LIMIT_TIMEOUT";
                fallbackMarkets++;

                if (pendingLong)
                    EnterLong(Quantity, LongSignalName);
                else if (pendingShort)
                    EnterShort(Quantity, ShortSignalName);

                pendingLimitActive = false;
            }
            else
            {
                // No fallback - cancel the attempt
                fallbackSkips++;
                pendingLong = false;
                pendingShort = false;
                pendingLimitActive = false;
                protectionArmed = false;
            }
        }
    }

    protected override void OnExecutionUpdate(Cbi.Execution execution, string executionId,
                                              double price, int quantity, Cbi.MarketPosition marketPosition, string orderId, DateTime time)
    {
        if (execution.Order.Name != LongSignalName && execution.Order.Name != ShortSignalName)
            return;

        if (execution.Order.OrderState == Cbi.OrderState.Filled)
        {
            if (marketPosition == MarketPosition.Flat)
            {
                // Position closed
                lastFlatTime = time;
                double tradeRealizedPnL = execution.Order.AverageFillPrice - entryPrice;

                if (activeSide == "SHORT")
                    tradeRealizedPnL = entryPrice - execution.Order.AverageFillPrice;

                double tradeRealizedPnLUsd = tradeRealizedPnL / TickSize * TickSize * 5.0; // $5 per tick MNQ
                tradeRealizedPnLUsd -= 0.70; // Commission

                cumulativePnlAtEntry += tradeRealizedPnLUsd;
                sessionTrades++;

                if (tradeRealizedPnLUsd < 0)
                    consecutiveLosses++;
                else
                    consecutiveLosses = 0;

                if (consecutiveLosses >= MaxConsecutiveLosses)
                {
                    governorResumeTime = time.AddSeconds(EmergencyCooldownSeconds);
                    if (PrintDiagnostics)
                        Print($"Loss governor triggered: {consecutiveLosses} consecutive losses. Cooldown until {governorResumeTime}");
                }

                if (EnableTelemetry)
                    WriteTelemetryExit(tradeRealizedPnLUsd);

                // Reset trade state
                activeSide = "";
                activeRegime = "";
                entryPrice = 0.0;
                activeMfeTicks = 0.0;
                activeMaeTicks = 0.0;
                activeTradeId = 0;
            }
            else
            {
                // Entry filled
                entryPrice = price;
                activeMaxPrice = price;
                activeMinPrice = price;
                pendingLong = false;
                pendingShort = false;
                pendingLimitActive = false;
                entryTelemetryStarted = true;
            }
        }
    }

// ================================================================
// Helper functions
// ================================================================

    private void ArmProtection(string signalName)
    {
        SetProfitTarget(signalName, CalculationMode.Ticks, TargetTicks);
        SetStopLoss(signalName, CalculationMode.Ticks, StopTicks, false);
    }

    private bool IsLossGovernorActive()
    {
        if (consecutiveLosses >= MaxConsecutiveLosses)
            return true;

        if (cumulativePnlAtEntry <= -MaxDailyLoss)
            return true;

        return false;
    }

    private bool IsOpeningRange(DateTime now)
    {
        DateTime etTime = ToTime(now);
        if (etTime.Hour == 9 && etTime.Minute >= 30 && etTime.Minute < (30 + OpeningRangeMinutes))
            return true;

        return false;
    }

    private string GetSessionRegime(DateTime now)
    {
        // Match original CH time_zone classification
        DateTime etTime = ToTime(now);
        int hour = etTime.Hour;
        int minute = etTime.Minute;

        if (hour == 9 && minute < 45)
            return "OPEN_15";
        else if ((hour == 9 && minute >= 45) || (hour == 10 && minute < 30))
            return "POST_OPEN";
        else if (hour == 15 && minute >= 30)
            return "CLOSE_30";
        else
            return "NORMAL";
    }

    private DateTime ToTime(DateTime utc)
    {
        // Convert to ET for regime classification
        return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
    }

    private void UpdateMfeMAE()
    {
        if (entryPrice == 0.0)
            return;

        if (activeSide == "LONG")
        {
            double currentMfe = (Close[0] - entryPrice) / TickSize;
            double currentMae = (Close[0] - entryPrice) / TickSize;

            if (currentMfe > activeMfeTicks)
                activeMfeTicks = currentMfe;

            if (currentMae < activeMaeTicks)
                activeMaeTicks = currentMae;
        }
        else if (activeSide == "SHORT")
        {
            double currentMfe = (entryPrice - Close[0]) / TickSize;
            double currentMae = (entryPrice - Close[0]) / TickSize;

            if (currentMfe > activeMfeTicks)
                activeMfeTicks = currentMfe;

            if (currentMae < activeMaeTicks)
                activeMaeTicks = currentMae;
        }
    }

// ================================================================
// Telemetry
// ================================================================

    private void OpenTelemetry()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NinjaTrader 8", "trace");

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            telemetryPath = Path.Combine(dir,
                TelemetryFilePrefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");

            telemetryWriter = new StreamWriter(telemetryPath, false);
            telemetryWriter.WriteLine("record_type,trade_id,time,side,regime,event_delta,event_imbalance,total_events,spread_ticks,limit_submitted,limit_filled,market_fallback,fallback_reason,cooldown_active,loss_governor_active,mfe_ticks,mae_ticks,pnl_usd,exit_reason,diagnostic");
        }
        catch (Exception ex)
        {
            Print($"Telemetry open error: {ex.Message}");
        }
    }

    private void WriteTelemetrySignal(string side, string regime)
    {
        if (telemetryWriter == null)
            return;

        try
        {
            telemetryWriter.WriteLine($"SIGNAL,{activeTradeId},{Time[0]:yyyy-MM-dd HH:mm:ss.fff},{side},{regime},{currentEventDelta:F2},{currentEventImbalance:F4},{currentTotalEvents:F0},{currentSpreadTicks:F1},{(UseLimitFirst ? "1" : "0")},0,0,,,,,,,");
            telemetryWriter.Flush();
        }
        catch { }
    }

    private void WriteTelemetryExit(double pnlUsd)
    {
        if (telemetryWriter == null)
            return;

        try
        {
            string exitReason = "UNKNOWN";
            if (activeMfeTicks >= TargetTicks * 0.9)
                exitReason = "TARGET";
            else if (activeMaeTicks <= -StopTicks * 0.9)
                exitReason = "STOP";

            telemetryWriter.WriteLine($"EXIT,{activeTradeId},{Time[0]:yyyy-MM-dd HH:mm:ss.fff},{activeSide},{activeRegime},{currentEventDelta:F2},{currentEventImbalance:F4},{currentTotalEvents:F0},{currentSpreadTicks:F1},,,,,,,{activeMfeTicks:F1},{activeMaeTicks:F1},{pnlUsd:F2},{exitReason},");
            telemetryWriter.Flush();
        }
        catch { }
    }

    private void CloseTelemetry()
    {
        if (telemetryWriter != null)
        {
            try
            {
                telemetryWriter.Close();
                if (PrintDiagnostics)
                    Print($"Telemetry written to: {telemetryPath}");
            }
            catch { }
        }
    }

    private void PrintSummary()
    {
        if (!PrintDiagnostics)
            return;

        Print("=== T2 EVENT IMBALANCE BASELINE SUMMARY ===");
        Print($"Total LONG signals: {signalLongCount}");
        Print($"Total SHORT signals: {signalShortCount}");
        Print($"Session trades: {sessionTrades}");
        Print($"Consecutive losses: {consecutiveLosses}");
        Print("");
        Print("=== REJECTION REASONS ===");
        Print($"RTH rejects: {rthRejects}");
        Print($"Spread rejects: {spreadRejects}");
        Print($"Event delta rejects: {eventDeltaRejects}");
        Print($"Imbalance rejects: {imbalanceRejects}");
        Print($"Cooldown rejects: {cooldownRejects}");
        Print($"Pending rejects: {pendingRejects}");
        Print($"Governor rejects: {governorRejects}");
        Print("");
        Print("=== EXECUTION STATS ===");
        Print($"Limit timeouts: {limitTimeouts}");
        Print($"Market fallbacks: {fallbackMarkets}");
        Print($"Fallback skips: {fallbackSkips}");
    }

// ================================================================
// Properties
// ================================================================

    [NinjaScriptProperty]
    [Range(1, int.MaxValue)]
    [Display(Name = "Quantity", Order = 1, GroupName = "01. Core")]
    public int Quantity { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "UseLimitFirst", Order = 2, GroupName = "01. Core")]
    public bool UseLimitFirst { get; set; }

    [NinjaScriptProperty]
    [Range(0, 10)]
    [Display(Name = "LimitOffsetTicks", Order = 3, GroupName = "01. Core")]
    public int LimitOffsetTicks { get; set; }

    [NinjaScriptProperty]
    [Range(1, 60)]
    [Display(Name = "LimitTimeoutSeconds", Order = 4, GroupName = "01. Core")]
    public int LimitTimeoutSeconds { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "UseMarketFallback", Order = 5, GroupName = "01. Core")]
    public bool UseMarketFallback { get; set; }

    [NinjaScriptProperty]
    [Range(1, 100)]
    [Display(Name = "StopTicks", Order = 1, GroupName = "02. Risk")]
    public int StopTicks { get; set; }

    [NinjaScriptProperty]
    [Range(1, 200)]
    [Display(Name = "TargetTicks", Order = 2, GroupName = "02. Risk")]
    public int TargetTicks { get; set; }

    [NinjaScriptProperty]
    [Range(60, 3600)]
    [Display(Name = "MaxHoldSeconds", Order = 3, GroupName = "02. Risk")]
    public int MaxHoldSeconds { get; set; }

    [NinjaScriptProperty]
    [Range(0, int.MaxValue)]
    [Display(Name = "StartTimeEt", Order = 1, GroupName = "03. Session")]
    public int StartTimeEt { get; set; }

    [NinjaScriptProperty]
    [Range(0, int.MaxValue)]
    [Display(Name = "EndTimeEt", Order = 2, GroupName = "03. Session")]
    public int EndTimeEt { get; set; }

    [NinjaScriptProperty]
    [Range(0, 600)]
    [Display(Name = "RthBufferSeconds", Order = 3, GroupName = "03. Session")]
    public int RthBufferSeconds { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "OneTradePerSession", Order = 4, GroupName = "03. Session")]
    public bool OneTradePerSession { get; set; }

    [NinjaScriptProperty]
    [Range(0, 3600)]
    [Display(Name = "CooldownSeconds", Order = 1, GroupName = "04. Filters")]
    public int CooldownSeconds { get; set; }

    [NinjaScriptProperty]
    [Range(0, 3600)]
    [Display(Name = "EmergencyCooldownSeconds", Order = 2, GroupName = "04. Filters")]
    public int EmergencyCooldownSeconds { get; set; }

    [NinjaScriptProperty]
    [Range(0, 100)]
    [Display(Name = "MaxSpreadTicks", Order = 3, GroupName = "04. Filters")]
    public double MaxSpreadTicks { get; set; }

    [NinjaScriptProperty]
    [Range(0, 1000)]
    [Display(Name = "MinEventDelta", Order = 4, GroupName = "04. Filters")]
    public double MinEventDelta { get; set; }

    [NinjaScriptProperty]
    [Range(0, 1.0)]
    [Display(Name = "MinEventImbalance", Order = 5, GroupName = "04. Filters")]
    public double MinEventImbalance { get; set; }

    [NinjaScriptProperty]
    [Range(10, 1000)]
    [Display(Name = "EventLookbackBars", Order = 6, GroupName = "04. Filters")]
    public int EventLookbackBars { get; set; }

    [NinjaScriptProperty]
    [Range(0, 10)]
    [Display(Name = "MaxConsecutiveLosses", Order = 1, GroupName = "05. Governor")]
    public int MaxConsecutiveLosses { get; set; }

    [NinjaScriptProperty]
    [Range(0, 10000)]
    [Display(Name = "MaxDailyLoss", Order = 2, GroupName = "05. Governor")]
    public double MaxDailyLoss { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "UseOpeningRangeSuppression", Order = 3, GroupName = "05. Governor")]
    public bool UseOpeningRangeSuppression { get; set; }

    [NinjaScriptProperty]
    [Range(5, 60)]
    [Display(Name = "OpeningRangeMinutes", Order = 4, GroupName = "05. Governor")]
    public int OpeningRangeMinutes { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "EnableTelemetry", Order = 1, GroupName = "06. Telemetry")]
    public bool EnableTelemetry { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "TelemetryFilePrefix", Order = 2, GroupName = "06. Telemetry")]
    public string TelemetryFilePrefix { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "PrintDiagnostics", Order = 3, GroupName = "06. Telemetry")]
    public bool PrintDiagnostics { get; set; }
}
}
