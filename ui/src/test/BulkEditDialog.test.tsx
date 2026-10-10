import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { CustomFieldDefinition } from "../api/types";
import {
  BulkEditDialog,
  GROUP_BULK_FIELDS,
  IMAGE_BULK_FIELDS,
  PERFORMER_BULK_FIELDS,
  VIDEO_BULK_FIELDS,
} from "../components/BulkEditDialog";

const mocks = vi.hoisted(() => ({
  tagsFind: vi.fn(),
  performersFind: vi.fn(),
  performerCountries: vi.fn(),
  studiosFind: vi.fn(),
  studiosGet: vi.fn(),
  groupsFind: vi.fn(),
  customFieldsList: vi.fn(),
}));

vi.mock("../api/client", () => ({
  tags: { find: mocks.tagsFind },
  performers: { find: mocks.performersFind, countries: mocks.performerCountries },
  studios: { find: mocks.studiosFind, get: mocks.studiosGet },
  groups: { find: mocks.groupsFind },
  customFields: { list: mocks.customFieldsList },
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

function renderDialog(dialog: React.ReactNode) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });

  return render(<QueryClientProvider client={queryClient}>{dialog}</QueryClientProvider>);
}

describe("BulkEditDialog", () => {
  beforeEach(() => {
    mocks.tagsFind.mockResolvedValue({ items: [{ id: 1, name: "Tag One" }] });
    mocks.performersFind.mockResolvedValue({ items: [] });
    mocks.performerCountries.mockResolvedValue([
      { value: "US", code: "US", name: "United States", performerCount: 42, isCustom: false },
    ]);
    mocks.studiosFind.mockResolvedValue({ items: [{ id: 11, name: "Alpha Studio" }] });
    mocks.studiosGet.mockResolvedValue({ id: 11, name: "Alpha Studio" });
    mocks.groupsFind.mockResolvedValue({ items: [{ id: 5, name: "Series One" }] });
    mocks.customFieldsList.mockResolvedValue([]);
  });

  it("posts corrected mode keys and video group payloads", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();

    renderDialog(
      <BulkEditDialog
        open
        onClose={vi.fn()}
        title="Edit Videos"
        selectedCount={2}
        fields={VIDEO_BULK_FIELDS}
        onApply={onApply}
      />,
    );

    await user.click(screen.getByRole("checkbox", { name: "Tags" }));
    await user.click(screen.getByRole("button", { name: "Overwrite" }));
    await user.type(screen.getByPlaceholderText("Search tags..."), "Tag");
    await waitFor(() => expect(screen.getByRole("option", { name: /Tag One/i })).toBeInTheDocument());
    expect(screen.getByRole("listbox")).toHaveClass("bg-surface");
    await user.click(screen.getByRole("option", { name: /Tag One/i }));

    await user.click(screen.getByRole("checkbox", { name: "Groups" }));
    const overwriteButtons = screen.getAllByRole("button", { name: "Overwrite" });
    await user.click(overwriteButtons[1]);
    await user.type(screen.getByPlaceholderText("Search groups..."), "Series");
    await waitFor(() => expect(screen.getByRole("option", { name: /Series One/i })).toBeInTheDocument());
    await user.click(screen.getByRole("option", { name: /Series One/i }));

    await user.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      tagIds: [1],
      tagMode: "SET",
      groupIds: [{ groupId: 5, videoIndex: 0 }],
      groupMode: "SET",
    });
  });

  it("loads studio search results and renders the shared rating widget", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();
    const { container } = renderDialog(
      <BulkEditDialog
        open
        onClose={vi.fn()}
        title="Edit Groups"
        selectedCount={1}
        fields={GROUP_BULK_FIELDS}
        onApply={onApply}
      />,
    );

    await user.click(screen.getByRole("checkbox", { name: "Rating" }));
    expect(container.querySelectorAll('button[title="Set rating"]').length).toBe(5);

    await user.click(screen.getByRole("checkbox", { name: "Studio" }));
    const searchInput = screen.getByPlaceholderText("Search studios...");
    await user.type(searchInput, "Alpha");
    await waitFor(() => expect(screen.getByRole("option", { name: /Alpha Studio/i })).toBeInTheDocument());
    await user.click(screen.getByRole("option", { name: /Alpha Studio/i }));

    await user.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ studioId: 11 });
  });

  it("keeps Apply disabled while a ticked rating has no value, but not for a nullable field", async () => {
    const user = userEvent.setup();

    renderDialog(
      <BulkEditDialog
        open
        onClose={vi.fn()}
        title="Edit Videos"
        selectedCount={2}
        fields={VIDEO_BULK_FIELDS}
        onApply={vi.fn()}
      />,
    );

    await user.click(screen.getByRole("checkbox", { name: "Rating" }));
    expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();
    await user.click(screen.getByRole("checkbox", { name: "Studio" }));
    expect(screen.getByRole("button", { name: "Apply" })).toBeEnabled();
  });

  it("posts clearFields when a nullable studio field is enabled without a selection", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();

    renderDialog(
      <BulkEditDialog
        open
        onClose={vi.fn()}
        title="Edit Videos"
        selectedCount={2}
        fields={VIDEO_BULK_FIELDS}
        onApply={onApply}
      />,
    );

    await user.click(screen.getByRole("checkbox", { name: "Studio" }));
    await user.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ studioId: null, clearFields: ["studioId"] });
  });

  it("sets and clears performer countries", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();

    const { rerender } = renderDialog(
      <BulkEditDialog
        open
        onClose={vi.fn()}
        title="Edit Performers"
        selectedCount={2}
        fields={PERFORMER_BULK_FIELDS}
        onApply={onApply}
      />,
    );
    await user.click(screen.getByRole("checkbox", { name: "Country" }));
    await user.click(screen.getByRole("button", { name: "Show countries" }));
    await user.click((await screen.findByText("United States")).closest("button")!);
    await user.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenLastCalledWith({ country: "US" });

    rerender(
      <QueryClientProvider client={new QueryClient()}>
        <BulkEditDialog
          key="clear-country"
          open
          onClose={vi.fn()}
          title="Edit Performers"
          selectedCount={2}
          fields={PERFORMER_BULK_FIELDS}
          onApply={onApply}
        />
      </QueryClientProvider>,
    );
    await user.click(screen.getByRole("checkbox", { name: "Country" }));
    await user.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenLastCalledWith({ country: null, clearFields: ["country"] });
  });

  it("keeps keyboard focus on a field's checkbox when it is ticked and unticked", async () => {
    const user = userEvent.setup();
    renderDialog(
      <BulkEditDialog
        open
        onClose={vi.fn()}
        title="Edit Videos"
        selectedCount={2}
        fields={VIDEO_BULK_FIELDS}
        onApply={vi.fn()}
      />,
    );

    const organized = screen.getByRole("checkbox", { name: "Organized" });
    organized.focus();
    await user.keyboard(" ");
    expect(screen.getByRole("checkbox", { name: "Organized" })).toBe(organized);
    expect(organized).toBeChecked();
    expect(organized).toHaveFocus();

    await user.keyboard(" ");
    expect(organized).not.toBeChecked();
    expect(organized).toHaveFocus();
  });

  it("closes an open country list on Escape without closing the dialog", async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    renderDialog(
      <BulkEditDialog
        open
        onClose={onClose}
        title="Edit Performers"
        selectedCount={2}
        fields={PERFORMER_BULK_FIELDS}
        onApply={vi.fn()}
      />,
    );

    await user.click(screen.getByRole("checkbox", { name: "Country" }));
    await user.click(screen.getByRole("button", { name: "Show countries" }));
    await screen.findByText("United States");
    await user.keyboard("{Escape}");
    expect(screen.queryByText("United States")).not.toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();

    await user.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  describe("custom fields", () => {
    const baseDefinition: CustomFieldDefinition = {
      key: "review_status",
      label: "Review status",
      type: "text",
      entityTypes: ["video"],
      options: [],
      filterable: false,
      sortable: false,
      isMultiValue: false,
    };
    const definitions: CustomFieldDefinition[] = [
      baseDefinition,
      { ...baseDefinition, key: "confirmed_tags", label: "Confirmed tags", type: "tag", isMultiValue: true },
      { ...baseDefinition, key: "stage", label: "Stage", type: "enum", options: ["draft", "final"] },
      { ...baseDefinition, key: "verified", label: "Verified", type: "boolean" },
      { ...baseDefinition, key: "structured", label: "Structured", type: "json" },
    ];

    function renderVideoDialog(onApply = vi.fn(), withCustomFields = true) {
      mocks.customFieldsList.mockResolvedValue(definitions);
      renderDialog(
        <BulkEditDialog
          open
          onClose={vi.fn()}
          title="Edit Videos"
          selectedCount={3}
          fields={VIDEO_BULK_FIELDS}
          customFieldEntityType={withCustomFields ? "video" : undefined}
          onApply={onApply}
        />,
      );
      return onApply;
    }

    async function customFieldsSection() {
      const section = within(await screen.findByRole("group", { name: "Custom fields" }));
      const toggle = section.getByRole("button", { name: /Custom fields/ });
      if (toggle.getAttribute("aria-expanded") !== "true") {
        await userEvent.click(toggle);
      }
      return section;
    }

    it("starts collapsed, expands on demand, and stays open while a field is ticked", async () => {
      const user = userEvent.setup();
      renderVideoDialog();
      const section = within(await screen.findByRole("group", { name: "Custom fields" }));
      const toggle = section.getByRole("button", { name: /Custom fields/ });

      expect(toggle).toHaveAttribute("aria-expanded", "false");
      expect(section.queryByRole("checkbox", { name: /Review status/ })).not.toBeInTheDocument();

      await user.click(toggle);
      expect(toggle).toHaveAttribute("aria-expanded", "true");
      await user.click(section.getByRole("checkbox", { name: /Review status/ }));
      expect(toggle).toHaveTextContent("1 of 5 selected");

      await user.click(toggle);
      expect(toggle).toHaveAttribute("aria-expanded", "false");
      expect(toggle).toHaveTextContent("1 of 5 selected");
      expect(section.queryByRole("checkbox", { name: /Review status/ })).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();

      await user.click(toggle);
      expect(section.getByRole("checkbox", { name: /Review status/ })).toBeChecked();
    });

    it("shows no section without an entity type and never loads definitions", () => {
      mocks.customFieldsList.mockClear();
      renderVideoDialog(vi.fn(), false);
      expect(screen.queryByRole("group", { name: "Custom fields" })).not.toBeInTheDocument();
      expect(mocks.customFieldsList).not.toHaveBeenCalled();
    });

    it("shows no section when the entity has no definitions", async () => {
      mocks.customFieldsList.mockResolvedValue([]);
      renderDialog(
        <BulkEditDialog
          open
          onClose={vi.fn()}
          title="Edit Videos"
          selectedCount={3}
          fields={VIDEO_BULK_FIELDS}
          customFieldEntityType="video"
          onApply={vi.fn()}
        />,
      );
      await waitFor(() => expect(mocks.customFieldsList).toHaveBeenCalledWith("video"));
      expect(screen.queryByRole("group", { name: "Custom fields" })).not.toBeInTheDocument();
    });

    it("sends a text value under Overwrite and leaves untouched definitions out", async () => {
      const user = userEvent.setup();
      const onApply = renderVideoDialog();
      const section = await customFieldsSection();

      await user.click(section.getByRole("checkbox", { name: /Review status/ }));
      await user.click(section.getByRole("radio", { name: "Overwrite" }));
      await user.type(section.getByRole("textbox", { name: "Review status" }), "done");
      await user.click(screen.getByRole("button", { name: "Apply" }));

      expect(onApply).toHaveBeenCalledWith({ customFields: { review_status: "done" }, customFieldMode: "SET" });
    });

    it("defaults to Add and sends multi-value tag ids alongside regular fields", async () => {
      const user = userEvent.setup();
      const onApply = renderVideoDialog();
      const section = await customFieldsSection();

      await user.click(screen.getByRole("checkbox", { name: "Organized" }));
      await user.click(screen.getByRole("button", { name: "True" }));

      await user.click(section.getByRole("checkbox", { name: /Confirmed tags/ }));
      await user.type(section.getByRole("combobox", { name: "Confirmed tags" }), "Tag");
      await waitFor(() => expect(screen.getByRole("option", { name: /Tag One/i })).toBeInTheDocument());
      await user.click(screen.getByRole("option", { name: /Tag One/i }));
      await user.click(screen.getByRole("button", { name: "Apply" }));

      expect(onApply).toHaveBeenCalledWith({
        organized: true,
        customFields: { confirmed_tags: [1] },
        customFieldMode: "ADD",
      });
    });

    it("sends Remove for the whole custom field group", async () => {
      const user = userEvent.setup();
      const onApply = renderVideoDialog();
      const section = await customFieldsSection();

      await user.click(section.getByRole("checkbox", { name: /Confirmed tags/ }));
      await user.click(section.getByRole("radio", { name: "Remove" }));
      await user.type(section.getByRole("combobox", { name: "Confirmed tags" }), "Tag");
      await waitFor(() => expect(screen.getByRole("option", { name: /Tag One/i })).toBeInTheDocument());
      await user.click(screen.getByRole("option", { name: /Tag One/i }));
      await user.click(screen.getByRole("button", { name: "Apply" }));

      expect(onApply).toHaveBeenCalledWith({ customFields: { confirmed_tags: [1] }, customFieldMode: "REMOVE" });
    });

    it("sends enum and boolean values as typed scalars", async () => {
      const user = userEvent.setup();
      const onApply = renderVideoDialog();
      const section = await customFieldsSection();

      await user.click(section.getByRole("checkbox", { name: /Stage/ }));
      await user.click(section.getByRole("checkbox", { name: /Verified/ }));
      await user.selectOptions(section.getByRole("combobox", { name: "Stage" }), "final");
      await user.selectOptions(section.getByRole("combobox", { name: "Verified" }), "true");
      await user.click(screen.getByRole("button", { name: "Apply" }));

      expect(onApply).toHaveBeenCalledWith({
        customFields: { stage: "final", verified: true },
        customFieldMode: "ADD",
      });
    });

    it("sends only a prefixed clearFields entry for a cleared field", async () => {
      const user = userEvent.setup();
      const onApply = renderVideoDialog();
      const section = await customFieldsSection();

      await user.click(section.getByRole("checkbox", { name: /Review status/ }));
      await user.type(section.getByRole("textbox", { name: "Review status" }), "stale");
      await user.click(section.getByRole("button", { name: "Clear value for Review status" }));
      expect(section.queryByRole("textbox", { name: "Review status" })).not.toBeInTheDocument();
      expect(section.queryByRole("radio", { name: "Remove" })).not.toBeInTheDocument();
      await user.click(screen.getByRole("button", { name: "Apply" }));

      expect(onApply).toHaveBeenCalledWith({ clearFields: ["customFields.review_status"] });
    });

    it("combines a cleared field with a value and a regular cleared field", async () => {
      const user = userEvent.setup();
      const onApply = renderVideoDialog();
      const section = await customFieldsSection();

      await user.click(screen.getByRole("checkbox", { name: "Studio" }));
      await user.click(section.getByRole("checkbox", { name: /Review status/ }));
      await user.click(section.getByRole("button", { name: "Clear value for Review status" }));
      await user.click(section.getByRole("checkbox", { name: /Stage/ }));
      await user.selectOptions(section.getByRole("combobox", { name: "Stage" }), "draft");
      await user.click(screen.getByRole("button", { name: "Apply" }));

      expect(onApply).toHaveBeenCalledWith({
        studioId: null,
        customFields: { stage: "draft" },
        customFieldMode: "ADD",
        clearFields: ["studioId", "customFields.review_status"],
      });
    });

    it("keeps Apply disabled while a ticked field is empty, since nothing would be sent", async () => {
      const user = userEvent.setup();
      const onApply = renderVideoDialog();
      const section = await customFieldsSection();

      await user.click(section.getByRole("checkbox", { name: /Review status/ }));
      expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();

      await user.type(section.getByRole("textbox", { name: "Review status" }), "x");
      expect(screen.getByRole("button", { name: "Apply" })).toBeEnabled();
      await user.clear(section.getByRole("textbox", { name: "Review status" }));
      expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();
      expect(onApply).not.toHaveBeenCalled();
    });

    it("offers the section for performers and never for entities without custom field support", async () => {
      mocks.customFieldsList.mockResolvedValue([{ ...baseDefinition, entityTypes: ["performer"] }]);
      const { unmount } = renderDialog(
        <BulkEditDialog
          open
          onClose={vi.fn()}
          title="Edit Performers"
          selectedCount={2}
          fields={PERFORMER_BULK_FIELDS}
          customFieldEntityType="performer"
          onApply={vi.fn()}
        />,
      );
      expect(await screen.findByRole("group", { name: "Custom fields" })).toBeInTheDocument();
      expect(mocks.customFieldsList).toHaveBeenCalledWith("performer");
      unmount();

      mocks.customFieldsList.mockClear();
      renderDialog(
        <BulkEditDialog
          open
          onClose={vi.fn()}
          title="Edit Images"
          selectedCount={2}
          fields={IMAGE_BULK_FIELDS}
          onApply={vi.fn()}
        />,
      );
      expect(screen.queryByRole("group", { name: "Custom fields" })).not.toBeInTheDocument();
      expect(mocks.customFieldsList).not.toHaveBeenCalled();
    });

    it("drops a field's value and cleared state when it is unticked again", async () => {
      const user = userEvent.setup();
      const onApply = renderVideoDialog();
      const section = await customFieldsSection();

      await user.click(section.getByRole("checkbox", { name: /Review status/ }));
      await user.click(section.getByRole("button", { name: "Clear value for Review status" }));
      await user.click(section.getByRole("checkbox", { name: /Review status/ }));
      await user.click(section.getByRole("checkbox", { name: /Review status/ }));
      expect(section.getByRole("textbox", { name: "Review status" })).toBeInTheDocument();
      await user.type(section.getByRole("textbox", { name: "Review status" }), "again");
      await user.click(screen.getByRole("button", { name: "Apply" }));

      expect(onApply).toHaveBeenCalledWith({ customFields: { review_status: "again" }, customFieldMode: "ADD" });
    });

    it("disables Apply while an enabled JSON field holds invalid JSON", async () => {
      const user = userEvent.setup();
      const onApply = renderVideoDialog();
      const section = await customFieldsSection();

      await user.click(section.getByRole("checkbox", { name: /Structured/ }));
      await user.click(section.getByRole("button", { name: "Add Structured JSON" }));
      const editor = screen.getByRole("textbox", { name: "Structured JSON" });
      await user.type(editor, "{{not json");
      expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();

      await user.clear(editor);
      await user.type(editor, '{{"a": 1}');
      await user.click(screen.getByRole("button", { name: "Apply JSON" }));
      expect(screen.getByRole("button", { name: "Apply" })).toBeEnabled();
      await user.click(screen.getByRole("button", { name: "Apply" }));

      expect(onApply).toHaveBeenCalledWith({ customFields: { structured: { a: 1 } }, customFieldMode: "ADD" });
    });
  });
});
