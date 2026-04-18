# Cove

A modern media organizer and metadata manager built with .NET 10 (backend) and React 19.2 / TypeScript (frontend).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 22+](https://nodejs.org/) (LTS recommended)
- [FFmpeg](https://ffmpeg.org/download.html) — must be on PATH
- PostgreSQL 18.3 — **automatically downloaded** on first run (Windows/macOS via EDB, Linux via PGDG packages)

## Project Structure

```
src/                            # .NET backend
├── Cove.slnx                   # Solution file
├── Cove.Api/                   # ASP.NET Core web API + SignalR
├── Cove.Core/                  # Domain models, interfaces, services
├── Cove.Data/                  # EF Core + PostgreSQL, managed PG service
├── Cove.Media/                 # Media processing
├── Cove.Plugins/               # Plugin system
└── Cove.Tests/                 # Unit & integration tests
ui/                             # React frontend (Vite + Tailwind v4)
benchmarks/                     # Performance benchmarks (CPU vs HW accel)
```

## Quick Start

### 1. Build the frontend

```bash
cd ui
npm install
npm run build
```

This outputs production assets to `src/Cove.Api/wwwroot/`.

### 2. Build and run the backend

```bash
cd src
dotnet build Cove.slnx
dotnet run --project Cove.Api
```

The app starts on `https://localhost:5001` (or the configured port). On first run, it will:
- Download PostgreSQL 18.3 portable binaries (if not already present)
- Initialize the database
- Start the managed PostgreSQL instance

### 3. Development mode

Run the frontend dev server with hot reload:

```bash
cd ui
npm run dev
```

Then run the backend separately:

```bash
cd src
dotnet run --project Cove.Api
```

## Running Tests

```bash
# Backend tests
cd src
dotnet test

# Frontend tests
cd ui
npm test
```

## Benchmarks

The `benchmarks/generate_benchmark` project tests CPU vs hardware-accelerated frame extraction:

```bash
cd benchmarks/generate_benchmark
dotnet run -c Release -- /path/to/video.mp4
```

Tests all available hardware accelerators (VAAPI, D3D11VA, DXVA2, CUDA, QSV, Vulkan) and compares against CPU decoding.

## Technology Stack

| Layer    | Technology                                     |
|----------|-------------------------------------------------|
| Backend  | .NET 10, ASP.NET Core, EF Core, SignalR        |
| Frontend | React 19.2, TypeScript, Vite 6, Tailwind CSS 4 |
| Database | PostgreSQL 18.3 (managed/embedded)             |
| Media    | FFmpeg (in-process via FFmpeg.AutoGen)          |
| Testing  | xUnit (.NET), Vitest (frontend)                |
