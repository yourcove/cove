# Cove CLI

Cove CLI is an experimental command-line client for Cove's REST API.

> [!WARNING]
> This is an alpha-quality tool. Expect incomplete features and breaking changes, and do not rely on it for critical workflows. It is not distributed through package managers or as a prebuilt release; build it from source.

## Build and install

Building requires [Bun](https://bun.sh/) 1.3 or newer. From the repository checkout:

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

Authenticate interactively with a Cove server:

```sh
cove-cli auth login --server https://cove.example --username user
```

For automation, provide an API token without saving a profile:

```sh
COVE_SERVER=https://cove.example COVE_TOKEN=... cove-cli auth status --json
```

Run `cove-cli help auth login` for all authentication options. Configuration is stored in the platform configuration directory. Set `COVE_CONFIG_DIR` to override its location.

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
