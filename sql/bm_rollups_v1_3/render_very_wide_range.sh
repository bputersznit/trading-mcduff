#!/bin/bash
export CH_HOST='localhost'
export CH_PORT='8123'
export CH_USER='default'
export CH_PASSWORD='unlucky-strange'
export CH_DATABASE='default'
export CH_SECURE='false'

# Same 1 hour, QUADRUPLE original range (20 → 80 points)
python3 BM_MNQ_render_bookmap_frame_v1_3.py \
  --trade-date 2025-10-07 \
  --start-time 09:30:00 \
  --end-time 10:30:00 \
  --scale 1S \
  --symbol MNQZ5 \
  --price-window-points 80 \
  --center-mode traded_median \
  --heatmap-field heatmap_proxy_value \
  --heatmap-lower-quantile 0.55 \
  --heatmap-upper-quantile 0.999 \
  --heatmap-gamma 0.38 \
  --persistence-alpha 0.85 \
  --bubble-area-q95 100 \
  --bubble-alpha 0.75 \
  --dpi 220 \
  --fig-width 28 \
  --fig-height 18 \
  --out ./BM_MNQ_1HOUR_FULL_DEPTH.png
