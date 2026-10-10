import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { TagGroup, TagGroupUpdate } from "../api/types";
import { TagGroupsManager } from "../components/TagGroupsManager";

const mocks = vi.hoisted(() => ({
  list: vi.fn(),
  create: vi.fn(),
  update: vi.fn(),
  permissions: new Set<string>(),
}));

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    tagGroups: { ...actual.tagGroups, list: mocks.list, create: mocks.create, update: mocks.update },
  };
});

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ hasPermission: (permission: string) => mocks.permissions.has(permission) }),
}));

const makeGroup = (id: number, name: string, color: string | null): TagGroup => ({
  id,
  name,
  color,
  description: null,
  sortOrder: id * 10,
  tagCount: 1,
  createdAt: "2026-01-01T00:00:00Z",
  updatedAt: "2026-01-01T00:00:00Z",
});

const INITIAL_GROUPS = [
  makeGroup(1, "Shared green A", "#6ee7b7"),
  makeGroup(2, "Shared green B", "#6ee7b7"),
  makeGroup(3, "Custom red", "#a43737"),
  makeGroup(4, "Unpainted", null),
];

// A small in-memory server: updates change what the next list returns, like the real API, where an
// empty color clears it and an omitted one leaves it unchanged.
let store: TagGroup[];
const colorOf = (id: number) => store.find((group) => group.id === id)?.color ?? null;

function renderManager() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <TagGroupsManager />
    </QueryClientProvider>,
  );
}

// Real requests take a moment, so React renders while they are in flight; instant mocks skip that.
function withNetworkDelay() {
  const delay = () => new Promise((resolve) => setTimeout(resolve, 5));
  const list = mocks.list.getMockImplementation()!;
  const update = mocks.update.getMockImplementation()!;
  mocks.list.mockImplementation(async () => {
    await delay();
    return list();
  });
  mocks.update.mockImplementation(async (id: number, body: TagGroupUpdate) => {
    await delay();
    return update(id, body);
  });
}

async function openSuggestions(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole("button", { name: "Suggest distinct colors" }));
}

const hexColor = expect.stringMatching(/^#[0-9a-f]{6}$/);

beforeEach(() => {
  store = INITIAL_GROUPS.map((group) => ({ ...group }));
  mocks.permissions = new Set(["taggroups.write", "taggroups.delete"]);
  mocks.list.mockReset().mockImplementation(async () => store.map((group) => ({ ...group })));
  mocks.create.mockReset().mockResolvedValue(INITIAL_GROUPS[0]);
  mocks.update.mockReset().mockImplementation(async (id: number, body: TagGroupUpdate) => {
    const group = store.find((item) => item.id === id)!;
    if (body.color != null) group.color = body.color.trim() || null;
    return { ...group };
  });
});

describe("TagGroupsManager color suggestions", () => {
  it("preselects default, shared, and missing colors and saves only the checked groups", async () => {
    const user = userEvent.setup();
    renderManager();

    await openSuggestions(user);

    expect(screen.getByRole("checkbox", { name: "Shared green A" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Shared green B" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Unpainted" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Custom red" })).not.toBeChecked();
    expect(within(screen.getByTestId("tag-group-color-preview-3")).getByText("Unchanged")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Suggest distinct colors" })).toHaveFocus();
    expect(mocks.update).not.toHaveBeenCalled();

    // Untick one so it keeps its color.
    await user.click(screen.getByRole("checkbox", { name: "Shared green B" }));
    await user.click(screen.getByRole("button", { name: "Apply to 2 groups" }));

    expect(await screen.findByText("Updated the colors of 2 tag groups.")).toBeInTheDocument();
    expect(mocks.update).toHaveBeenCalledTimes(2);
    expect(mocks.update).toHaveBeenCalledWith(1, { color: hexColor });
    expect(mocks.update).toHaveBeenCalledWith(4, { color: hexColor });
    expect(colorOf(1)).not.toBe(colorOf(4));
    expect(colorOf(1)).not.toBe("#6ee7b7");
    // The preview closes and focus goes back to the button that opened it.
    expect(screen.queryByRole("checkbox", { name: "Shared green A" })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Suggest distinct colors" })).toHaveFocus());
  });

  it("undo restores the previous colors, clearing groups that had none", async () => {
    const user = userEvent.setup();
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));
    await screen.findByText("Updated the colors of 3 tag groups.");
    mocks.update.mockClear();

    await user.click(screen.getByRole("button", { name: "Undo" }));

    expect(await screen.findByText("Restored the previous colors of 3 tag groups.")).toBeInTheDocument();
    expect(mocks.update).toHaveBeenCalledWith(1, { color: "#6ee7b7" });
    expect(mocks.update).toHaveBeenCalledWith(2, { color: "#6ee7b7" });
    expect(mocks.update).toHaveBeenCalledWith(4, { color: "" });
    expect(store.map((group) => group.color)).toEqual(INITIAL_GROUPS.map((group) => group.color));
    expect(screen.queryByRole("button", { name: "Undo" })).not.toBeInTheDocument();
  });

  it("undo leaves alone a group whose color changed after applying", async () => {
    const user = userEvent.setup();
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));
    await screen.findByText("Updated the colors of 3 tag groups.");

    // Someone recolors Shared green B by hand before Undo is clicked.
    await user.click(screen.getAllByRole("button", { name: "Edit" })[1]);
    const hex = screen.getByRole("textbox", { name: "Color hex value" });
    await user.clear(hex);
    await user.type(hex, "#123456");
    await user.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(colorOf(2)).toBe("#123456"));

    await user.click(screen.getByRole("button", { name: "Undo" }));

    expect(await screen.findByText("Restored the previous colors of 2 tag groups.")).toBeInTheDocument();
    expect(
      screen.getByText("Kept the current colors of 1 tag group changed since: Shared green B."),
    ).toBeInTheDocument();
    expect(colorOf(2)).toBe("#123456");
    expect(colorOf(1)).toBe("#6ee7b7");
    expect(colorOf(4)).toBeNull();
  });

  it("names failed groups with the reason, retries them, and undo covers every saved group", async () => {
    const user = userEvent.setup();
    const update = mocks.update.getMockImplementation()!;
    mocks.update.mockImplementation(async (id: number, body: TagGroupUpdate) => {
      if (id === 2) throw new Error("API Error 500: Internal Server Error");
      return update(id, body);
    });
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));

    expect(await screen.findByText("Updated the colors of 2 tag groups.")).toBeInTheDocument();
    expect(
      screen.getByText(
        /Couldn't update 1 tag group: Shared green B\. The server returned an error\. Please try again\./,
      ),
    ).toBeInTheDocument();
    expect(colorOf(2)).toBe("#6ee7b7");

    mocks.update.mockImplementation(update);
    await user.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByText("Updated the colors of 3 tag groups.")).toBeInTheDocument();
    expect(screen.queryByText(/Couldn't update/)).not.toBeInTheDocument();
    expect(colorOf(2)).not.toBe("#6ee7b7");

    await user.click(screen.getByRole("button", { name: "Undo" }));

    expect(await screen.findByText("Restored the previous colors of 3 tag groups.")).toBeInTheDocument();
    expect(store.map((group) => group.color)).toEqual(INITIAL_GROUPS.map((group) => group.color));
  });

  it("undo after a partial failure restores only the groups that were saved", async () => {
    const user = userEvent.setup();
    const update = mocks.update.getMockImplementation()!;
    mocks.update.mockImplementation(async (id: number, body: TagGroupUpdate) => {
      if (id === 2) throw new Error("API Error 500: Internal Server Error");
      return update(id, body);
    });
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));
    await screen.findByText("Updated the colors of 2 tag groups.");
    mocks.update.mockClear();

    await user.click(screen.getByRole("button", { name: "Undo" }));

    expect(await screen.findByText("Restored the previous colors of 2 tag groups.")).toBeInTheDocument();
    expect(mocks.update.mock.calls.map(([id]) => id).sort()).toEqual([1, 4]);
  });

  it("undo leaves alone a group recolored elsewhere, such as in another tab", async () => {
    const user = userEvent.setup();
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));
    await screen.findByText("Updated the colors of 3 tag groups.");
    // The list this page holds still shows the applied color; only the server knows about the change.
    store.find((group) => group.id === 4)!.color = "#654321";

    await user.click(screen.getByRole("button", { name: "Undo" }));

    expect(await screen.findByText("Restored the previous colors of 2 tag groups.")).toBeInTheDocument();
    expect(screen.getByText("Kept the current colors of 1 tag group changed since: Unpainted.")).toBeInTheDocument();
    expect(colorOf(4)).toBe("#654321");
  });

  it("retrying a failed undo checks again for groups changed since", async () => {
    const user = userEvent.setup();
    const update = mocks.update.getMockImplementation()!;
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));
    await screen.findByText("Updated the colors of 3 tag groups.");

    mocks.update.mockImplementation(async (id: number, body: TagGroupUpdate) => {
      if (id === 2) throw new Error("API Error 500: Internal Server Error");
      return update(id, body);
    });
    await user.click(screen.getByRole("button", { name: "Undo" }));
    expect(await screen.findByText(/Couldn't restore 1 tag group: Shared green B./)).toBeInTheDocument();
    expect(screen.getByText("Restored the previous colors of 2 tag groups.")).toBeInTheDocument();

    // Shared green B is recolored by hand before the retry.
    mocks.update.mockImplementation(update);
    store.find((group) => group.id === 2)!.color = "#123456";
    await user.click(screen.getByRole("button", { name: "Retry" }));

    expect(
      await screen.findByText("Kept the current colors of 1 tag group changed since: Shared green B."),
    ).toBeInTheDocument();
    expect(screen.getByText("Restored the previous colors of 2 tag groups.")).toBeInTheDocument();
    expect(screen.queryByText(/Couldn't restore/)).not.toBeInTheDocument();
    expect(colorOf(2)).toBe("#123456");
  });

  it("shows the server's reason when it rejects a color", async () => {
    const user = userEvent.setup();
    mocks.update.mockRejectedValue(new Error('API Error 400: {"message":"Color must be #RRGGBB or #RRGGBBAA."}'));
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));

    expect(
      await screen.findByText(/Couldn't update 3 tag groups: .*. Color must be #RRGGBB or #RRGGBBAA./),
    ).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Undo" })).not.toBeInTheDocument();
  });

  it("locks undo while a group is open for editing, and saving a rename keeps the applied color", async () => {
    const user = userEvent.setup();
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));
    await screen.findByText("Updated the colors of 3 tag groups.");
    const applied = colorOf(1);

    await user.click(screen.getAllByRole("button", { name: "Edit" })[0]);
    expect(screen.getByRole("button", { name: "Undo" })).toBeDisabled();

    const name = screen.getAllByRole("textbox")[0];
    await user.clear(name);
    await user.type(name, "Renamed group");
    mocks.update.mockClear();
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.update).toHaveBeenCalledTimes(1));
    expect(mocks.update.mock.calls[0][1]).not.toHaveProperty("color");
    expect(colorOf(1)).toBe(applied);
    await waitFor(() => expect(screen.getByRole("button", { name: "Undo" })).toBeEnabled());
  });

  it("retrying a partly failed apply leaves alone a group recolored meanwhile", async () => {
    const user = userEvent.setup();
    const update = mocks.update.getMockImplementation()!;
    mocks.update.mockImplementation(async (id: number, body: TagGroupUpdate) => {
      if (id === 2) throw new Error("API Error 500: Internal Server Error");
      return update(id, body);
    });
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));
    await screen.findByText(/Couldn't update 1 tag group: Shared green B./);

    // Shared green B gets a color by hand before the retry.
    mocks.update.mockImplementation(update);
    store.find((group) => group.id === 2)!.color = "#123456";
    await user.click(screen.getByRole("button", { name: "Retry" }));

    expect(
      await screen.findByText("Kept the current colors of 1 tag group changed since: Shared green B."),
    ).toBeInTheDocument();
    expect(colorOf(2)).toBe("#123456");

    // Undo then restores only what the apply actually changed.
    await user.click(screen.getByRole("button", { name: "Undo" }));
    expect(await screen.findByText("Restored the previous colors of 2 tag groups.")).toBeInTheDocument();
    expect(colorOf(2)).toBe("#123456");
  });

  it("undo treats a color changed only in case or alpha as changed", async () => {
    const user = userEvent.setup();
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));
    await screen.findByText("Updated the colors of 3 tag groups.");
    const group = store.find((item) => item.id === 1)!;
    group.color = `${group.color}80`;

    await user.click(screen.getByRole("button", { name: "Undo" }));

    expect(
      await screen.findByText(/Kept the current colors of 1 tag group changed since: Shared green A./),
    ).toBeInTheDocument();
    expect(group.color).toMatch(/^#[0-9a-f]{6}80$/);
  });

  it("returns focus to the trigger after Undo once the save and refetch finish", async () => {
    const user = userEvent.setup();
    withNetworkDelay();
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));
    await screen.findByText("Updated the colors of 3 tag groups.");
    await user.click(screen.getByRole("button", { name: "Undo" }));

    await screen.findByText("Restored the previous colors of 3 tag groups.");
    await waitFor(() => expect(screen.getByRole("button", { name: "Suggest distinct colors" })).toHaveFocus());
  });

  it("doesn't take focus from a field the user moved to while colors were saving", async () => {
    const user = userEvent.setup();
    withNetworkDelay();
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Apply to 3 groups" }));
    const name = screen.getAllByRole("textbox")[0];
    await user.click(name);
    await user.type(name, "New");

    await screen.findByText("Updated the colors of 3 tag groups.");
    await waitFor(() => expect(screen.getByRole("button", { name: "Suggest distinct colors" })).toBeEnabled());
    expect(name).toHaveFocus();
  });

  it("cancel closes the preview without saving and returns focus", async () => {
    const user = userEvent.setup();
    renderManager();

    await openSuggestions(user);
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("checkbox", { name: "Shared green A" })).not.toBeInTheDocument();
    expect(mocks.update).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.getByRole("button", { name: "Suggest distinct colors" })).toHaveFocus());
  });

  it("is not offered to users who can't save tag groups", async () => {
    mocks.permissions = new Set(["tags.write"]);
    renderManager();

    await screen.findByText("Shared green A");
    expect(screen.queryByRole("button", { name: "Suggest distinct colors" })).not.toBeInTheDocument();
  });

  it("is not offered while a group is being edited, and editing is locked while it is open", async () => {
    const user = userEvent.setup();
    renderManager();

    await user.click((await screen.findAllByRole("button", { name: "Edit" }))[0]);
    expect(screen.queryByRole("button", { name: "Suggest distinct colors" })).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    await openSuggestions(user);
    for (const button of screen.getAllByRole("button", { name: "Edit" })) expect(button).toBeDisabled();
  });
});

describe("TagGroupsManager form colors", () => {
  it("gives a new group a suggested color instead of the old default green", async () => {
    const user = userEvent.setup();
    renderManager();
    await screen.findByText("Shared green A");

    await user.type(screen.getAllByRole("textbox")[0], "New group");
    await user.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(mocks.create).toHaveBeenCalledTimes(1));
    const body = mocks.create.mock.calls[0][0];
    expect(body.name).toBe("New group");
    expect(body.color).toMatch(/^#[0-9a-f]{6}$/);
    expect(body.color).not.toBe("#6ee7b7");
    expect(screen.getByRole("textbox", { name: "Color hex value" })).toHaveValue("");
  });

  it("keeps an explicitly typed color for a new group", async () => {
    const user = userEvent.setup();
    renderManager();
    await screen.findByText("Shared green A");

    await user.type(screen.getAllByRole("textbox")[0], "New group");
    await user.type(screen.getByRole("textbox", { name: "Color hex value" }), "#123456");
    await user.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(mocks.create).toHaveBeenCalledWith(expect.objectContaining({ color: "#123456" })));
  });

  it("doesn't send an edited group's color when the color field is left alone", async () => {
    const user = userEvent.setup();
    renderManager();

    await user.click((await screen.findAllByRole("button", { name: "Edit" }))[2]);
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.update).toHaveBeenCalledTimes(1));
    expect(mocks.update.mock.calls[0][1]).not.toHaveProperty("color");
    expect(colorOf(3)).toBe("#a43737");
  });

  it("sends an edited group's color when the user changes it", async () => {
    const user = userEvent.setup();
    renderManager();

    await user.click((await screen.findAllByRole("button", { name: "Edit" }))[2]);
    const hex = screen.getByRole("textbox", { name: "Color hex value" });
    await user.clear(hex);
    await user.type(hex, "#123456");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.update).toHaveBeenCalledWith(3, expect.objectContaining({ color: "#123456" })));
  });

  it("clears an edited group's color when the color field is emptied", async () => {
    const user = userEvent.setup();
    renderManager();

    await user.click((await screen.findAllByRole("button", { name: "Edit" }))[2]);
    await user.clear(screen.getByRole("textbox", { name: "Color hex value" }));
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.update).toHaveBeenCalledWith(3, expect.objectContaining({ color: "" })));
  });

  it("does not save a default color into an edited group that has none", async () => {
    const user = userEvent.setup();
    renderManager();

    await user.click((await screen.findAllByRole("button", { name: "Edit" }))[3]);
    expect(screen.getByRole("textbox", { name: "Color hex value" })).toHaveValue("");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.update).toHaveBeenCalledTimes(1));
    expect(mocks.update.mock.calls[0][1]).not.toHaveProperty("color");
    expect(colorOf(4)).toBeNull();
  });
});
