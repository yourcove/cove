import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { EntityEngagement, Video } from "../api/types";
import {
  DEFAULT_VIDEO_LIST_COLUMN_IDS,
  getVideoDisplayDuration,
  getVideoPrimaryFile,
  isDefaultVideoListColumns,
  normalizeVideoListColumns,
  resolveVideoListColumns,
  VIDEO_LIST_COLUMN_BY_ID,
  VIDEO_LIST_COLUMN_IDS,
  VIDEO_LIST_COLUMNS,
  VIDEO_LIST_MAX_COLUMN_WIDTH_PX,
  VIDEO_LIST_MIN_COLUMN_WIDTH_PX,
  hasVideoListColumnWidths,
  normalizeVideoListColumnWidths,
  resolveVideoListColumnWidths,
} from "../components/videoListColumns";

vi.mock("../components/Rating", () => ({
  RatingBadge: ({ rating }: { rating?: number }) => <span data-testid="rating-badge">{rating}</span>,
}));

const secondaryFile = {
  id: 11,
  basename: "beta.mp4",
  path: "/library/beta.mp4",
  format: "mp4",
  size: 500,
  duration: 30,
  width: 640,
  height: 360,
  frameRate: 25,
  bitRate: 800_000,
  videoCodec: "h264",
  audioCodec: "aac",
  fingerprints: [],
};

const primaryFile = {
  id: 10,
  basename: "alpha.mp4",
  path: "/library/alpha.mp4",
  format: "mp4",
  size: 1_610_612_736,
  duration: 3900,
  width: 1920,
  height: 1080,
  frameRate: 29.97,
  bitRate: 2_400_000,
  videoCodec: "hevc",
  audioCodec: "aac",
  fingerprints: [],
};

const video: Video = {
  id: 42,
  title: "Sample Video",
  code: "ABC-123",
  director: "Someone",
  date: "2024-03-05",
  organized: true,
  isVr: false,
  studioId: 7,
  studioName: "Studio Seven",
  urls: [],
  tags: [
    { id: 1, name: "one", favorite: false, organized: false },
    { id: 2, name: "two", favorite: false, organized: false },
    { id: 3, name: "three", favorite: false, organized: false },
    { id: 4, name: "four", favorite: false, organized: false },
    { id: 5, name: "five", favorite: false, organized: false },
  ] as Video["tags"],
  performers: [
    { id: 1, name: "Ann", favorite: false },
    { id: 2, name: "Bob", favorite: false },
    { id: 3, name: "Cy", favorite: false },
    { id: 4, name: "Dee", favorite: false },
  ] as Video["performers"],
  files: [secondaryFile, primaryFile],
  primaryFileId: 10,
  groups: [{ id: 1, name: "Group A", videoIndex: 1 }],
  galleries: [{ id: 1 }, { id: 2 }],
  remoteIds: [],
  createdAt: "2024-01-01T10:00:00Z",
  updatedAt: "2024-01-02T10:00:00Z",
};

const engagement: EntityEngagement = {
  hostId: 42,
  isFavorite: true,
  rating: 80,
  resumeTime: 0,
  playDuration: 0,
  playCount: 3,
  lastPlayedAt: "2024-02-01T12:00:00Z",
  likeCount: 2,
  derivedLikeCount: 2,
  pageVisitCount: 0,
  completeCount: 0,
};

const context = { onNavigate: vi.fn(), thumbnailHeightPx: 64 };

describe("videoListColumns", () => {
  it("defines unique ids and valid defaults", () => {
    expect(new Set(VIDEO_LIST_COLUMN_IDS).size).toBe(VIDEO_LIST_COLUMNS.length);
    for (const id of DEFAULT_VIDEO_LIST_COLUMN_IDS) {
      expect(VIDEO_LIST_COLUMN_BY_ID.has(id)).toBe(true);
    }
    expect(isDefaultVideoListColumns(DEFAULT_VIDEO_LIST_COLUMN_IDS)).toBe(true);
    expect(isDefaultVideoListColumns([...DEFAULT_VIDEO_LIST_COLUMN_IDS].reverse())).toBe(false);
  });

  it("normalizes untrusted column lists", () => {
    expect(normalizeVideoListColumns(undefined)).toBeUndefined();
    expect(normalizeVideoListColumns("title")).toBeUndefined();
    expect(normalizeVideoListColumns([])).toBeUndefined();
    expect(normalizeVideoListColumns(["bogus", 3, null])).toBeUndefined();
    expect(normalizeVideoListColumns(["path", "bogus", " title ", "path", 7, "date"])).toEqual([
      "path",
      "title",
      "date",
    ]);
  });

  it("resolves defaults when the option is missing", () => {
    expect(resolveVideoListColumns(undefined)).toEqual(DEFAULT_VIDEO_LIST_COLUMN_IDS);
    expect(resolveVideoListColumns({ displayMode: "list" })).toEqual(DEFAULT_VIDEO_LIST_COLUMN_IDS);
    expect(resolveVideoListColumns({ listColumns: ["title", "code"] })).toEqual(["title", "code"]);
    expect(resolveVideoListColumns(undefined)).not.toBe(DEFAULT_VIDEO_LIST_COLUMN_IDS);
  });

  it("prefers the primary file and falls back to the first file", () => {
    expect(getVideoPrimaryFile(video)?.id).toBe(10);
    expect(getVideoPrimaryFile({ ...video, primaryFileId: 99 })?.id).toBe(11);
    expect(getVideoPrimaryFile({ ...video, primaryFileId: null })?.id).toBe(11);
    expect(getVideoPrimaryFile({ ...video, files: [] })).toBeUndefined();
    expect(getVideoDisplayDuration(video)).toBe(3900);
    expect(getVideoDisplayDuration({ ...video, clipStartSec: 10, clipEndSec: 25 })).toBe(15);
  });

  it.each(VIDEO_LIST_COLUMNS.map((column) => [column.id, column] as const))(
    "renders the %s column without throwing",
    (_id, column) => {
      expect(() => render(<div>{column.render(video, engagement, context)}</div>).unmount()).not.toThrow();
      const emptyVideo = { ...video, title: undefined, files: [], tags: [], performers: [], groups: [], galleries: [] };
      expect(() => render(<div>{column.render(emptyVideo, undefined, context)}</div>).unmount()).not.toThrow();
    },
  );

  it("formats file-derived and engagement values", () => {
    const cell = (id: string) => VIDEO_LIST_COLUMN_BY_ID.get(id as never)!.render(video, engagement, context);
    render(
      <div>
        <span data-testid="duration">{cell("duration")}</span>
        <span data-testid="fileSize">{cell("fileSize")}</span>
        <span data-testid="resolution">{cell("resolution")}</span>
        <span data-testid="codec">{cell("codec")}</span>
        <span data-testid="playCount">{cell("playCount")}</span>
        <span data-testid="galleries">{cell("galleries")}</span>
        <span data-testid="performers">{cell("performers")}</span>
        <span data-testid="tags">{cell("tags")}</span>
        <span data-testid="rating">{cell("rating")}</span>
        <span data-testid="title">{cell("title")}</span>
      </div>,
    );
    expect(screen.getByTestId("duration")).toHaveTextContent("1:05:00");
    expect(screen.getByTestId("fileSize")).toHaveTextContent("1.5 GB");
    expect(screen.getByTestId("resolution")).toHaveTextContent("1080p");
    expect(screen.getByTestId("codec")).toHaveTextContent("hevc / aac");
    expect(screen.getByTestId("playCount")).toHaveTextContent("3");
    expect(screen.getByTestId("galleries")).toHaveTextContent("2");
    expect(screen.getByTestId("performers")).toHaveTextContent("+1");
    expect(screen.getByTestId("tags")).toHaveTextContent("+1");
    expect(screen.getByTestId("rating")).toHaveTextContent("80");
    expect(screen.getByTestId("title")).toHaveTextContent("Sample Video");
  });

  it("falls back to the primary filename for untitled videos", () => {
    render(<div>{VIDEO_LIST_COLUMN_BY_ID.get("title")!.render({ ...video, title: "" }, undefined, context)}</div>);
    expect(screen.getByText("alpha.mp4")).toBeInTheDocument();
  });
});

describe("videoListColumnWidths", () => {
  it("normalizes untrusted width maps", () => {
    expect(normalizeVideoListColumnWidths(undefined)).toBeUndefined();
    expect(normalizeVideoListColumnWidths([200])).toBeUndefined();
    expect(normalizeVideoListColumnWidths({ bogus: 200, title: "wide", date: Number.NaN })).toBeUndefined();
    expect(normalizeVideoListColumnWidths({ title: 300.4, date: 10, path: 99999, bogus: 1 })).toEqual({
      title: 300,
      date: VIDEO_LIST_MIN_COLUMN_WIDTH_PX,
      path: VIDEO_LIST_MAX_COLUMN_WIDTH_PX,
    });
  });

  it("resolves widths from saved-filter options", () => {
    expect(resolveVideoListColumnWidths(undefined)).toEqual({});
    expect(resolveVideoListColumnWidths({ listColumnWidths: { title: 250 } })).toEqual({ title: 250 });
    expect(hasVideoListColumnWidths({})).toBe(false);
    expect(hasVideoListColumnWidths({ title: 250 })).toBe(true);
  });
});
