import { useCallback, useMemo, useState } from "react";
import type { FindFilter, Image, PaginatedResponse } from "../api/types";
import type { LightboxImage, LightboxProps } from "../components/Lightbox";
import { extendLightboxPageBounds } from "../utils/lightboxPagination";

interface UsePaginatedImageLightboxOptions {
  items: Image[];
  filter: FindFilter;
  totalCount: number;
  infinitePageSize: boolean;
  queryPage: (filter: FindFilter) => Promise<PaginatedResponse<Image>>;
  toLightboxImage: (image: Image) => LightboxImage;
}

export function usePaginatedImageLightbox({
  items,
  filter,
  totalCount,
  infinitePageSize,
  queryPage,
  toLightboxImage,
}: UsePaginatedImageLightboxOptions) {
  const [open, setOpen] = useState(false);
  const [initialIndex, setInitialIndex] = useState(0);
  const [autoPlay, setAutoPlay] = useState(false);
  const [scopeItems, setScopeItems] = useState<Image[] | null>(null);
  const [pageBounds, setPageBounds] = useState(() => ({ first: filter.page ?? 1, last: filter.page ?? 1 }));
  const sourceItems = scopeItems ?? items;
  const lightboxImages = useMemo(() => sourceItems.map(toLightboxImage), [sourceItems, toLightboxImage]);

  const openImage = useCallback((imageId: number) => {
    setScopeItems(null);
    setPageBounds({ first: filter.page ?? 1, last: filter.page ?? 1 });
    setAutoPlay(false);
    setInitialIndex(Math.max(0, items.findIndex((image) => image.id === imageId)));
    setOpen(true);
  }, [filter.page, items]);

  const openScope = useCallback((images: Image[], shouldAutoPlay = images.length > 1) => {
    if (images.length === 0) return;
    setScopeItems(images);
    setInitialIndex(0);
    setAutoPlay(shouldAutoPlay);
    setOpen(true);
  }, []);

  const close = useCallback(() => {
    setOpen(false);
    setAutoPlay(false);
    setScopeItems(null);
  }, []);

  const loadPage = useCallback(async (page: number, direction: "previous" | "next") => {
    const response = await queryPage({ ...filter, page });
    setPageBounds((bounds) => extendLightboxPageBounds(bounds, page, direction));
    return response.items.map(toLightboxImage);
  }, [filter, queryPage, toLightboxImage]);

  const lightboxProps: Pick<LightboxProps, "images" | "initialIndex" | "open" | "onClose" | "autoPlay" | "hasPrevious" | "hasNext" | "loadPrevious" | "loadNext" | "wrap"> = {
    images: lightboxImages,
    initialIndex,
    open,
    onClose: close,
    autoPlay,
    hasPrevious: scopeItems === null && !infinitePageSize && pageBounds.first > 1,
    hasNext: scopeItems === null && !infinitePageSize && pageBounds.last * (filter.perPage ?? 40) < totalCount,
    loadPrevious: () => loadPage(pageBounds.first - 1, "previous"),
    loadNext: () => loadPage(pageBounds.last + 1, "next"),
    wrap: scopeItems !== null || infinitePageSize,
  };

  return { openImage, openScope, lightboxProps };
}
