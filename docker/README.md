# Docker Setup

Cove provides two Docker images to suit different deployment needs. Both use PostgreSQL with pgvector for embedding storage and similarity search, and both include **FFmpeg with hardware acceleration support** (NVENC, VAAPI, QSV, Vulkan) via [BtbN static builds](https://github.com/BtbN/FFmpeg-Builds).

## Option 1: All-in-one (recommended for simple setups)

A single container with PostgreSQL, pgvector, FFmpeg, and Cove. Best for UnRAID, Synology, and users who want minimal configuration.

```bash
docker compose --file docker-compose.allinone.yml up --detach
```

Then open http://localhost:5073.

### Volumes

| Volume | Purpose |
|--------|---------|
| `/var/lib/postgresql/cove-data` | PostgreSQL database |
| `/data` | Reserved for managed PostgreSQL data; unused by the provided Docker configurations |
| `/config` | Cove configuration files |
| `/generated` | Thumbnails, previews, sprites |
| `/cache` | Temporary cache |
| `/backups` | Database backups |
| `/media` | User-added source media, mounted read-write by default |

## Option 2: App + PostgreSQL (recommended for docker-compose users)

Separate containers for the app and database. Easier to manage, upgrade, and back up independently. The provided compose file uses the official `pgvector/pgvector` PostgreSQL 18 image. PostgreSQL 18 images expect a single mount at `/var/lib/postgresql` (the data goes in its `18/docker` subdirectory) and refuse to start with a mount at `/var/lib/postgresql/data`, the path older PostgreSQL images used.

## File ownership

The `cove-app` image of the App + PostgreSQL setup runs Cove as the `cove` user, uid 1655 and gid 1655. Docker creates missing bind-mount directories as `root`, so make the Cove directories writable by that user before the first start:

```bash
mkdir -p cove-data/config cove-data/data cove-data/generated cove-data/backups
sudo chown -R 1655:1655 cove-data/config cove-data/data cove-data/generated cove-data/backups
```

Leave `cove-data/pgdata` to the database container, which sets its own ownership. If Cove cannot write to `/config`, it stops at startup with an error that names the uid and gid it runs as.

The all-in-one image runs Cove as `root`, so its directories need no ownership change.

```bash
docker compose up --detach
```

## Mounting your media

Both Compose files include the same commented media-volume example. Uncomment and edit it:

```yaml
volumes:
  - /path/to/your/media:/media
```

Then add `/media` as a library path in Cove's settings. The read-write mount allows source-file deletion in Cove and rename operations provided by installed extensions. Append `:ro` only when you intentionally want to prevent source-file changes; scanning still works, but deletion and extension-provided rename workflows do not.

## GPU Acceleration

FFmpeg in the Docker images supports hardware-accelerated encoding/decoding. To use it, you need to pass the GPU device into the container.

### NVIDIA GPU

1. Install [nvidia-container-toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html) on the host
2. Uncomment the `deploy` section in your compose file:

```yaml
deploy:
  resources:
    reservations:
      devices:
        - driver: nvidia
          count: 1
          capabilities: [gpu]
```

3. Set transcoding to NVENC in Cove's Settings → Transcoding

**NVIDIA driver version:** the bundled FFmpeg (BtbN's `master-latest` build, which Cove also downloads for native Windows and Linux installs) is currently built against NVIDIA Video Codec SDK 13.1, so NVENC encoding needs **host driver 610 or newer**. On older drivers, such as the 580 series many distributions package, NVENC cannot open an encode session. Cove then encodes in software and logs a warning at startup that quotes FFmpeg's required driver version. NVDEC/CUDA decoding still works on those drivers. BtbN raises the SDK version over time, so the version in that warning takes precedence over the one given here.

### Intel / AMD (VAAPI)

1. Uncomment the `devices` section in your compose file:

```yaml
devices:
  - /dev/dri:/dev/dri
```

2. Set transcoding to VAAPI in Cove's Settings → Transcoding

## Extensions

Extensions are loaded from the `/config/extensions/` directory inside the container. To install an extension:

```yaml
volumes:
  - ./my-extensions:/config/extensions:ro
```

Each extension is a subdirectory containing pre-compiled DLL files (backend) and/or pre-bundled JavaScript modules (frontend). Extensions ship with all their dependencies already included — **no NuGet restore or npm install happens at runtime**. This means:

- Container startup is fast and deterministic
- No network access needed for extension loading
- Extensions work identically in Docker and native installs

### Python for extensions

Both images ship Python in a virtual environment at `/opt/cove-python`, owned by the `cove` user and
first on `PATH`. Extensions that drive Python tooling can `pip install` into it at runtime, which the
system Python cannot do because Debian marks it externally managed.

yt-dlp does not need it: the yt-dlp downloader extension provisions one of yt-dlp's standalone
builds, which carries its own Python and the curl_cffi that `--impersonate` needs for sites that only
serve their higher resolutions to browser-shaped requests.

See the main [README](../README.md) for extension development docs.

## Environment Variables

All Cove configuration can be overridden via environment variables using the `COVE__` prefix with `__` as the section separator:

| Variable | Default | Description |
|----------|---------|-------------|
| `COVE__Port` | `5073` | HTTP port |
| `COVE__Postgres__Managed` | `false` | Use embedded PostgreSQL manager (disabled in Docker) |
| `COVE__Postgres__ConnectionString` | — | PostgreSQL connection string; the target database must have pgvector available |
| `COVE__GeneratedPath` | `/generated` | Path for thumbnails/previews |
| `COVE__CachePath` | `/cache` | Temporary cache path |
| `COVE__FfmpegPath` | auto-detected | Custom FFmpeg binary path |
| `COVE__Auth__Enabled` | `false` | Enable authentication |
| `COVE__Telemetry__Logs__Enabled` | `false` | Export logs over OTLP |
| `COVE__Telemetry__Traces__Enabled` | `false` | Export request traces over OTLP |
| `COVE__Telemetry__Metrics__Enabled` | `false` | Export metrics over OTLP |
| `COVE__Telemetry__Endpoint` | — | OTLP endpoint; overrides `OTEL_EXPORTER_OTLP_ENDPOINT` |
| `COVE__Telemetry__ServiceName` | `cove` | Service name reported to the telemetry backend |
| `COVE__TranscodeHardwareAcceleration` | `none` | Hardware accel: `none`, `nvenc`, `vaapi`, `qsv` |

## Database Migrations

Cove uses EF Core migrations to manage database schema changes. On startup:

1. **New installs**: All migrations are applied automatically
2. **Existing databases**: Schema is baselined; only new migrations apply
3. **Before any migration**: An automatic pg_dump backup is created in `/backups`

If the frontend shows a "Database Update Required" screen, simply restart the container — migrations apply on startup.

The all-in-one container enables `CREATE EXTENSION vector` before Cove starts and exits if pgvector is missing from the image.

## Building locally

From the repository root:

```bash
# All-in-one
docker build --file docker/Dockerfile --tag cove:local .

# App-only
docker build --file docker/Dockerfile.app --tag yourcove:local .
```
