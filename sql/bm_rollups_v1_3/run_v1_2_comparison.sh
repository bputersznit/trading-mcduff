#!/bin/bash
export CH_HOST='localhost'
export CH_PORT='8123'
export CH_USER='default'
export CH_PASSWORD='unlucky-strange'
export CH_DATABASE='default'
export CH_SECURE='false'

python3 BM_MNQ_render_bookmap_frame_v1_2.py \
  --trade-date 2025-10-07 \
  --start-time 09:30:00 \
  --end-time 09:35:00 \
  --scale 1S \
  --symbol "" \
  --heatmap-field heatmap_proxy_value \
  --heatmap-lower-quantile 0.55 \
  --heatmap-upper-quantile 0.999 \
  --heatmap-gamma 0.38 \
  --bubble-area-q95 45 \
  --bubble-alpha 0.35 \
  --out ./BM_MNQ_v1_2_comparison.png
