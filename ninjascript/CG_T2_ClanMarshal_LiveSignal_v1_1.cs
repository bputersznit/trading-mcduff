// CG_T2_ClanMarshal_LiveSignal_v1_1.cs
// NinjaTrader 8 Strategy
//
// Purpose:
//   T2 ClanMarshal LiveSignal v1.1 execution chassis.
//   Adds battlefield observability, session regime tagging, loss governor,
//   opening-drive suppression, dynamic threshold adjustment, and CSV telemetry.
//
// Doctrine:
//   Survival first.
//   Execution integrity second.
//   CH/NT parity measurement third.
//   Profit scale only after validated repeatability.
//
// Notes:
//   1. This is a runnable NT8 strategy file, but its alpha signal layer is still a
//      proxy implementation intended for calibration against your ClickHouse baseline.
//   2. Protective exits use SetStopLoss / SetProfitTarget before entry submission.
//      In managed NT8 strategies, these generate protective broker-side exit orders
//      after entry execution when supported by connection/order handling.
//   3. The strategy never intentionally enters without a configured stop/target.
//   4. CSV telemetry is written to Documents/NinjaTrader 8/trace by default.
//
// Installation:
//   NinjaTrader 8 -> New -> NinjaScript Editor -> Strategies -> right click -> New Strategy
//   Name it: CG_T2_ClanMarshal_LiveSignal_v1_1
//   Replace the generated file content with this entire file.
//   Compile.

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
public class CG_T2_ClanMarshal_LiveSignal_v1_1 : Strategy
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
    private long wallRejects = 0;
    private long deltaRejects = 0;
    private long momentumRejects = 0;
    private long cooldownRejects = 0;
    private long pendingRejects = 0;
    private long governorRejects = 0;
    private long limitTimeouts = 0;
    private long fallbackMarkets = 0;
    private long fallbackSkips = 0;
    private long signalLongCount = 0;
    private long signalShortCount = 0;

    // Latest computed proxies
    private double currentWallScoreProxy = 0.0;
    private double currentDeltaProxy = 0.0;
    private double currentEventDeltaProxy = 0.0;
    private double currentMomentumTicks = 0.0;
    private double currentSpreadTicks = 1.0;
    private double currentFlowRate = 0.0;

    // ================================================================
    // NT lifecycle
    // ================================================================

    protected override void OnStateChange()
    {
        if (State == State.SetDefaults)
        {
            Description = "T2 ClanMarshal LiveSignal v1.1: OCO++ chassis with telemetry, regimes, loss governor, opening suppression, and proxy parity calibration.";
            Name = "CG_T2_ClanMarshal_LiveSignal_v1_1";

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

            StopTicks = 24;
            TargetTicks = 60;
            MaxHoldSeconds = 900;

            // Session controls
            StartTimeEt = 93500;
            EndTimeEt = 155900;
            RthBufferSeconds = 0;
            OneTradePerSession = false;

            // Baseline controlled-density settings from v8.3
            CooldownSeconds = 30;
            EmergencyCooldownSeconds = 60;
            MaxSpreadTicks = 8;

            MinWallScoreProxy = 5.0;
            MinDeltaAbs = 10.0;
            MinEventCountDeltaProxy = -250.0;
            MomentumConfirmTicks = 1.0;

            LowFlowMarketThreshold = 5000.0;
            MediumFlowMarketThreshold = 15000.0;

            // Regime/suppression controls
            EnableRegimeTagging = true;
            EnableLossGovernor = true;
            LossGovernorLossesForCooldown = 2;
            LossGovernorLossesForPause = 3;
            LossGovernorPauseMinutes = 20;
            LossGovernorCooldownMultiplier = 2.0;

            EnableOpeningSuppressor = true;
            OpeningWallAdd = 2.0;
            OpeningDeltaAdd = 7.0;
            OpeningCooldownAddSeconds = 15;

            EnableDynamicThresholds = true;
            HighSpreadWallAdd = 2.0;
            HighSpreadDeltaAdd = 5.0;
            LowFlowWallAdd = 3.0;
            LowFlowCooldownAddSeconds = 10;

            // Proxy signal model defaults
            FastLookbackTicks = 20;
            SlowLookbackTicks = 80;
            FlowLookbackBars = 20;

            // Telemetry
            EnableTelemetry = true;
            TelemetryFilePrefix = "CG_T2_ClanMarshal_v1_1";
            PrintDiagnostics = true;
        }
        else if (State == State.Configure)
        {
            // Add a 1-second series to provide a stable low-cost telemetry heartbeat.
            AddDataSeries(BarsPeriodType.Second, 1);
        }
        else if (State == State.DataLoaded)
        {
            ResetSessionState();
            OpenTelemetry();
        }
        else if (State == State.Terminated)
        {
            WriteDiagnosticFooter();
            CloseTelemetry();
        }
    }

    protected override void OnBarUpdate()
    {
        if (CurrentBars[0] < Math.Max(SlowLookbackTicks + 5, 100))
            return;

        if (BarsInProgress != 0)
            return;

        DateTime now = Times[0][0];
        int sessionDate = ToDay(now);
        if (sessionDate != lastSessionDate)
        {
            ResetSessionState();
            lastSessionDate = sessionDate;
        }

        UpdateActiveTradeMfeMae();
        CheckMaxHoldExit(now);
        CheckPendingLimitTimeout(now);

        if (Position.MarketPosition != MarketPosition.Flat)
            return;

        if (!IsWithinRth(now))
        {
            rthRejects++;
            return;
        }

        if (OneTradePerSession && sessionTrades > 0)
            return;

        if (pendingLimitActive)
        {
            pendingRejects++;
            return;
        }

        if (EnableLossGovernor && LossGovernorActive(now))
        {
            governorRejects++;
            return;
        }

        string regime = EnableRegimeTagging ? GetSessionRegime(now) : "NA";

        ComputeProxyFeatures();

        double effectiveMinWall = MinWallScoreProxy;
        double effectiveMinDelta = MinDeltaAbs;
        int effectiveCooldownSeconds = CooldownSeconds;

        ApplyOpeningSuppressor(regime, ref effectiveMinWall, ref effectiveMinDelta, ref effectiveCooldownSeconds);
        ApplyDynamicThresholds(ref effectiveMinWall, ref effectiveMinDelta, ref effectiveCooldownSeconds);
        ApplyLossGovernorCooldown(ref effectiveCooldownSeconds);

        if ((now - lastEntryAttemptTime).TotalSeconds < effectiveCooldownSeconds)
        {
            cooldownRejects++;
            return;
        }

        if (currentSpreadTicks > MaxSpreadTicks)
        {
            spreadRejects++;
            return;
        }

        bool longSignal = IsLongSignal(effectiveMinWall, effectiveMinDelta);
        bool shortSignal = IsShortSignal(effectiveMinWall, effectiveMinDelta);

        if (!longSignal && !shortSignal)
            return;

        // Tie-breaker: if both fire, favor stronger side by signed delta/momentum.
        if (longSignal && shortSignal)
        {
            if (currentDeltaProxy >= 0)
                shortSignal = false;
            else
                longSignal = false;
        }

        if (longSignal)
        {
            signalLongCount++;
            SubmitLong(now, regime, effectiveCooldownSeconds);
        }
        else if (shortSignal)
        {
            signalShortCount++;
            SubmitShort(now, regime, effectiveCooldownSeconds);
        }
    }

    protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
        MarketPosition marketPosition, string orderId, DateTime time)
    {
        if (execution == null || execution.Order == null)
            return;

        string orderName = execution.Order.Name ?? "";

        if (orderName == LongSignalName || orderName == ShortSignalName)
        {
            if (execution.Order.OrderState == OrderState.Filled || execution.Order.OrderState == OrderState.PartFilled)
            {
                OnEntryExecution(orderName, price, time);
            }
        }

        // Detect transition to flat after an exit.
        if (Position.MarketPosition == MarketPosition.Flat && entryTelemetryStarted && activeTradeId > 0)
        {
            OnFlatAfterExit(time);
        }
    }

    protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
        double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
    {
        if (order == null)
            return;

        string orderName = order.Name ?? "";

        if ((orderName == LongSignalName || orderName == ShortSignalName) &&
            (orderState == OrderState.Cancelled || orderState == OrderState.Rejected))
        {
            pendingLimitActive = false;
            pendingLong = false;
            pendingShort = false;

            if (orderState == OrderState.Rejected)
            {
                lastExitReason = "ENTRY_REJECTED";
                if (Position.MarketPosition != MarketPosition.Flat)
                    ExitAll("EntryRejectedProtection");
            }
        }
    }

    // ================================================================
    // Signal proxy layer
    // ================================================================

    private void ComputeProxyFeatures()
    {
        // Strategy/methodology:
        //   This proxy layer approximates CH-derived wall/delta/flow primitives
        //   from available chart/tick data. It is intentionally transparent so
        //   each component can be calibrated against CH exports.

        double fastMoveTicks = (Close[0] - Close[Math.Min(FastLookbackTicks, CurrentBar)]) / TickSize;
        double slowMoveTicks = (Close[0] - Close[Math.Min(SlowLookbackTicks, CurrentBar)]) / TickSize;

        double upVol = 0.0;
        double downVol = 0.0;
        double eventCount = 0.0;

        int n = Math.Min(FlowLookbackBars, CurrentBar);
        for (int i = 0; i < n; i++)
        {
            eventCount += 1.0;

            if (Close[i] > Close[i + 1])
                upVol += Volume[i];
            else if (Close[i] < Close[i + 1])
                downVol += Volume[i];
            else
            {
                upVol += Volume[i] * 0.5;
                downVol += Volume[i] * 0.5;
            }
        }

        currentDeltaProxy = upVol - downVol;
        currentMomentumTicks = fastMoveTicks;
        currentEventDeltaProxy = eventCount - SlowLookbackTicks;
        currentFlowRate = upVol + downVol;

        // Wall-score proxy:
        //   Treat compression + directional response as a pseudo-wall interaction.
        //   Higher score means stronger local participation/response.
        double absFast = Math.Abs(fastMoveTicks);
        double absSlow = Math.Abs(slowMoveTicks);
        double flowNorm = Math.Log10(Math.Max(10.0, currentFlowRate));
        currentWallScoreProxy = Math.Max(0.0, (absFast * 0.65) + (absSlow * 0.20) + flowNorm);

        // In historical playback without true bid/ask spread, default to one tick.
        // In live work, replace this with GetCurrentBid/GetCurrentAsk spread if desired.
        double bid = GetCurrentBid();
        double ask = GetCurrentAsk();
        if (bid > 0 && ask > 0 && ask >= bid)
            currentSpreadTicks = Math.Max(1.0, (ask - bid) / TickSize);
        else
            currentSpreadTicks = 1.0;
    }

    private bool IsLongSignal(double effectiveMinWall, double effectiveMinDelta)
    {
        if (currentWallScoreProxy < effectiveMinWall)
        {
            wallRejects++;
            return false;
        }

        if (currentDeltaProxy < effectiveMinDelta)
        {
            deltaRejects++;
            return false;
        }

        if (currentEventDeltaProxy < MinEventCountDeltaProxy)
            return false;

        if (currentMomentumTicks < MomentumConfirmTicks)
        {
            momentumRejects++;
            return false;
        }

        return true;
    }

    private bool IsShortSignal(double effectiveMinWall, double effectiveMinDelta)
    {
        if (currentWallScoreProxy < effectiveMinWall)
            return false;

        if (currentDeltaProxy > -effectiveMinDelta)
            return false;

        if (currentEventDeltaProxy < MinEventCountDeltaProxy)
            return false;

        if (currentMomentumTicks > -MomentumConfirmTicks)
            return false;

        return true;
    }

    // ================================================================
    // Entry / exit execution
    // ================================================================

    private void SubmitLong(DateTime now, string regime, int effectiveCooldownSeconds)
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

        WriteSignalTelemetry(activeTradeId, now, activeSide, regime, true, false, false, "SIGNAL_SUBMITTED");

        if (UseLimitFirst)
        {
            double limitPrice = Close[0] - (LimitOffsetTicks * TickSize);
            EnterLongLimit(Quantity, limitPrice, LongSignalName);
        }
        else
        {
            EnterLong(Quantity, LongSignalName);
        }
    }

    private void SubmitShort(DateTime now, string regime, int effectiveCooldownSeconds)
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

        WriteSignalTelemetry(activeTradeId, now, activeSide, regime, true, false, false, "SIGNAL_SUBMITTED");

        if (UseLimitFirst)
        {
            double limitPrice = Close[0] + (LimitOffsetTicks * TickSize);
            EnterShortLimit(Quantity, limitPrice, ShortSignalName);
        }
        else
        {
            EnterShort(Quantity, ShortSignalName);
        }
    }

    private void ArmProtection(string fromEntrySignal)
    {
        // OCO++ doctrine:
        //   Protection instructions are registered before the entry is submitted.
        //   NT managed engine attaches the stop/target to the corresponding entry signal.
        SetStopLoss(fromEntrySignal, CalculationMode.Ticks, StopTicks, false);
        SetProfitTarget(fromEntrySignal, CalculationMode.Ticks, TargetTicks);
    }

    private void CheckPendingLimitTimeout(DateTime now)
    {
        if (!pendingLimitActive || !UseLimitFirst)
            return;

        if ((now - pendingSubmitTime).TotalSeconds < LimitTimeoutSeconds)
            return;

        limitTimeouts++;
        pendingLimitActive = false;

        if (UseMarketFallback && Position.MarketPosition == MarketPosition.Flat)
        {
            fallbackMarkets++;
            lastFallbackReason = "LIMIT_TIMEOUT_MARKET_FALLBACK";

            if (pendingLong)
                EnterLong(Quantity, LongSignalName);
            else if (pendingShort)
                EnterShort(Quantity, ShortSignalName);
        }
        else
        {
            fallbackSkips++;
            lastFallbackReason = "LIMIT_TIMEOUT_NO_FALLBACK";
            pendingLong = false;
            pendingShort = false;
        }
    }

    private void OnEntryExecution(string orderName, double fillPrice, DateTime time)
    {
        pendingLimitActive = false;
        pendingLong = false;
        pendingShort = false;

        entryPrice = fillPrice;
        activeMaxPrice = fillPrice;
        activeMinPrice = fillPrice;
        activeMfeTicks = 0.0;
        activeMaeTicks = 0.0;
        cumulativePnlAtEntry = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
        entryTelemetryStarted = true;
        sessionTrades++;

        WriteSignalTelemetry(activeTradeId, time, activeSide, activeRegime, true, true,
            lastFallbackReason == "LIMIT_TIMEOUT_MARKET_FALLBACK", "ENTRY_FILLED");
    }

    private void CheckMaxHoldExit(DateTime now)
    {
        if (Position.MarketPosition == MarketPosition.Flat || !entryTelemetryStarted)
            return;

        if ((now - pendingSubmitTime).TotalSeconds < MaxHoldSeconds)
            return;

        lastExitReason = "MAX_HOLD";
        ExitAll("MaxHoldExit");
    }

    private void ExitAll(string signalName)
    {
        if (Position.MarketPosition == MarketPosition.Long)
            ExitLong(signalName, LongSignalName);
        else if (Position.MarketPosition == MarketPosition.Short)
            ExitShort(signalName, ShortSignalName);
    }

    private void OnFlatAfterExit(DateTime time)
    {
        double cumPnlNow = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
        double tradePnl = cumPnlNow - cumulativePnlAtEntry;

        RegisterTradeOutcome(tradePnl, time);

        WriteExitTelemetry(activeTradeId, time, activeSide, activeRegime, tradePnl,
            string.IsNullOrEmpty(lastExitReason) ? "PROTECTIVE_OR_MANAGED_EXIT" : lastExitReason);

        activeTradeId = 0;
        activeSide = "";
        activeRegime = "";
        entryPrice = 0.0;
        entryTelemetryStarted = false;
        protectionArmed = false;
        lastExitReason = "";
        lastFlatTime = time;
    }

    private void UpdateActiveTradeMfeMae()
    {
        if (Position.MarketPosition == MarketPosition.Flat || !entryTelemetryStarted)
            return;

        activeMaxPrice = Math.Max(activeMaxPrice, High[0]);
        activeMinPrice = Math.Min(activeMinPrice, Low[0]);

        if (Position.MarketPosition == MarketPosition.Long)
        {
            activeMfeTicks = Math.Max(activeMfeTicks, (activeMaxPrice - entryPrice) / TickSize);
            activeMaeTicks = Math.Max(activeMaeTicks, (entryPrice - activeMinPrice) / TickSize);
        }
        else if (Position.MarketPosition == MarketPosition.Short)
        {
            activeMfeTicks = Math.Max(activeMfeTicks, (entryPrice - activeMinPrice) / TickSize);
            activeMaeTicks = Math.Max(activeMaeTicks, (activeMaxPrice - entryPrice) / TickSize);
        }
    }

    // ================================================================
    // Regime / governor / thresholds
    // ================================================================

    private string GetSessionRegime(DateTime t)
    {
        int hhmmss = ToTime(t);

        if (hhmmss < 100000) return "OPENING_DRIVE";
        if (hhmmss < 113000) return "MID_MORNING";
        if (hhmmss < 133000) return "LUNCH";
        if (hhmmss < 153000) return "POWER_HOUR";
        return "CLOSE";
    }

    private bool LossGovernorActive(DateTime now)
    {
        return now < governorResumeTime;
    }

    private void RegisterTradeOutcome(double pnl, DateTime now)
    {
        if (!EnableLossGovernor)
            return;

        if (pnl < 0)
            consecutiveLosses++;
        else
            consecutiveLosses = 0;

        if (consecutiveLosses >= LossGovernorLossesForPause)
            governorResumeTime = now.AddMinutes(LossGovernorPauseMinutes);
    }

    private void ApplyLossGovernorCooldown(ref int effectiveCooldownSeconds)
    {
        if (!EnableLossGovernor)
            return;

        if (consecutiveLosses >= LossGovernorLossesForCooldown)
            effectiveCooldownSeconds = (int)Math.Round(effectiveCooldownSeconds * LossGovernorCooldownMultiplier);
    }

    private void ApplyOpeningSuppressor(string regime, ref double minWall, ref double minDelta, ref int cooldown)
    {
        if (!EnableOpeningSuppressor)
            return;

        if (regime != "OPENING_DRIVE")
            return;

        minWall += OpeningWallAdd;
        minDelta += OpeningDeltaAdd;
        cooldown += OpeningCooldownAddSeconds;
    }

    private void ApplyDynamicThresholds(ref double minWall, ref double minDelta, ref int cooldown)
    {
        if (!EnableDynamicThresholds)
            return;

        if (currentSpreadTicks > Math.Max(1.0, MaxSpreadTicks * 0.75))
        {
            minWall += HighSpreadWallAdd;
            minDelta += HighSpreadDeltaAdd;
        }

        if (currentFlowRate < LowFlowMarketThreshold)
        {
            minWall += LowFlowWallAdd;
            cooldown += LowFlowCooldownAddSeconds;
        }
    }

    private bool IsWithinRth(DateTime t)
    {
        int hhmmss = ToTime(t);
        return hhmmss >= StartTimeEt && hhmmss <= EndTimeEt;
    }

    private void ResetSessionState()
    {
        sessionTrades = 0;
        consecutiveLosses = 0;
        governorResumeTime = Core.Globals.MinDate;
        pendingLimitActive = false;
        pendingLong = false;
        pendingShort = false;
    }

    // ================================================================
    // Telemetry
    // ================================================================

    private void OpenTelemetry()
    {
        if (!EnableTelemetry)
            return;

        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NinjaTrader 8", "trace");

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            telemetryPath = Path.Combine(dir,
                TelemetryFilePrefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");

            telemetryWriter = new StreamWriter(telemetryPath, false);
            telemetryWriter.WriteLine("record_type,trade_id,time,side,regime,wall_score,delta_proxy,event_delta,momentum_ticks,spread_ticks,flow_rate,limit_submitted,limit_filled,market_fallback,fallback_reason,cooldown_active,loss_governor_active,mfe_ticks,mae_ticks,pnl_usd,exit_reason,diagnostic");
            telemetryWriter.Flush();

            if (PrintDiagnostics)
                Print("[Telemetry] Writing: " + telemetryPath);
        }
        catch (Exception ex)
        {
            Print("[Telemetry ERROR] " + ex.Message);
        }
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

    private void WriteSignalTelemetry(long tradeId, DateTime time, string side, string regime,
        bool limitSubmitted, bool limitFilled, bool marketFallback, string diagnostic)
    {
        if (!EnableTelemetry || telemetryWriter == null)
            return;

        bool cooldownActive = (time - lastEntryAttemptTime).TotalSeconds < CooldownSeconds;
        bool lossGovActive = EnableLossGovernor && LossGovernorActive(time);

        WriteCsvLine("SIGNAL", tradeId, time, side, regime,
            currentWallScoreProxy, currentDeltaProxy, currentEventDeltaProxy, currentMomentumTicks,
            currentSpreadTicks, currentFlowRate, limitSubmitted, limitFilled, marketFallback,
            lastFallbackReason, cooldownActive, lossGovActive, activeMfeTicks, activeMaeTicks,
            0.0, "", diagnostic);
    }

    private void WriteExitTelemetry(long tradeId, DateTime time, string side, string regime, double pnl, string exitReason)
    {
        if (!EnableTelemetry || telemetryWriter == null)
            return;

        WriteCsvLine("EXIT", tradeId, time, side, regime,
            currentWallScoreProxy, currentDeltaProxy, currentEventDeltaProxy, currentMomentumTicks,
            currentSpreadTicks, currentFlowRate, false, true,
            lastFallbackReason == "LIMIT_TIMEOUT_MARKET_FALLBACK", lastFallbackReason,
            false, EnableLossGovernor && LossGovernorActive(time), activeMfeTicks, activeMaeTicks,
            pnl, exitReason, "TRADE_CLOSED");
    }

    private void WriteCsvLine(string recordType, long tradeId, DateTime time, string side, string regime,
        double wall, double delta, double eventDelta, double momentum, double spread, double flow,
        bool limitSubmitted, bool limitFilled, bool marketFallback, string fallbackReason,
        bool cooldownActive, bool lossGovernorActive, double mfe, double mae, double pnl,
        string exitReason, string diagnostic)
    {
        try
        {
            telemetryWriter.WriteLine(string.Join(",",
                Csv(recordType),
                tradeId.ToString(CultureInfo.InvariantCulture),
                Csv(time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                Csv(side),
                Csv(regime),
                wall.ToString("0.####", CultureInfo.InvariantCulture),
                delta.ToString("0.####", CultureInfo.InvariantCulture),
                eventDelta.ToString("0.####", CultureInfo.InvariantCulture),
                momentum.ToString("0.####", CultureInfo.InvariantCulture),
                spread.ToString("0.####", CultureInfo.InvariantCulture),
                flow.ToString("0.####", CultureInfo.InvariantCulture),
                limitSubmitted ? "1" : "0",
                limitFilled ? "1" : "0",
                marketFallback ? "1" : "0",
                Csv(fallbackReason),
                cooldownActive ? "1" : "0",
                lossGovernorActive ? "1" : "0",
                mfe.ToString("0.####", CultureInfo.InvariantCulture),
                mae.ToString("0.####", CultureInfo.InvariantCulture),
                pnl.ToString("0.####", CultureInfo.InvariantCulture),
                Csv(exitReason),
                Csv(diagnostic)
            ));
            telemetryWriter.Flush();
        }
        catch (Exception ex)
        {
            Print("[Telemetry WRITE ERROR] " + ex.Message);
        }
    }

    private string Csv(string value)
    {
        if (value == null)
            value = "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private void WriteDiagnosticFooter()
    {
        if (!PrintDiagnostics)
            return;

        Print("[CG_T2 v1.1 Diagnostics] " +
            "RTH=" + rthRejects +
            " Spread=" + spreadRejects +
            " Wall=" + wallRejects +
            " Delta=" + deltaRejects +
            " Momentum=" + momentumRejects +
            " Cooldown=" + cooldownRejects +
            " Pending=" + pendingRejects +
            " Governor=" + governorRejects +
            " LimitTimeouts=" + limitTimeouts +
            " FallbackMarkets=" + fallbackMarkets +
            " FallbackSkips=" + fallbackSkips +
            " LongSignals=" + signalLongCount +
            " ShortSignals=" + signalShortCount);
    }

    // ================================================================
    // Helper methods
    // ================================================================

    private int ToDay(DateTime dt)
    {
        return dt.Year * 10000 + dt.Month * 100 + dt.Day;
    }

    private int ToTime(DateTime dt)
    {
        return dt.Hour * 10000 + dt.Minute * 100 + dt.Second;
    }

    // ================================================================
    // Parameters
    // ================================================================

    [NinjaScriptProperty]
    [Range(1, 10)]
    [Display(Name = "Quantity", Order = 1, GroupName = "01. Execution")]
    public int Quantity { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "UseLimitFirst", Order = 2, GroupName = "01. Execution")]
    public bool UseLimitFirst { get; set; }

    [NinjaScriptProperty]
    [Range(0, 20)]
    [Display(Name = "LimitOffsetTicks", Order = 3, GroupName = "01. Execution")]
    public int LimitOffsetTicks { get; set; }

    [NinjaScriptProperty]
    [Range(1, 60)]
    [Display(Name = "LimitTimeoutSeconds", Order = 4, GroupName = "01. Execution")]
    public int LimitTimeoutSeconds { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "UseMarketFallback", Order = 5, GroupName = "01. Execution")]
    public bool UseMarketFallback { get; set; }

    [NinjaScriptProperty]
    [Range(1, 200)]
    [Display(Name = "StopTicks", Order = 6, GroupName = "01. Execution")]
    public int StopTicks { get; set; }

    [NinjaScriptProperty]
    [Range(1, 400)]
    [Display(Name = "TargetTicks", Order = 7, GroupName = "01. Execution")]
    public int TargetTicks { get; set; }

    [NinjaScriptProperty]
    [Range(10, 7200)]
    [Display(Name = "MaxHoldSeconds", Order = 8, GroupName = "01. Execution")]
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
    [Range(0, 600)]
    [Display(Name = "RthBufferSeconds", Order = 3, GroupName = "02. Session")]
    public int RthBufferSeconds { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "OneTradePerSession", Order = 4, GroupName = "02. Session")]
    public bool OneTradePerSession { get; set; }

    [NinjaScriptProperty]
    [Range(0, 600)]
    [Display(Name = "CooldownSeconds", Order = 1, GroupName = "03. Filters")]
    public int CooldownSeconds { get; set; }

    [NinjaScriptProperty]
    [Range(0, 600)]
    [Display(Name = "EmergencyCooldownSeconds", Order = 2, GroupName = "03. Filters")]
    public int EmergencyCooldownSeconds { get; set; }

    [NinjaScriptProperty]
    [Range(1, 100)]
    [Display(Name = "MaxSpreadTicks", Order = 3, GroupName = "03. Filters")]
    public int MaxSpreadTicks { get; set; }

    [NinjaScriptProperty]
    [Range(0, 1000)]
    [Display(Name = "MinWallScoreProxy", Order = 4, GroupName = "03. Filters")]
    public double MinWallScoreProxy { get; set; }

    [NinjaScriptProperty]
    [Range(0, 1000000)]
    [Display(Name = "MinDeltaAbs", Order = 5, GroupName = "03. Filters")]
    public double MinDeltaAbs { get; set; }

    [NinjaScriptProperty]
    [Range(-1000000, 1000000)]
    [Display(Name = "MinEventCountDeltaProxy", Order = 6, GroupName = "03. Filters")]
    public double MinEventCountDeltaProxy { get; set; }

    [NinjaScriptProperty]
    [Range(0, 100)]
    [Display(Name = "MomentumConfirmTicks", Order = 7, GroupName = "03. Filters")]
    public double MomentumConfirmTicks { get; set; }

    [NinjaScriptProperty]
    [Range(0, 10000000)]
    [Display(Name = "LowFlowMarketThreshold", Order = 8, GroupName = "03. Filters")]
    public double LowFlowMarketThreshold { get; set; }

    [NinjaScriptProperty]
    [Range(0, 10000000)]
    [Display(Name = "MediumFlowMarketThreshold", Order = 9, GroupName = "03. Filters")]
    public double MediumFlowMarketThreshold { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "EnableRegimeTagging", Order = 1, GroupName = "04. Regime")]
    public bool EnableRegimeTagging { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "EnableLossGovernor", Order = 1, GroupName = "05. Loss Governor")]
    public bool EnableLossGovernor { get; set; }

    [NinjaScriptProperty]
    [Range(1, 10)]
    [Display(Name = "LossGovernorLossesForCooldown", Order = 2, GroupName = "05. Loss Governor")]
    public int LossGovernorLossesForCooldown { get; set; }

    [NinjaScriptProperty]
    [Range(1, 10)]
    [Display(Name = "LossGovernorLossesForPause", Order = 3, GroupName = "05. Loss Governor")]
    public int LossGovernorLossesForPause { get; set; }

    [NinjaScriptProperty]
    [Range(1, 240)]
    [Display(Name = "LossGovernorPauseMinutes", Order = 4, GroupName = "05. Loss Governor")]
    public int LossGovernorPauseMinutes { get; set; }

    [NinjaScriptProperty]
    [Range(1.0, 10.0)]
    [Display(Name = "LossGovernorCooldownMultiplier", Order = 5, GroupName = "05. Loss Governor")]
    public double LossGovernorCooldownMultiplier { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "EnableOpeningSuppressor", Order = 1, GroupName = "06. Opening Suppressor")]
    public bool EnableOpeningSuppressor { get; set; }

    [NinjaScriptProperty]
    [Range(0, 100)]
    [Display(Name = "OpeningWallAdd", Order = 2, GroupName = "06. Opening Suppressor")]
    public double OpeningWallAdd { get; set; }

    [NinjaScriptProperty]
    [Range(0, 1000000)]
    [Display(Name = "OpeningDeltaAdd", Order = 3, GroupName = "06. Opening Suppressor")]
    public double OpeningDeltaAdd { get; set; }

    [NinjaScriptProperty]
    [Range(0, 600)]
    [Display(Name = "OpeningCooldownAddSeconds", Order = 4, GroupName = "06. Opening Suppressor")]
    public int OpeningCooldownAddSeconds { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "EnableDynamicThresholds", Order = 1, GroupName = "07. Dynamic Thresholds")]
    public bool EnableDynamicThresholds { get; set; }

    [NinjaScriptProperty]
    [Range(0, 100)]
    [Display(Name = "HighSpreadWallAdd", Order = 2, GroupName = "07. Dynamic Thresholds")]
    public double HighSpreadWallAdd { get; set; }

    [NinjaScriptProperty]
    [Range(0, 1000000)]
    [Display(Name = "HighSpreadDeltaAdd", Order = 3, GroupName = "07. Dynamic Thresholds")]
    public double HighSpreadDeltaAdd { get; set; }

    [NinjaScriptProperty]
    [Range(0, 100)]
    [Display(Name = "LowFlowWallAdd", Order = 4, GroupName = "07. Dynamic Thresholds")]
    public double LowFlowWallAdd { get; set; }

    [NinjaScriptProperty]
    [Range(0, 600)]
    [Display(Name = "LowFlowCooldownAddSeconds", Order = 5, GroupName = "07. Dynamic Thresholds")]
    public int LowFlowCooldownAddSeconds { get; set; }

    [NinjaScriptProperty]
    [Range(2, 1000)]
    [Display(Name = "FastLookbackTicks", Order = 1, GroupName = "08. Proxy Features")]
    public int FastLookbackTicks { get; set; }

    [NinjaScriptProperty]
    [Range(5, 5000)]
    [Display(Name = "SlowLookbackTicks", Order = 2, GroupName = "08. Proxy Features")]
    public int SlowLookbackTicks { get; set; }

    [NinjaScriptProperty]
    [Range(2, 1000)]
    [Display(Name = "FlowLookbackBars", Order = 3, GroupName = "08. Proxy Features")]
    public int FlowLookbackBars { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "EnableTelemetry", Order = 1, GroupName = "09. Telemetry")]
    public bool EnableTelemetry { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "TelemetryFilePrefix", Order = 2, GroupName = "09. Telemetry")]
    public string TelemetryFilePrefix { get; set; }

    [NinjaScriptProperty]
    [Display(Name = "PrintDiagnostics", Order = 3, GroupName = "09. Telemetry")]
    public bool PrintDiagnostics { get; set; }
}
}
