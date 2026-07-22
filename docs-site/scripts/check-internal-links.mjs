import { readFile, readdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { parse } from 'parse5';

const outputDirectory = path.resolve(process.argv[2] ?? 'dist');
const fallbackSite = 'https://yourcove.net';
const configuredSite = process.env.SITE_URL ?? fallbackSite;
const documentOrigin = new URL(configuredSite).origin;
const [, repositoryName] = (process.env.GITHUB_REPOSITORY ?? '').split('/');
const basePath = configuredSite.includes('github.io') && repositoryName
  ? `/${repositoryName}`
  : '';

async function findHtmlFiles(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = await Promise.all(
    entries.map((entry) => {
      const entryPath = path.join(directory, entry.name);
      return entry.isDirectory()
        ? findHtmlFiles(entryPath)
        : entry.isFile() && entry.name.endsWith('.html')
          ? [entryPath]
          : [];
    }),
  );

  return files.flat();
}

function outputPathToUrl(filePath) {
  const relativePath = path.relative(outputDirectory, filePath).split(path.sep).join('/');
  const route = relativePath === 'index.html'
    ? '/'
    : relativePath.endsWith('/index.html')
      ? `/${relativePath.slice(0, -'index.html'.length)}`
      : `/${relativePath}`;
  return new URL(`${basePath}${route}`, documentOrigin);
}

function targetCandidates(url) {
  const decodedPath = decodeURIComponent(url.pathname);
  const isUnderBase = !basePath || decodedPath === basePath || decodedPath.startsWith(`${basePath}/`);
  if (!isUnderBase) return [];

  const sitePath = decodedPath.slice(basePath.length) || '/';
  const relativePath = sitePath.replace(/^\/+/, '');

  if (sitePath.endsWith('/')) {
    return [path.join(outputDirectory, relativePath, 'index.html')];
  }

  if (path.extname(sitePath)) {
    return [path.join(outputDirectory, relativePath)];
  }

  return [
    path.join(outputDirectory, relativePath),
    path.join(outputDirectory, `${relativePath}.html`),
    path.join(outputDirectory, relativePath, 'index.html'),
  ];
}

async function isFile(filePath) {
  try {
    const target = await stat(filePath);
    return target.isFile();
  } catch (error) {
    if (error.code === 'ENOENT' || error.code === 'ENOTDIR') return false;
    throw error;
  }
}

async function findTargetFile(url) {
  const candidates = targetCandidates(url);
  const matches = await Promise.all(candidates.map(isFile));
  const matchIndex = matches.findIndex(Boolean);
  return matchIndex === -1 ? undefined : candidates[matchIndex];
}

const documentsByFile = new Map();
const identifiersByFile = new Map();

async function getDocument(filePath) {
  if (!documentsByFile.has(filePath)) {
    documentsByFile.set(filePath, readFile(filePath, 'utf8').then((html) => parse(html)));
  }

  return documentsByFile.get(filePath);
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

async function getDocumentIdentifiers(filePath) {
  if (!identifiersByFile.has(filePath)) {
    identifiersByFile.set(filePath, (async () => {
      const document = await getDocument(filePath);
      const identifiers = new Set();

      for (const element of getElements(document)) {
        const id = getAttribute(element, 'id');
        if (id !== undefined) identifiers.add(id);
        if (element.tagName === 'a') {
          const name = getAttribute(element, 'name');
          if (name !== undefined) identifiers.add(name);
        }
      }

      return identifiers;
    })());
  }

  return identifiersByFile.get(filePath);
}

function decodeFragment(hash) {
  try {
    return decodeURIComponent(hash.slice(1));
  } catch {
    return hash.slice(1);
  }
}

const htmlFiles = await findHtmlFiles(outputDirectory);
const failures = [];

for (const htmlFile of htmlFiles) {
  const sourceUrl = outputPathToUrl(htmlFile);
  const document = await getDocument(htmlFile);

  for (const anchor of getElements(document)) {
    if (anchor.tagName !== 'a') continue;
    const href = getAttribute(anchor, 'href');
    if (!href) continue;

    const targetUrl = new URL(href, sourceUrl);
    if (targetUrl.origin !== documentOrigin) continue;

    const targetFile = await findTargetFile(targetUrl);
    if (!targetFile) {
      failures.push({
        source: sourceUrl.pathname,
        href,
        target: targetUrl.pathname,
      });
      continue;
    }

    if (targetUrl.hash && targetFile.endsWith('.html')) {
      const identifiers = await getDocumentIdentifiers(targetFile);
      const fragment = decodeFragment(targetUrl.hash);
      if (!identifiers.has(fragment)) {
        failures.push({
          source: sourceUrl.pathname,
          href,
          target: `${targetUrl.pathname}${targetUrl.hash}`,
        });
      }
    }
  }
}

if (failures.length > 0) {
  console.error(`Found ${failures.length} broken internal link${failures.length === 1 ? '' : 's'}:`);
  for (const failure of failures) {
    console.error(`- ${failure.source}: ${failure.href} -> ${failure.target}`);
  }
  process.exitCode = 1;
} else {
  console.log(`Checked ${htmlFiles.length} HTML files; all internal links resolve.`);
}
