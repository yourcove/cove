import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { parse } from 'parse5';

const outputDirectory = path.resolve(process.argv[2] ?? 'dist');
const configuredSite = process.env.SITE_URL ?? 'https://yourcove.net';
const [, repositoryName] = (process.env.GITHUB_REPOSITORY ?? '').split('/');
const basePath = configuredSite.includes('github.io') && repositoryName
  ? `/${repositoryName}`
  : '';
const siteOrigin = new URL(configuredSite).origin;
const docsDirectory = path.join(outputDirectory, 'docs');

async function findHtmlFiles(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = await Promise.all(entries.map((entry) => {
    const entryPath = path.join(directory, entry.name);
    return entry.isDirectory()
      ? findHtmlFiles(entryPath)
      : entry.isFile() && entry.name.endsWith('.html')
        ? [entryPath]
        : [];
  }));

  return files.flat();
}

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

function routeFor(filePath) {
  const relativePath = path.relative(outputDirectory, filePath).split(path.sep).join('/');
  return relativePath.endsWith('/index.html')
    ? `/${relativePath.slice(0, -'index.html'.length)}`
    : `/${relativePath}`;
}

function withBase(route) {
  return `${basePath}${route}`;
}

function closestElement(element, tagName) {
  let current = element.parentNode;
  while (current) {
    if (current.tagName === tagName) return current;
    current = current.parentNode;
  }
  return undefined;
}

function assertPagerLinks(elements, route) {
  for (const relation of ['prev', 'next']) {
    const matching = elements.filter(
      (element) => element.tagName === 'a' && getAttribute(element, 'rel') === relation,
    );
    assert.ok(matching.length <= 1, `${route} must render at most one ${relation} link`);

    if (matching.length === 0) continue;
    const href = getAttribute(matching[0], 'href');
    const label = getText(matching[0]).replace(/\s+/g, ' ').trim();
    assert.ok(label, `${route} ${relation} link must have an accessible label`);
    assert.ok(href, `${route} ${relation} link must have an href`);

    const target = new URL(href, configuredSite);
    assert.equal(
      target.origin,
      siteOrigin,
      `${route} ${relation} href must use the documentation site origin`,
    );
    assert.ok(
      target.pathname.startsWith(`${basePath}/docs/`),
      `${route} ${relation} href must stay within the documentation and active site base`,
    );
  }
}

const htmlFiles = await findHtmlFiles(docsDirectory);

for (const htmlFile of htmlFiles) {
  const route = routeFor(htmlFile);
  const document = parse(await readFile(htmlFile, 'utf8'));
  const elements = [...getElements(document)];
  const currentLinks = elements.filter(
    (element) => element.tagName === 'a' && getAttribute(element, 'aria-current') === 'page',
  );
  assert.equal(currentLinks.length, 1, `${route} must have exactly one current sidebar link`);
  assert.equal(
    getAttribute(currentLinks[0], 'href'),
    withBase(route),
    `${route} current sidebar href must match the generated route and active site base`,
  );

  const currentNavigation = closestElement(currentLinks[0], 'nav');
  assert.ok(currentNavigation, `${route} current link must belong to a navigation landmark`);
  assert.ok(
    getAttribute(currentNavigation, 'aria-label')?.trim(),
    `${route} current navigation must have an accessible label`,
  );

  assertPagerLinks(elements, route);
}

console.log(
  `Checked generated navigation on ${htmlFiles.length} documentation pages with base ${basePath || '/'}; all sidebar and pager links are structurally valid.`,
);
