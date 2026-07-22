import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { access, mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { parse } from 'parse5';

const execFileAsync = promisify(execFile);
const scriptsDirectory = path.dirname(fileURLToPath(import.meta.url));
const siteRoot = path.join(scriptsDirectory, '..');
const astroCli = path.join(siteRoot, 'node_modules', 'astro', 'bin', 'astro.mjs');
const productionOrigin = 'https://yourcove.net';

function* getElements(node) {
  if (node.tagName) yield node;
  for (const child of node.childNodes ?? []) {
    yield* getElements(child);
  }
}

function getAttribute(element, name) {
  return element.attrs?.find((attribute) => attribute.name === name)?.value;
}

function findElements(document, tagName, attributes) {
  return [...getElements(document)].filter(
    (element) => element.tagName === tagName
      && Object.entries(attributes).every(([name, value]) => getAttribute(element, name) === value),
  );
}

async function listFiles(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];

  for (const entry of entries) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...await listFiles(entryPath));
    } else {
      files.push(entryPath);
    }
  }

  return files;
}

async function pathExists(filePath) {
  try {
    await access(filePath);
    return true;
  } catch {
    return false;
  }
}

async function buildSite(environment) {
  const fixtureRoot = await mkdtemp(path.join(siteRoot, '.deployment-test-'));
  const outputDirectory = path.join(fixtureRoot, 'dist');
  const buildEnvironment = { ...process.env };

  delete buildEnvironment.GITHUB_REPOSITORY;
  delete buildEnvironment.SITE_URL;
  delete buildEnvironment.COVE_DOCS_DEPLOYMENT;

  try {
    await execFileAsync(
      process.execPath,
      [astroCli, 'build', '--outDir', outputDirectory],
      {
        cwd: siteRoot,
        env: {
          ...buildEnvironment,
          ASTRO_TELEMETRY_DISABLED: '1',
          COVE_DOCS_REQUIRE_GIT_PROVENANCE: 'false',
          ...environment,
        },
      },
    );
  } catch (error) {
    await rm(fixtureRoot, { recursive: true, force: true });
    throw error;
  }

  return { fixtureRoot, outputDirectory };
}

async function readPage(outputDirectory, route) {
  const html = await readFile(
    path.join(outputDirectory, route.replace(/^\/+|\/+$/g, ''), 'index.html'),
    'utf8',
  );
  return { document: parse(html), html };
}

function assertRobotsMeta(document, expectedContent) {
  const robots = findElements(document, 'meta', { name: 'robots' });
  assert.equal(robots.length, 1, 'Page must have exactly one robots meta tag');
  assert.equal(getAttribute(robots[0], 'content'), expectedContent);
}

function assertCanonical(document, expectedHref) {
  const canonical = findElements(document, 'link', { rel: 'canonical' });
  assert.equal(canonical.length, 1, 'Page must have exactly one canonical link');
  assert.equal(getAttribute(canonical[0], 'href'), expectedHref);
}

function assertSitemapLink(document, expectedHref) {
  const sitemap = findElements(document, 'link', { rel: 'sitemap' });
  if (expectedHref === null) {
    assert.equal(sitemap.length, 0, 'Preview pages must not advertise a sitemap');
  } else {
    assert.equal(sitemap.length, 1, 'Production pages must advertise exactly one sitemap');
    assert.equal(getAttribute(sitemap[0], 'href'), expectedHref);
  }
}

function assertPreviewNotice(document, expected) {
  const notices = findElements(document, 'aside', { 'data-preview-notice': '' });
  assert.equal(
    notices.length,
    expected ? 1 : 0,
    `Page must ${expected ? '' : 'not '}render the preview notice`,
  );
  if (expected) {
    assert.ok(
      getAttribute(notices[0], 'aria-label')?.trim(),
      'Preview notice must have an accessible label',
    );
  }
}

test('invalid deployment modes fail instead of changing crawler behavior silently', async () => {
  await assert.rejects(
    buildSite({ COVE_DOCS_DEPLOYMENT: 'prod' }),
    (error) => {
      assert.match(error.stderr, /must be "preview" or "production"/);
      return true;
    },
  );
});

test('the default preview output is visibly non-production and blocks indexing', async () => {
  const previewOrigin = 'https://preview.invalid';
  const { fixtureRoot, outputDirectory } = await buildSite({
    SITE_URL: previewOrigin,
  });

  try {
    for (const route of ['/', '/docs/']) {
      const { document } = await readPage(outputDirectory, route);
      assertRobotsMeta(document, 'noindex, nofollow, noarchive');
      assertCanonical(document, `${productionOrigin}${route}`);
      assertSitemapLink(document, null);
      assertPreviewNotice(document, true);
    }

    const robots = await readFile(path.join(outputDirectory, 'robots.txt'), 'utf8');
    assert.equal(robots, 'User-agent: *\nDisallow: /\n');
    assert.equal(await pathExists(path.join(outputDirectory, 'sitemap-index.xml')), false);

    const generatedFiles = await listFiles(outputDirectory);
    const crawlerFacingFiles = generatedFiles.filter(
      (filePath) => filePath.endsWith('.html') || path.basename(filePath) === 'robots.txt',
    );
    assert.ok(crawlerFacingFiles.length > 1, 'Preview build must contain crawler-facing output');

    for (const filePath of crawlerFacingFiles) {
      const contents = await readFile(filePath, 'utf8');
      assert.doesNotMatch(
        contents,
        new RegExp(previewOrigin.replaceAll('.', '\\.')),
        `${path.relative(outputDirectory, filePath)} must not expose the preview origin`,
      );
    }
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

test('production output remains indexable with canonical and sitemap metadata', async () => {
  const siteUrl = 'https://docs.example.test';
  const { fixtureRoot, outputDirectory } = await buildSite({
    COVE_DOCS_DEPLOYMENT: 'production',
    SITE_URL: siteUrl,
  });

  try {
    for (const route of ['/', '/docs/']) {
      const { document } = await readPage(outputDirectory, route);
      assertRobotsMeta(
        document,
        'index, follow, max-image-preview:large, max-snippet:-1, max-video-preview:-1',
      );
      assertCanonical(document, `${siteUrl}${route}`);
      assertSitemapLink(document, `${siteUrl}/sitemap-index.xml`);
      assertPreviewNotice(document, false);
    }

    const robots = await readFile(path.join(outputDirectory, 'robots.txt'), 'utf8');
    assert.equal(
      robots,
      `User-agent: *\nAllow: /\n\nSitemap: ${siteUrl}/sitemap-index.xml\n`,
    );
    assert.equal(await pathExists(path.join(outputDirectory, 'sitemap-index.xml')), true);
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});

test('production crawler URLs preserve a GitHub Pages repository base', async () => {
  const siteUrl = 'https://example.github.io/repository';
  const { fixtureRoot, outputDirectory } = await buildSite({
    COVE_DOCS_DEPLOYMENT: 'production',
    GITHUB_REPOSITORY: 'example/repository',
    SITE_URL: siteUrl,
  });

  try {
    for (const route of ['/', '/docs/']) {
      const { document } = await readPage(outputDirectory, route);
      assertRobotsMeta(
        document,
        'index, follow, max-image-preview:large, max-snippet:-1, max-video-preview:-1',
      );
      assertCanonical(document, `${siteUrl}${route}`);
      assertSitemapLink(document, `${siteUrl}/sitemap-index.xml`);
      assertPreviewNotice(document, false);
    }

    const robots = await readFile(path.join(outputDirectory, 'robots.txt'), 'utf8');
    assert.equal(
      robots,
      `User-agent: *\nAllow: /\n\nSitemap: ${siteUrl}/sitemap-index.xml\n`,
    );

    const sitemapIndex = await readFile(path.join(outputDirectory, 'sitemap-index.xml'), 'utf8');
    const sitemapLocations = [...sitemapIndex.matchAll(/<loc>([^<]+)<\/loc>/g)]
      .map((match) => match[1]);
    assert.ok(sitemapLocations.length > 0, 'Sitemap index must contain at least one sitemap');

    const contentLocations = [];
    for (const sitemapLocation of sitemapLocations) {
      const sitemapUrl = new URL(sitemapLocation);
      assert.equal(sitemapUrl.origin, 'https://example.github.io');
      assert.equal(
        sitemapUrl.pathname.match(/\/repository(?=\/)/g)?.length,
        1,
        `${sitemapLocation} must contain the repository base exactly once`,
      );

      const sitemap = await readFile(path.join(outputDirectory, path.basename(sitemapUrl.pathname)), 'utf8');
      contentLocations.push(
        ...[...sitemap.matchAll(/<loc>([^<]+)<\/loc>/g)].map((match) => match[1]),
      );
    }

    assert.ok(contentLocations.length > 0, 'Generated sitemaps must contain page locations');
    for (const contentLocation of contentLocations) {
      const contentUrl = new URL(contentLocation);
      assert.equal(contentUrl.origin, 'https://example.github.io');
      assert.equal(
        contentUrl.pathname.match(/\/repository(?=\/)/g)?.length,
        1,
        `${contentLocation} must contain the repository base exactly once`,
      );
    }
    assert.ok(contentLocations.includes(`${siteUrl}/`));
    assert.ok(contentLocations.includes(`${siteUrl}/docs/`));
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});
