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

  it("combines entity media and provenance under the provenance hover controller", () => {
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
    expect(screen.getByRole("img", { name: "Sourced tag" })).toHaveAttribute("src", "/tag.jpg");
    expect(overrideRendererCalls.at(-1)?.componentProps).toMatchObject({
      entityType: "tag",
      entityId: 17,
      surface: "hover",
    });
  });

  it("renders extension-only media in a provenance popup without a native image", () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    overrideRenderState.active = true;
    overrideRenderState.replace = true;
    render(
      <TagBadge
        name="Extension-only sourced tag"
        tag={{ id: 18, name: "Extension-only sourced tag" }}
        provenance={[{ sourceKey: "ext:tagger", appliedAt: "2026-07-19T00:00:00Z" }]}
        onClick={() => {}}
      />,
    );

    fireEvent.focus(screen.getByRole("button", { name: "Extension-only sourced tag" }));

    expect(screen.getByTestId("extension-media")).toBeInTheDocument();
    expect(screen.getAllByText("Tag Sources").some((element) => !element.closest(".sr-only"))).toBe(true);
    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
    expect(consoleError).not.toHaveBeenCalled();
    consoleError.mockRestore();
  });

  it("degrades a failed native image to provenance-only content", () => {
    render(
      <TagBadge
        name="Broken sourced tag"
        tag={{ id: 19, name: "Broken sourced tag", imagePath: "/missing-tag.jpg" }}
        provenance={[{ sourceKey: "ext:tagger", appliedAt: "2026-07-19T00:00:00Z" }]}
        onClick={() => {}}
      />,
    );

    fireEvent.focus(screen.getByRole("button", { name: "Broken sourced tag" }));
    fireEvent.error(screen.getByRole("img", { name: "Broken sourced tag" }));

    expect(screen.queryByRole("img", { name: "Broken sourced tag" })).not.toBeInTheDocument();
    expect(screen.getAllByText("Tag Sources").some((element) => !element.closest(".sr-only"))).toBe(true);
  });

  it("collapses the preview frame when an active override delegates after native image failure", () => {
    overrideRenderState.active = true;
    overrideRenderState.replace = false;
    render(
      <TagBadge
        name="Delegated broken tag"
        tag={{ id: 22, name: "Delegated broken tag", imagePath: "/delegated-missing.jpg" }}
        provenance={[{ sourceKey: "ext:tagger", appliedAt: "2026-07-19T00:00:00Z" }]}
        onClick={() => {}}
      />,
    );

    fireEvent.focus(screen.getByRole("button", { name: "Delegated broken tag" }));
    const nativeImage = screen.getByRole("img", { name: "Delegated broken tag" });
    const previewFrame = nativeImage.parentElement!;

    fireEvent.error(nativeImage);

    expect(previewFrame).toHaveClass("empty:hidden");
    expect(previewFrame).toBeEmptyDOMElement();
    expect(screen.queryByRole("img", { name: "Delegated broken tag" })).not.toBeInTheDocument();
    expect(screen.getAllByText("Tag Sources").some((element) => !element.closest(".sr-only"))).toBe(true);
  });

  it("keeps provenance-only behavior when no tag id is available", () => {
    overrideRenderState.active = true;
    overrideRenderState.replace = true;
    render(
      <TagBadge
        name="Unidentified sourced tag"
        provenance={[{ sourceKey: "ext:tagger", appliedAt: "2026-07-19T00:00:00Z" }]}
        onClick={() => {}}
      />,
    );

    fireEvent.focus(screen.getByRole("button", { name: "Unidentified sourced tag" }));

    expect(screen.getAllByText("Tag Sources").some((element) => !element.closest(".sr-only"))).toBe(true);
    expect(screen.queryByTestId("extension-media")).not.toBeInTheDocument();
    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
    expect(overrideRendererCalls).toHaveLength(0);
  });

  it("keeps the correction menu clickable from a combined sourced tag badge", () => {
    const onAdjustThreshold = vi.fn();
    render(
      <TagBadge
        name="Reportable sourced tag"
        tag={{ id: 20, name: "Reportable sourced tag", imagePath: "/tag.jpg" }}
        provenance={[{ sourceKey: "ext:tagger", appliedAt: "2026-07-19T00:00:00Z" }]}
        reportable
        onAdjustThreshold={onAdjustThreshold}
      />,
    );

    const menuTrigger = screen.getByRole("button", { name: "More actions for Reportable sourced tag" });
    fireEvent.focus(menuTrigger);
    expect(screen.getAllByText("Tag Sources").some((element) => !element.closest(".sr-only"))).toBe(true);

    // Keyboard activation dispatches click without mousedown. Capture must dismiss provenance before
    // the trigger's stopPropagation handler opens its own menu.
    fireEvent.click(menuTrigger);
    expect(screen.getAllByText("Tag Sources").every((element) => Boolean(element.closest(".sr-only")))).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: /Adjust when this tag appears/i }));

    expect(onAdjustThreshold).toHaveBeenCalledTimes(1);
    expect(screen.getAllByText("Tag Sources").every((element) => Boolean(element.closest(".sr-only")))).toBe(true);
  });
});
