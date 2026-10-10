import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  findSpriteIndex,
  isCoveSpriteVtt,
  parseSpriteVtt,
  spriteCaptureTime,
  spriteTileStyle,
  useVideoSprites,
} from "../hooks/useVideoSprites";
import { serverAwareFetch } from "../state/serverAvailability";

vi.mock("../state/serverAvailability", () => ({ serverAwareFetch: vi.fn() }));

const VTT = `WEBVTT

00:00:00.000 --> 00:00:21.601
abc_sprite.jpg#xywh=0,0,160,90

00:00:21.601 --> 00:00:43.202
abc_sprite.jpg#xywh=160,0,160,90

00:00:43.202 --> 00:01:04.804
abc_sprite.jpg#xywh=0,90,160,90
`;

describe("sprite VTT helpers", () => {
  it("parses tile times and sheet coordinates", () => {
    expect(parseSpriteVtt(VTT)).toEqual([
      { start: 0, end: 21.601, x: 0, y: 0, w: 160, h: 90 },
      { start: 21.601, end: 43.202, x: 160, y: 0, w: 160, h: 90 },
      { start: 43.202, end: 64.804, x: 0, y: 90, w: 160, h: 90 },
    ]);
  });

  it("finds the tile covering a time and clamps outside the tiles", () => {
    const entries = parseSpriteVtt(VTT);
    expect(findSpriteIndex(entries, 0)).toBe(0);
    expect(findSpriteIndex(entries, 30)).toBe(1);
    expect(findSpriteIndex(entries, 500)).toBe(2);
    expect(findSpriteIndex([{ start: 5, end: 10, x: 0, y: 0, w: 1, h: 1 }], 1)).toBe(0);
  });

  it("scales one tile and the whole sheet to the requested width", () => {
    const entries = parseSpriteVtt(VTT);
    const sprites = { entries, imageUrl: "/sprite.jpg", sheetWidth: 320, sheetHeight: 180, capturedMidTile: false };
    expect(spriteTileStyle(sprites, entries[2], 80)).toEqual({
      width: 80,
      height: 45,
      backgroundImage: "url(/sprite.jpg)",
      backgroundPosition: "-0px -45px",
      backgroundSize: "160px 90px",
    });
  });
});

describe("sprite capture times", () => {
  it("tells Cove sprite sheets from ones copied over from Stash", () => {
    expect(isCoveSpriteVtt(VTT.replaceAll("abc", "1189"))).toBe(true);
    expect(isCoveSpriteVtt(VTT.replaceAll("abc", "9452bc8abe8d6656"))).toBe(false);
    expect(isCoveSpriteVtt(VTT.replaceAll("abc", "0123456789012345"))).toBe(false);
    expect(isCoveSpriteVtt(VTT.replaceAll("abc", "d41d8cd98f00b204e9800998ecf8427e"))).toBe(false);
    expect(isCoveSpriteVtt("WEBVTT\n")).toBe(false);
  });

  it("uses the middle of a Cove tile and the start of a Stash tile", () => {
    const entries = parseSpriteVtt(VTT);
    const sprites = { entries, imageUrl: "/sprite.jpg", sheetWidth: 320, sheetHeight: 180 };
    expect(spriteCaptureTime({ ...sprites, capturedMidTile: true }, entries[1])).toBe(32.402);
    expect(spriteCaptureTime({ ...sprites, capturedMidTile: false }, entries[1])).toBe(21.601);
  });
});

describe("useVideoSprites", () => {
  const fetchMock = vi.mocked(serverAwareFetch);
  const vttFor = (videoId: number) => VTT.replaceAll("abc", `video${videoId}`);

  afterEach(() => {
    fetchMock.mockReset();
  });

  it("records that Cove captured the tiles of a sheet named after the video", async () => {
    fetchMock.mockResolvedValue(new Response(VTT.replaceAll("abc", "7")));

    const { result } = renderHook(() => useVideoSprites(7));

    await waitFor(() => expect(result.current).not.toBeNull());
    expect(result.current?.capturedMidTile).toBe(true);
  });

  it("loads the sprite sheet and its size for a video", async () => {
    fetchMock.mockResolvedValue(new Response(VTT));

    const { result } = renderHook(() => useVideoSprites(7));

    await waitFor(() => expect(result.current).not.toBeNull());
    expect(fetchMock).toHaveBeenCalledWith("/api/stream/video/7/vtt/thumbs");
    expect(result.current).toMatchObject({
      imageUrl: "/api/stream/video/7/sprite",
      sheetWidth: 320,
      sheetHeight: 180,
      capturedMidTile: false,
    });
    expect(result.current?.entries).toHaveLength(3);
  });

  it("returns null for a missing or empty sprite file", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 404 }));
    const missing = renderHook(() => useVideoSprites(7));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    await act(async () => {});
    expect(missing.result.current).toBeNull();

    fetchMock.mockResolvedValueOnce(new Response("WEBVTT\n"));
    const empty = renderHook(() => useVideoSprites(8));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    await act(async () => {});
    expect(empty.result.current).toBeNull();
  });

  it("makes no request without a video", () => {
    const { result } = renderHook(() => useVideoSprites(null));
    expect(result.current).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("drops the previous video's sprites at once and ignores its late response", async () => {
    const pending = new Map<string, (response: Response) => void>();
    fetchMock.mockImplementation((url) => new Promise<Response>((resolve) => pending.set(String(url), resolve)));
    const { result, rerender } = renderHook(({ videoId }) => useVideoSprites(videoId), {
      initialProps: { videoId: 1 },
    });
    await act(async () => pending.get("/api/stream/video/1/vtt/thumbs")!(new Response(vttFor(1))));
    expect(result.current?.imageUrl).toBe("/api/stream/video/1/sprite");

    rerender({ videoId: 2 });
    expect(result.current).toBeNull();

    rerender({ videoId: 3 });
    await act(async () => pending.get("/api/stream/video/2/vtt/thumbs")!(new Response(vttFor(2))));
    expect(result.current).toBeNull();

    await act(async () => pending.get("/api/stream/video/3/vtt/thumbs")!(new Response(vttFor(3))));
    expect(result.current?.imageUrl).toBe("/api/stream/video/3/sprite");
  });
});
