#!/bin/bash
# run-app.sh — Runs compiled SFML application on DISPLAY=:99
# Usage: /opt/run-app.sh [executable_path]

APP_BIN="${1:-/workspace/app}"
BIN_NAME="$(basename "${APP_BIN}")"

# Terminate any previously running game
killall -9 "${BIN_NAME}" 2>/dev/null || true
pkill -9 -x "${BIN_NAME}" 2>/dev/null || true
sleep 0.05

if [ ! -f "${APP_BIN}" ]; then
    echo "Error: Executable not found: ${APP_BIN}"
    exit 1
fi

chmod +x "${APP_BIN}"

export DISPLAY=:99
cd /workspace
# Run the application detached using nohup and disown
nohup "${APP_BIN}" > /tmp/app.log 2>&1 &
APP_PID=$!
disown ${APP_PID} 2>/dev/null || true

echo "APP_STARTED:${APP_PID}"
exit 0
