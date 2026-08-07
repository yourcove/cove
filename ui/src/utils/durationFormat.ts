export function formatHumanDuration(value: unknown): string {
  if (typeof value !== "number" || !Number.isFinite(value)) return "";

  const roundedSeconds = Math.round(value);
  const sign = roundedSeconds < 0 ? "-" : "";
  const totalSeconds = Math.abs(roundedSeconds);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const parts: string[] = [];

  if (hours > 0) parts.push(`${hours} hr`);
  if (minutes > 0) parts.push(`${minutes} min`);
  if (seconds > 0 || parts.length === 0) parts.push(`${seconds} sec`);

  return `${sign}${parts.join(" ")}`;
}
