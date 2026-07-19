export const MEDIA_PLAYER_ACTIONS_SLOT = "media-player-actions" as const;
export const MEDIA_PLAYER_OVERLAY_SLOT = "media-player-overlay" as const;

export type MediaPlayerSurface = "detail" | "quick-view" | "compilation";

export interface MediaPlayerContentRect {
  left: number;
  top: number;
  width: number;
  height: number;
}

export interface MediaPlayerInteractionModeOptions {
  hideNativeControls?: boolean;
  pauseTracking?: boolean;
  pausePlayback?: boolean;
}

export interface MediaPlayerExtensionContext {
  hostType: "video";
  hostId: number;
  surface: MediaPlayerSurface;

  currentTime: number;
  duration: number;
  playing: boolean;
  playbackRate?: number;
  intrinsicWidth: number;
  intrinsicHeight: number;
  contentRect: MediaPlayerContentRect;

  play(): Promise<void>;
  pause(): void;
  seek(seconds: number): void;
  setPlaybackRate?(rate: number): void;

  acquireInteractionMode(options?: MediaPlayerInteractionModeOptions): () => void;
}

export interface MediaPlayerInteractionSnapshot {
  active: boolean;
  hideNativeControls: boolean;
  pauseTracking: boolean;
  pausePlayback: boolean;
}

export const EMPTY_MEDIA_PLAYER_INTERACTION: MediaPlayerInteractionSnapshot = {
  active: false,
  hideNativeControls: false,
  pauseTracking: false,
  pausePlayback: false,
};
