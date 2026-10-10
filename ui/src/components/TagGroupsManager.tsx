import { useEffect, useMemo, useRef, useState } from "react";
import { useIsMutating, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Palette, Plus } from "lucide-react";
import { tagGroups } from "../api/client";
import type { TagGroup } from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { getApiValidationFailureDetail } from "../utils/requestFailure";
import { nextTagGroupColor, normalizeGroupColor } from "../utils/tagGroupColors";
import { TagGroupColorSuggestions, type TagGroupColorChange } from "./TagGroupColorSuggestions";

const EMPTY_DRAFT = { name: "", description: "", color: "", sortOrder: undefined as number | undefined };

type ColorChangeMode = "apply" | "undo";

interface ColorChangeResult {
  mode: ColorChangeMode;
  /** Every change saved so far in this mode, including earlier attempts before a retry. */
  saved: TagGroupColorChange[];
  failed: TagGroupColorChange[];
  /** Undo only: groups whose color changed again after the suggestions were applied. */
  skipped: TagGroupColorChange[];
  errorDetail?: string;
}

interface ColorChangeRequest {
  mode: ColorChangeMode;
  changes: TagGroupColorChange[];
  savedBefore?: TagGroupColorChange[];
  skippedBefore?: TagGroupColorChange[];
}

const COLOR_MUTATION_KEY = ["tag-group-colors"];

const storedColor = (color: string | null | undefined) => color?.trim() || null;

// Saves one group at a time (there is no bulk endpoint) and reports which saves failed instead of
// stopping at the first error. An empty string clears a color on the server.
async function saveGroupColors({
  mode,
  changes,
  savedBefore = [],
  skippedBefore = [],
}: ColorChangeRequest): Promise<ColorChangeResult> {
  const saved = [...savedBefore];
  const skipped = [...skippedBefore];
  const failed: TagGroupColorChange[] = [];
  let errorDetail: string | undefined;
  // Only change a group that still has the color this change starts from: its previous color when
  // applying, the applied color when undoing. Any other group was recolored meanwhile, here or in
  // another tab, or deleted, and keeps what it has. The list is read fresh so a just-saved edit is seen.
  // The server stores colors as sent (trimmed), so they are compared exactly.
  let toSave: TagGroupColorChange[];
  try {
    const current = new Map((await tagGroups.list()).map((group) => [group.id, storedColor(group.color)]));
    toSave = changes.filter(
      (change) => current.get(change.id) === storedColor(mode === "apply" ? change.previous : change.next),
    );
    skipped.push(...changes.filter((change) => !toSave.includes(change)));
  } catch (error) {
    return { mode, saved, failed: changes, skipped, errorDetail: getApiValidationFailureDetail(error) };
  }
  for (const change of toSave) {
    try {
      await tagGroups.update(change.id, { color: (mode === "apply" ? change.next : change.previous) ?? "" });
      saved.push(change);
    } catch (error) {
      failed.push(change);
      errorDetail ??= getApiValidationFailureDetail(error);
    }
  }
  return { mode, saved, failed, skipped, errorDetail };
}

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
  // Saving a group's color needs taggroups.write on the server, unlike the form's looser gate.
  const canRecolor = hasPermission("taggroups.write");
  const { data: groups = [], isLoading } = useQuery({ queryKey: ["tag-groups"], queryFn: tagGroups.list });
  const [draft, setDraft] = useState(EMPTY_DRAFT);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [suggesting, setSuggesting] = useState(false);
  const [colorResult, setColorResult] = useState<ColorChangeResult | null>(null);
  // The color in the group as it was loaded into the edit form, so Save only sends a color the user changed.
  const [editingLoadedColor, setEditingLoadedColor] = useState("");
  const suggestButtonRef = useRef<HTMLButtonElement>(null);
  // A new group starts with the hue furthest from every existing group's, instead of one fixed color.
  const autoColor = useMemo(() => nextTagGroupColor(groups), [groups]);
  // Counts color changes still running after the dialog was closed and reopened, which this
  // component's own mutation state doesn't know about.
  const colorChangesRunning = useIsMutating({ mutationKey: COLOR_MUTATION_KEY }) > 0;

  // Wait for the group list, which Undo and the preview read; let the tag lists refresh on their own.
  const invalidateGroups = () => {
    void queryClient.invalidateQueries({ queryKey: ["tags"] });
    return queryClient.invalidateQueries({ queryKey: ["tag-groups"] });
  };

  // Bumped when the panel closes or a color change finishes; see the focus effect below.
  const [focusRequest, setFocusRequest] = useState(0);
  const handledFocusRequest = useRef(0);

  const closeSuggestions = () => {
    setSuggesting(false);
    setFocusRequest((n) => n + 1);
  };

  const saveMutation = useMutation({
    mutationFn: async () => {
      const payload = {
        name: draft.name.trim(),
        description: draft.description.trim() || null,
        sortOrder: draft.sortOrder ?? null,
      };
      const color = draft.color.trim();
      // New groups fall back to the suggested color. When editing, the color is only sent if the
      // user changed it, so saving a rename can't put back a color that was replaced meanwhile.
      // An emptied field clears the color rather than quietly saving a default.
      return editingId == null
        ? tagGroups.create({ ...payload, color: color || autoColor })
        : tagGroups.update(editingId, color === editingLoadedColor ? payload : { ...payload, color });
    },
    onSuccess: () => {
      setDraft(EMPTY_DRAFT);
      setEditingId(null);
      invalidateGroups();
    },
  });

  // onSettled returns the group-list refetch, so the mutation stays pending until the list and the
  // preview show the new colors.
  const colorMutation = useMutation({
    mutationKey: COLOR_MUTATION_KEY,
    mutationFn: saveGroupColors,
    onSuccess: (result) => {
      setColorResult(result);
      if (suggesting) closeSuggestions();
    },
    onSettled: async () => {
      await invalidateGroups();
      // Undo and Retry unmount or disable the button that was clicked.
      setFocusRequest((n) => n + 1);
    },
  });

  const undoColors = (result: ColorChangeResult) => colorMutation.mutate({ mode: "undo", changes: result.saved });

  // A retry checks again for groups changed since, like the first attempt.
  const retryColors = (result: ColorChangeResult) =>
    colorMutation.mutate({
      mode: result.mode,
      changes: result.failed,
      savedBefore: result.saved,
      skippedBefore: result.skipped,
    });

  // When the panel closes or a color change finishes, the button the user clicked is gone or disabled.
  // Put focus on the button that opened the panel once it is enabled again (after Apply it stays
  // disabled until the refetch finishes), but only if focus was lost: someone typing elsewhere keeps it.
  const recolorBusy = suggesting || colorMutation.isPending || colorChangesRunning;
  useEffect(() => {
    if (focusRequest === handledFocusRequest.current || recolorBusy) return;
    handledFocusRequest.current = focusRequest;
    const active = document.activeElement;
    const focusLost =
      !active || active === document.body || !active.isConnected || (active as HTMLButtonElement).disabled === true;
    if (focusLost) suggestButtonRef.current?.focus();
  }, [focusRequest, recolorBusy]);

  const deleteMutation = useMutation({
    mutationFn: (id: number) => tagGroups.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["tag-groups"] });
      queryClient.invalidateQueries({ queryKey: ["tags"] });
    },
  });

  const startEdit = (group: TagGroup) => {
    setEditingId(group.id);
    setEditingLoadedColor(group.color ?? "");
    setDraft({
      name: group.name,
      description: group.description ?? "",
      color: group.color ?? "",
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
                value={normalizeGroupColor(draft.color) ?? (editingId == null ? autoColor : "#808080")}
                onChange={(event) => setDraft((current) => ({ ...current, color: event.target.value }))}
                className="h-9 w-12 flex-none rounded border border-border bg-card p-1"
              />
              <input
                type="text"
                value={draft.color}
                placeholder={editingId == null ? autoColor : "None"}
                aria-label="Color hex value"
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
                  setDraft(EMPTY_DRAFT);
                }}
                className="rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:text-foreground"
              >
                Cancel
              </button>
            ) : null}
          </div>
        </div>
      ) : null}

      {canRecolor && groups.length > 1 && editingId == null ? (
        <div className="flex justify-end">
          <button
            ref={suggestButtonRef}
            type="button"
            aria-expanded={suggesting}
            disabled={recolorBusy}
            onClick={() => setSuggesting(true)}
            className="inline-flex items-center gap-1.5 rounded-lg border border-border px-3 py-1.5 text-sm text-secondary hover:border-accent hover:text-foreground disabled:opacity-60"
          >
            <Palette className="h-4 w-4" /> Suggest distinct colors
          </button>
        </div>
      ) : null}

      {/* Kept mounted so screen readers announce the result when its text appears. */}
      <div role="status" aria-live="polite">
        {colorResult ? (
          <ColorResultNotice
            result={colorResult}
            // Locked while a group is open for editing too, so a Save can't race the Undo or Retry.
            busy={recolorBusy || editingId != null}
            lockedReason={editingId != null ? "Save or cancel the group you're editing first." : undefined}
            onUndo={() => undoColors(colorResult)}
            onRetry={() => retryColors(colorResult)}
            onDismiss={() => {
              setColorResult(null);
              suggestButtonRef.current?.focus();
            }}
          />
        ) : null}
      </div>

      {suggesting ? (
        <TagGroupColorSuggestions
          groups={groups}
          applying={colorMutation.isPending}
          onApply={(changes) => colorMutation.mutate({ mode: "apply", changes })}
          onCancel={closeSuggestions}
        />
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
                  // Editing while colors are being suggested or saved could load a color about to change.
                  disabled={recolorBusy}
                  className="rounded-lg border border-border px-2 py-1 text-xs text-secondary hover:text-foreground disabled:opacity-60"
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

function ColorResultNotice({
  result,
  busy,
  lockedReason,
  onUndo,
  onRetry,
  onDismiss,
}: {
  result: ColorChangeResult;
  busy: boolean;
  lockedReason?: string;
  onUndo: () => void;
  onRetry: () => void;
  onDismiss: () => void;
}) {
  const count = (n: number) => `${n} tag group${n === 1 ? "" : "s"}`;
  const names = (changes: TagGroupColorChange[]) => changes.map((change) => change.name).join(", ");
  const undo = result.mode === "undo";
  const buttonClass =
    "rounded-lg border border-border px-2 py-1 text-xs text-secondary hover:text-foreground disabled:opacity-60";
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-lg border border-border bg-card px-3 py-2 text-sm">
      <div className="min-w-0 flex-1 space-y-0.5">
        {result.saved.length > 0 ? (
          <div className="text-foreground">
            {undo ? "Restored the previous colors of" : "Updated the colors of"} {count(result.saved.length)}.
          </div>
        ) : null}
        {result.skipped.length > 0 ? (
          <div className="text-secondary">
            Kept the current colors of {count(result.skipped.length)} changed since: {names(result.skipped)}.
          </div>
        ) : null}
        {result.failed.length > 0 ? (
          <div className="text-red-300">
            Couldn't {undo ? "restore" : "update"} {count(result.failed.length)}: {names(result.failed)}.{" "}
            {result.errorDetail}
          </div>
        ) : null}
      </div>
      {result.failed.length > 0 ? (
        <button type="button" onClick={onRetry} disabled={busy} title={lockedReason} className={buttonClass}>
          Retry
        </button>
      ) : null}
      {!undo && result.saved.length > 0 ? (
        <button type="button" onClick={onUndo} disabled={busy} title={lockedReason} className={buttonClass}>
          Undo
        </button>
      ) : null}
      <button
        type="button"
        onClick={onDismiss}
        disabled={busy}
        title={lockedReason}
        className="rounded-lg px-2 py-1 text-xs text-muted hover:text-foreground disabled:opacity-60"
      >
        Dismiss
      </button>
    </div>
  );
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
