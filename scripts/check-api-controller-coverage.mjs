#!/usr/bin/env node

import fs from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const controllerRoot = "src/Cove.Api/Controllers";
const defaultBaselinePath = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "../src/Cove.ApiTests/controller-coverage-baseline.json",
);

function fail(message) {
  throw new Error(message);
}

function decodeXmlAttribute(value) {
  if (value.includes("<")) fail("unescaped '<' in XML attribute");

  let decoded = "";
  let cursor = 0;
  while (cursor < value.length) {
    const entityStart = value.indexOf("&", cursor);
    if (entityStart < 0) {
      decoded += value.slice(cursor);
      break;
    }

    decoded += value.slice(cursor, entityStart);
    const entityEnd = value.indexOf(";", entityStart + 1);
    if (entityEnd < 0) fail(`unterminated XML entity near: ${value.slice(entityStart, entityStart + 40)}`);
    const name = value.slice(entityStart + 1, entityEnd);

    if (name === "amp") decoded += "&";
    else if (name === "apos") decoded += "'";
    else if (name === "gt") decoded += ">";
    else if (name === "lt") decoded += "<";
    else if (name === "quot") decoded += "\"";
    else if (/^#(?:\d+|x[0-9a-fA-F]+)$/.test(name)) {
      const hexadecimal = name.startsWith("#x");
      const codePoint = Number.parseInt(name.slice(hexadecimal ? 2 : 1), hexadecimal ? 16 : 10);
      const validXmlCharacter = codePoint === 0x9 || codePoint === 0xa || codePoint === 0xd
        || (codePoint >= 0x20 && codePoint <= 0xd7ff)
        || (codePoint >= 0xe000 && codePoint <= 0xfffd)
        || (codePoint >= 0x10000 && codePoint <= 0x10ffff);
      if (!Number.isInteger(codePoint) || !validXmlCharacter) fail(`invalid XML character reference: &${name};`);
      decoded += String.fromCodePoint(codePoint);
    } else {
      fail(`unknown XML entity: &${name};`);
    }
    cursor = entityEnd + 1;
  }

  return decoded;
}

function parseAttributes(text) {
  const attributes = new Map();
  let index = 0;

  while (index < text.length) {
    while (/\s/.test(text[index] ?? "")) index += 1;
    if (index === text.length) break;

    const nameMatch = /^[A-Za-z_:][A-Za-z0-9_.:-]*/.exec(text.slice(index));
    if (!nameMatch) fail(`invalid XML attribute near: ${text.slice(index, index + 40)}`);
    const name = nameMatch[0];
    index += name.length;

    while (/\s/.test(text[index] ?? "")) index += 1;
    if (text[index] !== "=") fail(`XML attribute ${name} is missing '='`);
    index += 1;
    while (/\s/.test(text[index] ?? "")) index += 1;

    const quote = text[index];
    if (quote !== "\"" && quote !== "'") fail(`XML attribute ${name} must be quoted`);
    index += 1;
    const end = text.indexOf(quote, index);
    if (end < 0) fail(`XML attribute ${name} has no closing quote`);
    if (attributes.has(name)) fail(`duplicate XML attribute: ${name}`);
    attributes.set(name, decodeXmlAttribute(text.slice(index, end)));
    index = end + 1;
  }

  return attributes;
}

function findTagEnd(xml, start) {
  let quote = null;
  for (let index = start; index < xml.length; index += 1) {
    const character = xml[index];
    if (quote !== null) {
      if (character === quote) quote = null;
    } else if (character === "\"" || character === "'") {
      quote = character;
    } else if (character === ">") {
      return index;
    } else if (character === "<") {
      fail("malformed XML tag");
    }
  }
  fail("unterminated XML tag");
}

function visitXml(xml, visitor) {
  if (xml.charCodeAt(0) === 0xfeff) xml = xml.slice(1);

  const stack = [];
  let cursor = 0;
  let rootSeen = false;
  let rootClosed = false;

  while (cursor < xml.length) {
    const tagStart = xml.indexOf("<", cursor);
    if (tagStart < 0) {
      if (xml.slice(cursor).trim()) fail("text appears after the XML document");
      break;
    }
    if (xml.slice(cursor, tagStart).trim() && stack.length === 0) {
      fail("text appears outside the XML root element");
    }

    if (xml.startsWith("<!--", tagStart)) {
      const end = xml.indexOf("-->", tagStart + 4);
      if (end < 0) fail("unterminated XML comment");
      cursor = end + 3;
      continue;
    }
    if (xml.startsWith("<![CDATA[", tagStart)) {
      if (stack.length === 0) fail("CDATA appears outside the XML root element");
      const end = xml.indexOf("]]>", tagStart + 9);
      if (end < 0) fail("unterminated XML CDATA section");
      cursor = end + 3;
      continue;
    }
    if (xml.startsWith("<?", tagStart)) {
      const end = xml.indexOf("?>", tagStart + 2);
      if (end < 0) fail("unterminated XML processing instruction");
      cursor = end + 2;
      continue;
    }
    if (xml.startsWith("<!", tagStart)) fail("unsupported XML declaration");

    const tagEnd = findTagEnd(xml, tagStart + 1);
    let tagText = xml.slice(tagStart + 1, tagEnd).trim();
    if (!tagText) fail("empty XML tag");

    if (tagText.startsWith("/")) {
      const match = /^\/\s*([A-Za-z_:][A-Za-z0-9_.:-]*)\s*$/.exec(tagText);
      if (!match) fail(`invalid XML closing tag: <${tagText}>`);
      const name = match[1];
      const expected = stack.pop();
      if (expected !== name) fail(`mismatched XML closing tag: expected </${expected ?? "none"}>, found </${name}>`);
      visitor({ attributes: new Map(), closing: true, name, selfClosing: false });
      if (stack.length === 0) rootClosed = true;
      cursor = tagEnd + 1;
      continue;
    }

    const selfClosing = /\/\s*$/.test(tagText);
    if (selfClosing) tagText = tagText.replace(/\/\s*$/, "").trimEnd();
    const nameMatch = /^([A-Za-z_:][A-Za-z0-9_.:-]*)/.exec(tagText);
    if (!nameMatch) fail(`invalid XML opening tag: <${tagText}>`);
    const name = nameMatch[1];
    const attributes = parseAttributes(tagText.slice(name.length));

    if (stack.length === 0) {
      if (rootSeen || rootClosed) fail("XML document has multiple root elements");
      rootSeen = true;
      if (name !== "coverage") fail(`expected <coverage> root element, found <${name}>`);
    }

    visitor({ attributes, closing: false, name, selfClosing });
    if (!selfClosing) stack.push(name);
    else if (stack.length === 0) rootClosed = true;
    cursor = tagEnd + 1;
  }

  if (!rootSeen) fail("XML document has no root element");
  if (stack.length > 0) fail(`unclosed XML element: <${stack.at(-1)}>`);
  if (!rootClosed) fail("XML root element was not closed");
}

function normalizeControllerFilename(filename) {
  const slashPath = filename.trim().replaceAll("\\", "/");
  if (!slashPath) return null;

  const normalized = path.posix.normalize(slashPath);
  const segments = normalized.split("/");
  let sourceIndex = -1;
  for (let index = 0; index <= segments.length - 4; index += 1) {
    if (segments[index].toLowerCase() === "src"
        && segments[index + 1].toLowerCase() === "cove.api"
        && segments[index + 2].toLowerCase() === "controllers") {
      sourceIndex = index;
      break;
    }
  }
  if (sourceIndex < 0) return null;

  const relativeSegments = segments.slice(sourceIndex + 3);
  if (relativeSegments.length === 0 || !relativeSegments.at(-1).toLowerCase().endsWith(".cs")) return null;
  if (relativeSegments.some((segment) => !segment || segment === "." || segment === "..")) return null;
  if (relativeSegments.some((segment) => ["bin", "generated", "obj"].includes(segment.toLowerCase()))) return null;

  return `${controllerRoot}/${relativeSegments.join("/")}`;
}

function parseInteger(value, description, { positive = false } = {}) {
  if (!/^\d+$/.test(value ?? "")) fail(`${description} must be an integer`);
  const parsed = Number.parseInt(value, 10);
  if (!Number.isSafeInteger(parsed) || (positive ? parsed < 1 : parsed < 0)) {
    fail(`${description} is outside the supported range`);
  }
  return parsed;
}

function parseBranchCoverage(value, filename, lineNumber) {
  const match = /\(\s*(\d+)\s*\/\s*(\d+)\s*\)/.exec(value ?? "");
  if (!match) fail(`branch line ${filename}:${lineNumber} has invalid condition-coverage`);
  const covered = parseInteger(match[1], `covered branches at ${filename}:${lineNumber}`);
  const total = parseInteger(match[2], `total branches at ${filename}:${lineNumber}`, { positive: true });
  if (covered > total) fail(`covered branches exceed total branches at ${filename}:${lineNumber}`);
  return { covered, total };
}

function summarizeFile(file) {
  let coveredLines = 0;
  let coveredBranches = 0;
  let totalBranches = 0;
  for (const line of file.lines.values()) {
    if (line.hits > 0) coveredLines += 1;
    if (line.branches !== null) {
      coveredBranches += line.branches.covered;
      totalBranches += line.branches.total;
    }
  }
  return {
    coveredBranches,
    coveredLines,
    path: file.path,
    totalBranches,
    totalLines: file.lines.size,
    uncoveredLines: file.lines.size - coveredLines,
  };
}

export function analyzeCobertura(xml) {
  const files = new Map();
  const classFiles = [];

  visitXml(xml, ({ attributes, closing, name, selfClosing }) => {
    if (name === "class") {
      if (closing) {
        classFiles.pop();
        return;
      }
      if (classFiles.length > 0) fail("nested <class> elements are not supported by Cobertura");
      const filename = attributes.get("filename");
      if (!filename) fail("Cobertura <class> element is missing filename");
      classFiles.push(selfClosing ? null : normalizeControllerFilename(filename));
      if (selfClosing) classFiles.pop();
      return;
    }

    if (closing || name !== "line" || classFiles.length === 0 || classFiles.at(-1) === null) return;

    const filename = classFiles.at(-1);
    const lineNumber = parseInteger(attributes.get("number"), `line number in ${filename}`, { positive: true });
    const hits = parseInteger(attributes.get("hits"), `hits at ${filename}:${lineNumber}`);
    const branchAttribute = (attributes.get("branch") ?? "false").toLowerCase();
    if (branchAttribute !== "true" && branchAttribute !== "false") {
      fail(`branch at ${filename}:${lineNumber} must be true or false`);
    }
    const branch = branchAttribute === "true"
      ? parseBranchCoverage(attributes.get("condition-coverage"), filename, lineNumber)
      : null;

    const key = filename.toLowerCase();
    let file = files.get(key);
    if (!file) {
      file = { lines: new Map(), path: filename };
      files.set(key, file);
    }

    const existing = file.lines.get(lineNumber);
    if (!existing) {
      file.lines.set(lineNumber, { branches: branch, hits });
      return;
    }

    existing.hits = Math.max(existing.hits, hits);
    if (branch !== null) {
      if (existing.branches === null) existing.branches = branch;
      else {
        existing.branches.covered = Math.min(
          Math.max(existing.branches.covered, branch.covered),
          Math.max(existing.branches.total, branch.total),
        );
        existing.branches.total = Math.max(existing.branches.total, branch.total);
      }
    }
  });

  if (files.size === 0) fail(`no source lines found under ${controllerRoot}`);
  const fileSummaries = [...files.values()].map(summarizeFile).sort((left, right) => left.path.localeCompare(right.path));
  const aggregate = fileSummaries.reduce((total, file) => ({
    coveredBranches: total.coveredBranches + file.coveredBranches,
    coveredLines: total.coveredLines + file.coveredLines,
    totalBranches: total.totalBranches + file.totalBranches,
    totalLines: total.totalLines + file.totalLines,
    uncoveredLines: total.uncoveredLines + file.uncoveredLines,
  }), { coveredBranches: 0, coveredLines: 0, totalBranches: 0, totalLines: 0, uncoveredLines: 0 });

  return { aggregate, files: fileSummaries };
}

function validateBaseline(baseline) {
  if (baseline === null || typeof baseline !== "object" || Array.isArray(baseline)) fail("baseline must be a JSON object");
  if (baseline.schemaVersion !== 1) fail("baseline schemaVersion must be 1");
  if (baseline.scope !== `${controllerRoot}/**/*.cs`) fail(`baseline scope must be ${controllerRoot}/**/*.cs`);
  const coveredLines = parseInteger(String(baseline.minimum?.coveredLines ?? ""), "baseline minimum.coveredLines");
  const totalLines = parseInteger(String(baseline.minimum?.totalLines ?? ""), "baseline minimum.totalLines", { positive: true });
  if (coveredLines > totalLines) fail("baseline covered lines exceed total lines");
  if (typeof baseline.targetLineRate !== "number" || baseline.targetLineRate <= 0 || baseline.targetLineRate > 1) {
    fail("baseline targetLineRate must be greater than 0 and no greater than 1");
  }
  return { coveredLines, targetLineRate: baseline.targetLineRate, totalLines };
}

export function evaluateCoverage(report, baselineDocument) {
  const baseline = validateBaseline(baselineDocument);
  const { coveredLines, totalLines } = report.aggregate;
  const passesBaseline = coveredLines * baseline.totalLines >= baseline.coveredLines * totalLines;
  const targetCoveredLines = Math.ceil(baseline.targetLineRate * totalLines);
  return {
    baseline,
    passesBaseline,
    targetCoveredLines,
    targetGap: Math.max(0, targetCoveredLines - coveredLines),
  };
}

function percentage(covered, total) {
  return total === 0 ? "n/a" : `${(covered / total * 100).toFixed(3)}%`;
}

function fraction(covered, total) {
  return `${covered.toLocaleString("en-US")}/${total.toLocaleString("en-US")} (${percentage(covered, total)})`;
}

export function renderReport(report, evaluation, { baselinePath, reportPath, top = 10 } = {}) {
  const targetPercentage = `${(evaluation.baseline.targetLineRate * 100).toFixed(1)}%`;
  const lines = [
    "API controller coverage",
    `Scope: ${controllerRoot}/**/*.cs (unique source lines; obj/bin/generated excluded)`,
    `Report: ${reportPath ?? "(in memory)"}`,
    `Baseline: ${fraction(evaluation.baseline.coveredLines, evaluation.baseline.totalLines)} from ${baselinePath ?? "(in memory)"}`,
    `Target: ${targetPercentage} line coverage`,
    "",
    `Aggregate: lines ${fraction(report.aggregate.coveredLines, report.aggregate.totalLines)}; branches ${fraction(report.aggregate.coveredBranches, report.aggregate.totalBranches)}; uncovered lines ${report.aggregate.uncoveredLines.toLocaleString("en-US")}`,
    `Baseline ratchet: ${evaluation.passesBaseline ? "PASS" : "FAIL"}`,
    `${targetPercentage} target gap: ${evaluation.targetGap.toLocaleString("en-US")} additional covered lines at the current denominator`,
    "",
    "Per-controller source file:",
  ];

  for (const file of report.files) {
    const displayPath = file.path.slice(`${controllerRoot}/`.length);
    lines.push(`  ${displayPath}: lines ${fraction(file.coveredLines, file.totalLines)}; branches ${fraction(file.coveredBranches, file.totalBranches)}; uncovered ${file.uncoveredLines.toLocaleString("en-US")}`);
  }

  const hotspots = [...report.files]
    .filter((file) => file.uncoveredLines > 0)
    .sort((left, right) => right.uncoveredLines - left.uncoveredLines || left.path.localeCompare(right.path))
    .slice(0, top);
  lines.push("", `Top ${top} uncovered source files:`);
  if (hotspots.length === 0) lines.push("  None");
  else {
    for (const file of hotspots) {
      lines.push(`  ${file.uncoveredLines.toLocaleString("en-US")}  ${file.path.slice(`${controllerRoot}/`.length)}`);
    }
  }

  if (!evaluation.passesBaseline) {
    lines.push(
      "",
      `Coverage ${percentage(report.aggregate.coveredLines, report.aggregate.totalLines)} is below the checked-in minimum ${percentage(evaluation.baseline.coveredLines, evaluation.baseline.totalLines)}.`,
    );
  }
  return `${lines.join("\n")}\n`;
}

function usage() {
  return `Usage: node scripts/check-api-controller-coverage.mjs [options] REPORT

Checks unique source-line coverage for src/Cove.Api/Controllers/**/*.cs in a
Cobertura report. The checked-in API-test baseline is enforced by default.

Options:
  --baseline PATH  Coverage baseline JSON (default: src/Cove.ApiTests/controller-coverage-baseline.json)
  --top COUNT      Number of uncovered source files to print (default: 10)
  --help           Show this help
`;
}

function parseArgs(args) {
  const options = { baselinePath: defaultBaselinePath, help: false, reportPath: null, top: 10 };
  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index];
    if (argument === "--help") options.help = true;
    else if (argument === "--baseline" || argument === "--top") {
      const value = args[index + 1];
      if (!value || value.startsWith("--")) fail(`${argument} requires a value`);
      if (argument === "--baseline") options.baselinePath = path.resolve(value);
      else options.top = parseInteger(value, "--top", { positive: true });
      index += 1;
    } else if (argument.startsWith("--")) fail(`unknown option: ${argument}`);
    else if (options.reportPath !== null) fail("only one Cobertura report may be checked at a time");
    else options.reportPath = path.resolve(argument);
  }
  if (!options.help && options.reportPath === null) fail("a Cobertura report path is required");
  return options;
}

async function readTextFile(filePath, description) {
  try {
    return await fs.readFile(filePath, "utf8");
  } catch (error) {
    fail(`could not read ${description} ${filePath}: ${error.message}`);
  }
}

async function run() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    process.stdout.write(usage());
    return 0;
  }

  const [xml, baselineJson] = await Promise.all([
    readTextFile(options.reportPath, "Cobertura report"),
    readTextFile(options.baselinePath, "coverage baseline"),
  ]);
  let baselineDocument;
  try {
    baselineDocument = JSON.parse(baselineJson);
  } catch (error) {
    fail(`coverage baseline is not valid JSON: ${error.message}`);
  }

  const report = analyzeCobertura(xml);
  const evaluation = evaluateCoverage(report, baselineDocument);
  process.stdout.write(renderReport(report, evaluation, options));
  return evaluation.passesBaseline ? 0 : 1;
}

const invokedPath = process.argv[1] ? path.resolve(process.argv[1]) : null;
if (invokedPath === fileURLToPath(import.meta.url)) {
  run()
    .then((status) => { process.exitCode = status; })
    .catch((error) => {
      process.stderr.write(`API controller coverage check failed: ${error.message}\n`);
      process.exitCode = 2;
    });
}
