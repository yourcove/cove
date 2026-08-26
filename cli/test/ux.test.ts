import { expect, test } from "bun:test";
import { createProgram, invocationNeedsTheme } from "../src/index";
import { renderVideo, renderVideoResults } from "../src/output";
import { stripTerminalSequences, uiPalette } from "../src/ui";
import { json, runCli, useTestResources } from "./helpers";

const resources = useTestResources();

async function bashCompletionResult(words: string[], line = words.join(" ")): Promise<{ candidates: string[]; noSpace: boolean }> {
  const generated = await runCli(["completion", "bash"]);
  const wordArray = words.map(value => JSON.stringify(value)).join(" ");
  const script = `${generated.stdout}\n__cove_cli_nospace=0\ncompopt() { if [[ "$1" == "-o" && "$2" == "nospace" ]]; then __cove_cli_nospace=1; fi; }\nCOMP_WORDS=(${wordArray})\nCOMP_CWORD=${words.length - 1}\nCOMP_LINE=${JSON.stringify(line)}\nCOMP_POINT=${line.length}\n_cove_cli\nprintf '__COVE_NOSPACE__%s\\n' "$__cove_cli_nospace"\nprintf '%s\\n' "\${COMPREPLY[@]}"`;
  const processResult = Bun.spawn(["bash", "--noprofile", "--norc", "-s"], { stdin: "pipe", stdout: "pipe", stderr: "pipe" });
  processResult.stdin.write(script);
  processResult.stdin.end();
  const [stdout, stderr, exitCode] = await Promise.all([
    new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited,
  ]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  const lines = stdout.trimEnd().split("\n");
  const marker = lines.shift();
  expect(marker).toMatch(/^__COVE_NOSPACE__[01]$/);
  return { candidates: lines.filter(Boolean), noSpace: marker === "__COVE_NOSPACE__1" };
}

async function bashCompletions(words: string[], line = words.join(" ")): Promise<string[]> {
  return (await bashCompletionResult(words, line)).candidates;
}

test("explicitly enabled UI colors do not depend on process-level color detection", () => {
  expect(uiPalette(true).accent("accent")).toContain("\u001b[");
  const brandedHelp = createProgram(undefined, true).helpInformation();
  expect(brandedHelp).toContain("\u001b[");
  expect(brandedHelp).toContain("\u001b[38;2;79;143;247m");
  expect(createProgram(undefined, true, "rgb(59, 189, 131)").helpInformation()).toContain("\u001b[38;2;59;189;131m");
  expect(uiPalette(false).accent("accent")).toBe("accent");
});

test("theme discovery is skipped when an invocation cannot display the theme", () => {
  expect(invocationNeedsTheme(["bun", "cove-cli", "--version"], true)).toBe(false);
  expect(invocationNeedsTheme(["bun", "cove-cli", "-V"], true)).toBe(false);
  expect(invocationNeedsTheme(["bun", "cove-cli", "--profile", "example", "completion", "fish"], true)).toBe(false);
  expect(invocationNeedsTheme(["bun", "cove-cli", "videos", "show", "4", "--json"], true)).toBe(false);
  expect(invocationNeedsTheme(["bun", "cove-cli", "--help"], true)).toBe(true);
  expect(invocationNeedsTheme(["bun", "cove-cli", "videos", "show", "4"], false)).toBe(false);
});

test("bare and command-group invocations show clean help", async () => {
  const root = await runCli([]);
  expect(root.exitCode).toBe(0);
  expect(root.stderr).toBe("");
  expect(root.stdout).toContain("Explore:");
  expect(root.stdout).toContain("Catalog:");
  expect(root.stdout).toContain("Account:");
  expect(root.stdout).toStartWith("COVE  CLI\n\nUsage:");
  expect(root.stdout.indexOf("Explore:")).toBeLessThan(root.stdout.indexOf("Account:"));
  const explore = root.stdout.slice(root.stdout.indexOf("Explore:"), root.stdout.indexOf("Catalog:"));
  const catalog = root.stdout.slice(root.stdout.indexOf("Catalog:"), root.stdout.indexOf("Account:"));
  const exploreEntities = ["videos", "images", "audios", "texts", "galleries", "segments", "performers", "tags", "groups", "studios"];
  for (const command of exploreEntities) {
    expect(explore).toContain(`  ${command}`);
    expect(catalog).not.toContain(`  ${command}`);
  }
  for (let index = 1; index < exploreEntities.length; index += 1) {
    expect(explore.indexOf(`  ${exploreEntities[index - 1]}`)).toBeLessThan(explore.indexOf(`  ${exploreEntities[index]}`));
  }
  expect(explore).not.toContain("  filters");
  expect(root.stdout).not.toContain("Error: (outputHelp)");

  const videos = await runCli(["videos"]);
  expect(videos.exitCode).toBe(0);
  expect(videos.stderr).toBe("");
  expect(videos.stdout).toContain("Usage: cove-cli videos");
  expect(videos.stdout).not.toContain("Error: (outputHelp)");
});

test("saved-filter commands are scoped to standard entity families", async () => {
  const root = createProgram(undefined, false);
  expect(root.commands.map(command => command.name())).not.toContain("filters");
  expect(await bashCompletions(["cove-cli", "fil"])).not.toContain("filters");

  for (const resource of ["videos", "audios", "images", "galleries", "tags", "performers", "studios", "groups", "texts"]) {
    const result = await runCli([resource, "filters", "--help"]);
    expect(result.exitCode).toBe(0);
    expect(result.stderr).toBe("");
    for (const command of ["list", "show", "create", "update", "delete", "default"]) expect(result.stdout).toContain(`  ${command}`);
    expect(result.stdout).not.toContain("--mode");
  }

  const segments = await runCli(["segments", "--help"]);
  expect(segments.stdout).not.toContain("  filters");
});

test("visual and raw AI artifacts are not exposed as first-class CLI commands", async () => {
  const root = createProgram(undefined, false);
  const commandNames = root.commands.map(command => command.name());
  expect(commandNames).not.toContain("faces");
  expect(commandNames).not.toContain("detections");
  expect(commandNames).not.toContain("embeddings");
  expect(await bashCompletions(["cove-cli", "fa"])).not.toContain("faces");
  expect(await bashCompletions(["cove-cli", "det"])).not.toContain("detections");
  expect(await bashCompletions(["cove-cli", "emb"])).not.toContain("embeddings");

  for (const command of ["faces", "detections", "embeddings"]) {
    const result = await runCli([command, "list"]);
    expect(result.exitCode).toBe(2);
    expect(result.stdout).toBe("");
    expect(result.stderr).toContain(`error: unknown command '${command}'`);
    expect(result.stderr).toContain("help: run `cove-cli --help` for usage");
  }
});

test("completion generates shell-native scripts from the command tree", async () => {
  const markers = { bash: "# bash completion", zsh: "#compdef cove-cli", fish: "# fish completion" } as const;
  for (const shell of ["bash", "zsh", "fish"] as const) {
    const result = await runCli(["completion", shell]);
    expect(result.exitCode).toBe(0);
    expect(result.stderr).toBe("");
    expect(result.stdout).toStartWith(markers[shell]);
    expect(result.stdout).toContain("cove-cli");
    expect(result.stdout).toContain("videos");
    expect(result.stdout).toContain("--output");
    expect(result.stdout).toContain("--hyperlinks");
    expect(result.stdout).toContain("--performer");
    expect(result.stdout).toContain("--filter-by");
    expect(result.stdout).toContain("title:excludes=");
    expect(result.stdout).not.toContain("\u001b[");
    if (shell === "fish") {
      expect(result.stdout).toContain("set -l path '__root__'");
      expect(result.stdout).toContain("if test \"$token\" = '--'");
    }
    if (shell === "zsh") {
      expect(result.stdout).toContain("compadd -S '' -- 'title:equals='");
      expect(result.stdout).toContain("compadd -- 'title:is-null'");
    }
  }

  const invalid = await runCli(["completion", "powershell"]);
  expect(invalid.exitCode).toBe(2);
  expect(invalid.stdout).toBe("");
  expect(invalid.stderr).toContain("bash, zsh, or fish");
  expect(invalid.stderr).toContain("cove-cli completion --help");

  expect(await bashCompletions(["cove-cli", "videos", "list", "--", ""])).toEqual([]);
  expect(await bashCompletions(["cove-cli", "--output", "=", "j"], "cove-cli --output=j")).toEqual(["json", "jsonl"]);
  expect(await bashCompletions(["cove-cli", "-oj"])).toEqual(["-ojson", "-ojsonl"]);
  expect(await bashCompletions(["cove-cli", "--hyperlinks", "=", "a"], "cove-cli --hyperlinks=a")).toEqual(["auto", "always"]);
  expect(await bashCompletions(["cove-cli", "--hyperlinks", "a"])).toEqual(["auto", "always"]);
  expect(await bashCompletions(["cove-cli", "similar", "video", "42", "--by", "v"])).toEqual(["visual"]);
  expect(await bashCompletions(["cove-cli", "similar", "video", "42", "--type=i"])).toEqual(["--type=images"]);
  expect(await bashCompletions(["cove-cli", "--profile", "=", "auth"], "cove-cli --profile=auth")).toEqual([]);
  const attachedProfile = await bashCompletions(["cove-cli", "--profile", "=", "auth", "videos", "list", ""], "cove-cli --profile=auth videos list ");
  expect(attachedProfile).toContain("--tag");
  expect(attachedProfile).not.toContain("login");
  const fragmentedProfile = await bashCompletions(["cove-cli", "--profile", "=", "foo", ":", "auth", "videos", "list", ""], "cove-cli --profile=foo:auth videos list ");
  expect(fragmentedProfile).toContain("--tag");
  expect(fragmentedProfile).not.toContain("login");
  expect(await bashCompletions(["cove-cli", "videos", "list", "--filter-by", "title:e"])).toEqual(["title:equals=", "title:excludes="]);
  expect(await bashCompletions(["cove-cli", "videos", "list", "--filter-by", "path:under"])).toEqual(["path:under-path="]);
  expect(await bashCompletions(["cove-cli", "videos", "list", "--filter-by=title:e"])).toEqual(["--filter-by=title:equals=", "--filter-by=title:excludes="]);
  expect(await bashCompletions(["cove-cli", "performers", "list", "--filter-by", "name:e"])).toEqual(["name:equals=", "name:excludes="]);
  expect(await bashCompletions(["cove-cli", "videos", "list", "--filter-by", "name:e"])).toEqual([]);
  const splitFilter = await bashCompletionResult(["cove-cli", "videos", "list", "--filter-by", "title", ":", "e"], "cove-cli videos list --filter-by title:e");
  expect(splitFilter).toEqual({ candidates: ["equals=", "excludes="], noSpace: true });
  const splitAttachedFilter = await bashCompletionResult(["cove-cli", "videos", "list", "--filter-by", "=", "title", ":", "e"], "cove-cli videos list --filter-by=title:e");
  expect(splitAttachedFilter).toEqual({ candidates: ["equals=", "excludes="], noSpace: true });
  const unaryFilter = await bashCompletionResult(["cove-cli", "videos", "list", "--filter-by", "title", ":", "is-n"], "cove-cli videos list --filter-by title:is-n");
  expect(unaryFilter).toEqual({ candidates: ["is-null"], noSpace: false });
}, 10_000);

test("hyperlink policy can force or suppress OSC-8 links", async () => {
  const running = resources.startServer(request => {
    const path = new URL(request.url).pathname;
    if (path === "/api/videos/4") return json({ id: 4, title: "Example Video", performers: [], files: [] });
    if (path === "/api/performers/7") return json({ id: 7, name: "Example Performer", aliases: [] });
    if (path === "/api/videos/find") return json({ items: [{ id: 5, title: "List Result", performers: [], files: [] }], totalCount: 1, page: 1, perPage: 25 });
    if (path === "/api/search/global") return json({ groups: [{ type: "video", items: [{ id: 6, title: "Search Result", subtitle: "Context" }] }], failedTypes: [] });
    return json({}, 404);
  });
  const directory = await resources.tempDirectory();
  const env = { ...process.env, TERM: "dumb", COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory };

  const forced = await runCli(["videos", "show", "4", "--hyperlinks", "always"], { env });
  expect(forced.exitCode).toBe(0);
  expect(forced.stdout).toContain("\u001b]8;;");
  expect(forced.stdout).toContain("/video/4");

  const linkedList = await runCli(["videos", "list", "--performer", "7", "--hyperlinks", "always"], { env });
  expect(linkedList.exitCode).toBe(0);
  expect(stripTerminalSequences(linkedList.stdout).split("\n")[4]).toBe("List Result");
  expect(linkedList.stdout).not.toMatch(/\n\n(?:ID|TITLE)\b/);
  expect(linkedList.stdout.split("1-1 of 1")).toHaveLength(3);
  expect(linkedList.stdout).toContain("Page 1/1");
  expect(linkedList.stdout).toContain("\u001b]8;;");
  expect(linkedList.stdout).toContain("/video/5");

  const linkedSearch = await runCli(["search", "Example", "--hyperlinks", "always"], { env });
  expect(linkedSearch.exitCode).toBe(0);
  expect(linkedSearch.stdout.split("\n")[2]).toMatch(/^TITLE/);
  expect(linkedSearch.stdout.split("\n")[2]).not.toContain("ID");
  expect(linkedSearch.stdout).toContain("/video/6");

  const disabled = await runCli(["videos", "show", "4", "--hyperlinks", "never"], { env: { ...env, TERM: "xterm-256color" } });
  expect(disabled.exitCode).toBe(0);
  expect(disabled.stdout).not.toContain("\u001b]8;;");

  const automatic = await runCli(["videos", "show", "4", "--hyperlinks", "auto"], { env: { ...env, TERM: "xterm-256color" } });
  expect(automatic.exitCode).toBe(0);
  expect(automatic.stdout).not.toContain("\u001b]8;;");

  const machine = await runCli(["videos", "show", "4", "--hyperlinks", "always", "--json"], { env });
  expect(machine.exitCode).toBe(0);
  expect(machine.stdout).not.toContain("\u001b]8;;");
  expect(JSON.parse(machine.stdout).id).toBe(4);

  const invalid = await runCli(["videos", "show", "4", "--hyperlinks", "sometimes"], { env });
  expect(invalid.exitCode).toBe(2);
  expect(invalid.stderr).toContain("auto, always, or never");

  const invalidGroup = await runCli(["videos", "--hyperlinks", "sometimes"], { env });
  expect(invalidGroup.exitCode).toBe(2);
  expect(invalidGroup.stderr).toContain("auto, always, or never");
});

test("usage errors are concise, actionable, and emitted once", async () => {
  const result = await runCli(["vidoes"]);
  expect(result.exitCode).toBe(2);
  expect(result.stdout).toBe("");
  expect(result.stderr.match(/unknown command/g)?.length).toBe(1);
  expect(result.stderr).toContain("error: unknown command 'vidoes'");
  expect(result.stderr).toContain("tip: did you mean videos?");
  expect(result.stderr).toContain("help: run `cove-cli --help` for usage");
  expect(result.stderr).not.toContain("Commands:");
  expect(result.stderr).not.toContain("Error: error:");

  const nested = await runCli(["videos", "lits"]);
  expect(nested.exitCode).toBe(2);
  expect(nested.stderr).toContain("error: unknown command 'lits'");
  expect(nested.stderr).toContain("tip: did you mean list?");
  expect(nested.stderr).toContain("help: run `cove-cli videos --help` for usage");
  expect(nested.stderr).not.toContain("too many arguments");

  const unknownOption = await runCli(["videos", "--bogus"]);
  expect(unknownOption.exitCode).toBe(2);
  expect(unknownOption.stderr).toContain("error: unknown option '--bogus'");

  const unconfiguredList = await runCli(["videos", "list"]);
  expect(unconfiguredList.exitCode).toBe(1);
  expect(unconfiguredList.stderr).toContain("No Cove server is configured");

  const misspelledHelp = await runCli(["help", "vidoes"]);
  expect(misspelledHelp.exitCode).toBe(2);
  expect(misspelledHelp.stderr).toContain("tip: did you mean videos?");
  expect(misspelledHelp.stderr).toContain("help: run `cove-cli --help` for usage");

  const nestedMisspelledHelp = await runCli(["help", "videos", "lits"]);
  expect(nestedMisspelledHelp.exitCode).toBe(2);
  expect(nestedMisspelledHelp.stderr).toContain("tip: did you mean list?");
  expect(nestedMisspelledHelp.stderr).toContain("help: run `cove-cli videos --help` for usage");
});

test("JSON usage errors stay structured and do not include help output", async () => {
  const result = await runCli(["vidoes", "--json"]);
  expect(result.exitCode).toBe(2);
  expect(result.stdout).toBe("");
  expect(result.stderr).not.toContain("Usage:");
  expect(JSON.parse(result.stderr)).toEqual({
    error: {
      code: "INVALID_ARGUMENT",
      message: "unknown command 'vidoes'",
      details: { suggestion: "videos", help: "cove-cli --help" },
    },
  });

  const finalJson = await runCli(["--output", "human", "--output", "json", "--bogus"]);
  expect(finalJson.exitCode).toBe(2);
  expect(JSON.parse(finalJson.stderr).error.code).toBe("INVALID_ARGUMENT");

  const finalHuman = await runCli(["--output", "json", "--output", "human", "--bogus"]);
  expect(finalHuman.exitCode).toBe(2);
  expect(finalHuman.stderr).toStartWith("error:");
});

test("output formats support JSON Lines without breaking the JSON alias", async () => {
  const running = resources.startServer(request => {
    const path = new URL(request.url).pathname;
    if (path === "/api/auth/me") return json({ user: { uiPreferences: { defaultFilters: {} } } });
    if (path === "/api/tags/find") return json({ items: [
      { id: 1, name: "First", aliases: [] },
      { id: 2, name: "Second", aliases: [] },
    ], totalCount: 2, page: 1, perPage: 25 });
    return json({}, 404);
  });
  const directory = await resources.tempDirectory();
  const env = { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory };

  const jsonl = await runCli(["tags", "list", "--page", "1", "--output", "jsonl"], { env });
  expect(jsonl.exitCode).toBe(0);
  expect(jsonl.stderr).toBe("");
  expect(jsonl.stdout.trim().split("\n").map(line => JSON.parse(line))).toEqual([
    { id: 1, name: "First", aliases: [] },
    { id: 2, name: "Second", aliases: [] },
  ]);

  const jsonAlias = await runCli(["tags", "list", "--page", "1", "--json"], { env });
  expect(JSON.parse(jsonAlias.stdout)).toEqual({ tags: [
    { id: 1, name: "First", aliases: [] },
    { id: 2, name: "Second", aliases: [] },
  ], totalCount: 2 });

  const invalid = await runCli(["tags", "list", "--output", "yaml"]);
  expect(invalid.exitCode).toBe(2);
  expect(invalid.stderr).toContain("--output must be one of human, json, or jsonl");

  const invalidGroup = await runCli(["videos", "--output", "yaml"]);
  expect(invalidGroup.exitCode).toBe(2);
  expect(invalidGroup.stderr).toContain("--output must be one of human, json, or jsonl");

  const conflict = await runCli(["tags", "list", "--json", "--output", "human"]);
  expect(conflict.exitCode).toBe(2);
  expect(JSON.parse(conflict.stderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT" } });

  const groupConflict = await runCli(["videos", "--json", "--output", "human"]);
  expect(groupConflict.exitCode).toBe(2);
  expect(JSON.parse(groupConflict.stderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT" } });
});

test("list volume defaults are bounded with single-request limit and batched unlimited modes", async () => {
  const requests: Array<{ page: number; perPage: number }> = [];
  const tags = Array.from({ length: 41 }, (_, index) => ({ id: index + 1, name: `Tag ${index + 1}`, aliases: [] }));
  const running = resources.startServer(async request => {
    const path = new URL(request.url).pathname;
    if (path === "/api/auth/me") return json({ user: { uiPreferences: { defaultFilters: {} } } });
    if (path !== "/api/tags/find") return json({}, 404);
    const body = await request.json() as { findFilter: { page: number; perPage: number } };
    requests.push({ page: body.findFilter.page, perPage: body.findFilter.perPage });
    const start = (body.findFilter.page - 1) * body.findFilter.perPage;
    return json({ items: tags.slice(start, start + body.findFilter.perPage), totalCount: tags.length, page: body.findFilter.page, perPage: body.findFilter.perPage });
  });
  const directory = await resources.tempDirectory();
  const env = { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory };

  const bounded = await runCli(["tags", "list", "--json"], { env });
  expect(bounded.exitCode).toBe(0);
  expect(JSON.parse(bounded.stdout).tags).toHaveLength(25);
  expect(requests.splice(0)).toEqual([{ page: 1, perPage: 25 }]);

  const paged = await runCli(["tags", "list", "--page", "2", "--per-page", "15"], { env });
  expect(paged.exitCode).toBe(0);
  expect(paged.stdout.split("16-30 of 41 · Page 2/3")).toHaveLength(3);
  expect(requests.splice(0)).toEqual([{ page: 2, perPage: 15 }]);

  const limited = await runCli(["tags", "list", "--limit", "30", "--json"], { env });
  expect(limited.exitCode).toBe(0);
  expect(JSON.parse(limited.stdout).tags).toHaveLength(30);
  expect(requests.splice(0)).toEqual([{ page: 1, perPage: 30 }]);

  const unlimited = await runCli(["tags", "list", "--unlimited", "--json"], { env });
  expect(unlimited.exitCode).toBe(0);
  expect(JSON.parse(unlimited.stdout).tags).toHaveLength(41);
  expect(requests.splice(0)).toEqual([{ page: 1, perPage: 40 }, { page: 2, perPage: 40 }]);

  const conflict = await runCli(["tags", "list", "--unlimited", "--limit", "5", "--json"], { env });
  expect(conflict.exitCode).toBe(2);
  expect(JSON.parse(conflict.stderr)).toMatchObject({ error: { code: "FILTER_CONFLICT" } });
  expect(requests).toHaveLength(0);
});

test("concise help hides parser defaults while nested help exposes full sort reference", async () => {
  const concise = await runCli(["videos", "list", "--help"]);
  expect(concise.exitCode).toBe(0);
  expect(concise.stderr).toBe("");
  expect(concise.stdout).not.toContain("(default: [])");
  expect(concise.stdout).not.toContain("play_duration, resume_time");
  expect(concise.stdout).toContain("cove-cli help videos list");
  expect(concise.stdout).toContain("direction defaults to asc");

  const detailed = await runCli(["help", "videos", "list"]);
  expect(detailed.exitCode).toBe(0);
  expect(detailed.stderr).toBe("");
  expect(detailed.stdout).toContain("Usage: cove-cli videos list");
  expect(detailed.stdout).toContain("All sort fields:");
  expect(detailed.stdout).toContain("play_duration");
});

test("video list output uses Cove's compact two-line hierarchy", () => {
  const rendered = renderVideoResults("Videos", [{
    id: 42,
    title: "A sample title",
    date: "2026-08-18",
    studioName: "Studio",
    performers: [],
    files: [{ duration: 392, width: 1920, height: 1080 }],
  }], { color: false, totalCount: 24 });

  expect(rendered).toStartWith("Videos · 24 matches");
  expect(rendered).toContain("A sample title");
  expect(rendered).toContain("Studio · 6:32 · 2026-08-18");
  expect(rendered).not.toContain("👤");
  expect(rendered).not.toContain("🏷");
  expect(rendered.split("\n")[3]).toEndWith("  42");
  expect(rendered).not.toMatch(/\n\n(?:ID|TITLE)\b/);
  expect(rendered).not.toContain("#42");
  expect(rendered).toContain("Showing 1 of 24 matches.");
  expect(rendered).not.toMatch(/[┌┬┐├┼┤└┴┘│]/);
});

test("video detail output is sectioned and begins with useful metadata", () => {
  const rendered = renderVideo({
    id: 42,
    title: "A sample title",
    date: "2026-08-18",
    studioName: "Studio",
    director: "Director",
    performers: [{ id: 7, name: "Performer" }],
    tags: [{ id: 8, name: "Tag" }],
    groups: [],
    galleries: [],
    urls: ["/videos/42"],
    files: [{ path: "/media/sample.mp4", duration: 392, width: 1920, height: 1080 }],
  }, { color: false, terminalWidth: 80, server: "https://cove.example" });

  expect(rendered).toStartWith("A sample title\n#42 · 2026-08-18 · 6:32 · 1920×1080 · Studio");
  expect(rendered).toContain("\n\nOverview\n");
  expect(rendered).toContain("\n\nLibrary\n");
  expect(rendered).toContain("\n\nFiles and links\n");
});

test("human authentication output uses the shared status-card style", async () => {
  const running = resources.startServer(request => {
    const path = new URL(request.url).pathname;
    if (path === "/api/system/status") return json({ version: "1.2.3", authEnabled: true });
    if (path === "/api/auth/me") return json({ user: { username: "user" }, permissions: [] });
    return json({}, 404);
  });
  const directory = await resources.tempDirectory();

  const result = await runCli(["auth", "login", "--server", running.url, "--no-color"], { env: {
    ...process.env,
    COVE_TOKEN: "test-token",
    COVE_CONFIG_DIR: directory,
  } });

  expect(result.exitCode).toBe(0);
  expect(result.stdout).toStartWith("✓ Logged in\n");
  expect(result.stdout).toContain("Server");
  expect(result.stdout).toContain("Profile");
  expect(result.stdout).toContain("Account");
  expect(result.stdout).not.toContain("\u001b[");
});
