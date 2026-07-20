import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

interface OverrideRendererCall {
  targetComponent: string;
  componentProps: Record<string, unknown>;
  renderDefault: () => ReactNode;
}

const { overrideRendererCalls, overrideRenderState } = vi.hoisted(() => ({
  overrideRendererCalls: [] as OverrideRendererCall[],
  overrideRenderState: { replace: false },
}));

vi.mock("../extensions/ExtensionLoader", () => ({
  ExtensionComponentOverrideRenderer: (props: OverrideRendererCall) => {
    overrideRendererCalls.push(props);
    return overrideRenderState.replace
      ? <div data-testid="tag-media-override">Animated tag media</div>
      : props.renderDefault();
  },
}));

vi.mock("../components/Rating", () => ({
  RatingBanner: () => null,
  RatingBadge: () => null,
}));

import { TagTile } from "../components/EntityCards";

const baseTag = {
  id: 17,
  name: "Animated Tag",
  description: "A tag with alternate media",
  imagePath: "/tag-preview.jpg",
  favorite: false,
  organized: false,
  aliases: [],
};

describe("TagTile entity media integration", () => {
  beforeEach(() => {
    overrideRendererCalls.length = 0;
    overrideRenderState.replace = false;
  });

  it("passes card context to entity.media while preserving host navigation and selection", () => {
    overrideRenderState.replace = true;
    const onSelect = vi.fn();
    const { container } = render(
      <TagTile
        tag={baseTag as any}
        onClick={vi.fn()}
        selected
        onSelect={onSelect}
      />,
    );

    expect(overrideRendererCalls).toHaveLength(1);
    expect(overrideRendererCalls[0]?.targetComponent).toBe("entity.media");
    expect(overrideRendererCalls[0]?.componentProps).toEqual({
      entityType: "tag",
      entityId: 17,
      surface: "card",
      imageUrl: "/tag-preview.jpg",
      alt: "Animated Tag",
      fit: "cover",
      loading: "lazy",
      className: "h-full w-full",
    });

    const replacement = screen.getByTestId("tag-media-override");
    expect(replacement.closest(".card-media")).toBe(container.querySelector(".card-media"));
    expect(screen.queryByRole("img", { name: "Animated Tag" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open tag Animated Tag" })).toHaveAttribute("href", "/tag/17");
    expect(screen.getByRole("button", { name: "Deselect item" })).toBeInTheDocument();
  });

  it("keeps the native tag fallback available when an override delegates", () => {
    const { container } = render(
      <TagTile
        tag={{ ...baseTag, imagePath: undefined } as any}
        onClick={vi.fn()}
      />,
    );

    expect(overrideRendererCalls).toHaveLength(1);
    expect(overrideRendererCalls[0]?.componentProps).toMatchObject({
      entityType: "tag",
      entityId: 17,
    });
    expect(overrideRendererCalls[0]?.componentProps.imageUrl == null).toBe(true);
    expect(screen.queryByRole("img", { name: "Animated Tag" })).not.toBeInTheDocument();
    expect(container.querySelector(".card-media svg")).toBeInTheDocument();
  });
});
