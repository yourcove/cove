import { useCallback } from "react";
import type { FindFilter, PaginatedResponse, Video } from "../api/types";
import { videos } from "../api/client";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import { useOptionalVideoQueue } from "../state/VideoQueueContext";

interface UseVideoQueueNavigationOptions {
  items: Video[];
  filter: FindFilter;
  /** The list total, or undefined while it is still loading. */
  totalCount: number | undefined;
  infinitePageSize: boolean;
  queryPage: (filter: FindFilter) => Promise<PaginatedResponse<Video>>;
  onNavigate: (route: any) => void;
}

/** How a video appears in the play queue: its title or first file name, studio or date, and cover. */
export function videoQueueItem(video: {
  id: number;
  title?: string | null;
  files?: ReadonlyArray<{ basename?: string | null }>;
  studioName?: string | null;
  date?: string | null;
  updatedAt?: string;
}) {
  return {
    id: video.id,
    title: video.title || video.files?.[0]?.basename || `Video ${video.id}`,
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
          items.map(videoQueueItem),
          !infinitePageSize
            ? {
                autoplay,
                startIndex: (firstPage - 1) * pageSize,
                // The videos through this page are a lower bound for the queue: the total may still be loading,
                // or come from a separate count taken before videos were added.
                totalCount: Math.max(totalCount ?? 0, (firstPage - 1) * pageSize + ids.length),
                loadPrevious:
                  firstPage > 1
                    ? async () => {
                        const page = firstPage - 1;
                        const response = await queryPage({ ...filter, page });
                        firstPage = page;
                        return {
                          items: response.items.map(videoQueueItem),
                          hasMore: page > 1,
                          totalCount: response.totalCount,
                        };
                      }
                    : undefined,
                // Without a total yet, a full page may have more after it; an empty next page ends the queue.
                loadNext: (totalCount === undefined ? items.length >= pageSize : lastPage * pageSize < totalCount)
                  ? async () => {
                      const page = lastPage + 1;
                      const response = await queryPage({ ...filter, page });
                      lastPage = page;
                      return {
                        items: response.items.map(videoQueueItem),
                        hasMore: page * pageSize < response.totalCount,
                        totalCount: response.totalCount,
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
