# Docker Setup

Cove provides two Docker images to suit different deployment needs. Both use PostgreSQL with pgvector for embedding storage and similarity search, and both include **FFmpeg with hardware acceleration support** (NVENC, VAAPI, QSV, Vulkan) via [BtbN static builds](https://github.com/BtbN/FFmpeg-Builds).

## Option 1: All-in-one (recommended for simple setups)

A single container with PostgreSQL, pgvector, FFmpeg, and Cove. Best for Synology and users who want minimal configuration.

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

Separate containers for the app and database. Easier to manage, upgrade, and back up independently. The provided compose file uses the official `pgvector/pgvector` PostgreSQL 18 image.

```bash
docker compose up --detach
```

### Unraid

The Unraid template for the app image is [`docker/unraid/cove.xml`](unraid/cove.xml). It runs `ghcr.io/yourcove/cove-app:latest` and expects a PostgreSQL 18 server with pgvector that you provide. Appdata defaults to `/mnt/user/appdata/cove/`.

1. Copy [`docker/unraid/cove.xml`](unraid/cove.xml) to `/boot/config/plugins/dockerMan/templates-user/my-Cove.xml` on the Unraid flash drive, **or** add `https://github.com/yourcove/cove` as a template repository under **Settings → Docker**.
2. Run PostgreSQL 18 with pgvector (for example `pgvector/pgvector:pg18`) before starting Cove.
3. Open the **Docker** tab, choose **Add Container**, and select **Cove**.
4. Set the PostgreSQL connection string. On a custom Docker network, `Host` is the database container name. On the default `bridge` network, set `Host` to your Unraid server IP and publish PostgreSQL port `5432`.
5. Set **Media** to your library share (default `/mnt/user/media`). Add extra Path mappings for additional shares.
6. Apply, then open the WebUI on port `5073` and complete first-run setup. Enter `/media` as the library path in Cove.

The app runs as uid `1000`; if it cannot write to `/config`, run `chown -R 1000:1000 /mnt/user/appdata/cove`.

Optional GPU transcoding:

- **Intel/AMD:** in Advanced View, set **VAAPI Device** to `/dev/dri`, then choose VAAPI or QSV in Cove **Settings → Transcoding**.
- **NVIDIA:** install the Nvidia-Driver plugin, add `--runtime=nvidia` to **Extra Parameters** (not `--gpus all`), set **NVIDIA Visible Devices** to `all`, then choose NVENC in Cove.

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
| `COVE__Port` | `5073` | HTTP port |
| `COVE__Postgres__Managed` | `false` | Use embedded PostgreSQL manager (disabled in Docker) |
| `COVE__Postgres__ConnectionString` | — | PostgreSQL connection string; the target database must have pgvector available |
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

The all-in-one container enables `CREATE EXTENSION vector` before Cove starts and exits if pgvector is missing from the image.

## Building locally

From the repository root:

```bash
# All-in-one
docker build --file docker/Dockerfile --tag cove:local .

# App-only
docker build --file docker/Dockerfile.app --tag yourcove:local .
```
