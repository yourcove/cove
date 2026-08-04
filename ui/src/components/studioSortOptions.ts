export const STUDIO_SORT_OPTIONS: { value: string; label: string }[] = [
  { value: "name", label: "Name" },
  { value: "rating", label: "Rating" },
  { value: "video_count", label: "Video Count" },
  { value: "gallery_count", label: "Gallery Count" },
  { value: "image_count", label: "Image Count" },
  { value: "latest_video_date", label: "Latest Video Date" },
  { value: "total_file_size", label: "Total File Size" },
  { value: "parent_count", label: "Parent Studio Count" },
  { value: "child_count", label: "Substudios Count" },
  { value: "tag_count", label: "Tag Count" },
  { value: "updated_at", label: "Updated At" },
  { value: "random", label: "Random" },
  { value: "created_at", label: "Created At" },
];

export const STUDIO_MULTI_SORT_KEYS = STUDIO_SORT_OPTIONS
  .map((option) => option.value)
  .filter((key) => key !== "random");
