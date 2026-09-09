import type { Video, VideoCreate } from "../api/types";
import { getEditableTagIds } from "./tags";

interface SubVideoRange {
  startSec: number;
  endSec?: number;
}

interface SubVideoOverrides {
  title?: string;
  tagIds?: number[];
}

function mergeUniqueIds(primary: number[], extra?: number[]) {
  return Array.from(new Set([...(primary ?? []), ...(extra ?? [])]));
}

export function buildSubVideoCreate(
  video: Video,
  range: SubVideoRange,
  overrides: SubVideoOverrides = {},
): VideoCreate {
  const mergedTagIds = mergeUniqueIds(getEditableTagIds(video.tags), overrides.tagIds);
  const performerIds = video.performers.map((performer) => performer.id);
  const galleryIds = video.galleries.map((gallery) => gallery.id);
  const groups = video.groups.map((group) => ({ groupId: group.id, videoIndex: group.videoIndex }));
  const urls = video.urls.filter((url) => url.trim().length > 0);
  const title = overrides.title?.trim() || video.title;

  return {
    title,
    code: video.code,
    details: video.details,
    director: video.director,
    date: video.date,
    organized: video.organized,
    isVr: video.isVr ?? false,
    studioId: video.studioId,
    urls: urls.length > 0 ? [...urls] : undefined,
    tagIds: mergedTagIds.length > 0 ? mergedTagIds : undefined,
    performerIds: performerIds.length > 0 ? performerIds : undefined,
    galleryIds: galleryIds.length > 0 ? galleryIds : undefined,
    groups: groups.length > 0 ? groups : undefined,
    customFields: video.customFields ? { ...video.customFields } : undefined,
    parentVideoId: video.id,
    clipStartSec: range.startSec,
    clipEndSec: range.endSec,
  };
}
