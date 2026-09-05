import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MediaDetailLayout } from "../components/MediaDetailLayout/MediaDetailLayout";
import { getActiveManualContexts } from "../components/ManualContext";

afterEach(() => {
  cleanup();
  document.documentElement.style.removeProperty("--cove-media-detail-desktop-tab-nav");
  document.documentElement.style.removeProperty("--cove-media-detail-sidebar-collapsible");
  if (typeof window.localStorage?.removeItem === "function") {
    window.localStorage.removeItem("cove.detailSidebarCollapsed");
  }
});

describe("MediaDetailLayout", () => {
  it("renders media, tabs, and content", () => {
    render(
      <MediaDetailLayout
        title="Video Title"
        media={<div>Player Surface</div>}
        tabs={[
          { key: "details", label: "Details" },
          { key: "segments", label: "Segments", count: 3 },
        ]}
        activeTab="details"
      >
        <MediaDetailLayout.Content>
          <div>Body content</div>
        </MediaDetailLayout.Content>
      </MediaDetailLayout>,
    );

    expect(screen.getByRole("heading", { name: "Video Title" })).toBeInTheDocument();
    expect(screen.getByText("Player Surface")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /details/i })).toBeInTheDocument();
    expect(screen.getByText("Body content")).toBeInTheDocument();
  });

  it("fires onTabChange when a tab is clicked", () => {
    const onTabChange = vi.fn();

    render(
      <MediaDetailLayout
        title="Video Title"
        tabs={[
          { key: "details", label: "Details" },
          { key: "segments", label: "Segments" },
        ]}
        activeTab="details"
        onTabChange={onTabChange}
      >
        <MediaDetailLayout.Content>
          <div>Body content</div>
        </MediaDetailLayout.Content>
      </MediaDetailLayout>,
    );

    fireEvent.click(screen.getByRole("tab", { name: /segments/i }));
    expect(onTabChange).toHaveBeenCalledWith("segments");
  });

  it("collapses the sidebar independently from the icon rail", () => {
    const onTabChange = vi.fn();

    render(
      <MediaDetailLayout
        title="Video Title"
        media={<div>Player Surface</div>}
        tabs={[
          { key: "details", label: "Details" },
          { key: "segments", label: "Segments" },
        ]}
        activeTab="details"
        onTabChange={onTabChange}
      >
        <MediaDetailLayout.Content>
          <div>Body content</div>
        </MediaDetailLayout.Content>
      </MediaDetailLayout>,
    );

    expect(screen.queryByRole("button", { name: /expand tab rail labels/i })).not.toBeInTheDocument();

    const sidebar = screen.getByTestId("media-detail-layout-sidebar");
    const collapseButton = screen.getByRole("button", { name: /collapse details sidebar/i });

    fireEvent.click(collapseButton);
    expect(sidebar).toHaveAttribute("data-sidebar-collapsed", "true");

    fireEvent.click(screen.getByRole("tab", { name: /segments/i }));
    expect(onTabChange).toHaveBeenCalledWith("segments");
    expect(sidebar).toHaveAttribute("data-sidebar-collapsed", "false");
  });

  it("lets extension CSS choose the desktop tab presentation", () => {
    document.documentElement.style.setProperty("--cove-media-detail-desktop-tab-nav", "row");
    document.documentElement.style.setProperty("--cove-media-detail-sidebar-collapsible", "false");

    render(
      <MediaDetailLayout
        title="Video Title"
        media={<div>Player Surface</div>}
        tabs={[
          { key: "details", label: "Details" },
          { key: "segments", label: "Segments" },
        ]}
        activeTab="details"
      >
        <MediaDetailLayout.Content>
          <div>Body content</div>
        </MediaDetailLayout.Content>
      </MediaDetailLayout>,
    );

    const tablist = screen.getByRole("tablist", { name: /detail tabs/i });

    expect(tablist.closest(".media-detail-layout-tabs-row")).not.toBeNull();
    expect(screen.queryByTestId("media-detail-layout-sidebar-toggle")).not.toBeInTheDocument();
    expect(screen.getByText("Body content")).toBeInTheDocument();
  });

  it("publishes the active detail tab as manual context", async () => {
    render(
      <MediaDetailLayout
        title="Video Title"
        tabs={[
          { key: "details", label: "Details" },
          { key: "related", label: "Related", manualContexts: ["panel:related-media", "feature:example.detail"] },
        ]}
        activeTab="related"
      >
        <MediaDetailLayout.Content>
          <div>Body content</div>
        </MediaDetailLayout.Content>
      </MediaDetailLayout>,
    );

    await waitFor(() => {
      expect(getActiveManualContexts()).toEqual(
        expect.arrayContaining(["detail-tab:related", "tab:related", "panel:related-media", "feature:example.detail"]),
      );
    });
  });

  it("uses tab semantics and arrow-key navigation for tabs", () => {
    const onTabChange = vi.fn();

    render(
      <MediaDetailLayout
        title="Video Title"
        tabs={[
          { key: "details", label: "Details" },
          { key: "segments", label: "Segments" },
          { key: "related", label: "Related" },
        ]}
        activeTab="details"
        onTabChange={onTabChange}
      >
        <MediaDetailLayout.Content>
          <div>Body content</div>
        </MediaDetailLayout.Content>
      </MediaDetailLayout>,
    );

    const detailsTab = screen.getByRole("tab", { name: /details/i });
    const segmentsTab = screen.getByRole("tab", { name: /segments/i });

    expect(detailsTab).toHaveAttribute("aria-selected", "true");
    expect(detailsTab).toHaveAttribute("tabindex", "0");
    expect(segmentsTab).toHaveAttribute("aria-selected", "false");
    expect(segmentsTab).toHaveAttribute("tabindex", "-1");

    detailsTab.focus();
    fireEvent.keyDown(detailsTab, { key: "ArrowRight" });

    expect(onTabChange).toHaveBeenCalledWith("segments");
    expect(segmentsTab).toHaveFocus();
  });

  it("fires keyboard shortcut handlers", () => {
    const onShortcut = vi.fn();

    render(
      <MediaDetailLayout
        title="Video Title"
        keyboardShortcuts={[{ key: "s", description: "Open segments", handler: onShortcut }]}
      >
        <MediaDetailLayout.Content>
          <div>Body content</div>
        </MediaDetailLayout.Content>
      </MediaDetailLayout>,
    );

    fireEvent.keyDown(window, { key: "s" });
    expect(onShortcut).toHaveBeenCalledTimes(1);
  });

  it("adds the sticky class when mediaSticky is enabled", () => {
    render(
      <MediaDetailLayout title="Video Title" media={<div>Player Surface</div>} mediaSticky>
        <MediaDetailLayout.Content>
          <div>Body content</div>
        </MediaDetailLayout.Content>
      </MediaDetailLayout>,
    );

    expect(screen.getByTestId("media-detail-layout-media")).toHaveClass("xl:sticky");
  });

  it("frames video media with the shared bounded aspect container", () => {
    render(
      <MediaDetailLayout title="Segment Title" media={<div>Player Surface</div>} mediaAspectRatio="video">
        <MediaDetailLayout.Content>
          <div>Body content</div>
        </MediaDetailLayout.Content>
      </MediaDetailLayout>,
    );

    expect(screen.getByTestId("media-detail-layout-media")).toHaveClass("items-center");
    expect(screen.getByTestId("media-detail-layout-media-frame")).toHaveClass("aspect-video");
  });
});
