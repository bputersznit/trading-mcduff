// ============================================================================
// PATCH: L2 Replay Feed Validation Diagnostics
// Target: CG_MNQ_Flagship_Hybrid_v1_1.cs
// Purpose:
//   Validate whether NinjaTrader Market Replay is delivering usable L2/depth
//   events into OnMarketDepth(), and diagnose whether the T3 wall layer is blind.
// ============================================================================


// -----------------------------------------------------------------------------
// 1) ADD THESE FIELDS near the existing "Diagnostics" or "T3 Wall State" section
// -----------------------------------------------------------------------------

private long l2Events = 0;
private long l2BidEvents = 0;
private long l2AskEvents = 0;
private long l2TopBidEvents = 0;
private long l2TopAskEvents = 0;
private long l2InsertEvents = 0;
private long l2UpdateEvents = 0;
private long l2RemoveEvents = 0;

private DateTime lastL2HealthPrintTime = Core.Globals.MinDate;

// Set true while validating replay depth. Turn false after validation to reduce Output noise.
private bool EnableL2VerbosePrint = true;


// -----------------------------------------------------------------------------
// 2) REPLACE your existing OnMarketDepth() with this version
// -----------------------------------------------------------------------------

protected override void OnMarketDepth(MarketDepthEventArgs marketDepthUpdate)
{
    if (marketDepthUpdate == null)
        return;

    l2Events++;

    if (marketDepthUpdate.MarketDataType == MarketDataType.Bid)
    {
        l2BidEvents++;

        if (marketDepthUpdate.Position == 0)
            l2TopBidEvents++;
    }
    else if (marketDepthUpdate.MarketDataType == MarketDataType.Ask)
    {
        l2AskEvents++;

        if (marketDepthUpdate.Position == 0)
            l2TopAskEvents++;
    }

    if (marketDepthUpdate.Operation == Operation.Add)
        l2InsertEvents++;
    else if (marketDepthUpdate.Operation == Operation.Update)
        l2UpdateEvents++;
    else if (marketDepthUpdate.Operation == Operation.Remove)
        l2RemoveEvents++;

    // Verbose raw L2 print: use only for short replay windows because this can be noisy.
    if (EnableL2VerbosePrint && PrintDiagnostics)
    {
        Print(string.Format(
            "[L2] {0:yyyy-MM-dd HH:mm:ss.fff} Type={1} Pos={2} Op={3} Price={4:F2} Vol={5}",
            Time[0],
            marketDepthUpdate.MarketDataType,
            marketDepthUpdate.Position,
            marketDepthUpdate.Operation,
            marketDepthUpdate.Price,
            marketDepthUpdate.Volume
        ));
    }

    // Existing top-of-book tracking retained.
    if (marketDepthUpdate.MarketDataType == MarketDataType.Ask &&
        marketDepthUpdate.Position == 0)
    {
        lastBestAsk = marketDepthUpdate.Price;
        askWallSize = marketDepthUpdate.Volume;
    }
    else if (marketDepthUpdate.MarketDataType == MarketDataType.Bid &&
             marketDepthUpdate.Position == 0)
    {
        lastBestBid = marketDepthUpdate.Price;
        bidWallSize = marketDepthUpdate.Volume;
    }
}


// -----------------------------------------------------------------------------
// 3) ADD THIS METHOD anywhere in the class body, e.g. after ComputeT3WallFeatures()
// -----------------------------------------------------------------------------

private void PrintL2Health(DateTime etNow)
{
    if (!PrintDiagnostics)
        return;

    // Print once per minute.
    if (lastL2HealthPrintTime != Core.Globals.MinDate &&
        (etNow - lastL2HealthPrintTime).TotalSeconds < 60)
        return;

    lastL2HealthPrintTime = etNow;

    Print(string.Format(
        "[L2_HEALTH] {0:yyyy-MM-dd HH:mm:ss} all={1} bid={2} ask={3} topBid={4} topAsk={5} add={6} upd={7} rem={8} bidWall={9} askWall={10} bestBid={11:F2} bestAsk={12:F2}",
        etNow,
        l2Events,
        l2BidEvents,
        l2AskEvents,
        l2TopBidEvents,
        l2TopAskEvents,
        l2InsertEvents,
        l2UpdateEvents,
        l2RemoveEvents,
        bidWallSize,
        askWallSize,
        lastBestBid,
        lastBestAsk
    ));

    WriteTelemetry(
        "L2_HEALTH",
        string.Format(
            "all:{0}|bid:{1}|ask:{2}|topBid:{3}|topAsk:{4}|add:{5}|upd:{6}|rem:{7}|bidWall:{8}|askWall:{9}|bestBid:{10:F2}|bestAsk:{11:F2}",
            l2Events,
            l2BidEvents,
            l2AskEvents,
            l2TopBidEvents,
            l2TopAskEvents,
            l2InsertEvents,
            l2UpdateEvents,
            l2RemoveEvents,
            bidWallSize,
            askWallSize,
            lastBestBid,
            lastBestAsk
        )
    );
}


// -----------------------------------------------------------------------------
// 4) ADD THIS CALL inside OnBarUpdate(), after this line:
//      currentRegime = GetSessionRegime(etNow);
// -----------------------------------------------------------------------------

PrintL2Health(etNow);


// -----------------------------------------------------------------------------
// 5) ADD THESE RESET LINES inside ResetSessionState()
// -----------------------------------------------------------------------------

l2Events = 0;
l2BidEvents = 0;
l2AskEvents = 0;
l2TopBidEvents = 0;
l2TopAskEvents = 0;
l2InsertEvents = 0;
l2UpdateEvents = 0;
l2RemoveEvents = 0;
lastL2HealthPrintTime = Core.Globals.MinDate;


// -----------------------------------------------------------------------------
// 6) OPTIONAL: ADD THIS PARAMETER near your other NinjaScriptProperty parameters
// -----------------------------------------------------------------------------

[NinjaScriptProperty]
[Display(Name = "Enable L2 Verbose Print", Order = 4, GroupName = "09. Telemetry")]
public bool EnableL2VerbosePrintParameter
{ get; set; }


// -----------------------------------------------------------------------------
// 7) OPTIONAL: DEFAULT IT in State.SetDefaults
// -----------------------------------------------------------------------------

EnableL2VerbosePrintParameter = true;


// -----------------------------------------------------------------------------
// 8) OPTIONAL: replace the verbose print condition in OnMarketDepth()
// -----------------------------------------------------------------------------

// Replace:
// if (EnableL2VerbosePrint && PrintDiagnostics)
//
// With:
// if (EnableL2VerbosePrintParameter && PrintDiagnostics)


// ============================================================================
// INTERPRETATION GUIDE
// ============================================================================
//
// Good replay L2:
//   [L2_HEALTH] ... all=thousands bid>0 ask>0 topBid>0 topAsk>0 bidWall>0 askWall>0
//
// Missing replay L2:
//   [L2_HEALTH] ... all=0 bid=0 ask=0 topBid=0 topAsk=0 bidWall=0 askWall=0
//
// Handler receiving depth but top-of-book logic bad:
//   all>0 bid>0 ask>0 but topBid/topAsk remain 0 or bidWall/askWall remain 0
//
// Depth exists but threshold too high:
//   bidWall/askWall usually 1-10 while MinWallSize=100.
//   In this case, the T3 layer is being starved by an unrealistic MinWallSize.
//
// Recommended validation window:
//   Replay 09:30-09:35 ET or the 11:40 impulse window for only 2-3 minutes first.
// ============================================================================
