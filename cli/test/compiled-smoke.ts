import { expect, test } from "bun:test";
import { join } from "node:path";
import { json, runCli, useTestResources } from "./helpers";

const resources = useTestResources();
const cliRoot = join(import.meta.dir, "..");
const executable = process.env.COVE_CLI_EXECUTABLE
  ?? join(cliRoot, "dist", process.platform === "win32" ? "cove-cli.exe" : "cove-cli");

test("compiled executable reports the package version and renders help", async () => {
  const packageMetadata = await Bun.file(join(cliRoot, "package.json")).json() as { version: string };

  const version = await runCli(["--version"], { executable });
  expect(version).toEqual({ stdout: `${packageMetadata.version}\n`, stderr: "", exitCode: 0 });

  const help = await runCli(["--help", "--no-color"], { executable });
  expect(help.exitCode).toBe(0);
  expect(help.stderr).toBe("");
  expect(help.stdout).toContain("Usage: cove-cli [options] [command]");
  expect(help.stdout).toContain("Explore:");

  const invalid = await runCli(["search", "x", "--json"], { executable });
  expect(invalid.exitCode).toBe(2);
  expect(invalid.stdout).toBe("");
  expect(JSON.parse(invalid.stderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT" } });
});

test("compiled executable performs an authenticated API command", async () => {
  const response = { groups: [{ type: "video", items: [{ id: 4, title: "Result", subtitle: null }] }], failedTypes: [] };
  const running = resources.startServer(request => {
    const url = new URL(request.url);
    expect(request.headers.get("Authorization")).toBe("Bearer test-token");
    expect(url.pathname).toBe("/api/search/global");
    expect(url.searchParams.get("q")).toBe("example");
    return json(response);
  });
  const env = await resources.cliEnvironment({ server: running.url, token: "test-token" });

  const result = await runCli(["search", "example", "--json"], { executable, env });

  expect(result.exitCode).toBe(0);
  expect(result.stderr).toBe("");
  expect(JSON.parse(result.stdout)).toEqual(response);
});
