import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { parse } from 'parse5';

const outputDirectory = path.resolve(process.argv[2] ?? 'dist');

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

function outputPathToRoute(filePath) {
  const relativePath = path.relative(outputDirectory, filePath).split(path.sep).join('/');
  if (relativePath === 'index.html') return '/';
  if (relativePath.endsWith('/index.html')) return `/${relativePath.slice(0, -'index.html'.length)}`;
  return `/${relativePath}`;
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

function getPageMetadata(document) {
  let title;
  let description;

  for (const element of getElements(document)) {
    if (element.tagName === 'title' && title === undefined) {
      title = element.childNodes?.map((node) => node.value ?? '').join('').trim();
      continue;
    }

    if (element.tagName === 'meta' && getAttribute(element, 'name') === 'description') {
      description ??= getAttribute(element, 'content')?.trim();
    }
  }

  return { title, description };
}

function collectDuplicates(routesByValue) {
  return [...routesByValue.entries()]
    .filter(([, routes]) => routes.length > 1)
    .sort(([first], [second]) => first.localeCompare(second));
}

const htmlFiles = await findHtmlFiles(outputDirectory);
const routesByTitle = new Map();
const routesByDescription = new Map();
const missing = [];

for (const htmlFile of htmlFiles) {
  const route = outputPathToRoute(htmlFile);
  const { title, description } = getPageMetadata(parse(await readFile(htmlFile, 'utf8')));

  if (!title) missing.push({ route, field: 'title' });
  else routesByTitle.set(title, [...(routesByTitle.get(title) ?? []), route]);

  if (!description) missing.push({ route, field: 'description' });
  else routesByDescription.set(description, [...(routesByDescription.get(description) ?? []), route]);
}

const duplicateTitles = collectDuplicates(routesByTitle);
const duplicateDescriptions = collectDuplicates(routesByDescription);
const failureCount = missing.length + duplicateTitles.length + duplicateDescriptions.length;

if (failureCount > 0) {
  for (const { route, field } of missing) {
    console.error(`- ${route} has no ${field}`);
  }

  for (const [title, routes] of duplicateTitles) {
    console.error(`- title "${title}" is shared by ${routes.join(', ')}`);
  }

  for (const [description, routes] of duplicateDescriptions) {
    console.error(`- description "${description.slice(0, 60)}..." is shared by ${routes.join(', ')}`);
  }

  console.error(
    `Found ${failureCount} page metadata problem${failureCount === 1 ? '' : 's'}; each page needs its own title and description.`,
  );
  process.exitCode = 1;
} else {
  console.log(`Checked ${htmlFiles.length} HTML files; every page has a unique title and description.`);
}
