import { useQuery } from "@tanstack/react-query";
import { audios, galleries, images, texts, videos } from "../api/client";
import type { FindFilter } from "../api/types";
import { DetailListToolbar, type DetailListToolbarProps } from "./DetailListToolbar";
import { MediaAggregateMetadata } from "./MediaAggregateMetadata";

type MediaType = "videos" | "images" | "galleries" | "audios" | "texts";

interface Props extends DetailListToolbarProps {
  mediaType: MediaType;
  aggregateObjectFilter: Record<string, unknown>;
  selectedIds: ReadonlySet<number>;
}

function aggregate(
  mediaType: MediaType,
  request: { findFilter?: FindFilter; objectFilter?: Record<string, unknown>; ids?: number[] },
): Promise<{ count: number; fileSize: number; duration?: number }> {
  switch (mediaType) {
    case "videos":
      return videos.aggregate(request);
    case "images":
      return images.aggregate(request);
    case "galleries":
      return galleries.aggregate(request);
    case "audios":
      return audios.aggregate(request);
    case "texts":
      return texts.aggregate(request);
  }
}

/** Uses the same aggregate endpoints as top-level lists, with the parent relation included. */
export function MediaDetailListToolbar({ mediaType, aggregateObjectFilter, selectedIds, ...props }: Props) {
  const aggregateFilter = { q: props.filter.q, page: 1, perPage: 0 };
  const filtered = useQuery({
    queryKey: [mediaType, "aggregate", "detail", aggregateFilter, aggregateObjectFilter],
    queryFn: () =>
      aggregate(mediaType, {
        findFilter: aggregateFilter,
        objectFilter: aggregateObjectFilter,
      }),
  });
  const ids = [...selectedIds].sort((left, right) => left - right);
  const selected = useQuery({
    queryKey: [mediaType, "aggregate", "selection", ids],
    queryFn: () =>
      mediaType === "videos" ? videos.aggregate({ objectFilter: { ids } }) : aggregate(mediaType, { ids }),
    enabled: ids.length > 0,
  });

  const metadata = (query: typeof filtered) =>
    query.isError ? (
      <button type="button" className="text-xs text-muted" onClick={() => void query.refetch()}>
        Retry totals
      </button>
    ) : (
      <MediaAggregateMetadata
        loading={query.isPending}
        fileSize={query.data?.fileSize}
        duration={query.data?.duration}
      />
    );

  return (
    <DetailListToolbar
      {...props}
      metadataByline={metadata(filtered)}
      selectionMetadata={ids.length > 0 ? metadata(selected) : undefined}
    />
  );
}
