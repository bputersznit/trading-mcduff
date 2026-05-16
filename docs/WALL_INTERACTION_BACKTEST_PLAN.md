# Wall Interaction Backtest Pipeline - Implementation Plan

**Goal:** Validate wall microstructure edge before NT8 implementation
**Data:** Sept-Oct 2025 MBO (789M events, already aggregated to 100ms)
**Baseline:** ClanMarshal v9.4 (36 trades, 442.75 pts)

---

## Phase 1: Wall Event Detection (Week 1)

### Input
- `CG_mnq_book_proxy_100ms` (12.6M rows)
- Columns: bucket_time, best_bid, best_ask, bid_size, ask_size, spread

### Output
- `CG_mnq_wall_events` (estimated ~50K-100K events)

### Detection Logic
```sql
-- Wall creation event
INSERT INTO CG_mnq_wall_events
SELECT
    row_number() OVER (ORDER BY bucket_time) AS event_id,
    bucket_time AS event_time,
    'WALL_CREATED' AS event_type,

    -- Bid wall detection (size > 50 contracts)
    CASE
        WHEN bid_size >= 50 THEN 'BID'
        WHEN ask_size >= 50 THEN 'ASK'
    END AS wall_side,

    CASE
        WHEN bid_size >= 50 THEN best_bid
        WHEN ask_size >= 50 THEN best_ask
    END AS wall_price,

    -- Size metrics
    greatest(bid_size, ask_size) AS wall_size,

    -- Context
    (best_bid + best_ask) / 2 AS mid_price,
    (best_ask - best_bid) AS spread_ticks

FROM CG_mnq_book_proxy_100ms
WHERE bid_size >= 50 OR ask_size >= 50;
```

**Threshold tuning:**
- Min wall size: 50 contracts (vs v9.4's 10 for "thin")
- Persistence: Wall must exist for 3+ consecutive 100ms buckets
- Distance: Track walls within 10 ticks of mid price

**Expected output:**
- ~1000 wall events per day
- ~22 trading days = ~22K wall events

---

## Phase 2: Interaction Detection (Week 2)

### Input
- `CG_mnq_wall_events` (walls)
- `CG_mnq_book_proxy_100ms` (price/volume)

### Output
- `CG_mnq_wall_interactions` (estimated ~5K-10K interactions)

### Detection Logic
```sql
-- Start interaction when price within 5 ticks of wall
WITH wall_proximity AS (
    SELECT
        we.event_id AS wall_event_id,
        we.wall_price,
        we.wall_side,
        bp.bucket_time,
        bp.mid_price,
        abs(bp.mid_price - we.wall_price) AS distance_ticks,
        bp.bid_volume_100ms,
        bp.ask_volume_100ms
    FROM CG_mnq_wall_events we
    JOIN CG_mnq_book_proxy_100ms bp
        ON bp.bucket_time BETWEEN we.event_time AND we.event_time + INTERVAL 60 SECOND
    WHERE abs(bp.mid_price - we.wall_price) <= 5
)
-- Group into interactions (continuous proximity = one interaction)
SELECT ...
```

**Interaction boundaries:**
- **Start:** Price comes within 5 ticks of wall
- **End:** Price moves > 10 ticks away OR 60 seconds elapsed OR wall disappears

**Aggression tracking:**
- Sum bid_volume, ask_volume during interaction window
- Calculate delta, delta_rate, imbalance_ratio
- Track max favorable/adverse excursion

**Expected output:**
- ~250 interactions per day
- ~22 days = ~5,500 interactions

---

## Phase 3: Outcome Classification (Week 2-3)

### Input
- `CG_mnq_wall_interactions` (raw interactions)

### Output
- Same table, `outcome` and `regime` columns populated

### Classification Logic

#### Outcome Classification
```sql
UPDATE CG_mnq_wall_interactions
SET outcome = CASE
    -- REJECT: Wall held, price reversed
    WHEN wall_size_consumed < wall_size_initial * 0.3
         AND price_change_ticks * (wall_side = 'BID' ? -1 : 1) > 0
    THEN 'REJECT'

    -- BREAK: Wall consumed, price broke through
    WHEN wall_size_consumed > wall_size_initial * 0.7
         AND abs(price_change_ticks) > 5
    THEN 'BREAK'

    -- ABSORB: High aggression but no move
    WHEN abs(delta) > 100
         AND abs(price_change_ticks) < 2
    THEN 'ABSORB'

    -- FADE: Low aggression, drifted away
    WHEN abs(delta) < 50
         AND abs(price_change_ticks) < 3
    THEN 'FADE'

    -- SPOOF: Wall pulled before test
    WHEN wall_size_final < wall_size_initial * 0.5
         AND wall_size_consumed < 10
    THEN 'SPOOF'

    ELSE 'INCONCLUSIVE'
END;
```

#### Regime Classification
```sql
UPDATE CG_mnq_wall_interactions
SET regime = CASE
    -- ABSORPTION: High aggression into wall + no move
    WHEN abs(delta) > 100 AND abs(price_change_ticks) < 2
    THEN 'ABSORPTION'

    -- EXHAUSTION: Low aggression + stall
    WHEN abs(delta) < 50 AND abs(price_change_ticks) < 3
    THEN 'EXHAUSTION'

    -- BREAKOUT: Clean break with aggression
    WHEN abs(delta) > 50 AND abs(price_change_ticks) > 5
    THEN 'BREAKOUT'

    -- SPOOF: Wall disappeared
    WHEN wall_size_final < wall_size_initial * 0.5
    THEN 'SPOOF'
END;
```

**Expected distribution:**
- REJECT: ~30% of interactions
- BREAK: ~25% of interactions
- ABSORB: ~20% of interactions
- FADE: ~15% of interactions
- SPOOF: ~10% of interactions

---

## Phase 4: Trade Candidate Generation (Week 3)

### Input
- `CG_mnq_wall_interactions` (classified)

### Output
- `CG_mnq_wall_trade_candidates` (estimated ~500-1000 setups)

### Signal Logic

#### ABSORPTION_FLIP (Best Reversal Trade)
```sql
INSERT INTO CG_mnq_wall_trade_candidates
SELECT
    row_number() OVER (ORDER BY signal_time) AS candidate_id,
    interaction_id,
    end_time + INTERVAL 100 MILLISECOND AS signal_time,
    'ABSORPTION_FLIP' AS setup_type,

    -- Entry opposite to wall side (absorption on bid = go short)
    CASE wall_side WHEN 'BID' THEN 'SHORT' ELSE 'LONG' END AS entry_side,

    price_end AS entry_price,

    -- Risk parameters (1:2 risk/reward)
    CASE wall_side
        WHEN 'BID' THEN price_end + 20  -- Stop above wall
        ELSE price_end - 20
    END AS stop_price,

    CASE wall_side
        WHEN 'BID' THEN price_end - 40  -- Target 2x stop
        ELSE price_end + 40
    END AS target_price,

    2.0 AS risk_reward_ratio,

    -- Quality metrics
    wall_size_initial / 100.0 AS wall_strength,
    abs(delta) / 200.0 AS aggression_strength,
    1.0 AS regime_clarity,  -- ABSORPTION is clear regime

    (wall_size_initial / 100.0 + abs(delta) / 200.0 + 1.0) / 3.0 AS overall_quality

FROM CG_mnq_wall_interactions
WHERE regime = 'ABSORPTION'
  AND abs(delta) > 100              -- Significant aggression
  AND wall_size_initial > 50        -- Meaningful wall
  AND abs(price_change_ticks) < 2;  -- True absorption (no move)
```

#### PULL_BREAK (Best Breakout Trade)
```sql
-- Similar logic for SPOOF regime
-- Entry in direction wall pulled (spoofed bid = go long)
WHERE regime = 'SPOOF'
  AND wall_size_final < wall_size_initial * 0.5
```

#### ICEBERG_HOLD (Scalp Fade)
```sql
-- Wall replenishes + multiple rejections
-- Entry against prevailing direction
WHERE outcome = 'REJECT'
  AND wall_size_consumed > 0  -- Some fills occurred
  AND wall_size_final >= wall_size_initial * 0.9  -- But replenished
```

**Filter by quality:**
```sql
WHERE overall_quality >= 0.85  -- Comparable to v9.4's force_rank >= 0.94
```

**Expected output:**
- ~50-100 ABSORPTION_FLIP setups
- ~30-50 PULL_BREAK setups
- ~100-150 ICEBERG_HOLD setups
- **Total: ~200-300 trade candidates** (vs v9.4's 36)

---

## Phase 5: Backtest Execution (Week 4)

### Input
- `CG_mnq_wall_trade_candidates` (signals)

### Output
- `CG_mnq_wall_backtest_results` (trade outcomes)

### Execution Simulation
```sql
CREATE TABLE CG_mnq_wall_backtest_results AS
SELECT
    candidate_id,
    signal_time,
    setup_type,
    entry_side,
    entry_price,
    stop_price,
    target_price,

    -- Simulate exit using tick-by-tick data
    -- (Similar to CG_mnq_hybrid_v6_2 exit simulation)

    exit_time,
    exit_price,
    exit_reason,  -- 'TARGET' / 'STOP' / 'TIMEOUT'

    pnl_pts,
    hold_seconds,

    -- Compare to v9.4 regime
    regime,
    directional_efficiency,
    vol_ratio_5d

FROM CG_mnq_wall_trade_candidates
-- Join with 100ms book proxy for exit simulation
```

**Exit rules:**
1. **Target hit:** Price reaches target_price
2. **Stop hit:** Price reaches stop_price
3. **Time stop:** 5 minutes elapsed (300 seconds)

**Friction modeling:**
- Slippage: 1.5 pts per trade
- Commission: $0.70 per trade
- Total cost: Same as v9.4 for fair comparison

---

## Phase 6: Performance Analysis (Week 4)

### Compare to v9.4 Baseline

```sql
WITH v94_baseline AS (
    SELECT
        36 AS trades,
        442.75 AS total_pnl_pts,
        12.30 AS expectancy_pts,
        0.6944 AS win_rate,
        -12.00 AS max_dd_pts,
        13.56 AS profit_factor
),
wall_results AS (
    SELECT
        count() AS trades,
        round(sum(pnl_pts), 2) AS total_pnl_pts,
        round(avg(pnl_pts), 2) AS expectancy_pts,
        round(countIf(pnl_pts > 0) / count(), 4) AS win_rate,
        round(min(cumsum_pnl_pts - cummax_pnl_pts), 2) AS max_dd_pts,
        round(
            sumIf(pnl_pts, pnl_pts > 0) / abs(sumIf(pnl_pts, pnl_pts < 0)),
            2
        ) AS profit_factor
    FROM CG_mnq_wall_backtest_results
)
SELECT * FROM v94_baseline
UNION ALL
SELECT * FROM wall_results;
```

**Key questions:**
1. **More trades?** (v9.4 = 36, target = 100-200)
2. **Better expectancy?** (v9.4 = 12.30 pts, maintain or improve)
3. **Lower drawdown?** (v9.4 = -12 pts, target < -20 pts)
4. **Higher PF?** (v9.4 = 13.56, maintain > 5.0)

**If wall framework beats v9.4:**
- Proceed to NT8 implementation (ClanMarshal v10.0)
- Integrate L2 aggression tracking
- Real-time regime classification

**If wall framework underperforms v9.4:**
- Refine thresholds (wall size, delta, quality score)
- OR stick with v9.4's simpler thin wall approach
- Focus on regime awareness improvements instead

---

## Success Metrics

### Tier 1: Validation Success
- ✅ Find > 100 trade candidates (vs v9.4's 36)
- ✅ Expectancy > 8 pts (vs v9.4's 12.30)
- ✅ Win rate > 55% (vs v9.4's 69.44%)
- ✅ Max DD < -30 pts (vs v9.4's -12)

### Tier 2: Production Ready
- ✅ All Tier 1 metrics
- ✅ Friction survivability > 80% at 1.5 pts
- ✅ Profit factor > 5.0
- ✅ No kill-switch breaches (daily loss < -30 pts)

### Tier 3: Superior to v9.4
- ✅ All Tier 2 metrics
- ✅ Total PnL > 442.75 pts
- ✅ Expectancy > 12.30 pts
- ✅ Max DD < -12 pts

---

## Timeline

**Week 1:** Wall event detection + population
**Week 2:** Interaction tracking + aggression metrics
**Week 3:** Outcome classification + trade candidate generation
**Week 4:** Backtest execution + performance analysis

**Total:** 4 weeks to validation

**If successful:** Proceed to NT8 implementation (2-3 weeks)
**If unsuccessful:** Iterate on thresholds or stick with v9.4

---

## Next Action

Run Phase 1 wall event detection on existing Sept-Oct 2025 data to see:
1. How many wall events are detected
2. Distribution of wall sizes
3. Behavior classification (static/iceberg/pulling)

This will validate the schema is capturing meaningful liquidity structure.

**Command to start:**
```bash
clickhouse-client < CG_WALL_MICROSTRUCTURE_SCHEMA.sql
# Then: Run wall event detection query
```

---

**Status:** Schema complete, backtest pipeline designed
**Next:** Populate CG_mnq_wall_events from book proxy data
**Goal:** Prove wall interaction edge exists before NT8 implementation
