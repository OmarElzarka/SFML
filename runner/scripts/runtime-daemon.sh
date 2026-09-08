#!/bin/bash
set -e

DISPLAY_NUM=":99"
VNC_PORT=5900
WEBSOCKIFY_PORT=6080

echo "[runtime-daemon] Starting persistent SFML 2.6.2 graphical runtime..."

# 1. Start Virtual Display
echo "[runtime-daemon] Starting Xvfb on display ${DISPLAY_NUM}..."
Xvfb ${DISPLAY_NUM} -screen 0 1024x768x24 -ac +extension GLX +render -noreset &
XVFB_PID=$!
export DISPLAY=${DISPLAY_NUM}

# Wait for Xvfb
for i in $(seq 1 30); do
    if xdpyinfo -display ${DISPLAY_NUM} >/dev/null 2>&1; then
        echo "[runtime-daemon] Xvfb is ready on ${DISPLAY_NUM}."
        break
    fi
    sleep 0.1
done

# 2. Start Window Manager
echo "[runtime-daemon] Setting dark background..."
xsetroot -solid "#161b22" 2>/dev/null || true

echo "[runtime-daemon] Starting openbox window manager..."
openbox --sm-disable &
OPENBOX_PID=$!
sleep 0.2

# 3. Start VNC Server
echo "[runtime-daemon] Starting x11vnc on port ${VNC_PORT}..."
x11vnc -display ${DISPLAY_NUM} -nopw -listen 127.0.0.1 -rfbport ${VNC_PORT} \
    -shared -forever -noxdamage -cursor arrow \
    >/dev/null 2>&1 &
X11VNC_PID=$!

# Wait for x11vnc
for i in $(seq 1 30); do
    if (echo > /dev/tcp/127.0.0.1/${VNC_PORT}) >/dev/null 2>&1; then
        echo "[runtime-daemon] x11vnc is ready on port ${VNC_PORT}."
        break
    fi
    sleep 0.1
done

# 4. Start WebSocket Proxy
echo "[runtime-daemon] Starting websockify on port ${WEBSOCKIFY_PORT}..."
websockify --web /usr/share/novnc ${WEBSOCKIFY_PORT} 127.0.0.1:${VNC_PORT} \
    >/dev/null 2>&1 &
WEBSOCKIFY_PID=$!

# Wait for websockify
for i in $(seq 1 30); do
    if (echo > /dev/tcp/127.0.0.1/${WEBSOCKIFY_PORT}) >/dev/null 2>&1; then
        echo "[runtime-daemon] websockify is ready on port ${WEBSOCKIFY_PORT}."
        break
    fi
    sleep 0.1
done

echo "[runtime-daemon] SFML 2.6.2 graphical runtime is ACTIVE on display ${DISPLAY_NUM}, port ${WEBSOCKIFY_PORT}."

# Cleanup on signal
cleanup() {
    echo "[runtime-daemon] Shutting down graphical services..."
    pkill -9 -x app 2>/dev/null || true
    kill ${WEBSOCKIFY_PID} 2>/dev/null || true
    kill ${X11VNC_PID} 2>/dev/null || true
    kill ${OPENBOX_PID} 2>/dev/null || true
    kill ${XVFB_PID} 2>/dev/null || true
    exit 0
}
trap cleanup SIGTERM SIGINT

# Keep container running indefinitely
wait ${WEBSOCKIFY_PID}
