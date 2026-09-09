import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { MetadataServerLinks, metadataServerEntityUrl } from "../components/MetadataServerLinks";

describe("MetadataServerLinks", () => {
  it("builds stash-box entity URLs from GraphQL endpoints", () => {
    expect(metadataServerEntityUrl("https://stashdb.org/graphql", "scenes", "scene-1")).toBe(
      "https://stashdb.org/scenes/scene-1",
    );
    expect(metadataServerEntityUrl("https://example.test/api/graphql/", "performers", "performer-1")).toBe(
      "https://example.test/api/performers/performer-1",
    );
    expect(metadataServerEntityUrl("https://stashdb.org/graphql", "studios", "studio-1")).toBe(
      "https://stashdb.org/studios/studio-1",
    );
  });

  it("strips endpoint query data and rejects unsafe or unsupported endpoints", () => {
    expect(metadataServerEntityUrl("https://example.test/graphql?token=secret#fragment", "tags", "tag-1")).toBe(
      "https://example.test/tags/tag-1",
    );
    expect(metadataServerEntityUrl("javascript:alert(1)", "tags", "tag-1")).toBeNull();
    expect(metadataServerEntityUrl("https://example.test/api", "tags", "tag-1")).toBeNull();
    expect(metadataServerEntityUrl("not a URL", "tags", "tag-1")).toBeNull();
  });

  it("renders one accessible external link for every remote ID", () => {
    render(
      <MetadataServerLinks
        entityType="tags"
        metadataServers={[
          { endpoint: "https://stashdb.org/graphql", name: "StashDB" },
          { endpoint: "https://theporndb.net/graphql", name: "ThePornDB" },
        ]}
        remoteIds={[
          { endpoint: "https://stashdb.org/graphql", remoteId: "tag-1" },
          { endpoint: "https://theporndb.net/graphql", remoteId: "tag-2" },
        ]}
      />,
    );

    expect(screen.getByRole("link", { name: "Open StashDB metadata page" })).toHaveAttribute(
      "href",
      "https://stashdb.org/tags/tag-1",
    );
    expect(screen.getByRole("link", { name: "Open ThePornDB metadata page" })).toHaveAttribute(
      "href",
      "https://theporndb.net/tags/tag-2",
    );
    expect(screen.getByText("StashDB")).toBeVisible();
  });

  it("renders no layout wrapper when there are no usable remote IDs", () => {
    const { container } = render(
      <MetadataServerLinks
        className="mb-3"
        entityType="tags"
        remoteIds={[{ endpoint: "not a URL", remoteId: "tag-1" }]}
      />,
    );

    expect(container).toBeEmptyDOMElement();
  });
});
