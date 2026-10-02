import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { StringListEditor } from "../components/StringListEditor";

function TestHost({ initialValues = [""] }: { initialValues?: string[] }) {
  const [values, setValues] = useState(initialValues);

  return (
    <StringListEditor
      values={values}
      onChange={setValues}
      placeholder="https://..."
      addLabel="Add URL"
      inputType="url"
    />
  );
}

describe("StringListEditor", () => {
  it("keeps the edited input focused while typing", async () => {
    const user = userEvent.setup();

    render(<TestHost />);

    const input = screen.getByPlaceholderText("https://...");
    await user.type(input, "abc");

    expect(screen.getByDisplayValue("abc")).toHaveFocus();
  });

  it("keeps the following inputs mounted when an earlier entry is removed", async () => {
    const user = userEvent.setup();

    render(<TestHost initialValues={["first", "second", "third"]} />);
    const third = screen.getByDisplayValue("third");

    await user.click(screen.getAllByTitle("Remove entry")[0]);

    expect(screen.queryByDisplayValue("first")).not.toBeInTheDocument();
    expect(screen.getByDisplayValue("third")).toBe(third);
  });

  it("gives an added entry its own input without remounting the existing ones", async () => {
    const user = userEvent.setup();

    render(<TestHost initialValues={["first"]} />);
    const first = screen.getByDisplayValue("first");

    await user.click(screen.getByRole("button", { name: "+ Add URL" }));

    expect(screen.getAllByPlaceholderText("https://...")).toHaveLength(2);
    expect(screen.getByDisplayValue("first")).toBe(first);
  });
});
