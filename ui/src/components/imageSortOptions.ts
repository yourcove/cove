export const IMAGE_SORT_OPTIONS: { value: string; label: string }[] = [
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "date", label: "Date" },
  { value: "file_mod_time", label: "File Modification Time" },
  { value: "file_size", label: "File Size" },
  { value: "resolution", label: "Resolution" },
  { value: "path", label: "Path" },
  { value: "title", label: "Title" },
  { value: "rating", label: "Rating" },
  { value: "like_counter", label: "Likes" },
  { value: "performer_count", label: "Performer Count" },
  { value: "tag_count", label: "Tag Count" },
  { value: "random", label: "Random" },
];

export const IMAGE_MULTI_SORT_KEYS = IMAGE_SORT_OPTIONS
  .map((option) => option.value)
  .filter((key) => key !== "rating" && key !== "like_counter" && key !== "random");
