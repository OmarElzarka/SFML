# Architecture Overview — SFML 2.6.2 Online Playground

## 1. System Topology

The SFML Online Playground is designed around containerized isolation, sub-second execution startup, secure single-port HTTPS/WSS proxying, and native SFML 2.6.2 graphical and audio execution:

```text
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                                     STUDENT BROWSER                                    │
│                                                                                        │
│   ┌───────────────────────────┐    ┌───────────────────────────────────────────────┐   │
│   │    Monaco Editor (C++)    │    │              SFML 2.6.2 Viewport              │   │
│   │ • Multi-file C++ editing  │    │ ┌──────────────────────┐ ┌──────────────────┐ │   │
│   │ • Real-time GCC output    │    │ │ noVNC Canvas (RFB)   │ │ Web Audio API    │ │   │
│   │ • Project & Asset manager │    │ │ (Display Port 6080)  │ │ (Audio Port 6081)│ │   │
│   └─────────────┬─────────────┘    │ └──────────▲───────────┘ └────────▲─────────┘ │   │
│                 │                  └────────────┼──────────────────────┼───────────┘   │
│                 │ POST /api/projects/{id}/run   │                      │               │
└─────────────────┼───────────────────────────────┼──────────────────────┼───────────────┘
                  │                               │                      │
                  ▼                               │                      │
┌─────────────────────────────────────────────────┴──────────────────────┴───────────────┐
│                           ASP.NET Core 10 Web API Backend                              │
│                                                                                        │
│  • Reverse Proxy (VncProxyService):                                                    │
│    - Routes /vnc/{sessionId}       ──► Container Port 6080 (RFB / noVNC Lite UI)       │
│    - Routes /vnc/{sessionId}/audio ──► Container Port 6081 (Raw PCM WebSocket Stream)  │
│  • Persistent Runtime Orchestrator (PersistentRuntimeService):                         │
│    - Manages single persistent, pre-warmed sandbox container (`sfml-runtime`)         │
│    - Hot compiles student files directly inside /workspace without restart delay       │
│    - Launches native SFML app instantly via detached runner process                   │
│  • Project & Asset Store: In-memory project trees with multi-file compilation support  │
└────────────────────────────────────────┬───────────────────────────────────────────────┘
                                         │ Docker Engine (Local Socket)
                                         ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                        PERSISTENT DOCKER SANDBOX RUNNER                                │
│                        Image: sfml-sandbox:latest (`sfml-runtime`)                     │
│                                                                                        │
│  ┌──────────────────────────────────────────────────────────────────────────────────┐  │
│  │ Security & Sandbox Limits                                                        │  │
│  │ User: runner (UID 1000) | Memory: 512MB | CPUs: 1.0 | PIDs: 128                  │  │
│  │ CapDrop: ALL | CapAdd: SYS_PTRACE | SecurityOpt: no-new-privileges               │  │
│  └──────────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                        │
│  ┌────────────────────────────────────────┐ ┌───────────────────────────────────────┐  │
│  │ Graphical Display Pipeline             │ │ Low-Latency Audio Pipeline            │  │
│  │ 1. Xvfb :99 (1024x768x24 Framebuffer)  │ │ 1. PulseAudio (Headless Virtual Sink) │  │
│  │ 2. Openbox (Window Manager + Controls) │ │    - UNIX Socket /tmp/pulse-socket    │  │
│  │ 3. x11vnc (RFB Server on port 5900)    │ │    - module-null-sink:                │  │
│  │ 4. websockify (Port 6080 -> 5900)      │ │      SFML_Virtual_Sink                │  │
│  │                                        │ │ 2. audio-relay.py (Port 6081)         │  │
│  │                                        │ │    - Spawns parec on demand           │  │
│  │                                        │ │    - Streams raw 16-bit 44.1kHz stereo│  │
│  │                                        │ │      PCM over RFC 6455 WebSockets     │  │
│  └────────────────────────────────────────┘ └───────────────────────────────────────┘  │
│                                                                                        │
│  3. Student Application Execution:                                                     │
│     - Compilation: g++ -std=c++17 *.cpp -lsfml-graphics -lsfml-window -lsfml-audio ... │
│     - Execution: PULSE_SERVER=unix:/tmp/pulse-socket DISPLAY=:99 ./app                 │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Key Components

### A. Frontend (Angular 19 + Monaco)
- **Monaco Editor**: High-performance multi-file C++ editor supporting `.cpp`, `.hpp`, and `.h` files with VS Code-like shortcuts (`Ctrl+Enter` to Run, `Ctrl+S` suppression).
- **Project & Asset Manager**: Supports creating and switching multiple student projects, uploading image (`.png`, `.jpg`, `.bmp`), audio (`.wav`, `.ogg`, `.flac`), and font (`.ttf`) assets with live asset streaming to the runner workspace.
- **VNC Viewport**: Embeds noVNC `vnc_lite.html` with a custom borderless styling and auto-focus helper script, supporting window management (Openbox), resizing, and pop-out mode.
- **AudioService (Web Audio API)**:
  - Connects to `/vnc/{sessionId}/audio` via WebSocket.
  - Receives raw 16-bit 44.1kHz stereo PCM frames directly from the container's virtual sink.
  - Normalizes samples and schedules playback onto an `AudioBufferSourceNode` chain.
  - Dynamic drift prevention: caps buffer lead-in to 25–100ms to guarantee lip-sync precision with the VNC display.
  - Full volume slider and mute controls in the viewport header, with browser autoplay policy handling on user gestures.
- **Terminal Panel**: Displays real-time GCC compilation diagnostics and runtime exit status.

### B. Backend (ASP.NET Core 10 Web API)
- **Unified HTTPS / WSS Gateway**: All traffic (frontend static files, REST API, VNC RFB stream, and PCM audio stream) is served through port 443 / 80, eliminating mixed-content errors and firewall restrictions.
- **VncProxyService**:
  - Automatically identifies whether an incoming WebSocket is display or audio traffic.
  - Proxies RFB data to container port 6080 and PCM audio data to container port 6081.
  - Negotiates RFC 6455 subprotocols dynamically and forwards frames with zero buffering delay.
- **PersistentRuntimeService**:
  - Maintains a pre-warmed sandbox container (`sfml-runtime`).
  - Handles asset synchronization, source code transfer, incremental builds, and execution restarts without container spin-up overhead.

### C. Runner Sandbox (`sfml-sandbox:latest`)
- **Base Environment**: Ubuntu 22.04 LTS with multi-stage build.
- **SFML Pinned**: Builds SFML `2.6.2` from source with all modules enabled (`Graphics`, `Window`, `Audio`, `Network`, `System`).
- **Audio Subsystem**:
  - PulseAudio configured with a virtual null sink (`SFML_Virtual_Sink`) communicating over UNIX domain socket `/tmp/pulse-socket`.
  - OpenAL Soft automatically selects PulseAudio backend and outputs to `SFML_Virtual_Sink`.
  - `audio-relay.py`: High-performance asynchronous WebSocket streamer written in pure Python standard library (RFC 6455), reading PCM chunks from `parec` with 20ms latency and broadcasting to connected browser clients.
- **Virtual Display**: `Xvfb` running on `:99` (1024x768x24) with Openbox window manager for authentic desktop window behavior.
- **Security Hardening**:
  - Non-root user (`runner`, UID 1000).
  - Strict resource constraints (512MB RAM, 1.0 CPU, 128 PID limit).
  - Linux capability drop (`CapDrop: ALL`, with `SYS_PTRACE` for X11 virtual display).
  - `no-new-privileges` flag enabled.
