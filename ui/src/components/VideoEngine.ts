import type { ComponentPropsWithRef, ComponentType, Ref } from "react";

/**
 * Component override target for the element that plays a video. An extension registers a component
 * for it (`UIManifestBuilder.OverrideComponent("video.engine", ...)`) whose static `videoEngine`
 * declares what it can play. Cove's player keeps its controls, tracking and overlays and renders the
 * engine in place of its `<video>` for every stream kind the engine accepts.
 */
export const VIDEO_ENGINE_TARGET = "video.engine" as const;

/**
 * How a source reaches the player: the original file served with byte ranges, a server transcode
 * piped as fragmented MP4 (no ranges; seeking restarts it), or a server transcode as an HLS playlist.
 */
export type VideoStreamKind = "direct" | "transcode-mp4" | "hls";

export interface VideoEngineCapabilities {
  /** Stream kinds the engine can load. Cove plays every other kind with the browser's `<video>`. */
  streams: VideoStreamKind[];
  /**
   * Containers the engine plays directly, matched against the file's format (for example "avi").
   * When set, the engine only takes direct streams of these containers (or of any, with "any"), and
   * Cove plays them directly instead of moving formats browsers cannot play to a transcode. When
   * omitted, the engine takes every direct stream and Cove's transcode choices stay as they are.
   */
  directFormats?: "any" | string[];
  /** Platform features the element supports. Controls for unsupported features are hidden. */
  features?: {
    pictureInPicture?: boolean;
    airplay?: boolean;
    vr?: boolean;
  };
}

/**
 * The media surface Cove drives through its element ref. Engines behave like `<video>`: they dispatch
 * the standard media events on the element, select a resource from `<source>` children (firing `error`
 * on a `<source>` that cannot be opened and ending in `NETWORK_NO_SOURCE`), and expose `<track>`
 * children through `textTracks`.
 */
export type VideoEngineElement = HTMLElement &
  Pick<
    HTMLVideoElement,
    | "src"
    | "currentSrc"
    | "currentTime"
    | "duration"
    | "paused"
    | "ended"
    | "seeking"
    | "readyState"
    | "networkState"
    | "buffered"
    | "volume"
    | "muted"
    | "playbackRate"
    | "videoWidth"
    | "videoHeight"
    | "error"
    | "textTracks"
    | "play"
    | "pause"
    | "load"
  >;

/**
 * Exactly what Cove would give a `<video>`: the ref, the `<source>` and `<track>` children, and the
 * element's attributes and handlers. An engine forwards all of it to its element, like
 * `<video {...props} />`. Stream choice comes from the declared capabilities and the URL arrives
 * through `<source>`, so there are no engine-specific props.
 */
export type VideoEngineProps = Omit<ComponentPropsWithRef<"video">, "ref"> & { ref?: Ref<VideoEngineElement> };

export type VideoEngineComponent = ComponentType<VideoEngineProps> & { videoEngine: VideoEngineCapabilities };

export function isVideoEngineComponent(component: unknown): component is VideoEngineComponent {
  const capabilities = (component as { videoEngine?: Partial<VideoEngineCapabilities> } | null)?.videoEngine;
  return (
    typeof component === "function" &&
    Array.isArray(capabilities?.streams) &&
    capabilities.streams.every((kind) => kind === "direct" || kind === "transcode-mp4" || kind === "hls")
  );
}

/** Whether the engine plays a file of this container directly. */
export function playsDirectFormat(capabilities: VideoEngineCapabilities, format: string | undefined) {
  if (!capabilities.streams.includes("direct")) return false;
  if (capabilities.directFormats === "any") return true;
  const normalized = format?.trim().toLowerCase();
  return !!normalized && !!capabilities.directFormats?.some((entry) => entry.trim().toLowerCase() === normalized);
}
