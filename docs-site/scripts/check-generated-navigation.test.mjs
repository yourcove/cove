import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
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

test('generated navigation checker resolves relative pagers within a repository base', async () => {
  const fixtureRoot = await mkdtemp(path.join(tmpdir(), 'cove-docs-navigation-base-check-'));
  const fixtureDist = path.join(fixtureRoot, 'dist');
  const pageDirectory = path.join(fixtureDist, 'docs', 'page');

  try {
    await mkdir(pageDirectory, { recursive: true });
    await writeFile(path.join(pageDirectory, 'index.html'), `
      <nav aria-label="Documentation">
        <a href="/cove/docs/page/" aria-current="page">Page</a>
      </nav>
      <a href="../other/" rel="prev">Previous Other page</a>
    `);

    const { stdout } = await execFileAsync(process.execPath, [checkerPath, fixtureDist], {
      env: {
        ...process.env,
        SITE_URL: 'https://example.github.io/cove',
        GITHUB_REPOSITORY: 'example/cove',
      },
    });
    assert.match(stdout, /with base \/cove/);
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
});
