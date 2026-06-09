# Cove

Cove is a local-first media organizer for people who want more than a simple folder browser.

It helps you scan media, search it, tag it, browse it in different ways, and keep growing the library without outgrowing the app. Cove is meant to feel approachable when you first install it and much deeper once your library gets larger, more detailed, or shared with other people on your network.

## What Cove does

- Keep videos, images, galleries, audio, and text together in one library.
- Search by title, tag, performer, studio, group, path, and more.
- Go beyond flat tagging with performer occurrence tagging, tag groups, segments, sub-scenes, compilations, and dynamic groups.
- Browse in grids, lists, feed pages, or vertical pages depending on how you want to explore.
- Pick up where you left off with watch later, watch history, and continue watching.
- Share within your network with users, roles, permissions, content rules, and share links.
- Extend Cove with downloaders, scrapers, themes, and full extensions.

## Who Cove is for

Cove works for you whether you're:

- Setting up your first local library and want something easier to use than a pile of folders.
- Or your current setup has become too limited and you want richer organization, better search, more browsing options, and a deeper extension system.

## Get started

### Native app

Download the latest release for Windows, macOS, or Linux:

- [Latest release](https://github.com/yourcove/cove/releases/latest)
- [All releases](https://github.com/yourcove/cove/releases)

The native app handles first-run setup for you.

Native releases also include the Cove Instance Manager, which lets you create, start, stop, and switch between separate local Cove libraries from one place.

### Docker all-in-one

This is the easiest container setup and the best place to start for most docker installs.
Sample docker compose files are in the docker folder.

```bash
cd docker
docker compose -f docker-compose.allinone.yml up -d
```

Then open `http://localhost:5073`.

### Docker Compose

If you prefer to keep the app and database separate:

```bash
cd docker
docker compose up -d
```

For more Docker-specific details, volumes, GPU passthrough, and environment variables, see [docker/README.md](docker/README.md).

## After first start
1. Add one or more media folders.
2. Run the first scan.
3. Start searching, tagging, browsing, and organizing.
4. Add downloaders, scrapers, or other extensions when you want Cove to do more.

## What makes Cove different

- It is not limited to one media type.
- It can organize around real metadata instead of only filenames and folders.
- It supports deeper structure inside videos through segments, sub-videos, and compilations.
- It offers social media-style browsing through feed and vertical pages without giving up a proper library model.
- It treats extensions as a core part of the app, not a tiny afterthought.
- It is built for local control first, with deliberate sharing inside your network when you want it.

## Extensions

Cove has a full extension system for adding new capabilities.

Extensions can add things like:

- downloaders
- scrapers
- themes
- settings panels
- pages and UI areas
- API endpoints
- background jobs
- nearly anything else you can imagine

If you want to build your own, check out the extension template repos:
https://github.com/yourcove/single-extension-repo-template 
https://github.com/yourcove/multi-extension-repo-template

## Run from source

If you want to work on Cove itself, you will need:

- .NET 10 SDK
- Node.js 22+

Run Cove in development mode like this:

```bash
# Terminal 1
cd ui
npm install
npm run dev

# Terminal 2
cd src
dotnet run --project Cove.Api
```

If you want a production-style frontend build first:

```bash
cd ui
npm install
npm run build

cd ../src
dotnet run --project Cove.Api
```

## Repo at a glance

- `src/` - .NET backend, data layer, plugins, SDK, and tests
- `ui/` - React frontend
- `docker/` - Dockerfiles and compose setups
- `docs/` - internal notes, guides, and project documentation

## Notes

Cove is an independent project. Some compatibility and import tooling exists for people bringing over data from stash.
