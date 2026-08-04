export const PERFORMER_SORT_OPTIONS: { value: string; label: string }[] = [
  { value: "name", label: "Name" },
  { value: "rating", label: "Rating" },
  { value: "video_count", label: "Video Count" },
  { value: "image_count", label: "Image Count" },
  { value: "gallery_count", label: "Gallery Count" },
  { value: "latest_video_date", label: "Latest Video Date" },
  { value: "total_file_size", label: "Total File Size" },
  { value: "tag_count", label: "Tag Count" },
  { value: "career_length", label: "Career Length" },
  { value: "last_like_at", label: "Last Like At" },
  { value: "last_played_at", label: "Last Played At" },
  { value: "measurements", label: "Measurements" },
  { value: "like_counter", label: "Likes" },
  { value: "play_count", label: "Play Count" },
  { value: "birthdate", label: "Birthdate" },
  { value: "height", label: "Height" },
  { value: "weight", label: "Weight" },
  { value: "created_at", label: "Created At" },
  { value: "updated_at", label: "Updated At" },
  { value: "random", label: "Random" },
];

export const PERFORMER_MULTI_SORT_KEYS = PERFORMER_SORT_OPTIONS
  .map((option) => option.value)
  .filter((key) => !["rating", "career_length", "last_like_at", "last_played_at", "measurements", "like_counter", "play_count", "random"].includes(key));
