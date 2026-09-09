import { useCallback } from "react";
import type { FindFilter, PaginatedResponse, Video } from "../api/types";
import { videos } from "../api/client";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import { useOptionalVideoQueue } from "../state/VideoQueueContext";

interface UseVideoQueueNavigationOptions {
  items: Video[];
  filter: FindFilter;
  totalCount: number;
  infinitePageSize: boolean;
  queryPage: (filter: FindFilter) => Promise<PaginatedResponse<Video>>;
  onNavigate: (route: any) => void;
}

function toQueueItem(video: Video) {
  return {
    id: video.id,
    title: video.title || video.files[0]?.basename || `Video ${video.id}`,
    subtitle: video.studioName || video.date || undefined,
    imagePath: videos.screenshotUrl(video.id, video.updatedAt),
  };
}

/** Opens a video with a queue that follows the exact list query the user opened it from. */
export function useVideoQueueNavigation({
  items,
  filter,
  totalCount,
  infinitePageSize,
  queryPage,
  onNavigate,
}: UseVideoQueueNavigationOptions) {
  const appConfig = useOptionalAppConfig();
  const videoQueue = useOptionalVideoQueue();
  const setQueue = videoQueue?.setQueue;
  const autoplay = appConfig?.config?.ui.continuePlaylistDefault ?? false;

  const openVideo = useCallback(
    (videoId: number) => {
      const ids = items.map((video) => video.id);
      if (ids.length > 0 && setQueue) {
        const pageSize = filter.perPage ?? 40;
        let firstPage = filter.page ?? 1;
        let lastPage = firstPage;
        setQueue(
          ids,
          videoId,
          items.map(toQueueItem),
          !infinitePageSize
            ? {
                autoplay,
                startIndex: (firstPage - 1) * pageSize,
                totalCount,
                loadPrevious:
                  firstPage > 1
                    ? async () => {
                        const page = firstPage - 1;
                        const response = await queryPage({ ...filter, page });
                        firstPage = page;
                        return { items: response.items.map(toQueueItem), hasMore: page > 1 };
                      }
                    : undefined,
                loadNext:
                  lastPage * pageSize < totalCount
                    ? async () => {
                        const page = lastPage + 1;
                        const response = await queryPage({ ...filter, page });
                        lastPage = page;
                        return {
                          items: response.items.map(toQueueItem),
                          hasMore: page * pageSize < response.totalCount,
                        };
                      }
                    : undefined,
              }
            : { autoplay },
        );
      }
      onNavigate({ page: "video", id: videoId });
    },
    [autoplay, filter, infinitePageSize, items, onNavigate, queryPage, setQueue, totalCount],
  );

  const navigateFromList = useCallback(
    (route: any) => {
      if (route?.page === "video" && typeof route.id === "number") {
        openVideo(route.id);
        return;
      }
      onNavigate(route);
    },
    [onNavigate, openVideo],
  );

  return { openVideo, navigateFromList };
}
