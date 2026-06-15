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
COPY src/Cove.Plugins/Cove.Plugins.csproj Cove.Plugins/
COPY src/Cove.Sdk/Cove.Sdk.csproj Cove.Sdk/
COPY src/Cove.PerformanceTests/Cove.PerformanceTests.csproj Cove.PerformanceTests/
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

# ── Stage 3: App-only runtime (FFmpeg + Cove, no PostgreSQL) ──────
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# Install FFmpeg with hwaccel support (BtbN GPL static builds)
# These include NVENC, VAAPI, QSV, Vulkan — much more capable than Debian's ffmpeg
ARG TARGETARCH
# FFMPEG_MIRROR_BASE, when set (CI passes this repo's ffmpeg-payload release), is tried first; we
# fall back to BtbN's latest release API. BtbN release assets are versioned and old builds are not
# retained, so the mirror gives container builds a stable, always-available source.
ARG FFMPEG_MIRROR_BASE=""
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        xz-utils \
    && case "${TARGETARCH:-amd64}" in \
        amd64) FFMPEG_ASSET="ffmpeg-master-latest-linux64-gpl.tar.xz"; BTBN_PLATFORM="linux64" ;; \
        arm64) FFMPEG_ASSET="ffmpeg-master-latest-linuxarm64-gpl.tar.xz"; BTBN_PLATFORM="linuxarm64" ;; \
        *) echo "Unsupported arch: ${TARGETARCH}" && exit 1 ;; \
    esac \
    && BTBN_VARIANT="gpl" \
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
    && tar -Jx --strip-components=2 -C /usr/local/bin/ --wildcards '*/bin/ffmpeg' '*/bin/ffprobe' -f /tmp/ffmpeg.tar.xz \
    && rm -f /tmp/ffmpeg.tar.xz /tmp/btbn-release.json \
    && chmod +x /usr/local/bin/ffmpeg /usr/local/bin/ffprobe \
    && apt-get purge -y --auto-remove xz-utils \
    && rm -rf /var/lib/apt/lists/*

# Vendor-neutral GPU acceleration loaders only (a few MB). ffmpeg's hwaccel paths
# dlopen libva-drm.so.2 and libvulkan; if absent the process hard-aborts at startup
# (e.g. "libva-drm.so.2: cannot open shared object file" on Intel Arc). Installing the
# dispatch loaders prevents that crash and lets hwaccel fail soft when no driver is present.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libva2 \
        libva-drm2 \
        libvulkan1 \
    && rm -rf /var/lib/apt/lists/*

# Optional vendor GPU drivers, off by default so non-Intel/CPU-only users aren't burdened.
# Build with --build-arg COVE_GPU_VENDOR=intel (Arc / recent iGPUs) or =amd (Mesa).
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

# PostgreSQL client tools (pg_dump / pg_restore / psql). The app-only image talks to an
# external PostgreSQL container, but the backup/restore feature — including the mandatory
# pre-migration backup — shells out to these client binaries, so they must be present here
# too (the all-in-one image gets them for free via the bundled postgresql-17 server). Pin
# to the newest client from pgdg (currently v18). pg_dump must be >= the server version it
# dumps, and the external Postgres can be any version, so we track the latest stable client
# rather than the v17 the all-in-one image bundles. Bump when a newer Postgres major ships.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl gnupg lsb-release \
    && echo "deb http://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc | gpg --dearmor -o /etc/apt/trusted.gpg.d/pgdg.gpg \
    && apt-get update && apt-get install -y --no-install-recommends \
        postgresql-client-18 \
    && apt-get purge -y --auto-remove gnupg lsb-release \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -m -s /bin/bash cove

COPY --from=api-build /app /opt/cove

RUN mkdir -p /data /config /generated /cache /backups \
    && chown -R cove:cove /data /config /generated /cache /backups /opt/cove

USER cove
WORKDIR /opt/cove

# COVE_HOME points the data root (cove-config.json + installed extensions + app state) at the
# /config bind mount so it survives container removal / `docker compose down -v`. Backups go to
# the dedicated /backups mount.
ENV COVE_HOME=/config \
    COVE__Host=0.0.0.0 \
    COVE__Port=5073 \
    COVE__GeneratedPath=/generated \
    COVE__CachePath=/cache \
    COVE__BackupPath=/backups \
    COVE__Postgres__Managed=false

EXPOSE 5073
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD curl -fsS http://localhost:5073/health || exit 1
VOLUME ["/data", "/config", "/generated", "/cache", "/backups"]

ENTRYPOINT ["dotnet", "Cove.dll"]
