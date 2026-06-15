# ── Stage 1: Build frontend ────────────────────────────────────────
FROM node:22-slim AS ui-build
WORKDIR /build/ui
COPY ui/package.json ui/package-lock.json ./
RUN npm ci --ignore-scripts
COPY ui/ ./
RUN mkdir -p /build/src/Cove.Api/wwwroot
# Changelog lives at repo root and is imported by the UI build (src/data/changelog.ts).
COPY CHANGELOG.md /build/CHANGELOG.md
RUN npm run build

# ── Stage 2: Build backend ────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
ARG VERSION=0.0.0
WORKDIR /build/src
COPY src/Cove.slnx ./
COPY src/Cove.Api/Cove.Api.csproj Cove.Api/
COPY src/Cove.Core/Cove.Core.csproj Cove.Core/
COPY src/Cove.Data/Cove.Data.csproj Cove.Data/
COPY src/Cove.PerformanceTests/Cove.PerformanceTests.csproj Cove.PerformanceTests/
COPY src/Cove.Plugins/Cove.Plugins.csproj Cove.Plugins/
COPY src/Cove.Sdk/Cove.Sdk.csproj Cove.Sdk/
COPY src/Cove.Tests/Cove.Tests.csproj Cove.Tests/
RUN dotnet restore Cove.slnx

COPY src/ ./
# Cove.Api.csproj sets <ApplicationIcon>..\..\coveicon.ico</ApplicationIcon>, which resolves to the
# repo root (/build/coveicon.ico from /build/src/Cove.Api). It lives outside src/, so copy it in
# explicitly or the Release publish fails with CS7064 (Could not find file '/build/coveicon.ico').
COPY coveicon.ico /build/coveicon.ico
COPY --from=ui-build /build/src/Cove.Api/wwwroot/ Cove.Api/wwwroot/
RUN dotnet publish Cove.Api/Cove.Api.csproj \
    -c Release \
    -o /app \
    --no-restore \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -p:Version=${VERSION}

# ── Stage 3: All-in-one runtime (PostgreSQL + FFmpeg + Cove) ──────
FROM debian:bookworm-slim AS runtime

# Avoid interactive prompts during package installation
ENV DEBIAN_FRONTEND=noninteractive

# Install PostgreSQL 18 and s6-overlay for process supervision
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        gnupg \
        lsb-release \
        xz-utils \
    && echo "deb http://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc | gpg --dearmor -o /etc/apt/trusted.gpg.d/pgdg.gpg \
    && apt-get update && apt-get install -y --no-install-recommends \
        postgresql-18 \
        postgresql-18-pgvector \
        libicu72 \
        libssl3 \
    && apt-get purge -y --auto-remove gnupg lsb-release \
    && rm -rf /var/lib/apt/lists/*

# Vendor-neutral GPU acceleration *loaders* only (a few MB). ffmpeg's hwaccel paths
# dlopen libva-drm.so.2 and libvulkan; if they're absent the process hard-aborts at
# startup (e.g. "libva-drm.so.2: cannot open shared object file", "Unable to open the
# libvulkan library!"). Installing just the dispatch loaders prevents that crash and
# lets hwaccel fail soft when no driver is present — without bloating the image with a
# vendor-specific driver that only helps some users.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libva2 \
        libva-drm2 \
        libvulkan1 \
    && rm -rf /var/lib/apt/lists/*

# Optional vendor GPU drivers, off by default so non-Intel/CPU-only users aren't
# burdened. Build with --build-arg COVE_GPU_VENDOR=intel (iHD/oneVPL, for Arc & recent
# iGPUs) or =amd (Mesa radeonsi/RADV). The driver is what actually enables acceleration;
# the loaders above are enough to avoid the startup crash.
ARG COVE_GPU_VENDOR=none
RUN if [ "$COVE_GPU_VENDOR" = "intel" ]; then \
        ( [ -f /etc/apt/sources.list.d/debian.sources ] \
            && sed -i 's/^Components: main.*$/Components: main contrib non-free non-free-firmware/' /etc/apt/sources.list.d/debian.sources \
            || sed -i 's/ main$/ main contrib non-free non-free-firmware/' /etc/apt/sources.list ) \
        && apt-get update && apt-get install -y --no-install-recommends \
            intel-media-va-driver-non-free libmfx-gen1.2 mesa-vulkan-drivers vainfo \
        && rm -rf /var/lib/apt/lists/* ; \
    elif [ "$COVE_GPU_VENDOR" = "amd" ]; then \
        apt-get update && apt-get install -y --no-install-recommends \
            mesa-va-drivers mesa-vulkan-drivers vainfo \
        && rm -rf /var/lib/apt/lists/* ; \
    fi

# Install FFmpeg with hwaccel support (BtbN GPL-SHARED builds)
# The gpl-shared variant includes the FFmpeg shared libraries (.so files) that
# FFmpeg.AutoGen's DynamicallyLoadedBindings needs to dlopen for in-process
# frame extraction (used by phash generation and sprite sheets).
# Shared libs are symlinked into /usr/local/bin so that DynamicallyLoadedBindings
# (which sets LibrariesPath to the directory of the ffmpeg binary) can find them.
ARG TARGETARCH
# FFMPEG_MIRROR_BASE, when set (CI passes this repo's ffmpeg-payload release), is tried first; we
# fall back to BtbN's latest release API. BtbN release assets are versioned and old builds are not
# retained, so the mirror gives container builds a stable, always-available source.
ARG FFMPEG_MIRROR_BASE=""
RUN case "${TARGETARCH}" in \
        amd64) FFMPEG_ASSET="ffmpeg-master-latest-linux64-gpl-shared.tar.xz"; BTBN_PLATFORM="linux64" ;; \
        arm64) FFMPEG_ASSET="ffmpeg-master-latest-linuxarm64-gpl-shared.tar.xz"; BTBN_PLATFORM="linuxarm64" ;; \
        *) echo "Unsupported arch: ${TARGETARCH}" && exit 1 ;; \
    esac \
    && BTBN_VARIANT="gpl-shared" \
    && if [ -n "${FFMPEG_MIRROR_BASE}" ]; then \
           echo "Fetching ffmpeg from mirror: ${FFMPEG_MIRROR_BASE}/${FFMPEG_ASSET}"; \
           curl -fL --retry 3 --retry-all-errors --retry-delay 5 -o /tmp/ffmpeg.tar.xz "${FFMPEG_MIRROR_BASE}/${FFMPEG_ASSET}" || rm -f /tmp/ffmpeg.tar.xz; \
       fi \
    && if [ ! -s /tmp/ffmpeg.tar.xz ]; then \
           echo "Resolving ffmpeg from BtbN latest release API: ${BTBN_PLATFORM}/${BTBN_VARIANT}"; \
           curl -fsSL --retry 5 --retry-all-errors --retry-delay 5 -o /tmp/btbn-release.json "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest"; \
           BTBN_URL=$(grep -o '"browser_download_url": "[^"]*"' /tmp/btbn-release.json \
             | sed 's/.*"browser_download_url": "\([^"]*\)".*/\1/' \
             | grep -E "/${FFMPEG_ASSET}$|/ffmpeg-N-[^/]*-${BTBN_PLATFORM}-${BTBN_VARIANT}\.tar\.xz$" \
             | head -n 1); \
           if [ -z "${BTBN_URL}" ]; then echo "Unable to find BtbN asset for ${BTBN_PLATFORM}/${BTBN_VARIANT}" && exit 1; fi; \
           echo "Fetching ffmpeg from BtbN: ${BTBN_URL}"; \
           curl -fL --retry 5 --retry-all-errors --retry-delay 5 -o /tmp/ffmpeg.tar.xz "${BTBN_URL}"; \
       fi \
    && mkdir -p /tmp/ffmpeg-extract \
    && tar -Jx -C /tmp/ffmpeg-extract -f /tmp/ffmpeg.tar.xz \
    && FFMPEG_DIR=$(ls -d /tmp/ffmpeg-extract/ffmpeg-*) \
    && cp "$FFMPEG_DIR/bin/ffmpeg" "$FFMPEG_DIR/bin/ffprobe" /usr/local/bin/ \
    && chmod +x /usr/local/bin/ffmpeg /usr/local/bin/ffprobe \
    && mkdir -p /usr/local/lib/ffmpeg \
    && cp "$FFMPEG_DIR/lib/"*.so* /usr/local/lib/ffmpeg/ \
    && find /usr/local/lib/ffmpeg -name "*.so*" -exec ln -sf {} /usr/local/bin/ \; \
    && echo "/usr/local/lib/ffmpeg" > /etc/ld.so.conf.d/ffmpeg.conf \
    && ldconfig \
    && rm -rf /tmp/ffmpeg-extract /tmp/ffmpeg.tar.xz /tmp/btbn-release.json


# Install s6-overlay for multi-process supervision
ARG S6_OVERLAY_VERSION=3.2.0.2
ARG TARGETARCH
RUN case "${TARGETARCH}" in \
        amd64) S6_ARCH="x86_64" ;; \
        arm64) S6_ARCH="aarch64" ;; \
        *) echo "Unsupported arch: ${TARGETARCH}" && exit 1 ;; \
    esac \
    && curl -fsSL "https://github.com/just-containers/s6-overlay/releases/download/v${S6_OVERLAY_VERSION}/s6-overlay-noarch.tar.xz" \
        | tar -Jxp -C / \
    && curl -fsSL "https://github.com/just-containers/s6-overlay/releases/download/v${S6_OVERLAY_VERSION}/s6-overlay-${S6_ARCH}.tar.xz" \
        | tar -Jxp -C /

# Install ASP.NET Core runtime
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0 /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet

# Create cove user
RUN useradd -m -s /bin/bash cove

# ── s6 service: PostgreSQL ─────────────────────────────────────────
RUN mkdir -p /etc/s6-overlay/s6-rc.d/postgres/dependencies.d \
    && touch /etc/s6-overlay/s6-rc.d/postgres/dependencies.d/base \
    && echo "longrun" > /etc/s6-overlay/s6-rc.d/postgres/type

COPY docker/s6/postgres-run.sh /etc/s6-overlay/s6-rc.d/postgres/run
RUN chmod +x /etc/s6-overlay/s6-rc.d/postgres/run

# ── s6 service: Cove ───────────────────────────────────────────────
RUN mkdir -p /etc/s6-overlay/s6-rc.d/cove/dependencies.d \
    && touch /etc/s6-overlay/s6-rc.d/cove/dependencies.d/postgres \
    && echo "longrun" > /etc/s6-overlay/s6-rc.d/cove/type

COPY docker/s6/cove-run.sh /etc/s6-overlay/s6-rc.d/cove/run
RUN chmod +x /etc/s6-overlay/s6-rc.d/cove/run

# Register both services in the default bundle
RUN mkdir -p /etc/s6-overlay/s6-rc.d/user/contents.d \
    && touch /etc/s6-overlay/s6-rc.d/user/contents.d/postgres \
    && touch /etc/s6-overlay/s6-rc.d/user/contents.d/cove

# ── Cove application ──────────────────────────────────────────────
COPY --from=api-build /app /opt/cove

# Data directories
RUN mkdir -p /data /config /generated /cache /backups \
    && chown -R cove:cove /data /config /generated /cache /backups

# PostgreSQL data
RUN mkdir -p /var/lib/postgresql/cove-data \
    && chown -R postgres:postgres /var/lib/postgresql/cove-data

# COVE_HOME points the data root (cove-config.json + installed extensions + app state) at the
# /config bind mount so it survives container removal / `docker compose down -v`. Backups go to the
# dedicated /backups mount. Generated assets and cache use their own mounts.
ENV COVE_HOME=/config \
    COVE__Host=0.0.0.0 \
    COVE__Port=5073 \
    COVE__GeneratedPath=/generated \
    COVE__CachePath=/cache \
    COVE__BackupPath=/backups \
    COVE__Postgres__Managed=false \
    COVE__Postgres__Port=5432 \
    COVE__Postgres__Database=cove \
    COVE__Postgres__ConnectionString="Host=localhost;Port=5432;Database=cove;Username=cove;Password=cove" \
    PGDATA=/var/lib/postgresql/cove-data \
    S6_KEEP_ENV=1

EXPOSE 5073
VOLUME ["/data", "/config", "/generated", "/cache", "/backups"]

ENTRYPOINT ["/init"]
