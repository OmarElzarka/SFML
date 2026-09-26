#!/usr/bin/env python3
"""
audio-relay.py — Ultra-low-latency WebSocket Audio Streamer for SFML Playground
Streams raw 16-bit PCM (44.1kHz, Stereo) from PulseAudio monitor sink directly to browser clients.
Runs on port 6081.
"""

import asyncio
import os
import signal
import sys
import logging
import websockets

logging.basicConfig(
    level=logging.INFO,
    format="[audio-relay] %(asctime)s %(levelname)s: %(message)s"
)
logger = logging.getLogger("audio-relay")

HOST = "0.0.0.0"
PORT = 6081
PULSE_SERVER = os.environ.get("PULSE_SERVER", "unix:/tmp/pulse-socket")
PULSE_DEVICE = "SFML_Virtual_Sink.monitor"

# 44100 Hz * 2 channels * 2 bytes/sample * 0.025s (25ms chunks) ≈ 4410 bytes
CHUNK_SIZE = 4410

connected_clients = set()
parec_proc = None
reader_task = None
stream_lock = asyncio.Lock()


async def start_stream():
    global parec_proc, reader_task
    if parec_proc is not None:
        return

    cmd = [
        "parec",
        f"--server={PULSE_SERVER}",
        "-d", PULSE_DEVICE,
        "--format=s16le",
        "--rate=44100",
        "--channels=2",
        "--raw",
        "--latency-msec=20"
    ]
    logger.info("Starting parec capture process: %s", " ".join(cmd))
    try:
        parec_proc = await asyncio.create_subprocess_exec(
            *cmd,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.DEVNULL
        )
        reader_task = asyncio.create_task(audio_broadcaster())
    except Exception as e:
        logger.error("Failed to start parec process: %s", e)
        parec_proc = None


async def stop_stream():
    global parec_proc, reader_task
    if reader_task:
        reader_task.cancel()
        reader_task = None

    if parec_proc:
        logger.info("Stopping parec capture process...")
        try:
            parec_proc.terminate()
            await asyncio.wait_for(parec_proc.wait(), timeout=2.0)
        except Exception:
            try:
                parec_proc.kill()
            except Exception:
                pass
        parec_proc = None


async def audio_broadcaster():
    """Continuously reads PCM chunks from parec and broadcasts to all WebSocket clients."""
    global parec_proc
    logger.info("Audio broadcast loop started.")
    try:
        while parec_proc and parec_proc.stdout and not parec_proc.stdout.at_eof():
            data = await parec_proc.stdout.read(CHUNK_SIZE)
            if not data:
                await asyncio.sleep(0.01)
                continue

            if not connected_clients:
                continue

            # Broadcast chunk to all active websockets
            dead_clients = []
            for ws in list(connected_clients):
                try:
                    await ws.send(data)
                except Exception:
                    dead_clients.append(ws)

            for dead in dead_clients:
                connected_clients.discard(dead)

    except asyncio.CancelledError:
        pass
    except Exception as e:
        logger.error("Error in audio broadcaster: %s", e)
    finally:
        logger.info("Audio broadcast loop ended.")


async def handle_client(websocket, path):
    """Handles an incoming WebSocket client connection."""
    client_ip = websocket.remote_address
    logger.info("New audio client connected from %s (path: %s). Total clients: %d",
                client_ip, path, len(connected_clients) + 1)

    async with stream_lock:
        connected_clients.add(websocket)
        if len(connected_clients) == 1:
            await start_stream()

    try:
        # Keep connection open until client disconnects
        async for _ in websocket:
            pass
    except Exception:
        pass
    finally:
        async with stream_lock:
            connected_clients.discard(websocket)
            logger.info("Audio client %s disconnected. Remaining clients: %d",
                        client_ip, len(connected_clients))
            if len(connected_clients) == 0:
                await stop_stream()


async def main():
    loop = asyncio.get_running_loop()

    def shutdown():
        logger.info("Shutdown signal received.")
        for task in asyncio.all_tasks(loop):
            task.cancel()

    for sig in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(sig, shutdown)

    server = await websockets.serve(
        handle_client,
        HOST,
        PORT,
        max_size=None,
        ping_interval=20,
        ping_timeout=20
    )
    logger.info("Audio Relay Server listening on ws://%s:%d", HOST, PORT)
    await server.wait_closed()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except (KeyboardInterrupt, asyncio.CancelledError):
        logger.info("Audio relay server stopped.")
