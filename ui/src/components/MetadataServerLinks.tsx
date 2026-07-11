import { ExternalLink } from "lucide-react";

type MetadataEntityType = "scenes" | "performers" | "studios" | "tags";

interface RemoteId {
  endpoint: string;
  remoteId: string;
}

interface MetadataServer {
  endpoint: string;
  name?: string;
}

export function metadataServerEntityUrl(endpoint: string, entityType: MetadataEntityType, remoteId: string): string | null {
  try {
    const url = new URL(endpoint);
    if (url.protocol !== "http:" && url.protocol !== "https:") return null;
    const graphqlPath = url.pathname.match(/^(.*\/)?graphql\/?$/i);
    if (!graphqlPath) return null;
    url.pathname = `${graphqlPath[1] ?? "/"}${entityType}/${encodeURIComponent(remoteId)}`;
    url.search = "";
    url.hash = "";
    return url.toString();
  } catch {
    return null;
  }
}

function metadataServerLabel(endpoint: string, servers: MetadataServer[]): string {
  const normalizedEndpoint = endpoint.trim().replace(/\/$/, "").toLowerCase();
  const configuredName = servers.find((server) => server.endpoint.trim().replace(/\/$/, "").toLowerCase() === normalizedEndpoint)?.name?.trim();
  if (configuredName) return configuredName;
  try {
    return new URL(endpoint).hostname.replace(/^www\./i, "");
  } catch {
    return endpoint;
  }
}

export function MetadataServerLinks({ remoteIds, entityType, metadataServers = [], className = "contents" }: { remoteIds?: RemoteId[]; entityType: MetadataEntityType; metadataServers?: MetadataServer[]; className?: string }) {
  const links = (remoteIds ?? []).flatMap((remoteId) => {
    const href = metadataServerEntityUrl(remoteId.endpoint, entityType, remoteId.remoteId);
    return href ? [{ ...remoteId, href, label: metadataServerLabel(remoteId.endpoint, metadataServers) }] : [];
  });

  if (links.length === 0) return null;

  return (
    <div className={className}>
      {links.map((link) => (
        <a
          key={`${link.endpoint}-${link.remoteId}`}
          href={link.href}
          target="_blank"
          rel="noopener noreferrer"
          className="inline-flex items-center gap-1.5 rounded-full border border-border bg-card px-3 py-1 text-xs text-accent transition hover:border-accent/60 hover:text-accent-hover"
          title={`Open ${link.label} metadata page`}
          aria-label={`Open ${link.label} metadata page`}
        >
          <ExternalLink className="h-3 w-3" />
          <span>{link.label}</span>
        </a>
      ))}
    </div>
  );
}
