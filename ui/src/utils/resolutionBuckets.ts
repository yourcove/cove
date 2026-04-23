export interface ResolutionBucket {
  label: string;
  value: number;
  minDimension: number;
  maxDimensionExclusive: number;
}

const RESOLUTION_BUCKETS: ResolutionBucket[] = [
  { label: "144p", value: 144, minDimension: 144, maxDimensionExclusive: 341 },
  { label: "240p", value: 240, minDimension: 341, maxDimensionExclusive: 533 },
  { label: "360p", value: 360, minDimension: 533, maxDimensionExclusive: 747 },
  { label: "480p", value: 480, minDimension: 747, maxDimensionExclusive: 907 },
  { label: "540p", value: 540, minDimension: 907, maxDimensionExclusive: 1120 },
  { label: "720p", value: 720, minDimension: 1120, maxDimensionExclusive: 1600 },
  { label: "1080p", value: 1080, minDimension: 1600, maxDimensionExclusive: 2240 },
  { label: "1440p", value: 1440, minDimension: 2240, maxDimensionExclusive: 3200 },
  { label: "4K", value: 2160, minDimension: 3200, maxDimensionExclusive: 4480 },
  { label: "5K", value: 2880, minDimension: 4480, maxDimensionExclusive: 5632 },
  { label: "6K", value: 3384, minDimension: 5632, maxDimensionExclusive: 6656 },
  { label: "7K", value: 4032, minDimension: 6656, maxDimensionExclusive: 7424 },
  { label: "8K", value: 4320, minDimension: 7424, maxDimensionExclusive: 9840 },
  { label: "HUGE", value: 9999, minDimension: 9840, maxDimensionExclusive: Number.POSITIVE_INFINITY },
];

export const RESOLUTION_FILTER_OPTIONS = [
  { label: "Any", value: 0 },
  { label: "144p", value: 144 },
  { label: "240p", value: 240 },
  { label: "360p", value: 360 },
  { label: "480p", value: 480 },
  { label: "720p", value: 720 },
  { label: "1080p", value: 1080 },
  { label: "1440p", value: 1440 },
  { label: "4K", value: 2160 },
  { label: "5K", value: 2880 },
  { label: "6K", value: 3384 },
  { label: "8K", value: 4320 },
];

export function getResolutionBucket(maxDimension: number): ResolutionBucket | null {
  if (!Number.isFinite(maxDimension) || maxDimension < RESOLUTION_BUCKETS[0].minDimension) {
    return null;
  }

  return RESOLUTION_BUCKETS.find((bucket) =>
    maxDimension >= bucket.minDimension && maxDimension < bucket.maxDimensionExclusive,
  ) ?? RESOLUTION_BUCKETS[RESOLUTION_BUCKETS.length - 1];
}

export function getResolutionBucketLabel(width: number, height: number): string | null {
  return getResolutionBucket(Math.max(width, height))?.label ?? null;
}