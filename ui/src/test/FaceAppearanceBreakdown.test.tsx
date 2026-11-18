import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Detection, Face, FaceAppearance, FaceHostTrack, FaceImageSources } from "../api/types";
import { useState } from "react";
import { FaceAppearanceBreakdown, type FaceMoveOutcome, MoveOutcomeList } from "../components/FaceAppearanceBreakdown";
import { buildFaceCarouselSampleImageUrls } from "../components/faceComparisonImages";

const api = vi.hoisted(() => ({
  imageSources: vi.fn(),
  hostTracks: vi.fn(),
  split: vi.fn(),
  markNotPresent: vi.fn(),
  link: vi.fn(),
  mergeInto: vi.fn(),
  setCoverFromDetection: vi.fn(),
  deleteCover: vi.fn(),
  uploadCover: vi.fn(),
  findPerformers: vi.fn(),
}));

vi.mock("../api/client", () => ({
  faces: {
    ...api,
    detectionCropUrl: (detectionId: number) => `/crop/${detectionId}`,
  },
  performers: { find: api.findPerformers },
}));

function detection(id: number, hostId: number, observedAtSec: number, score: number, groupKey?: string): Detection {
  return {
    id,
    hostType: "video",
    hostId,
    observedAtSec,
    frameWidth: 1000,
    frameHeight: 1000,
    class: "face",
    score,
    x: 100,
    y: 100,
    w: 200,
    h: 200,
    groupKey,
  } as Detection;
}

function track(groupKey: string, detectionId: number, seconds: number): FaceHostTrack {
  return {
    groupKey,
    firstSeenSeconds: seconds,
    lastSeenSeconds: seconds + 2,
    representativeFrameSeconds: seconds,
    representativeDetectionId: detectionId,
    sampleCount: 2,
    detectionCount: 2,
    suggestedGroup: 0,
  };
}

function appearance(hostId: number, title: string): FaceAppearance {
  return {
    appearanceId: hostId,
    hostType: "video",
    hostId,
    title,
    thumbnailUrl: "",
    frameSampleCount: 4,
    retainedSpatialSampleCount: 4,
    segmentCount: 1,
  };
}

const face = { id: 5, coverImageUrl: "/faces/5/image" } as Face;
const appearances = [appearance(70, "First video"), appearance(71, "Second video")];
const unknownCover: FaceImageSources["cover"] = { hasStoredCover: true, detectionId: null, sourceOnFace: false };

function renderBreakdown(options: { canWrite?: boolean; canEditOccurrences?: boolean; detections?: Detection[] } = {}) {
  const onNavigate = vi.fn();
  const onChanged = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  // As the page does: the result of a move is shown outside the list.
  function Harness() {
    const [outcomes, setOutcomes] = useState<FaceMoveOutcome[] | null>(null);
    return (
      <>
        {outcomes ? (
          <MoveOutcomeList outcomes={outcomes} onNavigate={onNavigate} onDismiss={() => setOutcomes(null)} />
        ) : null}
        <FaceAppearanceBreakdown
          face={face}
          faceTitle="Mixed face"
          appearances={appearances}
          detections={options.detections ?? []}
          canWrite={options.canWrite ?? true}
          canEditOccurrences={options.canEditOccurrences ?? true}
          onNavigate={onNavigate}
          onChanged={onChanged}
          onMoved={setOutcomes}
        />
      </>
    );
  }
  render(
    <QueryClientProvider client={client}>
      <Harness />
    </QueryClientProvider>,
  );
  return { onNavigate, onChanged };
}

const select = (name: RegExp) => fireEvent.click(screen.getByRole("button", { name }));

describe("FaceAppearanceBreakdown", () => {
  beforeEach(() => {
    api.hostTracks.mockImplementation(async (_faceId: number, host: { hostId: number }) =>
      host.hostId === 70 ? [track("a", 11, 600), track("b", 12, 1844)] : [track("c", 21, 328)],
    );
    api.imageSources.mockImplementation(async (_faceId: number, ids: number[]) => ({
      cover: unknownCover,
      sources: ids.map((detectionId) => ({
        detectionId,
        hostType: "video",
        hostId: detectionId < 20 ? 70 : 71,
        hostTitle: detectionId < 20 ? "First video" : "Second video",
        observedAtSec: detectionId === 12 ? 1844 : 600,
        segmentId: detectionId === 12 ? 900 : null,
      })),
    }));
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("shows each video's appearances and opens the face segment, or the video at that time", async () => {
    const { onNavigate } = renderBreakdown();

    const first = await screen.findByRole("region", { name: "First video" });
    fireEvent.click(await within(first).findByRole("link", { name: "Open First video at 30:44" }));
    expect(onNavigate).toHaveBeenLastCalledWith({ page: "segment", id: 900 });

    fireEvent.click(within(first).getByRole("link", { name: "Open First video at 10:00" }));
    expect(onNavigate).toHaveBeenLastCalledWith({ page: "video", id: 70, seekTo: 600 });
    expect(within(screen.getByRole("region", { name: "Second video" })).getAllByRole("listitem")).toHaveLength(1);
  });

  it("starts a long video as a sample spread across it, and still selects every appearance", async () => {
    api.hostTracks.mockImplementation(async (_faceId: number, host: { hostId: number }) =>
      host.hostId === 70 ? Array.from({ length: 30 }, (_, index) => track(`t${index}`, 100 + index, index * 60)) : [],
    );
    api.markNotPresent.mockResolvedValue({ targetFaceId: 40, createdNewFace: true });
    renderBreakdown();

    const first = await screen.findByRole("region", { name: "First video" });
    await within(first).findByText("Showing 12 of 30 appearances, spread across the video.");
    expect(within(first).getAllByRole("listitem")).toHaveLength(12);
    // The sample runs from the first appearance to the last.
    expect(within(first).getByRole("button", { name: /Select the appearance in First video at 29:00/ })).toBeTruthy();
    await waitFor(() =>
      expect(api.imageSources).toHaveBeenCalledWith(5, [100, 103, 105, 108, 111, 113, 116, 118, 121, 124, 126, 129]),
    );

    fireEvent.click(within(first).getByRole("button", { name: "Show all 30" }));
    expect(within(first).getAllByRole("listitem")).toHaveLength(30);
    fireEvent.click(within(first).getByRole("button", { name: "Show fewer" }));
    expect(within(first).getAllByRole("listitem")).toHaveLength(12);

    fireEvent.click(within(first).getByRole("button", { name: "Select all 30" }));
    fireEvent.click(screen.getByRole("button", { name: /Not this person/ }));
    fireEvent.click(screen.getByRole("button", { name: "Move" }));
    await waitFor(() => expect(api.markNotPresent).toHaveBeenCalledWith(5, { hostType: "video", hostId: 70 }));
  });

  it("moves one of a video's appearances off the face and reports where it went", async () => {
    api.split.mockResolvedValue({ targetFaceId: 40, createdNewFace: false });
    const { onChanged } = renderBreakdown();

    await screen.findByRole("button", { name: /Select the appearance in First video at 10:00/ });
    select(/Select the appearance in First video at 10:00/);
    fireEvent.click(screen.getByRole("button", { name: /Not this person/ }));
    fireEvent.click(screen.getByRole("button", { name: "Move" }));

    await waitFor(() => expect(api.split).toHaveBeenCalledWith(5, { hostType: "video", hostId: 70, groupKeys: ["a"] }));
    expect(api.markNotPresent).not.toHaveBeenCalled();
    expect(await screen.findByText(/joined the/)).toBeTruthy();
    expect(screen.getByText(/which was left as it is/)).toBeTruthy();
    expect(onChanged).toHaveBeenCalledWith(["video"]);
  });

  it("says who the selected appearances are: whole videos are marked not present and new faces are linked", async () => {
    api.split.mockResolvedValue({ targetFaceId: 40, createdNewFace: false });
    api.markNotPresent.mockResolvedValue({ targetFaceId: 41, createdNewFace: true });
    api.link.mockResolvedValue({});
    api.findPerformers.mockResolvedValue({ items: [{ id: 9, name: "Right Person" }] });
    renderBreakdown();

    await screen.findByRole("button", { name: /Select the appearance in Second video/ });
    select(/Select the appearance in First video at 30:44/);
    select(/Select the appearance in Second video/);
    fireEvent.click(screen.getByRole("button", { name: /This is/ }));
    fireEvent.change(screen.getByRole("textbox", { name: "Search performers" }), { target: { value: "Right" } });
    fireEvent.click(await screen.findByRole("button", { name: "Right Person" }));
    fireEvent.click(screen.getByRole("button", { name: "Move" }));

    await waitFor(() => expect(api.link).toHaveBeenCalledWith(41, { performerId: 9 }));
    expect(api.split).toHaveBeenCalledWith(5, { hostType: "video", hostId: 70, groupKeys: ["b"] });
    expect(api.markNotPresent).toHaveBeenCalledWith(5, { hostType: "video", hostId: 71 });
    // The appearances that joined a face which already existed do not relabel that face.
    expect(api.link).toHaveBeenCalledTimes(1);
    expect(await screen.findByText(/linked to Right Person/)).toBeTruthy();
  });

  it("combines the new faces made by naming one person into a single face", async () => {
    api.split.mockResolvedValue({ targetFaceId: 41, createdNewFace: true });
    api.markNotPresent.mockResolvedValue({ targetFaceId: 42, createdNewFace: true });
    api.mergeInto.mockResolvedValue({});
    api.link.mockResolvedValue({});
    api.findPerformers.mockResolvedValue({ items: [{ id: 9, name: "Right Person" }] });
    renderBreakdown();

    await screen.findByRole("button", { name: /Select the appearance in Second video/ });
    select(/Select the appearance in First video at 10:00/);
    select(/Select the appearance in Second video/);
    fireEvent.click(screen.getByRole("button", { name: /This is/ }));
    fireEvent.change(screen.getByRole("textbox", { name: "Search performers" }), { target: { value: "Right" } });
    fireEvent.click(await screen.findByRole("button", { name: "Right Person" }));
    fireEvent.click(screen.getByRole("button", { name: "Move" }));

    await waitFor(() => expect(api.mergeInto).toHaveBeenCalledWith(42, { targetFaceId: 41 }));
    // Both are linked before they are combined, so the merged face's own segments are named too.
    expect(api.link.mock.calls).toEqual([
      [41, { performerId: 9 }],
      [42, { performerId: 9 }],
    ]);
    expect(Math.max(...api.link.mock.invocationCallOrder)).toBeLessThan(api.mergeInto.mock.invocationCallOrder[0]);
    expect(await screen.findAllByText(/linked to Right Person/)).toHaveLength(2);
  });

  it("does not combine new faces when no person is named, since they may be different people", async () => {
    api.split.mockResolvedValue({ targetFaceId: 41, createdNewFace: true });
    api.markNotPresent.mockResolvedValue({ targetFaceId: 42, createdNewFace: true });
    renderBreakdown();

    await screen.findByRole("button", { name: /Select the appearance in Second video/ });
    select(/Select the appearance in First video at 10:00/);
    select(/Select the appearance in Second video/);
    fireEvent.click(screen.getByRole("button", { name: /Not this person/ }));
    fireEvent.click(screen.getByRole("button", { name: "Move" }));

    expect(await screen.findAllByText(/moved to a/)).toHaveLength(2);
    expect(api.mergeInto).not.toHaveBeenCalled();
    expect(api.link).not.toHaveBeenCalled();
  });

  it("shows an appearance that came with a merged face by one of the face's own detections", async () => {
    api.hostTracks.mockResolvedValue([
      { groupKey: "merged", firstSeenSeconds: 600, sampleCount: 2, detectionCount: 0, suggestedGroup: 0 },
    ]);
    renderBreakdown({ detections: [detection(31, 70, 600, 0.9, "merged:span-1")] });

    const first = await screen.findByRole("region", { name: "First video" });
    expect(await within(first).findByRole("img", { name: "Face in First video at 10:00" })).toHaveProperty(
      "src",
      expect.stringContaining("/crop/31"),
    );
  });

  it("marks a video not present when the ticked appearances turn out to be all of them", async () => {
    api.split.mockRejectedValue(
      new Error(
        'API Error 400: {"error":"Separating every appearance would leave the face empty here — mark it not present instead."}',
      ),
    );
    api.markNotPresent.mockResolvedValue({ targetFaceId: 41, createdNewFace: true });
    renderBreakdown();

    await screen.findByRole("button", { name: /Select the appearance in First video at 10:00/ });
    select(/Select the appearance in First video at 10:00/);
    fireEvent.click(screen.getByRole("button", { name: /Not this person/ }));
    fireEvent.click(screen.getByRole("button", { name: "Move" }));

    await waitFor(() => expect(api.markNotPresent).toHaveBeenCalledWith(5, { hostType: "video", hostId: 70 }));
    expect(await screen.findByText(/moved to a/)).toBeTruthy();
  });

  it("says so when a video's appearances had already moved with an earlier one, and shows a server error plainly", async () => {
    api.split.mockRejectedValue(
      new Error('API Error 400: {"error":"That face is not present on the specified host."}'),
    );
    api.markNotPresent.mockRejectedValue(new Error('API Error 500: {"error":"The provider failed."}'));
    renderBreakdown();

    await screen.findByRole("button", { name: /Select the appearance in Second video/ });
    select(/Select the appearance in First video at 10:00/);
    select(/Select the appearance in Second video/);
    fireEvent.click(screen.getByRole("button", { name: /Not this person/ }));
    fireEvent.click(screen.getByRole("button", { name: "Move" }));

    expect(await screen.findByText(/already moved along with/)).toBeTruthy();
    expect(screen.getByText("The provider failed.")).toBeTruthy();
    // What failed stays ticked for another try; what is done does not.
    expect(screen.getByText(/1 appearance in 1 place selected/)).toBeTruthy();
  });

  it("says when the cover's source is not recorded, removes it, and sets it from an appearance", async () => {
    api.deleteCover.mockResolvedValue(undefined);
    api.setCoverFromDetection.mockResolvedValue(undefined);
    const { onChanged } = renderBreakdown();

    expect(await screen.findByText(/source not recorded/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Remove cover" }));
    expect(api.deleteCover).not.toHaveBeenCalled();
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Remove cover" }));
    await waitFor(() => expect(api.deleteCover).toHaveBeenCalledWith(5));

    fireEvent.click(await screen.findByRole("button", { name: "Use as cover: First video at 30:44" }));
    await waitFor(() => expect(api.setCoverFromDetection).toHaveBeenCalledWith(5, 12));
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(2));
  });

  it("does not take one ticked detection group for the whole video when the extension reports no appearances", async () => {
    api.hostTracks.mockResolvedValue([]);
    api.split.mockResolvedValue({ targetFaceId: 40, createdNewFace: true });
    renderBreakdown({ detections: [detection(11, 70, 600, 0.9, "a"), detection(12, 70, 1844, 0.8, "b")] });

    await screen.findByRole("button", { name: /Select the appearance in First video at 10:00/ });
    select(/Select the appearance in First video at 10:00/);
    fireEvent.click(screen.getByRole("button", { name: /Not this person/ }));
    fireEvent.click(screen.getByRole("button", { name: "Move" }));

    await waitFor(() => expect(api.split).toHaveBeenCalledWith(5, { hostType: "video", hostId: 70, groupKeys: ["a"] }));
    expect(api.markNotPresent).not.toHaveBeenCalled();
  });

  it("without an extension that edits appearances, groups the face's own detections and offers no selection", async () => {
    renderBreakdown({
      canEditOccurrences: false,
      detections: [
        detection(11, 70, 600, 0.9, "t1:span-1"),
        detection(13, 70, 610, 0.8, "t1:span-2"),
        detection(12, 70, 1844, 0.7, "t2"),
      ],
    });

    const first = await screen.findByRole("region", { name: "First video" });
    await waitFor(() => expect(within(first).getAllByRole("listitem")).toHaveLength(2));
    expect(api.hostTracks).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: /Select the appearance/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "Select all" })).toBeNull();
  });
});

describe("buildFaceCarouselSampleImageUrls", () => {
  const ranked = [1, 2, 3, 4].map((id) => detection(id, 70, id * 10, 0.9 - id * 0.01));
  const crop = (id: number) => `/crop/${id}`;

  it("leaves out the best detection after a cover of unknown source, and the cover's own detection when it is known", () => {
    expect(buildFaceCarouselSampleImageUrls(null, ranked, crop)).toEqual(["/crop/1", "/crop/2", "/crop/3"]);
    expect(buildFaceCarouselSampleImageUrls(face, ranked, crop)).toEqual(["/crop/2", "/crop/3"]);
    expect(buildFaceCarouselSampleImageUrls(face, ranked, crop, 2)).toEqual(["/crop/1", "/crop/3"]);
  });
});
