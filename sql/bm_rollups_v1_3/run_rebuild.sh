#!/bin/bash
export CH_HOST='localhost'
export CH_PORT='8123'
export CH_USER='default'
export CH_PASSWORD='unlucky-strange'
export CH_DATABASE='default'

cd /home/bernard/trading4/CG_MNQ_MarketReplayLab/sql/bm_rollups_v1_3
python3 rebuild_heatmap_stateful_v2.py
