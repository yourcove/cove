import { Keyboard, X } from "lucide-react";
import { useEffect } from "react";
import { createPortal } from "react-dom";
import { useKeyboardShortcuts } from "../keyboard/KeyboardShortcutProvider";

function KeyCap({ children }: { children: string }) {
  return (
    <kbd className="inline-flex items-center justify-center min-w-[24px] h-6 px-1.5 rounded bg-surface border border-border text-xs font-mono text-foreground">
      {children}
    </kbd>
  );
}

function formatKeys(keys: string) {
  return keys.split(" ").map((k, i) => (
    <span key={i} className="inline-flex items-center gap-0.5">
      {i > 0 && <span className="text-muted mx-0.5">then</span>}
      <KeyCap>{k}</KeyCap>
    </span>
  ));
}

export function KeyboardShortcutsDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { actions, effectiveBindings, activeActionIds } = useKeyboardShortcuts();

  useEffect(() => {
    if (!open) return;
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      event.preventDefault();
      event.stopPropagation();
      onClose();
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [onClose, open]);

  if (!open) return null;

  const sections = Array.from(
    actions
      .reduce((groups, action) => {
        if (!activeActionIds.has(action.id)) return groups;
        const bindings = effectiveBindings[action.id] ?? [];
        if (bindings.length === 0) return groups;
        const entries = groups.get(action.group) ?? [];
        entries.push(...bindings.map((keys) => ({ keys, description: action.label })));
        groups.set(action.group, entries);
        return groups;
      }, new Map<string, Array<{ keys: string; description: string }>>())
      .entries(),
  ).map(([title, shortcuts]) => ({ title, shortcuts }));

  return createPortal(
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4"
      onClick={onClose}
      role="dialog"
      aria-modal="true"
      aria-labelledby="keyboard-shortcuts-title"
    >
      <div
        className="shortcuts-dialog bg-surface border border-border rounded-xl shadow-2xl w-full max-w-3xl max-h-[80vh] overflow-y-auto"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b border-border sticky top-0 shortcuts-dialog-header bg-surface z-10">
          <div className="flex items-center gap-2">
            <Keyboard className="w-5 h-5 text-accent" />
            <h2 id="keyboard-shortcuts-title" className="text-lg font-semibold text-foreground">
              Keyboard Shortcuts
            </h2>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close keyboard shortcuts"
            className="p-1.5 rounded-lg hover:bg-card-hover text-muted hover:text-foreground"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        {/* Content */}
        <div className="grid grid-cols-1 md:grid-cols-2 gap-6 p-6">
          {sections.length === 0 ? (
            <p className="text-sm text-secondary">No configurable shortcuts are active on this screen.</p>
          ) : (
            sections.map((section) => (
              <div key={section.title}>
                <h3 className="text-sm font-semibold text-accent mb-3 uppercase tracking-wider">{section.title}</h3>
                <div className="space-y-2">
                  {section.shortcuts.map((s) => (
                    <div key={s.keys} className="flex items-center justify-between gap-4">
                      <span className="text-sm text-secondary">{s.description}</span>
                      <div className="flex items-center gap-1 shrink-0">{formatKeys(s.keys)}</div>
                    </div>
                  ))}
                </div>
              </div>
            ))
          )}
        </div>
      </div>
    </div>,
    document.body,
  );
}
