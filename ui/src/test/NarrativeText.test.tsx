import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { NarrativeText, useMarkdownRenderingEnabled } from "../components/NarrativeText";

const mocks = vi.hoisted(() => ({
  user: {
    id: "1",
    username: "tester",
    kind: "user" as const,
    permissions: ["*"],
    uiPreferences: null as { renderMarkdown?: boolean | null } | null,
  },
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: mocks.user }),
}));

function PreferenceProbe() {
  return <span>{useMarkdownRenderingEnabled() ? "enabled" : "disabled"}</span>;
}

afterEach(() => {
  mocks.user.uiPreferences = null;
});

describe("NarrativeText", () => {
  it("renders Markdown-like ASCII literally by default", () => {
    render(<NarrativeText>{"**literal**\n_under_score_\n# heading"}</NarrativeText>);

    expect(screen.getByText(/\*\*literal\*\*/)).toBeInTheDocument();
    expect(screen.queryByRole("heading")).not.toBeInTheDocument();
    expect(screen.queryByText("literal", { selector: "strong" })).not.toBeInTheDocument();
  });

  it("renders safe textual Markdown when enabled", () => {
    mocks.user.uiPreferences = { renderMarkdown: true };

    render(
      <NarrativeText>
        {"# Heading\n\n**Bold** [safe](https://example.com) ![remote](https://example.com/image.jpg)\n\n<div>raw</div>"}
      </NarrativeText>,
    );

    expect(screen.getByRole("heading", { name: "Heading" })).toBeInTheDocument();
    expect(screen.getByText("Bold", { selector: "strong" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "safe" })).toHaveAttribute("target", "_blank");
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
    expect(document.querySelector("div div")).not.toHaveTextContent("raw");
  });

  it("keeps safe relative links and suppresses unsafe protocols", () => {
    mocks.user.uiPreferences = { renderMarkdown: true };

    render(
      <NarrativeText>
        {
          "[relative](notes/123) [dot](./notes) [root](/notes) [fragment](#notes) [unsafe](javascript:alert(1)) [data](data:text/html,bad)"
        }
      </NarrativeText>,
    );

    expect(screen.getByRole("link", { name: "relative" })).toHaveAttribute("href", "notes/123");
    expect(screen.getByRole("link", { name: "dot" })).toHaveAttribute("href", "./notes");
    expect(screen.getByRole("link", { name: "root" })).toHaveAttribute("href", "/notes");
    expect(screen.getByRole("link", { name: "fragment" })).toHaveAttribute("href", "#notes");
    expect(screen.queryByRole("link", { name: "unsafe" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "data" })).not.toBeInTheDocument();
  });

  it("exposes the resolved preference through the public hook", () => {
    const view = render(<PreferenceProbe />);
    expect(screen.getByText("disabled")).toBeInTheDocument();

    mocks.user.uiPreferences = { renderMarkdown: true };
    view.rerender(<PreferenceProbe />);
    expect(screen.getByText("enabled")).toBeInTheDocument();
  });
});
