import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { GalleryEditModal } from "../pages/GalleryEditModal";

const { mockGalleries } = vi.hoisted(() => ({
  mockGalleries: {
    update: vi.fn(),
  },
}));

vi.mock("../api/client", () => ({
  galleries: mockGalleries,
}));

vi.mock("../components/StudioSelector", () => ({
  StudioSelector: ({ onChange }: { onChange: (value: number | undefined) => void }) => (
    <div>
      Studio Selector
      <button onClick={() => onChange(undefined)}>Clear studio</button>
    </div>
  ),
}));

vi.mock("../components/EntityReferenceSelector", () => ({
  EntityReferenceMultiSelector: ({
    entityType,
    values,
    onChange,
  }: {
    entityType: string;
    values: number[];
    onChange: (values: number[]) => void;
  }) => (
    <div>
      {entityType} selector: {values.join(",")}
      {entityType === "video" ? (
        <>
          <button onClick={() => onChange([...values, 22])}>Add video 22</button>
          <button onClick={() => onChange(values.filter((id) => id !== 14))}>Remove video 14</button>
        </>
      ) : null}
    </div>
  ),
}));

vi.mock("../components/shared", () => ({
  buildTagProvenanceById: () => new Map(),
  CustomFieldsEditor: () => <div>Custom Fields Editor</div>,
}));

vi.mock("../components/StringListEditor", () => ({
  StringListEditor: () => <div>String List Editor</div>,
}));

function renderModal() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  render(
    <QueryClientProvider client={queryClient}>
      <GalleryEditModal
        open
        onClose={vi.fn()}
        gallery={{
          id: 21,
          title: "Summer Set",
          code: "SUM-21",
          date: "2026-05-01",
          details: "A bright summer gallery.",
          photographer: "Riley Smith",
          organized: true,
          studioId: 9,
          urls: ["https://example.com/gallery/21"],
          tags: [{ id: 8, name: "Beach" }],
          performers: [{ id: 5, name: "Alex" }],
          videoIds: [14],
          customFields: {},
        } as any}
      />
    </QueryClientProvider>,
  );

  return queryClient;
}

describe("GalleryEditModal", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("omits rating and organized while using an ISO date field", async () => {
    mockGalleries.update.mockResolvedValue({});

    renderModal();

    expect(screen.queryByText("Rating")).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();

    const dateInput = screen.getByDisplayValue("2026-05-01");
    expect(dateInput).toHaveAttribute("type", "text");
    expect(dateInput).toHaveAttribute("placeholder", "yyyy-MM-dd");

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mockGalleries.update).toHaveBeenCalledTimes(1));

    const [galleryId, payload] = mockGalleries.update.mock.calls[0];
    expect(galleryId).toBe(21);
    expect(payload).not.toHaveProperty("rating");
    expect(payload).not.toHaveProperty("organized");
    expect(payload).toHaveProperty("date", "2026-05-01");
  });

  it("adds video relationships and refreshes the gallery videos", async () => {
    mockGalleries.update.mockResolvedValue({});

    const queryClient = renderModal();
    const invalidateQueries = vi.spyOn(queryClient, "invalidateQueries");

    expect(screen.getByText("Videos")).toBeInTheDocument();
    expect(screen.getByText("video selector: 14")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Add video 22" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mockGalleries.update).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ["gallery-videos", 21] }));

    expect(mockGalleries.update.mock.calls[0][1]).toHaveProperty("videoIds", [14, 22]);
  });

  it("removes existing video relationships", async () => {
    mockGalleries.update.mockResolvedValue({});

    renderModal();

    fireEvent.click(screen.getByRole("button", { name: "Remove video 14" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mockGalleries.update).toHaveBeenCalledTimes(1));

    expect(mockGalleries.update.mock.calls[0][1]).toHaveProperty("videoIds", []);
  });

  it("marks a cleared date and studio for removal", async () => {
    mockGalleries.update.mockResolvedValue({});

    renderModal();

    fireEvent.change(screen.getByDisplayValue("2026-05-01"), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "Clear studio" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mockGalleries.update).toHaveBeenCalledTimes(1));
    expect(mockGalleries.update.mock.calls[0][1]).toEqual(expect.objectContaining({
      clearFields: ["date", "studioId"],
    }));
  });
});
