import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, FolderPlus, Layers } from "lucide-react";
import { groups } from "../api/client";
import type { GroupItemKind, SegmentSpanDerivedQuery } from "../api/types";
import { EditModal } from "./EditModal";

export interface AddToGroupEntry {
  key: string;
  videoId?: number;
  hostType?: string;
  hostId?: number;
  kind?: GroupItemKind;
  spanKey?: string;
  startSec?: number;
  endSec?: number;
  title?: string;
  profileId?: number;
  derivedQuery?: SegmentSpanDerivedQuery;
}

interface Props {
  open: boolean;
  onClose: () => void;
  items: AddToGroupEntry[];
  onAdded?: (groupId: number) => void;
}

export function AddToGroupDialog({ open, onClose, items, onAdded }: Props) {
  const queryClient = useQueryClient();
  const [groupSearch, setGroupSearch] = useState("");
  const [selectedGroupId, setSelectedGroupId] = useState<number | null>(null);
  const [newGroupName, setNewGroupName] = useState("");

  const normalizedItems = useMemo(() => items.filter((item) => (item.videoId ?? item.hostId ?? 0) > 0), [items]);
  const existingGroupQuery = useQuery({
    queryKey: ["groups", "picker", groupSearch],
    queryFn: () =>
      groups.find({ page: 1, perPage: 20, sort: "name", direction: "asc", q: groupSearch.trim() || undefined }),
    enabled: open,
  });

  const addMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async () => {
      if (normalizedItems.length === 0) {
        throw new Error("No group-ready items were selected.");
      }

      let groupId = selectedGroupId;
      if (!groupId) {
        const name = newGroupName.trim();
        if (!name) {
          throw new Error("Choose a group or enter a new group name.");
        }

        const created = await groups.create({ name });
        groupId = created.id;
      }

      const spanItems = normalizedItems.filter(
        (item) => item.videoId && (item.spanKey || item.startSec != null || item.endSec != null || item.derivedQuery),
      );
      const directItems = normalizedItems.filter((item) => !spanItems.includes(item));

      if (spanItems.length > 0) {
        await groups.items.fromSpans(groupId, {
          spans: spanItems.map((item) => ({
            spanKey: item.spanKey,
            videoId: item.videoId,
            startSec: item.startSec,
            endSec: item.endSec,
            title: item.title,
            profileId: item.profileId,
            derivedQuery: item.derivedQuery,
          })),
        });
      }

      for (const item of directItems) {
        const hostType = item.hostType ?? (item.videoId ? "video" : undefined);
        const hostId = item.hostId ?? item.videoId;
        if (!hostType || !hostId) continue;
        await groups.items.create(groupId, {
          orderIndex: 1_000_000,
          kind: item.kind ?? getGroupItemKind(hostType),
          hostType,
          hostId,
          videoId: hostType === "video" ? hostId : undefined,
          title: item.title,
        });
      }

      return groupId;
    },
    onSuccess: (groupId) => {
      queryClient.invalidateQueries({ queryKey: ["groups"] });
      queryClient.invalidateQueries({ queryKey: ["group", groupId] });
      queryClient.invalidateQueries({ queryKey: ["group-items", groupId] });
      setSelectedGroupId(null);
      setNewGroupName("");
      setGroupSearch("");
      onClose();
      onAdded?.(groupId);
    },
  });

  const canSubmit =
    normalizedItems.length > 0 && (selectedGroupId != null || newGroupName.trim().length > 0) && !addMutation.isPending;

  return (
    <EditModal title="Add To Group" open={open} onClose={onClose}>
      <div className="space-y-5 py-4">
        <div className="rounded-xl border border-border bg-card/60 p-4">
          <div className="flex items-center gap-2 text-sm font-medium text-foreground">
            <Layers className="h-4 w-4 text-accent" />
            {normalizedItems.length} item{normalizedItems.length === 1 ? "" : "s"} ready to add
          </div>
          <div className="mt-3 flex flex-wrap gap-2 text-xs text-secondary">
            {normalizedItems.slice(0, 6).map((item) => (
              <span key={item.key} className="rounded-full border border-border bg-surface px-2 py-1">
                {item.title || "Untitled video"}
              </span>
            ))}
            {normalizedItems.length > 6 ? (
              <span className="rounded-full border border-border bg-surface px-2 py-1">
                +{normalizedItems.length - 6} more
              </span>
            ) : null}
          </div>
        </div>

        <div>
          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            Choose existing group
          </label>
          <input
            type="text"
            value={groupSearch}
            onChange={(event) => setGroupSearch(event.target.value)}
            placeholder="Search groups..."
            className="mt-2 w-full rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
          />
          <div className="mt-3 max-h-56 space-y-2 overflow-y-auto">
            {(existingGroupQuery.data?.items ?? []).map((group) => {
              const selected = selectedGroupId === group.id;
              return (
                <button
                  key={group.id}
                  type="button"
                  onClick={() => {
                    setSelectedGroupId(group.id);
                    setNewGroupName("");
                  }}
                  className={`flex w-full items-center justify-between rounded-xl border px-3 py-2 text-left text-sm transition-colors ${selected ? "border-accent bg-accent/10 text-foreground" : "border-border bg-card/60 text-secondary hover:border-accent hover:text-foreground"}`}
                >
                  <span>{group.name}</span>
                  {selected ? <Check className="h-4 w-4 text-accent" /> : null}
                </button>
              );
            })}
          </div>
        </div>

        <div className="rounded-xl border border-dashed border-border bg-surface/40 p-4">
          <label className="block text-xs font-semibold uppercase tracking-wide text-muted">
            Or create a new group
          </label>
          <div className="mt-2 flex gap-2">
            <input
              type="text"
              value={newGroupName}
              onChange={(event) => {
                setNewGroupName(event.target.value);
                if (event.target.value.trim().length > 0) {
                  setSelectedGroupId(null);
                }
              }}
              placeholder="Compilation name"
              className="flex-1 rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
            />
            <div className="inline-flex items-center rounded-lg border border-border px-3 text-sm text-secondary">
              <FolderPlus className="h-4 w-4" />
            </div>
          </div>
        </div>

        {addMutation.error instanceof Error ? (
          <div className="rounded-lg border border-red-400/30 bg-red-500/10 px-3 py-2 text-sm text-red-200">
            {addMutation.error.message}
          </div>
        ) : null}

        <div className="flex justify-end gap-2 border-t border-border pt-4">
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={() => addMutation.mutate()}
            disabled={!canSubmit}
            className="rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white transition-colors hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
          >
            {addMutation.isPending ? "Adding..." : "Add to group"}
          </button>
        </div>
      </div>
    </EditModal>
  );
}

function getGroupItemKind(hostType: string): GroupItemKind {
  if (hostType === "image" || hostType === "audio" || hostType === "text" || hostType === "group") {
    return hostType;
  }

  return "video";
}
