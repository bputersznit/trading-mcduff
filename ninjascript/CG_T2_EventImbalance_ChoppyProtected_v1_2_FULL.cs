// CG_T2_EventImbalance_ChoppyProtected_v1_2_FULL.cs
// NinjaTrader 8 Strategy
//
// Full rewrite of T2 Event Imbalance protected strategy.
//
// Goals:
//   - Runnable full NT8 strategy.
//   - One MNQ contract at a time.
//   - No overlapping positions.
//   - Broker-managed stop/target protection via SetStopLoss / SetProfitTarget.
//   - 3-layer choppy-day protection.
//   - RTH and regime tagging.
//   - CSV telemetry for CH/NT parity review.
//   - Correct MNQ PnL accounting.
//
// Important:
//   This is a live-safe proxy implementation, not yet a true MBO/ClickHouse wall engine.
//   The signal core approximates event imbalance from tick-series price/volume movement.
//   Replace SignalEngine later with CH-validated MBO wall/event logic.
//
// File name / class name must match:
//   CG_T2_EventImbalance_ChoppyProtected_v1_2_FULL.cs
//
// Install:
//   Documents\NinjaTrader 8\bin\Custom\Strategies\CG_T2_EventImbalance_ChoppyProtected_v1_2_FULL.cs

#region Using declarations
using System;
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
    public class CG_T2_EventImbalance_ChoppyProtected_v1_2_FULL : Strategy
    {
        // ================================================================
        // Constants
        // ================================================================

        private const string LongSignalName  = "T2_Long";
        private const string ShortSignalName = "T2_Short";

        private const double MNQ_TICK_VALUE_USD = 0.50;
        private const double COMMISSION_RT_USD  = 0.70;

        // ================================================================
        // Internal state
        // ================================================================

        private long tradeIdCounter = 0;
        private long activeTradeId = 0;

        private DateTime lastEntryAttemptTime = Core.Globals.MinDate;
        private int lastSessionDate = -1;

        private bool pendingEntry = false;
        private bool protectionArmed = false;

        private double entryPrice = 0.0;
        private MarketPosition entryPosition = MarketPosition.Flat;
        private string activeSide = "";
        private string activeRegime = "";
        private DateTime activeEntryTime = Core.Globals.MinDate;

        private double activeHighSinceEntry = 0.0;
        private double activeLowSinceEntry = 0.0;
        private double activeMfeTicks = 0.0;
        private double activeMaeTicks = 0.0;

        // Protection/governor state
        private int consecutiveLosses = 0;
        private bool choppyDayDetected = false;
        private string choppyDayReason = "";

        private bool dailyMaxLossHit = false;
        private double sessionPnL = 0.0;
        private int sessionTrades = 0;

        private bool emergencyStopTriggered = false;
        private string emergencyStopReason = "";
        private double cumulativePnL = 0.0;
        private double peakCumulativePnL = 0.0;

        // Diagnostics
        private long rthRejects = 0;
        private long spreadRejects = 0;
        private long eventDeltaRejects = 0;
        private long imbalanceRejects = 0;
        private long cooldownRejects = 0;
        private long positionRejects = 0;
        private long pendingRejects = 0;
        private long choppyRejects = 0;
        private long dailyLossRejects = 0;
        private long emergencyRejects = 0;
        private long longSignals = 0;
        private long shortSignals = 0;
        private long entries = 0;
        private long exits = 0;

        // Feature state
        private double currentEventDelta = 0.0;
        private double currentEventImbalance = 0.0;
        private double currentTotalEvents = 0.0;
        private double currentSpreadTicks = 1.0;
        private double currentMomentumTicks = 0.0;
        private double currentVolumeProxy = 0.0;

        // Telemetry
        private StreamWriter telemetryWriter = null;
        private string telemetryPath = "";

        // ================================================================
        // NT lifecycle
        // ================================================================

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "T2 Event Imbalance full protected rewrite with choppy-day governors, OCO++ stop/target, telemetry, and live-safe single-position execution.";
                Name = "CG_T2_EventImbalance_ChoppyProtected_v1_2_FULL";

                Calculate = Calculate.OnEachTick;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                TimeInForce = TimeInForce.Day;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                BarsRequiredToTrade = 250;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;

                // Baseline T2 protected parameter set
                Quantity = 1;
                StopTicks = 16;
                TargetTicks = 32;

                StartTimeEt = 93500;
                EndTimeEt = 155900;
                CooldownSeconds = 0;
                MaxSpreadTicks = 8;

                EventLookbackBars = 200;
                MinEventDelta = 20.0;
                MinEventImbalance = 0.15;
                MinMomentumTicks = 0.0;
                UseRegimeFilter = true;
                DisableLunch = false;

                // Choppy protection
                EnableChoppyFilter = true;
                MaxConsecutiveLosses = 3;

                EnableDailyMaxLoss = true;
                DailyMaxLoss = 200.0;

                EnableEmergencyStop = true;
                EmergencyStopDD = 400.0;

                // Timeouts and safety
                MaxHoldSeconds = 900;
                EnableMaxHoldExit = true;

                EnableTelemetry = true;
                TelemetryFilePrefix = "CG_T2_ChoppyProtected_v1_2_FULL";
                PrintDiagnostics = true;
            }
            else if (State == State.Configure)
            {
                // 1-tick secondary series used for event imbalance proxy.
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                ResetSessionState();

                if (EnableTelemetry)
                    OpenTelemetry();

                if (PrintDiagnostics)
                {
                    Print("[CG_T2 v1.2] Loaded full protected strategy.");
                    Print("[CG_T2 v1.2] Signal core is proxy tick/event imbalance, not true MBO wall engine.");
                }
            }
            else if (State == State.Terminated)
            {
                PrintSummary();
                CloseTelemetry();
            }
        }

        // ================================================================
        // Main loop
        // ================================================================

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < BarsRequiredToTrade)
                return;

            if (CurrentBars.Length > 1 && CurrentBars[1] < Math.Max(EventLookbackBars + 2, 20))
                return;

            DateTime now = Time[0];
            DateTime etNow = ToEastern(now);
            int sessionDate = etNow.Year * 10000 + etNow.Month * 100 + etNow.Day;

            if (sessionDate != lastSessionDate)
            {
                ResetSessionState();
                lastSessionDate = sessionDate;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                UpdateMfeMae();
                CheckMaxHoldExit(now);
                positionRejects++;
                return;
            }

            if (!IsWithinRth(etNow))
            {
                rthRejects++;
                return;
            }

            string regime = GetSessionRegime(etNow);

            if (UseRegimeFilter && DisableLunch && regime == "LUNCH")
                return;

            // Layer 3: emergency stop blocks everything.
            if (EnableEmergencyStop && emergencyStopTriggered)
            {
                emergencyRejects++;
                return;
            }

            // Layer 1: choppy-day stop.
            if (EnableChoppyFilter && choppyDayDetected)
            {
                choppyRejects++;
                return;
            }

            // Layer 2: daily max loss.
            if (EnableDailyMaxLoss && dailyMaxLossHit)
            {
                dailyLossRejects++;
                return;
            }

            if (pendingEntry)
            {
                pendingRejects++;
                return;
            }

            double secondsSinceLastEntry = (now - lastEntryAttemptTime).TotalSeconds;
            if (secondsSinceLastEntry < CooldownSeconds)
            {
                cooldownRejects++;
                return;
            }

            ComputeEventFeatures();

            if (currentSpreadTicks > MaxSpreadTicks)
            {
                spreadRejects++;
                return;
            }

            bool longSignal = IsLongSignal();
            bool shortSignal = IsShortSignal();

            if (longSignal && shortSignal)
            {
                // Tie-breaker: favor dominant signed imbalance.
                if (currentEventDelta >= 0)
                    shortSignal = false;
                else
                    longSignal = false;
            }

            if (longSignal)
            {
                longSignals++;
                SubmitLong(now, regime);
            }
            else if (shortSignal)
            {
                shortSignals++;
                SubmitShort(now, regime);
            }
        }

        // ================================================================
        // Feature computation
        // ================================================================

        private void ComputeEventFeatures()
        {
            // Methodology:
            // This is a live-available proxy for MBO event imbalance.
            // It classifies 1-tick series movements into upward/downward event pressure,
            // weighted by volume when available. True CH parity should later replace this
            // with MBO add/cancel/fill state reconstruction.

            double upEvents = 0.0;
            double downEvents = 0.0;
            double totalVol = 0.0;

            int n = Math.Min(EventLookbackBars, CurrentBars[1] - 2);
            if (n <= 2)
            {
                currentEventDelta = 0.0;
                currentEventImbalance = 0.0;
                currentTotalEvents = 0.0;
                currentMomentumTicks = 0.0;
                currentVolumeProxy = 0.0;
                currentSpreadTicks = 1.0;
                return;
            }

            for (int i = 0; i < n; i++)
            {
                double vol = Math.Max(1.0, Volumes[1][i]);
                totalVol += vol;

                if (Closes[1][i] > Closes[1][i + 1])
                    upEvents += vol;
                else if (Closes[1][i] < Closes[1][i + 1])
                    downEvents += vol;
                else
                {
                    upEvents += vol * 0.5;
                    downEvents += vol * 0.5;
                }
            }

            currentTotalEvents = upEvents + downEvents;
            currentEventDelta = upEvents - downEvents;
            currentEventImbalance = currentTotalEvents > 0.0 ? currentEventDelta / currentTotalEvents : 0.0;
            currentMomentumTicks = (Close[0] - Close[Math.Min(10, CurrentBar)]) / TickSize;
            currentVolumeProxy = totalVol;

            double bid = GetCurrentBid(0);
            double ask = GetCurrentAsk(0);
            if (bid > 0.0 && ask > 0.0 && ask >= bid)
                currentSpreadTicks = Math.Max(1.0, (ask - bid) / TickSize);
            else
                currentSpreadTicks = 1.0;
        }

        private bool IsLongSignal()
        {
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

            if (currentMomentumTicks < MinMomentumTicks)
                return false;

            return true;
        }

        private bool IsShortSignal()
        {
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

            if (currentMomentumTicks > -MinMomentumTicks)
                return false;

            return true;
        }

        // ================================================================
        // Entries and protection
        // ================================================================

        private void SubmitLong(DateTime now, string regime)
        {
            if (Position.MarketPosition != MarketPosition.Flat || pendingEntry)
                return;

            ArmProtection(LongSignalName);

            activeTradeId = ++tradeIdCounter;
            activeSide = "LONG";
            activeRegime = regime;
            lastEntryAttemptTime = now;
            pendingEntry = true;
            protectionArmed = true;

            WriteTelemetry("SIGNAL", "LONG_SIGNAL");

            EnterLong(Quantity, LongSignalName);
        }

        private void SubmitShort(DateTime now, string regime)
        {
            if (Position.MarketPosition != MarketPosition.Flat || pendingEntry)
                return;

            ArmProtection(ShortSignalName);

            activeTradeId = ++tradeIdCounter;
            activeSide = "SHORT";
            activeRegime = regime;
            lastEntryAttemptTime = now;
            pendingEntry = true;
            protectionArmed = true;

            WriteTelemetry("SIGNAL", "SHORT_SIGNAL");

            EnterShort(Quantity, ShortSignalName);
        }

        private void ArmProtection(string signalName)
        {
            // OCO++ doctrine:
            // Set protective instructions before entry submission.
            SetStopLoss(signalName, CalculationMode.Ticks, StopTicks, false);
            SetProfitTarget(signalName, CalculationMode.Ticks, TargetTicks);
        }

        private void CheckMaxHoldExit(DateTime now)
        {
            if (!EnableMaxHoldExit)
                return;

            if (entryPosition == MarketPosition.Flat || activeEntryTime == Core.Globals.MinDate)
                return;

            if ((now - activeEntryTime).TotalSeconds < MaxHoldSeconds)
                return;

            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong("MaxHoldExit", LongSignalName);
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort("MaxHoldExit", ShortSignalName);
        }

        // ================================================================
        // Execution handling
        // ================================================================

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            string name = execution.Order.Name ?? "";

            bool isEntry =
                name == LongSignalName ||
                name == ShortSignalName;

            if (!isEntry)
            {
                // Exit execution: when account position is flat, close our active trade.
                if (Position.MarketPosition == MarketPosition.Flat && entryPosition != MarketPosition.Flat)
                    HandleExitExecution(price, time, name);

                return;
            }

            if (execution.Order.OrderState != OrderState.Filled &&
                execution.Order.OrderState != OrderState.PartFilled)
                return;

            if (marketPosition != MarketPosition.Flat)
                HandleEntryExecution(price, marketPosition, time);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null)
                return;

            string name = order.Name ?? "";

            if ((name == LongSignalName || name == ShortSignalName) &&
                (orderState == OrderState.Rejected || orderState == OrderState.Cancelled))
            {
                pendingEntry = false;

                if (orderState == OrderState.Rejected)
                {
                    WriteTelemetry("ORDER_REJECT", nativeError ?? "ENTRY_REJECTED");

                    if (Position.MarketPosition != MarketPosition.Flat)
                    {
                        if (Position.MarketPosition == MarketPosition.Long)
                            ExitLong("EntryRejectFlatten", LongSignalName);
                        else if (Position.MarketPosition == MarketPosition.Short)
                            ExitShort("EntryRejectFlatten", ShortSignalName);
                    }
                }
            }
        }

        private void HandleEntryExecution(double price, MarketPosition marketPosition, DateTime time)
        {
            entryPrice = price;
            entryPosition = marketPosition;
            activeEntryTime = time;
            pendingEntry = false;
            entries++;

            activeHighSinceEntry = price;
            activeLowSinceEntry = price;
            activeMfeTicks = 0.0;
            activeMaeTicks = 0.0;

            WriteTelemetry("ENTRY", "ENTRY_FILLED");
        }

        private void HandleExitExecution(double exitPrice, DateTime time, string exitOrderName)
        {
            double tradePnl = ComputeNetPnL(entryPrice, exitPrice, entryPosition == MarketPosition.Long);

            sessionTrades++;
            exits++;

            sessionPnL += tradePnl;
            cumulativePnL += tradePnl;
            if (cumulativePnL > peakCumulativePnL)
                peakCumulativePnL = cumulativePnL;

            CheckProtectionLayers(tradePnl);

            WriteTelemetry("EXIT", "EXIT_" + exitOrderName + "_PNL_" + tradePnl.ToString("0.00", CultureInfo.InvariantCulture));

            ResetTradeState();
        }

        private double ComputeNetPnL(double entry, double exit, bool isLong)
        {
            double ticks = isLong ? (exit - entry) / TickSize : (entry - exit) / TickSize;
            double gross = ticks * MNQ_TICK_VALUE_USD;
            return gross - COMMISSION_RT_USD;
        }

        private void ResetTradeState()
        {
            activeTradeId = 0;
            activeSide = "";
            activeRegime = "";
            activeEntryTime = Core.Globals.MinDate;

            entryPrice = 0.0;
            entryPosition = MarketPosition.Flat;

            activeHighSinceEntry = 0.0;
            activeLowSinceEntry = 0.0;
            activeMfeTicks = 0.0;
            activeMaeTicks = 0.0;

            pendingEntry = false;
            protectionArmed = false;
        }

        private void UpdateMfeMae()
        {
            if (entryPosition == MarketPosition.Flat || entryPrice <= 0.0)
                return;

            activeHighSinceEntry = Math.Max(activeHighSinceEntry, High[0]);
            activeLowSinceEntry = Math.Min(activeLowSinceEntry, Low[0]);

            if (entryPosition == MarketPosition.Long)
            {
                activeMfeTicks = Math.Max(activeMfeTicks, (activeHighSinceEntry - entryPrice) / TickSize);
                activeMaeTicks = Math.Max(activeMaeTicks, (entryPrice - activeLowSinceEntry) / TickSize);
            }
            else if (entryPosition == MarketPosition.Short)
            {
                activeMfeTicks = Math.Max(activeMfeTicks, (entryPrice - activeLowSinceEntry) / TickSize);
                activeMaeTicks = Math.Max(activeMaeTicks, (activeHighSinceEntry - entryPrice) / TickSize);
            }
        }

        // ================================================================
        // Protection layers
        // ================================================================

        private void CheckProtectionLayers(double tradePnL)
        {
            // Layer 1: consecutive loss choppy-day detector.
            if (EnableChoppyFilter && !choppyDayDetected)
            {
                if (tradePnL < 0.0)
                    consecutiveLosses++;
                else
                    consecutiveLosses = 0;

                if (consecutiveLosses >= MaxConsecutiveLosses)
                {
                    choppyDayDetected = true;
                    choppyDayReason = consecutiveLosses + " consecutive losses";
                    WriteTelemetry("PROTECTION", "CHOPPY_FILTER_TRIGGERED");
                    if (PrintDiagnostics)
                        Print("[PROTECTION] Choppy day filter triggered: " + choppyDayReason);
                }
            }

            // Layer 2: daily loss stop.
            if (EnableDailyMaxLoss && !dailyMaxLossHit)
            {
                if (sessionPnL <= -DailyMaxLoss)
                {
                    dailyMaxLossHit = true;
                    WriteTelemetry("PROTECTION", "DAILY_MAX_LOSS_TRIGGERED");
                    if (PrintDiagnostics)
                        Print("[PROTECTION] Daily max loss triggered: " + sessionPnL.ToString("0.00"));
                }
            }

            // Layer 3: cumulative DD stop from peak.
            if (EnableEmergencyStop && !emergencyStopTriggered)
            {
                double ddFromPeak = peakCumulativePnL - cumulativePnL;
                if (ddFromPeak >= EmergencyStopDD)
                {
                    emergencyStopTriggered = true;
                    emergencyStopReason = "Cumulative peak-to-valley DD $" + ddFromPeak.ToString("0.00");
                    WriteTelemetry("PROTECTION", "EMERGENCY_STOP_TRIGGERED");
                    if (PrintDiagnostics)
                        Print("[PROTECTION] Emergency stop triggered: " + emergencyStopReason);
                }
            }
        }

        private void ResetSessionState()
        {
            consecutiveLosses = 0;
            choppyDayDetected = false;
            choppyDayReason = "";

            dailyMaxLossHit = false;
            sessionPnL = 0.0;
            sessionTrades = 0;

            pendingEntry = false;

            if (PrintDiagnostics)
                Print("[SESSION] Reset T2 protected session state.");
        }

        // ================================================================
        // Time / regime
        // ================================================================

        private bool IsWithinRth(DateTime et)
        {
            int hhmmss = et.Hour * 10000 + et.Minute * 100 + et.Second;
            return hhmmss >= StartTimeEt && hhmmss <= EndTimeEt;
        }

        private DateTime ToEastern(DateTime t)
        {
            try
            {
                TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                if (t.Kind == DateTimeKind.Utc)
                    return TimeZoneInfo.ConvertTimeFromUtc(t, eastern);

                // NT chart times are often local/session times. If already unspecified/local,
                // treat as chart/session time rather than forcibly converting twice.
                return t;
            }
            catch
            {
                return t;
            }
        }

        private string GetSessionRegime(DateTime et)
        {
            int hhmmss = et.Hour * 10000 + et.Minute * 100 + et.Second;

            if (hhmmss < 94500) return "OPEN_15";
            if (hhmmss < 103000) return "POST_OPEN";
            if (hhmmss >= 113000 && hhmmss < 133000) return "LUNCH";
            if (hhmmss >= 153000) return "CLOSE_30";
            return "NORMAL";
        }

        // ================================================================
        // Telemetry
        // ================================================================

        private void OpenTelemetry()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "trace");

                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                telemetryPath = Path.Combine(dir,
                    TelemetryFilePrefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");

                telemetryWriter = new StreamWriter(telemetryPath, false);
                telemetryWriter.WriteLine(
                    "record_type,trade_id,time,side,regime,event_delta,event_imbalance,total_events,momentum_ticks,spread_ticks,entry_price,mfe_ticks,mae_ticks,session_pnl,cumulative_pnl,consecutive_losses,choppy,daily_loss_hit,emergency_stop,diagnostic");
                telemetryWriter.Flush();

                if (PrintDiagnostics)
                    Print("[TELEMETRY] " + telemetryPath);
            }
            catch (Exception ex)
            {
                Print("[TELEMETRY ERROR] " + ex.Message);
            }
        }

        private void WriteTelemetry(string recordType, string diagnostic)
        {
            if (!EnableTelemetry || telemetryWriter == null)
                return;

            try
            {
                string line = string.Join(",",
                    Csv(recordType),
                    activeTradeId.ToString(CultureInfo.InvariantCulture),
                    Csv(Time[0].ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                    Csv(activeSide),
                    Csv(activeRegime),
                    currentEventDelta.ToString("0.####", CultureInfo.InvariantCulture),
                    currentEventImbalance.ToString("0.####", CultureInfo.InvariantCulture),
                    currentTotalEvents.ToString("0.####", CultureInfo.InvariantCulture),
                    currentMomentumTicks.ToString("0.####", CultureInfo.InvariantCulture),
                    currentSpreadTicks.ToString("0.####", CultureInfo.InvariantCulture),
                    entryPrice.ToString("0.####", CultureInfo.InvariantCulture),
                    activeMfeTicks.ToString("0.####", CultureInfo.InvariantCulture),
                    activeMaeTicks.ToString("0.####", CultureInfo.InvariantCulture),
                    sessionPnL.ToString("0.####", CultureInfo.InvariantCulture),
                    cumulativePnL.ToString("0.####", CultureInfo.InvariantCulture),
                    consecutiveLosses.ToString(CultureInfo.InvariantCulture),
                    choppyDayDetected ? "1" : "0",
                    dailyMaxLossHit ? "1" : "0",
                    emergencyStopTriggered ? "1" : "0",
                    Csv(diagnostic)
                );

                telemetryWriter.WriteLine(line);
                telemetryWriter.Flush();
            }
            catch { }
        }

        private string Csv(string s)
        {
            if (s == null)
                s = "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private void CloseTelemetry()
        {
            try
            {
                if (telemetryWriter != null)
                {
                    telemetryWriter.Flush();
                    telemetryWriter.Close();
                    telemetryWriter.Dispose();
                    telemetryWriter = null;
                }
            }
            catch { }
        }

        private void PrintSummary()
        {
            if (!PrintDiagnostics)
                return;

            Print("=== CG_T2_EVENT_IMBALANCE_CHOPPY_PROTECTED_v1_2_FULL SUMMARY ===");
            Print("Entries: " + entries);
            Print("Exits: " + exits);
            Print("Session trades: " + sessionTrades);
            Print("Session PnL: $" + sessionPnL.ToString("0.00"));
            Print("Cumulative PnL: $" + cumulativePnL.ToString("0.00"));
            Print("Consecutive losses: " + consecutiveLosses);
            Print("Choppy day: " + choppyDayDetected + " " + choppyDayReason);
            Print("Daily max loss hit: " + dailyMaxLossHit);
            Print("Emergency stop: " + emergencyStopTriggered + " " + emergencyStopReason);
            Print("--- Rejects ---");
            Print("RTH: " + rthRejects);
            Print("Spread: " + spreadRejects);
            Print("EventDelta: " + eventDeltaRejects);
            Print("Imbalance: " + imbalanceRejects);
            Print("Cooldown: " + cooldownRejects);
            Print("Position: " + positionRejects);
            Print("Pending: " + pendingRejects);
            Print("Choppy: " + choppyRejects);
            Print("DailyLoss: " + dailyLossRejects);
            Print("Emergency: " + emergencyRejects);
            Print("--- Signals ---");
            Print("Long: " + longSignals);
            Print("Short: " + shortSignals);
        }

        // ================================================================
        // Parameters
        // ================================================================

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Quantity", Order = 1, GroupName = "01. Execution")]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "StopTicks", Order = 2, GroupName = "01. Execution")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 400)]
        [Display(Name = "TargetTicks", Order = 3, GroupName = "01. Execution")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableMaxHoldExit", Order = 4, GroupName = "01. Execution")]
        public bool EnableMaxHoldExit { get; set; }

        [NinjaScriptProperty]
        [Range(10, 7200)]
        [Display(Name = "MaxHoldSeconds", Order = 5, GroupName = "01. Execution")]
        public int MaxHoldSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "StartTimeEt", Order = 1, GroupName = "02. Session")]
        public int StartTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "EndTimeEt", Order = 2, GroupName = "02. Session")]
        public int EndTimeEt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseRegimeFilter", Order = 3, GroupName = "02. Session")]
        public bool UseRegimeFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "DisableLunch", Order = 4, GroupName = "02. Session")]
        public bool DisableLunch { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "CooldownSeconds", Order = 1, GroupName = "03. Filters")]
        public int CooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "MaxSpreadTicks", Order = 2, GroupName = "03. Filters")]
        public double MaxSpreadTicks { get; set; }

        [NinjaScriptProperty]
        [Range(10, 5000)]
        [Display(Name = "EventLookbackBars", Order = 3, GroupName = "03. Filters")]
        public int EventLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(Name = "MinEventDelta", Order = 4, GroupName = "03. Filters")]
        public double MinEventDelta { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "MinEventImbalance", Order = 5, GroupName = "03. Filters")]
        public double MinEventImbalance { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "MinMomentumTicks", Order = 6, GroupName = "03. Filters")]
        public double MinMomentumTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableChoppyFilter", Order = 1, GroupName = "04. Protection")]
        public bool EnableChoppyFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxConsecutiveLosses", Order = 2, GroupName = "04. Protection")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableDailyMaxLoss", Order = 3, GroupName = "04. Protection")]
        public bool EnableDailyMaxLoss { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "DailyMaxLoss", Order = 4, GroupName = "04. Protection")]
        public double DailyMaxLoss { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableEmergencyStop", Order = 5, GroupName = "04. Protection")]
        public bool EnableEmergencyStop { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "EmergencyStopDD", Order = 6, GroupName = "04. Protection")]
        public double EmergencyStopDD { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableTelemetry", Order = 1, GroupName = "05. Telemetry")]
        public bool EnableTelemetry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TelemetryFilePrefix", Order = 2, GroupName = "05. Telemetry")]
        public string TelemetryFilePrefix { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PrintDiagnostics", Order = 3, GroupName = "05. Telemetry")]
        public bool PrintDiagnostics { get; set; }
    }
}
