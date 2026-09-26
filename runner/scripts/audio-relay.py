#!/usr/bin/env python3
"""
audio-relay.py — Pure Python Standard Library WebSocket Audio Streamer
Zero external dependencies (uses asyncio, hashlib, base64, socket).
Streams raw 16-bit PCM (44.1kHz, Stereo) from PulseAudio monitor sink directly to browser clients.
Listens on port 6081.
"""

import asyncio
import base64
import hashlib
import logging
import os
import signal
import sys

logging.basicConfig(
    level=logging.INFO,
    format="[audio-relay] %(asctime)s %(levelname)s: %(message)s"
)
logger = logging.getLogger("audio-relay")

HOST = "0.0.0.0"
PORT = 6081
PULSE_SERVER = os.environ.get("PULSE_SERVER", "unix:/tmp/pulse-socket")
PULSE_DEVICE = "SFML_Virtual_Sink.monitor"
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

# 44100 Hz * 2 channels * 2 bytes/sample * 0.025s (25ms chunks) ≈ 4410 bytes
CHUNK_SIZE = 4410

connected_clients = set()
parec_proc = None
reader_task = None
stream_lock = asyncio.Lock()


def make_websocket_frame(data: bytes, opcode: int = 0x02) -> bytes:
    """Encodes raw binary data into an unmasked WebSocket frame (RFC 6455)."""
    length = len(data)
    first_byte = 0x80 | (opcode & 0x0F)
    if length < 126:
        header = bytes([first_byte, length])
    elif length <= 65535:
        header = bytes([first_byte, 126]) + length.to_bytes(2, "big")
    else:
        header = bytes([first_byte, 127]) + length.to_bytes(8, "big")
    return header + data


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
    """Continuously reads PCM chunks from parec and broadcasts as WebSocket binary frames."""
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

            frame = make_websocket_frame(data, opcode=0x02)
            dead_clients = []
            for writer in list(connected_clients):
                try:
                    writer.write(frame)
                    await writer.drain()
                except Exception:
                    dead_clients.append(writer)

            for dead in dead_clients:
                connected_clients.discard(dead)
                try:
                    dead.close()
                except Exception:
                    pass

    except asyncio.CancelledError:
        pass
    except Exception as e:
        logger.error("Error in audio broadcaster: %s", e)
    finally:
        logger.info("Audio broadcast loop ended.")


async def handle_client(reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
    """Handles an incoming WebSocket handshake and lifecycle."""
    client_addr = writer.get_extra_info("peername")
    logger.info("Client connected from %s", client_addr)

    try:
        # Read HTTP Upgrade Handshake
        headers = {}
        request_line = await reader.readline()
        if not request_line:
            writer.close()
            return

        while True:
            line = await reader.readline()
            if not line or line == b"\r\n" or line == b"\n":
                break
            line_str = line.decode("utf-8", errors="ignore").strip()
            if ":" in line_str:
                k, v = line_str.split(":", 1)
                headers[k.strip().lower()] = v.strip()

        ws_key = headers.get("sec-websocket-key")
        if not ws_key:
            logger.warning("Missing Sec-WebSocket-Key from %s", client_addr)
            writer.write(b"HTTP/1.1 400 Bad Request\r\n\r\n")
            await writer.drain()
            writer.close()
            return

        # Compute WebSocket Accept Hash
        accept_raw = hashlib.sha1((ws_key + WS_GUID).encode("utf-8")).digest()
        accept_str = base64.b64encode(accept_raw).decode("utf-8")

        # Negotiate Subprotocol if client requested
        ws_protocol = headers.get("sec-websocket-protocol")
        protocol_header = f"Sec-WebSocket-Protocol: {ws_protocol}\r\n" if ws_protocol else ""

        response = (
            "HTTP/1.1 101 Switching Protocols\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Sec-WebSocket-Accept: {accept_str}\r\n"
            f"{protocol_header}"
            "\r\n"
        )
        writer.write(response.encode("utf-8"))
        await writer.drain()
        logger.info("WebSocket handshake successful for %s (protocol: %s)", client_addr, ws_protocol or "default")

        async with stream_lock:
            connected_clients.add(writer)
            if len(connected_clients) == 1:
                await start_stream()

        # Listen for client frames or close
        while True:
            header = await reader.read(2)
            if not header or len(header) < 2:
                break
            opcode = header[0] & 0x0F
            masked = (header[1] & 0x80) != 0
            payload_len = header[1] & 0x7F

            if payload_len == 126:
                ext = await reader.readexactly(2)
                payload_len = int.from_bytes(ext, "big")
            elif payload_len == 127:
                ext = await reader.readexactly(8)
                payload_len = int.from_bytes(ext, "big")

            mask = b""
            if masked:
                mask = await reader.readexactly(4)

            payload = b""
            if payload_len > 0:
                payload = await reader.readexactly(payload_len)

            if opcode == 0x08:  # Close frame
                logger.info("Client %s sent Close frame.", client_addr)
                try:
                    writer.write(make_websocket_frame(b"", opcode=0x08))
                    await writer.drain()
                except Exception:
                    pass
                break
            elif opcode == 0x09:  # Ping frame -> Respond with Pong
                try:
                    writer.write(make_websocket_frame(payload, opcode=0x0A))
                    await writer.drain()
                except Exception:
                    pass
            elif opcode == 0x0A:  # Pong frame
                pass  # Keepalive acknowledged

    except Exception as e:
        logger.debug("Client handler exception: %s", e)
    finally:
        async with stream_lock:
            connected_clients.discard(writer)
            logger.info("Client %s disconnected. Active clients: %d", client_addr, len(connected_clients))
            if len(connected_clients) == 0:
                await stop_stream()
        try:
            writer.close()
            await writer.wait_closed()
        except Exception:
            pass


async def main():
    loop = asyncio.get_running_loop()

    def shutdown():
        logger.info("Shutdown signal received.")
        for task in asyncio.all_tasks(loop):
            task.cancel()

    for sig in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(sig, shutdown)

    server = await asyncio.start_server(handle_client, HOST, PORT)
    logger.info("Audio Relay Server listening on %s:%d", HOST, PORT)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except (KeyboardInterrupt, asyncio.CancelledError):
        logger.info("Audio relay server stopped.")
