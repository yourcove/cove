import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
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

  it("shows audio and text usage in the card footer", () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ items: [], totalCount: 0, page: 1, perPage: 10 }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <TagTile
          tag={{ ...baseTag, audioCount: 3, textCount: 2 } as any}
          onClick={vi.fn()}
        />
      </QueryClientProvider>,
    );

    expect(screen.getByTitle("Audios")).toHaveTextContent("3");
    expect(screen.getByTitle("Texts")).toHaveTextContent("2");

    fireEvent.mouseEnter(screen.getByTitle("Audios"));

    return waitFor(() => {
      const [url, options] = fetchMock.mock.calls[0] as [string, RequestInit];
      expect(url).toBe("/api/audios/find");
      expect(options.method).toBe("POST");
      expect(JSON.parse(String(options.body))).toMatchObject({
        objectFilter: { tagsCriterion: { modifier: "includes", value: [17] } },
      });
    });
  });
});
