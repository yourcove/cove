import { describe, expect, it } from "vitest";
import { createManualOpenRequest, registerManualContext } from "../components/ManualContext";

describe("createManualOpenRequest", () => {
  it("describes extension settings routes without selecting a topic", () => {
    const request = createManualOpenRequest("settings", "settings", "/settings/extensions/docs");

    expect(request.page).toBe("settings");
    expect(request.topicId).toBeUndefined();
    expect(request.contexts).toEqual(
      expect.arrayContaining([
        "page:settings",
        "route:/settings/extensions/docs",
        "settings-tab:extensions/docs",
        "settings-tab:extensions",
      ]),
    );
  });

  it("orders the most specific settings context before parent settings contexts", () => {
    expect(createManualOpenRequest("settings", "settings", "/settings/extensions/docs/topic").contexts).toEqual(
      expect.arrayContaining(["settings-tab:extensions/docs/topic", "settings-tab:extensions/docs"]),
    );

    const contexts = createManualOpenRequest("settings", "settings", "/settings/extensions/docs/topic").contexts ?? [];
    expect(contexts.indexOf("settings-tab:extensions/docs/topic")).toBeLessThan(
      contexts.indexOf("settings-tab:extensions/docs"),
    );
  });

  it("puts route and settings tab contexts before generic page contexts", () => {
    const contexts = createManualOpenRequest("settings", "settings", "/settings/extensions/docs").contexts ?? [];

    expect(contexts.indexOf("route:/settings/extensions/docs")).toBeLessThan(contexts.indexOf("page:settings"));
    expect(contexts.indexOf("settings-tab:extensions/docs")).toBeLessThan(contexts.indexOf("page:settings"));
  });

  it("emits extension settings aliases for shorthand settings routes", () => {
    const contexts = createManualOpenRequest("settings", "settings", "/settings/docs").contexts ?? [];

    expect(contexts).toEqual(
      expect.arrayContaining([
        "route:/settings/docs",
        "settings-tab:docs",
        "settings-tab:extensions/docs",
        "route:/settings/extensions/docs",
      ]),
    );
    expect(contexts.indexOf("settings-tab:extensions/docs")).toBeLessThan(contexts.indexOf("page:settings"));
  });

  it("puts active pane contexts before page contexts", () => {
    const unregister = registerManualContext("pane:extension-runner");
    try {
      expect(createManualOpenRequest("video", "videos", "/videos/1").contexts?.[0]).toBe("pane:extension-runner");
    } finally {
      unregister();
    }
  });
});
