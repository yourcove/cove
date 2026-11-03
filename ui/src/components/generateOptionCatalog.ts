import type { GenerateEstimate, GenerateOptions, GenerateWorkCount } from "../api/client";

export type GenerateOptionKey =
  | "thumbnails"
  | "previews"
  | "sprites"
  | "vrStereo"
  | "segmentCovers"
  | "phashes"
  | "md5"
  | "imageThumbnails"
  | "imagePhashes"
  | "galleryThumbnails"
  | "audioPhashes"
  | "textPhashes";

export type WorkSize = "XS" | "S" | "M" | "L" | "XL";

export interface GenerateOptionInfo {
  label: string;
  /** One line under the label. */
  summary: string;
  /** The Details panel: what the file is, where Cove uses it, and what shows without it. */
  what: string;
  usedFor: string;
  without: string;
  /**
   * Rough seconds of work for one item on typical hardware. Only picks a size letter, so it stays coarse;
   * MD5 is sized from the bytes it reads instead.
   */
  secondsEach: number;
  /** What one item is, singular and plural. */
  unit: [string, string];
}

// MD5 reads every byte, so its cost follows file size rather than file count.
const MD5_BYTES_PER_SECOND = 200 * 1024 * 1024;

export const GENERATE_OPTIONS: Record<GenerateOptionKey, GenerateOptionInfo> = {
  thumbnails: {
    label: "Thumbnails / screenshots",
    summary: "One still per video: its cover on cards and its page.",
    what: "One full-size frame per video, taken at 20% of its length. Skipped for a video whose cover you chose.",
    usedFor:
      "Video cards, the video page before playback, walls and feeds, the VR wall, and the cover sent with a draft to a metadata server.",
    without: "Cards show a film icon instead of a picture.",
    secondsEach: 0.8,
    unit: ["video", "videos"],
  },
  previews: {
    label: "Video previews",
    summary: "A short clip per video that plays when you hover its card.",
    what: "A silent 640 px clip of 12 short moments, under a second each, spread across the video.",
    usedFor: "Plays when you hover a video card, on the video wall, in feeds set to previews, and on the VR wall.",
    without: "Hovering a card plays no clip.",
    secondsEach: 30,
    unit: ["video", "videos"],
  },
  sprites: {
    label: "Sprite sheets",
    summary: "Small frames across each video for the seek bar; segments without a cover show one of these.",
    what: "Up to 81 small frames (160 px) spread across the video, in one image with a timing file.",
    usedFor:
      "Pictures when hovering the player's seek bar, the scrubber strip on the video page, scrubbing on cards, and the stand-in picture for clips, segments and face crops without their own frame.",
    without: "No seek-bar pictures, and those stand-ins fall back to the video's cover.",
    secondsEach: 15,
    unit: ["video", "videos"],
  },
  vrStereo: {
    label: "VR 3D covers and previews",
    summary: "VR videos only: the 3D cover and preview a headset shows.",
    what: "VR videos only: a cover and a preview clip with both eyes side by side.",
    usedFor: "The VR wall in a headset.",
    without: "The VR wall uses the ordinary one-eye cover and preview.",
    secondsEach: 30,
    unit: ["VR video", "VR videos"],
  },
  segmentCovers: {
    label: "Segment covers",
    summary:
      "The exact start frame of each segment: sharper than its sprite tile, and the picture the hover preview starts from.",
    what: "The exact frame at the start of each segment, full size. Segments with a cover you chose are skipped, and face segments unless you pick them.",
    usedFor: "Segment cards on the Segments page, dashboards and related lists; hovering a card plays from this frame.",
    without:
      "Cards show the sprite tile covering the segment's start, often seconds away and sometimes the shot before.",
    secondsEach: 0.8,
    unit: ["segment", "segments"],
  },
  phashes: {
    label: "Perceptual hashes (phash)",
    summary: "A fingerprint of how each video looks, for finding duplicates and matching on metadata servers.",
    what: "A fingerprint of how the video looks, from 25 frames across it, computed the way Stash does.",
    usedFor:
      "The duplicate finder's visual mode, the pHash filter and sort, identifying and submitting on StashDB and other metadata servers, scrapers, and matching files of the same video.",
    without: "Those find nothing for the video.",
    secondsEach: 8,
    unit: ["video", "videos"],
  },
  md5: {
    label: "MD5 checksums",
    summary: "A checksum of each file, for exact duplicates. Reads every file in full.",
    what: "A checksum of every byte of the file. Made for video, image, gallery, audio and text files alike.",
    usedFor: "The duplicate finder's exact matching, the Checksum filters, metadata-server matching and scrapers.",
    without: "Exact matching relies on the quicker oshash alone.",
    secondsEach: 5,
    unit: ["file", "files"],
  },
  imageThumbnails: {
    label: "Image thumbnails",
    summary: "Small copies of images for grids.",
    what: "A 640 px copy of each image. Other sizes are made when they are first shown.",
    usedFor: "Image cards and walls.",
    without: "Each one is made the first time it is shown, so grids load slower at first.",
    secondsEach: 0.2,
    unit: ["image", "images"],
  },
  imagePhashes: {
    label: "Image phashes",
    summary: "Fingerprints of how images look, for the pHash filter.",
    what: "A fingerprint of how each image looks.",
    usedFor: "The pHash option of the image and gallery Hash filter.",
    without: "That filter finds nothing for these images.",
    secondsEach: 0.1,
    unit: ["image", "images"],
  },
  galleryThumbnails: {
    label: "Gallery cover thumbnails",
    summary: "Covers for gallery cards.",
    what: "The 640 px thumbnail of each gallery's cover image, or of its first image.",
    usedFor: "Gallery cards on the Galleries page.",
    without: "It is made the first time the gallery is shown.",
    secondsEach: 0.2,
    unit: ["gallery", "galleries"],
  },
  audioPhashes: {
    label: "Audio perceptual hashes",
    summary: "Fingerprints of audio files, stored for later use.",
    what: "A 64-bit fingerprint sampled across each audio file.",
    usedFor: "Stored with the file; nothing in Cove uses it yet.",
    without: "Nothing changes today.",
    secondsEach: 2,
    unit: ["audio file", "audio files"],
  },
  textPhashes: {
    label: "Text perceptual hashes",
    summary: "Fingerprints of texts, stored for later use.",
    what: "A fingerprint of the words in each text, from its first 2 MB.",
    usedFor: "Stored with the file; nothing in Cove uses it yet.",
    without: "Nothing changes today.",
    secondsEach: 0.05,
    unit: ["text", "texts"],
  },
};

export const WORK_SIZES: WorkSize[] = ["XS", "S", "M", "L", "XL"];

/** The work for one item. */
export function sizeEach(seconds: number): WorkSize {
  if (seconds >= 60) return "L";
  if (seconds >= 10) return "M";
  if (seconds >= 1) return "S";
  return "XS";
}

/** The work for a whole run. */
export function sizeRun(seconds: number): WorkSize {
  if (seconds >= 12 * 3600) return "XL";
  if (seconds >= 2 * 3600) return "L";
  if (seconds >= 15 * 60) return "M";
  if (seconds >= 60) return "S";
  return "XS";
}

export const EACH_SIZE_WORDS: Record<WorkSize, string> = {
  XS: "under a second",
  S: "a few seconds",
  M: "up to a minute",
  L: "minutes",
  XL: "minutes",
};

export const RUN_SIZE_WORDS: Record<WorkSize, string> = {
  XS: "under a minute",
  S: "minutes",
  M: "about an hour",
  L: "several hours",
  XL: "half a day or more",
};

/** A count for display: exact, or for a sampled one rounded to two significant digits, as "~7,000". */
export function formatCount(count: number, estimated = false): string {
  if (!estimated || count < 100) return `${estimated && count > 0 ? "~" : ""}${count.toLocaleString()}`;
  const magnitude = 10 ** (Math.floor(Math.log10(count)) - 1);
  return `~${(Math.round(count / magnitude) * magnitude).toLocaleString()}`;
}

/** The segment kinds a run makes covers for when none were chosen: every kind except face. */
export function defaultSegmentCoverKinds(available: string[]): string[] {
  return available.filter((kind) => kind !== "face");
}

export function selectedSegmentCoverKinds(options: GenerateOptions, available: string[]): string[] {
  return (options.segmentCoverKinds ?? defaultSegmentCoverKinds(available)).map((kind) => kind.toLowerCase());
}

export function segmentKindLabel(kind: string): string {
  if (!kind) return "Segments without a kind";
  return `${kind.charAt(0).toUpperCase()}${kind.slice(1)} segments`;
}

export interface OptionWork {
  /** Items this run would make. */
  count: number;
  /** The count was scaled up from a sample, so it is rough. */
  estimated: boolean;
  /** Items the scope holds, made or not. */
  total: number;
  secondsEach: number;
  seconds: number;
}

/**
 * What one option would make in the estimated scope: what is missing, or everything when overwriting.
 * Null when the scope cannot reach the option at all.
 */
export function optionWork(
  key: GenerateOptionKey,
  estimate: GenerateEstimate,
  options: GenerateOptions,
): OptionWork | null {
  const info = GENERATE_OPTIONS[key];
  const overwrite = !!options.overwrite;
  if (key === "segmentCovers") {
    if (!estimate.segmentCovers) return null;
    const kinds = new Set(
      selectedSegmentCoverKinds(
        options,
        estimate.segmentCovers.map((entry) => entry.kind),
      ),
    );
    const chosen = estimate.segmentCovers.filter((entry) => kinds.has(entry.kind));
    const count = chosen.reduce((sum, entry) => sum + (overwrite ? entry.total : entry.missing), 0);
    const total = chosen.reduce((sum, entry) => sum + entry.total, 0);
    const estimated = !overwrite && chosen.some((entry) => entry.estimated);
    return { count, total, estimated, secondsEach: info.secondsEach, seconds: count * info.secondsEach };
  }

  const counts = estimate[key] as GenerateWorkCount | null | undefined;
  if (!counts) return null;
  const count = overwrite ? counts.total : counts.missing;
  // Totals are always counted; only what is missing is sampled.
  const estimated = !overwrite && !!counts.estimated;
  if (key === "md5") {
    const bytes = overwrite ? counts.totalBytes : counts.missingBytes;
    const seconds = bytes / MD5_BYTES_PER_SECOND;
    return {
      count,
      total: counts.total,
      estimated,
      secondsEach: count > 0 ? seconds / count : info.secondsEach,
      seconds,
    };
  }
  return { count, total: counts.total, estimated, secondsEach: info.secondsEach, seconds: count * info.secondsEach };
}

/** The size of a whole run: every ticked option's work added up. */
export function runSize(keys: GenerateOptionKey[], estimate: GenerateEstimate, options: GenerateOptions): WorkSize {
  const seconds = keys
    .filter((key) => !!options[key])
    .reduce((sum, key) => sum + (optionWork(key, estimate, options)?.seconds ?? 0), 0);
  return sizeRun(seconds);
}
