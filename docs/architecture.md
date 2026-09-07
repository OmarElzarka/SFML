# Architecture Overview — SFML 2.6.2 Online Playground

## 1. System Topology

The SFML Online Playground is designed around complete isolation, determinism, zero persistence, and real native SFML 2.6.2 execution:

```text
┌─────────────────────────────────────────────────────────────┐
│                      STUDENT BROWSER                        │
│                                                             │
│   ┌─────────────────────────┐   ┌───────────────────────┐   │
│   │      Monaco Editor      │   │  SFML Viewport (VNC)  │   │
│   │  (C++ syntax, shortcuts)│   │   (noVNC Canvas, RFB) │   │
│   └────────────┬────────────┘   └───────────▲───────────┘   │
│                │ POST /api/sessions         │               │
│                │ (JSON Source Code)         │               │
└────────────────┼────────────────────────────┼───────────────┘
                 │                            │
                 ▼                            │ WebSocket
┌─────────────────────────────────────────┐   │ (ws://host:port)
│          ASP.NET Core 10 Web API        │   │
│                                         │   │
│  • Endpoint Routing & Request Guard     │   │
│  • Session State Management (Memory)    │   │
│  • Dynamic Port Allocator               │   │
│  • DockerClient (Host Daemon Control)   │   │
│  • SessionCleanupService (Heartbeat)    │   │
└────────────────────┬────────────────────┘   │
                     │ Docker Engine API      │
                     ▼                        │
┌─────────────────────────────────────────┐   │
│          DOCKER SANDBOX RUNNER          │   │
│  Image: sfml-sandbox:latest             │   │
│                                         │   │
│  ┌───────────────────────────────────┐  │   │
│  │ User: runner (non-root UID 1000)  │  │   │
│  │ Memory: 512MB | CPUs: 0.5         │  │   │
│  │ CapDrop: ALL | CapAdd: SYS_PTRACE │  │   │
│  │ SecurityOpt: no-new-privileges    │  │   │
│  └───────────────────────────────────┘  │   │
│                                         │   │
│  1. /opt/compile.sh (g++ C++17)         │   │
│     -lsfml-graphics -lsfml-window ...   │   │
│  2. Xvfb :99 (Virtual Framebuffer)      │   │
│  3. x11vnc (RFB port 5900)              │   │
│  4. websockify (Port 6080 -> 5900) ─────┴───┘
│  5. ./app (Genuine SFML 2.6.2 Window)
└─────────────────────────────────────────┘
```

---

## 2. Key Components

### A. Frontend (Angular 19 + Monaco)
- **Monaco Editor**: Provides a rich C++ editing experience with custom dark theme, bracket pairing, line numbers, and keyboard shortcuts (`Ctrl+Enter` to Run, `Ctrl+S` browser-save suppression).
- **SessionService**: Handles session creation via `HttpClient`, polls or reacts to status transitions, triggers heartbeats every 15s to keep sessions alive, and provides connection metadata.
- **Viewport**: Hosts the `vnc_lite.html` client in an iframe with scaling enabled, allowing student interaction with keyboard and mouse.
- **Terminal Panel**: Renders real-time compiler diagnostic messages (preserving GCC file, line, and column numbers) or runtime exit status.

### B. Backend (ASP.NET Core 10 Web API)
- **Zero Database Persistence**: All sessions are strictly in-memory (`ConcurrentDictionary<string, Session>`). Once stopped or expired, no code, logs, or user traces remain.
- **Dynamic Port Allocation**: Finds available high TCP ports on demand for each sandbox container's websockify endpoint.
- **Strict Lifecycles**: Automatic cleanup via `SessionCleanupService` if browser disconnects or ceases sending heartbeats.
- **RESTful Endpoints**:
  - `POST /api/sessions`: Accepts source code, creates and starts sandbox container.
  - `GET /api/sessions/{id}`: Returns status, compiler output, and error messages.
  - `GET /api/sessions/{id}/display`: Returns VNC connection host and port.
  - `POST /api/sessions/{id}/stop`: Immediately stops and removes container.
  - `POST /api/sessions/{id}/heartbeat`: Updates session last-active timestamp.

### C. Runner Sandbox (`sfml-sandbox:latest`)
- **Base Environment**: Ubuntu 22.04 LTS with multi-stage build.
- **SFML Pinned**: Clones and builds SFML `2.6.2` strictly from git tags with CMake.
- **Virtual Display**: `Xvfb` running on `:99` with 24-bit color depth (1024x768).
- **VNC Bridge**: `x11vnc` captures the virtual X11 server and exposes RFB protocol on port 5900.
- **WebSocket Bridge**: `websockify` exposes noVNC static web assets and bridges browser WebSockets directly to `127.0.0.1:5900`.
- **Security Hardening**:
  - Non-root user (`runner`).
  - Strict resource constraints (512MB RAM, 0.5 CPU, 64 process limit).
  - All Linux capabilities dropped (`CapDrop: ALL`), with only `SYS_PTRACE` allowed for X11 virtual display initialization.
  - `no-new-privileges` flag enabled.
  - Auto-removal of containers on termination.
