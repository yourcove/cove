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

const samples = [
  {
    route: '/docs/',
    title: 'Documentation Home',
    prev: null,
    next: null,
  },
  {
    route: '/docs/user/',
    title: 'User Guide',
    prev: null,
    next: { route: '/docs/user/getting-started/install/', label: 'Install' },
  },
  {
    route: '/docs/user/troubleshooting/',
    title: 'Troubleshooting',
    prev: { route: '/docs/user/admin/backups-migrations-upgrades/', label: 'Backups and Upgrades' },
    next: null,
  },
  {
    route: '/docs/developer/',
    title: 'Developer Guide',
    prev: null,
    next: {
      route: '/docs/developer/getting-started/local-development/',
      label: 'Run Cove Locally',
    },
  },
  {
    route: '/docs/developer/getting-started/local-development/',
    title: 'Run Cove Locally',
    prev: { route: '/docs/developer/', label: 'Developer Guide' },
    next: {
      route: '/docs/developer/extensions/create-extension/',
      label: 'Create an Extension',
    },
  },
  {
    route: '/docs/developer/extensions/create-downloader/',
    title: 'Create a Downloader',
    prev: {
      route: '/docs/developer/extensions/create-scraper/',
      label: 'Create a Scraper',
    },
    next: { route: '/docs/developer/extensions/overview/', label: 'Architecture' },
  },
  {
    route: '/docs/developer/contributing/website/',
    title: 'Documentation Style Guide',
    prev: { route: '/docs/developer/api/overview/', label: 'API Surface' },
    next: null,
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

function getText(node) {
  if (node.nodeName === '#text') return node.value;
  return (node.childNodes ?? []).map(getText).join('');
}

function normalizedText(element) {
  return getText(element).replace(/\s+/g, ' ').trim();
}

function withBase(route) {
  return `${basePath}${route}`;
}

function routeFile(route) {
  return path.join(outputDirectory, route.replace(/^\/+|\/+$/g, ''), 'index.html');
}

function assertPagerLink(anchors, relation, expected, title) {
  const matching = anchors.filter((anchor) => getAttribute(anchor, 'rel') === relation);
  if (expected === null) {
    assert.equal(matching.length, 0, `${title} must not render a ${relation} link`);
    return;
  }

  assert.equal(matching.length, 1, `${title} must render exactly one ${relation} link`);
  const anchor = matching[0];
  assert.equal(
    getAttribute(anchor, 'href'),
    withBase(expected.route),
    `${title} ${relation} href must match its route and the active site base`,
  );
  assert.match(
    normalizedText(anchor),
    new RegExp(`\\b${expected.label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`),
    `${title} ${relation} link must be labeled ${expected.label}`,
  );
}

for (const sample of samples) {
  const html = await readFile(routeFile(sample.route), 'utf8');
  const document = parse(html);
  const elements = [...getElements(document)];
  const mainNavigation = elements.find(
    (element) => element.tagName === 'nav' && getAttribute(element, 'aria-label') === 'Main',
  );
  assert.ok(mainNavigation, `${sample.title} must render the main navigation`);

  const currentLinks = [...getElements(mainNavigation)].filter(
    (element) => element.tagName === 'a' && getAttribute(element, 'aria-current') === 'page',
  );
  assert.equal(currentLinks.length, 1, `${sample.title} must have exactly one current sidebar link`);
  assert.equal(
    getAttribute(currentLinks[0], 'href'),
    withBase(sample.route),
    `${sample.title} current sidebar href must match its route and the active site base`,
  );

  const anchors = elements.filter((element) => element.tagName === 'a');
  assertPagerLink(anchors, 'prev', sample.prev, sample.title);
  assertPagerLink(anchors, 'next', sample.next, sample.title);
}

console.log(
  `Checked generated navigation on ${samples.length} pages with base ${basePath || '/'}; all sidebar and pager links match.`,
);
