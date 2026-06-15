import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { TutorialStoryboardDialog, builtinTutorialTopics } from "../components/TutorialStoryboardDialog";
import type { ExtensionTutorialTopic } from "../api/types";

describe("TutorialStoryboardDialog", () => {
  it("gives every built-in manual slide a screenshot", () => {
    const missing = builtinTutorialTopics.flatMap((topic) =>
      topic.slides
        .filter((slide) => !slide.imageSrc || !slide.imageAlt)
        .map((slide) => `${topic.id}/${slide.id}`),
    );

    expect(missing).toEqual([]);
  });

  it("renders colored manual callouts without the box label text", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "list-pages", slideId: "anatomy" }}
      />,
    );

    expect(screen.queryByText(/Green box:/i)).not.toBeInTheDocument();

    const point = screen.getByText("the view switcher for grid, wall, feed, and other layouts").closest("[data-box-tone]");
    expect(point).not.toBeNull();
    expect(point).toHaveAttribute("data-box-tone", "green");
    expect((point as HTMLElement).className).toContain("border-green-500/55");
  });

  it("renders extension manual subtopics under their parent topic", async () => {
    const user = userEvent.setup();
    const extensionTopics: ExtensionTutorialTopic[] = [
      {
        id: "docs.bundle",
        title: "Docs Bundle",
        description: "Docs overview.",
        extensionId: "docs.bundle",
        order: 80,
        slides: [
          {
            id: "overview",
            title: "Docs overview",
            caption: "Start with extension docs.",
            points: ["Open the manual"],
          },
        ],
      },
      {
        id: "docs.bundle.child",
        title: "Docs Child",
        description: "Nested extension docs.",
        extensionId: "docs.bundle",
        parentTopicId: "docs.bundle",
        order: 81,
        slides: [
          {
            id: "settings",
            title: "Open nested docs",
            bodyMarkdown: "Use **extension manual pages** for workflows that live outside Cove source.\n\n- Contribute a topic\n- Attach matching contexts",
            imageSrc: "docs/topic.png",
            imageAlt: "Docs topic screenshot",
            links: [{ label: "Extension docs", url: "https://example.com/docs" }],
          },
        ],
      },
    ];

    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "docs.bundle" }}
        extensionTopics={extensionTopics}
      />,
    );

    expect(screen.getByRole("button", { name: /Docs overview/i })).toHaveAttribute("data-topic-depth", "0");
    const childButton = screen.getByRole("button", { name: /Docs Child/i });
    expect(childButton).toHaveAttribute("data-topic-depth", "1");

    await user.click(childButton);

    expect(screen.getByRole("heading", { name: "Docs Child" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Open nested docs" })).toBeInTheDocument();
    expect(screen.getByText("extension manual pages")).toBeInTheDocument();
    expect(screen.getByAltText("Docs topic screenshot")).toHaveAttribute("src", "/api/extensions/assets/docs.bundle/docs/topic.png");
    expect(screen.getByRole("link", { name: /Extension docs/i })).toHaveAttribute("href", "https://example.com/docs");
  });

  it("opens the topic whose manual contexts match the current UI context", () => {
    const extensionTopics: ExtensionTutorialTopic[] = [
      {
        id: "docs.search",
        title: "Docs Search",
        description: "Search workflows.",
        contexts: ["settings-tab:extensions/docs/search", "panel:docs-search"],
        pages: ["settings"],
        extensionId: "docs.bundle",
        order: 80,
        slides: [
          {
            id: "search",
            title: "Find related docs",
            bodyMarkdown: "Use the **Search** panel after docs are indexed.",
          },
        ],
      },
    ];

    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ page: "settings", contexts: ["panel:docs-search", "page:settings"] }}
        currentPage="settings"
        extensionTopics={extensionTopics}
      />,
    );

    expect(screen.getByRole("heading", { name: "Docs Search" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Find related docs" })).toBeInTheDocument();
    expect(screen.getByText("Search", { selector: "strong" })).toBeInTheDocument();
  });

  it("prefers explicit settings contexts over generic settings page topics", () => {
    const extensionTopics: ExtensionTutorialTopic[] = [
      {
        id: "docs.bundle",
        title: "Docs Bundle",
        description: "Docs settings and workflows.",
        contexts: ["settings-tab:extensions/docs", "route:/settings/extensions/docs"],
        pages: ["settings"],
        extensionId: "docs.bundle",
        order: 80,
        slides: [{ id: "overview", title: "Configure extension docs", bodyMarkdown: "Configure docs here." }],
      },
    ];

    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ page: "settings", contexts: ["page:settings", "settings-tab:extensions/docs"] }}
        currentPage="settings"
        extensionTopics={extensionTopics}
      />,
    );

    expect(screen.getByRole("heading", { name: "Docs Bundle" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Configure extension docs" })).toBeInTheDocument();
  });

  it("prefers explicit detail tab contexts over generic detail page topics", () => {
    const extensionTopics: ExtensionTutorialTopic[] = [
      {
        id: "docs.related",
        title: "Related Docs",
        description: "Related item workflows.",
        contexts: ["detail-tab:related", "panel:related-docs"],
        pages: ["video"],
        extensionId: "docs.bundle",
        order: 83,
        slides: [{ id: "related", title: "Find related items", bodyMarkdown: "Use Related." }],
      },
    ];

    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ page: "videos", contexts: ["page:videos", "detail-tab:related"] }}
        currentPage="video"
        extensionTopics={extensionTopics}
      />,
    );

    expect(screen.getByRole("heading", { name: "Related Docs" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Find related items" })).toBeInTheDocument();
  });
});

