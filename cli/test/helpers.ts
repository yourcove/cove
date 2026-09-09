import { afterEach } from "bun:test";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

const cliRoot = join(import.meta.dir, "..");

export function json(value: unknown, status = 200): Response {
  return Response.json(value, { status });
}

export function startServer(handler: (request: Request) => Response | Promise<Response>): { server: Bun.Server<unknown>; url: string } {
  const server = Bun.serve({ port: 0, fetch: handler });
  return { server, url: `http://127.0.0.1:${server.port}` };
}

export function page<T>(items: T[], totalCount = items.length, pageNumber = 1, perPage = 250): unknown {
  return { items, totalCount, page: pageNumber, perPage };
}

export interface CliResult {
  stdout: string;
  stderr: string;
  exitCode: number;
}

export interface RunCliOptions {
  cwd?: string;
  env?: NodeJS.ProcessEnv;
  executable?: string;
}

export async function runCli(args: string[], options: RunCliOptions = {}): Promise<CliResult> {
  const command = options.executable
    ? [options.executable, ...args]
    : [process.execPath, "src/index.ts", ...args];
  const processResult = Bun.spawn(command, {
    cwd: options.cwd ?? cliRoot,
    env: options.env ?? process.env,
    stdout: "pipe",
    stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([
    new Response(processResult.stdout).text(),
    new Response(processResult.stderr).text(),
    processResult.exited,
  ]);
  return { stdout, stderr, exitCode };
}

export interface CliEnvironmentOptions {
  env?: NodeJS.ProcessEnv;
  server?: string;
  token?: string;
}

export function useTestResources(): {
  cliEnvironment(options?: CliEnvironmentOptions): Promise<NodeJS.ProcessEnv>;
  startServer(handler: (request: Request) => Response | Promise<Response>): { server: Bun.Server<unknown>; url: string };
  tempDirectory(): Promise<string>;
} {
  const servers: Bun.Server<unknown>[] = [];
  const directories: string[] = [];

  afterEach(async () => {
    for (const server of servers.splice(0)) server.stop(true);
    await Promise.all(directories.splice(0).map(directory => rm(directory, { recursive: true, force: true })));
  });

  const tempDirectory = async (): Promise<string> => {
    const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
    directories.push(directory);
    return directory;
  };

  return {
    async cliEnvironment(options = {}) {
      const inheritedEnvironment = { ...process.env };
      delete inheritedEnvironment.COVE_SERVER;
      delete inheritedEnvironment.COVE_TOKEN;
      delete inheritedEnvironment.COVE_PROFILE;
      return {
        ...inheritedEnvironment,
        ...options.env,
        ...(options.server === undefined ? {} : { COVE_SERVER: options.server }),
        ...(options.token === undefined ? {} : { COVE_TOKEN: options.token }),
        COVE_CONFIG_DIR: await tempDirectory(),
      };
    },
    startServer(handler) {
      const running = startServer(handler);
      servers.push(running.server);
      return running;
    },
    tempDirectory,
  };
}
