import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { DatabaseSync } from "node:sqlite";
import { fileURLToPath } from "node:url";

const scriptPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "backfill-stash-video-covers.mjs");
const blobId = "11111111-1111-4111-8111-111111111111";

function runScript(argumentsList, environment) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [scriptPath, ...argumentsList], {
      env: { ...process.env, ...environment },
      stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.on("close", (status) => resolve({ status, stdout, stderr }));
  });
}

function createFixture({ duplicateBlob = false, duplicatePhysicalId = false, misplacedBlob = false, misplacedSameId = false } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "cove-stash-cover-backfill-"));
  const generatedPath = path.join(root, "generated");
  const screenshotsPath = path.join(generatedPath, "screenshots", "aa");
  const blobsPath = path.join(generatedPath, "blobs", "11");
  fs.mkdirSync(screenshotsPath, { recursive: true });
  fs.mkdirSync(blobsPath, { recursive: true });

  const cover = Buffer.from("the original Stash scene cover");
  const checksum = createHash("md5").update(cover).digest("hex");
  const videoFingerprint = createHash("md5").update("the source video bytes").digest("hex");
  fs.writeFileSync(path.join(screenshotsPath, "101.jpg"), "a JPEG converted from the original cover");
  fs.writeFileSync(path.join(screenshotsPath, "102.jpg"), "a generated frame, not a Stash cover");
  fs.writeFileSync(path.join(blobsPath, `${blobId}.jpg`), cover);
  if (duplicatePhysicalId) {
    fs.writeFileSync(path.join(blobsPath, `${blobId}.png`), "different payload bytes");
  }
  if (misplacedBlob) {
    const misplacedPath = path.join(generatedPath, "blobs", "wrong-bucket");
    fs.mkdirSync(misplacedPath, { recursive: true });
    fs.writeFileSync(path.join(misplacedPath, "33333333-3333-4333-8333-333333333333.jpg"), cover);
  }
  if (misplacedSameId) {
    const misplacedPath = path.join(generatedPath, "blobs", "wrong-bucket");
    fs.mkdirSync(misplacedPath, { recursive: true });
    fs.writeFileSync(path.join(misplacedPath, `${blobId}.png`), "different payload bytes");
  }
  if (duplicateBlob) {
    const duplicateId = "22222222-2222-4222-8222-222222222222";
    const duplicatePath = path.join(generatedPath, "blobs", "22");
    fs.mkdirSync(duplicatePath, { recursive: true });
    fs.writeFileSync(path.join(duplicatePath, `${duplicateId}.jpg`), cover);
  }

  const stashDbPath = path.join(root, "stash.sqlite");
  const stash = new DatabaseSync(stashDbPath);
  stash.exec("CREATE TABLE scenes (id INTEGER PRIMARY KEY, cover_blob TEXT);");
  stash.exec("CREATE TABLE scenes_files (scene_id INTEGER, file_id INTEGER, [primary] BOOLEAN);");
  stash.exec("CREATE TABLE files_fingerprints (file_id INTEGER, type TEXT, fingerprint BLOB);");
  stash.prepare("INSERT INTO scenes (id, cover_blob) VALUES (?, ?)").run(1, checksum);
  stash.prepare("INSERT INTO scenes_files (scene_id, file_id, [primary]) VALUES (?, ?, ?)").run(1, 11, 1);
  stash.prepare("INSERT INTO files_fingerprints (file_id, type, fingerprint) VALUES (?, ?, ?)").run(11, "md5", videoFingerprint);
  stash.close();

  const capturePath = path.join(root, "applied.sql");
  const invocationPath = path.join(root, "psql-invocations.jsonl");
  const fakePsqlPath = path.join(root, "fake-psql.mjs");
  fs.writeFileSync(fakePsqlPath, `#!/usr/bin/env node
import fs from "node:fs";
const args = process.argv.slice(2);
fs.appendFileSync(process.env.FAKE_PSQL_INVOCATIONS, JSON.stringify({
  args,
  database: process.env.PGDATABASE,
  host: process.env.PGHOST,
  hostAddress: process.env.PGHOSTADDR,
  password: process.env.PGPASSWORD,
  service: process.env.PGSERVICE,
  user: process.env.PGUSER,
}) + "\\n");
if (args.includes("--command")) {
  process.stdout.write("total|2\\nid|101\\nid|102\\nfp|101|0|md5|${videoFingerprint}\\n");
} else {
  let input = "";
  for await (const chunk of process.stdin) input += chunk;
  fs.writeFileSync(process.env.FAKE_PSQL_CAPTURE, input);
  process.stdout.write("1\\n");
}
`);
  fs.chmodSync(fakePsqlPath, 0o755);

  return { capturePath, checksum, fakePsqlPath, generatedPath, invocationPath, root, stashDbPath, videoFingerprint };
}

function baseArguments(fixture) {
  return [
    "--stash-db", fixture.stashDbPath,
    "--generated-path", fixture.generatedPath,
    "--database-url", "postgresql://example.invalid/cove",
    "--psql", fixture.fakePsqlPath,
    "--quiet",
    "--details", "0",
  ];
}

test("dry-run maps converted Stash covers by video fingerprint and performs no update", async () => {
  const fixture = createFixture();
  const reportPath = path.join(fixture.root, "report.json");
  try {
    const result = await runScript(
      [...baseArguments(fixture), "--report", reportPath],
      {
        FAKE_PSQL_CAPTURE: fixture.capturePath,
        FAKE_PSQL_INVOCATIONS: fixture.invocationPath,
        PGHOSTADDR: "203.0.113.10",
        PGSERVICE: "unrelated-service",
      },
    );

    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Mode: DRY RUN/);
    assert.match(result.stdout, /Stash scenes uniquely matched by file fingerprint: 1/);
    assert.match(result.stdout, /Planned video cover backfills: 1/);
    assert.match(result.stdout, /Dry run only: no database rows or generated files were changed/);
    assert.equal(fs.existsSync(fixture.capturePath), false);

    const [invocation] = fs.readFileSync(fixture.invocationPath, "utf8").trim().split("\n").map(JSON.parse);
    assert.equal(invocation.host, "example.invalid");
    assert.equal(invocation.database, "cove");
    assert.equal(invocation.hostAddress, undefined);
    assert.equal(invocation.user, undefined);
    assert.equal(invocation.password, undefined);
    assert.equal(invocation.service, undefined);
    assert.doesNotMatch(invocation.args.join(" "), /postgresql:\/\//);

    const report = JSON.parse(fs.readFileSync(reportPath, "utf8"));
    assert.equal(report.version, 3);
    assert.equal(report.blobs.duplicatePhysicalIds, 0);
    assert.deepEqual(report.plan, [{ blobId, checksum: fixture.checksum, stashSceneId: 1, videoId: 101 }]);
    assert.equal(report.result.applied, 0);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("apply uses a guarded transaction and retains generated files", async () => {
  const fixture = createFixture();
  const screenshotPath = path.join(fixture.generatedPath, "screenshots", "aa", "101.jpg");
  const blobPath = path.join(fixture.generatedPath, "blobs", "11", `${blobId}.jpg`);
  try {
    const result = await runScript(
      [...baseArguments(fixture), "--apply"],
      { FAKE_PSQL_CAPTURE: fixture.capturePath, FAKE_PSQL_INVOCATIONS: fixture.invocationPath },
    );

    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Mode: APPLY/);
    assert.match(result.stdout, /Applied video cover backfills: 1/);
    assert.equal(fs.existsSync(screenshotPath), true);
    assert.equal(fs.existsSync(blobPath), true);

    const sql = fs.readFileSync(fixture.capturePath, "utf8");
    assert.match(sql, new RegExp(`101\\t${blobId}`));
    assert.match(sql, /ImageBlobId/);
    assert.match(sql, /backfill race detected/);
    assert.doesNotMatch(sql, /\bDELETE\b/i);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("ambiguous identical Cove blobs are reported and skipped", async () => {
  const fixture = createFixture({ duplicateBlob: true });
  try {
    const result = await runScript(
      baseArguments(fixture),
      { FAKE_PSQL_CAPTURE: fixture.capturePath, FAKE_PSQL_INVOCATIONS: fixture.invocationPath },
    );

    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Planned video cover backfills: 0/);
    assert.match(result.stdout, /Skipped ambiguous blob matches: 1/);
    assert.equal(fs.existsSync(fixture.capturePath), false);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("canonical blob scanning ignores matching payloads outside their Cove bucket", async () => {
  const fixture = createFixture({ misplacedBlob: true });
  try {
    const result = await runScript(
      baseArguments(fixture),
      { FAKE_PSQL_CAPTURE: fixture.capturePath, FAKE_PSQL_INVOCATIONS: fixture.invocationPath },
    );

    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Planned video cover backfills: 1/);
    assert.match(result.stdout, /Skipped ambiguous blob matches: 0/);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("a blob id with multiple physical payloads is disqualified globally", async () => {
  const fixture = createFixture({ duplicatePhysicalId: true });
  try {
    const result = await runScript(
      baseArguments(fixture),
      { FAKE_PSQL_CAPTURE: fixture.capturePath, FAKE_PSQL_INVOCATIONS: fixture.invocationPath },
    );

    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Planned video cover backfills: 0/);
    assert.match(result.stdout, /Disqualified blob IDs with multiple payloads: 1/);
    assert.match(result.stdout, /Skipped missing exact blob matches: 1/);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("a canonical blob with the same id in a misplaced bucket is disqualified globally", async () => {
  const fixture = createFixture({ misplacedSameId: true });
  try {
    const result = await runScript(
      baseArguments(fixture),
      { FAKE_PSQL_CAPTURE: fixture.capturePath, FAKE_PSQL_INVOCATIONS: fixture.invocationPath },
    );

    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Planned video cover backfills: 0/);
    assert.match(result.stdout, /Disqualified blob IDs with multiple payloads: 1/);
    assert.match(result.stdout, /Skipped missing exact blob matches: 1/);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});
