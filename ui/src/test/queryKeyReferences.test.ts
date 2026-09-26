import { describe, expect, it } from "vitest";

// Every production source file, as text. Tests and generated code are excluded.
const sources = import.meta.glob(["../**/*.{ts,tsx}", "!./**", "!../generated/**", "!../**/*.test.{ts,tsx}"], {
  query: "?raw",
  import: "default",
  eager: true,
}) as Record<string, string>;

// Calls that act on cached data by key. A literal root here must belong to some query, or the call does nothing.
const KEY_REFERENCE =
  /\b(invalidateQueries|refetchQueries|cancelQueries|resetQueries|removeQueries|setQueryData|setQueriesData|getQueryData|getQueriesData|getQueryState|fetchQuery|prefetchQuery|ensureQueryData|fetchInfiniteQuery|prefetchInfiniteQuery|ensureInfiniteQueryData)\s*(?:<(?:[^<>]|<[^<>]*>)*>)?\(\s*(?:\{[^{}]*?\bqueryKey\s*:\s*)?\[\s*"([^"]+)"/g;
// These calls fetch the data they name, so their keys also define cached data.
const FETCHING_CALLS = new Set([
  "fetchQuery",
  "prefetchQuery",
  "ensureQueryData",
  "fetchInfiniteQuery",
  "prefetchInfiniteQuery",
  "ensureInfiniteQueryData",
]);
const QUERY_KEY = /queryKey\s*:\s*\[\s*"([^"]+)"/g;
// Key factories such as customFieldDefinitionsQueryKey() that return a literal root.
const KEY_FACTORY =
  /(?:function\s+\w*QueryKey\s*\([^)]*\)[^{]*\{\s*return|\w*QueryKey\s*=\s*\([^)]*\)[^=]*=>)\s*\[\s*"([^"]+)"/g;

interface KeyReference {
  root: string;
  call: string;
  location: string;
}

function collectQueryKeys() {
  const definedRoots = new Set<string>();
  const references: KeyReference[] = [];
  for (const [file, source] of Object.entries(sources)) {
    const referenceOffsets = new Set<number>();
    for (const match of source.matchAll(KEY_REFERENCE)) {
      const [text, call, root] = match;
      referenceOffsets.add(match.index + text.lastIndexOf(`"${root}"`));
      if (FETCHING_CALLS.has(call)) {
        definedRoots.add(root);
      } else {
        const line = source.slice(0, match.index).split("\n").length;
        references.push({ root, call, location: `${file.replace(/^\.\.\//, "src/")}:${line}` });
      }
    }
    for (const match of source.matchAll(QUERY_KEY)) {
      const [text, root] = match;
      if (!referenceOffsets.has(match.index + text.lastIndexOf(`"${root}"`))) definedRoots.add(root);
    }
    for (const match of source.matchAll(KEY_FACTORY)) definedRoots.add(match[1]);
  }
  return { definedRoots, references };
}

describe("query key references", () => {
  it("scans the production sources", () => {
    const { definedRoots, references } = collectQueryKeys();
    expect(Object.keys(sources).length).toBeGreaterThan(100);
    expect(definedRoots.size).toBeGreaterThan(50);
    expect(references.length).toBeGreaterThan(100);
  });

  it("only invalidates, reads or writes keys whose root some query uses", () => {
    const { definedRoots, references } = collectQueryKeys();
    const unmatched = references
      .filter((reference) => !definedRoots.has(reference.root))
      .map((reference) => `${reference.location} ${reference.call} ["${reference.root}", …]`);
    expect(unmatched).toEqual([]);
  });
});
