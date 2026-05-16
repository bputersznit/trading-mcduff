#!/bin/bash
export CH_HOST='localhost'
export CH_PORT='8123'
export CH_USER='default'
export CH_PASSWORD='unlucky-strange'
export CH_DATABASE='default'
export CH_SECURE='false'

# Bubbles only - no heatmap
python3 BM_MNQ_render_bookmap_frame_v1_3.py \
  --trade-date 2025-10-07 \
  --start-time 09:30:00 \
  --end-time 09:35:00 \
  --scale 1S \
  --symbol "" \
  --price-window-points 15 \
  --center-mode traded_median \
  --heatmap-field heatmap_proxy_value \
  --heatmap-lower-quantile 0.999 \
  --heatmap-upper-quantile 1.0 \
  --bubble-area-q95 200 \
  --bubble-min-area 20 \
  --bubble-max-area 1000 \
  --bubble-alpha 1.0 \
  --dpi 240 \
  --out ./BM_MNQ_v1_3_BUBBLES_ONLY.png
