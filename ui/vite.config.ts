import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "path";
import { extensionRuntimeModules, extensionRuntimeVersion } from "./scripts/extension-runtime-contract.ts";

const extensionRuntimeEntries = Object.fromEntries(
  extensionRuntimeModules.map((definition) => [
    `extension-runtime-${definition.id}`,
    path.resolve(__dirname, `./src/generated/extensions/runtime/${extensionRuntimeVersion}/${definition.sourceFileName}`),
  ])
);

const extensionRuntimeFileNames = new Map<string, string>(
  extensionRuntimeModules.map((definition) => [
    `extension-runtime-${definition.id}`,
    `assets/extension-runtime/${extensionRuntimeVersion}/${definition.outputFileName}`,
  ])
);

function buildExtensionImportMap() {
  return Object.fromEntries(
    extensionRuntimeModules.flatMap((definition) => {
      const target = `/${extensionRuntimeFileNames.get(`extension-runtime-${definition.id}`)!}`;
      return [definition.specifier, ...definition.legacySpecifiers].map((specifier) => [specifier, target]);
    })
  );
}

function extensionRuntimeImportMapPlugin() {
  return {
    name: "extension-runtime-import-map",
    transformIndexHtml() {
      const importMap = JSON.stringify({ imports: buildExtensionImportMap() }, null, 2);
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

export default defineConfig({
  plugins: [react(), tailwindcss(), extensionRuntimeImportMapPlugin()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: "http://localhost:9999",
        changeOrigin: true,
      },
      "/hubs": {
        target: "http://localhost:9999",
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
        index: path.resolve(__dirname, "./index.html"),
        ...extensionRuntimeEntries,
      },
      output: {
        entryFileNames: (chunkInfo) => extensionRuntimeFileNames.get(chunkInfo.name) ?? "assets/[name]-[hash].js",
        manualChunks: {
          vendor: ["react", "react-dom", "@tanstack/react-query"],
          icons: ["lucide-react"],
          signalr: ["@microsoft/signalr"],
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
});
