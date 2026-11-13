import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, describe, expect, it, vi } from "vitest";
import { TutorialStoryboardDialog, builtinTutorialTopics } from "../components/TutorialStoryboardDialog";
import { createManualOpenRequest } from "../components/ManualContext";
import { LazyMarkdown } from "../components/LazyMarkdown";
import type { ExtensionTutorialTopic } from "../api/types";

const sharedFeatureGuideModules = import.meta.glob("../../../docs/feature-guides/*.json", {
  eager: true,
  import: "default",
}) as Record<string, { schemaVersion?: number; id?: string }>;

// Manual articles render through a Markdown renderer that loads on first use. Load it before any
// test, so each test sees rendered Markdown whatever order the tests run in, and so a load started
// by one test cannot finish while a later test runs.
beforeAll(async () => {
  render(<LazyMarkdown>{"**ready**"}</LazyMarkdown>);
  await screen.findByText("ready", { selector: "strong" });
  cleanup();
});

describe("TutorialStoryboardDialog", () => {
  it("locks background page scrolling while the manual is open", () => {
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "auto";

    try {
      const { rerender } = render(<TutorialStoryboardDialog open onClose={vi.fn()} extensionTopics={[]} />);

      expect(document.body.style.overflow).toBe("hidden");

      rerender(<TutorialStoryboardDialog open={false} onClose={vi.fn()} extensionTopics={[]} />);

      expect(document.body.style.overflow).toBe("auto");
    } finally {
      document.body.style.overflow = previousOverflow;
    }
  });

  it("closes through the latest onClose when Escape is pressed", () => {
    const firstOnClose = vi.fn();
    const latestOnClose = vi.fn();
    const extensionTopics: ExtensionTutorialTopic[] = [];
    const { rerender } = render(
      <TutorialStoryboardDialog open onClose={firstOnClose} extensionTopics={extensionTopics} />,
    );

    rerender(<TutorialStoryboardDialog open onClose={latestOnClose} extensionTopics={extensionTopics} />);
    fireEvent.keyDown(window, { key: "Escape" });

    expect(firstOnClose).not.toHaveBeenCalled();
    expect(latestOnClose).toHaveBeenCalledTimes(1);
  });

  it("gives every established storyboard slide a screenshot", () => {
    const missing = builtinTutorialTopics.flatMap((topic) =>
      topic.slides
        .filter((slide) => !slide.guideArticle && (!slide.imageSrc || !slide.imageAlt))
        .map((slide) => `${topic.id}/${slide.id}`),
    );

    expect(missing).toEqual([]);
  });

  it("renders the shared keyboard shortcut guide as an in-app manual topic", () => {
    render(<TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "keyboard-shortcuts" }} />);

    expect(screen.getByRole("heading", { level: 3, name: "Keyboard shortcuts" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Press ? to open the shortcut reference" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Read single keys, combinations, and chords" })).toBeInTheDocument();
    expect(screen.getByText(/Press \? from any page when you are not typing in a field/)).toBeInTheDocument();
    expect(screen.getByAltText(/Keyboard Shortcuts reference showing global actions/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Customize keyboard shortcuts" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Next" })).not.toBeInTheDocument();
  });

  it("opens a shared guide app link inside Cove", async () => {
    const user = userEvent.setup();
    const onAppNavigate = vi.fn();
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "custom-fields" }}
        extensionTopics={[]}
        onAppNavigate={onAppNavigate}
      />,
    );

    const link = screen.getByRole("link", { name: "Open Custom Fields settings" });
    expect(link).toHaveAttribute("href", "/settings/library/custom-fields");
    expect(link).not.toHaveAttribute("target");

    await user.click(link);

    expect(onAppNavigate).toHaveBeenCalledWith("/settings/library/custom-fields");
  });

  it("keeps customization as a child of the keyboard shortcut overview", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "keyboard-shortcuts-customization" }}
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("button", { name: /^Keyboard shortcuts/ })).toHaveAttribute("data-topic-depth", "0");
    expect(screen.getByRole("button", { name: /^Customize keyboard shortcuts/ })).toHaveAttribute(
      "data-topic-depth",
      "1",
    );
    expect(screen.getByRole("heading", { level: 3, name: "Customize keyboard shortcuts" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Create and edit your own preset" })).toBeInTheDocument();
  });

  it("lets users collapse the category containing the selected article", async () => {
    const user = userEvent.setup();
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "keyboard-shortcuts-customization" }}
        extensionTopics={[]}
      />,
    );

    await user.click(screen.getByRole("button", { name: "Collapse Keyboard shortcuts" }));

    expect(screen.getByRole("button", { name: "Expand Keyboard shortcuts" })).toHaveAttribute("aria-expanded", "false");
    const topicRail = screen.getByRole("searchbox", { name: "Search User Guide topics" }).closest("aside");
    expect(within(topicRail!).queryByRole("button", { name: /^Customize keyboard shortcuts/ })).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 3, name: "Customize keyboard shortcuts" })).toBeInTheDocument();
  });

  it("keeps the built-in in-app topic list in exact parity with the shared guides", () => {
    const sharedGuideIds = Object.values(sharedFeatureGuideModules)
      .filter((guide) => guide.schemaVersion === 1 && guide.id)
      .map((guide) => guide.id)
      .sort();
    const topicIds = builtinTutorialTopics.map((topic) => topic.id).sort();

    expect(sharedGuideIds.length).toBeGreaterThan(0);
    expect(topicIds).toEqual(sharedGuideIds);
  });

  it("renders the shared User Guide overview as an in-app directory", () => {
    render(
      <TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "user-guide" }} extensionTopics={[]} />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "User Guide" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Guided tutorials" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Browse and play" })).toBeInTheDocument();
    expect(
      screen.getByAltText("Cove User Guide with a searchable topic list and a shared article"),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Media types" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Scan your first library" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Troubleshooting" })).toBeInTheDocument();
  });

  it("keeps long desktop topic names readable in a wider topic rail", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "content-performers" }}
        extensionTopics={[]}
      />,
    );

    const topicButton = screen.getByRole("button", { name: /Providers, scrapers, and downloaders/i });
    expect(within(topicButton).getByText("Providers, scrapers, and downloaders")).toHaveClass(
      "whitespace-normal",
      "break-words",
    );
    expect(screen.getByRole("region", { name: "User Guide article" }).parentElement).toHaveClass(
      "xl:grid-cols-[22rem_minmax(0,1.45fr)_minmax(18rem,0.55fr)]",
    );
  });

  it("starts a newly selected User Guide topic at the top of the article", async () => {
    const user = userEvent.setup();
    render(
      <TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "user-guide" }} extensionTopics={[]} />,
    );

    const articleScroller = screen.getByRole("region", { name: "User Guide article" });
    articleScroller.scrollTop = 1200;

    await user.click(screen.getByRole("button", { name: "Organize your library" }));

    expect(articleScroller.scrollTop).toBe(0);
    expect(screen.getByRole("heading", { level: 2, name: "Organizing your library" })).toBeInTheDocument();
  });

  it("renders media types as a top-level overview with one child per type", async () => {
    const user = userEvent.setup();
    const extensionTopics: ExtensionTutorialTopic[] = [];

    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "media-types" }}
        extensionTopics={extensionTopics}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Media types" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Choose the record that matches the content" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Learn the features of each media type" })).toBeInTheDocument();
    expect(
      screen.getByAltText("Cove navigation with video, image, audio, text, and gallery pages"),
    ).toBeInTheDocument();
    const topicRail = screen.getByRole("searchbox", { name: "Search User Guide topics" }).closest("aside");
    expect(topicRail).not.toBeNull();
    const topics = within(topicRail!);
    expect(topics.getByRole("button", { name: /^Media types/ })).toHaveAttribute("data-topic-depth", "0");
    expect(topics.getByRole("button", { name: /^Videos/ })).toHaveAttribute("data-topic-depth", "1");
    expect(topics.getByRole("button", { name: /^Images/ })).toHaveAttribute("data-topic-depth", "1");
    expect(topics.getByRole("button", { name: /^Galleries/ })).toHaveAttribute("data-topic-depth", "1");
    expect(topics.getByRole("button", { name: /^Audio/ })).toHaveAttribute("data-topic-depth", "1");
    expect(topics.getByRole("button", { name: /^Text/ })).toHaveAttribute("data-topic-depth", "1");
    expect(screen.getByRole("link", { name: "Media reference" })).toHaveAttribute(
      "href",
      "https://yourcove.net/docs/reference/media/",
    );

    await user.click(topics.getByRole("button", { name: /^Images/ }));
    expect(screen.getByRole("heading", { level: 3, name: "Images" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Keep each still independently useful" })).toBeInTheDocument();
    expect(screen.getByAltText(/image detail page with the image viewer/)).toBeInTheDocument();
  });

  it("renders organizing your library from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "organizing" }} extensionTopics={[]} />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Organizing your library" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "The main building blocks" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Tags versus groups" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Tag when something appears" })).toBeInTheDocument();
    expect(
      screen.getByAltText("Blank Create Tag form with classification and relationship fields"),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Dynamic groups" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Tags reference" })).toHaveAttribute(
      "href",
      "https://yourcove.net/docs/reference/tags/",
    );
  });

  it("renders dynamic groups from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "dynamic-groups" }} extensionTopics={[]} />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "When to use dynamic groups" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Decide whether you need a group" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Treat membership as current state" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Built-in groups" })).toBeInTheDocument();
    expect(screen.getByAltText("Blank Create Group form configured as a dynamic group")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Search and filters" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Segments and compilations" })).toBeInTheDocument();
  });

  it("renders segments and compilations from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "segments-and-compilations" }}
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Segments and compilations" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Choose the right representation" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Raw segment" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Display profiles" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "What this enables" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "When to adopt timeline structure" })).toBeInTheDocument();
    expect(
      screen.getByAltText("Display Profiles settings with built-in profiles and a resolution rule"),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "When to use dynamic groups" })).toBeInTheDocument();
  });

  it("renders search and filters from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "search-and-filters" }}
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Search and filters" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "What to use first" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Search across everything" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Remote ID filters" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Go further with filters" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "View modes and page size" })).toBeInTheDocument();
    expect(
      screen.getByAltText("Filter editor with searchable video criteria and an empty configuration area"),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Combine filters" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Use saved filters" })).toBeInTheDocument();
  });

  it("keeps the User Guide hierarchy to two levels", () => {
    render(
      <TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "combine-filters" }} extensionTopics={[]} />,
    );

    expect(screen.getByRole("button", { name: /^Find anything/ })).toHaveAttribute("data-topic-depth", "0");
    expect(screen.getByRole("button", { name: /^Search and filters/ })).toHaveAttribute("data-topic-depth", "0");
    const savedFiltersButton = screen
      .getAllByRole("button", { name: /^Use saved filters/ })
      .find((button) => button.hasAttribute("data-topic-depth"))!;
    const combineFiltersButton = screen
      .getAllByRole("button", { name: /^Combine filters/ })
      .find((button) => button.hasAttribute("data-topic-depth"))!;
    expect(savedFiltersButton).toHaveAttribute("data-topic-depth", "1");
    expect(combineFiltersButton).toHaveAttribute("data-topic-depth", "1");
    expect(
      savedFiltersButton.compareDocumentPosition(combineFiltersButton) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();

    const topicsById = new Map(builtinTutorialTopics.map((topic) => [topic.id, topic]));
    for (const topic of builtinTutorialTopics) {
      const parent = topic.parentTopicId ? topicsById.get(topic.parentTopicId) : undefined;
      expect(parent?.parentTopicId, `${topic.title} must not be nested three levels deep`).toBeUndefined();
    }
  });

  it("renders practical guide recipes as a collapsed list", async () => {
    const user = userEvent.setup();
    render(
      <TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "combine-filters" }} extensionTopics={[]} />,
    );

    const recipes = screen.getByRole("list", { name: "Recipes" });
    const urlRecipe = within(recipes).getByText("Include archive URLs but exclude private entries").closest("summary");
    const performerRecipe = within(recipes)
      .getByText("Require different performers in overlapping age ranges")
      .closest("summary");
    expect(urlRecipe?.closest("details")).not.toHaveAttribute("open");
    expect(performerRecipe?.closest("details")).not.toHaveAttribute("open");
    expect(screen.getByAltText(/URL Includes archive\.example/)).not.toBeVisible();

    await user.click(urlRecipe!);

    expect(urlRecipe?.closest("details")).toHaveAttribute("open");
    expect(screen.getByText(/Pairing it with Includes archive\.example/)).toBeInTheDocument();
    expect(screen.getByAltText(/URL Includes archive\.example/)).toBeVisible();
    expect(screen.getByAltText(/URL Includes archive\.example/)).not.toHaveAttribute(
      "src",
      "assets/combine-filters-urls.webp",
    );
    expect(screen.getByRole("button", { name: "Use saved filters" })).toBeInTheDocument();
  });

  it("provides collapsed single-field and multiple-field sorting recipes", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "search-and-filters" }}
        extensionTopics={[]}
      />,
    );

    const recipes = screen.getByRole("list", { name: "Recipes" });
    expect(within(recipes).getByText("Sort by one field").closest("details")).not.toHaveAttribute("open");
    expect(within(recipes).getByText("Sort by multiple fields").closest("details")).not.toHaveAttribute("open");
  });

  it("renders metadata provenance from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "metadata-provenance" }}
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Metadata provenance" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Why it matters" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "What to review" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Correct the right layer" })).toBeInTheDocument();
    expect(screen.getByAltText("Tag provenance popup showing a manual source and applied time")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Provenance reference" })).toHaveAttribute(
      "href",
      "https://yourcove.net/docs/reference/provenance/",
    );
  });

  it("renders providers, scrapers, and downloaders from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "providers-scrapers-downloaders" }}
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Providers, scrapers, and downloaders" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "The three main roles" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Configure a metadata server" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Install and use downloaders" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Review repeatedly with Tagger" })).toBeInTheDocument();
    expect(
      screen.getByRole("table", { name: "Choose services that match the material in your library." }),
    ).toBeInTheDocument();
    expect(screen.getByText("https://stashdb.org/graphql")).toBeInTheDocument();
    expect(
      screen.getByAltText(
        "Empty metadata server row showing name, endpoint, API key, request limit, and validation controls",
      ),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Review metadata provenance" })).toBeInTheDocument();
  });

  it("renders users, roles, and permissions from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "users-roles-permissions" }}
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Users, roles, and permissions" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "The access model" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "A practical setup order" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Roles versus content rules" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Share links" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Internet exposure" })).toBeInTheDocument();
    expect(
      screen.getByAltText("New role form showing role, saved-filter, segment, and streaming permission choices"),
    ).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Content rules reference" })).toHaveAttribute(
      "href",
      "https://yourcove.net/docs/reference/content-rules/",
    );
  });

  it("renders backups, migrations, and upgrades from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "backups-migrations-upgrades" }}
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Backups, migrations, and upgrades" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Know what each backup contains" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Upgrade Docker" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Restore an existing installation" })).toBeInTheDocument();
    expect(
      screen.getByRole("table", { name: "Each backup protects a different part of the installation." }),
    ).toBeInTheDocument();
    expect(screen.getByText(/docker compose --file docker-compose\.allinone\.yml pull/)).toBeInTheDocument();
    expect(
      screen.getByAltText("Backup and Restore settings showing Backup Database and Backup Config controls"),
    ).toBeInTheDocument();
  });

  it("renders troubleshooting from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "troubleshooting" }} extensionTopics={[]} />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Troubleshooting" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Find the logs" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Safe Docker checks" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Scan and media problems" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Prepare a useful report" })).toBeInTheDocument();
    expect(screen.getByText(/config --services/)).toBeInTheDocument();
    expect(
      screen.getByAltText("Cove Settings navigation with System Info expanded and Logs visible"),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Metadata provenance" })).toBeInTheDocument();
  });

  it("renders Scan your first library from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "scan-your-first-library" }}
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Scan your first library" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Before you scan" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Run the scan" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Recover from a bad first scan" })).toBeInTheDocument();
    expect(
      screen.getByAltText("Expanded Scan card showing generated-asset choices, force rescan, and the Run control"),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Troubleshooting" })).toBeInTheDocument();
  });

  it("renders Explore your library from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "explore-your-library" }}
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Explore your library" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Start from home" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Open one item" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Follow a relationship" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Leave one personal breadcrumb" })).toBeInTheDocument();
    expect(
      screen.getByAltText("Cove home page showing rows of sample videos, studios, groups, and performers"),
    ).toBeInTheDocument();
    expect(
      screen.getByAltText("Sample video detail page with metadata and relationships beside the video player"),
    ).toBeInTheDocument();
  });

  it("renders Find anything from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "find-anything" }} extensionTopics={[]} />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Find anything" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Search across the whole library" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Narrow one list" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Choose the lightest useful tool" })).toBeInTheDocument();
    expect(
      screen.getByAltText("Global search for Lucia showing results grouped into performers, galleries, and images"),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Search and filters" })).toBeInTheDocument();
  });

  it("renders Organize a collection from the shared website guide", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "organize-a-collection" }}
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Organize a collection" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Choose a small theme" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Create the group" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Edit and add the item" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Verify the collection" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Know which structure you just used" })).toBeInTheDocument();
    expect(
      screen.getByAltText("Sample static group detail page showing its description, media counts, and one video item"),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "User Guide" })).toBeInTheDocument();
  });

  it.each(["content-types", "content-images", "content-galleries", "content-audio", "content-texts"])(
    "redirects the legacy %s topic route to Media types",
    (topicId) => {
      render(<TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId }} extensionTopics={[]} />);

      expect(screen.getByRole("heading", { level: 2, name: "Media types" })).toBeInTheDocument();
    },
  );

  it("redirects the legacy tagging topic route to Organizing your library", () => {
    render(<TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "tagging" }} extensionTopics={[]} />);

    expect(screen.getByRole("heading", { level: 2, name: "Organizing your library" })).toBeInTheDocument();
  });

  it("redirects the legacy groups topic route to When to use dynamic groups", () => {
    render(<TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "groups" }} extensionTopics={[]} />);

    expect(screen.getByRole("heading", { level: 2, name: "When to use dynamic groups" })).toBeInTheDocument();
  });

  it.each(["segments", "segments-raw-derived", "segments-display-profiles", "segments-compilations"])(
    "redirects the legacy %s topic route to Segments and compilations",
    (topicId) => {
      render(<TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId }} extensionTopics={[]} />);

      expect(screen.getByRole("heading", { level: 2, name: "Segments and compilations" })).toBeInTheDocument();
    },
  );

  it("redirects the legacy search topic route to Search and filters", () => {
    render(<TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "search" }} extensionTopics={[]} />);

    expect(screen.getByRole("heading", { level: 2, name: "Search and filters" })).toBeInTheDocument();
  });

  it.each(["downloaders", "metadata", "metadata-scrapers", "metadata-servers", "metadata-tagger"])(
    "redirects the legacy %s topic route to Providers, scrapers, and downloaders",
    (topicId) => {
      render(<TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId }} extensionTopics={[]} />);

      expect(
        screen.getByRole("heading", { level: 2, name: "Providers, scrapers, and downloaders" }),
      ).toBeInTheDocument();
    },
  );

  it.each(["security", "security-users", "security-roles-permissions", "security-content-rules", "security-sharing"])(
    "redirects the legacy %s topic route to Users, roles, and permissions",
    (topicId) => {
      render(<TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId }} extensionTopics={[]} />);

      expect(screen.getByRole("heading", { level: 2, name: "Users, roles, and permissions" })).toBeInTheDocument();
    },
  );

  it("redirects the legacy backups topic route to Backups, migrations, and upgrades", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "backups-upgrades" }}
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 2, name: "Backups, migrations, and upgrades" })).toBeInTheDocument();
  });

  it("keeps video detail contextual Help on Use detail pages", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={createManualOpenRequest("video", "videos", "/video/1")}
        currentPage="video"
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 2, name: "Use detail pages" })).toBeInTheDocument();
  });

  it.each([
    ["videos", "Videos"],
    ["images", "Images"],
    ["galleries", "Galleries"],
    ["audios", "Audio"],
    ["texts", "Text"],
  ])("opens %s list contextual Help on its media-type guide", (page, title) => {
    render(
      <TutorialStoryboardDialog open onClose={vi.fn()} request={{ page }} currentPage={page} extensionTopics={[]} />,
    );

    expect(screen.getByRole("heading", { level: 2, name: title })).toBeInTheDocument();
  });

  it("keeps search contextual Help on Search and filters", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ page: "search" }}
        currentPage="search"
        extensionTopics={[]}
      />,
    );

    expect(screen.getByRole("heading", { level: 2, name: "Search and filters" })).toBeInTheDocument();
  });

  it.each(["groups", "group"])("keeps %s contextual Help on When to use dynamic groups", (page) => {
    render(
      <TutorialStoryboardDialog open onClose={vi.fn()} request={{ page }} currentPage={page} extensionTopics={[]} />,
    );

    expect(screen.getByRole("heading", { level: 2, name: "When to use dynamic groups" })).toBeInTheDocument();
  });

  it.each(["image", "gallery", "audio", "text"])("keeps %s detail contextual Help on Use detail pages", (page) => {
    render(
      <TutorialStoryboardDialog open onClose={vi.fn()} request={{ page }} currentPage={page} extensionTopics={[]} />,
    );

    expect(screen.getByRole("heading", { level: 2, name: "Use detail pages" })).toBeInTheDocument();
  });

  it("renders list-page guidance from the shared website guide", () => {
    render(<TutorialStoryboardDialog open onClose={vi.fn()} request={{ topicId: "list-pages" }} />);

    expect(screen.getByRole("heading", { level: 3, name: "Use list pages" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Learn one toolbar for every media list" })).toBeInTheDocument();
    expect(screen.getByAltText(/Video list with view, sort, filter/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Special views" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Next" })).not.toBeInTheDocument();
  });

  it("lets mobile users search and open manual topics without scanning the topic dropdown", async () => {
    const user = userEvent.setup();
    const extensionTopics: ExtensionTutorialTopic[] = [];

    render(<TutorialStoryboardDialog open onClose={vi.fn()} extensionTopics={extensionTopics} />);

    const search = screen.getByRole("searchbox", { name: "Search User Guide topics on mobile" });
    await user.click(search);

    let results = screen.getByRole("region", { name: "Mobile topic search results" });
    expect(within(results).getByRole("button", { name: /Your first hour with Cove/i })).toBeInTheDocument();
    expect(within(results).getByRole("button", { name: /^Keyboard shortcuts/ })).toBeInTheDocument();

    await user.type(search, "keyboard");

    results = screen.getByRole("region", { name: "Mobile topic search results" });
    expect(within(results).getByRole("button", { name: /^Keyboard shortcuts/ })).toBeInTheDocument();
    expect(within(results).getByRole("button", { name: /Customize keyboard shortcuts/i })).toBeInTheDocument();
    expect(within(results).queryByRole("button", { name: /Your first hour with Cove/i })).not.toBeInTheDocument();

    fireEvent.blur(search, { relatedTarget: null });
    expect(results).toBeInTheDocument();

    await user.click(within(results).getByRole("button", { name: /Customize keyboard shortcuts/i }));

    expect(screen.getByRole("heading", { level: 3, name: "Customize keyboard shortcuts" })).toBeInTheDocument();
    expect(search).toHaveValue("");
    expect(screen.queryByRole("region", { name: "Mobile topic search results" })).not.toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Tutorial topic" })).not.toBeInTheDocument();
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
            bodyMarkdown:
              "Use **extension manual pages** for workflows that live outside Cove source.\n\n- Contribute a topic\n- Attach matching contexts",
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
    expect(await screen.findByText("extension manual pages")).toBeInTheDocument();
    expect(screen.getByAltText("Docs topic screenshot")).toHaveAttribute(
      "src",
      "/api/extensions/assets/docs.bundle/docs/topic.png",
    );
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

  it("renders an extension's feature guide as a User Guide article with package images", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "example-hashing" }}
        extensionGuides={[
          {
            extensionId: "com.example.hashing",
            path: "docs/guides/hashing.json",
            guide: {
              schemaVersion: 1,
              id: "example-hashing",
              title: "Example hashing",
              description: "Calculate extra fingerprints.",
              order: 300,
              sections: [
                {
                  id: "choose",
                  title: "Choose fingerprints",
                  blocks: [
                    { type: "steps", items: ["Open the extension settings.", "Enable SHA-256."] },
                    { type: "image", src: "../images/settings.webp", alt: "Hashing settings" },
                  ],
                },
              ],
            },
          },
          {
            extensionId: "com.example.hashing",
            path: "docs/guides/shortcuts.json",
            guide: {
              schemaVersion: 1,
              id: "keyboard-shortcuts",
              title: "Hijacked shortcuts",
              description: "Must not replace Cove's topic.",
              order: 0,
              sections: [{ id: "s", title: "S", blocks: [{ type: "paragraph", text: "Replaced" }] }],
            },
          },
        ]}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Example hashing" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Choose fingerprints" })).toBeInTheDocument();
    expect(screen.getByText("Enable SHA-256.")).toBeInTheDocument();
    expect(screen.getByAltText("Hashing settings")).toHaveAttribute(
      "src",
      "/api/extensions/assets/com.example.hashing/docs/images/settings.webp",
    );
    expect(screen.queryByText("Hijacked shortcuts")).not.toBeInTheDocument();
  });

  it("nests extension feature guides under their own and Cove's topics", async () => {
    const user = userEvent.setup();
    const guide = (id: string, title: string, order: number, parentTopicId?: string) => ({
      extensionId: "com.example.hashing",
      path: `docs/guides/${id}.json`,
      guide: {
        schemaVersion: 1,
        id,
        title,
        description: `${title} description.`,
        order,
        ...(parentTopicId ? { parentTopicId } : {}),
        sections: [{ id: "body", title: `${title} section`, blocks: [{ type: "paragraph", text: `${title} body.` }] }],
      },
    });

    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "example-hashing" }}
        extensionGuides={[
          guide("example-hashing", "Example hashing", 1200),
          guide("example-hashing-failures", "Example failures", 1201, "example-hashing"),
          guide("example-under-extensions", "Example under extensions", 1202, "extensions"),
          guide("example-orphan", "Example orphan", 1203, "missing-parent"),
        ]}
      />,
    );

    const topicButton = (name: string) =>
      screen
        .getAllByRole("button", { name: new RegExp(name) })
        .find((button) => button.hasAttribute("data-topic-depth"));

    expect(topicButton("Example hashing")).toHaveAttribute("data-topic-depth", "0");
    expect(topicButton("Example orphan")).toHaveAttribute("data-topic-depth", "0");

    const child = topicButton("Example failures");
    expect(child).toHaveAttribute("data-topic-depth", "1");

    await user.click(child!);
    expect(screen.getByRole("heading", { name: "Example failures section" })).toBeInTheDocument();
    expect(screen.getByText("Example failures body.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Expand Discover and install extensions" }));
    expect(topicButton("Example under extensions")).toHaveAttribute("data-topic-depth", "1");
  });

  it("keeps extension feature guides with circular parents reachable", async () => {
    const user = userEvent.setup();
    const guide = (id: string, parentTopicId: string) => ({
      extensionId: "com.example.loop",
      path: `docs/guides/${id}.json`,
      guide: {
        schemaVersion: 1,
        id,
        title: `Loop ${id}`,
        description: "Circular parent.",
        order: 1300,
        parentTopicId,
        sections: [{ id: "body", title: "Body", blocks: [{ type: "paragraph", text: "Body." }] }],
      },
    });

    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "extensions" }}
        extensionGuides={[
          guide("loop-self", "loop-self"),
          guide("loop-a", "loop-b"),
          guide("loop-b", "loop-a"),
          guide("loop-c", "loop-a"),
        ]}
      />,
    );

    for (const id of ["loop-self", "loop-a", "loop-b"]) {
      const button = screen
        .getAllByRole("button", { name: new RegExp(`Loop ${id}`) })
        .find((candidate) => candidate.hasAttribute("data-topic-depth"));
      expect(button, `Loop ${id} must stay in the topic list`).toHaveAttribute("data-topic-depth", "0");
    }

    await user.click(screen.getByRole("button", { name: "Expand Loop loop-a" }));
    const child = screen
      .getAllByRole("button", { name: /Loop loop-c/ })
      .find((candidate) => candidate.hasAttribute("data-topic-depth"));
    expect(child, "a topic below a loop keeps its parent").toHaveAttribute("data-topic-depth", "1");
  });

  it("keeps extension feature guides within two User Guide levels", () => {
    const guide = (id: string, parentTopicId?: string) => ({
      extensionId: "com.example.deep",
      path: `docs/guides/${id}.json`,
      guide: {
        schemaVersion: 1,
        id,
        title: `Deep ${id}`,
        description: "Nested guide.",
        order: 1400,
        ...(parentTopicId ? { parentTopicId } : {}),
        sections: [{ id: "body", title: "Body", blocks: [{ type: "paragraph", text: "Body." }] }],
      },
    });

    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "deep-grandchild" }}
        extensionGuides={[
          guide("deep-root"),
          guide("deep-child", "deep-root"),
          guide("deep-grandchild", "deep-child"),
          guide("deep-great-grandchild", "deep-grandchild"),
        ]}
      />,
    );

    const topicButton = (name: string) =>
      screen
        .getAllByRole("button", { name: new RegExp(name) })
        .find((button) => button.hasAttribute("data-topic-depth"));

    expect(topicButton("Deep deep-root")).toHaveAttribute("data-topic-depth", "0");
    for (const id of ["deep-child", "deep-grandchild", "deep-great-grandchild"]) {
      expect(topicButton(`Deep ${id}`), `Deep ${id} is listed under the top-level guide`).toHaveAttribute(
        "data-topic-depth",
        "1",
      );
    }
    expect(screen.getByRole("heading", { level: 3, name: "Deep deep-grandchild" })).toBeInTheDocument();
  });

  it("lists an extension feature guide below a built-in child topic under that topic's parent", () => {
    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "media-types" }}
        extensionGuides={[
          {
            extensionId: "com.example.galleries",
            path: "docs/guides/galleries.json",
            guide: {
              schemaVersion: 1,
              id: "example-gallery-tips",
              title: "Example gallery tips",
              description: "Nested below a built-in child topic.",
              order: 1500,
              parentTopicId: "media-type-galleries",
              sections: [{ id: "body", title: "Body", blocks: [{ type: "paragraph", text: "Body." }] }],
            },
          },
        ]}
      />,
    );

    const button = screen
      .getAllByRole("button", { name: /Example gallery tips/ })
      .find((candidate) => candidate.hasAttribute("data-topic-depth"));
    expect(button).toHaveAttribute("data-topic-depth", "1");
  });

  it("labels the topics an extension adds with its name where they begin", async () => {
    const user = userEvent.setup();
    const guide = (extensionId: string, id: string, title: string, parentTopicId?: string) => ({
      extensionId,
      path: `docs/guides/${id}.json`,
      guide: {
        schemaVersion: 1,
        id,
        title,
        description: `${title} description.`,
        order: 1600,
        ...(parentTopicId ? { parentTopicId } : {}),
        sections: [{ id: "body", title: "Body", blocks: [{ type: "paragraph", text: "Body." }] }],
      },
    });

    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "badge-child" }}
        extensionNames={new Map([["com.example.badge", "Example Badge"]])}
        extensionGuides={[
          guide("com.example.badge", "badge-root", "Badge root"),
          guide("com.example.badge", "badge-child", "Badge child", "badge-root"),
          guide("com.example.badge", "badge-under-extensions", "Badge under extensions", "extensions"),
          guide("com.example.unnamed", "unnamed-root", "Unnamed root"),
        ]}
      />,
    );
    await user.click(screen.getByRole("button", { name: "Expand Discover and install extensions" }));

    const topicButton = (name: string) =>
      screen
        .getAllByRole("button", { name: new RegExp(name) })
        .find((button) => button.hasAttribute("data-topic-depth"))!;

    expect(within(topicButton("Badge root")).getByText("Example Badge")).toBeInTheDocument();
    expect(within(topicButton("Badge child")).queryByText("Example Badge")).not.toBeInTheDocument();
    expect(within(topicButton("Badge under extensions")).getByText("Example Badge")).toBeInTheDocument();
    expect(within(topicButton("Unnamed root")).getByText("Extension")).toBeInTheDocument();
    expect(within(topicButton("Discover and install extensions")).queryByText("Extension")).not.toBeInTheDocument();
  });

  it("does not list extension topics that reuse an earlier ID of a Cove topic", () => {
    const guide = (id: string, title: string) => ({
      extensionId: "com.example.alias",
      path: `docs/guides/${id}.json`,
      guide: {
        schemaVersion: 1,
        id,
        title,
        description: "Reuses an old topic ID.",
        order: 1700,
        sections: [{ id: "body", title: "Body", blocks: [{ type: "paragraph", text: "Body." }] }],
      },
    });

    render(
      <TutorialStoryboardDialog
        open
        onClose={vi.fn()}
        request={{ topicId: "search" }}
        extensionTopics={[
          {
            id: "tagging",
            title: "Extension tagging slides",
            extensionId: "com.example.alias",
            order: 1700,
            slides: [{ id: "one", title: "One" }],
          },
        ]}
        extensionGuides={[guide("search", "Extension search guide"), guide("alias-example", "Alias example")]}
      />,
    );

    expect(screen.getByRole("heading", { level: 3, name: "Search and filters" })).toBeInTheDocument();
    expect(screen.queryByText("Extension search guide")).not.toBeInTheDocument();
    expect(screen.queryByText("Extension tagging slides")).not.toBeInTheDocument();
    expect(
      screen
        .getAllByRole("button", { name: /Alias example/ })
        .some((button) => button.hasAttribute("data-topic-depth")),
    ).toBe(true);
  });

  it("renders repeated content in an extension guide without duplicate keys or element ids", () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      const { container } = render(
        <TutorialStoryboardDialog
          open
          onClose={vi.fn()}
          request={{ topicId: "repeat-guide" }}
          extensionGuides={[
            {
              extensionId: "com.example.repeat",
              path: "docs/guides/repeat.json",
              guide: {
                schemaVersion: 1,
                id: "repeat-guide",
                title: "Repeat guide",
                description: "Repeats content.",
                order: 1800,
                sections: [
                  {
                    id: "same",
                    title: "First",
                    blocks: [
                      { type: "list", items: ["Again", "Again"] },
                      { type: "steps", items: ["Again", "Again"] },
                      {
                        type: "links",
                        items: [
                          { label: "Docs", href: "/docs/user/" },
                          { label: "Docs", href: "https://example.com/" },
                        ],
                      },
                      { type: "table", columns: ["Same", "Same"], rows: [["1", "2"]] },
                    ],
                  },
                  { id: "same", title: "Second", blocks: [{ type: "paragraph", text: "Body." }] },
                ],
              },
            },
          ]}
        />,
      );

      const duplicateKeyWarnings = consoleError.mock.calls.filter((call) => String(call[0]).includes("same key"));
      expect(duplicateKeyWarnings).toEqual([]);
      const ids = [...container.ownerDocument.querySelectorAll("[id^='manual-guide-']")].map((element) => element.id);
      expect(new Set(ids).size).toBe(ids.length);
      expect(ids).toEqual(["manual-guide-same", "manual-guide-same-2"]);
    } finally {
      consoleError.mockRestore();
    }
  });
});
