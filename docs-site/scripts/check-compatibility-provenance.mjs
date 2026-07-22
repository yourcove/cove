import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

const execFileAsync = promisify(execFile);
const siteRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = join(siteRoot, '..');
const requireGitProvenance = process.env.COVE_DOCS_REQUIRE_GIT_PROVENANCE === 'true';

const compatibility = await import('../src/lib/compatibility.mjs');

async function checkStaticConfiguration() {
  assert.match(compatibility.COVE_DOCUMENTED_RELEASE_VERSION, /^\d+\.\d+\.\d+$/);
  assert.equal(
    compatibility.COVE_DOCUMENTED_SDK_VERSION,
    compatibility.COVE_DOCUMENTED_RELEASE_VERSION,
  );
  assert.equal(
    compatibility.COVE_SOURCE_VERSION,
    `${compatibility.COVE_DOCUMENTED_RELEASE_VERSION}-dev`,
  );
  assert.match(compatibility.COVE_REVIEWED_SOURCE_REVISION, /^[0-9a-f]{40}$/);
  assert.equal(compatibility.COVE_SOURCE_REF, 'main');

  const directoryTargets = await readFile(join(repositoryRoot, 'Directory.Build.targets'), 'utf8');
  const releaseWorkflow = await readFile(join(repositoryRoot, '.github/workflows/release.yml'), 'utf8');
  const packageWorkflow = await readFile(
    join(repositoryRoot, '.github/workflows/publish-plugins-package.yml'),
    'utf8',
  );

  assert.match(directoryTargets, /git describe --tags --abbrev=0/);
  assert.match(directoryTargets, /<Version[^>]*>\$\(_CoveLatestTag\)-dev<\/Version>/);
  assert.match(releaseWorkflow, /VERSION=\$\{GITHUB_REF_NAME#v\}/);
  assert.match(releaseWorkflow, /-p:Version=\$\{\{ env\.VERSION \}\}/);
  assert.match(packageWorkflow, /dotnet pack src\/Cove\.Sdk\/Cove\.Sdk\.csproj/);
  assert.match(packageWorkflow, /-p:Version=\$\{\{ steps\.version\.outputs\.version \}\}/);
  assert.match(packageWorkflow, /-p:PackageVersion=\$\{\{ steps\.version\.outputs\.version \}\}/);
}

function gitUnavailableMessage(error) {
  const detail = error?.stderr?.trim() || error?.message || String(error);
  return `Git metadata is unavailable: ${detail}`;
}

async function skipOrThrowGitUnavailable(error) {
  const message = gitUnavailableMessage(error);
  if (requireGitProvenance) {
    throw new Error(`Git provenance is required but could not be verified. ${message}`);
  }

  console.warn(`Skipping Git provenance checks. ${message}`);
  return false;
}

async function checkGitProvenance() {
  try {
    await execFileAsync(
      'git',
      ['cat-file', '-e', `${compatibility.COVE_REVIEWED_SOURCE_REVISION}^{commit}`],
      { cwd: repositoryRoot },
    );
  } catch (error) {
    return skipOrThrowGitUnavailable(error);
  }

  let exactTag;
  try {
    ({ stdout: exactTag } = await execFileAsync(
      'git',
      ['describe', '--tags', '--exact-match', compatibility.COVE_REVIEWED_SOURCE_REVISION],
      { cwd: repositoryRoot },
    ));
  } catch (error) {
    if (!/no tag exactly matches/i.test(error?.stderr ?? '')) {
      return skipOrThrowGitUnavailable(error);
    }
  }
  assert.equal(
    exactTag,
    undefined,
    `The reviewed source revision must be untagged, but Git resolved exact tag ${exactTag?.trim()}.`,
  );

  let nearestTag;
  try {
    ({ stdout: nearestTag } = await execFileAsync(
      'git',
      ['describe', '--tags', '--abbrev=0', compatibility.COVE_REVIEWED_SOURCE_REVISION],
      { cwd: repositoryRoot },
    ));
  } catch (error) {
    return skipOrThrowGitUnavailable(error);
  }

  const tagVersion = nearestTag.trim().replace(/^v+/, '');
  assert.equal(
    compatibility.COVE_DOCUMENTED_RELEASE_VERSION,
    tagVersion,
    'The documented Cove baseline must match the reviewed revision\'s nearest tag.',
  );
  assert.equal(compatibility.COVE_DOCUMENTED_SDK_VERSION, tagVersion);
  assert.equal(compatibility.COVE_SOURCE_VERSION, `${tagVersion}-dev`);

  console.log(
    `Git compatibility provenance verified for untagged ${compatibility.COVE_REVIEWED_SOURCE_REVISION} from nearest tag ${nearestTag.trim()}.`,
  );
  return true;
}

try {
  await checkStaticConfiguration();
  console.log('Static compatibility provenance checks passed.');
  await checkGitProvenance();
} catch (error) {
  console.error(error instanceof Error ? error.message : error);
  process.exitCode = 1;
}
