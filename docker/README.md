# Docker Setup

Cove provides two Docker images to suit different deployment needs. Both include **FFmpeg with hardware acceleration support** (NVENC, VAAPI, QSV, Vulkan) via [BtbN static builds](https://github.com/BtbN/FFmpeg-Builds).

## Option 1: All-in-one (recommended for simple setups)

A single container with PostgreSQL, FFmpeg, and Cove. Best for UnRAID, Synology, and users who want minimal configuration.

```bash
docker compose -f docker-compose.allinone.yml up -d
```

Then open http://localhost:9999.

### Volumes

| Volume | Purpose |
|--------|---------|
| `/var/lib/postgresql/cove-data` | PostgreSQL database |
| `/data` | Your media library mount point |
| `/config` | Cove configuration files |
| `/generated` | Thumbnails, previews, sprites |
| `/cache` | Temporary cache |
| `/backups` | Database backups |

## Option 2: App + PostgreSQL (recommended for docker-compose users)

Separate containers for the app and database. Easier to manage, upgrade, and back up independently.

```bash
docker compose up -d
```

### Mounting your media

Uncomment and edit the media volume in the compose file:

```yaml
volumes:
  - /path/to/your/media:/media:ro
```

Then add `/media` as a library path in Cove's settings.

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

See the main [README](../README.md) for extension development docs.

## Environment Variables

All Cove configuration can be overridden via environment variables using the `COVE__` prefix with `__` as the section separator:

| Variable | Default | Description |
|----------|---------|-------------|
| `COVE__Port` | `9999` | HTTP port |
| `COVE__Postgres__Managed` | `false` | Use embedded PostgreSQL manager (disabled in Docker) |
| `COVE__Postgres__ConnectionString` | — | PostgreSQL connection string |
| `COVE__GeneratedPath` | `/generated` | Path for thumbnails/previews |
| `COVE__CachePath` | `/cache` | Temporary cache path |
| `COVE__FfmpegPath` | auto-detected | Custom FFmpeg binary path |
| `COVE__Auth__Enabled` | `false` | Enable authentication |
| `COVE__TranscodeHardwareAcceleration` | `none` | Hardware accel: `none`, `nvenc`, `vaapi`, `qsv` |

## Database Migrations

Cove uses EF Core migrations to manage database schema changes. On startup:

1. **New installs**: All migrations are applied automatically
2. **Existing databases**: Schema is baselined; only new migrations apply
3. **Before any migration**: An automatic pg_dump backup is created in `/backups`

If the frontend shows a "Database Update Required" screen, simply restart the container — migrations apply on startup.

## Building locally

From the repository root:

```bash
# All-in-one
docker build -f docker/Dockerfile -t cove:local .

# App-only
docker build -f docker/Dockerfile.app -t yourcove:local .
```
