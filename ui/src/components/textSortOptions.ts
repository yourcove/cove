export const TEXT_SORT_OPTIONS: { value: string; label: string }[] = [
  { value: "updatedAt", label: "Updated At" },
  { value: "createdAt", label: "Created At" },
  { value: "date", label: "Date" },
  { value: "words", label: "Words" },
  { value: "pages", label: "Pages" },
  { value: "rating", label: "Rating" },
  { value: "read_count", label: "Read Count" },
  { value: "like_counter", label: "Likes" },
  { value: "read_duration", label: "Read Duration" },
  { value: "last_read_at", label: "Last Read" },
  { value: "file_size", label: "File Size" },
  { value: "file_mod_time", label: "File Modified" },
  { value: "file_count", label: "File Count" },
  { value: "path", label: "Path" },
  { value: "tag_count", label: "Tag Count" },
  { value: "performer_count", label: "Performer Count" },
  { value: "title", label: "Title" },
  { value: "random", label: "Random" },
];

export const TEXT_MULTI_SORT_KEYS = TEXT_SORT_OPTIONS
  .map((option) => option.value)
  .filter((key) => key !== "random");
