import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { VideoCreateModal } from "../components/VideoCreateModal";
import type { Video } from "../api/types";
const api = vi.hoisted(() => ({ splitFile: vi.fn(), create: vi.fn() }));
vi.mock("../api/client", () => ({ videos: api }));
vi.mock("../hooks/useFileBackedCreatePreferences", () => ({ useFileBackedCreatePreferences: () => ({}) }));
vi.mock("../components/FileBackedCreateSource", () => ({ FileBackedCreateSource: () => <div>Source selection</div> }));
vi.mock("../components/StudioSelector", () => ({
  StudioSelector: ({ value, onChange }: any) => (
    <input aria-label="Studio" value={value ?? ""} onChange={(e) => onChange(Number(e.target.value))} />
  ),
}));
vi.mock("../components/EntityReferenceSelector", () => ({
  EntityReferenceMultiSelector: ({ entityType, values, onChange }: any) => (
    <input
      aria-label={entityType}
      value={values.join(",")}
      onChange={(e) => onChange(e.target.value ? e.target.value.split(",").map(Number) : [])}
    />
  ),
}));
vi.mock("../components/shared", () => ({
  CustomFieldsEditor: ({ value, onChange, onFieldChange }: any) => (
    <input
      aria-label="Custom"
      value={JSON.stringify(value)}
      onChange={(e) => {
        onFieldChange("one");
        onChange(JSON.parse(e.target.value));
      }}
    />
  ),
}));
const source = {
  organized: false,
  files: [],
  groups: [],
  remoteIds: [],
  createdAt: "",
  updatedAt: "",
  id: 1,
  title: "Source",
  code: "Code",
  details: "Details",
  director: "Director",
  date: "2024-03",
  isVr: true,
  studioId: 3,
  urls: ["https://example.com"],
  tags: [{ id: 4, name: "Tag", favorite: false, organized: false, aliases: [] }],
  performers: [{ id: 5, name: "Performer", favorite: false }],
  galleries: [{ id: 6 }],
  customFields: { one: "original", two: "other" },
} as Video;
function mount(split = true, initialTitle?: string) {
  const close = vi.fn(),
    created = vi.fn();
  const result = render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
      <VideoCreateModal
        open
        initialTitle={initialTitle}
        onClose={close}
        onCreated={created}
        split={split ? { source, ownerId: 1, fileId: 2, filename: "secondary.mp4" } : undefined}
      />
    </QueryClientProvider>,
  );
  return { ...result, close, created };
}
describe("video creation and splitting", () => {
  beforeEach(() => vi.resetAllMocks());
  it("prefills and trims the title for regular video creation", () => {
    mount(false, "  New video  ");

    expect(screen.getByPlaceholderText("Video title")).toHaveValue("New video");
  });
  it("starts blank and cancellation sends no mutation", () => {
    const { close } = mount();
    expect(screen.getByPlaceholderText("Video title")).toHaveValue("");
    expect(screen.getByRole("dialog", { name: "Split into a new scene" })).toHaveAttribute("aria-modal", "true");
    expect(screen.queryByText("Source selection")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(close).toHaveBeenCalledOnce();
    expect(api.splitFile).not.toHaveBeenCalled();
  });
  it("copies all fields while preserving edits, cleared fields and individual custom fields", async () => {
    api.splitFile.mockResolvedValue({ videoId: 9 });
    const { created } = mount();
    fireEvent.change(screen.getByPlaceholderText("Video title"), { target: { value: "New title" } });
    fireEvent.change(screen.getByPlaceholderText("Studio code"), { target: { value: "temporary" } });
    fireEvent.change(screen.getByPlaceholderText("Studio code"), { target: { value: "" } });
    fireEvent.change(screen.getByLabelText("Custom"), { target: { value: '{"one":"edited"}' } });
    fireEvent.click(screen.getByRole("button", { name: "Prepopulate from video" }));
    fireEvent.click(screen.getByRole("button", { name: "Create scene and split" }));
    await waitFor(() => expect(created).toHaveBeenCalledWith(9));
    expect(api.splitFile).toHaveBeenCalledWith(
      1,
      2,
      undefined,
      expect.objectContaining({
        title: "New title",
        code: undefined,
        details: "Details",
        director: "Director",
        date: "2024-03",
        isVr: true,
        studioId: 3,
        urls: ["https://example.com"],
        tagIds: [4],
        performerIds: [5],
        galleryIds: [6],
        customFields: { one: "edited", two: "other" },
      }),
    );
  });
  it("preserves an explicitly unset custom field even when its normalized value was already absent", async () => {
    api.splitFile.mockResolvedValue({ videoId: 9 });
    mount();
    fireEvent.change(screen.getByLabelText("Custom"), { target: { value: "{} " } });
    fireEvent.click(screen.getByRole("button", { name: "Prepopulate from video" }));
    fireEvent.click(screen.getByRole("button", { name: "Create scene and split" }));
    await waitFor(() =>
      expect(api.splitFile).toHaveBeenCalledWith(
        1,
        2,
        undefined,
        expect.objectContaining({ customFields: { two: "other" } }),
      ),
    );
  });
  it("blocks dismissal while pending and retains the draft after failure", async () => {
    let reject!: (error: Error) => void;
    api.splitFile.mockImplementation(
      () =>
        new Promise((_, fail) => {
          reject = fail;
        }),
    );
    const { close, created } = mount();
    fireEvent.change(screen.getByPlaceholderText("Video title"), { target: { value: "Draft" } });
    fireEvent.click(screen.getByRole("button", { name: "Create scene and split" }));
    await screen.findByRole("button", { name: "Creating…" });
    fireEvent.keyDown(window, { key: "Escape" });
    expect(close).not.toHaveBeenCalled();
    reject(new Error("Split failed"));
    await screen.findByRole("alert");
    expect(screen.getByPlaceholderText("Video title")).toHaveValue("Draft");
    expect(created).not.toHaveBeenCalled();
  });
  it("keeps normal creation available", async () => {
    api.create.mockResolvedValue({ id: 10 });
    const { created } = mount(false);
    expect(screen.getByText("Source selection")).toBeInTheDocument();
    fireEvent.change(screen.getByPlaceholderText("Video title"), { target: { value: "Ordinary" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(created).toHaveBeenCalledWith(10));
    expect(api.splitFile).not.toHaveBeenCalled();
  });
});
