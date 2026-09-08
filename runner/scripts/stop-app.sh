#!/bin/bash
# stop-app.sh — Stops any running SFML application
APP_BIN="${1:-/workspace/app}"
BIN_NAME="$(basename "${APP_BIN}")"
killall -9 "${BIN_NAME}" 2>/dev/null || true
pkill -9 -x "${BIN_NAME}" 2>/dev/null || true
echo "APP_STOPPED"
exit 0
