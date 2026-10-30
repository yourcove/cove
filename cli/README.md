# Cove CLI

Cove CLI is an experimental command-line client for Cove's REST API.

> [!WARNING]
> This is an alpha-quality tool. Expect incomplete features and breaking changes, and do not rely on it for critical workflows. It is not distributed through package managers or as a prebuilt release; build it from source.

## Build and install

Building requires [Bun](https://bun.sh/) 1.4 or newer. From the repository checkout:

```sh
cd cli
bun install --frozen-lockfile
mkdir -p ~/bin
bun build src/index.ts --compile --outfile="$HOME/bin/cove-cli"
chmod +x ~/bin/cove-cli
```

Ensure `~/bin` is on your `PATH`. For the current shell:

```sh
export PATH="$HOME/bin:$PATH"
```

Add that export to your shell configuration to make it persistent, then verify the installation:

```sh
cove-cli --version
cove-cli --help
```

## Use

The built-in help is the command reference:

```sh
cove-cli --help
cove-cli help <command>
cove-cli help <command> <subcommand>
```

For example, use `cove-cli help videos list` to see the available video filters, sorting, pagination, and output options.

Create and update a video worklist:

```sh
cove-cli videos filters add-ids "Selected videos" 42 67 91
cove-cli videos list --saved-filter "Selected videos"
cove-cli videos filters remove-ids "Selected videos" 42
```

Removing the last video ID deletes the saved filter.

Organize tags into tag groups by name or ID:

```sh
cove-cli tag-groups create Season --color "#6ee7b7"
cove-cli tags set-group Season Summer Winter
cove-cli tags clear-group Winter
cove-cli tag-groups update Season --name Seasons --description ""
cove-cli tag-groups delete Seasons --yes
```

An empty `--description` or `--color` clears it. Deleting a tag group keeps its tags and leaves them ungrouped.

Create, show, and delete groups. Commands that act on a group take its ID or its name:

```sh
cove-cli groups create "Road Trip" --description "Summer drive" --date 2026-07
cove-cli groups show "Road Trip"
cove-cli groups delete "Road Trip" --yes
```

A reference made only of digits is tried as an ID first and then as a name, except in `groups delete`, where it is always an ID. Names match exactly, including spaces; a name differing only in the case of A–Z letters is accepted when no exact match exists. Group names need not be unique, so a name shared by several groups is rejected with their IDs; use one of those IDs instead. Deleting a group keeps the videos and other records it held.

Add records to a static group by type and record ID, then reorder or remove them by their group item ID:

```sh
cove-cli groups items add "Road Trip" video 7 8 9
cove-cli groups items add 42 image 15 --first
cove-cli groups items list "Road Trip"
cove-cli groups items move 42 103 --to-position 2
cove-cli groups items remove 42 103
```

New items go at the end unless `--first` or `--to-position` is given. Every ID is checked before anything is added or removed, so an unknown ID leaves the group unchanged. Removing an item keeps the record itself in the library.

Register files that are already on the server as videos, then fill in their metadata and cover:

```sh
cove-cli videos import-file /media/videos/incoming/example.mp4 --json
cove-cli library update videos 42 --data '{"title":"Example","date":"2026-07-01"}'
cove-cli library image videos 42 --file cover.jpg
cove-cli library image performers 17 --file portrait.webp
```

`import-file` sends each path to Cove as given, so use the absolute path the server sees, which may differ from yours when Cove runs in a container. Importing a file Cove already knows returns its existing video. Several paths are imported in order and the first failure stops the run; in `--json` output the error's `details.imported` lists the paths and video IDs already imported. `library image` accepts JPEG, PNG, GIF, WebP, AVIF, and BMP files for videos, performers, studios, tags, galleries, audios, and texts.

To pick up a whole folder instead, scan just that folder and wait for the job:

```sh
job=$(cove-cli maintenance scan --path /media/videos/incoming --json | jq -r .jobId)
cove-cli jobs wait "$job"
```

Each `--path` must be an absolute server path inside a configured library path; Cove skips any other path without reporting an error, so a mistyped path yields a scan that finds nothing. A scan needs the `library.scan` permission; `import-file` needs only `videos.write`.

Find records by the ID a metadata server such as StashDB gives them:

```sh
cove-cli performers list --remote-id stashdb.org=3a1c7f0e-5b2d-4c8e-9f61-0d4b7a2e8c13 --json
cove-cli studios show --remote-id stashdb.org=3a1c7f0e-5b2d-4c8e-9f61-0d4b7a2e8c13 --json
cove-cli videos show --remote-id https://stashdb.org/graphql=3a1c7f0e-5b2d-4c8e-9f61-0d4b7a2e8c13
cove-cli tags list --remote-id 3a1c7f0e-5b2d-4c8e-9f61-0d4b7a2e8c13
```

`--remote-id` works on `videos`, `performers`, `studios`, and `tags`, in both `list` and `show`. The endpoint is the metadata server's GraphQL URL; a bare host such as `stashdb.org` stands for `https://stashdb.org/graphql`, and leaving out `endpoint=` matches the ID on any endpoint. `list` returns every match, including none. `show` fails with `PERFORMER_NOT_FOUND` (or `VIDEO_`, `STUDIO_`, `TAG_`) when nothing matches and `PERFORMER_AMBIGUOUS` with up to 10 candidate IDs when several records share the ID.

For endpoints without a dedicated command, `api` sends authenticated requests under `/api`:

```sh
cove-cli api get videos/42
cove-cli api put videos/42 --data '{"organized":true}'
cove-cli api post studios/9/image --file logo.png
cove-cli api delete videos/42/image --yes
```

`--file` sends a multipart upload in the `file` form field; use `--field` to name another field.

Authenticate interactively with a Cove server:

```sh
cove-cli auth login --server https://cove.example --username user
```

For automation, provide an API token without saving a profile:

```sh
COVE_SERVER=https://cove.example COVE_TOKEN=... cove-cli auth status --json
```

Run `cove-cli help auth login` for all authentication options. Configuration is stored in the platform configuration directory. Set `COVE_CONFIG_DIR` to override its location.

Issue API tokens for the signed-in identity, optionally scoping one below your own permissions:

```sh
cove-cli tokens create agent --scope viewer
cove-cli tokens create ci --scope videos.read,images.read --expires 2026-12-31T00:00:00Z
cove-cli tokens list
cove-cli tokens revoke agent
```

A token acts as its owner. Its effective permissions are the intersection of the owner's current permissions and the token's scope, so a scope can only remove access relative to its owner, never add it; Cove refuses to issue a token whose scope exceeds its owner. `--scope viewer` is a read-only preset, and `--scope` otherwise takes permission keys, repeated or comma-separated. The plaintext token is shown once at creation and never again.

Cove expands implied permissions, so a scope key also grants what it implies: `--scope images.delete.file` reaches `images.delete` and `images.read` as well. List read keys only when a token should stay read-only. A scope that includes a permission the current identity does not hold is reported before the request is sent; in `--json` and `--jsonl` output a preset is never narrowed silently, so name the permissions explicitly when the identity holds only part of a preset.

Install extension packages from local ZIP files or package URLs:

```sh
cove-cli extensions install ./cove.example-0.8.1-dev.20261018143000.zip --trust-unverified
cove-cli extensions install core.zip faces.zip --trust-unverified --profile personal
```

An installed extension with the same ID is replaced. Packages are installed in the order given, so list dependencies first; the first failure stops the run and names the packages already installed. Cove downloads a URL itself, so it must be reachable from the server. `--trust-unverified` is required because these packages are not verified by an extension registry.

## Develop

Run the CLI directly from the checkout:

```sh
cd cli
bun install
bun run src/index.ts --help
```

Available checks:

```sh
bun run typecheck
bun test
bun run build
bun run test:compiled
```
