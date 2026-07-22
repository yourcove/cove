import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const execFileAsync = promisify(execFile);
const scriptsDirectory = path.dirname(fileURLToPath(import.meta.url));
const siteRoot = path.join(scriptsDirectory, '..');
const checkerPath = path.join(scriptsDirectory, 'check-generated-navigation.mjs');

test('generated navigation checker rejects an external pager link with a documentation path', async () => {
  const fixtureRoot = await mkdtemp(path.join(tmpdir(), 'cove-docs-navigation-check-'));
  const fixtureDist = path.join(fixtureRoot, 'dist');

  try {
    await cp(path.join(siteRoot, 'dist'), fixtureDist, { recursive: true });
    const developerPage = path.join(fixtureDist, 'docs', 'developer', 'index.html');
    const original = await readFile(developerPage, 'utf8');
    const mutated = original.replace(
      /<a href="[^"]+" rel="next"/,
      '<a href="https://attacker.example/docs/terminology/" rel="next"',
    );
    assert.notEqual(mutated, original, 'Fixture must contain a developer next link to mutate');
    await writeFile(developerPage, mutated);

    await assert.rejects(
      execFileAsync(process.execPath, [checkerPath, fixtureDist], { env: process.env }),
      (error) => {
        assert.match(error.stderr, /next href must use the documentation site origin/);
        return true;
      },
    );
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});
