export const extensionRuntimeVersion = "v1";

export const extensionRuntimeModules = [
  {
    id: "react",
    source: "react",
    specifier: "@cove/runtime/react",
    sourceFileName: "react.ts",
    outputFileName: "react.js",
    legacySpecifiers: ["react"],
  },
  {
    id: "react-dom",
    source: "react-dom",
    specifier: "@cove/runtime/react-dom",
    sourceFileName: "react-dom.ts",
    outputFileName: "react-dom.js",
    legacySpecifiers: ["react-dom"],
  },
  {
    id: "react-dom-client",
    source: "react-dom/client",
    specifier: "@cove/runtime/react-dom-client",
    sourceFileName: "react-dom-client.ts",
    outputFileName: "react-dom-client.js",
    legacySpecifiers: ["react-dom/client"],
  },
  {
    id: "react-jsx-runtime",
    source: "react/jsx-runtime",
    specifier: "@cove/runtime/react-jsx-runtime",
    sourceFileName: "react-jsx-runtime.ts",
    outputFileName: "react-jsx-runtime.js",
    legacySpecifiers: ["react/jsx-runtime"],
  },
  {
    id: "react-jsx-dev-runtime",
    source: "react/jsx-dev-runtime",
    specifier: "@cove/runtime/react-jsx-dev-runtime",
    sourceFileName: "react-jsx-dev-runtime.ts",
    outputFileName: "react-jsx-dev-runtime.js",
    legacySpecifiers: ["react/jsx-dev-runtime"],
  },
  {
    id: "react-query",
    source: "@tanstack/react-query",
    specifier: "@cove/runtime/react-query",
    sourceFileName: "react-query.ts",
    outputFileName: "react-query.js",
    legacySpecifiers: ["@tanstack/react-query"],
  },
  {
    id: "lucide-react",
    source: "lucide-react",
    specifier: "@cove/runtime/lucide-react",
    sourceFileName: "lucide-react.ts",
    outputFileName: "lucide-react.js",
    legacySpecifiers: ["lucide-react"],
  },
  {
    id: "components",
    source: null as any, // local barrel – not auto-generated
    specifier: "@cove/runtime/components",
    sourceFileName: "components.ts",
    outputFileName: "components.js",
    legacySpecifiers: [],
  },
];
