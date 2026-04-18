import { useState } from "react";
import { QueryKey, useMutation, useQueryClient } from "@tanstack/react-query";
import { Merge, Loader2, X, ArrowRight } from "lucide-react";

interface MergeItem {
  id: number;
  name: string;
  imagePath?: string;
}

interface Props {
  open: boolean;
  onClose: () => void;
  entityType: "scene" | "performer" | "tag" | "studio";
  items: MergeItem[];
  onMerge: (targetId: number, sourceIds: number[]) => Promise<unknown>;
  queryKey: string | QueryKey;
}

export function MergeDialog({ open, onClose, entityType, items, onMerge, queryKey }: Props) {
  const [targetId, setTargetId] = useState<number | null>(items[0]?.id ?? null);
  const qc = useQueryClient();

  const mutation = useMutation({
    mutationFn: () => {
      if (!targetId) throw new Error("No target selected");
      const sourceIds = items.filter((i) => i.id !== targetId).map((i) => i.id);
      return onMerge(targetId, sourceIds);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: typeof queryKey === "string" ? [queryKey] : queryKey });
      onClose();
    },
  });

  if (!open || items.length < 2) return null;

  const sources = items.filter((i) => i.id !== targetId);
  const target = items.find((i) => i.id === targetId);

  return (
    <div className="fixed inset-0 z-[100] flex items-center justify-center">
      <div className="absolute inset-0 bg-black/70" onClick={onClose} />
      <div className="relative bg-surface rounded-lg shadow-xl w-full max-w-lg max-h-[85vh] flex flex-col mx-4">
        <div className="flex items-center justify-between px-6 py-4 border-b border-border">
          <h2 className="text-lg font-semibold flex items-center gap-2">
            <Merge className="w-5 h-5" />
            Merge {entityType.charAt(0).toUpperCase() + entityType.slice(1)}s
          </h2>
          <button onClick={onClose} className="text-secondary hover:text-white p-1">
            <X className="w-5 h-5" />
          </button>
        </div>

        <div className="p-6 overflow-y-auto flex-1 space-y-4">
          <p className="text-sm text-secondary">
            Select the destination {entityType}. All other {entityType}s will be merged into it and deleted.
          </p>

          <div className="space-y-1">
            <label className="text-xs text-secondary block mb-2">Destination</label>
            {items.map((item) => (
              <label
                key={item.id}
                className={`flex items-center gap-3 p-3 rounded cursor-pointer border transition-all ${
                  targetId === item.id
                    ? "border-accent bg-accent/10"
                    : "border-border hover:border-border"
                }`}
              >
                <input
                  type="radio"
                  name="mergeTarget"
                  checked={targetId === item.id}
                  onChange={() => setTargetId(item.id)}
                  className="accent-accent"
                />
                {item.imagePath && (
                  <img src={item.imagePath} alt="" className="w-8 h-8 rounded object-cover" />
                )}
                <span className="text-sm flex-1">{item.name}</span>
                {targetId === item.id && (
                  <span className="text-xs text-accent font-medium">Destination</span>
                )}
              </label>
            ))}
          </div>

          {target && sources.length > 0 && (
            <div className="bg-card/50 rounded p-3 text-sm text-secondary">
              <div className="flex items-center gap-2 mb-2 text-xs text-secondary font-medium">
                Summary
              </div>
              <div className="flex items-center gap-2 flex-wrap">
                {sources.map((s, i) => (
                  <span key={s.id}>
                    <span className="text-red-400 line-through">{s.name}</span>
                    {i < sources.length - 1 && <span className="text-secondary mx-1">,</span>}
                  </span>
                ))}
                <ArrowRight className="w-4 h-4 text-secondary mx-1" />
                <span className="text-green-400 font-medium">{target.name}</span>
              </div>
            </div>
          )}

          {mutation.isError && (
            <p className="text-sm text-red-400">Merge failed. Please try again.</p>
          )}
        </div>

        <div className="flex justify-end gap-2 px-6 py-4 border-t border-border">
          <button
            onClick={onClose}
            className="px-4 py-2 text-sm text-secondary hover:text-white rounded"
          >
            Cancel
          </button>
          <button
            onClick={() => mutation.mutate()}
            disabled={mutation.isPending || !targetId}
            className="px-4 py-2 text-sm bg-accent hover:bg-accent-hover text-white rounded flex items-center gap-2 disabled:opacity-50"
          >
            {mutation.isPending ? (
              <Loader2 className="w-4 h-4 animate-spin" />
            ) : (
              <Merge className="w-4 h-4" />
            )}
            Merge
          </button>
        </div>
      </div>
    </div>
  );
}
