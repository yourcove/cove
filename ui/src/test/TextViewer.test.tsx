import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { TextViewer } from "../components/TextViewer";

describe("TextViewer", () => {
  it("reflows hard-wrapped plain text into readable paragraphs", () => {
    render(<TextViewer renderMode="text" content={"Alpha line\ncontinues here\n\nSecond paragraph"} />);

    const content = screen.getByTestId("text-viewer-content");

    expect(content.querySelectorAll("p")).toHaveLength(2);
    expect(content).toHaveTextContent("Alpha line continues here");
    expect(content).toHaveTextContent("Second paragraph");
  });

  it("preserves meaningful line breaks for list-like text blocks", () => {
    render(<TextViewer renderMode="text" content={"Reading list\n- First item\n- Second item\n\nClosing paragraph"} />);

    const content = screen.getByTestId("text-viewer-content");
    const blocks = content.querySelectorAll("p");

    expect(blocks).toHaveLength(2);
    expect(blocks[0]).toHaveClass("whitespace-pre-wrap");
    expect(blocks[0]?.textContent).toContain("- First item");
    expect(blocks[0]?.textContent).toContain("- Second item");
    expect(blocks[1]?.textContent).toBe("Closing paragraph");
  });

  it("renders sanitized html content without flattening inline structure", () => {
    render(
      <TextViewer
        renderMode="html"
        content={'<article><h1>Chapter One</h1><p>First <em>paragraph</em>.</p><script>alert("x")</script></article>'}
      />,
    );

    const content = screen.getByTestId("text-viewer-content");

    expect(content.querySelector("h1")?.textContent).toBe("Chapter One");
    expect(content.querySelector("em")?.textContent).toBe("paragraph");
    expect(content.querySelector("script")).toBeNull();
  });
});
