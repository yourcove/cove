import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { tagGroups } from "../api/client";
import type { TagGroup } from "../api/types";
import { useAuth } from "../auth/AuthContext";

export function TagGroupsManager({
  title = "Tag Groups",
  description = "Organize tag selectors, badge colors, and occurrence thresholds.",
  framed = true,
}: {
  title?: string;
  description?: string;
  framed?: boolean;
}) {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canWrite = hasPermission("taggroups.write") || hasPermission("tags.write");
  const canDelete = hasPermission("taggroups.delete");
  const { data: groups = [], isLoading } = useQuery({ queryKey: ["tag-groups"], queryFn: tagGroups.list });
  const [draft, setDraft] = useState({
    name: "",
    description: "",
    color: "#6ee7b7",
    sortOrder: undefined as number | undefined,
  });
  const [editingId, setEditingId] = useState<number | null>(null);

  const saveMutation = useMutation({
    mutationFn: async () => {
      const payload = {
        name: draft.name.trim(),
        description: draft.description.trim() || null,
        color: draft.color.trim() || null,
        sortOrder: draft.sortOrder ?? null,
      };
      return editingId == null ? tagGroups.create(payload) : tagGroups.update(editingId, payload);
    },
    onSuccess: () => {
      setDraft({ name: "", description: "", color: "#6ee7b7", sortOrder: undefined });
      setEditingId(null);
      queryClient.invalidateQueries({ queryKey: ["tag-groups"] });
      queryClient.invalidateQueries({ queryKey: ["tags"] });
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => tagGroups.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["tag-groups"] });
      queryClient.invalidateQueries({ queryKey: ["tags"] });
    },
  });

  const startEdit = (group: TagGroup) => {
    setEditingId(group.id);
    setDraft({
      name: group.name,
      description: group.description ?? "",
      color: group.color ?? "#6ee7b7",
      sortOrder: group.sortOrder,
    });
  };

  const content = (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-foreground">{title}</h3>
        {description ? <p className="mt-1 text-sm text-secondary">{description}</p> : null}
      </div>
      {canWrite ? (
        <div className="grid min-w-0 gap-3 sm:grid-cols-2 lg:grid-cols-[minmax(8rem,1fr)_minmax(12rem,1.5fr)_8.25rem_4.5rem] lg:items-end">
          <TagGroupTextField
            label="Name"
            value={draft.name}
            onChange={(value) => setDraft((current) => ({ ...current, name: value }))}
          />
          <TagGroupTextField
            label="Description"
            value={draft.description}
            onChange={(value) => setDraft((current) => ({ ...current, description: value }))}
          />
          <label className="block text-sm">
            <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">Color</span>
            <div className="flex items-center gap-2">
              <input
                type="color"
                value={/^#[0-9a-fA-F]{6}$/.test(draft.color) ? draft.color : "#6ee7b7"}
                onChange={(event) => setDraft((current) => ({ ...current, color: event.target.value }))}
                className="h-9 w-12 flex-none rounded border border-border bg-card p-1"
              />
              <input
                type="text"
                value={draft.color}
                onChange={(event) => setDraft((current) => ({ ...current, color: event.target.value }))}
                className="w-[4.75rem] min-w-0 flex-none rounded-lg border border-border bg-card px-2 py-2 text-sm text-foreground outline-none focus:border-accent"
              />
            </div>
          </label>
          <TagGroupNumberField
            label="Order"
            value={draft.sortOrder}
            onChange={(value) => setDraft((current) => ({ ...current, sortOrder: value }))}
          />
          <div className="flex flex-wrap gap-2 sm:col-span-2 lg:col-span-4 lg:justify-end">
            <button
              type="button"
              onClick={() => saveMutation.mutate()}
              disabled={saveMutation.isPending || !draft.name.trim()}
              className="inline-flex items-center gap-2 rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
            >
              <Plus className="h-4 w-4" /> {editingId == null ? "Add" : "Save"}
            </button>
            {editingId != null ? (
              <button
                type="button"
                onClick={() => {
                  setEditingId(null);
                  setDraft({ name: "", description: "", color: "#6ee7b7", sortOrder: undefined });
                }}
                className="rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:text-foreground"
              >
                Cancel
              </button>
            ) : null}
          </div>
        </div>
      ) : null}

      <div className="space-y-2">
        {isLoading ? <div className="text-sm text-muted">Loading...</div> : null}
        {groups.map((group) => (
          <div
            key={group.id}
            className="flex flex-col gap-3 rounded-lg border border-border bg-card p-3 sm:flex-row sm:items-center sm:justify-between"
          >
            <div className="flex min-w-0 items-center gap-3">
              <span
                className="h-4 w-4 rounded-full border border-border"
                style={{ backgroundColor: group.color ?? "transparent" }}
              />
              <div className="min-w-0">
                <div className="truncate text-sm font-medium text-foreground">{group.name}</div>
                <div className="text-xs text-muted">
                  {group.tagCount} tag{group.tagCount === 1 ? "" : "s"}
                </div>
              </div>
            </div>
            <div className="flex items-center gap-2">
              {canWrite ? (
                <button
                  type="button"
                  onClick={() => startEdit(group)}
                  className="rounded-lg border border-border px-2 py-1 text-xs text-secondary hover:text-foreground"
                >
                  Edit
                </button>
              ) : null}
              {canDelete ? (
                <button
                  type="button"
                  onClick={() => {
                    if (confirm(`Delete tag group "${group.name}"?`)) deleteMutation.mutate(group.id);
                  }}
                  className="rounded-lg border border-border px-2 py-1 text-xs text-red-300 hover:border-red-500 hover:text-red-200"
                >
                  Delete
                </button>
              ) : null}
            </div>
          </div>
        ))}
      </div>
    </div>
  );

  return framed ? <section className="rounded-xl border border-border bg-surface p-4">{content}</section> : content;
}

function TagGroupTextField({
  label,
  value,
  onChange,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <label className="block text-sm">
      <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">{label}</span>
      <input
        type="text"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="w-full min-w-0 rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground outline-none focus:border-accent"
      />
    </label>
  );
}

function TagGroupNumberField({
  label,
  value,
  onChange,
}: {
  label: string;
  value?: number;
  onChange: (value: number | undefined) => void;
}) {
  return (
    <label className="block text-sm">
      <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">{label}</span>
      <input
        type="number"
        value={value ?? ""}
        onChange={(event) => onChange(event.target.value === "" ? undefined : Number(event.target.value))}
        className="w-full rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground outline-none focus:border-accent"
      />
    </label>
  );
}
