#!/usr/bin/env node

import { randomUUID } from "node:crypto";
import fs from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

function fail(message) {
  throw new Error(message);
}

export function prepareCoverageGuttersXml(xml) {
  if (typeof xml !== "string") fail("Cobertura input must be a string");

  const coverageRoot = /<coverage(?:\s[^>]*)?>/.exec(xml);
  if (!coverageRoot) fail("Cobertura <coverage> root was not found");
  if (!/<packages(?:\s[^>]*)?\s*\/?\s*>/.test(xml)) fail("Cobertura <packages> element was not found");

  const sourcesElement = /<sources(?:\s[^>]*)?>([\s\S]*?)<\/sources>/.exec(xml);
  if (sourcesElement) {
    const sourceValues = [...sourcesElement[1].matchAll(/<source(?:\s[^>]*)?>([\s\S]*?)<\/source>/g)]
      .map((match) => match[1].trim())
      .filter(Boolean);
    if (sourceValues.length === 0) fail("Cobertura <sources> must contain a nonempty <source>");
  } else if (/<sources(?:\s[^>]*)?\s*\/>/.test(xml)) {
    fail("Cobertura <sources> must contain a nonempty <source>");
  } else {
    const newline = xml.includes("\r\n") ? "\r\n" : "\n";
    const insertion = `${newline}  <sources>${newline}    <source>/</source>${newline}  </sources>`;
    const insertionIndex = coverageRoot.index + coverageRoot[0].length;
    xml = `${xml.slice(0, insertionIndex)}${insertion}${xml.slice(insertionIndex)}`;
  }

  return xml.replace(/branch=(["'])(True|False)\1/g, (_match, quote, value) => (
    `branch=${quote}${value.toLowerCase()}${quote}`
  ));
}

export async function prepareCoverageGuttersFile(inputPath, outputPath) {
  const resolvedInput = path.resolve(inputPath);
  const resolvedOutput = path.resolve(outputPath);
  if (resolvedInput === resolvedOutput) fail("Input and output paths must be different so the raw report is preserved");

  const xml = await fs.readFile(resolvedInput, "utf8");
  const prepared = prepareCoverageGuttersXml(xml);
  await fs.mkdir(path.dirname(resolvedOutput), { recursive: true });

  const temporaryPath = `${resolvedOutput}.tmp-${process.pid}-${randomUUID()}`;
  try {
    await fs.writeFile(temporaryPath, prepared);
    await fs.rename(temporaryPath, resolvedOutput);
  } finally {
    await fs.rm(temporaryPath, { force: true });
  }

  return resolvedOutput;
}

async function main() {
  const [inputPath, outputPath, ...extraArguments] = process.argv.slice(2);
  if (!inputPath || !outputPath || extraArguments.length > 0) {
    fail("Usage: node scripts/prepare-coverage-gutters.mjs <input.cobertura.xml> <output.cobertura.xml>");
  }

  const preparedPath = await prepareCoverageGuttersFile(inputPath, outputPath);
  process.stdout.write(`Coverage Gutters report: ${preparedPath}\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    process.stderr.write(`prepare-coverage-gutters: ${error.message}\n`);
    process.exitCode = 1;
  });
}
