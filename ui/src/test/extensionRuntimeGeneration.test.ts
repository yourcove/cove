import { describe, expect, it } from "vitest";
import lucideDefault, * as lucideRuntime from "../generated/extensions/runtime/v1/lucide-react";
import runtimeSource from "../generated/extensions/runtime/v1/lucide-react.ts?raw";

describe("extension runtime generation", () => {
  it("exports the Lucide namespace as the compatibility default", () => {
    expect(runtimeSource).toContain("export default runtimeModule;");
    expect(runtimeSource).not.toContain("runtimeModule.default");
  });

  it("keeps named Lucide icons available through the compatibility default", () => {
    expect(lucideDefault.Search).toBe(lucideRuntime.Search);
  });
});
