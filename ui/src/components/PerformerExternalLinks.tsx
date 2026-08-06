import { ExternalLink, MoreHorizontal } from "lucide-react";
import { useState } from "react";
import type { MetadataServer, PerformerRemoteId } from "../api/types";
import { MetadataServerLinks } from "./MetadataServerLinks";

const COLLAPSED_LINK_COUNT = 4;

interface Props {
  remoteIds: PerformerRemoteId[];
  urls: string[];
  metadataServers?: Pick<MetadataServer, "endpoint" | "name">[];
}

export function PerformerExternalLinks({ remoteIds, urls, metadataServers }: Props) {
  const [expanded, setExpanded] = useState(false);
  const hasMore = remoteIds.length + urls.length > COLLAPSED_LINK_COUNT;
  const visibleRemoteIds = expanded ? remoteIds : remoteIds.slice(0, COLLAPSED_LINK_COUNT);
  const visibleUrls = expanded
    ? urls
    : urls.slice(0, Math.max(0, COLLAPSED_LINK_COUNT - remoteIds.length));

  return (
    <>
      <div className="flex flex-wrap gap-2">
        <MetadataServerLinks
          remoteIds={visibleRemoteIds}
          entityType="performers"
          metadataServers={metadataServers}
        />
        {visibleUrls.map((url, index) => (
          <a key={`${url}-${index}`} href={url} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1.5 rounded-full border border-border bg-card px-3 py-1 text-xs text-accent hover:border-accent/60 hover:text-accent-hover">
            <ExternalLink className="h-3 w-3" />
            {formatUrlLabel(url)}
          </a>
        ))}
      </div>
      {hasMore ? (
        <button
          type="button"
          onClick={() => setExpanded((value) => !value)}
          className="inline-flex items-center rounded-full border border-border bg-card px-3 py-1 text-xs text-secondary hover:border-accent/60 hover:text-foreground"
          aria-expanded={expanded}
          aria-label={expanded ? "Show fewer URLs" : "Show all URLs"}
        >
          {expanded ? "Show less" : <MoreHorizontal className="h-4 w-4" />}
        </button>
      ) : null}
    </>
  );
}

function formatUrlLabel(url: string): string {
  try {
    return new URL(url).hostname.replace("www.", "");
  } catch {
    return url;
  }
}
