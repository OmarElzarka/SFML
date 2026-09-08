# SFML 2.6.2 Playground — Sandbox Runner
# This image provides a complete SFML 2.6.2 build + runtime environment
# with virtual display (Xvfb) and browser-accessible VNC (noVNC/websockify).

FROM ubuntu:22.04 AS sfml-builder

ENV DEBIAN_FRONTEND=noninteractive

# Install SFML 2.6.2 build dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    cmake \
    git \
    ca-certificates \
    libx11-dev \
    libxrandr-dev \
    libxcursor-dev \
    libxi-dev \
    libudev-dev \
    libgl1-mesa-dev \
    libfreetype-dev \
    libopenal-dev \
    libvorbis-dev \
    libflac-dev \
    && rm -rf /var/lib/apt/lists/*

# Build SFML 2.6.2 from source (pinned version)
WORKDIR /build
RUN git clone --branch 2.6.2 --depth 1 https://github.com/SFML/SFML.git sfml-2.6.2 \
    && cd sfml-2.6.2 \
    && cmake -B build \
        -DCMAKE_INSTALL_PREFIX=/usr/local \
        -DCMAKE_BUILD_TYPE=Release \
        -DSFML_BUILD_EXAMPLES=OFF \
        -DSFML_BUILD_DOC=OFF \
        -DSFML_BUILD_AUDIO=ON \
        -DSFML_BUILD_GRAPHICS=ON \
        -DSFML_BUILD_WINDOW=ON \
        -DSFML_BUILD_NETWORK=ON \
    && cmake --build build --parallel $(nproc) \
    && cmake --install build

# ─── Runtime Stage ───────────────────────────────────────────────────────────
FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive

# Install runtime dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    # C++ compiler & language server for student code
    g++ \
    clangd \
    # SFML runtime dependencies
    libx11-6 \
    libxrandr2 \
    libxcursor1 \
    libxi6 \
    libudev1 \
    libgl1-mesa-glx \
    libgl1-mesa-dri \
    libfreetype6 \
    libopenal1 \
    libvorbisfile3 \
    libflac8 \
    # Virtual display
    xvfb \
    # VNC server
    x11vnc \
    # WebSocket proxy
    websockify \
    # noVNC browser client
    novnc \
    # Utilities
    procps \
    xdotool \
    && rm -rf /var/lib/apt/lists/*

# Copy SFML 2.6.2 libraries and headers from builder
COPY --from=sfml-builder /usr/local/lib/ /usr/local/lib/
COPY --from=sfml-builder /usr/local/include/SFML/ /usr/local/include/SFML/
RUN ldconfig

# Create non-root user
RUN useradd -m -s /bin/bash runner

# Copy scripts
COPY scripts/ /opt/
RUN chmod +x /opt/*.sh

# Create working directory and X11 socket directory
RUN mkdir -p /workspace && chown runner:runner /workspace \
    && mkdir -p /tmp/.X11-unix && chmod 1777 /tmp/.X11-unix

# Install Openbox window manager and x11-xserver-utils for window title bar, close/minimize/maximize buttons, moving, and resizing
RUN apt-get update && apt-get install -y --no-install-recommends \
    openbox \
    x11-xserver-utils \
    && rm -rf /var/lib/apt/lists/*

# Copy Openbox configuration
COPY config/rc.xml /etc/xdg/openbox/rc.xml
RUN mkdir -p /home/runner/.config/openbox \
    && cp /etc/xdg/openbox/rc.xml /home/runner/.config/openbox/rc.xml \
    && chown -R runner:runner /home/runner/.config

# Customize noVNC lite UI to eliminate curved borders and status bar
RUN echo "#noVNC_status_bar { display: none !important; } html, body { background: #161b22 !important; border-radius: 0 !important; margin: 0 !important; padding: 0 !important; overflow: hidden !important; }" >> /usr/share/novnc/app/styles/lite.css

# Inject focus helper script into vnc_lite.html to ensure canvas receives all keyboard & mouse events
RUN sed -i 's|</body>|<script>window.addEventListener("load",function(){window.addEventListener("click",function(){var c=document.querySelector("canvas");if(c){c.focus();}});window.addEventListener("keydown",function(e){var c=document.querySelector("canvas");if(c\&\&document.activeElement!==c){c.focus();}});});</script></body>|' /usr/share/novnc/vnc_lite.html

# noVNC is installed at /usr/share/novnc
# websockify is available as a command

EXPOSE 6080

USER runner
WORKDIR /workspace

ENTRYPOINT ["/opt/runtime-daemon.sh"]
