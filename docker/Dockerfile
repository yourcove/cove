# ── Stage 1: Build frontend ────────────────────────────────────────
FROM node:22-slim AS ui-build
WORKDIR /build/ui
COPY ui/package.json ui/package-lock.json ./
RUN npm ci --ignore-scripts
COPY ui/ ./
RUN mkdir -p /build/src/Cove.Api/wwwroot
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
COPY src/Cove.Tests/Cove.Tests.csproj Cove.Tests/
RUN dotnet restore Cove.slnx

COPY src/ ./
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

# Install PostgreSQL 17 and s6-overlay for process supervision
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
        postgresql-17 \
        postgresql-17-pgvector \
        libicu72 \
        libssl3 \
    && apt-get purge -y --auto-remove gnupg lsb-release \
    && rm -rf /var/lib/apt/lists/*

# Install FFmpeg with hwaccel support (BtbN GPL static builds)
# These include NVENC, VAAPI, QSV, Vulkan — much more capable than Debian's ffmpeg
ARG TARGETARCH
RUN case "${TARGETARCH}" in \
        amd64) FFMPEG_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz" ;; \
        arm64) FFMPEG_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64-gpl.tar.xz" ;; \
        *) echo "Unsupported arch: ${TARGETARCH}" && exit 1 ;; \
    esac \
    && curl -fsSL "$FFMPEG_URL" | tar -Jx --strip-components=2 -C /usr/local/bin/ --wildcards '*/bin/ffmpeg' '*/bin/ffprobe' \
    && chmod +x /usr/local/bin/ffmpeg /usr/local/bin/ffprobe

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

ENV COVE__Host=0.0.0.0 \
    COVE__Port=9999 \
    COVE__GeneratedPath=/generated \
    COVE__CachePath=/cache \
    COVE__Postgres__Managed=false \
    COVE__Postgres__Port=5432 \
    COVE__Postgres__Database=cove \
    COVE__Postgres__ConnectionString="Host=localhost;Port=5432;Database=cove;Username=cove;Password=cove" \
    PGDATA=/var/lib/postgresql/cove-data \
    S6_KEEP_ENV=1

EXPOSE 9999
VOLUME ["/data", "/config", "/generated", "/cache", "/backups"]

ENTRYPOINT ["/init"]
