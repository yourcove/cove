import { describe, expect, it } from "vitest";
import { DetailListPagination, MediaDetailLayout } from "../generated/extensions/runtime/v1/components";

describe("extension component runtime", () => {
  it("publishes Cove's native catalog pagination and media detail layout", () => {
    expect(DetailListPagination).toBeTypeOf("function");
    expect(MediaDetailLayout).toBeTypeOf("function");
  });
});
