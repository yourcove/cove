import { useQuery } from "@tanstack/react-query";
import { Download, Pencil, Plus, Search, Trash2, Upload } from "lucide-react";
import { useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { extensions } from "../api/client";
import { useKeyboardShortcuts } from "../keyboard/KeyboardShortcutProvider";
import { normalizeShortcutEvent, normalizeShortcutSequence } from "../keyboard/keybindings";

function downloadText(filename: string, contents: string) {
  const url = URL.createObjectURL(new Blob([contents], { type: "application/json" }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

export function KeyboardShortcutSettings() {
  const {
    actions,
    presets,
    activePresetId,
    effectivePresetId,
    effectiveBindings,
    selectPreset,
    clonePreset,
    updatePersonalPreset,
    deletePersonalPreset,
    importPreset,
    exportPreset,
    setDispatchSuspended,
    showChordHints,
    setShowChordHints,
  } = useKeyboardShortcuts();
  const [query, setQuery] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  const [recording, setRecording] = useState<{
    actionId: string;
    label: string;
    existing: string[];
    strokes: string[];
  } | null>(null);
  const [renaming, setRenaming] = useState<{ presetId: string; name: string } | null>(null);
  const [activeShortcutTabId, setActiveShortcutTabId] = useState("cove");
  const fileInputRef = useRef<HTMLInputElement>(null);
  const renameButtonRef = useRef<HTMLButtonElement>(null);
  const renameDialogRef = useRef<HTMLDivElement>(null);
  const activePreset = presets.find((preset) => preset.id === activePresetId);
  const effectivePreset = presets.find((preset) => preset.id === effectivePresetId);
  const editable = activePreset?.provenance?.source === "personal" || activePreset?.provenance?.source === "import";
  const { data: extensionInfos = [] } = useQuery({
    queryKey: ["extensions-list"],
    queryFn: () => extensions.list(),
  });

  const shortcutTabs = useMemo(() => {
    const normalizedQuery = query.trim().toLowerCase();
    const extensionNames = new Map(extensionInfos.map((extension) => [extension.id, extension.name]));
    const tabs = new Map<string, { id: string; label: string; extensionId?: string; actions: typeof actions }>();
    tabs.set("cove", {
      id: "cove",
      label: "Cove",
      actions: actions.filter((action) => action.source !== "extension"),
    });
    for (const action of actions) {
      if (action.source !== "extension") continue;
      const extensionId = action.extensionId ?? "unknown-extension";
      const tabId = `extension:${extensionId}`;
      const tab = tabs.get(tabId) ?? {
        id: tabId,
        label: extensionNames.get(extensionId) ?? extensionId,
        extensionId,
        actions: [],
      };
      tab.actions.push(action);
      tabs.set(tabId, tab);
    }

    const tabEntries = Array.from(tabs.values());
    const labelCounts = tabEntries.reduce((counts, tab) => {
      const label = tab.label.toLocaleLowerCase();
      counts.set(label, (counts.get(label) ?? 0) + 1);
      return counts;
    }, new Map<string, number>());

    return tabEntries
      .map((tab) => {
        const matchingActions = tab.actions.filter(
          (action) =>
            !normalizedQuery ||
            `${action.label} ${action.description ?? ""} ${action.id} ${action.group}`
              .toLowerCase()
              .includes(normalizedQuery),
        );
        return {
          ...tab,
          label:
            tab.extensionId && (labelCounts.get(tab.label.toLocaleLowerCase()) ?? 0) > 1
              ? `${tab.label} (${tab.extensionId})`
              : tab.label,
          matchCount: matchingActions.length,
          groups: Array.from(
            matchingActions
              .reduce((result, action) => {
                const entries = result.get(action.group) ?? [];
                entries.push(action);
                result.set(action.group, entries);
                return result;
              }, new Map<string, typeof actions>())
              .entries(),
          ),
        };
      })
      .sort((left, right) => {
        if (left.id === "cove") return -1;
        if (right.id === "cove") return 1;
        return left.label.localeCompare(right.label);
      });
  }, [actions, extensionInfos, query]);
  const activeShortcutTab = shortcutTabs.find((tab) => tab.id === activeShortcutTabId) ?? shortcutTabs[0];

  useEffect(() => {
    if (!shortcutTabs.some((tab) => tab.id === activeShortcutTabId)) setActiveShortcutTabId("cove");
  }, [activeShortcutTabId, shortcutTabs]);

  const isRenaming = renaming !== null;
  useEffect(() => {
    if (!isRenaming) return;
    return () => renameButtonRef.current?.focus();
  }, [isRenaming]);

  const handleRenameKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    event.stopPropagation();
    if (event.key === "Escape") {
      event.preventDefault();
      setRenaming(null);
      return;
    }
    if (event.key !== "Tab" || !renameDialogRef.current) return;
    const focusable = Array.from(
      renameDialogRef.current.querySelectorAll<HTMLElement>(
        "button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex='-1'])",
      ),
    );
    if (focusable.length === 0) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  };

  const updateBindings = (actionId: string, bindings: string[]) => {
    if (!activePreset || !editable) return;
    const normalized = [...new Set(bindings.map(normalizeShortcutSequence).filter(Boolean))];
    updatePersonalPreset({
      ...activePreset,
      bindings: { ...activePreset.bindings, [actionId]: normalized },
    });
  };

  useEffect(() => {
    if (!recording) return;
    setDispatchSuspended(true);
    const onKeyDown = (event: KeyboardEvent) => {
      event.preventDefault();
      event.stopImmediatePropagation();
      if (event.key === "Escape") {
        setRecording(null);
        return;
      }
      if (event.key === "Enter" && recording.strokes.length > 0) {
        updateBindings(recording.actionId, [...recording.existing, recording.strokes.join(" ")]);
        setRecording(null);
        return;
      }
      const stroke = normalizeShortcutEvent(event);
      if (!stroke || recording.strokes.length >= 3) return;
      setRecording((current) => (current ? { ...current, strokes: [...current.strokes, stroke] } : null));
    };
    window.addEventListener("keydown", onKeyDown, { capture: true });
    return () => {
      window.removeEventListener("keydown", onKeyDown, { capture: true });
      setDispatchSuspended(false);
    };
  }, [recording, setDispatchSuspended]);

  return (
    <div className="space-y-5">
      <div className="rounded-xl border border-border bg-card p-4">
        <div className="flex flex-wrap items-end gap-3">
          <label className="min-w-52 flex-1 text-sm text-secondary">
            Preset
            <select
              value={activePresetId}
              onChange={(event) => selectPreset(event.target.value)}
              className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-foreground"
            >
              {!activePreset && <option value={activePresetId}>Unavailable preset</option>}
              {presets.map((preset) => (
                <option key={preset.id} value={preset.id}>
                  {preset.name}
                </option>
              ))}
            </select>
          </label>
          {!editable ? (
            <button
              type="button"
              onClick={() => clonePreset(effectivePresetId)}
              className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:border-accent hover:text-foreground"
            >
              <Pencil className="h-4 w-4" /> Edit a copy
            </button>
          ) : (
            <>
              <button
                ref={renameButtonRef}
                type="button"
                onClick={() => activePreset && setRenaming({ presetId: activePreset.id, name: activePreset.name })}
                className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:border-accent hover:text-foreground"
              >
                <Pencil className="h-4 w-4" /> Rename
              </button>
              <button
                type="button"
                onClick={() => activePreset && deletePersonalPreset(activePreset.id)}
                className="inline-flex items-center gap-2 rounded-lg border border-red-500/40 px-3 py-2 text-sm text-red-300 hover:bg-red-500/10"
              >
                <Trash2 className="h-4 w-4" /> Delete
              </button>
            </>
          )}
          <button
            type="button"
            onClick={() => fileInputRef.current?.click()}
            className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:border-accent hover:text-foreground"
          >
            <Upload className="h-4 w-4" /> Import
          </button>
          <button
            type="button"
            onClick={() => {
              const exported = exportPreset(effectivePresetId);
              if (exported)
                downloadText(
                  `${(effectivePreset?.name ?? "keyboard-shortcuts").replace(/[^a-z0-9_-]+/gi, "-").toLowerCase()}.json`,
                  exported,
                );
            }}
            className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:border-accent hover:text-foreground"
          >
            <Download className="h-4 w-4" /> Export
          </button>
          <input
            ref={fileInputRef}
            type="file"
            accept="application/json,.json"
            className="hidden"
            onChange={async (event) => {
              const file = event.target.files?.[0];
              if (!file) return;
              try {
                const imported = importPreset(await file.text());
                setMessage(`Imported ${imported.name}.`);
              } catch (error) {
                setMessage(error instanceof Error ? error.message : "Could not import the preset.");
              } finally {
                event.target.value = "";
              }
            }}
          />
        </div>
        <p className="mt-2 text-xs text-muted">
          {activePreset
            ? activePreset.description
            : `The selected preset is unavailable; using ${effectivePreset?.name ?? "Cove Native"} temporarily.`}
        </p>
        {message && (
          <div className="mt-3 rounded-lg border border-border bg-surface px-3 py-2 text-sm text-secondary">
            {message}
          </div>
        )}
        <label className="mt-4 flex items-center gap-3 rounded-lg border border-border bg-surface px-3 py-2 text-sm text-secondary">
          <input
            type="checkbox"
            checked={showChordHints}
            onChange={(event) => setShowChordHints(event.target.checked)}
            className="h-4 w-4 accent-accent"
          />
          <span>
            <span className="block font-medium text-foreground">Show chord hints</span>
            <span className="block text-xs text-muted">
              Show valid next keys while waiting for a multi-key shortcut.
            </span>
          </span>
        </label>
      </div>

      <label className="relative block">
        <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted" />
        <input
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="Search shortcuts"
          className="w-full rounded-lg border border-border bg-surface py-2 pl-9 pr-3 text-sm text-foreground focus:border-accent focus:outline-none"
        />
      </label>

      <div
        className="flex gap-1 overflow-x-auto border-b border-border"
        role="tablist"
        aria-label="Shortcut source"
        aria-orientation="horizontal"
      >
        {shortcutTabs.map((tab, index) => {
          const selected = tab.id === activeShortcutTab.id;
          return (
            <button
              key={tab.id}
              id={`keyboard-shortcut-tab-${index}`}
              type="button"
              role="tab"
              aria-selected={selected}
              aria-label={
                query.trim()
                  ? `${tab.label}, ${tab.matchCount} ${tab.matchCount === 1 ? "match" : "matches"}`
                  : tab.label
              }
              aria-controls={`keyboard-shortcut-panel-${index}`}
              tabIndex={selected ? 0 : -1}
              title={tab.extensionId}
              onClick={() => setActiveShortcutTabId(tab.id)}
              onKeyDown={(event) => {
                let nextIndex = index;
                if (event.key === "ArrowRight") nextIndex = (index + 1) % shortcutTabs.length;
                else if (event.key === "ArrowLeft") nextIndex = (index - 1 + shortcutTabs.length) % shortcutTabs.length;
                else if (event.key === "Home") nextIndex = 0;
                else if (event.key === "End") nextIndex = shortcutTabs.length - 1;
                else return;
                event.preventDefault();
                setActiveShortcutTabId(shortcutTabs[nextIndex].id);
                requestAnimationFrame(() => document.getElementById(`keyboard-shortcut-tab-${nextIndex}`)?.focus());
              }}
              className={`shrink-0 whitespace-nowrap border-b-2 px-4 py-2 text-sm font-medium transition-colors ${
                selected
                  ? "border-accent text-foreground"
                  : "border-transparent text-muted hover:border-border hover:text-secondary"
              }`}
            >
              {tab.label}
              {query.trim() && (
                <span className="ml-1.5 text-xs text-muted" aria-hidden="true">
                  {tab.matchCount}
                </span>
              )}
            </button>
          );
        })}
      </div>

      <section
        id={`keyboard-shortcut-panel-${shortcutTabs.indexOf(activeShortcutTab)}`}
        role="tabpanel"
        aria-labelledby={`keyboard-shortcut-tab-${shortcutTabs.indexOf(activeShortcutTab)}`}
        className="space-y-5"
      >
        {activeShortcutTab.groups.length > 0 ? (
          activeShortcutTab.groups.map(([group, definitions]) => (
            <div key={group} className="space-y-3">
              <h3 className="text-xs font-semibold uppercase tracking-wide text-muted">{group}</h3>
              <div className="grid gap-3 md:grid-cols-2">
                {definitions.map((definition) => {
                  const bindings = effectiveBindings[definition.id] ?? [];
                  return (
                    <div key={definition.id} className="rounded-xl border border-border bg-card p-3">
                      <div className="text-sm font-medium text-foreground">{definition.label}</div>
                      <div className="mt-1 text-xs text-muted">{definition.id}</div>
                      <div className="mt-3 space-y-2">
                        {bindings.map((binding, index) => (
                          <div key={`${definition.id}:${index}`} className="flex gap-2">
                            <input
                              value={binding}
                              readOnly={!editable}
                              onChange={(event) =>
                                updateBindings(
                                  definition.id,
                                  bindings.map((value, bindingIndex) =>
                                    bindingIndex === index ? event.target.value : value,
                                  ),
                                )
                              }
                              className="min-w-0 flex-1 rounded-lg border border-border bg-surface px-3 py-2 font-mono text-sm text-foreground read-only:text-muted focus:border-accent focus:outline-none"
                              aria-label={`${definition.label} binding ${index + 1}`}
                            />
                            {editable && (
                              <button
                                type="button"
                                onClick={() =>
                                  updateBindings(
                                    definition.id,
                                    bindings.filter((_, bindingIndex) => bindingIndex !== index),
                                  )
                                }
                                className="rounded-lg border border-border p-2 text-muted hover:border-red-500/50 hover:text-red-300"
                                aria-label={`Remove ${binding}`}
                              >
                                <Trash2 className="h-4 w-4" />
                              </button>
                            )}
                          </div>
                        ))}
                        {bindings.length === 0 && (
                          <div className="rounded-lg border border-dashed border-border px-3 py-2 text-sm text-muted">
                            Unbound
                          </div>
                        )}
                        {editable && bindings.length < 8 && (
                          <button
                            type="button"
                            onClick={() =>
                              setRecording({
                                actionId: definition.id,
                                label: definition.label,
                                existing: bindings,
                                strokes: [],
                              })
                            }
                            className="inline-flex items-center gap-1 text-xs text-accent hover:text-accent-hover"
                          >
                            <Plus className="h-3.5 w-3.5" /> Add binding
                          </button>
                        )}
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>
          ))
        ) : (
          <div className="rounded-xl border border-dashed border-border px-4 py-8 text-center text-sm text-muted">
            No shortcuts in {activeShortcutTab.label} match your search.
          </div>
        )}
      </section>

      {renaming &&
        (() => {
          const preset = presets.find((candidate) => candidate.id === renaming.presetId);
          const trimmedName = renaming.name.trim();
          const canSave = Boolean(preset && trimmedName && trimmedName !== preset.name);
          return (
            <div
              ref={renameDialogRef}
              className="fixed inset-0 z-[100] flex items-center justify-center bg-black/60 p-4"
              role="dialog"
              aria-modal="true"
              aria-label="Rename keyboard shortcut preset"
              onKeyDown={handleRenameKeyDown}
            >
              <form
                className="w-full max-w-md rounded-xl border border-border bg-card p-5 shadow-2xl"
                onSubmit={(event) => {
                  event.preventDefault();
                  if (!preset || !canSave) return;
                  updatePersonalPreset({ ...preset, name: trimmedName });
                  setMessage(`Renamed preset to ${trimmedName}.`);
                  setRenaming(null);
                }}
              >
                <h3 className="text-lg font-semibold text-foreground">Rename preset</h3>
                <label className="mt-4 block text-sm text-secondary">
                  Preset name
                  <input
                    autoFocus
                    value={renaming.name}
                    onChange={(event) => setRenaming({ ...renaming, name: event.target.value })}
                    className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-foreground focus:border-accent focus:outline-none"
                  />
                </label>
                <div className="mt-4 flex justify-end gap-2">
                  <button
                    type="button"
                    onClick={() => setRenaming(null)}
                    className="rounded-lg border border-border px-3 py-2 text-sm text-secondary"
                  >
                    Cancel
                  </button>
                  <button
                    type="submit"
                    disabled={!canSave}
                    className="rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white disabled:opacity-50"
                  >
                    Save
                  </button>
                </div>
              </form>
            </div>
          );
        })()}

      {recording && (
        <div
          className="fixed inset-0 z-[100] flex items-center justify-center bg-black/60 p-4"
          role="dialog"
          aria-modal="true"
          aria-label="Record keyboard shortcut"
        >
          <div className="w-full max-w-md rounded-xl border border-border bg-card p-5 shadow-2xl">
            <h3 className="text-lg font-semibold text-foreground">Record {recording.label}</h3>
            <p className="mt-2 text-sm text-secondary">
              Press up to three keys or modifier chords, then Enter to save. Press Escape to cancel.
            </p>
            <input
              autoFocus
              readOnly
              aria-label="Recorded shortcut"
              value={recording.strokes.join(" ")}
              placeholder="Waiting for keys…"
              className="mt-5 min-h-12 w-full rounded-lg border border-accent bg-surface px-4 py-3 text-center font-mono text-foreground placeholder:text-muted"
            />
            <div className="mt-4 flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setRecording(null)}
                className="rounded-lg border border-border px-3 py-2 text-sm text-secondary"
              >
                Cancel
              </button>
              <button
                type="button"
                disabled={recording.strokes.length === 0}
                onClick={() => {
                  updateBindings(recording.actionId, [...recording.existing, recording.strokes.join(" ")]);
                  setRecording(null);
                }}
                className="rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white disabled:opacity-50"
              >
                Save binding
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
