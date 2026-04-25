import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { StringListEditor } from "../components/StringListEditor";

function TestHost() {
  const [values, setValues] = useState([""]);

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
});