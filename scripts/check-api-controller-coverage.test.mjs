import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  analyzeCobertura,
  evaluateCoverage,
  renderReport,
} from "./check-api-controller-coverage.mjs";

const scriptPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "check-api-controller-coverage.mjs");

function coverageXml(classes) {
  return `<?xml version="1.0" encoding="utf-8"?>
<coverage>
  <packages>
    <package name="Cove">
      <classes>${classes}</classes>
    </package>
  </packages>
</coverage>`;
}

function classXml(filename, lines, name = "Example") {
  return `
        <class name="${name}" filename="${filename}">
          <methods><method name="Run"><lines>${lines}</lines></method></methods>
          <lines>${lines}</lines>
        </class>`;
}

function runScript(argumentsList) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [scriptPath, ...argumentsList], { stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.on("close", (status) => resolve({ status, stderr, stdout }));
  });
}

test("deduplicates async and aggregate records by normalized Windows filename and source line", () => {
  const regularLines = `
    <line number="10" hits="0" branch="False" />
    <line number="11" hits="0" branch="True" condition-coverage="0% (0/2)" />`;
  const asyncLines = `
    <line number="10" hits="3" branch="False" />
    <line number="11" hits="1" branch="True" condition-coverage="50% (1/2)" />`;
  const xml = coverageXml([
    classXml("C:\\agent\\_work\\cove\\src\\Cove.Api\\Controllers\\VideosController.cs", regularLines),
    classXml("/agent/_work/cove/src/Cove.Api/Controllers/VideosController.cs", asyncLines, "&lt;RunAsync&gt;d__1"),
    classXml("C:\\agent\\_work\\cove\\src\\Cove.Api\\obj\\Release\\generated\\Controllers\\IgnoredController.cs", `<line number="1" hits="1" branch="False" />`),
    classXml("/agent/_work/cove/src/Cove.Api/Controllers/Generated/IgnoredController.cs", `<line number="1" hits="1" branch="False" />`),
  ].join(""));

  const report = analyzeCobertura(xml);

  assert.equal(report.files.length, 1);
  assert.equal(report.files[0].path, "src/Cove.Api/Controllers/VideosController.cs");
  assert.deepEqual(report.aggregate, {
    coveredBranches: 1,
    coveredLines: 2,
    totalBranches: 2,
    totalLines: 2,
    uncoveredLines: 0,
  });
});

test("reports aggregate, per-controller branch diagnostics, uncovered lines, hotspots, and the 90% target", () => {
  const xml = coverageXml([
    classXml("/repo/src/Cove.Api/Controllers/AudiosController.cs", `
      <line number="20" hits="1" branch="True" condition-coverage="50% (1/2)" />
      <line number="21" hits="0" branch="False" />`),
    classXml("/repo/src/Cove.Api/Controllers/Nested/ImagesController.cs", `
      <line number="30" hits="0" branch="True" condition-coverage="0% (0/4)" />`),
  ].join(""));
  const report = analyzeCobertura(xml);
  const evaluation = evaluateCoverage(report, {
    schemaVersion: 1,
    scope: "src/Cove.Api/Controllers/**/*.cs",
    minimum: { coveredLines: 1, totalLines: 3 },
    targetLineRate: 0.9,
  });

  const output = renderReport(report, evaluation, { baselinePath: "baseline.json", reportPath: "coverage.xml", top: 1 });

  assert.match(output, /Aggregate: lines 1\/3 \(33\.333%\); branches 1\/6 \(16\.667%\); uncovered lines 2/);
  assert.match(output, /AudiosController\.cs: lines 1\/2 .* branches 1\/2 .* uncovered 1/);
  assert.match(output, /Nested\/ImagesController\.cs: lines 0\/1 .* branches 0\/4 .* uncovered 1/);
  assert.match(output, /Target: 90\.0% line coverage/);
  assert.match(output, /90\.0% target gap: 2 additional covered lines/);
  assert.match(output, /Top 1 uncovered source files:/);
  assert.equal(evaluation.passesBaseline, true);
});

test("rejects malformed or non-Cobertura input", () => {
  assert.throws(
    () => analyzeCobertura(`<coverage><packages><class filename="/repo/src/Cove.Api/Controllers/VideosController.cs"><line number="1" hits="1" /></packages></coverage>`),
    /mismatched XML closing tag/,
  );
  assert.throws(
    () => analyzeCobertura(coverageXml(classXml("/repo/src/Cove.Core/Other.cs", `<line number="1" hits="1" />`))),
    /no source lines found under src\/Cove\.Api\/Controllers/,
  );
});

test("rejects malformed XML entities, branch attributes, and baseline configuration", () => {
  assert.throws(
    () => analyzeCobertura(coverageXml(classXml("/repo/src/Cove.Api/Controll&bogus;ers/Omitted.cs", `<line number="1" hits="0" />`))),
    /unknown XML entity: &bogus;/,
  );
  assert.throws(
    () => analyzeCobertura(coverageXml(classXml("/repo/src/Cove.Api/Controllers/VideosController.cs", `<line number="1" hits="1" branch="sometimes" />`))),
    /branch at .* must be true or false/,
  );

  const report = analyzeCobertura(coverageXml(classXml(
    "/repo/src/Cove.Api/Controllers/VideosController.cs",
    `<line number="1" hits="1" branch="False" />`,
  )));
  assert.throws(
    () => evaluateCoverage(report, {
      schemaVersion: 1,
      scope: "src/Cove.Api/Controllers/**/*.cs",
      minimum: { coveredLines: 1, totalLines: 1 },
      targetLineRate: "0.9",
    }),
    /targetLineRate must be/,
  );
});

test("CLI exits nonzero when unique controller line coverage falls below the checked-in-style threshold", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "cove-controller-coverage-"));
  const reportPath = path.join(root, "coverage.xml");
  const baselinePath = path.join(root, "baseline.json");
  try {
    fs.writeFileSync(reportPath, coverageXml(classXml("/repo/src/Cove.Api/Controllers/VideosController.cs", `
      <line number="1" hits="1" branch="False" />
      <line number="2" hits="0" branch="False" />`)));
    fs.writeFileSync(baselinePath, JSON.stringify({
      schemaVersion: 1,
      scope: "src/Cove.Api/Controllers/**/*.cs",
      minimum: { coveredLines: 3, totalLines: 4 },
      targetLineRate: 0.9,
    }));

    const result = await runScript(["--baseline", baselinePath, reportPath]);

    assert.equal(result.status, 1, result.stderr);
    assert.match(result.stdout, /Baseline ratchet: FAIL/);
    assert.match(result.stdout, /Coverage 50\.000% is below the checked-in minimum 75\.000%/);
    assert.equal(result.stderr, "");
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("CLI treats a malformed XML entity as a tooling error instead of omitting its source file", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "cove-controller-coverage-invalid-"));
  const reportPath = path.join(root, "coverage.xml");
  const baselinePath = path.join(root, "baseline.json");
  try {
    fs.writeFileSync(reportPath, coverageXml([
      classXml("/repo/src/Cove.Api/Controllers/CoveredController.cs", `<line number="1" hits="1" />`),
      classXml("/repo/src/Cove.Api/Controll&bogus;ers/OmittedController.cs", `<line number="1" hits="0" />`),
    ].join("")));
    fs.writeFileSync(baselinePath, JSON.stringify({
      schemaVersion: 1,
      scope: "src/Cove.Api/Controllers/**/*.cs",
      minimum: { coveredLines: 0, totalLines: 1 },
      targetLineRate: 0.9,
    }));

    const result = await runScript(["--baseline", baselinePath, reportPath]);

    assert.equal(result.status, 2);
    assert.match(result.stderr, /API controller coverage check failed: unknown XML entity: &bogus;/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
