import { fireEvent, render, screen } from "@testing-library/react";
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
  overrideRenderState: { replace: true },
}));

vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({
    getComponentOverrides: () => [{ targetComponent: "entity.media" }],
  }),
  ExtensionComponentOverrideRenderer: (props: OverrideRendererCall) => {
    overrideRendererCalls.push(props);
    return overrideRenderState.replace ? (
      <div data-testid="entity-media-override">Extension media</div>
    ) : (
      props.renderDefault()
    );
  },
}));

vi.mock("../components/Rating", () => ({
  RatingBanner: () => null,
  RatingBadge: () => null,
}));

import {
  AudioTile,
  FaceTile,
  GalleryTile,
  GroupTile,
  ImageTile,
  PerformerTile,
  StudioTile,
  TextTile,
  VideoCard,
  VideoTile,
} from "../components/EntityCards";
import { RelatedEntityListRow, RelatedEntityListView } from "../components/RelatedEntityListView";

const video = {
  id: 42,
  title: "Sample Video",
  imagePath: "/video-source.jpg",
  updatedAt: "2026-07-11T00:00:00Z",
  files: [{ id: 1, basename: "sample.mp4", size: 1000, duration: 120, width: 1920, height: 1080 }],
  performers: [],
  groups: [],
  galleries: [],
  tags: [],
  organized: false,
  urls: [],
  remoteIds: [],
  createdAt: "2026-07-11T00:00:00Z",
};

const studio = {
  id: 9,
  name: "Sample Studio",
  imagePath: "/studio.png",
  favorite: false,
  organized: false,
  urls: [],
  aliases: [],
  tags: [],
  remoteIds: [],
  videoCount: 0,
  imageCount: 0,
  galleryCount: 0,
  groupCount: 0,
  performerCount: 0,
  childStudioCount: 0,
  audioCount: 0,
  textCount: 0,
  createdAt: "2026-07-11T00:00:00Z",
  updatedAt: "2026-07-11T00:00:00Z",
};

const gallery = {
  id: 7,
  title: "Sample Gallery",
  coverPath: "/gallery.jpg",
  organized: false,
  urls: [],
  tags: [],
  performers: [],
  imageCount: 0,
  videoCount: 0,
  videoIds: [],
  files: [],
  createdAt: "2026-07-11T00:00:00Z",
  updatedAt: "2026-07-11T00:00:00Z",
};

const group = {
  id: 6,
  name: "Sample Group",
  frontImagePath: "/group.jpg",
  urls: [],
  tags: [],
  videoCount: 0,
  subGroupCount: 0,
  containingGroupCount: 0,
  createdAt: "2026-07-11T00:00:00Z",
  updatedAt: "2026-07-11T00:00:00Z",
};

const audio = {
  id: 5,
  title: "Sample Audio",
  imagePath: "/audio.jpg",
  organized: false,
  urls: [],
  tags: [],
  performers: [],
  tracks: [],
  files: [],
  groups: [],
  createdAt: "2026-07-11T00:00:00Z",
  updatedAt: "2026-07-11T00:00:00Z",
  fileCount: 0,
  maxDuration: 90,
  hasVideoFiles: false,
};

const text = {
  id: 4,
  title: "Sample Text",
  imagePath: "/text.jpg",
  organized: false,
  urls: [],
  tags: [],
  performers: [],
  files: [],
  groups: [],
  createdAt: "2026-07-11T00:00:00Z",
  updatedAt: "2026-07-11T00:00:00Z",
  fileCount: 0,
};

beforeEach(() => {
  overrideRendererCalls.length = 0;
  overrideRenderState.replace = true;
  vi.stubGlobal(
    "IntersectionObserver",
    class {
      observe() {}
      disconnect() {}
      unobserve() {}
    },
  );
});

describe("entity card media contexts", () => {
  const cases = [
    {
      name: "VideoCard",
      renderCard: () => <VideoCard video={video as any} onClick={vi.fn()} />,
      expected: {
        entityType: "video",
        entityId: 42,
        alt: "Sample Video",
        fit: "cover",
        loading: "lazy",
        className: "video-card-preview-image h-full w-full",
      },
    },
    {
      name: "VideoTile",
      renderCard: () => <VideoTile video={video as any} onClick={vi.fn()} />,
      expected: {
        entityType: "video",
        entityId: 42,
        alt: "Sample Video",
        fit: "cover",
        loading: "lazy",
        className: "h-full w-full object-cover",
      },
    },
    {
      name: "PerformerTile",
      renderCard: () => (
        <PerformerTile
          performer={{ id: 8, name: "Sample Performer", imagePath: "/performer.jpg", tags: [] }}
          onClick={vi.fn()}
        />
      ),
      expected: {
        entityType: "performer",
        entityId: 8,
        imageUrl: "/performer.jpg",
        alt: "Sample Performer",
        fit: "cover",
        loading: "lazy",
        className: "h-full w-full",
      },
    },
    {
      name: "StudioTile",
      renderCard: () => <StudioTile studio={studio as any} onClick={vi.fn()} />,
      expected: {
        entityType: "studio",
        entityId: 9,
        imageUrl: "/studio.png",
        alt: "Sample Studio",
        fit: "contain",
        loading: "lazy",
        className: "box-border h-full w-full p-4",
      },
    },
    {
      name: "ImageTile",
      renderCard: () => (
        <ImageTile
          image={
            {
              id: 3,
              title: "Sample Image",
              organized: false,
              urls: [],
              tags: [],
              performers: [],
              galleryCount: 0,
              galleryIds: [],
              galleries: [],
              files: [],
              createdAt: "",
              updatedAt: "",
            } as any
          }
          onClick={vi.fn()}
        />
      ),
      expected: {
        entityType: "image",
        entityId: 3,
        alt: "Sample Image",
        fit: "cover",
        loading: "lazy",
        className: "h-full w-full",
      },
    },
    {
      name: "GalleryTile",
      renderCard: () => <GalleryTile gallery={gallery as any} onClick={vi.fn()} />,
      expected: {
        entityType: "gallery",
        entityId: 7,
        imageUrl: "/gallery.jpg",
        alt: "Sample Gallery",
        fit: "cover",
        loading: "lazy",
        className: "h-full w-full",
      },
    },
    {
      name: "GroupTile",
      renderCard: () => <GroupTile group={group as any} onClick={vi.fn()} />,
      expected: {
        entityType: "group",
        entityId: 6,
        imageUrl: "/group.jpg",
        alt: "Sample Group",
        fit: "cover",
        loading: "lazy",
        className: "h-full w-full",
      },
    },
    {
      name: "AudioTile",
      renderCard: () => <AudioTile audio={audio as any} onClick={vi.fn()} />,
      expected: {
        entityType: "audio",
        entityId: 5,
        imageUrl: "/audio.jpg",
        alt: "Sample Audio",
        fit: "cover",
        loading: "lazy",
        className: "h-full w-full",
      },
    },
    {
      name: "TextTile",
      renderCard: () => <TextTile text={text as any} onClick={vi.fn()} />,
      expected: {
        entityType: "text",
        entityId: 4,
        imageUrl: "/text.jpg",
        alt: "Sample Text",
        fit: "cover",
        loading: "lazy",
        className: "h-full w-full",
      },
    },
    {
      name: "FaceTile",
      renderCard: () => (
        <FaceTile
          face={
            {
              id: 2,
              label: "Sample Face",
              coverImageUrl: "/face.jpg",
              ignored: false,
              detectionCount: 1,
              videoCount: 0,
              imageCount: 0,
              createdAt: "2026-07-11T00:00:00Z",
              updatedAt: "2026-07-11T00:00:00Z",
              appearanceCount: 1,
              frameSampleCount: 1,
            } as any
          }
          onClick={vi.fn()}
        />
      ),
      expected: {
        entityType: "face",
        entityId: 2,
        imageUrl: "/face.jpg",
        alt: "Sample Face",
        fit: "cover",
        loading: "lazy",
        className: "h-full w-full object-cover transition-transform duration-300 group-hover:scale-[1.02]",
      },
    },
  ];

  it.each(cases)("passes the $name primary visual through entity.media", ({ renderCard, expected }) => {
    render(renderCard());

    expect(overrideRendererCalls).toHaveLength(1);
    expect(overrideRendererCalls[0]?.targetComponent).toBe("entity.media");
    expect(overrideRendererCalls[0]?.componentProps).toMatchObject({ surface: "card", ...expected });
    expect(overrideRendererCalls[0]?.componentProps.imageUrl).toEqual(expect.any(String));
  });
});

describe("studio usage counts", () => {
  it("shows audio and text usage in the card footer", () => {
    render(<StudioTile studio={{ ...studio, audioCount: 4, textCount: 2 } as any} onClick={vi.fn()} />);

    expect(screen.getByTitle("Audios")).toHaveTextContent("4");
    expect(screen.getByTitle("Texts")).toHaveTextContent("2");
  });
});

describe("entity card host boundaries", () => {
  it("replaces both VideoCard visuals while preserving navigation, selection, and overlays", () => {
    const { container } = render(<VideoCard video={video as any} onClick={vi.fn()} selected onSelect={vi.fn()} />);

    expect(screen.getByTestId("entity-media-override").closest(".card-media")).toBe(
      container.querySelector(".card-media"),
    );
    expect(container.querySelector(".card-media img")).not.toBeInTheDocument();
    expect(container.querySelector(".card-media video")).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open video Sample Video" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Deselect item" })).toBeInTheDocument();
    expect(screen.getByText("2:00")).toBeInTheDocument();
  });

  it("initializes native VideoCard observation when delegation starts after replacement", () => {
    const observe = vi.fn();
    const disconnect = vi.fn();
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        observe = observe;
        disconnect = disconnect;
        unobserve() {}
      },
    );
    const view = render(<VideoCard video={video as any} onClick={vi.fn()} />);

    expect(observe).not.toHaveBeenCalled();

    overrideRenderState.replace = false;
    view.rerender(<VideoCard video={video as any} onClick={vi.fn()} />);

    expect(observe).toHaveBeenCalledTimes(1);
    expect(observe.mock.calls[0]?.[0]).toBeInstanceOf(HTMLVideoElement);

    overrideRenderState.replace = true;
    view.rerender(<VideoCard video={video as any} onClick={vi.fn()} />);

    expect(disconnect).toHaveBeenCalledTimes(1);
  });

  it("keeps AudioTile hover playback host-owned while replacing only its cover", () => {
    const { container } = render(<AudioTile audio={audio as any} onClick={vi.fn()} selected onSelect={vi.fn()} />);

    expect(screen.getByTestId("entity-media-override").closest(".card-media")).toBe(
      container.querySelector(".card-media"),
    );
    expect(container.querySelector(".card-media img")).not.toBeInTheDocument();
    expect(container.querySelector(".card-media audio")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open Sample Audio" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Deselect item" })).toBeInTheDocument();
  });

  it("keeps ImageTile preview clicks host-owned around replacement media", () => {
    const onPreview = vi.fn();
    const { container } = render(
      <ImageTile
        image={
          {
            id: 3,
            title: "Sample Image",
            organized: false,
            urls: [],
            tags: [],
            performers: [],
            galleryCount: 0,
            galleryIds: [],
            galleries: [],
            files: [],
            createdAt: "",
            updatedAt: "",
          } as any
        }
        onClick={vi.fn()}
        onPreview={onPreview}
      />,
    );

    fireEvent.click(container.querySelector(".card-media")!);

    expect(onPreview).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("link", { name: "Sample Image" })).toHaveAttribute("href", "/image/3");
  });
});

describe("entity list media contexts", () => {
  it("uses the embedded zoom level to size wall columns", () => {
    vi.stubGlobal("innerWidth", 1440);
    const items = Array.from({ length: 8 }, (_, index) => ({
      ...video,
      id: index + 1,
      title: `Wall Video ${index + 1}`,
    }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(["engagement", "video", "batch", items.map((item) => item.id)], []);

    const view = render(
      <QueryClientProvider client={queryClient}>
        <RelatedEntityListView
          entityType="videos"
          items={items as any}
          displayMode="wall"
          zoomLevel={2}
          infinitePageSize={false}
          onNavigate={vi.fn()}
        />
      </QueryClientProvider>,
    );

    expect(view.container.firstElementChild?.children).toHaveLength(8);

    view.rerender(
      <QueryClientProvider client={queryClient}>
        <RelatedEntityListView
          entityType="videos"
          items={items as any}
          displayMode="wall"
          zoomLevel={8}
          infinitePageSize={false}
          onNavigate={vi.fn()}
        />
      </QueryClientProvider>,
    );

    expect(view.container.firstElementChild?.children).toHaveLength(2);
  });

  it("routes tag list thumbnails through entity.media without changing the row chrome", () => {
    overrideRenderState.replace = false;
    const { container } = render(
      <RelatedEntityListRow
        entityType="tags"
        item={{ id: 17, name: "List Tag", imagePath: "/list-tag.jpg" } as any}
        onNavigate={vi.fn()}
      />,
    );

    expect(overrideRendererCalls).toHaveLength(1);
    expect(overrideRendererCalls[0]?.componentProps).toMatchObject({
      entityType: "tag",
      entityId: 17,
      surface: "list",
      imageUrl: "/list-tag.jpg",
      alt: "List Tag",
      fit: "cover",
      className: "h-full w-full",
    });
    expect(screen.getByRole("button", { name: /List Tag/i })).toBeInTheDocument();

    const nativeImage = container.querySelector("img") as HTMLImageElement;
    const nativeFallback = nativeImage.nextElementSibling as HTMLElement;
    expect(nativeImage).toHaveAttribute("src", "/list-tag.jpg");
    expect(nativeFallback).toHaveClass("hidden");

    fireEvent.error(nativeImage);
    expect(nativeImage).toHaveStyle({ display: "none" });
    expect(nativeFallback).toHaveStyle({ display: "flex" });
    expect(nativeFallback.querySelector("svg")).toBeInTheDocument();
  });

  it("preserves the native tag icon when a list thumbnail has no image", () => {
    overrideRenderState.replace = false;
    const { container } = render(
      <RelatedEntityListRow
        entityType="tags"
        item={{ id: 18, name: "No Image Tag", hasImage: false } as any}
        onNavigate={vi.fn()}
      />,
    );

    expect(overrideRendererCalls[0]?.componentProps).toMatchObject({
      entityType: "tag",
      entityId: 18,
      surface: "list",
      imageUrl: null,
    });
    expect(container.querySelector("img")).not.toBeInTheDocument();
    expect(container.querySelector("svg")).toBeInTheDocument();
  });

  it("keeps feed tag-chip range selection while opening entity media hover", () => {
    const onToggle = vi.fn();
    const onNavigate = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(["engagement", "image", "batch", [31]], []);

    render(
      <QueryClientProvider client={queryClient}>
        <RelatedEntityListView
          entityType="images"
          items={[
            {
              id: 31,
              title: "Feed Image",
              organized: false,
              urls: [],
              tags: [{ id: 41, name: "Feed Tag", imagePath: "/feed-tag.jpg" }],
              performers: [],
              galleryCount: 0,
              galleryIds: [],
              galleries: [],
              files: [],
              createdAt: "",
              updatedAt: "",
            } as any,
          ]}
          displayMode="feed"
          infinitePageSize={false}
          selecting
          onToggle={onToggle}
          onNavigate={onNavigate}
        />
      </QueryClientProvider>,
    );

    const tagChip = screen.getByRole("button", { name: "#Feed Tag" });
    fireEvent.mouseEnter(tagChip);

    expect(screen.getByRole("tooltip", { name: "Media for Feed Tag" })).toContainElement(
      screen.getByTestId("entity-media-override"),
    );

    fireEvent.click(tagChip, { shiftKey: true });

    expect(onToggle).toHaveBeenCalledWith(31, { range: true });
    expect(onNavigate).not.toHaveBeenCalled();
  });
});
