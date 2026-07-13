import type { DetailListDisplayMode } from "./DetailListToolbar";

export type RelatedEntityType = "videos" | "images" | "performers" | "galleries" | "studios" | "tags" | "groups" | "audios" | "texts" | "segments" | "faces";

const RELATED_ENTITY_DISPLAY_MODES: Record<RelatedEntityType, DetailListDisplayMode[]> = {
  videos: ["grid", "list", "wall", "tagger", "feed", "vertical"],
  images: ["grid", "list", "wall", "tagger", "feed"],
  performers: ["grid", "list", "wall", "tagger"],
  galleries: ["grid", "list", "wall", "tagger"],
  studios: ["grid", "list", "tagger"],
  tags: ["grid", "list", "graph", "tagger"],
  groups: ["grid", "list", "tagger"],
  audios: ["grid", "list", "tagger"],
  texts: ["grid", "list", "tagger"],
  segments: ["grid", "list"],
  faces: ["grid", "list"],
};

export function getRelatedEntityDisplayModes(entityType: RelatedEntityType) {
  return RELATED_ENTITY_DISPLAY_MODES[entityType];
}
