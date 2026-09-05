import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "path";
import fs from "fs";
import { extensionRuntimeModules, extensionRuntimeVersion } from "./scripts/extension-runtime-contract.ts";

// Exposes the repo-root CHANGELOG.md to the app as `virtual:changelog-raw`.
// Reading it via fs (instead of a cross-package import) keeps CHANGELOG.md as the
// single source of truth while staying robust to where the UI is built from
// (local, CI, or the Docker frontend stage). Falls back to an empty string if absent.
function changelogPlugin() {
  const virtualId = "virtual:changelog-raw";
  const resolvedId = "\0" + virtualId;
  const changelogPath = path.resolve(import.meta.dirname, "..", "CHANGELOG.md");
  return {
    name: "cove-changelog",
    resolveId(id: string) {
      return id === virtualId ? resolvedId : null;
    },
    load(id: string) {
      if (id !== resolvedId) return null;
      let raw = "";
      try {
        raw = fs.readFileSync(changelogPath, "utf-8");
      } catch {
        raw = "";
      }
      return `export default ${JSON.stringify(raw)};`;
    },
  };
}

const extensionRuntimeEntries = Object.fromEntries(
  extensionRuntimeModules.map((definition) => [
    `extension-runtime-${definition.id}`,
    path.resolve(
      import.meta.dirname,
      `./src/generated/extensions/runtime/${extensionRuntimeVersion}/${definition.sourceFileName}`,
    ),
  ]),
);

const extensionRuntimeFileNames = new Map<string, string>(
  extensionRuntimeModules.map((definition) => [
    `extension-runtime-${definition.id}`,
    `assets/extension-runtime/${extensionRuntimeVersion}/${definition.outputFileName}`,
  ]),
);

function buildExtensionImportMap(useDevRuntimeModules: boolean) {
  return Object.fromEntries(
    extensionRuntimeModules.flatMap((definition) => {
      const target = useDevRuntimeModules
        ? `/src/generated/extensions/runtime/${extensionRuntimeVersion}/${definition.sourceFileName}`
        : `/${extensionRuntimeFileNames.get(`extension-runtime-${definition.id}`)!}`;
      return [definition.specifier, ...definition.legacySpecifiers].map((specifier) => [specifier, target]);
    }),
  );
}

function extensionRuntimeImportMapPlugin(useDevRuntimeModules: boolean) {
  return {
    name: "extension-runtime-import-map",
    transformIndexHtml() {
      const importMap = JSON.stringify({ imports: buildExtensionImportMap(useDevRuntimeModules) }, null, 2);
      return [
        {
          tag: "meta",
          attrs: {
            name: "cove-extension-runtime-version",
            content: extensionRuntimeVersion,
          },
          injectTo: "head",
        },
        {
          tag: "script",
          attrs: {
            type: "importmap",
          },
          children: importMap,
          injectTo: "head",
        },
      ];
    },
  };
}

export default defineConfig(({ command }) => {
  const useDevRuntimeModules = command === "serve";

  return {
    plugins: [react(), tailwindcss(), changelogPlugin(), extensionRuntimeImportMapPlugin(useDevRuntimeModules)],
    resolve: {
      alias: {
        "@": path.resolve(import.meta.dirname, "./src"),
      },
    },
    server: {
      host: "127.0.0.1",
      port: 5173,
      proxy: {
        "/api": {
          target: "http://localhost:5073",
          changeOrigin: true,
        },
        "/hubs": {
          target: "http://localhost:5073",
          changeOrigin: true,
          ws: true,
        },
      },
    },
    build: {
      outDir: "../src/Cove.Api/wwwroot",
      emptyOutDir: true,
      rollupOptions: {
        preserveEntrySignatures: "strict",
        input: {
          index: path.resolve(import.meta.dirname, "./index.html"),
          ...extensionRuntimeEntries,
        },
        output: {
          entryFileNames: (chunkInfo) => extensionRuntimeFileNames.get(chunkInfo.name) ?? "assets/[name]-[hash].js",
          manualChunks(id) {
            if (id.includes("/node_modules/lucide-react/")) return "icons";
            if (id.includes("/node_modules/@microsoft/signalr/")) return "signalr";
            if (
              id.includes("/node_modules/react/") ||
              id.includes("/node_modules/react-dom/") ||
              id.includes("/node_modules/@tanstack/react-query/")
            ) {
              return "vendor";
            }
          },
        },
      },
    },
    test: {
      globals: true,
      environment: "jsdom",
      setupFiles: "./src/test/setup.ts",
      css: true,
    },
  };
});
