import { describe, expect, it } from "vitest";
import {
  collapseExtensionCriteria,
  executableExtensionFilterKey,
  expandExtensionCriteria,
  extensionFilterKey,
  unavailableExtensionCriterionDefinitions,
} from "../extensions/extensionListFilters";

describe("extension list filter object representation", () => {
  it("round-trips namespaced criteria without changing core filters", () => {
    const saved = {
      favoriteCriterion: { value: true },
      extensionCriteria: [{
        extensionId: "midnight-rider.animated-tag-previews",
        filterId: "has-preview",
        modifier: "equals",
        value: false,
      }],
    };

    const expanded = expandExtensionCriteria(saved);
    expect(expanded[extensionFilterKey("midnight-rider.animated-tag-previews", "has-preview")])
      .toEqual({ modifier: "EQUALS", value: false });
    expect(collapseExtensionCriteria(expanded)).toEqual(saved);
  });

  it("marks a saved criterion unavailable without discarding it", () => {
    const saved = {
      extensionCriteria: [{ extensionId: "missing.extension", filterId: "owned", modifier: "equals", value: true }],
    };

    const definitions = unavailableExtensionCriterionDefinitions(saved, []);

    expect(definitions).toEqual([expect.objectContaining({
      label: "Unavailable extension filter (missing.extension/owned)",
      supported: false,
    })]);
    expect(collapseExtensionCriteria(expandExtensionCriteria(saved))).toEqual(saved);
  });

  it("scopes executable filters to tags and gives their namespace precedence over legacy keys", () => {
    const dualDeclaration = {
      id: "owned",
      entityType: "tags",
      label: "Owned",
      criterionType: "boolean",
      extensionId: "owner.actual",
      filterKey: "favoriteCriterion",
      filterId: "has-preview",
      order: 100,
    };

    expect(executableExtensionFilterKey(dualDeclaration))
      .toBe(extensionFilterKey("owner.actual", "has-preview"));
    expect(executableExtensionFilterKey({ ...dualDeclaration, entityType: "videos" })).toBeNull();
  });

  it("does not throw on malformed percent encoding in imported filter keys", () => {
    const malformed = { "extension-filter:%E0%A4%A:owned": { modifier: "EQUALS", value: true } };
    expect(collapseExtensionCriteria(malformed)).toEqual(malformed);
  });

  it("preserves structurally malformed saved extension criteria without dereferencing them", () => {
    const saved = {
      extensionCriteria: [null, 42, { extensionId: "owner", filterId: "owned", modifier: "equals" }],
    };

    expect(() => unavailableExtensionCriterionDefinitions(saved, [])).not.toThrow();
    expect(unavailableExtensionCriterionDefinitions(saved, [])).toEqual([]);
    expect(expandExtensionCriteria(saved).extensionCriteria).toEqual(saved.extensionCriteria);
    expect(collapseExtensionCriteria({}, saved)).toEqual(saved);
  });
});
