import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { metadata, type GenerateOptions } from "../api/client";
import { Loader2, Check } from "lucide-react";
import { EditModal } from "./EditModal";
import { GenerateOptionList, GenerateRunSummary, type GenerateOptionGroup } from "./GenerateOptionList";

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

export function GenerateDialog({
  open,
  onClose,
  onOpenJobDrawer,
  videoIds,
  imageIds,
  audioIds,
  textIds,
  title,
}: Props) {
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
    vrStereo: false,
    segmentCovers: false,
    phashes: false,
    md5: false,
    imageThumbnails: isImageScoped,
    imagePhashes: false,
    audioPhashes: isAudioScoped,
    textPhashes: isTextScoped,
    overwrite: false,
  });
  const [submitted, setSubmitted] = useState(false);

  // Counted for the dialog's scope whatever is ticked.
  const estimateQ = useQuery({
    queryKey: ["generate-estimate", "dialog", videoIds ?? null, imageIds ?? null, audioIds ?? null, textIds ?? null],
    queryFn: ({ signal }) => metadata.estimateGenerate({ videoIds, imageIds, audioIds, textIds }, signal),
    enabled: open,
    staleTime: 30_000,
    refetchOnWindowFocus: false,
    retry: false,
  });

  const generateMut = useMutation({
    mutationFn: () => metadata.generate({ ...opts, videoIds, imageIds, audioIds, textIds }),
    onSuccess: () => {
      setSubmitted(true);
    },
  });

  if (!open) return null;

  const scopedCount = videoIds?.length ?? imageIds?.length ?? audioIds?.length ?? textIds?.length ?? 0;
  const scopedNoun = isVideoScoped
    ? "video"
    : isImageScoped
      ? "image"
      : isAudioScoped
        ? "audio"
        : isTextScoped
          ? "text"
          : "item";
  const label = isScoped ? `Generate for ${scopedCount} ${scopedNoun}${scopedCount !== 1 ? "s" : ""}` : "Generate All";

  const groups: GenerateOptionGroup[] = [
    ...(showVideo
      ? [
          {
            title: "Video content",
            keys: ["thumbnails", "previews", "sprites", "vrStereo", "segmentCovers", "phashes", "md5"] as const,
          },
        ]
      : []),
    ...(showImage ? [{ title: "Image content", keys: ["imageThumbnails", "imagePhashes"] as const }] : []),
    ...(showAudio ? [{ title: "Audio", keys: ["audioPhashes"] as const }] : []),
    ...(showText ? [{ title: "Text", keys: ["textPhashes"] as const }] : []),
  ].map((group) => ({ title: group.title, keys: [...group.keys] }));
  const estimatePending = estimateQ.isFetching && !estimateQ.data;

  return (
    <EditModal open={open} onClose={onClose} title={title ?? label}>
      {/* Options */}
      <div className="space-y-3">
        <GenerateOptionList
          idPrefix="generate-dialog"
          groups={groups}
          sizing="run"
          options={opts}
          estimate={estimateQ.data}
          estimatePending={estimatePending}
          isChecked={(key) => !!opts[key]}
          onCheckedChange={(key, checked) => setOpts((current) => ({ ...current, [key]: checked }))}
          onSegmentCoverKindsChange={(kinds) => setOpts((current) => ({ ...current, segmentCoverKinds: kinds }))}
        />

        {estimateQ.isError ? (
          <p className="px-2.5 text-xs text-red-400">
            Could not count what is missing, so sizes show the work for one item only.
          </p>
        ) : null}

        <div className="border-t border-border my-3" />

        <label className="flex items-center gap-3 cursor-pointer group px-2.5">
          <input
            type="checkbox"
            checked={!!opts.overwrite}
            onChange={() => setOpts((current) => ({ ...current, overwrite: !current.overwrite }))}
            className="w-4 h-4 rounded border-border accent-orange-500"
          />
          <span className="text-sm text-orange-400 group-hover:text-orange-300">Make again what is already made</span>
        </label>

        <GenerateRunSummary
          keys={groups.flatMap((group) => group.keys)}
          options={opts}
          estimate={estimateQ.data}
          estimatePending={estimatePending}
        />
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
                onClick={() => {
                  onClose();
                  setSubmitted(false);
                  onOpenJobDrawer();
                }}
                className="px-4 py-2 rounded-lg text-sm font-medium bg-accent hover:bg-accent-hover text-white"
              >
                View Progress
              </button>
            )}
            <button
              onClick={() => {
                onClose();
                setSubmitted(false);
              }}
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
