import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const execFileAsync = promisify(execFile);
const scriptsDirectory = path.dirname(fileURLToPath(import.meta.url));
const checkerPath = path.join(scriptsDirectory, 'check-internal-links.mjs');

async function withFixture(run) {
  const fixtureDirectory = await mkdtemp(path.join(tmpdir(), 'cove-docs-link-check-'));
  try {
    await run(fixtureDirectory);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
}

async function runChecker(fixtureDirectory, environment = {}) {
  const checkerEnvironment = { ...process.env };
  delete checkerEnvironment.GITHUB_REPOSITORY;
  delete checkerEnvironment.SITE_URL;

  return execFileAsync(process.execPath, [checkerPath, fixtureDirectory], {
    env: {
      ...checkerEnvironment,
      ...environment,
    },
  });
}

test('rejects an extensionless target backed only by a directory', async () => {
  await withFixture(async (fixtureDirectory) => {
    await writeFile(
      path.join(fixtureDirectory, 'index.html'),
      '<a href="/missing-page">Missing page</a>',
    );
    await mkdir(path.join(fixtureDirectory, 'missing-page'));

    await assert.rejects(
      runChecker(fixtureDirectory),
      (error) => {
        assert.match(error.stderr, /Found 1 broken internal link:/);
        assert.match(error.stderr, /\/missing-page/);
        return true;
      },
    );
  });
});

test('rejects missing same-page and cross-page fragments', async () => {
  await withFixture(async (fixtureDirectory) => {
    await writeFile(
      path.join(fixtureDirectory, 'index.html'),
      '<a href="#missing-local">Local</a><a href="/other/#missing-cross-page">Cross-page</a>',
    );
    await mkdir(path.join(fixtureDirectory, 'other'));
    await writeFile(path.join(fixtureDirectory, 'other', 'index.html'), '<h2 id="present">Present</h2>');

    await assert.rejects(
      runChecker(fixtureDirectory),
      (error) => {
        assert.match(error.stderr, /Found 2 broken internal links:/);
        assert.match(error.stderr, /#missing-local/);
        assert.match(error.stderr, /#missing-cross-page/);
        return true;
      },
    );
  });
});

test('accepts existing same-page and cross-page fragments', async () => {
  await withFixture(async (fixtureDirectory) => {
    await writeFile(
      path.join(fixtureDirectory, 'index.html'),
      '<h2 id="local">Local</h2><a href="#local">Local link</a><a href="/other/#cross-page">Cross-page link</a>',
    );
    await mkdir(path.join(fixtureDirectory, 'other'));
    await writeFile(path.join(fixtureDirectory, 'other', 'index.html'), '<a name="cross-page">Cross-page</a>');

    const result = await runChecker(fixtureDirectory);
    assert.match(result.stdout, /all internal links resolve/);
  });
});

test('ignores data-href attributes', async () => {
  await withFixture(async (fixtureDirectory) => {
    await writeFile(
      path.join(fixtureDirectory, 'index.html'),
      '<a data-href="/missing-page">Not a link</a>',
    );

    const result = await runChecker(fixtureDirectory);
    assert.match(result.stdout, /all internal links resolve/);
  });
});

test('does not treat data-id or data-name as fragment identifiers', async () => {
  await withFixture(async (fixtureDirectory) => {
    await writeFile(
      path.join(fixtureDirectory, 'index.html'),
      '<div data-id="missing-id"></div><a data-name="missing-name"></a><a href="#missing-id">ID</a><a href="#missing-name">Name</a>',
    );

    await assert.rejects(
      runChecker(fixtureDirectory),
      (error) => {
        assert.match(error.stderr, /Found 2 broken internal links:/);
        assert.match(error.stderr, /#missing-id/);
        assert.match(error.stderr, /#missing-name/);
        return true;
      },
    );
  });
});

test('ignores fragment identifiers inside comments and script text', async () => {
  await withFixture(async (fixtureDirectory) => {
    await writeFile(
      path.join(fixtureDirectory, 'index.html'),
      '<!-- <h2 id="comment-only">Comment</h2> --><script>const template = \'<h2 id="script-only">Script</h2>\';</script><a href="#comment-only">Comment</a><a href="#script-only">Script</a>',
    );

    await assert.rejects(
      runChecker(fixtureDirectory),
      (error) => {
        assert.match(error.stderr, /Found 2 broken internal links:/);
        assert.match(error.stderr, /#comment-only/);
        assert.match(error.stderr, /#script-only/);
        return true;
      },
    );
  });
});

test('ignores links inside comments and script text', async () => {
  await withFixture(async (fixtureDirectory) => {
    await writeFile(
      path.join(fixtureDirectory, 'index.html'),
      '<!-- <a href="/comment-only/">Comment</a> --><script>const template = \'<a href="/script-only/">Script</a>\';</script>',
    );

    const result = await runChecker(fixtureDirectory);
    assert.match(result.stdout, /all internal links resolve/);
  });
});

test('rejects a broken absolute URL on the default site origin', async () => {
  await withFixture(async (fixtureDirectory) => {
    await writeFile(
      path.join(fixtureDirectory, 'index.html'),
      '<a href="https://yourcove.net/missing-page/">Missing page</a>',
    );

    await assert.rejects(
      runChecker(fixtureDirectory),
      (error) => {
        assert.match(error.stderr, /Found 1 broken internal link:/);
        assert.match(error.stderr, /https:\/\/yourcove\.net\/missing-page\//);
        return true;
      },
    );
  });
});

test('resolves links within a GitHub Pages repository subpath', async () => {
  await withFixture(async (fixtureDirectory) => {
    await writeFile(
      path.join(fixtureDirectory, 'index.html'),
      '<a href="/cove/docs/page/#section">Valid link</a><a href="/docs/page/">Outside base</a>',
    );
    await mkdir(path.join(fixtureDirectory, 'docs', 'page'), { recursive: true });
    await writeFile(
      path.join(fixtureDirectory, 'docs', 'page', 'index.html'),
      '<h2 id="section">Section</h2>',
    );

    await assert.rejects(
      runChecker(fixtureDirectory, {
        GITHUB_REPOSITORY: 'yourcove/cove',
        SITE_URL: 'https://yourcove.github.io/cove',
      }),
      (error) => {
        assert.match(error.stderr, /Found 1 broken internal link:/);
        assert.match(error.stderr, /\/docs\/page\//);
        assert.doesNotMatch(error.stderr, /\/cove\/docs\/page\/#section/);
        return true;
      },
    );
  });
});
