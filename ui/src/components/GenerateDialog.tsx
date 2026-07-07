import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { metadata, type GenerateOptions } from "../api/client";
import { Loader2, Check } from "lucide-react";
import { EditModal } from "./EditModal";

interface Props {
  open: boolean;
  onClose: () => void;
  onOpenJobDrawer?: () => void;
  /** If provided, generate only for these video IDs. */
  videoIds?: number[];
  /** If provided, generate only for these image IDs. */
  imageIds?: number[];
  /** If provided, generate only for these audio IDs. */
  audioIds?: number[];
  /** If provided, generate only for these text IDs. */
  textIds?: number[];
  title?: string;
}

export function GenerateDialog({ open, onClose, onOpenJobDrawer, videoIds, imageIds, audioIds, textIds, title }: Props) {
  const isVideoScoped = (videoIds?.length ?? 0) > 0;
  const isImageScoped = (imageIds?.length ?? 0) > 0;
  const isAudioScoped = (audioIds?.length ?? 0) > 0;
  const isTextScoped = (textIds?.length ?? 0) > 0;
  const isScoped = isVideoScoped || isImageScoped || isAudioScoped || isTextScoped;

  const showVideo = isVideoScoped || !isScoped;
  const showImage = isImageScoped;
  const showAudio = isAudioScoped || !isScoped;
  const showText = isTextScoped || !isScoped;

  const [opts, setOpts] = useState<GenerateOptions>({
    thumbnails: showVideo,
    previews: false,
    sprites: false,
    segments: false,
    segmentThumbnails: false,
    segmentPreviews: false,
    phashes: false,
    md5: false,
    imageThumbnails: isImageScoped,
    imagePhashes: false,
    audioPhashes: isAudioScoped,
    textPhashes: isTextScoped,
    overwrite: false,
  });
  const [submitted, setSubmitted] = useState(false);

  const generateMut = useMutation({
    mutationFn: () => metadata.generate({ ...opts, videoIds, imageIds, audioIds, textIds }),
    onSuccess: () => {
      setSubmitted(true);
    },
  });

  if (!open) return null;

  const toggle = (key: keyof GenerateOptions) =>
    setOpts((o) => {
      const nextValue = !o[key];
      if (key === "segmentThumbnails") {
        return { ...o, segmentThumbnails: nextValue, segmentPreviews: nextValue ? o.segmentPreviews : false, segments: false };
      }
      if (key === "segmentPreviews") {
        return { ...o, segmentThumbnails: nextValue ? true : o.segmentThumbnails, segmentPreviews: nextValue, segments: false };
      }
      return { ...o, [key]: nextValue };
    });

  const scopedCount = videoIds?.length ?? imageIds?.length ?? audioIds?.length ?? textIds?.length ?? 0;
  const scopedNoun = isVideoScoped ? "video" : isImageScoped ? "image" : isAudioScoped ? "audio" : isTextScoped ? "text" : "item";
  const label = isScoped
    ? `Generate for ${scopedCount} ${scopedNoun}${scopedCount !== 1 ? "s" : ""}`
    : "Generate All";

  const renderGroup = (heading: string, rows: ReadonlyArray<readonly [keyof GenerateOptions, string]>, withDivider = true) => (
    <>
      {withDivider && <div className="border-t border-border my-3" />}
      <h4 className="text-xs font-semibold text-muted uppercase tracking-wider mb-2">{heading}</h4>
      {rows.map(([key, labelText]) => (
        <label key={key} className="flex items-center gap-3 cursor-pointer group">
          <input
            type="checkbox"
            checked={!!opts[key]}
            onChange={() => toggle(key)}
            className="w-4 h-4 rounded border-border accent-accent"
          />
          <span className="text-sm text-foreground group-hover:text-accent">{labelText}</span>
        </label>
      ))}
    </>
  );

  return (
    <EditModal open={open} onClose={onClose} title={title ?? label}>
        {/* Options */}
        <div className="space-y-3">
          <p className="text-sm text-secondary mb-4">Select what to generate:</p>

          {showVideo && renderGroup("Video Content", [
            ["thumbnails", "Thumbnails / Screenshots"],
            ["previews", "Video Previews"],
            ["sprites", "Sprite Sheets"],
            ["segmentThumbnails", "Segment Thumbnails"],
            ["segmentPreviews", "Animated Segment Previews"],
            ["phashes", "Video perceptual hashes"],
            ["md5", "MD5 Checksums"],
          ], false)}

          {showImage && renderGroup("Image Content", [
            ["imageThumbnails", "Image Thumbnails"],
            ["imagePhashes", "Image perceptual hashes"],
          ], showVideo)}

          {showAudio && renderGroup("Audio", [
            ["audioPhashes", "Audio perceptual hashes"],
          ], showVideo || showImage)}

          {showText && renderGroup("Text", [
            ["textPhashes", "Text perceptual hashes"],
          ], showVideo || showImage || showAudio)}

          <div className="border-t border-border my-3" />

          <label className="flex items-center gap-3 cursor-pointer group">
            <input
              type="checkbox"
              checked={!!opts.overwrite}
              onChange={() => toggle("overwrite")}
              className="w-4 h-4 rounded border-border accent-orange-500"
            />
            <span className="text-sm text-orange-400 group-hover:text-orange-300">Overwrite existing</span>
          </label>
        </div>

        {/* Actions */}
        <div className="mt-5 flex items-center justify-end gap-2 border-t border-border pt-4">
          {submitted ? (
            <>
              <div className="flex items-center gap-2 text-sm text-green-400 mr-auto">
                <Check className="w-4 h-4" />
                Job started
              </div>
              {onOpenJobDrawer && (
                <button
                  onClick={() => { onClose(); setSubmitted(false); onOpenJobDrawer(); }}
                  className="px-4 py-2 rounded-lg text-sm font-medium bg-accent hover:bg-accent-hover text-white"
                >
                  View Progress
                </button>
              )}
              <button
                onClick={() => { onClose(); setSubmitted(false); }}
                className="px-4 py-2 rounded-lg text-sm text-secondary hover:text-foreground hover:bg-surface"
              >
                Close
              </button>
            </>
          ) : (
            <>
              <button
                onClick={onClose}
                className="px-4 py-2 rounded-lg text-sm text-secondary hover:text-foreground hover:bg-surface"
              >
                Cancel
              </button>
              <button
                onClick={() => generateMut.mutate()}
                disabled={generateMut.isPending}
                className="px-4 py-2 rounded-lg text-sm font-medium bg-accent hover:bg-accent-hover text-white disabled:opacity-50 flex items-center gap-2"
              >
                {generateMut.isPending && <Loader2 className="w-4 h-4 animate-spin" />}
                Generate
              </button>
            </>
          )}
        </div>
    </EditModal>
  );
}

