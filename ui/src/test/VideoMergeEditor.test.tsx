import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { VideoMergeEditor } from "../components/VideoMergeEditor";
import { buildVideoMergeDiff, videoMergeMetadata } from "../components/VideoMergeReview";
import { defaultDiffSelection } from "../components/MetadataDiff";
import { MergeDialog } from "../components/MergeDialog";
import type { Video, VideoFile } from "../api/types";

const api = vi.hoisted(() => ({
  get: vi.fn(),
  merge: vi.fn(),
  assessMerge: vi.fn(),
  findTags: vi.fn(),
  assess: vi.fn(),
  apply: vi.fn(),
}));
vi.mock("../api/client", () => ({
  videos: {
    get: api.get,
    merge: api.merge,
    assessMerge: api.assessMerge,
    screenshotUrl: (id: number) => `/cover/${id}`,
  },
  tags: { find: api.findTags },
  videoAlignments: { assess: api.assess, apply: api.apply },
}));
const file = (id: number, overrides: Partial<VideoFile> = {}): VideoFile => ({
  id,
  path: `/library/video-${id}.mp4`,
  basename: `video-${id}.mp4`,
  format: "mp4",
  width: 1920,
  height: 1080,
  duration: 600,
  videoCodec: "h264",
  audioCodec: "aac",
  frameRate: 30,
  bitRate: 8_000_000,
  size: 600_000_000,
  fingerprints: [],
  ...overrides,
});
const video = (id: number): Video => ({
  id,
  title: `Title ${id}`,
  details: `Description ${id}`,
  organized: false,
  urls: [],
  tags: [],
  performers: [],
  files: [],
  galleries: [],
  remoteIds: [],
  groups: [],
  createdAt: "",
  updatedAt: "",
});
const attach = expect.objectContaining({ mode: "attach" });
function setup(props: Partial<Parameters<typeof VideoMergeEditor>[0]> = {}) {
  const onClose = vi.fn(),
    onMerged = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const view = render(
    <QueryClientProvider client={client}>
      <VideoMergeEditor sourceIds={[1]} targetId={2} onClose={onClose} onMerged={onMerged} {...props} />
    </QueryClientProvider>,
  );
  return { ...view, onClose, onMerged };
}
beforeEach(() => {
  vi.clearAllMocks();
  api.get.mockImplementation(async (id: number) => video(id));
  api.merge.mockResolvedValue(video(2));
  api.assessMerge.mockResolvedValue([]);
  api.findTags.mockResolvedValue({ items: [{ id: 9, name: "Added tag" }] });
  api.assess.mockResolvedValue({
    sourceFileId: 2,
    targetFileId: 1,
    sourceDuration: 600,
    targetDuration: 600,
    equivalent: true,
    canAlign: true,
    dependencyCount: 0,
    dependencyCounts: {},
  });
  api.apply.mockResolvedValue({ primaryFileId: 1 });
});
describe("VideoMergeEditor", () => {
  it("labels remote IDs with configured server names without changing merge identity", () => {
    const source = {
      ...video(1),
      remoteIds: [{ endpoint: "https://metadata.example/graphql/", remoteId: "source-id" }],
    };
    const target = {
      ...video(2),
      remoteIds: [{ endpoint: "https://www.other.example/graphql", remoteId: "target-id" }],
    };
    const diff = buildVideoMergeDiff([source], target, [], undefined, [
      { endpoint: "https://metadata.example/graphql", name: "Example catalog" },
    ]);
    const field = diff.fields.find((field) => field.key === "remoteIds")!;
    expect(field.render!(source.remoteIds[0])).toBe("Example catalog\nsource-id");
    expect(field.itemLabel!(source.remoteIds[0])).toBe("Example catalog: source-id");
    expect(field.render!(target.remoteIds[0])).toBe("other.example\ntarget-id");
    const selection = defaultDiffSelection(diff.fields, diff.source, diff.target);
    expect(videoMergeMetadata(selection, [source], target).remoteIds).toEqual(
      expect.arrayContaining([...source.remoteIds, ...target.remoteIds]),
    );
  });
  it("saves mixed choices and a library tag in a single merge request", async () => {
    api.get.mockImplementation(async (id: number) => ({
      ...video(id),
      urls: id === 2 ? ["https://merge.example/target"] : [],
    }));
    const { onMerged } = setup();
    fireEvent.click(await screen.findByLabelText("Title from source"));
    // Relationships are edited with the same selector as the video's edit form: search, add, x to drop.
    const user = userEvent.setup();
    await user.type(screen.getByPlaceholderText("Search tags..."), "Added");
    await user.click(await screen.findByRole("option", { name: /Added tag/ }));
    expect(screen.getByRole("button", { name: "Remove Added tag" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Remove Added tag" }));
    expect(screen.queryByRole("button", { name: "Remove Added tag" })).not.toBeInTheDocument();
    await user.type(screen.getByPlaceholderText("Search tags..."), "Added");
    await user.click(await screen.findByRole("option", { name: /Added tag/ }));
    expect(screen.getAllByText(/1 tag added/).length).toBeGreaterThan(0);
    await user.type(screen.getByLabelText("Add a URL to the result"), "https://merge.example/new");
    await user.click(screen.getByRole("button", { name: "Add" }));
    expect(screen.getByRole("button", { name: "Remove URLs: https://merge.example/new" })).toBeInTheDocument();
    fireEvent.click(await screen.findByRole("button", { name: "Merge & attach 1 copy" }));
    await waitFor(() => expect(onMerged).toHaveBeenCalledWith(2));
    expect(api.merge).toHaveBeenCalledWith(
      2,
      [1],
      expect.objectContaining({
        tagIds: [9],
        urls: expect.arrayContaining(["https://merge.example/target", "https://merge.example/new"]),
        fields: expect.objectContaining({ title: "source", details: "target" }),
      }),
      attach,
    );
  });
  it("cancels without saving and initializes a new comparison with target defaults", async () => {
    const first = setup();
    fireEvent.click(await screen.findByLabelText("Title from source"));
    fireEvent.click(screen.getByRole("button", { name: "Back" }));
    expect(first.onClose).toHaveBeenCalled();
    expect(api.merge).not.toHaveBeenCalled();
    first.unmount();
    setup({ targetId: 3 });
    expect(await screen.findByLabelText("Title from target")).toBeChecked();
  });
  it("keeps the cover row visible and falls back after a source preview fails", async () => {
    setup();
    fireEvent.click(await screen.findByLabelText("Cover from source"));
    fireEvent.error(screen.getAllByAltText("Video cover").find((image) => image.getAttribute("src") === "/cover/1")!);
    expect(screen.getByLabelText("Cover from source")).toBeDisabled();
    fireEvent.error(screen.getAllByAltText("Video cover").find((image) => image.getAttribute("src") === "/cover/2")!);
    expect(screen.getByRole("group", { name: /Cover/ })).toBeInTheDocument();
    expect(screen.getAllByText("No cover available").length).toBeGreaterThan(0);
    fireEvent.click(await screen.findByRole("button", { name: "Merge & attach 1 copy" }));
    await waitFor(() =>
      expect(api.merge).toHaveBeenCalledWith(
        2,
        [1],
        expect.objectContaining({ fields: expect.objectContaining({ cover: "target" }) }),
        attach,
      ),
    );
  });
  it("keeps derived tags read-only instead of promoting them into editable links", async () => {
    api.get.mockImplementation(async (id: number) => ({
      ...video(id),
      tags: [{ id: id + 10, name: "Derived " + id, canRemove: false }],
    }));
    setup();
    await screen.findByLabelText("Title from target");
    expect(screen.getByLabelText("Derived tags from source")).toBeDisabled();
    fireEvent.click(await screen.findByRole("button", { name: "Merge & attach 1 copy" }));
    await waitFor(() =>
      expect(api.merge).toHaveBeenCalledWith(2, [1], expect.objectContaining({ tagIds: [] }), attach),
    );
  });
  it("keeps the draft after a failed save", async () => {
    api.merge.mockRejectedValue(new Error("Merge failed"));
    setup();
    fireEvent.click(await screen.findByLabelText("Title from source"));
    fireEvent.click(await screen.findByRole("button", { name: "Merge & attach 1 copy" }));
    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(screen.getByLabelText("Title from source")).toBeChecked();
  });
  it("compares the files side by side and marks the best value", async () => {
    api.get.mockImplementation(async (id: number) => ({
      ...video(id),
      files: [file(id, id === 1 ? { width: 3840, height: 2160, size: 2_000_000_000 } : { duration: 600 })],
      primaryFileId: id,
    }));
    setup();
    const files = await screen.findByRole("region", { name: "Files" });
    const resolution = within(files).getByRole("row", { name: /Resolution/ });
    const cells = within(resolution).getAllByRole("cell");
    expect(cells[0]).toHaveTextContent("3840×2160");
    expect(cells[0].className).toContain("text-emerald-300");
    expect(cells[1].className).not.toContain("text-emerald-300");
    expect(within(files).getByRole("row", { name: /Duration/ })).toHaveTextContent("10:00");
    expect(within(files).getByRole("row", { name: /Location/ })).toHaveTextContent("/library");
    expect(screen.getByText(/1 file attached/)).toBeInTheDocument();
  });
  it("sends the files decision with the merge and needs permission to delete from disk", async () => {
    api.get.mockImplementation(async (id: number) => ({ ...video(id), files: [file(id)], primaryFileId: id }));
    const first = setup();
    fireEvent.click(await screen.findByLabelText(/Remove the merged video's 1 file/));
    expect(screen.queryByLabelText(/Delete the files from disk/)).not.toBeInTheDocument();
    expect(screen.getByText(/You don't have permission to delete video files/)).toBeInTheDocument();
    expect(screen.getByText(/1 file removed from Cove/)).toBeInTheDocument();
    first.unmount();

    setup({ canDeleteFiles: true });
    fireEvent.click(await screen.findByLabelText(/Remove the merged video's 1 file/));
    fireEvent.click(screen.getByLabelText(/Delete the files from disk/));
    fireEvent.click(screen.getByLabelText(/Delete generated previews/));
    expect(screen.getByText(/1 file deleted from disk/)).toBeInTheDocument();
    fireEvent.click(await screen.findByRole("button", { name: "Merge & remove 1 copy" }));
    await waitFor(() =>
      expect(api.merge).toHaveBeenCalledWith(2, [1], expect.anything(), {
        mode: "remove",
        deleteFiles: true,
        deleteGenerated: false,
      }),
    );
  });
  it("combines several merged videos into one incoming side with an origin badge where they disagree", async () => {
    api.get.mockImplementation(async (id: number) => ({
      ...video(id),
      title: id === 2 ? "" : id === 3 ? "" : `Title ${id}`,
      details: id === 2 ? "Kept description" : `Description ${id}`,
      director: id === 3 ? "Director from three" : undefined,
      tags: [{ id: id * 10, name: `Tag ${id}` }],
    }));
    setup({ sourceIds: [3, 1] });
    await screen.findByText("2 videos combined");
    expect(screen.queryByRole("button", { name: "Swap" })).not.toBeInTheDocument();
    const details = screen.getByRole("group", { name: "Description" });
    expect(within(details).getByText("From Title 1")).toBeInTheDocument();
    expect(within(details).getByText("Description 1")).toBeInTheDocument();
    expect(screen.getByLabelText("Title from source")).toBeChecked();
    expect(screen.getByLabelText("Director from source")).toBeChecked();
    expect(screen.getByRole("button", { name: "Remove Tag 1" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Tag 3" })).toBeInTheDocument();
    fireEvent.click(await screen.findByRole("button", { name: "Merge & attach 2 copies" }));
    await waitFor(() =>
      expect(api.merge).toHaveBeenCalledWith(
        2,
        [1, 3],
        expect.objectContaining({
          tagIds: expect.arrayContaining([10, 20, 30]),
          fields: expect.objectContaining({ title: "source" }),
        }),
        attach,
      ),
    );
  });
});

describe("VideoMergeEditor combined incoming side", () => {
  it("shows the cover of the first copy that has a stored cover, as the backend reads it", async () => {
    api.get.mockImplementation(async (id: number) => ({
      ...video(id),
      imagePath: id === 3 ? "/stored-cover/3" : undefined,
    }));
    setup({ sourceIds: [1, 3] });
    await screen.findByText("2 videos combined");
    const cover = screen.getByRole("group", { name: /Cover/ });
    expect(within(cover).getAllByAltText("Video cover")[0]).toHaveAttribute("src", "/stored-cover/3");
  });
  it("skips whitespace-only values and lets a real one win, as the backend does", () => {
    const blank = { ...video(1), title: "   " };
    const real = { ...video(3), title: "Real title" };
    const diff = buildVideoMergeDiff([blank, real], { ...video(2), title: "" });
    expect(diff.source.values.title).toBe("Real title");
    expect(diff.source.provenance?.title).toBeUndefined();
  });
  it("treats a copy's organized flag as a fill, matching the merge without choices", () => {
    const diff = buildVideoMergeDiff([{ ...video(1), organized: true }], video(2));
    expect(defaultDiffSelection(diff.fields, diff.source, diff.target).organized).toBe("source");
  });
  it("warns before the merge which markers would not follow a removed file", async () => {
    api.get.mockImplementation(async (id: number) => ({ ...video(id), files: [file(id)], primaryFileId: id }));
    api.assessMerge.mockResolvedValue([{ videoId: 1, filesEquivalent: false, timelineItemCount: 3 }]);
    setup();
    await screen.findByLabelText(/Remove the merged video's 1 file/);
    expect(api.assessMerge).not.toHaveBeenCalled();
    fireEvent.click(screen.getByLabelText(/Remove the merged video's 1 file/));
    expect(await screen.findByRole("alert")).toHaveTextContent("3 timeline items on Title 1 will not be carried over");
    expect(api.assessMerge).toHaveBeenCalledWith(2, [1], expect.anything());
    fireEvent.click(screen.getByLabelText(/Attach 1 file to the kept video/));
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });
  it("keeps the files decision when the roles are swapped", async () => {
    api.get.mockImplementation(async (id: number) => ({ ...video(id), files: [file(id)], primaryFileId: id }));
    setup();
    fireEvent.click(await screen.findByLabelText(/Remove the merged video's 1 file/));
    fireEvent.click(screen.getByRole("button", { name: "Swap" }));
    expect(await screen.findByLabelText(/Remove the merged video's 1 file/)).toBeChecked();
    fireEvent.click(await screen.findByRole("button", { name: "Merge & remove 1 copy" }));
    await waitFor(() =>
      expect(api.merge).toHaveBeenCalledWith(1, [2], expect.anything(), expect.objectContaining({ mode: "remove" })),
    );
  });
});

describe("MergeDialog", () => {
  it("opens the review for any number of videos with the kept one first", () => {
    const renderReview = vi.fn(() => <div>review</div>);
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <MergeDialog
          open
          onClose={vi.fn()}
          entityType="video"
          items={[
            { id: 5, name: "Five" },
            { id: 6, name: "Six" },
            { id: 7, name: "Seven" },
          ]}
          onMerge={vi.fn()}
          queryKey="videos"
          renderReview={renderReview}
        />
      </QueryClientProvider>,
    );
    expect(screen.getAllByText("Merge in, then remove")).toHaveLength(2);
    fireEvent.click(screen.getByLabelText(/Six/));
    fireEvent.click(screen.getByRole("button", { name: "Compare metadata" }));
    expect(renderReview).toHaveBeenCalledWith(6, [5, 7], expect.any(Function));
    expect(screen.getByText("review")).toBeInTheDocument();
  });
});

describe("VideoMergeEditor primary file", () => {
  const withFiles = () =>
    api.get.mockImplementation(async (id: number) => ({ ...video(id), files: [file(id)], primaryFileId: id }));

  it("keeps the kept video's primary file by default and switches directly when the files are equivalent", async () => {
    withFiles();
    const { onMerged, onClose } = setup();
    expect(await screen.findByLabelText("Keep video-2.mp4 as the primary file")).toBeChecked();
    fireEvent.click(screen.getByLabelText("Make video-1.mp4 the primary file"));
    expect(screen.getAllByText(/becomes the primary file after the merge/).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "Merge & attach 1 copy" }));
    await waitFor(() => expect(onMerged).toHaveBeenCalledWith(2));
    expect(api.merge).toHaveBeenCalledWith(2, [1], expect.anything(), attach);
    expect(api.assess).toHaveBeenCalledWith(2, 1);
    expect(api.apply).toHaveBeenCalledWith(2, { fileId: 1, resolution: "direct", expectedPrimaryFileId: 2 });
    expect(onClose).toHaveBeenCalled();
  });

  it("opens the alignment dialog after the merge when the files differ and timed content is affected", async () => {
    withFiles();
    api.assess.mockResolvedValue({
      sourceFileId: 2,
      targetFileId: 1,
      sourceDuration: 600,
      targetDuration: 660,
      equivalent: false,
      canAlign: true,
      dependencyCount: 2,
      dependencyCounts: { segment: 2 },
    });
    const { onMerged, onClose } = setup();
    fireEvent.click(await screen.findByLabelText("Make video-1.mp4 the primary file"));
    fireEvent.click(screen.getByRole("button", { name: "Merge & attach 1 copy" }));
    expect(await screen.findByRole("heading", { name: "Set primary video file" })).toBeInTheDocument();
    expect(api.merge).toHaveBeenCalledTimes(1);
    expect(api.apply).not.toHaveBeenCalled();
    expect(onMerged).not.toHaveBeenCalled();
    // Leaving the dialog still closes the merge: the videos are merged already.
    fireEvent.click(screen.getAllByRole("button", { name: "Cancel" })[0]);
    await waitFor(() => expect(onMerged).toHaveBeenCalledWith(2));
    expect(onClose).toHaveBeenCalled();
  });

  it("drops the primary choice when the copy's file is removed instead of attached", async () => {
    withFiles();
    setup();
    fireEvent.click(await screen.findByLabelText("Make video-1.mp4 the primary file"));
    fireEvent.click(screen.getByLabelText(/Remove the merged video's 1 file/));
    expect(screen.queryByLabelText("Make video-1.mp4 the primary file")).not.toBeInTheDocument();
    fireEvent.click(screen.getByLabelText(/Attach 1 file to the kept video/));
    expect(screen.getByLabelText("Keep video-2.mp4 as the primary file")).toBeChecked();
    fireEvent.click(screen.getByRole("button", { name: "Merge & attach 1 copy" }));
    await waitFor(() => expect(api.merge).toHaveBeenCalled());
    expect(api.assess).not.toHaveBeenCalled();
  });

  it("names each copy's file in a merge of several videos", async () => {
    withFiles();
    setup({ sourceIds: [1, 3], targetId: 2 });
    expect(await screen.findByLabelText("Make video-1.mp4 the primary file")).toBeInTheDocument();
    expect(screen.getByLabelText("Make video-3.mp4 the primary file")).toBeInTheDocument();
    expect(screen.getByRole("radiogroup", { name: "Primary file after the merge" })).toBeInTheDocument();
  });

  it("is done without assessing when the merge already adopted the chosen file as primary", async () => {
    api.get.mockImplementation(async (id: number) => ({
      ...video(id),
      files: [file(id)],
      primaryFileId: id === 2 ? null : id,
    }));
    const { onMerged } = setup();
    fireEvent.click(await screen.findByLabelText("Make video-1.mp4 the primary file"));
    // After the merge the kept video reports the copy's file as its primary.
    api.merge.mockImplementation(async () => {
      api.get.mockImplementation(async (id: number) => ({ ...video(id), files: [file(1), file(2)], primaryFileId: 1 }));
      return video(2);
    });
    fireEvent.click(screen.getByRole("button", { name: "Merge & attach 1 copy" }));
    await waitFor(() => expect(onMerged).toHaveBeenCalledWith(2));
    expect(api.assess).not.toHaveBeenCalled();
    expect(api.apply).not.toHaveBeenCalled();
  });

  it("falls back to the dialog when the direct switch is refused, without merging again", async () => {
    withFiles();
    const refusal = new Error("The primary file changed while this dialog was open.");
    api.apply.mockRejectedValue(refusal);
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const { onMerged } = setup();
    fireEvent.click(await screen.findByLabelText("Make video-1.mp4 the primary file"));
    fireEvent.click(screen.getByRole("button", { name: "Merge & attach 1 copy" }));
    expect(await screen.findByRole("heading", { name: "Set primary video file" })).toBeInTheDocument();
    // The review is gone with the merged copies: nothing can merge them a second time.
    expect(screen.queryByRole("button", { name: "Merge & attach 1 copy" })).not.toBeInTheDocument();
    expect(api.merge).toHaveBeenCalledTimes(1);
    expect(onMerged).not.toHaveBeenCalled();
    expect(warn).toHaveBeenCalledWith("Setting the primary file after the merge did not go through directly.", refusal);
    warn.mockRestore();
  });
});
