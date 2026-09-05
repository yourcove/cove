import { describe, expect, it } from "vitest";
import { parseSavedFilterObject } from "../components/RelatedFilterWorkspace";

describe("related filter workspace helpers", () => {
  it("parses saved-filter objects", () => {
    expect(parseSavedFilterObject('{"q":"alpha","page":2}')).toEqual({ q: "alpha", page: 2 });
  });

  it.each([
    undefined,
    "",
    "not json",
    "[]",
    "null",
  ])("treats non-object saved-filter input as empty", (value) => {
    expect(parseSavedFilterObject(value)).toEqual({});
  });
});
