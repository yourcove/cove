import { describe, expect, it } from "vitest";
import { getApiValidationFailureDetail, getRequestFailureDetail } from "../utils/requestFailure";

describe("API validation failure details", () => {
  it("shows the first ASP.NET validation problem detail", () => {
    const error = new Error(
      `API Error 400: ${JSON.stringify({
        title: "One or more validation errors occurred.",
        errors: { Password: ["Password must be 8-200 characters."] },
      })}`,
    );

    expect(getApiValidationFailureDetail(error)).toBe("Password must be 8-200 characters.");
  });

  it("shows safe conflict recovery instructions from the migration API", () => {
    const message = "Cove 1.3.0 cannot upgrade this database. Run Cove 1.2.x cleanup first.";
    const error = new Error(
      `API Error 409: ${JSON.stringify({
        code: "NAME_RULE_CONFLICTS",
        message,
        unresolvedGroupCount: 2,
        unresolvedClaimCount: 4,
      })}`,
    );

    expect(getApiValidationFailureDetail(error)).toBe(message);
  });

  it("shows safe conflict recovery instructions for mutation failures", () => {
    const message =
      "This tag can’t be deleted because it is still referenced by:\n• Library Helper\n• Segment Studio\nRemove or retag the related records in the listed extensions, then try again.";
    const error = new Error(
      `API Error 409: ${JSON.stringify({
        code: "TAG_DELETE_EXTENSION_REFERENCES",
        message,
        extensions: [
          { id: "com.example.library-helper", name: "Library Helper" },
          { id: "com.example.segment-studio", name: "Segment Studio" },
        ],
      })}`,
    );

    expect(getRequestFailureDetail(error, "available")).toBe(message);
  });
});
