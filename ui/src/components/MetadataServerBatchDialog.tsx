import { useEffect, useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { CloudDownload, Loader2, RefreshCw, X } from "lucide-react";
import { performers, studios, tags } from "../api/client";
import { useAppConfig } from "../state/AppConfigContext";

type MetadataBatchEntity = "performer" | "studio" | "tag";

interface Props {
  open: boolean;
  entityType: MetadataBatchEntity;
  selectedIds: number[];
  onClose: () => void;
  onQueued?: () => void;
}

const ENTITY_COPY: Record<MetadataBatchEntity, { singular: string; plural: string }> = {
  performer: { singular: "performer", plural: "performers" },
  studio: { singular: "studio", plural: "studios" },
  tag: { singular: "tag", plural: "tags" },
};

const EXCLUDE_FIELD_OPTIONS: Record<MetadataBatchEntity, Array<{ id: string; label: string }>> = {
  performer: [
    { id: "name", label: "Name" },
    { id: "disambiguation", label: "Disambiguation" },
    { id: "gender", label: "Gender" },
    { id: "birthdate", label: "Birth date" },
    { id: "deathdate", label: "Death date" },
    { id: "country", label: "Country" },
    { id: "ethnicity", label: "Ethnicity" },
    { id: "eyecolor", label: "Eye color" },
    { id: "haircolor", label: "Hair color" },
    { id: "height", label: "Height" },
    { id: "measurements", label: "Measurements" },
    { id: "faketits", label: "Fake tits" },
    { id: "career", label: "Career dates" },
    { id: "tattoos", label: "Tattoos" },
    { id: "piercings", label: "Piercings" },
    { id: "aliases", label: "Aliases" },
    { id: "urls", label: "URLs" },
    { id: "image", label: "Image" },
  ],
  studio: [
    { id: "name", label: "Name" },
    { id: "aliases", label: "Aliases" },
    { id: "urls", label: "URLs" },
    { id: "parent", label: "Parent studio" },
    { id: "image", label: "Image" },
  ],
  tag: [
    { id: "name", label: "Name" },
    { id: "description", label: "Description" },
    { id: "aliases", label: "Aliases" },
  ],
};

export function MetadataServerBatchDialog({ open, entityType, selectedIds, onClose, onQueued }: Props) {
  const queryClient = useQueryClient();
  const { config } = useAppConfig();
  const metadataServers = config?.scraping.metadataServers ?? [];
  const batchDefaults = config?.scraping.metadataBatchDefaults;

  const [endpoint, setEndpoint] = useState("");
  const [refreshAlreadyTagged, setRefreshAlreadyTagged] = useState(false);
  const [createParentStudios, setCreateParentStudios] = useState(true);
  const [excludeFields, setExcludeFields] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);

  const selectedServer = metadataServers.find((server) => server.endpoint === endpoint);
  const estimatedRequests = selectedIds.length;
  const estimatedMinutes = selectedServer
    ? Math.max(1, Math.ceil(estimatedRequests / Math.max(selectedServer.maxRequestsPerMinute, 1)))
    : null;

  useEffect(() => {
    if (!open) {
      return;
    }

    const firstEndpoint = metadataServers[0]?.endpoint ?? "";
    const allowedFieldIds = new Set(EXCLUDE_FIELD_OPTIONS[entityType].map((option) => option.id));
    setEndpoint((current) => (metadataServers.some((item) => item.endpoint === current) ? current : firstEndpoint));
    setRefreshAlreadyTagged(batchDefaults?.refreshAlreadyTagged ?? false);
    setCreateParentStudios(batchDefaults?.createParentStudios ?? true);
    setExcludeFields((batchDefaults?.excludeFields ?? []).filter((field) => allowedFieldIds.has(field)));
    setError(null);
  }, [open, metadataServers, entityType, batchDefaults]);

  const entityCopy = ENTITY_COPY[entityType];
  const fieldOptions = useMemo(() => EXCLUDE_FIELD_OPTIONS[entityType], [entityType]);

  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async () => {
      if (!endpoint.trim()) {
        throw new Error("Choose a MetadataServer endpoint.");
      }

      if (selectedIds.length === 0) {
        throw new Error(`Select at least one ${entityCopy.singular}.`);
      }

      const request = {
        endpoint,
        ids: selectedIds,
        refreshAlreadyTagged,
        excludeFields: excludeFields.length > 0 ? excludeFields : undefined,
      };

      switch (entityType) {
        case "performer":
          return performers.batchTagMetadataServer(request);
        case "studio":
          return studios.batchTagMetadataServer({ ...request, createParentStudios });
        case "tag":
          return tags.batchTagMetadataServer(request);
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["jobs"] });
      onQueued?.();
      onClose();
    },
    onError: (mutationError: Error) => {
      setError(mutationError.message || "Failed to queue MetadataServer tagging.");
    },
  });

  if (!open) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4">
      <div className="w-full max-w-2xl overflow-hidden rounded-2xl border border-border bg-surface shadow-2xl">
        <div className="flex items-start justify-between border-b border-border px-5 py-4">
          <div>
            <h2 className="flex items-center gap-2 text-lg font-bold text-foreground">
              <CloudDownload className="h-5 w-5 text-accent" />
              Batch Tag {entityCopy.plural}
            </h2>
            <p className="mt-0.5 text-xs text-secondary">
              Queue MetadataServer tagging for {selectedIds.length} selected {entityCopy.plural}.
            </p>
          </div>
          <button onClick={onClose} className="text-muted hover:text-foreground" aria-label="Close MetadataServer batch dialog">
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="space-y-5 px-5 py-4">
          <div className="space-y-2">
            <label className="block text-sm font-medium text-foreground">MetadataServer endpoint</label>
            {metadataServers.length === 0 ? (
              <div className="rounded-xl border border-dashed border-border bg-card/50 px-4 py-5 text-sm text-muted">
                No MetadataServer endpoints are configured. Add one in Settings before queuing batch tagging.
              </div>
            ) : (
              <select
                value={endpoint}
                onChange={(event) => setEndpoint(event.target.value)}
                className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground outline-none"
              >
                {metadataServers.map((server) => (
                  <option key={server.endpoint} value={server.endpoint}>
                    {server.name || server.endpoint}
                  </option>
                ))}
              </select>
            )}
            {selectedServer ? (
              <div className="rounded-xl border border-border bg-card/50 px-4 py-3 text-xs text-muted">
                Estimated remote lookups: {estimatedRequests}. At {selectedServer.maxRequestsPerMinute} requests/minute this batch should take about {estimatedMinutes} minute{estimatedMinutes === 1 ? "" : "s"}.
              </div>
            ) : null}
          </div>

          <div className="space-y-2">
            <label className="flex items-center gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={refreshAlreadyTagged}
                onChange={(event) => setRefreshAlreadyTagged(event.target.checked)}
                className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
              />
              Refresh entities that already have MetadataServer links
            </label>

            {entityType === "studio" ? (
              <label className="flex items-center gap-2 text-sm text-secondary">
                <input
                  type="checkbox"
                  checked={createParentStudios}
                  onChange={(event) => setCreateParentStudios(event.target.checked)}
                  className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                />
                Create missing parent studios from the source hierarchy
              </label>
            ) : null}
          </div>

          <div className="space-y-2">
            <label className="block text-sm font-medium text-foreground">Keep current fields</label>
            <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
              {fieldOptions.map((option) => {
                const selected = excludeFields.includes(option.id);
                return (
                  <label key={option.id} className="flex items-center gap-2 rounded-xl border border-border bg-card px-3 py-2 text-sm text-secondary">
                    <input
                      type="checkbox"
                      checked={selected}
                      onChange={(event) => {
                        setExcludeFields((current) => {
                          if (event.target.checked) {
                            return [...current, option.id];
                          }

                          return current.filter((value) => value !== option.id);
                        });
                      }}
                      className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                    />
                    {option.label}
                  </label>
                );
              })}
            </div>
          </div>

          {error ? (
            <div className="rounded-xl border border-red-800/60 bg-red-950/30 px-3 py-2 text-sm text-red-300">
              {error}
            </div>
          ) : null}
        </div>

        <div className="flex items-center justify-between border-t border-border px-5 py-4">
          <div className="text-xs text-muted">
            This queues a background job. The selected entities remain editable while tagging runs.
          </div>
          <div className="flex items-center gap-2">
            <button onClick={onClose} className="rounded-xl px-4 py-2 text-sm text-secondary hover:text-foreground">
              Cancel
            </button>
            <button
              onClick={() => {
                setError(null);
                mutation.mutate();
              }}
              disabled={mutation.isPending || metadataServers.length === 0 || selectedIds.length === 0}
              className="inline-flex items-center gap-2 rounded-xl bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
            >
              {mutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
              Queue Tagging
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
