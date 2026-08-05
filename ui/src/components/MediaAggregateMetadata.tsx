import { formatFileSize } from "./shared";

interface Props {
  loading: boolean;
  duration?: number;
  fileSize?: number;
}

export function MediaAggregateMetadata({ loading, duration, fileSize }: Props) {
  if (loading) return <span className="text-xs text-muted">Calculating…</span>;

  const values = [
    duration == null ? null : formatAggregateDuration(duration),
    fileSize == null ? null : formatFileSize(fileSize),
  ].filter((value): value is string => value != null);

  return values.length > 0
    ? <span className="whitespace-nowrap text-xs text-muted">{values.join(" · ")}</span>
    : null;
}

export function formatAggregateDuration(totalSeconds: number) {
  const seconds = Math.max(0, Math.round(totalSeconds));
  const days = Math.floor(seconds / 86_400);
  const hours = Math.floor((seconds % 86_400) / 3_600);
  const minutes = Math.floor((seconds % 3_600) / 60);
  const remainder = seconds % 60;
  if (days > 0) return `${days}d ${hours}h ${minutes}m`;
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m ${remainder}s`;
  return `${remainder}s`;
}
