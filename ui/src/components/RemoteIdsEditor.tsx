import { useMemo } from "react";
import type { MetadataServer } from "../api/types";
import { useAppConfig } from "../state/AppConfigContext";

export interface RemoteIdValue {
  endpoint: string;
  remoteId: string;
}

interface RemoteIdsEditorProps {
  value: RemoteIdValue[];
  onChange: (value: RemoteIdValue[]) => void;
  metadataServers?: MetadataServer[];
}

export function normalizeRemoteIds(value: RemoteIdValue[]) {
  return value
    .map((item) => ({ endpoint: item.endpoint.trim(), remoteId: item.remoteId.trim() }))
    .filter((item) => item.endpoint.length > 0 && item.remoteId.length > 0);
}

export function RemoteIdsEditor({ value, onChange, metadataServers }: RemoteIdsEditorProps) {
  const { config } = useAppConfig();
  const serverOptions = metadataServers ?? config?.scraping?.metadataServers ?? [];
  const rows = value.length > 0 ? value : [{ endpoint: "", remoteId: "" }];
  const endpointOptions = useMemo(() => {
    const byEndpoint = new Map<string, { endpoint: string; label: string }>();
    for (const server of serverOptions) {
      const endpoint = server.endpoint.trim();
      if (endpoint) byEndpoint.set(endpoint, { endpoint, label: server.name?.trim() || endpoint });
    }
    for (const row of rows) {
      const endpoint = row.endpoint.trim();
      if (endpoint && !byEndpoint.has(endpoint)) byEndpoint.set(endpoint, { endpoint, label: endpoint });
    }
    return Array.from(byEndpoint.values());
  }, [rows, serverOptions]);
  const setRow = (index: number, next: RemoteIdValue) =>
    onChange(rows.map((row, candidateIndex) => (candidateIndex === index ? next : row)));
  const removeRow = (index: number) => onChange(rows.filter((_, candidateIndex) => candidateIndex !== index));
  const addRow = () => onChange([...rows, { endpoint: endpointOptions[0]?.endpoint ?? "", remoteId: "" }]);

  return (
    <div className="space-y-2">
      {rows.map((row, index) => (
        <div key={index} className="grid gap-2 sm:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)_auto]">
          <select
            value={row.endpoint}
            onChange={(event) => setRow(index, { ...row, endpoint: event.target.value })}
            className="min-w-0 rounded border border-border bg-card px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent"
          >
            <option value="">Select metadata server</option>
            {endpointOptions.map((option) => (
              <option key={option.endpoint} value={option.endpoint}>
                {option.label}
              </option>
            ))}
          </select>
          <input
            type="text"
            value={row.remoteId}
            onChange={(event) => setRow(index, { ...row, remoteId: event.target.value })}
            placeholder="Remote ID"
            className="min-w-0 rounded border border-border bg-card px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent"
          />
          <button
            type="button"
            onClick={() => removeRow(index)}
            className="rounded px-2 py-1 text-sm text-muted transition hover:bg-card hover:text-red-300"
            title="Remove remote ID"
          >
            x
          </button>
        </div>
      ))}
      <button type="button" onClick={addRow} className="text-xs text-accent hover:text-accent-hover">
        + Add remote ID
      </button>
    </div>
  );
}
