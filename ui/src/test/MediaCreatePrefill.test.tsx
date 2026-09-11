import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { images } from "../api/client";
import type { Image } from "../api/types";
import { AudioCreateModal } from "../pages/AudiosPage";
import { ImageCreateModal } from "../pages/ImageEditModal";
import { TextCreateModal } from "../pages/TextsPage";

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    audios: { ...actual.audios, create: vi.fn() },
    images: { ...actual.images, create: vi.fn(), get: vi.fn() },
    texts: { ...actual.texts, create: vi.fn() },
  };
});

vi.mock("../components/FileBackedCreateSource", () => ({
  FileBackedCreateSource: () => null,
}));

vi.mock("../hooks/useFileBackedCreatePreferences", () => ({
  useFileBackedCreatePreferences: () => ({
    urlDownloadMode: "now",
    setUrlDownloadMode: vi.fn(),
    scrapeMetadata: false,
    setScrapeMetadata: vi.fn(),
  }),
}));

vi.mock("../components/StudioSelector", () => ({
  StudioSelector: () => null,
}));

vi.mock("../components/EntityReferenceSelector", () => ({
  EntityReferenceMultiSelector: () => null,
  EntityReferenceValue: () => null,
}));

vi.mock("../components/PerformerContextTags", () => ({
  PerformerContextTagEditor: () => null,
  buildPerformerContextTagIds: () => ({}),
  syncPerformerContextTags: vi.fn(),
}));

vi.mock("../components/shared", () => ({
  buildTagProvenanceById: () => new Map(),
  CustomFieldsEditor: () => null,
  formatDuration: () => "",
}));

function renderCreateModal(kind: "audio" | "image" | "text") {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const props = {
    open: true,
    initialTitle: "  New item  ",
    onClose: vi.fn(),
    onCreated: vi.fn(),
  };
  render(
    <QueryClientProvider client={queryClient}>
      {kind === "audio" ? (
        <AudioCreateModal {...props} />
      ) : kind === "image" ? (
        <ImageCreateModal {...props} />
      ) : (
        <TextCreateModal {...props} />
      )}
    </QueryClientProvider>,
  );
}

describe("media create search prefills", () => {
  it.each([
    ["audio", "Audio title"],
    ["image", "Image title"],
    ["text", "Text title"],
  ] as const)("prefills the trimmed %s title", (kind, placeholder) => {
    renderCreateModal(kind);

    expect(screen.getByPlaceholderText(placeholder)).toHaveValue("New item");
  });

  it("clears the search-derived image title when creating another", async () => {
    const user = userEvent.setup();
    const created = { id: 1 } as Image;
    vi.mocked(images.create).mockResolvedValue(created);
    vi.mocked(images.get).mockResolvedValue(created);
    renderCreateModal("image");

    await user.click(screen.getByRole("checkbox", { name: /create another after save/i }));
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.getByPlaceholderText("Image title")).toHaveValue(""));
  });
});
