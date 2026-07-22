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

test('documented versions and provenance are deterministic without Git metadata', async () => {
  const compatibilityModule = await import('../src/lib/compatibility.mjs');
  const provenanceSource = await readFile(provenanceScript, 'utf8');
  assert.match(compatibilityModule.COVE_DOCUMENTED_RELEASE_VERSION, /^\d+\.\d+\.\d+$/);
  assert.equal(
    compatibilityModule.COVE_DOCUMENTED_SDK_VERSION,
    compatibilityModule.COVE_DOCUMENTED_RELEASE_VERSION,
  );
  assert.equal(
    compatibilityModule.COVE_SOURCE_VERSION,
    `${compatibilityModule.COVE_DOCUMENTED_RELEASE_VERSION}-dev`,
  );
  assert.equal(compatibilityModule.COVE_SOURCE_REF, 'main');
  assert.equal(
    compatibilityModule.COVE_REVIEWED_SOURCE_REVISION,
    '1ebd0d7251aa9ae2b1f5ea10f344978b03f6819c',
  );
  assert.equal(
    compatibilityModule.COVE_REVIEWED_SOURCE_URL,
    `https://github.com/yourcove/cove/commit/${compatibilityModule.COVE_REVIEWED_SOURCE_REVISION}`,
  );

  const directoryTargets = await readFile(join(repositoryRoot, 'Directory.Build.targets'), 'utf8');
  const releaseWorkflow = await readFile(join(repositoryRoot, '.github/workflows/release.yml'), 'utf8');
  const packageWorkflow = await readFile(
    join(repositoryRoot, '.github/workflows/publish-plugins-package.yml'),
    'utf8',
  );
  const docsWorkflow = await readFile(join(repositoryRoot, '.github/workflows/docs-site.yml'), 'utf8');

  assert.match(directoryTargets, /git describe --tags --abbrev=0/);
  assert.match(directoryTargets, /<Version[^>]*>\$\(_CoveLatestTag\)-dev<\/Version>/);
  assert.match(releaseWorkflow, /VERSION=\$\{GITHUB_REF_NAME#v\}/);
  assert.match(releaseWorkflow, /-p:Version=\$\{\{ env\.VERSION \}\}/);
  assert.match(packageWorkflow, /dotnet pack src\/Cove\.Sdk\/Cove\.Sdk\.csproj/);
  assert.match(packageWorkflow, /-p:Version=\$\{\{ steps\.version\.outputs\.version \}\}/);
  assert.match(packageWorkflow, /-p:PackageVersion=\$\{\{ steps\.version\.outputs\.version \}\}/);
  assert.match(docsWorkflow, /uses: actions\/checkout@v6\n\s+with:\n\s+fetch-depth: 0/);
  assert.match(docsWorkflow, /COVE_DOCS_REQUIRE_GIT_PROVENANCE:\s*['"]?true['"]?/);

  const commitCheck = provenanceSource.indexOf("['cat-file', '-e', `${compatibility.COVE_REVIEWED_SOURCE_REVISION}^{commit}`]");
  const exactTagCheck = provenanceSource.indexOf("['describe', '--tags', '--exact-match', compatibility.COVE_REVIEWED_SOURCE_REVISION]");
  const nearestTagCheck = provenanceSource.indexOf("['describe', '--tags', '--abbrev=0', compatibility.COVE_REVIEWED_SOURCE_REVISION]");
  assert.ok(commitCheck >= 0, 'Strict provenance must first resolve the reviewed source revision');
  assert.ok(exactTagCheck > commitCheck, 'Strict provenance must prove the reviewed revision is untagged');
  assert.ok(nearestTagCheck > exactTagCheck, 'Strict provenance must derive the baseline tag from the reviewed revision');
  assert.doesNotMatch(provenanceSource, /\['describe', '--tags', '--abbrev=0'\](?!,)/);
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

test('rendered scope explains host, SDK, manifest, source-ref, and unversioned API boundaries', async () => {
  const component = await readFile(join(siteRoot, 'src', 'components', 'CompatibilityScope.astro'), 'utf8');
  const pageTitle = await readFile(join(siteRoot, 'src', 'components', 'starlight', 'PageTitle.astro'), 'utf8');
  const extensionOverview = await readFile(join(docsRoot, 'developer/extensions/overview.mdx'), 'utf8');
  const extensionPackaging = await readFile(join(docsRoot, 'developer/extensions/packaging.mdx'), 'utf8');
  const apiOverview = await readFile(join(docsRoot, 'developer/api/overview.mdx'), 'utf8');
  const extensionManager = await readFile(join(repositoryRoot, 'src/Cove.Plugins/ExtensionManager.cs'), 'utf8');
  const extensionsController = await readFile(
    join(repositoryRoot, 'src/Cove.Api/Controllers/ExtensionsController.cs'),
    'utf8',
  );

  assert.match(pageTitle, /CompatibilityScope/);
  assert.match(component, /Documented tagged baseline.*contract details were checked.*source branch.*revision/is);
  assert.match(component, /advance only.*re-audited|not.*automatic.*latest/is);
  assert.match(component, /Cove version.*running application|running.*Cove.*host/is);
  assert.match(component, /Cove\.Sdk version.*compile-time.*package/is);
  assert.match(component, /minCoveVersion.*declared host compatibility floor/is);
  assert.match(component, /supported.*URL.*registry.*enforce/is);
  assert.match(component, /compiled\s+extensions.*startup.*reports.*may still\s+initialize/is);
  assert.match(component, /manifest-only.*bundle.*scraper-pack.*does not report/is);
  assert.match(component, /COVE_SOURCE_REF.*source links/is);
  assert.match(component, /unversioned.*\/api.*no.*compatibility guarantee/is);
  assert.match(extensionOverview, /Cove\.Sdk.*does not set.*minimum Cove host version/is);
  assert.match(extensionOverview, /URL.*registry.*enforce.*minCoveVersion/is);
  assert.match(extensionOverview, /compiled extensions.*startup.*reports.*mismatch.*may still initialize/is);
  assert.match(extensionOverview, /manifest-only.*bundle.*scraper-pack.*does not report/is);
  assert.match(extensionPackaging, /supported.*installers.*enforce.*minCoveVersion/is);
  assert.match(extensionPackaging, /startup discovery.*compiled extension.*reports.*may still initialize/is);
  assert.match(extensionPackaging, /manifest-only.*bundle.*scraper-pack.*does not report/is);
  assert.match(apiOverview, /no stable.*cross-release.*compatibility|no release-independent.*compatibility guarantee/is);
  assert.match(
    extensionsController,
    /if \(!IsCoveVersionCompatible\(manifest\.MinCoveVersion\)\)\s+return BadRequest/s,
  );
  assert.match(
    extensionsController,
    /detail\.Versions\s+\.Where\(v => IsCoveVersionCompatible\(v\.MinCoveVersion\)\)/s,
  );
  assert.match(
    extensionManager,
    /var problems = ValidateDependencies\(\);.*LogWarning.*foreach \(var ext in GetInitializationOrder\(\)\)/s,
  );
  assert.match(
    extensionManager,
    /if \(IsManifestOnlyKind\(manifestFile\.Kind\)\).*continue;.*_extensions\.Add\(ext\)/s,
  );
  assert.match(
    extensionManager,
    /List<DependencyProblem> ValidateDependencies\(\).*foreach \(var ext in _extensions\)/s,
  );
});
