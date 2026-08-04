export const AUDIO_SORT_OPTIONS: { value: string; label: string }[] = [
  { value: "updatedAt", label: "Updated At" },
  { value: "createdAt", label: "Created At" },
  { value: "date", label: "Date" },
  { value: "duration", label: "Duration" },
  { value: "rating", label: "Rating" },
  { value: "play_count", label: "Play Count" },
  { value: "like_counter", label: "Likes" },
  { value: "play_duration", label: "Play Duration" },
  { value: "last_played_at", label: "Last Played" },
  { value: "file_size", label: "File Size" },
  { value: "file_mod_time", label: "File Modified" },
  { value: "file_count", label: "File Count" },
  { value: "path", label: "Path" },
  { value: "bitrate", label: "Bitrate" },
  { value: "has_video_files", label: "Has Video Track" },
  { value: "track_count", label: "Track Count" },
  { value: "tag_count", label: "Tag Count" },
  { value: "performer_count", label: "Performer Count" },
  { value: "title", label: "Title" },
  { value: "random", label: "Random" },
];

export const AUDIO_MULTI_SORT_KEYS = AUDIO_SORT_OPTIONS
  .map((option) => option.value)
  .filter((key) => !["rating", "play_count", "like_counter", "play_duration", "last_played_at", "random"].includes(key));
