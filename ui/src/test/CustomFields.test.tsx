import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { CustomFieldDefinition } from "../api/types";
import { CustomFieldsDisplay, CustomFieldsEditor } from "../components/CustomFields";
import { customFieldDefinitionsQueryKey } from "../hooks/useCustomFieldDefinitions";

const jsonDefinition: CustomFieldDefinition = {
  id: 1,
  key: "structured_metadata",
  label: "Structured Metadata",
  type: "json",
  entityTypes: ["video"],
  options: [],
  filterable: false,
  sortable: false,
  isMultiValue: false,
  displayOrder: 0,
};

const longTextDefinition: CustomFieldDefinition = {
  ...jsonDefinition,
  key: "notes",
  label: "Notes",
  type: "longText",
  entityTypes: ["performer"],
};

function renderWithDefinition(
  ui: React.ReactNode,
  definitions: CustomFieldDefinition[] = [jsonDefinition],
  entityType: "video" | "performer" = "video",
) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: Infinity } },
  });
  queryClient.setQueryData(customFieldDefinitionsQueryKey(entityType), definitions);
  return render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
}

function SaveHarness({ onSave }: { onSave: () => void }) {
  const [value, setValue] = useState<Record<string, unknown>>({});
  const [isValid, setIsValid] = useState(true);

  return (
    <>
      <CustomFieldsEditor value={value} onChange={setValue} onValidityChange={setIsValid} entityType="video" />
      <button type="button" disabled={!isValid} onClick={onSave}>
        Save entity
      </button>
    </>
  );
}

describe("JSON custom fields", () => {
  it("opens JSON editing on demand and commits structured JSON only when applied", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithDefinition(
      <CustomFieldsEditor
        value={{ structured_metadata: { profile: { score: 0.95 }, labels: ["one", "two"] } }}
        onChange={onChange}
        entityType="video"
      />,
    );

    expect(screen.queryByRole("textbox", { name: "Structured Metadata JSON" })).not.toBeInTheDocument();
    expect(screen.getByText("JSON object · 2 properties")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Edit Structured Metadata JSON" }));

    const dialog = screen.getByRole("dialog", { name: "Structured Metadata" });
    const editor = screen.getByRole("textbox", { name: "Structured Metadata JSON" });
    expect(dialog).toHaveClass("h-[90vh]");
    expect(editor).toHaveValue(
      `{\n  "profile": {\n    "score": 0.95\n  },\n  "labels": [\n    "one",\n    "two"\n  ]\n}`,
    );
    const editorHighlight = document.querySelector("[data-json-editor-highlight]");
    expect(editorHighlight).toHaveClass("whitespace-pre", "overflow-auto");
    expect(editor).toHaveClass("whitespace-pre", "overflow-auto");
    expect(editorHighlight?.querySelector('[data-json-token="key"]')).toHaveTextContent('"profile"');
    expect(document.querySelector('[data-json-editor-highlight] [data-json-token="number"]')).toHaveTextContent("0.95");
    expect(screen.queryByRole("button", { name: /Collapse|Expand/ })).not.toBeInTheDocument();

    fireEvent.change(editor, { target: { value: '{"profile":' } });
    expect(editor).toHaveValue('{"profile":');
    expect(screen.getByRole("alert")).toHaveTextContent("Enter valid JSON");
    expect(onChange).not.toHaveBeenCalled();

    fireEvent.change(editor, { target: { value: '{"profile":{"score":1}}' } });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Format Structured Metadata JSON" }));
    expect(editor).toHaveValue(`{\n  "profile": {\n    "score": 1\n  }\n}`);
    await user.click(screen.getByRole("button", { name: "Apply JSON" }));
    expect(dialog).not.toBeInTheDocument();
    expect(onChange).toHaveBeenLastCalledWith({ structured_metadata: { profile: { score: 1 } } });
  });

  it("shows syntax-highlighted JSON with deeper branches collapsed by default", async () => {
    const user = userEvent.setup();
    const value = {
      profile: { details: { metrics: { score: 0.95, reviewed: true } } },
      labels: ["one", "two"],
    };
    renderWithDefinition(<CustomFieldsDisplay customFields={{ structured_metadata: value }} entityType="video" />);

    expect(screen.queryByLabelText("Structured Metadata JSON value")).not.toBeInTheDocument();
    expect(screen.getByText("JSON object · 2 properties")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "View Structured Metadata JSON" }));

    expect(screen.getByRole("dialog", { name: "Structured Metadata" })).toHaveClass("h-[90vh]");
    const renderedValue = screen.getByLabelText("Structured Metadata JSON value");
    expect(renderedValue).toHaveClass("bg-background");
    expect(screen.getByText('"profile"')).toHaveAttribute("data-json-token", "key");
    expect(screen.getByText('"one"')).toHaveAttribute("data-json-token", "string");
    expect(screen.queryByText('"score"')).not.toBeInTheDocument();

    const metricsToggle = screen.getByRole("button", { name: "Expand $.profile.details.metrics" });
    expect(metricsToggle).toHaveAttribute("aria-expanded", "false");
    await user.click(metricsToggle);
    expect(screen.getByRole("button", { name: "Collapse $.profile.details.metrics" })).toHaveAttribute(
      "aria-expanded",
      "true",
    );
    expect(screen.getByText('"score"')).toHaveAttribute("data-json-token", "key");
    expect(screen.getByText("0.95")).toHaveAttribute("data-json-token", "number");

    await user.click(screen.getByRole("button", { name: "Collapse $.profile" }));
    expect(screen.queryByText('"details"')).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Close JSON viewer" }));
    expect(screen.queryByRole("dialog", { name: "Structured Metadata" })).not.toBeInTheDocument();
  });

  it("traps dialog focus, closes with Escape, and restores focus", async () => {
    const user = userEvent.setup();
    renderWithDefinition(
      <CustomFieldsDisplay customFields={{ structured_metadata: { ready: true } }} entityType="video" />,
    );

    const launchButton = screen.getByRole("button", { name: "View Structured Metadata JSON" });
    await user.click(launchButton);
    const headerClose = screen.getByRole("button", { name: "Close JSON viewer" });
    await waitFor(() => expect(headerClose).toHaveFocus());

    await user.tab({ shift: true });
    expect(screen.getByRole("button", { name: "Close" })).toHaveFocus();
    await user.tab();
    expect(headerClose).toHaveFocus();

    await user.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "Structured Metadata" })).not.toBeInTheDocument();
    expect(launchButton).toHaveFocus();
  });

  it("falls back to plain editor rendering for very large JSON values", async () => {
    const user = userEvent.setup();
    renderWithDefinition(
      <CustomFieldsEditor value={{ structured_metadata: "x".repeat(100_001) }} onChange={vi.fn()} entityType="video" />,
    );

    await user.click(screen.getByRole("button", { name: "Edit Structured Metadata JSON" }));
    const highlight = document.querySelector("[data-json-editor-highlight]");
    expect(highlight).toBeInTheDocument();
    expect(highlight?.querySelector("[data-json-token]")).not.toBeInTheDocument();
  });

  it("does not auto-expand containers with many immediate entries", async () => {
    const user = userEvent.setup();
    const value = Object.fromEntries(Array.from({ length: 201 }, (_, index) => [`property${index}`, index]));
    renderWithDefinition(<CustomFieldsDisplay customFields={{ structured_metadata: value }} entityType="video" />);

    await user.click(screen.getByRole("button", { name: "View Structured Metadata JSON" }));
    expect(screen.getByRole("button", { name: "Expand JSON root" })).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByText('"property0"')).not.toBeInTheDocument();
  });

  it("discards dialog edits when cancelled", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithDefinition(
      <CustomFieldsEditor value={{ structured_metadata: { ready: false } }} onChange={onChange} entityType="video" />,
    );

    await user.click(screen.getByRole("button", { name: "Edit Structured Metadata JSON" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Structured Metadata JSON" }), {
      target: { value: '{"ready":true}' },
    });
    await user.click(screen.getByRole("button", { name: "Cancel JSON editing" }));

    expect(screen.queryByRole("dialog", { name: "Structured Metadata" })).not.toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
  });

  it("blocks an enclosing save until an invalid JSON draft is corrected", async () => {
    const user = userEvent.setup();
    const onSave = vi.fn();
    renderWithDefinition(<SaveHarness onSave={onSave} />);
    const save = screen.getByRole("button", { name: "Save entity" });
    await user.click(screen.getByRole("button", { name: "Add Structured Metadata JSON" }));
    const editor = screen.getByRole("textbox", { name: "Structured Metadata JSON" });

    fireEvent.change(editor, { target: { value: "{" } });
    await waitFor(() => expect(save).toBeDisabled());
    await user.click(save);
    expect(onSave).not.toHaveBeenCalled();

    fireEvent.change(editor, { target: { value: "9007199254740993" } });
    expect(screen.getByRole("alert")).toHaveTextContent("JSON integers must be between");
    expect(save).toBeDisabled();

    fireEvent.change(editor, { target: { value: "1e1000000" } });
    expect(screen.getByRole("alert")).toHaveTextContent("JSON numbers must be finite");
    expect(save).toBeDisabled();

    fireEvent.change(editor, { target: { value: '{"ready":true}' } });
    await waitFor(() => expect(save).toBeEnabled());
    await user.click(screen.getByRole("button", { name: "Apply JSON" }));
    await user.click(save);
    expect(onSave).toHaveBeenCalledOnce();
  });
});

describe("long text custom fields", () => {
  it("uses a multiline editor and preserves the complete value as one scalar", () => {
    const onChange = vi.fn();
    renderWithDefinition(
      <CustomFieldsEditor value={{ notes: "Short values work too." }} onChange={onChange} entityType="performer" />,
      [longTextDefinition],
      "performer",
    );

    const editor = screen.getByRole("textbox");
    expect(editor.tagName).toBe("TEXTAREA");
    const nextValue = `First paragraph\n\n${"x".repeat(5_001)}\nLast paragraph`;
    fireEvent.change(editor, { target: { value: nextValue } });
    expect(onChange).toHaveBeenLastCalledWith({ notes: nextValue });
  });

  it("presents multiline values without collapsing their line breaks", () => {
    const value = "First paragraph\n\nSecond paragraph";
    const { container } = renderWithDefinition(
      <CustomFieldsDisplay customFields={{ notes: value }} entityType="performer" />,
      [longTextDefinition],
      "performer",
    );

    const display = container.querySelector(".whitespace-pre-wrap");
    expect(display).not.toBeNull();
    expect(display?.textContent).toBe(value);
  });
});
