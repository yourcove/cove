import * as fs from "fs/promises";
import path from "path";
import { fileURLToPath } from "url";
import { extensionRuntimeModules, extensionRuntimeVersion } from "./extension-runtime-contract.ts";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const runtimeDir = path.resolve(__dirname, `../src/generated/extensions/runtime/${extensionRuntimeVersion}`);
const identifierPattern = /^[A-Za-z_$][A-Za-z0-9_$]*$/;

type DefaultExport = "none" | "source" | "namespace";

function buildRuntimeSource(source: string, exportNames: string[], defaultExport: DefaultExport) {
  const lines = [
    "// AUTO-GENERATED FILE. DO NOT EDIT.",
    "// @ts-nocheck",
    `// Generated from package exports for ${source}.`,
    `import * as runtimeModule from ${JSON.stringify(source)};`,
    "",
  ];

  if (defaultExport === "source") {
    lines.push(
      'const runtimeDefault = Object.prototype.hasOwnProperty.call(runtimeModule, "default")',
      "  ? runtimeModule.default",
      "  : runtimeModule;",
      "",
      "export default runtimeDefault;",
      ""
    );
  } else if (defaultExport === "namespace") {
    lines.push("export default runtimeModule;", "");
  }

  for (const exportName of exportNames) {
    lines.push(`export const ${exportName} = runtimeModule.${exportName};`);
  }

  if (exportNames.length === 0 && defaultExport === "none") {
    lines.push("export {};");
  }

  lines.push("");
  return lines.join("\n");
}

function buildTypeSource(source: string, defaultExport: DefaultExport) {
  const lines = [
    "// AUTO-GENERATED FILE. DO NOT EDIT.",
    `export * from ${JSON.stringify(source)};`,
  ];

  if (defaultExport === "source") {
    lines.push(`export { default } from ${JSON.stringify(source)};`);
  } else if (defaultExport === "namespace") {
    lines.push(
      `declare const runtimeDefault: typeof import(${JSON.stringify(source)});`,
      "export default runtimeDefault;"
    );
  }

  lines.push("");
  return lines.join("\n");
}

async function generateRuntimeModule(definition: (typeof extensionRuntimeModules)[number]) {
  const moduleNamespace = await import(definition.source);
  const exportNames = Object.keys(moduleNamespace)
    .filter((name) => name !== "default" && name !== "__esModule" && identifierPattern.test(name))
    .sort();
  const detectedDefaultExport = Object.prototype.hasOwnProperty.call(moduleNamespace, "default") ? "source" : "none";
  const defaultExport =
    "defaultExport" in definition ? definition.defaultExport as DefaultExport : detectedDefaultExport;

  await fs.writeFile(
    path.join(runtimeDir, definition.sourceFileName),
    buildRuntimeSource(definition.source, exportNames, defaultExport),
    "utf8"
  );
  await fs.writeFile(
    path.join(runtimeDir, definition.sourceFileName.replace(/\.ts$/, ".d.ts")),
    buildTypeSource(definition.source, defaultExport),
    "utf8"
  );
}

async function generateContractModule() {
  const contractPath = path.join(runtimeDir, "contract.ts");
  const typePath = path.join(runtimeDir, "contract.d.ts");
  const moduleSpecifiers = extensionRuntimeModules.map((definition) => definition.specifier);

  const contractSource = [
    "// AUTO-GENERATED FILE. DO NOT EDIT.",
    `export const extensionRuntimeVersion = ${JSON.stringify(extensionRuntimeVersion)};`,
    `export const sharedModuleSpecifiers = ${JSON.stringify(moduleSpecifiers, null, 2)};`,
    "",
  ].join("\n");

  const typeSource = [
    "// AUTO-GENERATED FILE. DO NOT EDIT.",
    `export declare const extensionRuntimeVersion: ${JSON.stringify(extensionRuntimeVersion)};`,
    "export declare const sharedModuleSpecifiers: readonly string[];",
    "",
  ].join("\n");

  await fs.writeFile(contractPath, contractSource, "utf8");
  await fs.writeFile(typePath, typeSource, "utf8");
}

async function main() {
  await fs.rm(runtimeDir, { recursive: true, force: true });
  await fs.mkdir(runtimeDir, { recursive: true });

  for (const definition of extensionRuntimeModules) {
    if (definition.source) {
      await generateRuntimeModule(definition);
    }
  }

  // Write the components barrel (source is null – it re-exports a local barrel)
  const componentsBarrel = [
    "// AUTO-GENERATED FILE. DO NOT EDIT.",
    '// Re-exports the shared component barrel for extensions.',
    'export * from "../../../../components/extension-shared";',
    "",
  ].join("\n");
  const componentsType = [
    "// AUTO-GENERATED FILE. DO NOT EDIT.",
    'export * from "../../../../components/extension-shared";',
    "",
  ].join("\n");
  await fs.writeFile(path.join(runtimeDir, "components.ts"), componentsBarrel, "utf8");
  await fs.writeFile(path.join(runtimeDir, "components.d.ts"), componentsType, "utf8");

  await generateContractModule();
  console.log(`Generated Cove extension runtime modules in ${runtimeDir}`);
}

main().catch((error) => {
  console.error("Failed to generate extension runtime modules", error);
  process.exit(1);
});
