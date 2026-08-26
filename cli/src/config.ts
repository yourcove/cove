import { chmod, mkdir, open, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { CliError } from "./errors";
import type { CoveCliConfig, StoredProfile } from "./types";

const EMPTY_CONFIG: CoveCliConfig = { version: 1, profiles: {} };

export function configDirectory(env: NodeJS.ProcessEnv = process.env): string {
  if (env.COVE_CONFIG_DIR) return env.COVE_CONFIG_DIR;
  if (process.platform === "win32") return join(env.APPDATA ?? join(homedir(), "AppData", "Roaming"), "Cove");
  if (process.platform === "darwin") return join(homedir(), "Library", "Application Support", "Cove");
  return join(env.XDG_CONFIG_HOME ?? join(homedir(), ".config"), "cove");
}

function isConfig(value: unknown): value is CoveCliConfig {
  if (!value || typeof value !== "object") return false;
  const candidate = value as Partial<CoveCliConfig>;
  return candidate.version === 1 && !!candidate.profiles && typeof candidate.profiles === "object";
}

function cloneEmpty(): CoveCliConfig {
  return { ...EMPTY_CONFIG, profiles: {} };
}

export class ConfigStore {
  readonly path: string;
  private readonly lockPath: string;

  constructor(directory = configDirectory()) {
    this.path = join(directory, "cli.json");
    this.lockPath = `${this.path}.lock`;
  }

  async load(): Promise<CoveCliConfig> {
    try {
      const parsed: unknown = JSON.parse(await readFile(this.path, "utf8"));
      if (!isConfig(parsed)) throw new Error("unsupported configuration shape");
      return parsed;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return cloneEmpty();
      if (error instanceof SyntaxError || error instanceof Error && error.message === "unsupported configuration shape") {
        throw new CliError("INVALID_CONFIG", `The Cove CLI configuration at ${this.path} is invalid.`);
      }
      throw error;
    }
  }

  async save(config: CoveCliConfig): Promise<void> {
    await mkdir(dirname(this.path), { recursive: true, mode: 0o700 });
    const temporary = `${this.path}.${process.pid}.${crypto.randomUUID()}.tmp`;
    await writeFile(temporary, `${JSON.stringify(config, null, 2)}\n`, { mode: 0o600 });
    if (process.platform !== "win32") await chmod(temporary, 0o600);
    await rename(temporary, this.path);
  }

  async update(mutator: (config: CoveCliConfig) => void | Promise<void>): Promise<CoveCliConfig> {
    return this.withLock(async () => {
      const config = await this.load();
      await mutator(config);
      await this.save(config);
      return config;
    });
  }

  async withLock<T>(callback: () => Promise<T>): Promise<T> {
    await mkdir(dirname(this.path), { recursive: true, mode: 0o700 });
    const started = Date.now();
    const owner = `${process.pid}:${crypto.randomUUID()}\n`;
    while (true) {
      try {
        const handle = await open(this.lockPath, "wx", 0o600);
        await handle.writeFile(owner);
        await handle.close();
        break;
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== "EEXIST") throw error;
        try {
          const info = await stat(this.lockPath);
          if (Date.now() - info.mtimeMs > 60_000) {
            await rm(this.lockPath, { force: true });
            continue;
          }
        } catch (statError) {
          if ((statError as NodeJS.ErrnoException).code === "ENOENT") continue;
          throw statError;
        }
        if (Date.now() - started > 10_000) {
          throw new CliError("CONFIG_LOCK_TIMEOUT", "Another Cove CLI process is still updating the profile configuration.");
        }
        await Bun.sleep(50);
      }
    }
    try {
      return await callback();
    } finally {
      try {
        if (await readFile(this.lockPath, "utf8") === owner) await rm(this.lockPath, { force: true });
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
      }
    }
  }
}

export function selectedProfileName(config: CoveCliConfig, explicit?: string): string {
  return explicit ?? process.env.COVE_PROFILE ?? config.defaultProfile ?? "default";
}

export function normalizeServer(server: string): string {
  let url: URL;
  try {
    url = new URL(server);
  } catch {
    throw new CliError("INVALID_SERVER", "The Cove server must be an absolute HTTP or HTTPS URL.");
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new CliError("INVALID_SERVER", "The Cove server must use HTTP or HTTPS.");
  }
  if (url.username || url.password) {
    throw new CliError("INVALID_SERVER", "The Cove server URL must not contain a username or password.");
  }
  url.search = "";
  url.hash = "";
  return url.toString().replace(/\/$/, "");
}

export function resolveProfile(
  config: CoveCliConfig,
  options: { profile?: string; server?: string; token?: string },
): { name: string; profile: StoredProfile; transientCredential: boolean } {
  const name = selectedProfileName(config, options.profile);
  const stored = config.profiles[name];
  const serverValue = options.server ?? process.env.COVE_SERVER ?? stored?.server;
  if (!serverValue) {
    throw new CliError("SERVER_REQUIRED", "No Cove server is configured. Run `cove-cli auth login --server <url>` first.");
  }
  const server = normalizeServer(serverValue);
  const token = options.token ?? process.env.COVE_TOKEN;
  const storedServer = stored ? normalizeServer(stored.server) : undefined;
  return {
    name,
    profile: {
      server,
      credential: token ? { type: "apiToken", token } : storedServer === server ? stored?.credential : undefined,
    },
    transientCredential: !!token,
  };
}
