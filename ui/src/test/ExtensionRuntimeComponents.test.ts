import { describe, expect, it } from "vitest";
import {
  DetailListPagination,
  EntityMedia,
  MediaDetailLayout,
  PERFORMER_CRITERIA,
  VIDEO_ENGINE_TARGET,
  useExtensionKeyboardBindings,
  useRegisterExtensionKeyboardActions,
} from "../generated/extensions/runtime/v1/components";

describe("extension component runtime", () => {
  it("publishes Cove's native catalog pagination and media detail layout", () => {
    expect(DetailListPagination).toBeTypeOf("function");
    expect(MediaDetailLayout).toBeTypeOf("function");
    expect(PERFORMER_CRITERIA.some((criterion) => criterion.filterKey === "nameCriterion")).toBe(true);
    expect(useRegisterExtensionKeyboardActions).toBeTypeOf("function");
    expect(useExtensionKeyboardBindings).toBeTypeOf("function");
  });

  it("publishes the entity media boundary that native cards render through", () => {
    expect(EntityMedia).toBeTypeOf("function");
  });

  it("publishes the component override target for video engines", () => {
    expect(VIDEO_ENGINE_TARGET).toBe("video.engine");
  });
});
