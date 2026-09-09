import { useCallback } from "react";
import type { FindFilter, Image, PaginatedResponse, Video } from "../api/types";
import { images } from "../api/client";
import { canReadEntity, canWriteEntity } from "../auth/visibility";
import { useAuth } from "../auth/AuthContext";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import { getImageDisplayTitle } from "../utils/imageDisplay";
import { usePaginatedImageLightbox } from "../hooks/usePaginatedImageLightbox";
import { useVideoQueueNavigation } from "../hooks/useVideoQueueNavigation";
import { Lightbox, type LightboxImage } from "./Lightbox";
import { RelatedEntityListView, type RelatedEntityListViewProps } from "./RelatedEntityListView";

interface ContextualListProps<TItem extends { id: number }> {
  items: TItem[];
  filter: FindFilter;
  totalCount: number;
  queryPage: (filter: FindFilter) => Promise<PaginatedResponse<TItem>>;
  onNavigate: (route: any) => void;
}

type ContextualVideoListViewProps = ContextualListProps<Video> &
  Omit<RelatedEntityListViewProps<Video>, "entityType" | "items" | "onNavigate">;

export function ContextualVideoListView({
  items,
  filter,
  totalCount,
  queryPage,
  onNavigate,
  ...listProps
}: ContextualVideoListViewProps) {
  const { navigateFromList } = useVideoQueueNavigation({
    items,
    filter,
    totalCount,
    infinitePageSize: listProps.infinitePageSize,
    queryPage,
    onNavigate,
  });
  return <RelatedEntityListView entityType="videos" items={items} onNavigate={navigateFromList} {...listProps} />;
}

type ContextualImageListViewProps = ContextualListProps<Image> &
  Omit<
    RelatedEntityListViewProps<Image>,
    "entityType" | "items" | "onNavigate" | "onImagePreview" | "onImageDetails"
  > & { interactionSource: string; interactionMeta?: Record<string, unknown> };

export function ContextualImageListView({
  items,
  filter,
  totalCount,
  queryPage,
  onNavigate,
  interactionSource,
  interactionMeta,
  ...listProps
}: ContextualImageListViewProps) {
  const appConfig = useOptionalAppConfig();
  const { hasPermission, user } = useAuth();
  const canEngage = canReadEntity("image", hasPermission) && (user?.kind === "user" || user?.kind === "system");
  const canLike = canWriteEntity("image", hasPermission);
  const toLightboxImage = useCallback(
    (image: Image): LightboxImage => ({
      id: image.id,
      src: images.imageUrl(image.id),
      title: getImageDisplayTitle(image),
      interactionSource,
      interactionMeta,
    }),
    [interactionMeta, interactionSource],
  );
  const lightbox = usePaginatedImageLightbox({
    items,
    filter,
    totalCount,
    infinitePageSize: listProps.infinitePageSize,
    queryPage,
    toLightboxImage,
  });

  return (
    <>
      <RelatedEntityListView
        entityType="images"
        items={items}
        onNavigate={onNavigate}
        onImagePreview={(image) => lightbox.openImage(image.id)}
        onImageDetails={(image) => onNavigate({ page: "image", id: image.id })}
        {...listProps}
      />
      {lightbox.lightboxProps.open ? (
        <Lightbox
          {...lightbox.lightboxProps}
          slideshowDelay={appConfig?.config?.ui.slideshowDelay}
          canEngage={canEngage}
          canLike={canLike}
        />
      ) : null}
    </>
  );
}
