import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { EntityDetailTabs } from "../components/EntityDetailTabs";

describe("EntityDetailTabs", () => {
  it("left aligns the shared entity tab list within its container", () => {
    render(
      <EntityDetailTabs
        tabs={[
          { key: "images", label: "Images", count: 5 },
          { key: "videos", label: "Videos", count: 1 },
          { key: "fileinfo", label: "File Info" },
        ]}
        activeTab="images"
        onTabChange={vi.fn()}
      />,
    );

    expect(screen.getByRole("tablist", { name: /detail tabs/i })).not.toHaveClass("mx-auto");
  });

  it("uses the main navigation icons for matching entity tabs", () => {
    render(
      <EntityDetailTabs
        tabs={[
          { key: "audios", label: "Audios", count: 2 },
          { key: "videos", label: "Videos", count: 5 },
          { key: "fileinfo", label: "File Info" },
        ]}
        activeTab="audios"
        onTabChange={vi.fn()}
      />,
    );

    expect(screen.getByRole("tab", { name: "Audios" }).querySelector(".lucide-headphones")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Videos" }).querySelector(".lucide-film")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "File Info" }).querySelector("svg")).not.toBeInTheDocument();
  });
});
