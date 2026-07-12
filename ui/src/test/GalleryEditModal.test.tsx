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
  StudioSelector: () => <div>Studio Selector</div>,
}));

vi.mock("../components/EntityReferenceSelector", () => ({
  EntityReferenceMultiSelector: ({ entityType }: { entityType: string }) => <div>{entityType} selector</div>,
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
}

describe("GalleryEditModal", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("omits rating, organized, and videos while using an ISO date field", async () => {
    mockGalleries.update.mockResolvedValue({});

    renderModal();

    expect(screen.queryByText("Rating")).not.toBeInTheDocument();
    expect(screen.queryByText("Videos")).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    expect(screen.queryByText("video selector")).not.toBeInTheDocument();

    const dateInput = screen.getByDisplayValue("2026-05-01");
    expect(dateInput).toHaveAttribute("type", "text");
    expect(dateInput).toHaveAttribute("placeholder", "yyyy-MM-dd");

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mockGalleries.update).toHaveBeenCalledTimes(1));

    const [galleryId, payload] = mockGalleries.update.mock.calls[0];
    expect(galleryId).toBe(21);
    expect(payload).not.toHaveProperty("rating");
    expect(payload).not.toHaveProperty("organized");
    expect(payload).not.toHaveProperty("videoIds");
    expect(payload).toHaveProperty("date", "2026-05-01");
  });
});
