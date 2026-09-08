# SFML 2.6.2 Online Playground

A fast, modern browser-based C++ SFML 2.6.2 playground that enables students to write, compile, and run **real SFML 2.6.2 code directly from a web browser** with zero local installations.

No Visual Studio, no CMake, no local C++ compiler, no DLLs, no accounts, and no database.

---

## Live Demo

https://sfml.omarelzarka.com/

---

## Quick Start

### Prerequisites

* Docker Desktop (running with Linux containers)
* .NET 10 SDK (or latest stable .NET)
* Node.js 20+ / 24+

### Run Locally for Development

1. **Build the SFML Sandbox Docker Image:**

   ```bash
   docker build -t sfml-sandbox:latest -f runner/Dockerfile runner/
   ```

2. **Start the ASP.NET Core Backend:**

   ```bash
   cd backend/SfmlPlayground.Api
   dotnet run
   ```

   *Listening at `http://localhost:5000`*

3. **Start the Angular Frontend:**

   ```bash
   cd frontend
   npm install
   npm start
   ```

   *Listening at `http://localhost:4200`*

4. Open http://localhost:4200 in your web browser. Write C++ code, click **Run**, and interact with the remote SFML window!

---

## Run with Docker Compose

To run the entire playground infrastructure with Docker Compose:

```bash
docker compose up --build
```

---

## Running Automated Tests

Run the backend unit and API integration tests:

```bash
dotnet test backend/SfmlPlayground.Tests
```

---

## Architecture Overview

```text
Browser (Angular 19 + Monaco Editor)
    │
    ▼ HTTP / WebSocket
ASP.NET Core 10 Web API
    │
    ▼ Docker Engine API
Sandbox Container (sfml-sandbox:latest)
    ├── Xvfb :99 (virtual framebuffer)
    ├── GCC (C++17 compilation against SFML 2.6.2 headers)
    ├── SFML 2.6.2 (genuine graphics, window, and system libraries)
    ├── x11vnc (RFB display server)
    └── websockify + noVNC (HTML5 browser remote display bridge)
```

---

## Project Structure

```text
sfml-playground/
│
├── frontend/                     # Angular 19 client
│   ├── src/app/                  # Components and services
│   ├── src/styles.css            # Custom dark design system
│   └── proxy.conf.json           # API proxy configuration
│
├── backend/
│   ├── SfmlPlayground.Api/       # ASP.NET Core Minimal API
│   │   ├── Models/               # Session & request models
│   │   ├── Services/             # DockerSessionService & SessionCleanupService
│   │   └── Program.cs            # Endpoints & configuration
│   └── SfmlPlayground.Tests/     # xUnit automated tests
│
├── runner/                       # Sandbox execution environment
│   ├── Dockerfile                # Multi-stage Ubuntu build with SFML 2.6.2
│   └── scripts/
│       ├── compile.sh            # Deterministic g++ compile script
│       └── entrypoint.sh         # Xvfb, VNC, websockify & app supervisor
│
├── docs/
│   ├── architecture.md           # System architecture & topology
│   └── azure-deployment.md       # Azure AKS & ACA deployment guide
│
├── docker-compose.yml            # Multi-container local orchestration
└── README.md
```

---

## API Reference

| Method | Endpoint                       | Description                                          |
| :----- | :----------------------------- | :--------------------------------------------------- |
| `POST` | `/api/sessions`                | Create a new session with C++ code & start container |
| `GET`  | `/api/sessions/{id}`           | Get real-time status and compiler output             |
| `GET`  | `/api/sessions/{id}/display`   | Get VNC host, dynamic port, and path                 |
| `POST` | `/api/sessions/{id}/stop`      | Terminate session and remove container               |
| `POST` | `/api/sessions/{id}/heartbeat` | Send heartbeat to prevent auto-cleanup               |
| `GET`  | `/api/sessions`                | List active sessions (for diagnostics)               |

---

## Security Hardening

* **No Host Execution**: Student code NEVER runs on the host system.
* **Resource Constraints**: 512MB RAM, 0.5 CPU cores, max 64 processes per container.
* **Restricted Privileges**: Dropped all Linux capabilities (`CapDrop: ALL`), with only `SYS_PTRACE` allowed for X11 virtual display initialization.
* **`no-new-privileges`**: Enforced on every container.
* **Non-Root Execution**: Runs as dedicated `runner` user (UID 1000).
* **Auto-Cleanup**: Containers are launched with auto-removal and monitored by `SessionCleanupService`.
* **Zero Database Persistence**: Zero disk state, accounts, or user data retained.
