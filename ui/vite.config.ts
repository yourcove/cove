import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "path";
import fs from "fs";
import { createRequire } from "node:module";
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

// The extension facade exports the whole catalog. Use Lucide's bundled CommonJS
// entry there so loading an extension does not fetch every individual icon chunk.
// App imports still use ESM, allowing DynamicIcon to load only the selected icon.
function extensionLucideBundlePlugin() {
  return {
    name: "extension-lucide-bundle",
    enforce: "pre" as const,
    apply: "build" as const,
    resolveId(id: string, importer?: string) {
      if (id === "lucide-react" && importer === extensionRuntimeEntries["extension-runtime-lucide-react"]) {
        return createRequire(import.meta.url).resolve("lucide-react");
      }
      return null;
    },
  };
}

export default defineConfig(({ command }) => {
  const useDevRuntimeModules = command === "serve";

  return {
    plugins: [
      react(),
      tailwindcss(),
      changelogPlugin(),
      extensionRuntimeImportMapPlugin(useDevRuntimeModules),
      extensionLucideBundlePlugin(),
    ],
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
            // Let Lucide dynamic imports split icons into individually loaded chunks.
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
