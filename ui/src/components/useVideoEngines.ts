import { useExtensionRuntimeSettled, useResolvedComponentOverrides } from "../extensions/ExtensionLoader";
import { VIDEO_ENGINE_TARGET, isVideoEngineComponent, type VideoEngineComponent } from "./VideoEngine";

export interface ResolvedVideoEngine {
  /** Stable identity of the contribution, used to remember an engine that failed for a video. */
  key: string;
  extensionId: string;
  Component: VideoEngineComponent;
  revision: number;
}

/**
 * Registered video engines in priority order. A contribution whose component does not declare valid
 * `videoEngine` capabilities is ignored. `settled` turns true once the extension runtime has loaded,
 * so the player does not commit to a transcode before an engine that plays the file has arrived.
 */
export function useVideoEngines(): { engines: ResolvedVideoEngine[]; settled: boolean } {
  const settled = useExtensionRuntimeSettled();
  const engines = useResolvedComponentOverrides(VIDEO_ENGINE_TARGET).flatMap(({ contribution, Component, revision }) =>
    isVideoEngineComponent(Component)
      ? [
          {
            key: JSON.stringify([contribution.extensionId, contribution.componentName]),
            extensionId: contribution.extensionId,
            Component,
            revision,
          },
        ]
      : [],
  );
  return { engines, settled };
}
