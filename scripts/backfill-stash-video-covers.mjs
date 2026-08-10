#!/usr/bin/env node

import { createHash } from "node:crypto";
import { createReadStream } from "node:fs";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { spawn } from "node:child_process";
import { DatabaseSync } from "node:sqlite";
import { fileURLToPath } from "node:url";

const canonicalBlobIdPattern = /^([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:\.|$)/;
const md5Pattern = /^[0-9a-f]{32}$/;

function usage() {
  return `Usage:
  node scripts/backfill-stash-video-covers.mjs \\
    --stash-db /path/to/stash-go.sqlite \\
    --generated-path /path/to/cove/generated

Dry-run is the default. Add --apply to update videos.ImageBlobId. The script
never deletes screenshots or blobs. A backfill is planned only when the
original Stash scene maps to exactly one Cove video by imported md5/oshash and
its cover_blob checksum maps to exactly one Cove blob payload.

Requires a recent Node.js runtime with node:sqlite (validated on Node 24).
Stop Cove while using --apply so blob files cannot change during the update.

Options:
  --stash-db PATH        Original Stash SQLite database (required)
  --generated-path PATH Cove generated directory containing blobs
  --database-url URL    Cove PostgreSQL URL (defaults to Cove/dev environment)
  --psql PATH           psql executable (default: psql)
  --apply               Apply the exact dry-run plan transactionally
  --limit COUNT         Inspect only the first COUNT eligible Cove videos
  --concurrency COUNT   Concurrent filesystem reads (default: CPU count, max 8)
  --details COUNT       Planned mappings printed to stdout (default: 20)
  --report PATH         Write the full JSON report, including planned mappings
  --quiet               Suppress scan progress
  --help                 Show this help
`;
}

function fail(message) {
  throw new Error(message);
}

function requireValue(args, index, option) {
  const value = args[index + 1];
  if (!value || value.startsWith("--")) fail(`${option} requires a value`);
  return value;
}

function parsePositiveInteger(value, option, { allowZero = false } = {}) {
  const parsed = Number.parseInt(value, 10);
  const minimum = allowZero ? 0 : 1;
  if (!Number.isInteger(parsed) || String(parsed) !== value || parsed < minimum) {
    fail(`${option} must be an integer greater than or equal to ${minimum}`);
  }
  return parsed;
}

export function parseArgs(args, environment = process.env) {
  const options = {
    apply: false,
    concurrency: Math.min(os.availableParallelism(), 8),
    databaseUrl: environment.COVE_DEV_SOURCE_DATABASE_URL ?? environment.DATABASE_URL ?? null,
    details: 20,
    generatedPath: environment.COVE_GENERATED_PATH ?? null,
    help: false,
    limit: null,
    psql: "psql",
    quiet: false,
    reportPath: null,
    stashDbPath: null,
  };

  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index];
    switch (argument) {
      case "--apply": options.apply = true; break;
      case "--quiet": options.quiet = true; break;
      case "--help": options.help = true; break;
      case "--stash-db": options.stashDbPath = requireValue(args, index, argument); index += 1; break;
      case "--generated-path": options.generatedPath = requireValue(args, index, argument); index += 1; break;
      case "--database-url": options.databaseUrl = requireValue(args, index, argument); index += 1; break;
      case "--psql": options.psql = requireValue(args, index, argument); index += 1; break;
      case "--report": options.reportPath = requireValue(args, index, argument); index += 1; break;
      case "--limit":
        options.limit = parsePositiveInteger(requireValue(args, index, argument), argument);
        index += 1;
        break;
      case "--concurrency":
        options.concurrency = parsePositiveInteger(requireValue(args, index, argument), argument);
        index += 1;
        break;
      case "--details":
        options.details = parsePositiveInteger(requireValue(args, index, argument), argument, { allowZero: true });
        index += 1;
        break;
      default: fail(`unknown argument: ${argument}`);
    }
  }

  if (!options.help) {
    if (!options.stashDbPath) fail("--stash-db is required");
    if (!options.generatedPath) fail("--generated-path is required");
    if (!options.databaseUrl && !environment.PGDATABASE) {
      fail("a Cove database URL is required via --database-url, COVE_DEV_SOURCE_DATABASE_URL, DATABASE_URL, or PGDATABASE");
    }
  }

  return options;
}

async function assertFile(filePath, description) {
  let stats;
  try {
    stats = await fs.stat(filePath);
  } catch (error) {
    if (error?.code === "ENOENT") fail(`${description} does not exist: ${filePath}`);
    throw error;
  }
  if (!stats.isFile()) fail(`${description} is not a file: ${filePath}`);
}

async function assertDirectory(directoryPath, description) {
  let stats;
  try {
    stats = await fs.stat(directoryPath);
  } catch (error) {
    if (error?.code === "ENOENT") fail(`${description} does not exist: ${directoryPath}`);
    throw error;
  }
  if (!stats.isDirectory()) fail(`${description} is not a directory: ${directoryPath}`);
}

function psqlEnvironment(databaseUrl) {
  const environment = { ...process.env };
  if (!databaseUrl) return environment;

  // An explicit URL must be the complete connection authority. libpq accepts many
  // PG* variables (including PGHOSTADDR and PGSERVICE) that can redirect or augment
  // a connection even when PGHOST is set, so do not inherit any of them here.
  for (const name of Object.keys(environment)) {
    if (name.startsWith("PG")) delete environment[name];
  }

  let parsed;
  try {
    parsed = new URL(databaseUrl);
  } catch {
    fail("--database-url must be a postgresql:// URL");
  }
  if (parsed.protocol !== "postgresql:" && parsed.protocol !== "postgres:") {
    fail("--database-url must use the postgresql:// or postgres:// scheme");
  }
  if (!parsed.hostname || !parsed.pathname || parsed.pathname === "/") {
    fail("--database-url must include a host and database name");
  }

  environment.PGHOST = decodeURIComponent(parsed.hostname);
  environment.PGDATABASE = decodeURIComponent(parsed.pathname.slice(1));
  if (parsed.port) environment.PGPORT = parsed.port;
  if (parsed.username) environment.PGUSER = decodeURIComponent(parsed.username);
  if (parsed.password) environment.PGPASSWORD = decodeURIComponent(parsed.password);

  const libpqParameters = new Map([
    ["application_name", "PGAPPNAME"],
    ["connect_timeout", "PGCONNECT_TIMEOUT"],
    ["options", "PGOPTIONS"],
    ["sslcert", "PGSSLCERT"],
    ["sslkey", "PGSSLKEY"],
    ["sslmode", "PGSSLMODE"],
    ["sslrootcert", "PGSSLROOTCERT"],
    ["target_session_attrs", "PGTARGETSESSIONATTRS"],
  ]);
  for (const [parameter, environmentName] of libpqParameters) {
    if (parsed.searchParams.has(parameter)) environment[environmentName] = parsed.searchParams.get(parameter);
  }
  return environment;
}

function runPsql(options, argumentsList, input = null) {
  return new Promise((resolve, reject) => {
    const child = spawn(options.psql, argumentsList, {
      env: psqlEnvironment(options.databaseUrl),
      stdio: [input === null ? "ignore" : "pipe", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.on("error", (error) => reject(new Error(`could not start psql: ${error.message}`)));
    child.on("close", (status) => {
      if (status === 0) resolve(stdout);
      else reject(new Error(`psql exited with status ${status}: ${stderr.trim() || "no error output"}`));
    });
    if (input !== null) child.stdin.end(input);
  });
}

async function readCoveCandidates(options) {
  const limitClause = options.limit === null ? "" : ` LIMIT ${options.limit}`;
  const eligibleQuery = `SELECT "Id" FROM videos
    WHERE "ImageBlobId" IS NULL OR btrim("ImageBlobId") = ''
    ORDER BY "Id"${limitClause}`;
  const sql = `
SELECT 'total|' || count(*)
FROM videos
WHERE "ImageBlobId" IS NULL OR btrim("ImageBlobId") = '';
WITH eligible AS (${eligibleQuery})
SELECT 'id|' || "Id" FROM eligible ORDER BY "Id";
SELECT 'fp|' || v."Id" || '|'
       || CASE WHEN v."ImageBlobId" IS NULL OR btrim(v."ImageBlobId") = '' THEN '0' ELSE '1' END
       || '|' || lower(ff."Type") || '|' || lower(btrim(ff."Value"))
FROM videos v
JOIN files f ON f."VideoId" = v."Id"
JOIN file_fingerprints ff ON ff."FileId" = f."Id"
WHERE lower(ff."Type") IN ('md5', 'oshash')
ORDER BY v."Id", lower(ff."Type"), lower(btrim(ff."Value"));`;
  const stdout = await runPsql(options, [
    "--no-psqlrc",
    "--set=ON_ERROR_STOP=1",
    "--tuples-only",
    "--no-align",
    "--quiet",
    "--command",
    sql,
  ]);

  let total = null;
  const ids = [];
  const fingerprints = new Map();
  const hasCoverByVideo = new Map();
  for (const line of stdout.split(/\r?\n/)) {
    if (!line) continue;
    if (line.startsWith("total|")) {
      total = Number.parseInt(line.slice("total|".length), 10);
    } else if (line.startsWith("id|")) {
      const id = Number.parseInt(line.slice("id|".length), 10);
      if (!Number.isSafeInteger(id) || id < 1) fail(`psql returned an invalid video ID: ${line}`);
      ids.push(id);
    } else if (line.startsWith("fp|")) {
      const [, rawId, covered, type, value] = line.split("|");
      const id = Number.parseInt(rawId, 10);
      if (!Number.isSafeInteger(id) || id < 1 || !["0", "1"].includes(covered)
          || !["md5", "oshash"].includes(type) || !/^[0-9a-f]+$/.test(value)) {
        fail(`psql returned an invalid fingerprint row: ${line}`);
      }
      hasCoverByVideo.set(id, covered === "1");
      const key = `${type}:${value}`;
      let videoIds = fingerprints.get(key);
      if (!videoIds) fingerprints.set(key, videoIds = new Set());
      videoIds.add(id);
    } else {
      fail(`psql returned an unexpected row: ${line}`);
    }
  }
  if (!Number.isSafeInteger(total) || total < 0) fail("psql did not return the eligible video count");
  return { fingerprints, hasCoverByVideo, ids, total };
}

function normalizeStashFingerprint(value) {
  if (value instanceof Uint8Array) return Buffer.from(value).toString("utf8").trim().toLowerCase();
  return String(value ?? "").trim().toLowerCase();
}

function readStashCoverData(stashDbPath) {
  const database = new DatabaseSync(stashDbPath, { readOnly: true });
  try {
    const sceneColumns = new Set(database.prepare("PRAGMA table_info('scenes')").all().map((column) => column.name));
    if (!sceneColumns.has("cover_blob")) fail("the Stash scenes table does not contain cover_blob");

    const sceneFileColumns = new Set(database.prepare("PRAGMA table_info('scenes_files')").all().map((column) => column.name));
    if (!sceneFileColumns.has("scene_id") || !sceneFileColumns.has("file_id")) {
      fail("the Stash database does not contain a compatible scenes_files table");
    }
    const primaryOrder = sceneFileColumns.has("primary")
      ? "coalesce(sf2.[primary], 0) DESC, sf2.file_id"
      : "sf2.file_id";

    const rows = database.prepare(`
SELECT s.id scene_id,
       lower(trim(s.cover_blob)) checksum,
       lower(ff.type) fingerprint_type,
       ff.fingerprint fingerprint_value
FROM scenes s
LEFT JOIN scenes_files sf ON sf.rowid = (
  SELECT sf2.rowid
  FROM scenes_files sf2
  WHERE sf2.scene_id = s.id
  ORDER BY ${primaryOrder}
  LIMIT 1
)
LEFT JOIN files_fingerprints ff
  ON ff.file_id = sf.file_id
 AND lower(ff.type) IN ('md5', 'oshash')
WHERE s.cover_blob IS NOT NULL
ORDER BY s.id, lower(ff.type)`).all();

    const checksums = new Set();
    const scenesById = new Map();
    const invalidSceneIds = new Set();
    for (const row of rows) {
      const checksum = String(row.checksum ?? "");
      if (!md5Pattern.test(checksum)) {
        invalidSceneIds.add(row.scene_id);
        continue;
      }
      checksums.add(checksum);
      let scene = scenesById.get(row.scene_id);
      if (!scene) {
        scene = { checksum, fingerprints: [], sceneId: row.scene_id };
        scenesById.set(row.scene_id, scene);
      }
      if (row.fingerprint_type && row.fingerprint_value !== null) {
        const type = String(row.fingerprint_type).toLowerCase();
        const value = normalizeStashFingerprint(row.fingerprint_value);
        if (["md5", "oshash"].includes(type) && /^[0-9a-f]+$/.test(value)) {
          scene.fingerprints.push(`${type}:${value}`);
        }
      }
    }
    for (const scene of scenesById.values()) scene.fingerprints = [...new Set(scene.fingerprints)];
    return { checksums, invalid: invalidSceneIds.size, scenes: [...scenesById.values()] };
  } finally {
    database.close();
  }
}

async function* walkFiles(root) {
  const directories = [root];
  while (directories.length > 0) {
    const directory = directories.pop();
    const handle = await fs.opendir(directory);
    for await (const entry of handle) {
      const entryPath = path.join(directory, entry.name);
      if (entry.isDirectory()) directories.push(entryPath);
      else if (entry.isFile()) yield entryPath;
    }
  }
}

async function runConcurrent(iterable, concurrency, operation) {
  const iterator = iterable[Symbol.asyncIterator]?.() ?? iterable[Symbol.iterator]();
  const workers = Array.from({ length: concurrency }, async () => {
    while (true) {
      const next = await iterator.next();
      if (next.done) return;
      await operation(next.value);
    }
  });
  await Promise.all(workers);
}

function hashFile(filePath, algorithm = "md5") {
  return new Promise((resolve, reject) => {
    const hash = createHash(algorithm);
    const stream = createReadStream(filePath);
    stream.on("data", (chunk) => hash.update(chunk));
    stream.on("error", reject);
    stream.on("end", () => resolve(hash.digest("hex")));
  });
}

function progress(options, message) {
  if (!options.quiet) process.stderr.write(`${message}\n`);
}

function parseBlobId(filePath) {
  const fileName = path.basename(filePath);
  if (fileName.startsWith(".")) return null;
  return canonicalBlobIdPattern.exec(fileName)?.[1] ?? null;
}

function isCanonicalBlobPath(blobsDirectory, filePath, blobId) {
  const relativePath = path.relative(blobsDirectory, filePath);
  const parts = relativePath.split(path.sep);
  return parts.length === 2
    && parts[0] === blobId.slice(0, 2)
    && path.basename(filePath).startsWith(blobId);
}

async function scanBlobs(options, stashChecksums, blobsDirectory) {
  const matches = new Map();
  const candidatesByBlobId = new Map();
  let payloads = 0;
  let hashed = 0;
  await runConcurrent(walkFiles(blobsDirectory), options.concurrency, async (filePath) => {
    const blobId = parseBlobId(filePath);
    if (blobId === null) return;

    const candidate = {
      blobId,
      canonical: isCanonicalBlobPath(blobsDirectory, filePath, blobId),
      filePath,
    };
    let candidates = candidatesByBlobId.get(blobId);
    if (!candidates) candidatesByBlobId.set(blobId, candidates = []);
    candidates.push(candidate);

    if (!candidate.canonical) return;
    payloads += 1;
    const stats = await fs.stat(filePath);
    candidate.checksum = await hashFile(filePath);
    candidate.mtimeMs = stats.mtimeMs;
    candidate.size = stats.size;
    hashed += 1;
    if (hashed % 5000 === 0) progress(options, `Hashed ${hashed.toLocaleString()} Cove blob payloads...`);
  });

  let duplicatePhysicalIds = 0;
  for (const candidates of candidatesByBlobId.values()) {
    if (candidates.length !== 1) {
      duplicatePhysicalIds += 1;
      continue;
    }
    const candidate = candidates[0];
    if (!candidate.canonical) continue;
    if (!stashChecksums.has(candidate.checksum)) continue;
    let checksumMatches = matches.get(candidate.checksum);
    if (!checksumMatches) matches.set(candidate.checksum, checksumMatches = []);
    checksumMatches.push(candidate);
  }
  return { duplicatePhysicalIds, hashed, matches, payloads };
}

function matchStashScenes(stashScenes, coveFingerprints, selectedVideoIds, hasCoverByVideo) {
  const selected = new Set(selectedVideoIds);
  const videoCandidates = new Map();
  const result = {
    alreadyCovered: 0,
    ambiguousFingerprint: 0,
    conflictingFingerprints: 0,
    duplicateVideoMappings: 0,
    matched: new Map(),
    noCoveFingerprint: 0,
    noStashFingerprint: 0,
    outsideSelection: 0,
  };

  for (const scene of stashScenes) {
    if (scene.fingerprints.length === 0) {
      result.noStashFingerprint += 1;
      continue;
    }
    const coveIdSets = scene.fingerprints.map((fingerprint) => coveFingerprints.get(fingerprint)).filter(Boolean);
    if (coveIdSets.length === 0) {
      result.noCoveFingerprint += 1;
      continue;
    }

    let matchingIds = new Set(coveIdSets[0]);
    for (const ids of coveIdSets.slice(1)) {
      matchingIds = new Set([...matchingIds].filter((id) => ids.has(id)));
    }
    if (matchingIds.size === 0) {
      result.conflictingFingerprints += 1;
      continue;
    }
    if (matchingIds.size > 1) {
      result.ambiguousFingerprint += 1;
      continue;
    }

    const videoId = matchingIds.values().next().value;
    let candidates = videoCandidates.get(videoId);
    if (!candidates) videoCandidates.set(videoId, candidates = []);
    candidates.push(scene);
  }

  for (const [videoId, scenes] of videoCandidates) {
    if (scenes.length !== 1) {
      result.duplicateVideoMappings += 1;
      continue;
    }
    if (hasCoverByVideo.get(videoId) === true) {
      result.alreadyCovered += 1;
      continue;
    }
    if (!selected.has(videoId)) {
      result.outsideSelection += 1;
      continue;
    }
    result.matched.set(videoId, scenes[0]);
  }
  return result;
}

function createPlan(sceneMatches, blobMatches) {
  const plan = [];
  const ambiguousItems = [];
  const missingItems = [];
  let ambiguous = 0;
  let missing = 0;

  for (const [videoId, scene] of sceneMatches) {
    const possibleMatches = blobMatches.get(scene.checksum) ?? [];
    const uniqueMatches = [...new Map(possibleMatches.map((match) => [match.blobId, match])).values()];
    if (uniqueMatches.length === 0) {
      missing += 1;
      missingItems.push({ checksum: scene.checksum, stashSceneId: scene.sceneId, videoId });
      continue;
    }
    if (uniqueMatches.length > 1) {
      ambiguous += 1;
      ambiguousItems.push({
        blobIds: uniqueMatches.map((match) => match.blobId).sort(),
        checksum: scene.checksum,
        stashSceneId: scene.sceneId,
        videoId,
      });
      continue;
    }

    const blob = uniqueMatches[0];
    plan.push({
      blobId: blob.blobId,
      blobMtimeMs: blob.mtimeMs,
      blobPath: blob.filePath,
      checksum: scene.checksum,
      stashSceneId: scene.sceneId,
      videoId,
    });
  }

  plan.sort((first, second) => first.videoId - second.videoId);
  ambiguousItems.sort((first, second) => first.videoId - second.videoId);
  missingItems.sort((first, second) => first.videoId - second.videoId);
  return { ambiguous, ambiguousItems, missing, missingItems, plan };
}

async function verifyPlanFiles(plan) {
  for (const item of plan) {
    const stats = await fs.stat(item.blobPath);
    if (stats.size === 0 || stats.mtimeMs !== item.blobMtimeMs) {
      fail(`blob payload changed while planning; rerun the script: ${item.blobId}`);
    }
  }
}

export function buildApplySql(plan) {
  const copyRows = plan.map((item) => `${item.videoId}\t${item.blobId}`).join("\n");
  return `BEGIN;
CREATE TEMP TABLE cove_stash_cover_backfill (
  video_id integer PRIMARY KEY,
  blob_id text NOT NULL
) ON COMMIT DROP;
COPY cove_stash_cover_backfill (video_id, blob_id) FROM STDIN;
${copyRows}
\\.
DO $cove_backfill$
DECLARE
  expected_count integer;
  updated_count integer;
BEGIN
  SELECT count(*) INTO expected_count FROM cove_stash_cover_backfill;
  UPDATE videos AS v
  SET "ImageBlobId" = b.blob_id,
      "UpdatedAt" = CURRENT_TIMESTAMP
  FROM cove_stash_cover_backfill AS b
  WHERE v."Id" = b.video_id
    AND (v."ImageBlobId" IS NULL OR btrim(v."ImageBlobId") = '');
  GET DIAGNOSTICS updated_count = ROW_COUNT;
  IF updated_count <> expected_count THEN
    RAISE EXCEPTION 'backfill race detected: planned %, updated %', expected_count, updated_count;
  END IF;
END
$cove_backfill$;
SELECT count(*) FROM cove_stash_cover_backfill;
COMMIT;
`;
}

async function applyPlan(options, plan) {
  if (plan.length === 0) return 0;
  await verifyPlanFiles(plan);

  const sql = buildApplySql(plan);
  const stdout = await runPsql(options, [
    "--no-psqlrc",
    "--set=ON_ERROR_STOP=1",
    "--tuples-only",
    "--no-align",
    "--quiet",
    "--file=-",
  ], sql);
  const counts = stdout.split(/\r?\n/).filter((line) => /^\d+$/.test(line));
  const updated = Number.parseInt(counts.at(-1) ?? "", 10);
  if (updated !== plan.length) fail(`psql reported ${updated} updates for a ${plan.length}-row plan`);
  return updated;
}

async function writeReport(reportPath, report) {
  const resolvedPath = path.resolve(reportPath);
  await fs.mkdir(path.dirname(resolvedPath), { recursive: true });
  const temporaryPath = `${resolvedPath}.tmp-${process.pid}`;
  await fs.writeFile(temporaryPath, `${JSON.stringify(report, null, 2)}\n`, { flag: "wx" });
  await fs.rename(temporaryPath, resolvedPath);
}

function printSummary(options, report) {
  console.log(`Mode: ${options.apply ? "APPLY" : "DRY RUN"}`);
  console.log(`Eligible Cove videos: ${report.cove.eligible.toLocaleString()}`);
  if (report.cove.selected !== report.cove.eligible) {
    console.log(`Eligible videos selected by --limit: ${report.cove.selected.toLocaleString()}`);
  }
  console.log(`Stash scenes uniquely matched by file fingerprint: ${report.mapping.matched.toLocaleString()}`);
  console.log(`Stash scene covers already explicit in Cove: ${report.mapping.alreadyCovered.toLocaleString()}`);
  console.log(`Skipped scenes without any matching Cove fingerprint: ${report.mapping.noCoveFingerprint.toLocaleString()}`);
  if (report.mapping.outsideSelection > 0) {
    console.log(`Matched scenes outside the --limit selection: ${report.mapping.outsideSelection.toLocaleString()}`);
  }
  console.log(`Skipped ambiguous/conflicting scene mappings: ${(report.mapping.ambiguousFingerprint + report.mapping.conflictingFingerprints + report.mapping.duplicateVideoMappings).toLocaleString()}`);
  console.log(`Cove blob payloads hashed: ${report.blobs.hashed.toLocaleString()}`);
  console.log(`Disqualified blob IDs with multiple payloads: ${report.blobs.duplicatePhysicalIds.toLocaleString()}`);
  console.log(`Planned video cover backfills: ${report.result.planned.toLocaleString()}`);
  console.log(`Skipped ambiguous blob matches: ${report.result.ambiguous.toLocaleString()}`);
  console.log(`Skipped missing exact blob matches: ${report.result.missing.toLocaleString()}`);
  console.log(`Applied video cover backfills: ${report.result.applied.toLocaleString()}`);

  for (const item of report.plan.slice(0, options.details)) {
    console.log(`  video ${item.videoId} -> blob ${item.blobId} (${item.checksum})`);
  }
  if (report.plan.length > options.details) {
    console.log(`  ... ${report.plan.length - options.details} additional mappings omitted; use --report for the full plan`);
  }

  if (!options.apply) console.log("Dry run only: no database rows or generated files were changed.");
  else console.log("Screenshots and blob payloads were retained; this command only updated Cove database references.");
}

export async function main(args = process.argv.slice(2), environment = process.env) {
  const options = parseArgs(args, environment);
  if (options.help) {
    process.stdout.write(usage());
    return;
  }

  options.stashDbPath = path.resolve(options.stashDbPath);
  options.generatedPath = path.resolve(options.generatedPath);
  const blobsDirectory = path.join(options.generatedPath, "blobs");
  await Promise.all([
    assertFile(options.stashDbPath, "Stash database"),
    assertDirectory(blobsDirectory, "Cove blobs directory"),
  ]);

  const stash = readStashCoverData(options.stashDbPath);
  progress(options, `Loaded ${stash.scenes.length.toLocaleString()} Stash scenes with valid cover checksums.`);
  const cove = await readCoveCandidates(options);
  const mapping = matchStashScenes(stash.scenes, cove.fingerprints, cove.ids, cove.hasCoverByVideo);
  progress(options, `Matched ${mapping.matched.size.toLocaleString()} Stash scenes to eligible Cove videos by imported file fingerprint.`);
  progress(options, `Hashing Cove blobs to resolve ${stash.checksums.size.toLocaleString()} Stash cover checksums...`);
  const blobs = await scanBlobs(options, stash.checksums, blobsDirectory);
  const planned = createPlan(mapping.matched, blobs.matches);

  let applied = 0;
  if (options.apply) applied = await applyPlan(options, planned.plan);

  const report = {
    version: 3,
    mode: options.apply ? "apply" : "dry-run",
    generatedAt: new Date().toISOString(),
    stash: {
      coverChecksums: stash.checksums.size,
      invalidCoverChecksums: stash.invalid,
    },
    cove: {
      eligible: cove.total,
      selected: cove.ids.length,
    },
    mapping: {
      alreadyCovered: mapping.alreadyCovered,
      ambiguousFingerprint: mapping.ambiguousFingerprint,
      conflictingFingerprints: mapping.conflictingFingerprints,
      duplicateVideoMappings: mapping.duplicateVideoMappings,
      matched: mapping.matched.size,
      noCoveFingerprint: mapping.noCoveFingerprint,
      noStashFingerprint: mapping.noStashFingerprint,
      outsideSelection: mapping.outsideSelection,
    },
    blobs: {
      duplicatePhysicalIds: blobs.duplicatePhysicalIds,
      hashed: blobs.hashed,
      payloads: blobs.payloads,
    },
    result: {
      ambiguous: planned.ambiguous,
      applied,
      missing: planned.missing,
      planned: planned.plan.length,
    },
    skipped: {
      ambiguousBlobMatches: planned.ambiguousItems,
      missingBlobMatches: planned.missingItems,
    },
    plan: planned.plan.map((item) => ({
      blobId: item.blobId,
      checksum: item.checksum,
      stashSceneId: item.stashSceneId,
      videoId: item.videoId,
    })),
  };

  if (options.reportPath) await writeReport(options.reportPath, report);
  printSummary(options, report);
}

const isEntryPoint = process.argv[1]
  && path.resolve(fileURLToPath(import.meta.url)) === path.resolve(process.argv[1]);
if (isEntryPoint) {
  main().catch((error) => {
    console.error(`ERROR: ${error.message}`);
    process.exitCode = 1;
  });
}
