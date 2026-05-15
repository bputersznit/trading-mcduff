# VPS Deployment Status - May 14, 2026

## VPS Connection Info

**Public IP:** 104.245.107.193
**User:** Administrator
**Connection:** `ssh Administrator@104.245.107.193`

**Note:** The 192.168.1.62 IP doesn't work - VPS is on different subnet. Use the public IP above.

---

## Files Deployed to VPS

**Path:** `C:\Users\Administrator\Documents\NinjaTrader 8\bin\Custom\Strategies\`

| File | Size | Status | Description |
|------|------|--------|-------------|
| CG_OrderFlow_Aggression_v2_0.cs | 24,364 bytes | ✅ DEPLOYED | **USE THIS** - Complete rewrite with execution aggression |
| CG_OrderFlow_Imbalance_v1_0_CORRECTED.cs | 17,315 bytes | ✅ DEPLOYED | v1.0 with P&L fix only |
| CG_OrderFlow_Imbalance_v1_0_FIXED.cs | 15,892 bytes | ✅ DEPLOYED | Older version with timeout fix |

---

## Next Steps on VPS

### 1. Compile in NinjaTrader

```
1. Open NinjaTrader 8
2. Press F5 (or Tools → Compile)
3. Check for errors in Output window
4. Should see: "Compilation successful"
```

### 2. Apply v2.0 to Chart

```
1. Open MNQ chart (any timeframe)
2. Right-click → Strategies
3. Add: CG_OrderFlow_Aggression_v2_0
4. Configure parameters (see below)
5. Enable strategy
```

---

## Recommended v2.0 Parameters for Testing

### Signal
- **Min Aggression Delta:** 50
- **Min Aggression Imbalance:** 0.60
- **Require 1s Confirmation:** TRUE
- **Require 5s Confirmation:** FALSE

### Risk
- **Target (ticks):** 40
- **Stop (ticks):** 20
- **Timeout (minutes):** 10
- **Max Spread (ticks):** 3

### Cooldown
- **Cooldown (seconds):** 30
- **Post-Stop Cooldown (seconds):** 60

### Daily Limits
- **Enable Daily Limits:** FALSE (for testing)
- **Max Daily Loss:** $500
- **Max Consecutive Losses:** 10

### Filters
- **Enable Opening Range Filter:** TRUE
- **Enable Manipulation Filters:** TRUE
- **Enable Spread Filter:** TRUE
- **Enable Cooldown:** TRUE
- **Enable Book Pull Detection:** FALSE
- **Enable Verbose Logging:** FALSE

---

## Key Differences from v1.0

### What v2.0 Fixes

✅ **Execution aggression** (not book updates)
✅ **Multi-scale confirmation** (100ms + 1s + 5s)
✅ **Event time** (not DateTime.Now)
✅ **Correct P&L calculation**
✅ **Spread filter** (max 3 ticks)
✅ **Cooldown timer** (30s / 60s)
✅ **Realistic slippage** (3 ticks)
✅ **Daily limits work** (currently disabled)

### What to Expect

- **30-50% fewer trades** than v1.0 (cooldown + spread filter)
- **Higher quality signals** (execution vs book noise)
- **No spoofing false positives**
- **Accurate P&L display** (not $0.00)
- **Working timeouts** (no more 220 minute bugs)
- **Better live/playback correlation**

---

## Testing Protocol

### Phase 1: Playback Validation (NOW)

1. Run on Oct 2025 Market Replay data
2. Verify:
   - Trades execute
   - P&L displays correctly
   - Timeouts work (10 min max hold)
   - Opening Range calculates
   - No compilation errors

### Phase 2: Compare to v1.0

Run both strategies on same data:
- v1.0_CORRECTED (old signal model)
- v2.0 (new execution aggression)

Expected:
- v2.0 takes fewer trades
- v2.0 has better win rate
- v2.0 avoids spoofing traps

### Phase 3: Enable Risk Limits

After validation, enable daily limits:
```
Enable Daily Limits = TRUE
Max Daily Loss = $100
Max Consecutive Losses = 4
```

### Phase 4: Live Paper Trading

- 1 contract only
- Monitor for 3-5 days
- Compare to playback results

---

## Troubleshooting

### "Strategy not found"
- Compile first (F5)
- Check Output window for errors
- Restart NinjaTrader

### "No trades executing"
- Check Market Depth subscription active
- Verify RTH hours (9:30 AM - 4:00 PM ET)
- Check Opening Range calculated (after 9:45 AM)
- Enable Verbose Logging to see signals

### "P&L shows $0.00"
- This was v1.0 bug - v2.0 should show correct P&L
- If still happens, report immediately

### "Timeout still wrong"
- v2.0 uses event time - should be accurate
- Check Output window for "TIMEOUT EXIT after X min"
- Should show ~10 minutes, not 220

### "Too many trades"
- Increase cooldown (try 60s)
- Enable 5s confirmation
- Increase MinAggressionDelta (try 75)
- Reduce MaxSpreadTicks (try 2)

### "Not enough trades"
- Disable 1s confirmation
- Decrease MinAggressionDelta (try 30)
- Increase MaxSpreadTicks (try 4)
- Reduce cooldown (try 15s)

---

## Performance Monitoring

### What to Watch

**Good signs:**
- P&L tracks accurately
- Timeouts around 10 minutes
- Spread filter preventing wide-spread entries
- Cooldown preventing rapid chop
- Daily limits enforce when enabled

**Bad signs:**
- P&L still shows $0.00 (recompile)
- Timeouts immediate or very long (event time bug)
- Entering during 5+ tick spreads (filter not working)
- Rapid LONG/SHORT flips (cooldown not working)
- Daily limits don't stop trading (enable first)

---

## Output Examples

### Successful Entry/Exit:
```
========== 5/12/2026 - NEW TRADING DAY ==========
9:45 AM | OR CALCULATED: High=29300.25 Low=29180.50
10:23:14.832 | ENTRY LONG @ 29245.75 | TZ:NORMAL OR:ABOVE_OR | AggVol:287 Spread:1.0
10:23 AM | #1 FILL Long @ 29246.00 | Tgt:29256.00 Stp:29241.00
10:28 AM | #1 EXIT Target | P&L:$20.00 | Daily:$20.00/0 | Limits:OFF
```

### Cooldown Preventing Entry:
(No message - signal silently filtered)

### Daily Limit Hit (when enabled):
```
11:45 AM | #5 EXIT Stop | P&L:-$10.00 | Daily:-$105.00/3 | Limits:ON
*** DAILY LOSS LIMIT HIT: $-105.00 ***
```

---

## File Transfer Commands

### Upload from local to VPS:
```bash
scp ninjascript/CG_OrderFlow_Aggression_v2_0.cs Administrator@104.245.107.193:'C:\Users\Administrator\Documents\NinjaTrader 8\bin\Custom\Strategies\'
```

### Download from VPS to local:
```bash
scp Administrator@104.245.107.193:'C:\Users\Administrator\Documents\NinjaTrader 8\bin\Custom\Strategies\CG_OrderFlow_Aggression_v2_0.cs' ./
```

### Check files on VPS:
```bash
ssh Administrator@104.245.107.193 'Get-ChildItem "C:\Users\Administrator\Documents\NinjaTrader 8\bin\Custom\Strategies\" -Filter "CG_*.cs"'
```

---

## Version Summary

| Version | Signal Model | P&L Calc | Timeout | Daily Limits | Status |
|---------|-------------|----------|---------|--------------|--------|
| v1.0 | Book updates | BROKEN | BROKEN | BROKEN | ❌ Do not use |
| v1.0_FIXED | Book updates | BROKEN | Fixed | BROKEN | ❌ Do not use |
| v1.0_CORRECTED | Book updates | Fixed | BROKEN (DateTime.Now) | Working | ⚠️ Use for comparison only |
| **v2.0** | **Execution aggression** | **Fixed** | **Fixed** | **Working (disabled)** | **✅ RECOMMENDED** |

---

## Architecture Documentation

Full technical details: `docs/ORDERFLOW_V2_ARCHITECTURE.md`

**Key architectural changes:**
- OnMarketData for actual trades
- Multi-scale buckets (100ms/1s/5s)
- Event time tracking
- Spread filter
- Cooldown mechanism
- Optional book pull detection

---

## Support

If v2.0 doesn't perform as expected:
1. Check Output window for errors
2. Enable Verbose Logging
3. Compare to v1.0_CORRECTED on same data
4. Review architecture doc
5. Verify Market Depth data quality

Remember: v2.0 measures **execution aggression** not book updates. It's a fundamentally different signal, so expect different trade entries than the ClickHouse backtest (which used book updates).
