import type { Gallery } from "../api/types";

type GalleryWithDisplayFallback = Pick<Gallery, "id" | "title" | "displayName">;

export function getGalleryDisplayTitle(gallery: GalleryWithDisplayFallback): string {
  const title = gallery.title?.trim();
  if (title) {
    return title;
  }

  const fallback = gallery.displayName?.trim();
  if (fallback) {
    return fallback;
  }

  return `Gallery ${gallery.id}`;
}
