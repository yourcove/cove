import type { Image } from "../api/types";

type ImageWithDisplayFallback = Pick<Image, "id" | "title" | "files">;

function getLeafName(path: string): string {
  const segments = path.replace(/\\/g, "/").split("/").filter(Boolean);
  return segments.at(-1) ?? path;
}

export function getImageDisplayTitle(image: ImageWithDisplayFallback): string {
  const title = image.title?.trim();
  if (title) {
    return title;
  }

  const primaryFile = image.files.find((file) => file.basename.trim() || file.path.trim());
  const fallbackPath = primaryFile?.basename.trim() || primaryFile?.path.trim();
  if (fallbackPath) {
    return getLeafName(fallbackPath);
  }

  return `Image ${image.id}`;
}