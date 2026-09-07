#!/bin/bash
set -e

SOURCE_FILE="/workspace/main.cpp"
OUTPUT_FILE="/workspace/app"
DISPLAY_NUM=":99"
VNC_PORT=5900
WEBSOCKIFY_PORT=6080

# ─── Start Virtual Display ──────────────────────────────────────────────────
echo "[entrypoint] Starting Xvfb on display ${DISPLAY_NUM}..."
Xvfb ${DISPLAY_NUM} -screen 0 1024x768x24 -ac +extension GLX +render -noreset &
XVFB_PID=$!
export DISPLAY=${DISPLAY_NUM}

# Wait for Xvfb to be ready
sleep 0.5
for i in $(seq 1 20); do
    if xdpyinfo -display ${DISPLAY_NUM} >/dev/null 2>&1; then
        echo "[entrypoint] Xvfb is ready."
        break
    fi
    sleep 0.25
done

# ─── Start Window Manager ───────────────────────────────────────────────────
echo "[entrypoint] Setting dark background..."
xsetroot -solid "#161b22" 2>/dev/null || true

echo "[entrypoint] Starting openbox window manager..."
openbox --sm-disable &
OPENBOX_PID=$!
sleep 0.3

# ─── Compile ────────────────────────────────────────────────────────────────
echo "[entrypoint] Compiling..."
COMPILE_OUTPUT=$(/opt/compile.sh "${SOURCE_FILE}" "${OUTPUT_FILE}" 2>&1) || {
    COMPILE_EXIT=$?
    echo "COMPILE_ERROR"
    echo "${COMPILE_OUTPUT}"
    exit ${COMPILE_EXIT}
}
echo "COMPILE_SUCCESS"
echo "${COMPILE_OUTPUT}"

# ─── Start VNC Server ───────────────────────────────────────────────────────
echo "[entrypoint] Starting x11vnc..."
x11vnc -display ${DISPLAY_NUM} -nopw -listen 127.0.0.1 -rfbport ${VNC_PORT} \
    -shared -forever -noxdamage -cursor arrow \
    >/dev/null 2>&1 &
X11VNC_PID=$!
sleep 0.5

# ─── Start WebSocket Proxy ──────────────────────────────────────────────────
echo "[entrypoint] Starting websockify on port ${WEBSOCKIFY_PORT}..."
websockify --web /usr/share/novnc ${WEBSOCKIFY_PORT} 127.0.0.1:${VNC_PORT} \
    >/dev/null 2>&1 &
WEBSOCKIFY_PID=$!
sleep 0.5

echo "DISPLAY_READY"
echo "WEBSOCKIFY_PORT=${WEBSOCKIFY_PORT}"

# ─── Run the SFML Application ───────────────────────────────────────────────
echo "[entrypoint] Running application..."
echo "APP_STARTED"

# Run the compiled application
"${OUTPUT_FILE}" 2>&1 || {
    APP_EXIT=$?
    echo "APP_TERMINATED"
    echo "EXIT_CODE=${APP_EXIT}"
}

echo "APP_FINISHED"

# Cleanup
kill ${X11VNC_PID} 2>/dev/null || true
kill ${WEBSOCKIFY_PID} 2>/dev/null || true
kill ${OPENBOX_PID} 2>/dev/null || true
kill ${XVFB_PID} 2>/dev/null || true

exit ${APP_EXIT:-0}
