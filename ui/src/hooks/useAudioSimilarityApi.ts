import { useMemo } from "react";
import { createAudioSimilarityClient } from "../api/client";
import { useExtensions } from "../extensions/ExtensionLoader";

const AUDIO_SIMILARITY_FEATURE_KEY = "audio-similarity";

export function useAudioSimilarityApi() {
  const { getFeature } = useExtensions();
  const feature = getFeature(AUDIO_SIMILARITY_FEATURE_KEY);
  const apiBasePath = feature?.options?.apiBasePath;

  return useMemo(() => (apiBasePath ? createAudioSimilarityClient(apiBasePath) : null), [apiBasePath]);
}
