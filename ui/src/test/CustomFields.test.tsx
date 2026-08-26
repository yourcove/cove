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
      <button type="button" disabled={!isValid} onClick={onSave}>Save entity</button>
    </>
  );
}

describe("JSON custom fields", () => {
  it("keeps invalid editor text visible and emits structured JSON only after it becomes valid", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithDefinition(
      <CustomFieldsEditor
        value={{ structured_metadata: { profile: { score: 0.95 }, labels: ["one", "two"] } }}
        onChange={onChange}
        entityType="video"
      />,
    );

    const editor = screen.getByRole("textbox", { name: "Structured Metadata JSON" });
    expect(editor).toHaveValue(`{\n  "profile": {\n    "score": 0.95\n  },\n  "labels": [\n    "one",\n    "two"\n  ]\n}`);

    fireEvent.change(editor, { target: { value: '{"profile":' } });
    expect(editor).toHaveValue('{"profile":');
    expect(screen.getByRole("alert")).toHaveTextContent("Enter valid JSON");
    expect(onChange).not.toHaveBeenCalled();

    fireEvent.change(editor, { target: { value: '{"profile":{"score":1}}' } });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(onChange).toHaveBeenLastCalledWith({ structured_metadata: { profile: { score: 1 } } });

    await user.click(screen.getByRole("button", { name: "Format Structured Metadata JSON" }));
    expect(editor).toHaveValue(`{\n  "profile": {\n    "score": 1\n  }\n}`);
  });

  it("presents JSON as an indented structured block", () => {
    const value = { profile: { score: 0.95, reviewed: true }, labels: ["one", "two"] };
    renderWithDefinition(
      <CustomFieldsDisplay customFields={{ structured_metadata: value }} entityType="video" />,
    );

    const renderedValue = screen.getByLabelText("Structured Metadata JSON value");
    expect(renderedValue.tagName).toBe("PRE");
    expect(renderedValue).toHaveTextContent(JSON.stringify(value, null, 2), { normalizeWhitespace: false });
  });

  it("blocks an enclosing save until an invalid JSON draft is corrected", async () => {
    const user = userEvent.setup();
    const onSave = vi.fn();
    renderWithDefinition(<SaveHarness onSave={onSave} />);
    const editor = screen.getByRole("textbox", { name: "Structured Metadata JSON" });
    const save = screen.getByRole("button", { name: "Save entity" });

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
    await user.click(save);
    expect(onSave).toHaveBeenCalledOnce();
  });
});

describe("long text custom fields", () => {
  it("uses a multiline editor and preserves the complete value as one scalar", () => {
    const onChange = vi.fn();
    renderWithDefinition(
      <CustomFieldsEditor
        value={{ notes: "Short values work too." }}
        onChange={onChange}
        entityType="performer"
      />,
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
