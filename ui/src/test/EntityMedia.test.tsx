import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

interface OverrideRendererCall {
  targetComponent: string;
  componentProps: Record<string, unknown>;
  renderDefault: () => ReactNode;
}

const { overrideRendererCalls, overrideRenderState } = vi.hoisted(() => ({
  overrideRendererCalls: [] as OverrideRendererCall[],
  overrideRenderState: { replace: false, active: false, aspectRatio: null as string | null },
}));

vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({
    getComponentOverrides: () => overrideRenderState.active ? [{ targetComponent: "entity.media" }] : [],
  }),
  ExtensionComponentOverrideRenderer: (props: OverrideRendererCall) => {
    overrideRendererCalls.push(props);
    return overrideRenderState.replace
      ? <div data-testid="extension-media" data-entity-media-aspect-ratio={overrideRenderState.aspectRatio ?? undefined}>Extension media</div>
      : props.renderDefault();
  },
}));

import { EntityMedia, EntityMediaHover, TagMediaHover } from "../components/EntityMedia";
import { TagBadge } from "../components/shared";

describe("EntityMedia", () => {
  beforeEach(() => {
    overrideRendererCalls.length = 0;
    overrideRenderState.replace = false;
    overrideRenderState.active = false;
    overrideRenderState.aspectRatio = null;
  });

  it("routes the stable entity media contract through the entity.media override target", () => {
    const renderDefault = vi.fn(() => <img src="/native-tag.jpg" alt="Native tag" />);

    render(
      <EntityMedia
        entityType="tag"
        entityId={17}
        surface="card"
        imageUrl="/tag.jpg"
        alt="Animated tag"
        fit="contain"
        loading="lazy"
        className="h-full w-full"
        renderDefault={renderDefault}
      />,
    );

    expect(overrideRendererCalls).toHaveLength(1);
    expect(overrideRendererCalls[0]?.targetComponent).toBe("entity.media");
    expect(overrideRendererCalls[0]?.componentProps).toEqual({
      entityType: "tag",
      entityId: 17,
      surface: "card",
      imageUrl: "/tag.jpg",
      alt: "Animated tag",
      fit: "contain",
      loading: "lazy",
      className: "h-full w-full",
    });
    expect(renderDefault).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("img", { name: "Native tag" })).toHaveAttribute("src", "/native-tag.jpg");
  });

  it("lets an override replace the native renderer without evaluating it", () => {
    overrideRenderState.replace = true;
    const renderDefault = vi.fn(() => <div>Native media</div>);

    render(
      <EntityMedia
        entityType="tag"
        entityId={17}
        surface="card"
        imageUrl={null}
        alt="Animated tag"
        fit="cover"
        renderDefault={renderDefault}
      />,
    );

    expect(screen.getByTestId("extension-media")).toBeInTheDocument();
    expect(renderDefault).not.toHaveBeenCalled();
    expect(screen.queryByText("Native media")).not.toBeInTheDocument();
  });

  it("renders supplied static hover media without an extension contribution", () => {
    render(
      <EntityMediaHover
        entityType="tag"
        entityId={17}
        imageUrl="/tag.jpg"
        alt="Static tag"
        fit="contain"
        loading="lazy"
      >
        <button type="button">Tag reference</button>
      </EntityMediaHover>,
    );

    fireEvent.mouseEnter(screen.getByRole("button", { name: "Tag reference" }));

    expect(screen.getByRole("tooltip", { name: "Media for Static tag" })).toContainElement(screen.getByRole("img", { name: "Static tag" }));
    expect(screen.getByRole("img", { name: "Static tag" })).toHaveAttribute("src", "/tag.jpg");
    expect(screen.getByRole("img", { name: "Static tag" })).toHaveAttribute("loading", "lazy");
    expect(screen.getByRole("img", { name: "Static tag" })).toHaveClass("object-contain");
  });

  it("resolves hasImage tag references through the shared hover boundary", () => {
    render(
      <TagMediaHover tag={{ id: 21, name: "API-backed tag", hasImage: true }}>
        <button type="button">Tag reference</button>
      </TagMediaHover>,
    );

    fireEvent.mouseEnter(screen.getByRole("button", { name: "Tag reference" }));

    expect(screen.getByRole("img", { name: "API-backed tag" })).toHaveAttribute("src", "/api/tags/21/image?max=640");
  });

  it("leaves a reference unchanged when neither core nor an extension has hover media", () => {
    render(
      <EntityMediaHover entityType="tag" entityId={17} imageUrl={null} alt="Plain tag" fit="cover">
        <button type="button">Tag reference</button>
      </EntityMediaHover>,
    );

    fireEvent.mouseEnter(screen.getByRole("button", { name: "Tag reference" }));

    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
    expect(overrideRendererCalls).toHaveLength(0);
  });

  it("lets an active contribution replace the core static hover media", () => {
    overrideRenderState.active = true;
    overrideRenderState.replace = true;

    render(
      <EntityMediaHover
        entityType="tag"
        entityId={17}
        imageUrl="/tag.jpg"
        alt="Animated tag"
        fit="cover"
        loading="lazy"
      >
        <button type="button">Tag reference</button>
      </EntityMediaHover>,
    );

    expect(overrideRendererCalls).toHaveLength(0);
    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();

    fireEvent.mouseEnter(screen.getByRole("button", { name: "Tag reference" }));

    expect(screen.getByRole("tooltip", { name: "Media for Animated tag" })).toContainElement(screen.getByTestId("extension-media"));
    expect(overrideRendererCalls.length).toBeGreaterThan(0);
    expect(overrideRendererCalls.at(-1)?.componentProps).toMatchObject({
      entityType: "tag",
      entityId: 17,
      surface: "hover",
      imageUrl: "/tag.jpg",
      alt: "Animated tag",
      fit: "cover",
      loading: "lazy",
      className: "h-full w-full",
    });
  });

  it("sizes extension hover media from its declared aspect ratio", async () => {
    overrideRenderState.active = true;
    overrideRenderState.replace = true;
    overrideRenderState.aspectRatio = "1:1";

    render(
      <EntityMediaHover entityType="tag" entityId={17} imageUrl={null} alt="Square preview" fit="cover">
        <button type="button">Tag reference</button>
      </EntityMediaHover>,
    );

    fireEvent.mouseEnter(screen.getByRole("button", { name: "Tag reference" }));

    await waitFor(() => {
      expect(screen.getByRole("tooltip", { name: "Media for Square preview" })).toHaveStyle({ aspectRatio: "1 / 1" });
    });
  });

  it("uses the core static image when an active contribution falls through", () => {
    overrideRenderState.active = true;

    render(
      <EntityMediaHover entityType="tag" entityId={17} imageUrl="/tag.jpg" alt="Static fallback" fit="cover">
        <button type="button">Tag reference</button>
      </EntityMediaHover>,
    );

    fireEvent.mouseEnter(screen.getByRole("button", { name: "Tag reference" }));

    expect(screen.getByRole("img", { name: "Static fallback" })).toHaveAttribute("src", "/tag.jpg");
    expect(screen.queryByTestId("extension-media")).not.toBeInTheDocument();
  });

  it("removes the tooltip after the core static image fails", () => {
    render(
      <EntityMediaHover entityType="tag" entityId={17} imageUrl="/missing.jpg" alt="Missing tag" fit="cover">
        <button type="button">Tag reference</button>
      </EntityMediaHover>,
    );

    fireEvent.mouseEnter(screen.getByRole("button", { name: "Tag reference" }));
    fireEvent.error(screen.getByRole("img", { name: "Missing tag" }));

    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Tag reference" })).toBeInTheDocument();
  });

  it("keeps provenance as the sole hover surface for a sourced tag badge", () => {
    overrideRenderState.active = true;
    overrideRenderState.replace = true;
    render(
      <TagBadge
        name="Sourced tag"
        tag={{ id: 17, name: "Sourced tag", imagePath: "/tag.jpg" }}
        provenance={[{ sourceKey: "ext:tagger", appliedAt: "2026-07-19T00:00:00Z" }]}
        onClick={() => {}}
      />,
    );

    fireEvent.focus(screen.getByRole("button", { name: "Sourced tag" }));

    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
    expect(screen.getAllByText("Tag Sources").some((element) => !element.closest(".sr-only"))).toBe(true);
    expect(overrideRendererCalls).toHaveLength(0);
  });
});
