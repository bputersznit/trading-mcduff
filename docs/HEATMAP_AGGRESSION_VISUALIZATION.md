# MNQ Heatmap + Aggression Visualization

## Overview

The `CG_MNQ_rolling_heatmap_aggression_chart.py` script creates rolling RTH visualization frames combining:
- **Resting liquidity heatmap**: Logarithmic grayscale (black = low liquidity, white = daily max)
- **Aggression overlay**: Open circular markers with green/red arcs showing buy/sell volume

## Installation

Script location: `scripts/CG_MNQ_rolling_heatmap_aggression_chart.py`

Required Python packages:
```bash
pip install clickhouse-connect matplotlib numpy pandas
```

## Usage

### Basic Command (Validated 22-Day Sample)

```bash
python scripts/CG_MNQ_rolling_heatmap_aggression_chart.py \
  --start-date 2025-09-23 \
  --end-date 2025-10-22 \
  --out-dir frames_heatmap_aggression \
  --heatmap-table CG_mnq_heatmap_1s \
  --resting-expr "greatest(abs(bid_net_liquidity), abs(ask_net_liquidity))"
```

**Important**: Your `CG_mnq_heatmap_1s` table uses `bid_net_liquidity` and `ask_net_liquidity` columns (not `bid_resting_size`/`ask_resting_size`). The script defaults to resting size, so you MUST specify the correct expression via `--resting-expr`.

### Alternative Resting Expressions

Depending on your visualization goals:

1. **Max net liquidity** (most responsive to walls):
   ```
   --resting-expr "greatest(abs(bid_net_liquidity), abs(ask_net_liquidity))"
   ```

2. **Total net liquidity** (shows combined depth):
   ```
   --resting-expr "abs(bid_net_liquidity) + abs(ask_net_liquidity)"
   ```

3. **Bid-only liquidity** (show bid walls only):
   ```
   --resting-expr "abs(bid_net_liquidity)"
   ```

4. **Ask-only liquidity** (show ask walls only):
   ```
   --resting-expr "abs(ask_net_liquidity)"
   ```

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `--start-date` | None | YYYY-MM-DD format (e.g., 2025-09-23) |
| `--end-date` | None | YYYY-MM-DD format (e.g., 2025-10-22) |
| `--out-dir` | frames_heatmap_aggression | Output directory for PNG frames |
| `--heatmap-table` | CG_mnq_heatmap_1s | ClickHouse heatmap table name |
| `--resting-expr` | greatest(bid_resting_size, ask_resting_size) | **CHANGE THIS** to match your schema |
| `--frame-minutes` | 30 | Rolling window width in minutes |
| `--step-minutes` | 5 | Step size between frames in minutes |

### Expected Output

For the 22-day validated sample (Sep 23 - Oct 22):
- RTH hours: 9:30 AM - 4:00 PM ET (6.5 hours/day)
- Windows per day: ~78 frames (6.5 hours × 60 min / 5 min step)
- Total frames: ~1,716 frames (22 days × 78 frames/day)

Each frame is named: `frame_00001_2025-09-23_0930.png`

## Creating Video from Frames

### MP4 (recommended)

```bash
ffmpeg -framerate 8 -i frames_heatmap_aggression/frame_%05d_*.png \
  -pix_fmt yuv420p \
  CG_MNQ_heatmap_aggression_roll.mp4
```

### GIF (larger file)

```bash
ffmpeg -framerate 8 -i frames_heatmap_aggression/frame_%05d_*.png \
  -vf "fps=8,scale=1280:-1:flags=lanczos" \
  CG_MNQ_heatmap_aggression_roll.gif
```

### Frame Rate Guide

| FPS | Description |
|-----|-------------|
| 4 | Slow, detailed observation (1 second = 20 minutes RT) |
| 8 | Moderate pacing (1 second = 40 minutes RT) **recommended** |
| 12 | Fast overview (1 second = 60 minutes RT) |
| 24 | Very fast (1 second = 2 hours RT) |

## Visualization Semantics

### Heatmap (grayscale)

- **Black**: Zero or very low resting liquidity
- **Dark gray**: Below-average resting liquidity
- **Light gray**: Above-average resting liquidity
- **White**: Daily maximum resting liquidity (walls)
- **Scale**: Logarithmic (better visualization of wide liquidity ranges)

### Aggression Bubbles (colored arcs)

- **Circle size**: Proportional to total executed volume (sqrt scaling)
- **Green arc**: Buy/aggressor-up volume share (buyers hitting asks)
- **Red arc**: Sell/aggressor-down volume share (sellers hitting bids)
- **Position**: Placed at median price of heatmap window
- **Open circle**: Shows both resting liquidity underneath and aggression overlay

### Example Interpretation

**Large white vertical band + small green bubble**:
- Strong resistance wall (high resting liquidity)
- Light buying aggression (small volume)
- Wall likely held (resistance not broken)

**White band disappears + large red bubble**:
- Support wall removed
- Heavy selling aggression
- Likely breakdown (support failed)

**Alternating green/red bubbles + stable gray heatmap**:
- Balanced buying and selling
- Stable liquidity
- Choppy/ranging price action

## Schema Requirements

### Heatmap Table

Required columns (actual names in `CG_mnq_heatmap_1s`):
- `bucket_time` (DateTime UTC)
- `price` (Float64)
- `bid_net_liquidity` (Int64) - cumulative bid-side net adds/cancels
- `ask_net_liquidity` (Int64) - cumulative ask-side net adds/cancels

### Aggression Table

Required columns (already in `CG_mnq_aggression_multiscale_v1`):
- `ts_100ms` (DateTime64 UTC)
- `buy_exec_size_1s` (UInt64)
- `sell_exec_size_1s` (UInt64)
- `trade_date` (Date)

### Session Regime Table

Required columns (already in `CG_mnq_session_regime_v2`):
- `trade_date` (Date)

## Validation Sample Performance Context

When viewing these charts, keep in mind the validated strategy performance:

**Core patterns** (from pattern expansion analysis):
- LONG_ORB_HIGH_BREAKOUT_CONTINUATION: 4 trades, 100% WR
- LONG_VWAP_RESISTANCE_RECLAIM: 7 trades, 85.71% WR

**Watch for visual patterns around these times**:
- Sep 26 14:24 (ORB HIGH, +38)
- Sep 29 10:06, 10:21 (ORB HIGH x2, +76)
- Sep 30 15:20 (ORB HIGH, +38)
- Oct 1 10:01 (VWAP, +38)
- Oct 3 10:39 (VWAP, +38)
- Oct 7 10:08 (VWAP, +38)
- Oct 13 10:25, 11:11 (VWAP x2, +76)
- Oct 14 15:47 (VWAP, +38)
- Oct 15 09:42 (VWAP, -22 **only loser**)

Look for correlation between:
- Wall strength (white heatmap intensity)
- Aggression imbalance (green vs red arc dominance)
- Trade outcomes (target vs stop)

## Troubleshooting

### No frames generated

Check:
1. Date range has data in `CG_mnq_session_regime_v2`
2. Heatmap table has RTH data for selected dates
3. Resting expression returns non-zero values

Query to test:
```sql
SELECT
    trade_date,
    count(*) AS rows,
    max(greatest(abs(bid_net_liquidity), abs(ask_net_liquidity))) AS max_resting
FROM CG_mnq_heatmap_1s
WHERE trade_date BETWEEN '2025-09-23' AND '2025-10-22'
GROUP BY trade_date
ORDER BY trade_date
```

### Frames too dark/light

Adjust the resting expression to use different aggregations:
- Too dark: Use `abs(bid_net_liquidity) + abs(ask_net_liquidity)` (sum, higher values)
- Too light: Use `greatest(abs(bid_net_liquidity), abs(ask_net_liquidity))` (max, lower values)

### Aggression bubbles too small/large

Edit script config (lines 40-41):
```python
max_bubble_area: float = 700.0  # increase for larger bubbles
min_bubble_area: float = 20.0   # increase for larger minimum
```

### Wrong timezone

The script uses `America/New_York` timezone for RTH (9:30-16:00 ET). If your data uses different timezone, edit config line 38:
```python
tz: str = "America/Chicago"  # or UTC, etc.
```

## Files

- **Script**: scripts/CG_MNQ_rolling_heatmap_aggression_chart.py
- **Heatmap source**: CG_mnq_heatmap_1s
- **Aggression source**: CG_mnq_aggression_multiscale_v1
- **Date source**: CG_mnq_session_regime_v2

## Next Steps

After generating frames and video:

1. **Visual pattern discovery**: Look for recurring heatmap/aggression patterns before validated trade signals
2. **Wall strength correlation**: Do stronger walls (whiter heatmap) correlate with higher win rates?
3. **Aggression timing**: Does aggression color (green/red dominance) predict breakout direction?
4. **Failure analysis**: What did the Oct 15 loser's heatmap/aggression look like compared to winners?
5. **Regime classification**: Can you visually identify choppy days vs trending days from heatmap patterns?

These visualizations can help develop additional entry filters or regime detection logic for the core strategy.
