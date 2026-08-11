import { useState } from "react";
import { Building2, Tags, Users } from "lucide-react";
import type { NameConflictEntityType } from "../../api/types";
import { EntityNameConflictCleanupPanel } from "./EntityNameConflictCleanupPanel";
import { TagNameConflictCleanupPanel } from "./TagNameConflictCleanupPanel";
import { useTagNameConflictSummary } from "./useTagNameConflicts";

type ConflictTab = "tag" | NameConflictEntityType;

export function NameConflictCleanupPanel({ includeEntityConflicts = true }: { includeEntityConflicts?: boolean }) {
  const [tab, setTab] = useState<ConflictTab>("tag");
  const summary = useTagNameConflictSummary(true, includeEntityConflicts);
  const tabs: { key: ConflictTab; label: string; icon: typeof Tags; count?: number }[] = [
    { key: "tag", label: "Tags", icon: Tags, count: summary.data?.tagUnresolvedGroupCount },
    ...(includeEntityConflicts ? [
      { key: "performer" as const, label: "Performers", icon: Users, count: summary.data?.performerUnresolvedGroupCount },
      { key: "studio" as const, label: "Studios", icon: Building2, count: summary.data?.studioUnresolvedGroupCount },
    ] : []),
  ];

  return (
    <div className="space-y-5">
      <section className="rounded-2xl border border-border bg-card p-4 sm:p-5">
        <h2 className="text-xl font-semibold text-foreground">Name conflicts</h2>
        <p className="mt-1 max-w-4xl text-sm text-secondary">
          {includeEntityConflicts
            ? "Review the exact canonical identity rules planned for Cove 1.3.0. Tags share a name-and-alias namespace; performer aliases remain non-unique and performer identity uses name plus disambiguation; studio identity uses the canonical name."
            : "Review tag names and aliases that share the same normalized namespace under the rules planned for Cove 1.3.0."}
        </p>
        <div className="mt-4 flex flex-wrap gap-2" role="tablist" aria-label="Conflict entity type">
          {tabs.map(({ key, label, icon: Icon, count }) => <button key={key} type="button" role="tab" aria-selected={tab === key} onClick={() => setTab(key)} className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium transition ${tab === key ? "border-accent bg-accent/10 text-accent" : "border-border text-secondary hover:text-foreground"}`}><Icon className="h-4 w-4" />{label}{count != null ? <span className={`rounded-full px-1.5 py-0.5 text-xs ${count > 0 ? "bg-amber-500/20 text-amber-200" : "bg-emerald-500/15 text-emerald-200"}`}>{count}</span> : null}</button>)}
        </div>
      </section>
      {tab === "tag" || !includeEntityConflicts ? <TagNameConflictCleanupPanel /> : <EntityNameConflictCleanupPanel entityType={tab} />}
    </div>
  );
}
