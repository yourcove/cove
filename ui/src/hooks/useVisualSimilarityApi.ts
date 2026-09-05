import { useMemo } from "react";
import { createVisualSimilarityClient } from "../api/client";
import { useExtensions } from "../extensions/ExtensionLoader";

const VISUAL_SIMILARITY_FEATURE_KEY = "visual-similarity";

export function useVisualSimilarityApi() {
  const { getFeature } = useExtensions();
  const feature = getFeature(VISUAL_SIMILARITY_FEATURE_KEY);
  const apiBasePath = feature?.options?.apiBasePath;

  return useMemo(() => (apiBasePath ? createVisualSimilarityClient(apiBasePath) : null), [apiBasePath]);
}
