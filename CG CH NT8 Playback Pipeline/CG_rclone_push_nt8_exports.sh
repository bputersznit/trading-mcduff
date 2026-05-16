#!/usr/bin/env bash
set -euo pipefail

# CG_rclone_push_nt8_exports.sh
# Strategy/methodology:
#   Push compressed CH export folders using rclone with resume/retry behavior.
#   Prefer sync only when destination should mirror source. Use copy for additive transfer.

SRC_DIR="${SRC_DIR:-$HOME/nt8_exports/mnqz5_sep_oct_2025}"
REMOTE_PATH="${REMOTE_PATH:-gdrive:bento/nt8_exports/mnqz5_sep_oct_2025}"
MODE="${MODE:-copy}"   # copy or sync
LOG_DIR="${LOG_DIR:-$SRC_DIR/logs}"
mkdir -p "$LOG_DIR"
LOG_FILE="$LOG_DIR/rclone_$(date +%Y%m%d_%H%M%S).log"

if [[ ! -d "$SRC_DIR" ]]; then
  echo "[ERROR] SRC_DIR does not exist: $SRC_DIR" >&2
  exit 1
fi

if [[ "$MODE" != "copy" && "$MODE" != "sync" ]]; then
  echo "[ERROR] MODE must be copy or sync" >&2
  exit 1
fi

echo "[RCLONE] $MODE $SRC_DIR → $REMOTE_PATH"

rclone "$MODE" "$SRC_DIR" "$REMOTE_PATH" \
  --transfers 4 \
  --checkers 8 \
  --retries 10 \
  --low-level-retries 20 \
  --stats 30s \
  --progress \
  --log-file "$LOG_FILE" \
  --log-level INFO

echo "[DONE] rclone log: $LOG_FILE"
