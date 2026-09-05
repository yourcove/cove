import type { Audio, AudioFile, TextDocument, TextFile } from "../api/types";

export function pickPrimaryAudioFile(audio?: Pick<Audio, "files"> | null): AudioFile | undefined {
  return [...(audio?.files ?? [])].sort(
    (left, right) =>
      Number(right.hasVideoTrack) - Number(left.hasVideoTrack) ||
      right.duration - left.duration ||
      right.size - left.size ||
      left.id - right.id,
  )[0];
}

export function getAudioDisplayTitle(audio: Pick<Audio, "id" | "title" | "files">) {
  const fallbackFile = pickPrimaryAudioFile(audio);
  return audio.title?.trim() || fallbackFile?.basename?.trim() || `Audio ${audio.id}`;
}

export function pickPrimaryTextFile(text?: Pick<TextDocument, "files"> | null): TextFile | undefined {
  return [...(text?.files ?? [])].sort(
    (left, right) =>
      (right.wordCount ?? 0) - (left.wordCount ?? 0) ||
      (right.pageCount ?? 0) - (left.pageCount ?? 0) ||
      right.size - left.size ||
      left.id - right.id,
  )[0];
}

export function getTextDisplayTitle(text: Pick<TextDocument, "id" | "title" | "files">) {
  const fallbackFile = pickPrimaryTextFile(text);
  return text.title?.trim() || fallbackFile?.basename?.trim() || `Text ${text.id}`;
}
