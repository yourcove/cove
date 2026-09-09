import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import {
  prepareCoverageGuttersFile,
  prepareCoverageGuttersXml,
} from "./prepare-coverage-gutters.mjs";

test("adds the source root and normalizes dotnet-coverage branch booleans", () => {
  const input = `<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0.5">
  <packages>
    <package name="Cove">
      <classes>
        <class name="Example" filename="/workspaces/api-tests/src/Example.cs">
          <lines>
            <line number="1" hits="1" branch="True" condition-coverage="50% (1/2)" />
            <line number="2" hits="0" branch="False" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>`;

  const output = prepareCoverageGuttersXml(input);

  assert.match(output, /<coverage line-rate="0\.5">\n  <sources>\n    <source>\/<\/source>\n  <\/sources>\n  <packages>/);
  assert.match(output, /branch="true"/);
  assert.match(output, /branch="false"/);
  assert.doesNotMatch(output, /branch="(?:True|False)"/);
});

test("preserves an existing nonempty source list and is idempotent", () => {
  const input = `<?xml version="1.0"?>
<coverage>
  <sources>
    <source>/agent/work/cove</source>
  </sources>
  <packages />
</coverage>`;

  const once = prepareCoverageGuttersXml(input);
  const twice = prepareCoverageGuttersXml(once);

  assert.equal(once, input);
  assert.equal(twice, input);
});

test("rejects input that Coverage Gutters cannot safely resolve", () => {
  assert.throws(
    () => prepareCoverageGuttersXml("<packages />"),
    /Cobertura <coverage> root/,
  );
  assert.throws(
    () => prepareCoverageGuttersXml("<coverage><sources /><packages /></coverage>"),
    /nonempty <source>/,
  );
});

test("writes a complete derived report without modifying the raw input", async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), "cove-coverage-gutters-"));
  const inputPath = path.join(directory, "raw.cobertura.xml");
  const outputPath = path.join(directory, "coverage-gutters.xml");
  const input = "<coverage><packages /></coverage>";

  try {
    await fs.writeFile(inputPath, input);

    const preparedPath = await prepareCoverageGuttersFile(inputPath, outputPath);

    assert.equal(preparedPath, outputPath);
    assert.equal(await fs.readFile(inputPath, "utf8"), input);
    assert.match(await fs.readFile(outputPath, "utf8"), /<source>\/<\/source>/);
    assert.deepEqual((await fs.readdir(directory)).sort(), ["coverage-gutters.xml", "raw.cobertura.xml"]);
    await assert.rejects(
      prepareCoverageGuttersFile(inputPath, inputPath),
      /Input and output paths must be different/,
    );
  } finally {
    await fs.rm(directory, { force: true, recursive: true });
  }
});
