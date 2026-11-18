import type { Detection, Face } from "../api/types";

type DetectionCropUrlBuilder = (detectionId: number, max?: number) => string;

/**
 * The detection crops that follow the cover in the carousel. The cover is usually a crop of the best detection, so
 * that one is left out when a cover exists; when the cover's own detection is known, that one is left out instead.
 */
export function buildFaceCarouselSampleImageUrls(
  face: Face | null | undefined,
  detections: Detection[],
  buildDetectionCropUrl: DetectionCropUrlBuilder,
  coverDetectionId?: number | null,
) {
  const ranked = rankFaceSampleDetections(detections);
  const sampleDetections = !face?.coverImageUrl
    ? ranked.slice(0, 3)
    : coverDetectionId != null
      ? ranked.filter((detection) => detection.id !== coverDetectionId).slice(0, 2)
      : ranked.slice(1, 3);
  return sampleDetections.map((detection) => buildDetectionCropUrl(detection.id, 2048));
}

export function buildFaceHeroImageUrls(face: Face | null | undefined, carouselSampleImageUrls: string[]) {
  return Array.from(
    new Set(
      [face?.coverImageUrl, ...carouselSampleImageUrls].filter(
        (url): url is string => typeof url === "string" && url.trim().length > 0,
      ),
    ),
  ).slice(0, 3);
}

function rankFaceSampleDetections(detections: Detection[]) {
  // Prefer detections that pass the quality/aspect gate (good, roughly-frontal crops)...
  const plausible = detections.filter(isPlausibleFaceDetection);
  // ...but if a face only ever appears in side-view/low-quality shots, still show its best available
  // detection rather than nothing — a face with no image at all is useless. When a better frontal image
  // is later matched, it's promoted to the face's cover and takes precedence over this fallback.
  const base = plausible.length > 0 ? plausible : detections.filter((detection) => detection.w > 0 && detection.h > 0);
  return sortFaceSampleDetections(base);
}

/** Best first: the provider's pick, then cover quality, then detection score. */
export function compareFaceSampleDetections(left: Detection, right: Detection) {
  const roleLeft = extractDetectionRole(left) === "best" ? 1 : 0;
  const roleRight = extractDetectionRole(right) === "best" ? 1 : 0;
  if (roleLeft !== roleRight) return roleRight - roleLeft;
  const qualityDelta = extractCoverQualityScore(right) - extractCoverQualityScore(left);
  if (qualityDelta !== 0) return qualityDelta;
  return (right.score ?? 0) - (left.score ?? 0);
}

function sortFaceSampleDetections(detections: Detection[]) {
  // One per video or image and moment, keeping the best.
  const seenMoments = new Set<string>();
  return [...detections].sort(compareFaceSampleDetections).filter((detection) => {
    const moment = `${detection.hostType}:${detection.hostId}:${detection.observedAtSec}`;
    if (seenMoments.has(moment)) return false;
    seenMoments.add(moment);
    return true;
  });
}

function isPlausibleFaceDetection(detection: Detection) {
  if (detection.w <= 0 || detection.h <= 0 || detection.score < 0.5) {
    return false;
  }

  const aspectRatio = detection.w / detection.h;
  if (!Number.isFinite(aspectRatio) || aspectRatio < 0.45 || aspectRatio > 1.8) {
    return false;
  }

  const area = (detection.w * detection.h) / (detection.frameWidth * detection.frameHeight);
  return !Number.isFinite(area) || area >= 0.005;
}

function extractDetectionRole(detection: Detection): string | undefined {
  const extra = detection.extra;
  if (extra && typeof extra === "object") {
    const role = (extra as Record<string, unknown>).role;
    return typeof role === "string" ? role : undefined;
  }
  return undefined;
}

function extractCoverQualityScore(detection: Detection): number {
  const extra = detection.extra;
  if (extra && typeof extra === "object") {
    const value = (extra as Record<string, unknown>).coverQualityScore;
    if (typeof value === "number" && Number.isFinite(value)) {
      return value;
    }
    if (typeof value === "string") {
      const parsed = Number(value);
      if (Number.isFinite(parsed)) {
        return parsed;
      }
    }
  }
  return 0;
}
