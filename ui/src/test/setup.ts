import "@testing-library/jest-dom/vitest";
import { afterAll, afterEach, beforeEach, vi } from "vitest";

// A console warning or error fails the test: React act warnings, React Query warnings, and app
// warnings all report through the console, and a passing run should print none. A test that expects
// a log asserts it with its own spy, which keeps it out of this check:
//
//   const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
//   ...
//   expect(warn).toHaveBeenCalledWith("...", error);
//
// Leave the spy in place until the test ends; vi.restoreAllMocks in afterEach restores it. A spy
// restored inside the test body no longer covers warnings logged when Testing Library unmounts.

const checkedLevels = ["error", "warn"] as const;
type CheckedLevel = (typeof checkedLevels)[number];

let unexpectedLogs: { level: CheckedLevel; message: string }[] = [];

// Renders console arguments the way the console would, for the failure message. It must never throw,
// because it runs inside the code that called console.error.
function format(...args: unknown[]): string {
  try {
    const rest = [...args];
    let message = "";
    // React formats its warnings with printf-style placeholders.
    if (typeof rest[0] === "string" && /%[sdifoOc%]/.test(rest[0])) {
      message = (rest.shift() as string).replace(/%[sdifoOc%]/g, (placeholder) => {
        if (placeholder === "%%") return "%";
        if (rest.length === 0) return placeholder;
        const value = rest.shift();
        return placeholder === "%c" ? "" : formatValue(value);
      });
    }
    return [message, ...rest.map(formatValue)].filter((part) => part !== "").join(" ");
  } catch {
    return "(console arguments that could not be formatted)";
  }
}

function formatValue(value: unknown): string {
  if (typeof value === "string") return value;
  if (value instanceof Error) return value.stack ?? `${value.name}: ${value.message}`;
  try {
    const json = JSON.stringify(value);
    if (json !== undefined) return json;
  } catch {
    // Circular or BigInt-bearing values fall through to their type tag.
  }
  return typeof value === "object" && value !== null ? Object.prototype.toString.call(value) : String(value);
}

for (const level of checkedLevels) {
  const print = console[level].bind(console);
  console[level] = (...args: unknown[]) => {
    // A call that arrives through a test's own spy is one that test expected and asserts.
    if (!vi.isMockFunction(console[level])) unexpectedLogs.push({ level, message: format(...args) });
    print(...args);
  };
}

function takeUnexpectedLogs(): string {
  const report = unexpectedLogs.map(({ level, message }) => `console.${level}: ${message}`).join("\n\n");
  unexpectedLogs = [];
  return report;
}

// Output logged between two tests, or in a describe's beforeAll, fails the next test with a message
// saying so. Work that outlives its test can still log while a later test runs; that output is
// reported against the later test, so a failure there can come from leftover work of an earlier one.
beforeEach(() => {
  if (unexpectedLogs.length > 0) {
    throw new Error(`Unexpected console output before this test started:\n\n${takeUnexpectedLogs()}`);
  }
});

afterEach(() => {
  if (unexpectedLogs.length > 0) {
    throw new Error(`Unexpected console output during this test:\n\n${takeUnexpectedLogs()}`);
  }
});

afterAll(() => {
  if (unexpectedLogs.length > 0) {
    throw new Error(`Unexpected console output after the last test in this file:\n\n${takeUnexpectedLogs()}`);
  }
});
