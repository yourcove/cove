# Cove

A modern media organizer and metadata manager built with .NET 10 (backend) and React 19.2 / TypeScript (frontend).

## Installation

### Docker (recommended)

The simplest way to run Cove. See [docker/README.md](docker/README.md) for full options.

```bash
# All-in-one (includes PostgreSQL + FFmpeg with hwaccel)
cd docker
docker compose -f docker-compose.allinone.yml up -d

# Or: app + separate PostgreSQL
docker compose up -d
```

Open http://localhost:9999. That's it.

### Native (Windows / macOS / Linux)

Download the latest release from [Releases](../../releases) and run it. On first launch, Cove automatically:

- **Downloads PostgreSQL** portable binaries (Windows/macOS via EDB, Linux via PGDG)
- **Downloads FFmpeg** with hardware acceleration (BtbN GPL builds)
- **Initializes the database** and applies all migrations
- **Starts everything** — no manual setup required

Or run from source (see [Development](#development) below).

## Prerequisites (development only)

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 22+](https://nodejs.org/) (LTS recommended)
- PostgreSQL 18 — auto-downloaded on first run, or bring your own

FFmpeg is auto-downloaded if not on PATH.

## Project Structure

```
src/                            # .NET backend
├── Cove.slnx                   # Solution file
├── Cove.Api/                   # ASP.NET Core web API + SignalR
├── Cove.Core/                  # Domain models, interfaces, DTOs
├── Cove.Data/                  # EF Core + PostgreSQL, migrations
├── Cove.Plugins/               # Extension system
└── Cove.Tests/                 # Unit & integration tests
ui/                             # React frontend (Vite + Tailwind v4)
docker/                         # Dockerfiles, compose files, s6 scripts
.github/workflows/              # CI + release pipelines
docs/                           # Architecture docs
```

## Development

### Quick Start

```bash
# 1. Build the frontend
cd ui
npm install
npm run build          # outputs to src/Cove.Api/wwwroot/

# 2. Run the backend
cd ../src
dotnet run --project Cove.Api
```

### Dev mode (hot reload)

```bash
# Terminal 1: frontend dev server
cd ui
npm run dev

# Terminal 2: backend
cd src
dotnet run --project Cove.Api
```

### Running Tests

```bash
# Backend
cd src
dotnet test

# Frontend
cd ui
npm test
```

## Building Release Artifacts

### Native executables

Self-contained single-file executables for each platform:

```bash
# Build the frontend first
cd ui && npm ci && npm run build && cd ..

# Windows x64
dotnet publish src/Cove.Api/Cove.Api.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64

# Linux x64
dotnet publish src/Cove.Api/Cove.Api.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64

# macOS ARM64
dotnet publish src/Cove.Api/Cove.Api.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o publish/osx-arm64
```

Each produces a single executable (~50-80MB) with the .NET runtime, backend, and frontend bundled. Users just download and run — no .NET SDK or Node.js required.

### Docker images

```bash
# All-in-one (PostgreSQL + FFmpeg + Cove)
docker build -f docker/Dockerfile -t cove:local .

# App-only (requires external PostgreSQL)
docker build -f docker/Dockerfile.app -t yourcove:local .
```

Both images use BtbN FFmpeg static builds with full hardware acceleration support.

### GitHub Actions

Releases are automated via tag-triggered CI:

1. Push a tag: `git tag v1.0.0 && git push --tags`
2. The [release workflow](.github/workflows/release.yml) builds:
   - Native executables for Windows, Linux, macOS (with SHA-256 checksums)
   - Docker images pushed to `ghcr.io` (multi-arch: amd64 + arm64)
   - GitHub Release with all artifacts

**Version tagging conventions:**
- `v1.0.0` — Stable release (Docker images published, GitHub Release)
- `v1.0.0a1` — Alpha pre-release (GitHub Release only, no Docker push)
- `v1.0.0b2` — Beta pre-release (GitHub Release only, no Docker push)

## Database Migrations

Cove uses EF Core migrations for schema management. The migration flow is fully automatic:

1. **New installs**: All migrations are applied on first startup
2. **Existing databases** (pre-migration): Automatically baselined — the initial migration is marked as applied
3. **Upgrades**: Pending migrations are detected and applied on startup
4. **Backup**: A `pg_dump` backup is created automatically before applying any migration
5. **UI gate**: The frontend shows a "Database Update Required" screen if migrations are pending (restart to apply)

### Adding a new migration (development)

```bash
cd src
dotnet ef migrations add MyMigrationName --project Cove.Data --startup-project Cove.Api --output-dir Migrations
```

## Extensions

Cove has a full extension system for adding new capabilities. Extensions can:

- Add new pages, tabs, and UI slots (frontend)
- Register custom API endpoints (backend)
- Subscribe to entity lifecycle events
- Run background jobs
- Store persistent key-value data
- Override built-in pages and dialogs
- Contribute themes

### Extension packaging

Extensions are **pre-compiled and self-contained** — no runtime package installation:

| Component | Format | Dependencies |
|-----------|--------|--------------|
| Backend (.NET) | Pre-compiled DLLs in a subdirectory | All NuGet packages bundled in the DLL output |
| Frontend (JS) | Pre-bundled ESM module (single .js file) | All npm packages bundled by the build tool |

This means:
- **No NuGet restore or npm install at runtime** — startup is fast and deterministic
- **No network access required** for extension loading
- **Works identically** in Docker and native installs
- Extensions are loaded from `%LOCALAPPDATA%/cove/extensions/` (native) or `/config/extensions/` (Docker)

### Building an extension

```bash
# Backend: compile your extension project
dotnet publish MyExtension/MyExtension.csproj -c Release -o output/my-extension

# Frontend: bundle with vite/esbuild/rollup to a single ESM module
npm run build  # outputs my-extension.js

# Deploy: copy the output directory to cove's extensions folder
# Native: %LOCALAPPDATA%/cove/extensions/my-extension/
# Docker: mount as a volume to /config/extensions/my-extension/
```

## What Gets Auto-Installed

| Component | Native | Docker |
|-----------|--------|--------|
| PostgreSQL | Auto-downloaded on first run (EDB portable) | Built into image (PGDG apt) |
| FFmpeg | Auto-downloaded on first run (BtbN static build with hwaccel) | Built into image (BtbN static build with hwaccel) |
| .NET Runtime | Bundled in single-file executable | Bundled in image |
| Frontend | Bundled in executable/image | Bundled in image |

Users download one file (native) or pull one image (Docker) and everything works.

## Technology Stack

| Layer    | Technology                                     |
|----------|-------------------------------------------------|
| Backend  | .NET 10, ASP.NET Core, EF Core, SignalR        |
| Frontend | React 19.2, TypeScript, Vite 6, Tailwind CSS 4 |
| Database | PostgreSQL 18 (auto-managed or external)       |
| Media    | FFmpeg with hwaccel (NVENC, VAAPI, QSV, Vulkan)|
| Testing  | xUnit (.NET), Vitest (frontend)                |
| CI/CD    | GitHub Actions, Docker (ghcr.io)               |

## Disclaimer

Cove was inspired in part by Stash and includes compatibility features for importing data from existing Stash libraries.

Cove is an independent implementation and is not a fork of Stash. No Stash source code is included in this repository or distributed as part of Cove.
