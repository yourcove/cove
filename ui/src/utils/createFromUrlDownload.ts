import { system } from "../api/client";
import type { DownloaderMatch } from "../api/types";
import { loadScrapeApplyPreferences } from "../components/videoScrapeUtils";

export type UrlDownloadMode = "now" | "later";
export type DownloadEntityName = "Audio" | "Image" | "Video" | "Text";

export class NoDownloaderFoundError extends Error {
  readonly url: string;

  constructor(url: string) {
    super("No downloader found for this URL.");
    this.name = "NoDownloaderFoundError";
    this.url = url;
  }
}

export function mergeUrlLists(urls: string[] | undefined, extraUrls: Array<string | null | undefined> = []) {
  return Array.from(new Set([...(urls ?? []), ...extraUrls].map((item) => item?.trim()).filter(Boolean) as string[]));
}

export function pickDownloaderMatch(matches: DownloaderMatch[], entity: DownloadEntityName) {
  return matches.find((item) => item.supportedEntity.toLowerCase() === entity.toLowerCase());
}

export async function createFromUrlWithOptionalDownload<
  TCreate extends { title?: string; urls?: string[] },
  TEntity extends { id?: number },
>({
  requestedUrl,
  data,
  entity,
  downloadMode,
  scrapeMetadata = false,
  create,
}: {
  requestedUrl: string;
  data: TCreate;
  entity: DownloadEntityName;
  downloadMode: UrlDownloadMode;
  scrapeMetadata?: boolean;
  create: (data: TCreate) => Promise<TEntity>;
}) {
  const trimmedUrl = requestedUrl.trim();

  if (downloadMode === "later") {
    return create({ ...data, urls: mergeUrlLists(data.urls, [trimmedUrl]) } as TCreate);
  }

  const matches = await system.matchDownloaders({ url: trimmedUrl });
  const match = pickDownloaderMatch(matches, entity);
  if (!match) throw new NoDownloaderFoundError(trimmedUrl);

  const normalizedUrl = match.normalizedUrl || trimmedUrl;
  const created = await create({
    ...data,
    title: data.title || match.label || undefined,
    urls: mergeUrlLists(data.urls, [trimmedUrl, normalizedUrl, match.sourceUrl]),
  } as TCreate);

  await system.startDownload({
    downloaderId: match.downloaderId,
    url: normalizedUrl,
    entity,
    entityId: created.id,
    qualityId: match.qualityOptions[0]?.id,
    autoApplyMetadata: scrapeMetadata,
    ...(scrapeMetadata ? buildMetadataApplyOptions() : {}),
    sourceUrl: match.sourceUrl ?? undefined,
  });

  return created;
}

function buildMetadataApplyOptions() {
  const preferences = loadScrapeApplyPreferences();
  return {
    createMissingTags: preferences.createMissingTags,
    createMissingPerformers: preferences.createMissingPerformers,
    createMissingStudio: preferences.createMissingStudio,
    markOrganized: preferences.markOrganized,
  };
}
