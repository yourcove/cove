import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { parse } from 'parse5';

const outputDirectory = path.resolve(process.argv[2] ?? 'dist');
const configuredSite = process.env.SITE_URL ?? 'https://yourcove.net';
const [, repositoryName] = (process.env.GITHUB_REPOSITORY ?? '').split('/');
const basePath = configuredSite.includes('github.io') && repositoryName
  ? `/${repositoryName}`
  : '';

const screenshots = [
  {
    route: '/docs/user/getting-started/first-scan/',
    asset: 'images/docs/first-scan-options.png',
    width: 1128,
    height: 638,
  },
  {
    route: '/docs/user/library/search-and-filters/',
    asset: 'images/docs/search-filter-controls.png',
    width: 1384,
    height: 828,
  },
  {
    route: '/docs/user/security/users-roles-permissions/',
    asset: 'images/docs/role-permissions.png',
    width: 1128,
    height: 782,
  },
  {
    route: '/docs/developer/extensions/create-extension/',
    asset: 'images/docs/install-extension-from-url.png',
    width: 1128,
    height: 337,
  },
  {
    route: '/docs/user/metadata/providers-scrapers-downloaders/',
    asset: 'images/docs/metadata-server-configuration.png',
    width: 730,
    height: 390,
  },
];

function* getElements(node) {
  if (node.tagName) yield node;
  for (const child of node.childNodes ?? []) {
    yield* getElements(child);
  }
}

function getAttribute(element, name) {
  return element.attrs?.find((attribute) => attribute.name === name)?.value;
}

function routeFile(route) {
  return path.join(outputDirectory, route.replace(/^\/+|\/+$/g, ''), 'index.html');
}

for (const screenshot of screenshots) {
  const html = await readFile(routeFile(screenshot.route), 'utf8');
  const document = parse(html);
  const expectedSource = `${basePath}/${screenshot.asset}`;
  const image = [...getElements(document)].find(
    (element) => element.tagName === 'img' && getAttribute(element, 'src') === expectedSource,
  );

  assert.ok(image, `${screenshot.route} must render ${expectedSource}`);
  assert.equal(getAttribute(image, 'width'), String(screenshot.width));
  assert.equal(getAttribute(image, 'height'), String(screenshot.height));
  assert.equal(getAttribute(image, 'loading'), 'lazy');
  assert.equal(getAttribute(image, 'decoding'), 'async');
}

console.log(
  `Checked ${screenshots.length} generated documentation screenshots with base ${basePath || '/'}; all image attributes match.`,
);
