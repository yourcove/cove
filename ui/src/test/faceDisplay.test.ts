import { describe, expect, it } from "vitest";
import type { Face } from "../api/types";
import { faceDisplayName, faceShownName, faceStoredName } from "../utils/faceDisplay";

function makeFace(overrides: Partial<Face> = {}): Face {
  return {
    id: 37008,
    ignored: false,
    detectionCount: 0,
    videoCount: 0,
    imageCount: 0,
    createdAt: "2026-07-03T00:00:00Z",
    updatedAt: "2026-07-03T00:00:00Z",
    appearanceCount: 0,
    frameSampleCount: 0,
    ...overrides,
  };
}

const suggestion = { performerId: 12, performerName: "Nasia Jansen", confidence: 88 };

describe("faceDisplayName", () => {
  it("prefers the linked performer, disambiguated when they own several faces", () => {
    expect(faceDisplayName(makeFace({ performerId: 4, performerName: "Harley Dean" }))).toBe("Harley Dean");
    expect(
      faceDisplayName(
        makeFace({ performerId: 4, performerName: "Harley Dean", performerFaceIndex: 2, performerFaceCount: 3 }),
      ),
    ).toBe("Harley Dean 2");
  });

  it("keeps a real label ahead of a suggestion", () => {
    const face = makeFace({ label: "Amber Moore", primarySourceKey: "face-2076", topSuggestion: suggestion });
    expect(faceDisplayName(face)).toBe("Amber Moore");
  });

  it("falls back to the suggested performer when the label is only the cluster's source key", () => {
    const face = makeFace({ label: "face-37008", primarySourceKey: "face-37008", topSuggestion: suggestion });
    expect(faceDisplayName(face)).toBe("Nasia Jansen");
    expect(faceStoredName(face)).toBeUndefined();
  });

  it("falls back to the suggested performer when the face has no label at all", () => {
    expect(faceDisplayName(makeFace({ topSuggestion: suggestion }))).toBe("Nasia Jansen");
  });

  it("still shows the placeholder id when nothing names the face", () => {
    expect(faceDisplayName(makeFace({ label: "face-37008", primarySourceKey: "face-37008" }))).toBe("Face #37008");
    expect(faceDisplayName(makeFace())).toBe("Face #37008");
  });

  it("does not mistake a label that merely resembles a source key for the placeholder", () => {
    expect(
      faceDisplayName(makeFace({ label: "face-1", primarySourceKey: "face-37008", topSuggestion: suggestion })),
    ).toBe("face-1");
  });
});

describe("faceShownName", () => {
  it("shows a face's own name, else its suggestion flagged as one", () => {
    expect(
      faceShownName(makeFace({ label: "face-37008", primarySourceKey: "face-37008", topSuggestion: suggestion })),
    ).toEqual({ name: "Nasia Jansen", suggested: true });
    expect(
      faceShownName(makeFace({ label: "Amber Moore", primarySourceKey: "face-2076", topSuggestion: suggestion })),
    ).toEqual({ name: "Amber Moore", suggested: false });
    expect(
      faceShownName(makeFace({ performerId: 4, performerName: "Harley Dean", topSuggestion: suggestion })),
    ).toEqual({ name: "Harley Dean", suggested: false });
    expect(faceShownName(makeFace({ label: "face-37008", primarySourceKey: "face-37008" }))).toBeUndefined();
  });
});
