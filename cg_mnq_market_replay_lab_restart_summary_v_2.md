# CG_MNQ_MarketReplayLab Restart Summary v2

## Objective

Build a realistic MNQZ5 order-book replay and fill simulator using Databento MBO data.

Primary goals:
- reconstruct stable L3 order book
- preserve queue position and resting order lifecycle
- support realistic market and passive fill simulation
- eventually bridge into strategy replay and NT8-style execution logic

---

## Canonical Instrument

Always use front-month MNQZ5 only:
- symbol = MNQZ5
- instrument_id = 42001149

These are effectively synonymous in the current data.

Representative extraction command:

```bash
python scripts/CG_extract_one_session.py \
  --start "2025-10-22 00:00:00" \
  --end "2025-10-22 20:00:00" \
  --symbol MNQZ5 \
  --instrument-id 42001149 \
  --min-price 24000 \
  --max-price 26000 \
  --out replay_cache/2025-10-22_MNQZ5_i42001149_pxfiltered_semantic.parquet
```

Result:
- ~42.9M rows
- stable price range after filtering
- semantic event types created

---

## Important Databento Semantics

### Side Mapping

Raw Databento side values:
- B = bid side / buy side resting liquidity
- A = ask side / sell side resting liquidity

Normalized internal values:
- BID
- ASK

### Flags Bitmask

Important values:
- 128 = F_LAST
- 64 = F_TOB
- 32 = F_SNAPSHOT
- 16 = F_MBP
- 8 = F_BAD_TS_RECV
- 4 = F_MAYBE_BAD_BOOK
- 2 = F_PUBLISHER_SPECIFIC

Most important for replay:
- F_LAST marks final row in an event batch
- only validate stable book after F_LAST rows complete

### Action Semantics

Databento action codes:
- A = add
- M = modify
- C = cancel
- R = clear
- T = aggressor trade
- F = resting fill
- N = informational / none

Important distinction:
- T = aggressor side trade
- F = resting order that got hit/lifted

A single trade can produce:
- T event
- F event
- C event

---

## Current Semantic Event Mapping

Current normalized event types:
- add
- modify
- cancel
- fill_resting
- trade_aggressor
- clear
- none

The semantic parquet currently used is:

```text
replay_cache/2025-10-22_MNQZ5_i42001149_pxfiltered_semantic.parquet
```

---

## Stable Book Reconstruction Result

Current best validator:

```text
scripts/CG_validate_book_state_eventbatched_strict.py
```

Current strict book implementation:

```text
cg_book/order_book_eventbatched_strict.py
```

Most recent successful validation:

- rows_loaded: 500000
- crossed_after_batch: 0
- spread: 0.5
- active_orders: 2536
- best_bid: 25239.5
- best_ask: 25240.0

Important stats:
- unknown_modify_refs: 564
- unknown_cancel_refs: 24986
- unknown_fill_refs: 2373
- crossed_book_count: 124

Key takeaway:
- final stable snapshots are uncrossed
- book is now good enough for realistic fill simulation

Representative successful run:

```bash
python -u scripts/CG_validate_book_state_eventbatched_strict.py \
  --parquet replay_cache/2025-10-22_MNQZ5_i42001149_pxfiltered_semantic.parquet \
  --max-events 500000
```

---

## Important Architectural Lesson

Validating after every raw row produced many false crossed books.

Correct approach:
- group rows by ts_event_ns
- process full batch
- only evaluate best bid / ask after batch complete
- F_LAST is useful confirmation that batch is complete

This dramatically reduced crossed books.

---

## Key Files

### Replay / Simulation

```text
cg_sim/models.py
cg_sim/replay.py
scripts/CG_replay_one_session.py
```

### Book Logic

```text
cg_book/order_book.py
cg_book/order_book_eventbatched.py
cg_book/order_book_eventbatched_strict.py
scripts/CG_validate_book_state.py
scripts/CG_validate_book_state_relaxed.py
scripts/CG_validate_book_state_eventbatched.py
scripts/CG_validate_book_state_eventbatched_strict.py
```

### Extraction

```text
scripts/CG_extract_one_session.py
```

---

## Next Immediate Step

Build strict execution realism.

Create:

```text
cg_exec/fill_model_strict.py
scripts/CG_test_fill_model_strict.py
scripts/CG_replay_and_fill_one_session.py
```

### Fill Model Requirements

#### Market Orders

Support:
- market buy sweeps asks upward
- market sell sweeps bids downward
- partial fills
- average fill price
- number of levels consumed
- slippage tracking

Core functions:

```python
simulate_market_buy(qty, book)
simulate_market_sell(qty, book)
```

#### Passive Queue Model

Support:
- place passive limit order
- initialize queue_ahead from visible resting size
- advance queue from cancels and fills ahead
- mark fill when queue ahead reaches zero

Core functions:

```python
place_passive_limit(side, price, qty, book)
advance_passive_order_from_book_events(order, event_batch)
```

---

## Recommended Next Development Order

1. cg_exec/fill_model_strict.py
2. scripts/CG_test_fill_model_strict.py
3. scripts/CG_replay_and_fill_one_session.py
4. strategy replay with fills
5. PnL and execution metrics
6. eventual NT8 replay bridge

---

## Important Reminder

The strict event-batched book is now the canonical version.

Use:

```text
cg_book/order_book_eventbatched_strict.py
```

Avoid using older relaxed validators for future development except for debugging comparisons.

