# First Test Run - v4.1 Short Gate
## Step-by-Step Checklist

**Goal:** Compare v4.1 (Short Gate) to v4 (No Gate) on April 13-14, 2026

---

## ✅ PRE-TEST SETUP (5 minutes)

### Step 1: Locate the Strategy File
```bash
# Your file is at:
/home/bernard/trading4/CG_MNQ_MarketReplayLab/ninjascript/CGScalpingStrategyNT8Native_v4_1_ShortGate.cs

# Copy to NinjaTrader Strategies folder
# (Update this path to match your NT8 installation)
```

**Windows Path (typical):**
```
C:\Users\[YourName]\Documents\NinjaTrader 8\bin\Custom\Strategies\
```

**Linux/VPS Path (if using Wine/remote):**
```
/path/to/.wine/drive_c/users/[user]/Documents/NinjaTrader 8/bin/Custom/Strategies/
```

**Action:**
- [ ] Copy `CGScalpingStrategyNT8Native_v4_1_ShortGate.cs` to Strategies folder

---

### Step 2: Compile in NinjaTrader

1. **Open NinjaTrader 8**
   - [ ] NT8 is running

2. **Open NinjaScript Editor**
   - [ ] Press `F3` or Tools → NinjaScript Editor

3. **Compile**
   - [ ] Press `F5` or click "Compile"
   - [ ] Wait for compilation
   - [ ] Check bottom panel: Should say "Compiled successfully"

4. **Check for Errors**
   ```
   ✅ Good: "Compiled successfully"
   ❌ Bad: "Error CS..." messages

   If errors: Copy error message and I'll help fix it
   ```

   - [ ] Compilation successful, no errors

---

## 🎮 TEST EXECUTION (10 minutes)

### Step 3: Open Market Replay

1. **New Workspace**
   - [ ] File → Workspaces → New Workspace
   - [ ] Name it "ShortGate_Test_1"

2. **Connect to Playback**
   - [ ] Click connection button (top left)
   - [ ] Select "Playback101" (or your playback connection)
   - [ ] Status should show "Connected"

3. **Open Chart**
   - [ ] Right-click anywhere → New → Chart
   - [ ] Instrument: **MNQ 06-26** (June 2026)
   - [ ] Data Series: **1 Minute** (recommended for visibility)
   - [ ] Click OK

---

### Step 4: Load Market Replay Data

1. **Set Playback Date**
   - [ ] Tools → Playback Connection
   - [ ] Set date: **April 13, 2026**
   - [ ] Set time: **08:00:00** (before RTH open)
   - [ ] Speed: **2x** (or your preference)

2. **Load Data**
   - [ ] Click "Load Data" button
   - [ ] Wait for data to load
   - [ ] Chart should show April 13 data

---

### Step 5: Apply v4.1 Strategy

1. **Add Strategy to Chart**
   - [ ] Right-click chart → Strategies → Add Strategy
   - [ ] Select: **CGScalpingStrategyNT8Native_v4_1_ShortGate**
   - [ ] Click "Add"

2. **Configure Strategy Parameters**

   **CRITICAL SETTINGS:**
   ```
   === 0. Data Series ===
   Bar Interval (minutes): 1

   === 1. Position ===
   Contracts: 1

   === 2a. ABSORPTION ===
   Target (points): 8.0
   Stop (points): 5.0
   Max Hold (seconds): 120
   Min Aggressor Volume: 40
   Absorption Ratio Threshold: 1.5

   === 2b. BREAKOUT ===
   Target (points): 10.0
   Stop (points): 6.0
   Max Hold (seconds): 60
   Volume Spike Multiplier: 2.5

   === 2c. Filters ===
   Enable Trend Filter: TRUE
   Fast EMA Period: 9
   Slow EMA Period: 21
   Disable Short Trades: FALSE
   Only Trade With Trend: TRUE
   RTH Only: TRUE
   Use Trailing Stops: TRUE
   Trailing Stop Trigger: 4.0
   Trailing Stop Distance: 3.0

   ⭐ Enable Short Gate: TRUE
   ⭐ Short Gate: Max Failed Checks: 1
   ⭐ Short Gate: Min EMA Separation: 5.0

   === 3. Risk Management ===
   Max Trades/Hour: 10.0
   Weekly Loss Limit: 250.0
   Hard Loss Limit: 500.0
   ```

   - [ ] All parameters configured
   - [ ] **UseShortGate = TRUE** (verify!)
   - [ ] Click OK to apply

---

### Step 6: Open Output Window

**CRITICAL:** This is where you'll see the Short Gate evaluations

1. **Open Tools → Output Window**
   - [ ] Output window visible
   - [ ] Should see initialization messages:
   ```
   ========================================
   CG SCALPING NT8 NATIVE v4.1 - SHORT GATE
   ========================================
   NEW v4.1 FEATURES:
     ✓ SHORT GATE: Stricter requirements for shorts
       - Max failed checks: 1
       - Min EMA separation: 5.0
       - Gate enabled: YES
   ```

2. **Clear Output** (optional)
   - [ ] Right-click Output window → Clear
   - [ ] Fresh start for test

---

### Step 7: Run the Replay

1. **Start Playback**
   - [ ] Tools → Playback Connection → Click "Play" ▶
   - [ ] Speed: 2x-10x (your preference)
   - [ ] Watch the chart and Output window

2. **Monitor Short Gate Activity**

   **In Output window, watch for:**

   **Long Trades (Should look normal):**
   ```
   04/13/2026 08:29:15 LONG ABSORPTION @ 25209.25 | Str: 65 | Target: +8 Stop: -5 | Trend: UP
   ```

   **Short Signal ACCEPTED:**
   ```
   === SHORT GATE EVALUATION ===
     ✅ GATE 1 PASS: Strong downtrend (Sep: 6.25)
     ✅ GATE 2 PASS: Strong signal (120)
     ✅ GATE 3 PASS: Near swing high (resistance)
     ✅ GATE 4 PASS: Strong negative delta (-85)
     ✅ GATE 5 PASS: Good time for shorts
     ✅ GATE 6 PASS: Extra strong absorption
   === SHORT GATE RESULT: PASS ✅ ===
       Failed 0/6 gates (max allowed: 1)

   04/13/2026 10:04:14 SHORT ABSORPTION @ 25792.25 | Str: 120 | Target: +8 Stop: -5 | Trend: DOWN
   ```

   **Short Signal REJECTED:**
   ```
   === SHORT GATE EVALUATION ===
     ❌ GATE 1 FAIL: Not in strong downtrend (Sep: 2.50, need 5.00)
     ✅ GATE 2 PASS: Strong signal (95)
     ❌ GATE 3 FAIL: Not near swing high
     ✅ GATE 4 PASS: Strong negative delta (-65)
     ✅ GATE 5 PASS: Good time for shorts
     ✅ GATE 6 PASS: Extra strong absorption
   === SHORT GATE RESULT: FAIL ❌ ===
       Failed 2/6 gates (max allowed: 1)
   ```

3. **Keep Count** (manually or screenshot Output window)
   - [ ] Count LONG trades taken
   - [ ] Count SHORT trades taken
   - [ ] Count SHORT trades rejected by gate

---

### Step 8: Let It Run

**April 13:**
- [ ] Run from 08:00 to 15:00 (RTH trading hours)
- [ ] Playback should auto-stop at end of day (or pause it)

**April 14:**
- [ ] Set date to April 14, 2026
- [ ] Set time to 08:00:00
- [ ] Click "Play" again
- [ ] Run from 08:00 to 15:00

**Total Time:** ~20-40 minutes depending on playback speed

---

## 📊 RESULTS COLLECTION (5 minutes)

### Step 9: Export Strategy Performance

1. **Right-click chart**
   - [ ] Strategies → CGScalpingStrategyNT8Native_v4_1_ShortGate → Properties
   - [ ] Click "Strategy Performance" tab

2. **Check Summary Stats**
   ```
   Total Trades: ?
   Winners: ?
   Losers: ?
   Win Rate: ?
   Total P&L: ?
   Profit Factor: ?
   ```

3. **Export Trade List**
   - [ ] Right-click Performance → Export → CSV
   - [ ] Save as: `v4_1_shortgate_april13-14.csv`

4. **Screenshot Output Window**
   - [ ] Capture all gate evaluation logs
   - [ ] Save as: `shortgate_logs_april13-14.png`

---

### Step 10: Analyze Results

**Open the results template I'll create next...**

- [ ] Fill in results in comparison template
- [ ] Review which gates failed most often
- [ ] Calculate improvement vs. original

---

## 🎯 WHAT TO LOOK FOR

### Success Indicators ✅

1. **Fewer Short Trades**
   - Original: 33 shorts
   - Expected: ~15 shorts (45% reduction)
   - Actual: _____

2. **Higher Short Win Rate**
   - Original: 21.2%
   - Expected: 40-45%
   - Actual: _____

3. **Less Short Loss**
   - Original: -$297
   - Expected: -$50 to -$100
   - Actual: _____

4. **Overall Improvement**
   - Original P&L: $261
   - Expected: $400-500
   - Actual: _____

### Gate Analysis 🔍

**Which gates failed most often?**
- [ ] GATE 1 (Downtrend): ____ failures
- [ ] GATE 2 (Signal Strength): ____ failures
- [ ] GATE 3 (Swing High): ____ failures
- [ ] GATE 4 (Volume Delta): ____ failures
- [ ] GATE 5 (Time of Day): ____ failures
- [ ] GATE 6 (Extra Strong): ____ failures

**Most common reason for rejection:** __________

---

## 🚨 TROUBLESHOOTING

### Problem: Strategy won't compile

**Error:** "The type or namespace name 'OrderFlowBar' could not be found"
- **Fix:** Make sure you copied the entire file, not just parts

**Error:** "CS1519: Invalid token"
- **Fix:** Check for missing braces or semicolons

**Error:** Other CS errors
- **Action:** Copy error message and share with me

---

### Problem: Strategy not appearing in list

- [ ] Did compilation succeed?
- [ ] Refresh NinjaScript list (right-click → Refresh)
- [ ] Restart NinjaTrader

---

### Problem: No trades being taken

**Check:**
- [ ] Is chart connected to Playback?
- [ ] Is playback actually running (time moving)?
- [ ] Are you in RTH hours (8:30 AM - 3:00 PM)?
- [ ] Is strategy enabled on chart?

**In Output window, look for:**
```
Outside RTH - No trading
Max trades gate: X trades/hour >= 10.0
```

---

### Problem: No gate evaluations showing

**Possible causes:**
- [ ] No short signals detected (normal in strong uptrend)
- [ ] All shorts passing with 0 fails (unlikely but possible)
- [ ] Output window not showing strategy output

**Fix:**
- [ ] Check Output window filter (show all)
- [ ] Verify UseShortGate = TRUE in settings
- [ ] Let playback run longer (maybe no shorts yet)

---

### Problem: Too many gate logs (Output too cluttered)

**If it's too verbose:**
1. Note the gate failure patterns
2. After test, can modify code to reduce logging
3. For now, just let it log everything (helpful for analysis)

---

## 📝 POST-TEST NOTES

### What Worked Well
```
[Your observations]




```

### What Needs Adjustment
```
[Your observations]




```

### Questions for Tuning
```
[Your observations]




```

---

## ✅ COMPLETION CHECKLIST

- [ ] v4.1 compiled successfully
- [ ] Test run completed (April 13-14)
- [ ] Results exported to CSV
- [ ] Gate logs captured
- [ ] Results entered in comparison template
- [ ] Initial analysis done
- [ ] Ready for Phase 2 (forward test)

---

**Time Required:** ~30-40 minutes total
**Next:** Fill out the results template I'll create next...

---

## 🆘 Need Help?

**If stuck:** Share with me:
1. Screenshot of error message (if won't compile)
2. Screenshot of Output window (if not working as expected)
3. Exported CSV results (for analysis help)

**Continue to:** `TEST_RESULTS_TEMPLATE.md` (creating next...)
