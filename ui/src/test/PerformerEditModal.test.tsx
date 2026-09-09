import { QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { PerformerEditModal } from "../pages/PerformerEditModal";
import type { Performer } from "../api/types";
import { MutationFailureNotice } from "../components/MutationFailureNotice";
import { createAppQueryClient } from "../queryClient";
import { resetMutationFailureForTests } from "../state/mutationFailure";

const mocks = vi.hoisted(() => ({
  performersUpdate: vi.fn(),
  performersCountries: vi.fn(),
  tagsCreate: vi.fn(),
  tagsFind: vi.fn(),
  performerImageUrl: vi.fn(),
  uploadPerformerImage: vi.fn(),
  deletePerformerImage: vi.fn(),
}));

vi.mock("../api/client", () => ({
  performers: { update: mocks.performersUpdate, countries: mocks.performersCountries },
  tags: { create: mocks.tagsCreate, find: mocks.tagsFind },
  entityImages: {
    performerImageUrl: mocks.performerImageUrl,
    uploadPerformerImage: mocks.uploadPerformerImage,
    deletePerformerImage: mocks.deletePerformerImage,
  },
}));

vi.mock("../components/ImageInput", () => ({
  ImageInput: ({ label }: { label: string }) => <div>{label}</div>,
}));

vi.mock("../components/shared", () => ({
  buildTagProvenanceById: () => new Map(),
  CustomFieldsEditor: () => <div>Custom Fields Editor</div>,
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({
    config: {
      ui: {
        ratingSystemOptions: {
          type: "stars",
          starPrecision: "full",
        },
      },
    },
  }),
  useOptionalAppConfig: () => ({
    config: {
      ui: {
        ratingSystemOptions: {
          type: "stars",
          starPrecision: "full",
        },
      },
    },
  }),
}));

function renderModal(performer: Performer) {
  const queryClient = createAppQueryClient();

  return render(
    <QueryClientProvider client={queryClient}>
      <MutationFailureNotice />
      <PerformerEditModal performer={performer} open onClose={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("PerformerEditModal", () => {
  beforeEach(() => {
    mocks.performersUpdate.mockResolvedValue({});
    mocks.performersCountries.mockResolvedValue([]);
    mocks.tagsFind.mockResolvedValue({ items: [] });
    mocks.performerImageUrl.mockReturnValue("/performers/1/image");
    mocks.uploadPerformerImage.mockResolvedValue(undefined);
    mocks.deletePerformerImage.mockResolvedValue(undefined);
  });

  afterEach(() => resetMutationFailureForTests());

  it.each(["TransgenderMale", "TransgenderFemale"])("preserves and submits the API gender %s", async (gender) => {
    const performer = {
      id: 1,
      name: "Sample Performer",
      gender,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
    } as unknown as Performer;
    const { container } = renderModal(performer);
    const select = [...container.querySelectorAll("select")].find((element) =>
      [...element.options].some((option) => option.value === "NonBinary"),
    )!;
    expect(select.value).toBe(gender);
    fireEvent.change(select, { target: { value: gender } });
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(mocks.performersUpdate).toHaveBeenCalledWith(1, expect.objectContaining({ gender })));
  });

  it("shows a rename conflict inline without exposing the API wrapper or global notice", async () => {
    const detail = 'A performer with name "Existing performer" and no disambiguation already exists.';
    mocks.performersUpdate.mockRejectedValueOnce(new Error(`API Error 409: ${JSON.stringify({ message: detail })}`));
    const performer: Performer = {
      id: 1,
      name: "Performer being edited",
      favorite: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };
    renderModal(performer);
    const user = userEvent.setup();

    await user.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(detail);
    expect(screen.queryByText("Couldn’t complete the action")).not.toBeInTheDocument();
    expect(screen.queryByText(/API Error 409/)).not.toBeInTheDocument();
  });

  it("uses the shared rating input and omits favorite from the payload", async () => {
    const user = userEvent.setup();
    const performer: Performer = {
      id: 1,
      name: "Sample Performer",
      favorite: true,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };

    const { container } = renderModal(performer);

    expect(screen.getByText("Rating")).toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: "Favorite" })).not.toBeInTheDocument();
    expect(container.querySelectorAll('button[title="Set rating"]').length).toBe(5);

    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.performersUpdate).toHaveBeenCalledWith(1, expect.any(Object)));
    expect(mocks.performersUpdate.mock.calls[0][1]).not.toHaveProperty("favorite");
  });

  it("explicitly clears blanked birthdate and height fields", async () => {
    const user = userEvent.setup();
    const performer: Performer = {
      id: 1,
      name: "Sample Performer",
      birthdate: "2000-01-01",
      heightCm: 170,
      favorite: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };

    const { container } = renderModal(performer);
    const birthdateInput = container.querySelector('input[value="2000-01-01"]');
    const heightInput = container.querySelector('input[type="number"][value="170"]');
    expect(birthdateInput).not.toBeNull();
    expect(heightInput).not.toBeNull();

    await user.clear(birthdateInput!);
    await user.clear(heightInput!);
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(mocks.performersUpdate).toHaveBeenCalledWith(
        1,
        expect.objectContaining({
          clearFields: [
            "disambiguation",
            "gender",
            "birthdate",
            "deathDate",
            "ethnicity",
            "country",
            "eyeColor",
            "hairColor",
            "heightCm",
            "weight",
            "measurements",
            "fakeTits",
            "penisLength",
            "circumcised",
            "careerStart",
            "careerEnd",
            "tattoos",
            "piercings",
            "details",
          ],
        }),
      ),
    );
  });

  it("searches tags remotely and adds selected tags to the payload", async () => {
    const user = userEvent.setup();
    const performer: Performer = {
      id: 1,
      name: "Sample Performer",
      favorite: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };

    mocks.tagsFind.mockImplementation(async ({ q }: { q?: string }) => ({
      items:
        q === "sha"
          ? [
              { id: 7, name: "Shaved Pussy" },
              { id: 8, name: "Shared Video" },
            ]
          : [],
    }));

    renderModal(performer);

    const input = screen.getByPlaceholderText("Search tags...");
    await user.type(input, "sha");

    await waitFor(() => {
      expect(mocks.tagsFind).toHaveBeenLastCalledWith({
        q: "sha",
        perPage: 20,
        sort: "name",
        direction: "asc",
      });
    });

    const firstOption = await screen.findByRole("option", { name: "Shaved Pussy" });
    await user.keyboard("{ArrowDown}");
    expect(input).toHaveAttribute("aria-activedescendant", firstOption.id);
    expect(firstOption).toHaveClass("bg-accent", "text-white");
    await user.keyboard("{Enter}");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(mocks.performersUpdate).toHaveBeenCalledWith(1, expect.objectContaining({ tagIds: [7] })),
    );
    expect(screen.getByText("Shaved Pussy")).toBeInTheDocument();
  });

  it("creates and selects a metadata-rich tag with the shared keyboard interaction", async () => {
    const user = userEvent.setup();
    const performer: Performer = {
      id: 1,
      name: "Sample Performer",
      favorite: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };
    mocks.tagsFind.mockResolvedValue({ items: [] });
    mocks.tagsCreate.mockResolvedValue({
      id: 9,
      name: "Novel tag",
      color: "#123456",
      tagGroupName: "Qualities",
      tagGroupColor: "#654321",
    });

    renderModal(performer);

    const input = screen.getByPlaceholderText("Search tags...");
    await user.type(input, "Novel tag");
    const createOption = await screen.findByRole("option", { name: "Create “Novel tag”" });
    expect(input).toHaveAttribute("aria-controls", createOption.parentElement?.id);
    await user.keyboard("{ArrowDown}");
    expect(createOption).toHaveClass("bg-accent", "text-white");
    await user.keyboard("{Enter}");

    await waitFor(() => expect(mocks.tagsCreate).toHaveBeenCalledWith({ name: "Novel tag" }));
    expect(await screen.findByText("Qualities")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(mocks.performersUpdate).toHaveBeenCalledWith(1, expect.objectContaining({ tagIds: [9] })),
    );
  });

  it("keeps the highlighted create option mounted while tag results refresh", async () => {
    const user = userEvent.setup();
    const performer: Performer = {
      id: 1,
      name: "Sample Performer",
      favorite: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };
    let resolveNextSearch!: (value: { items: Array<{ id: number; name: string }> }) => void;
    mocks.tagsFind.mockReset();
    mocks.tagsFind.mockResolvedValueOnce({ items: [] }).mockReturnValueOnce(
      new Promise((resolve) => {
        resolveNextSearch = resolve;
      }),
    );

    renderModal(performer);

    const input = screen.getByPlaceholderText("Search tags...");
    fireEvent.change(input, { target: { value: "Novel" } });
    const createOption = await screen.findByRole("option", { name: "Create “Novel”" });
    input.focus();
    await user.keyboard("{ArrowDown}");
    expect(input).toHaveAttribute("aria-activedescendant", createOption.id);

    fireEvent.change(input, { target: { value: "Novel tag" } });
    await waitFor(() => expect(mocks.tagsFind).toHaveBeenCalledTimes(2));
    const updatedCreateOption = screen.getByRole("option", { name: "Create “Novel tag”" });
    expect(updatedCreateOption).toBe(createOption);
    expect(input).toHaveAttribute("aria-activedescendant", updatedCreateOption.id);
    expect(screen.getByRole("listbox")).toHaveAttribute("aria-busy", "true");

    resolveNextSearch({ items: [] });
    await waitFor(() => expect(screen.getByRole("listbox")).not.toHaveAttribute("aria-busy"));
  });

  it("saves aliases from separate list inputs", async () => {
    const user = userEvent.setup();
    const performer: Performer = {
      id: 1,
      name: "Sample Performer",
      favorite: false,
      urls: [],
      aliases: ["Old Alias", "Second Alias"],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };

    renderModal(performer);

    const aliasInputs = screen.getAllByPlaceholderText("Alias");
    expect(aliasInputs).toHaveLength(2);
    expect(aliasInputs[0]).toHaveValue("Old Alias");
    expect(aliasInputs[1]).toHaveValue("Second Alias");

    await user.clear(aliasInputs[0]);
    await user.type(aliasInputs[0], "New Alias");
    await user.click(screen.getByRole("button", { name: /Add Alias/i }));
    await user.type(screen.getAllByPlaceholderText("Alias")[2], "Third Alias");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(mocks.performersUpdate).toHaveBeenCalledWith(
        1,
        expect.objectContaining({ aliases: ["New Alias", "Second Alias", "Third Alias"] }),
      ),
    );
  });
});
