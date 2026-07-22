import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const siteRoot = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
const docsRoot = path.join(siteRoot, 'src', 'content', 'docs');

async function listDocs(directory = docsRoot) {
  const entries = await readdir(directory, { withFileTypes: true });
  const paths = await Promise.all(entries.map((entry) => {
    const entryPath = path.join(directory, entry.name);
    return entry.isDirectory() ? listDocs(entryPath) : [entryPath];
  }));

  return paths.flat().filter((entryPath) => /\.mdx?$/.test(entryPath));
}

function jsonCodeBlocks(markdown) {
  const fence = '```';
  return [...markdown.matchAll(
    new RegExp(`${fence}json[\\t ]*\\n([\\s\\S]*?)\\n${fence}`, 'g'),
  )].map((match) => match[1]);
}

test('JSON documentation samples parse', async () => {
  const failures = [];

  for (const filePath of await listDocs()) {
    const content = await readFile(filePath, 'utf8');
    for (const sample of jsonCodeBlocks(content)) {
      try {
        JSON.parse(sample);
      } catch (error) {
        failures.push(`${path.relative(docsRoot, filePath)}: ${error.message}`);
      }
    }
  }

  assert.deepEqual(failures, []);
});
