import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { access, readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import test from 'node:test';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

const execFileAsync = promisify(execFile);
const siteRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = join(siteRoot, '..');
const docsRoot = join(siteRoot, 'src', 'content', 'docs', 'docs');
const provenanceScript = join(siteRoot, 'scripts', 'check-compatibility-provenance.mjs');

const sourceSensitivePages = new Map([
  ['developer/api/overview.mdx', { api: true, sdk: false }],
  ['developer/extensions/create-extension.mdx', { api: false, sdk: true }],
  ['developer/extensions/extension-points.mdx', { api: false, sdk: true }],
  ['developer/extensions/overview.mdx', { api: false, sdk: true }],
  ['developer/extensions/packaging.mdx', { api: false, sdk: true }],
  ['developer/extensions/permissions.mdx', { api: false, sdk: true }],
  ['developer/extensions/ui-extension-points.mdx', { api: false, sdk: true }],
  ['user/getting-started/import-existing-library.mdx', { api: false, sdk: false }],
]);

function parseCompatibility(frontmatter, path) {
  const block = frontmatter.match(/^compatibility:\n((?: {2}.*\n?)+)/m)?.[1];
  assert.ok(block, `${path} must declare compatibility metadata`);

  const sources = [...block.matchAll(/^ {4}- (.+)$/gm)].map((match) => match[1].trim());
  return {
    api: /^ {2}api: true$/m.test(block),
    sdk: /^ {2}sdk: true$/m.test(block),
    sources,
  };
}

test('source-sensitive pages declare the rendered compatibility scope and real source files', async () => {
  for (const [relativePath, expected] of sourceSensitivePages) {
    const content = await readFile(join(docsRoot, relativePath), 'utf8');
    const frontmatter = content.match(/^---\n([\s\S]*?)\n---/)?.[1];
    assert.ok(frontmatter, `${relativePath} must have frontmatter`);

    const compatibility = parseCompatibility(frontmatter, relativePath);
    assert.equal(compatibility.api, expected.api, `${relativePath} has the wrong API scope`);
    assert.equal(compatibility.sdk, expected.sdk, `${relativePath} has the wrong SDK scope`);
    assert.ok(compatibility.sources.length > 0, `${relativePath} must link its living source contract`);

    for (const sourcePath of compatibility.sources) {
      assert.match(sourcePath, /^(?:src|ui\/src)\//, `${relativePath} has an invalid source path`);
      assert.doesNotMatch(sourcePath, /[#?]/, `${relativePath} source links must not pin line numbers or queries`);
      await access(join(repositoryRoot, sourcePath));
    }
  }
});

test('provenance check skips unavailable Git metadata for an ordinary source export', async () => {
  const { stdout, stderr } = await execFileAsync(process.execPath, [provenanceScript], {
    cwd: repositoryRoot,
    env: {
      ...process.env,
      COVE_DOCS_REQUIRE_GIT_PROVENANCE: 'false',
      PATH: join(siteRoot, 'scripts', 'git-is-unavailable'),
    },
  });

  assert.match(stdout, /Static compatibility provenance checks passed/);
  assert.match(stderr, /Skipping Git provenance checks/);
});

test('strict provenance mode fails when Git metadata is unavailable', async () => {
  await assert.rejects(
    execFileAsync(process.execPath, [provenanceScript], {
      cwd: repositoryRoot,
      env: {
        ...process.env,
        COVE_DOCS_REQUIRE_GIT_PROVENANCE: 'true',
        PATH: join(siteRoot, 'scripts', 'git-is-unavailable'),
      },
    }),
    (error) => {
      assert.match(error.stderr, /Git provenance is required/);
      return true;
    },
  );
});

test('documentation CI fetches Git history and requires strict provenance', async () => {
  const workflow = await readFile(join(repositoryRoot, '.github/workflows/docs-site.yml'), 'utf8');

  assert.match(workflow, /uses: actions\/checkout@[^\n]+\n\s+with:\n\s+fetch-depth: 0/);
  assert.match(workflow, /COVE_DOCS_REQUIRE_GIT_PROVENANCE:\s*['"]?true['"]?/);
});
