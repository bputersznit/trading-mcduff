#!/bin/bash
export CH_HOST='localhost'
export CH_PORT='8123'
export CH_USER='default'
export CH_PASSWORD='unlucky-strange'
export CH_DATABASE='default'
export CH_SECURE='false'

# Aggression range: 25207.5 - 25265.5 (58 points)
# Show 58 points above AND 58 points below
# Total: 58 + 58 + 58 = 174 points
# Center: 25236.5, ±87 points = 348 ticks

python3 BM_MNQ_render_bookmap_frame_v1_4.py \
  --trade-date 2025-10-07 \
  --start-time 09:30:00 \
  --end-time 10:30:00 \
  --scale 1S \
  --symbol MNQZ5 \
  --price-window-points 348 \
  --center-mode traded_median \
  --heatmap-field heatmap_proxy_value \
  --heatmap-lower-quantile 0.55 \
  --heatmap-upper-quantile 0.999 \
  --heatmap-gamma 0.38 \
  --persistence-alpha 0.92 \
  --bubble-area-q95 100 \
  --bubble-alpha 0.75 \
  --dpi 200 \
  --fig-width 32 \
  --fig-height 24 \
  --out ./BM_MNQ_1HOUR_FULL_CONTEXT.png
