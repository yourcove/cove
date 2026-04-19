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

# ── Stage 3: App-only runtime (FFmpeg + Cove, no PostgreSQL) ──────
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# Install FFmpeg with hwaccel support (BtbN GPL static builds)
# These include NVENC, VAAPI, QSV, Vulkan — much more capable than Debian's ffmpeg
ARG TARGETARCH
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        xz-utils \
    && case "${TARGETARCH:-amd64}" in \
        amd64) FFMPEG_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz" ;; \
        arm64) FFMPEG_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64-gpl.tar.xz" ;; \
        *) echo "Unsupported arch: ${TARGETARCH}" && exit 1 ;; \
    esac \
    && curl -fsSL "$FFMPEG_URL" | tar -Jx --strip-components=2 -C /usr/local/bin/ --wildcards '*/bin/ffmpeg' '*/bin/ffprobe' \
    && chmod +x /usr/local/bin/ffmpeg /usr/local/bin/ffprobe \
    && apt-get purge -y --auto-remove curl xz-utils \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -m -s /bin/bash cove

COPY --from=api-build /app /opt/cove

RUN mkdir -p /data /config /generated /cache /backups \
    && chown -R cove:cove /data /config /generated /cache /backups /opt/cove

USER cove
WORKDIR /opt/cove

ENV COVE__Host=0.0.0.0 \
    COVE__Port=9999 \
    COVE__GeneratedPath=/generated \
    COVE__CachePath=/cache \
    COVE__Postgres__Managed=false

EXPOSE 9999
VOLUME ["/data", "/config", "/generated", "/cache", "/backups"]

ENTRYPOINT ["dotnet", "Cove.Api.dll"]
