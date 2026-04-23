# Complete Order Types Guide

## 📋 All Implemented Order Types (o/t)

Your execution simulator now supports **ALL professional order types**:

### 1️⃣ **Market Orders**
**Purpose:** Immediate execution at best available price
**Slippage:** Yes (tracked)
**Use Case:** Need to enter/exit NOW

```python
fill = fill_model.simulate_market_buy(qty=5, book=book)
fill = fill_model.simulate_market_sell(qty=5, book=book)
```

**Features:**
- Sweeps through multiple price levels
- Tracks slippage in ticks
- Calculates average fill price
- Returns levels consumed

---

### 2️⃣ **Limit Orders** (Passive)
**Purpose:** Join queue at specific price
**Slippage:** None (price guaranteed)
**Use Case:** Better pricing, willing to wait

```python
order = fill_model.place_passive_limit(
    side="BUY",
    price=20000.00,
    qty=5,
    book=book,
    ts_ns=current_time,
)
```

**Features:**
- Realistic queue position tracking
- Advances through cancel/fill events
- Fills when queue ahead = 0
- Configurable queue assumption (front/middle/back)

---

### 3️⃣ **Stop-Market Orders**
**Purpose:** Trigger at stop price, execute as market
**Slippage:** Yes (after trigger)
**Use Case:** Stop loss, breakout entries

```python
# Sell stop (stop loss for long)
stop_mgr.place_stop_market(
    order_id="stop_1",
    side="SELL",
    qty=5,
    stop_price=19950.00,  # Below current price
    ts_ns=current_time,
)

# Buy stop (breakout entry)
stop_mgr.place_stop_market(
    order_id="breakout_1",
    side="BUY",
    qty=5,
    stop_price=20100.00,  # Above current price
    ts_ns=current_time,
)
```

**Features:**
- Buy stop: triggers when price rises to/above stop
- Sell stop: triggers when price falls to/below stop
- Executes as market order upon trigger
- Tracks trigger time and fill details

---

### 4️⃣ **Stop-Limit Orders**
**Purpose:** Trigger at stop, place limit for price protection
**Slippage:** Limited (by limit price)
**Use Case:** Stop loss with max slippage control

```python
stop_mgr.place_stop_limit(
    order_id="stop_limit_1",
    side="SELL",
    qty=5,
    stop_price=19950.00,    # Trigger price
    limit_price=19925.00,   # Worst acceptable price
    ts_ns=current_time,
)
```

**Features:**
- Triggers at stop price
- Places limit order (not market)
- May not fill if price moves too fast
- Better price protection than stop-market

---

### 5️⃣ **Trailing Stops**
**Purpose:** Dynamic stop that follows favorable price movement
**Slippage:** Yes (market execution)
**Use Case:** Lock in profits while letting winners run

```python
# Trailing sell stop (protect long position)
stop_mgr.place_trailing_stop_market(
    order_id="trail_1",
    side="SELL",
    qty=5,
    initial_stop_price=19950.00,
    trail_offset=2.00,  # $2.00 trail distance
    ts_ns=current_time,
)
```

**Features:**
- Stop price trails favorable price movement
- For long: stop rises as price rises
- For short: stop falls as price falls
- Locks in gains automatically

---

### 6️⃣ **OCO (One-Cancels-Other) Orders**
**Purpose:** Two orders where filling one cancels the other
**Slippage:** Depends on order type
**Use Case:** Exit management (stop + target)

```python
# Create stop and target
stop_id = "stop_1"
target_id = "target_1"

# Place both orders
stop_mgr.place_stop_market(stop_id, "SELL", 5, 19900.00, ts)
stop_mgr.place_stop_limit(target_id, "SELL", 5, 20100.00, 20100.00, ts)

# Link as OCO
oco_mgr.create_oco_pair(
    oco_id="oco_1",
    order_a_id=stop_id,
    order_b_id=target_id,
    order_a_type="stop_market",
    order_b_type="stop_limit",
)
```

**Features:**
- Automatic cancellation when one fills
- Common for exit management
- Can combine any order types
- Tracks which leg filled

---

### 7️⃣ **Bracket Orders**
**Purpose:** Entry + Stop + Target as a complete package
**Slippage:** Varies by component
**Use Case:** Complete trade management

```python
bracket = oco_mgr.create_bracket_order(
    bracket_id="bracket_1",
    entry_side="BUY",
    entry_qty=5,
    entry_price=20000.00,
    stop_price=19950.00,      # Stop loss
    target_price=20100.00,    # Profit target
    entry_type="limit",
    book=book,
    ts_ns=current_time,
)
```

**Features:**
- Entry order placed immediately
- Stop and target placed when entry fills
- Stop and target are OCO (one fills, cancels other)
- Complete trade lifecycle management
- Tracks whether stopped out or target hit

---

## 🎯 Order Type Quick Reference

| Order Type | Execution | Slippage | Queue | Best For |
|------------|-----------|----------|-------|----------|
| **Market** | Immediate | Yes | No | Urgency |
| **Limit** | Passive | No | Yes | Better price |
| **Stop-Market** | Triggered | Yes | No | Stop loss |
| **Stop-Limit** | Triggered | Limited | Yes | Protected stop |
| **Trailing** | Triggered | Yes | No | Profit locking |
| **OCO** | Linked | Varies | Varies | Exit pairs |
| **Bracket** | Complete | Varies | Varies | Full trade |

---

## 📊 Order Type Combinations

### Strategy 1: Scalping
```python
# Quick in/out with market orders
entry_fill = fill_model.simulate_market_buy(5, book)
exit_fill = fill_model.simulate_market_sell(5, book)
```

### Strategy 2: Patient Entry, Protected Exit
```python
# Limit entry
entry = fill_model.place_passive_limit("BUY", 20000.00, 5, book, ts)

# Once filled, place OCO stop + target
stop = stop_mgr.place_stop_market("stop", "SELL", 5, 19950.00, ts)
target = stop_mgr.place_stop_limit("target", "SELL", 5, 20100.00, 20100.00, ts)
oco = oco_mgr.create_oco_pair("oco_1", "stop", "target", "stop_market", "stop_limit")
```

### Strategy 3: Full Automation
```python
# Bracket order does it all
bracket = oco_mgr.create_bracket_order(
    bracket_id="trade_1",
    entry_side="BUY",
    entry_qty=5,
    entry_price=20000.00,
    stop_price=19950.00,
    target_price=20100.00,
    entry_type="limit",
    book=book,
    ts_ns=ts,
)
```

### Strategy 4: Breakout with Trailing
```python
# Buy stop for breakout
entry = stop_mgr.place_stop_market("breakout", "BUY", 5, 20100.00, ts)

# Trailing stop to lock in profits
trail = stop_mgr.place_trailing_stop_market(
    "trail", "SELL", 5, 20080.00, trail_offset=2.00, ts_ns=ts
)
```

---

## 🔧 Order Management Flow

### Typical Workflow

1. **Place Orders**
   ```python
   order = stop_mgr.place_stop_market(...)
   ```

2. **Check Triggers** (each market update)
   ```python
   triggered_m, triggered_l = stop_mgr.check_triggers(book, ts)
   ```

3. **Execute Triggered**
   ```python
   stop_mgr.execute_triggered_orders(triggered_m, triggered_l, book, ts)
   ```

4. **Advance Passive Orders** (each event)
   ```python
   stop_mgr.advance_passive_stops(event, event_type)
   ```

5. **Check OCO/Brackets**
   ```python
   oco_mgr.check_oco_fills(ts)
   oco_mgr.check_bracket_status(ts)
   ```

---

## 📈 Performance Metrics

All order executions are tracked:
- **Slippage** (ticks)
- **Fill price** (average, best, worst)
- **Execution time**
- **Fill type distribution**
- **Queue statistics** (for passive orders)

Access via:
```python
metrics = PerformanceMetrics()
metrics.record_trade(trade)
summary = metrics.summary_dict()
```

**Tracked Metrics:**
- Sharpe ratio
- Sortino ratio
- Max drawdown
- Win rate
- Profit factor
- Average slippage
- Fill quality
- MAE/MFE analysis

---

## 🚀 Complete System Architecture

```
┌─────────────────────────────────────────────────────┐
│            Order Management System                   │
├─────────────────────────────────────────────────────┤
│                                                      │
│  ┌──────────────┐      ┌──────────────┐           │
│  │ Fill Model   │◄─────┤ Market/Limit │           │
│  │ (Strict)     │      │   Orders     │           │
│  └──────────────┘      └──────────────┘           │
│         ▲                                           │
│         │                                           │
│  ┌──────────────┐      ┌──────────────┐           │
│  │ Stop Manager │◄─────┤ Stop Orders  │           │
│  │              │      │ + Trailing   │           │
│  └──────────────┘      └──────────────┘           │
│         ▲                                           │
│         │                                           │
│  ┌──────────────┐      ┌──────────────┐           │
│  │ OCO/Bracket  │◄─────┤ OCO + Bracket│           │
│  │  Manager     │      │   Orders     │           │
│  └──────────────┘      └──────────────┘           │
│         ▲                                           │
│         │                                           │
│  ┌──────────────┐                                  │
│  │ Performance  │                                  │
│  │   Metrics    │                                  │
│  └──────────────┘                                  │
│                                                      │
└─────────────────────────────────────────────────────┘
```

---

## ✅ Summary

You now have a **professional-grade execution simulator** with:

✅ 7 order types (all common types)
✅ Realistic slippage modeling
✅ Queue position tracking
✅ OCO and bracket orders
✅ Trailing stops
✅ Complete performance metrics
✅ Event-batched book integration

**This is enterprise-level execution simulation capability!**
