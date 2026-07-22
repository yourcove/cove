import assert from 'node:assert/strict';
import { readFile, readdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { parse } from 'parse5';

const outputDirectory = path.resolve(process.argv[2] ?? 'dist');
const configuredSite = process.env.SITE_URL ?? 'https://yourcove.net';
const [, repositoryName] = (process.env.GITHUB_REPOSITORY ?? '').split('/');
const basePath = configuredSite.includes('github.io') && repositoryName
  ? `/${repositoryName}`
  : '';

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

function hasClass(element, name) {
  return getAttribute(element, 'class')?.split(/\s+/).includes(name) ?? false;
}

function closestElement(element, tagName) {
  let current = element.parentNode;
  while (current) {
    if (current.tagName === tagName) return current;
    current = current.parentNode;
  }
  return undefined;
}

function readPngDimensions(buffer) {
  const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  assert.ok(buffer.subarray(0, 8).equals(signature), 'PNG asset must have a valid signature');
  assert.equal(buffer.subarray(12, 16).toString('ascii'), 'IHDR', 'PNG asset must contain an IHDR header');
  return {
    width: buffer.readUInt32BE(16),
    height: buffer.readUInt32BE(20),
  };
}

function assetPathFor(source) {
  const sourceUrl = new URL(source, configuredSite);
  assert.equal(sourceUrl.origin, new URL(configuredSite).origin, `${source} must use the site origin`);
  const sourcePath = decodeURIComponent(sourceUrl.pathname);
  const isUnderBase = !basePath || sourcePath.startsWith(`${basePath}/`);
  assert.ok(isUnderBase, `${source} must include the active site base ${basePath || '/'}`);

  const relativePath = sourcePath.slice(basePath.length).replace(/^\/+/, '');
  const assetPath = path.resolve(outputDirectory, relativePath);
  assert.ok(
    assetPath.startsWith(`${outputDirectory}${path.sep}`),
    `${source} must resolve inside the generated site`,
  );
  return assetPath;
}

function isDocumentationScreenshot(image) {
  const figure = closestElement(image, 'figure');
  if (figure && hasClass(figure, 'docs-screenshot')) return true;

  const source = getAttribute(image, 'src');
  if (!source) return false;
  return decodeURIComponent(new URL(source, configuredSite).pathname).includes('/images/docs/');
}

let screenshotCount = 0;

for (const htmlFile of await findHtmlFiles(outputDirectory)) {
  const document = parse(await readFile(htmlFile, 'utf8'));
  const elements = [...getElements(document)];
  const images = elements.filter(
    (element) => element.tagName === 'img' && isDocumentationScreenshot(element),
  );

  for (const image of images) {
    screenshotCount += 1;
    const figure = closestElement(image, 'figure');
    assert.ok(figure, `${htmlFile} documentation screenshot must render inside a figure`);
    assert.ok(hasClass(figure, 'docs-screenshot'), `${htmlFile} screenshot figure must keep its layout class`);

    const source = getAttribute(image, 'src');
    const alt = getAttribute(image, 'alt');
    const width = getAttribute(image, 'width');
    const height = getAttribute(image, 'height');

    assert.ok(source, `${htmlFile} documentation screenshot must have a source`);
    assert.ok(alt?.trim(), `${source} must have non-empty alternative text`);
    assert.match(width ?? '', /^[1-9]\d*$/, `${source} must declare a positive intrinsic width`);
    assert.match(height ?? '', /^[1-9]\d*$/, `${source} must declare a positive intrinsic height`);
    assert.equal(getAttribute(image, 'loading'), 'lazy', `${source} must load lazily`);
    assert.equal(getAttribute(image, 'decoding'), 'async', `${source} must decode asynchronously`);
    assert.match(
      getAttribute(figure, 'style') ?? '',
      new RegExp(`--docs-screenshot-max-width:\\s*${width}px`),
      `${source} must use its intrinsic width as the layout maximum`,
    );

    const assetPath = assetPathFor(source);
    assert.equal((await stat(assetPath)).isFile(), true, `${source} must resolve to a generated asset`);

    if (path.extname(assetPath).toLowerCase() === '.png') {
      const dimensions = readPngDimensions(await readFile(assetPath));
      assert.equal(dimensions.width, Number(width), `${source} PNG width must match its HTML width`);
      assert.equal(dimensions.height, Number(height), `${source} PNG height must match its HTML height`);
    }
  }

  if (images.length > 0) {
    const generatedStyles = elements
      .filter((element) => element.tagName === 'style')
      .map(getText)
      .join('\n');
    assert.match(
      generatedStyles,
      /\.docs-screenshot[^\{]*\{(?=[^}]*\bwidth:100%)(?=[^}]*max-width:var\(--docs-screenshot-max-width\))[^}]*\}/,
      `${htmlFile} must consume the screenshot width variable in generated CSS`,
    );
    assert.match(
      generatedStyles,
      /\.docs-screenshot[^\{]*img[^\{]*\{(?=[^}]*\bwidth:100%)(?=[^}]*\bheight:auto)[^}]*\}/,
      `${htmlFile} must keep screenshot images responsive in generated CSS`,
    );
  }
}

console.log(
  `Checked ${screenshotCount} generated documentation screenshot${screenshotCount === 1 ? '' : 's'} with base ${basePath || '/'}; all assets and image attributes are valid.`,
);
