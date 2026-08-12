import { useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, CheckCircle2, Loader2, RefreshCw } from "lucide-react";
import { entityNameConflicts } from "../../api/client";
import type {
  EntityExternalReference,
  EntityExternalReferenceResolution,
  EntityNameConflictGroup,
  EntityNameConflictResolution,
  EntityNameImpact,
  NameConflictEntityType,
} from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { getApiValidationFailureDetail } from "../../utils/requestFailure";
import {
  entityNameConflictQueryKey,
  tagNameConflictSummaryQueryKey,
  useEntityNameConflictScan,
} from "./useTagNameConflicts";

type EntityAction = EntityNameConflictResolution["action"];
type ExternalAction = EntityExternalReferenceResolution["action"];
type EntityChoice = { action: EntityAction; newName: string; newDisambiguation: string };
type GroupChoices = Record<number, EntityChoice>;
type ExternalChoices = Record<string, ExternalAction | "">;
type PendingAction = { kind: "group"; group: EntityNameConflictGroup } | null;

interface GroupPlan {
  resolutions: EntityNameConflictResolution[];
  externalReferenceResolutions: EntityExternalReferenceResolution[];
  mergeEntityIds: Set<number>;
  renameCount: number;
  hasInvalidRename: boolean;
  hasUnresolvedExternalReferences: boolean;
  hasRestrictedExternalReferences: boolean;
  externalUpdatedReferenceCount: number;
  externalDeletedReferenceCount: number;
}

function externalReferenceKey(reference: Pick<EntityExternalReference, "entityId" | "referenceKey">) {
  return `${reference.entityId}:${reference.referenceKey}`;
}

function defaultChoice(entityId: number, survivorId: number): EntityChoice {
  return {
    action: entityId === survivorId ? "keep" : "merge-entity",
    newName: "",
    newDisambiguation: "",
  };
}

function buildGroupPlan(
  group: EntityNameConflictGroup,
  survivorId: number,
  choices: GroupChoices,
  externalChoices: ExternalChoices,
): GroupPlan {
  const resolutions = group.candidates.map((candidate) => {
    const choice = candidate.entityId === survivorId
      ? defaultChoice(candidate.entityId, survivorId)
      : choices[candidate.entityId] ?? defaultChoice(candidate.entityId, survivorId);
    return {
      entityId: candidate.entityId,
      action: choice.action,
      ...(choice.action === "rename" ? {
        newName: choice.newName,
        newDisambiguation: group.entityType === "performer" ? choice.newDisambiguation : undefined,
      } : {}),
    } satisfies EntityNameConflictResolution;
  });
  const mergeEntityIds = new Set(resolutions.filter((resolution) => resolution.action === "merge-entity").map((resolution) => resolution.entityId));
  const externalReferenceResolutions: EntityExternalReferenceResolution[] = [];
  let hasUnresolvedExternalReferences = false;
  let hasRestrictedExternalReferences = false;
  let externalUpdatedReferenceCount = 0;
  let externalDeletedReferenceCount = 0;
  for (const impact of group.impacts) {
    if (!mergeEntityIds.has(impact.entityId)) continue;
    for (const reference of impact.externalReferences ?? []) {
      if (reference.accessLimitation != null || reference.rowCount == null) {
        hasRestrictedExternalReferences = true;
        continue;
      }
      const action = externalChoices[externalReferenceKey(reference)];
      if (!action) {
        hasUnresolvedExternalReferences = true;
        continue;
      }
      externalReferenceResolutions.push({ entityId: reference.entityId, referenceKey: reference.referenceKey, action });
      if (action === "update-to-survivor") externalUpdatedReferenceCount += reference.rowCount;
      else externalDeletedReferenceCount += reference.rowCount;
    }
  }
  return {
    resolutions,
    externalReferenceResolutions,
    mergeEntityIds,
    renameCount: resolutions.filter((resolution) => resolution.action === "rename").length,
    hasInvalidRename: resolutions.some((resolution) => resolution.action === "rename" && !resolution.newName?.trim()),
    hasUnresolvedExternalReferences,
    hasRestrictedExternalReferences,
    externalUpdatedReferenceCount,
    externalDeletedReferenceCount,
  };
}

export function EntityNameConflictCleanupPanel({ entityType }: { entityType: NameConflictEntityType }) {
  const queryClient = useQueryClient();
  const scan = useEntityNameConflictScan(entityType);
  const [selectedSurvivors, setSelectedSurvivors] = useState<Record<string, number>>({});
  const [choices, setChoices] = useState<Record<string, GroupChoices>>({});
  const [externalChoices, setExternalChoices] = useState<Record<string, ExternalChoices>>({});
  const [pendingAction, setPendingAction] = useState<PendingAction>(null);

  useEffect(() => {
    if (!scan.data) return;
    queryClient.invalidateQueries({ queryKey: tagNameConflictSummaryQueryKey });
    setSelectedSurvivors((current) => {
      const retained: Record<string, number> = {};
      for (const group of scan.data.groups) {
        const selected = current[group.key];
        if (selected != null && group.candidates.some((candidate) => candidate.entityId === selected))
          retained[group.key] = selected;
      }
      return retained;
    });
  }, [queryClient, scan.data]);

  const planFor = (group: EntityNameConflictGroup) => buildGroupPlan(
    group,
    selectedSurvivors[group.key] ?? group.recommendedSurvivorEntityId,
    choices[group.key] ?? {},
    externalChoices[group.key] ?? {},
  );

  const refreshScan = () => {
    setSelectedSurvivors({});
    setChoices({});
    setExternalChoices({});
    return scan.refetch();
  };

  const mutation = useMutation({
    mutationFn: (action: Exclude<PendingAction, null>) => {
      const survivorId = selectedSurvivors[action.group.key] ?? action.group.recommendedSurvivorEntityId;
      const plan = planFor(action.group);
      return entityNameConflicts.resolve(
        entityType,
        action.group.key,
        action.group.revision,
        survivorId,
        plan.resolutions,
        plan.externalReferenceResolutions,
      );
    },
    onSuccess: (nextScan) => {
      queryClient.setQueryData(entityNameConflictQueryKey(entityType), nextScan);
      queryClient.invalidateQueries({ queryKey: tagNameConflictSummaryQueryKey });
      setChoices({});
      setExternalChoices({});
      setPendingAction(null);
    },
  });

  const pendingPlan = pendingAction ? planFor(pendingAction.group) : null;
  const singular = entityType === "performer" ? "performer" : "studio";
  const plural = entityType === "performer" ? "performers" : "studios";
  if (scan.isLoading)
    return <div className="flex min-h-40 items-center justify-center"><Loader2 className="h-6 w-6 animate-spin text-accent" aria-label={`Scanning ${plural}`} /></div>;
  if (scan.error)
    return (
      <StatusBox tone="error">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <span>The {singular} conflict scan failed: {(scan.error as Error).message}</span>
          <button type="button" onClick={() => scan.refetch()} disabled={scan.isFetching} className="inline-flex items-center gap-2 rounded-lg border border-red-300/40 px-3 py-1.5 text-sm font-medium text-red-100 hover:border-red-200 disabled:opacity-50">
            <RefreshCw className={`h-4 w-4 ${scan.isFetching ? "animate-spin" : ""}`} /> Retry scan
          </button>
        </div>
      </StatusBox>
    );
  if (!scan.data || scan.data.unresolvedGroupCount === 0)
    return <StatusBox tone="success"><span className="inline-flex items-center gap-2"><CheckCircle2 className="h-5 w-5" /> No {singular} identity conflicts remain.</span></StatusBox>;

  return (
    <div className="space-y-5">
      <section className="rounded-2xl border border-amber-500/30 bg-amber-500/10 p-4 sm:p-5">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div className="flex gap-3">
            <AlertTriangle className="mt-0.5 h-6 w-6 shrink-0 text-amber-300" aria-hidden="true" />
            <div>
              <h3 className="font-semibold text-amber-100">Resolve {singular} identities before Cove 1.3.0</h3>
              <p className="mt-1 max-w-3xl text-sm text-amber-100/80">
                {entityType === "performer"
                  ? "Cove 1.3.0 will make each trimmed, case-folded performer name plus disambiguation pair unique. Aliases may still overlap."
                  : "Cove 1.3.0 will make trimmed, case-folded studio names unique. Studio aliases do not participate."}
              </p>
              <p className="mt-2 text-xs text-amber-100/70">{scan.data.unresolvedGroupCount} unresolved {scan.data.unresolvedGroupCount === 1 ? "group" : "groups"}.</p>
            </div>
          </div>
          <div className="flex shrink-0 flex-wrap gap-2">
            <button type="button" onClick={refreshScan} disabled={scan.isFetching || mutation.isPending} className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:border-accent hover:text-foreground disabled:opacity-50">
              <RefreshCw className={`h-4 w-4 ${scan.isFetching ? "animate-spin" : ""}`} /> Refresh scan
            </button>
          </div>
        </div>
      </section>

      {mutation.error ? <StatusBox tone="error">{getApiValidationFailureDetail(mutation.error)}</StatusBox> : null}
      <div className="space-y-4">
        {scan.data.groups.map((group) => {
          const survivorId = selectedSurvivors[group.key] ?? group.recommendedSurvivorEntityId;
          return (
            <EntityConflictCard
              key={group.key}
              group={group}
              survivorId={survivorId}
              choices={choices[group.key] ?? {}}
              externalChoices={externalChoices[group.key] ?? {}}
              disabled={mutation.isPending}
              onSelectSurvivor={(entityId) => {
                setSelectedSurvivors((current) => ({ ...current, [group.key]: entityId }));
                setChoices((current) => ({ ...current, [group.key]: {} }));
                setExternalChoices((current) => ({ ...current, [group.key]: {} }));
              }}
              onChangeChoice={(entityId, choice) => setChoices((current) => ({
                ...current,
                [group.key]: { ...(current[group.key] ?? {}), [entityId]: choice },
              }))}
              onChangeExternal={(reference, action) => setExternalChoices((current) => ({
                ...current,
                [group.key]: { ...(current[group.key] ?? {}), [externalReferenceKey(reference)]: action },
              }))}
              onResolve={() => setPendingAction({ kind: "group", group })}
            />
          );
        })}
      </div>

      <ConfirmDialog
        open={pendingAction != null}
        title={`Resolve this ${singular} conflict?`}
        message={describePlan(pendingPlan, singular)}
        confirmLabel="Resolve group"
        destructive={Boolean(pendingPlan && pendingPlan.mergeEntityIds.size > 0)}
        isPending={mutation.isPending}
        errorMessage={mutation.error ? getApiValidationFailureDetail(mutation.error) : null}
        onCancel={() => { if (!mutation.isPending) setPendingAction(null); }}
        onConfirm={() => { if (pendingAction) mutation.mutate(pendingAction); }}
      />
    </div>
  );
}

function EntityConflictCard({
  group,
  survivorId,
  choices,
  externalChoices,
  disabled,
  onSelectSurvivor,
  onChangeChoice,
  onChangeExternal,
  onResolve,
}: {
  group: EntityNameConflictGroup;
  survivorId: number;
  choices: GroupChoices;
  externalChoices: ExternalChoices;
  disabled: boolean;
  onSelectSurvivor: (entityId: number) => void;
  onChangeChoice: (entityId: number, choice: EntityChoice) => void;
  onChangeExternal: (reference: EntityExternalReference, action: ExternalAction | "") => void;
  onResolve: () => void;
}) {
  const plan = buildGroupPlan(group, survivorId, choices, externalChoices);
  const identity = group.entityType === "performer" && group.normalizedDisambiguation
    ? `${group.normalizedName} — ${group.normalizedDisambiguation}`
    : group.normalizedName;
  const singular = group.entityType === "performer" ? "performer" : "studio";
  const externalImpacts = group.impacts.filter((impact) => (impact.externalReferences ?? []).length > 0);

  return (
    <section className="overflow-hidden rounded-2xl border border-border bg-surface shadow-lg shadow-black/10">
      <div className="flex flex-col gap-3 border-b border-border p-4 sm:flex-row sm:items-start sm:justify-between sm:p-5">
        <div>
          <h3 className="text-lg font-semibold text-foreground">{displayValue(identity)}</h3>
          <p className="mt-1 text-sm text-secondary">{group.candidates.length} {group.candidates.length === 1 ? singular : `${singular}s`} claim this future identity.</p>
          <p className="mt-1 text-xs text-secondary">The recommendation keeps the candidate with the most transferred references; the lowest ID breaks a tie.</p>
          {plan.hasRestrictedExternalReferences ? <p className="mt-2 text-sm text-red-200">A source being merged has an extension location Cove cannot inspect. Use the extension or a database administrator, rename it, or keep it as survivor.</p> : null}
          {plan.hasUnresolvedExternalReferences ? <p className="mt-2 text-sm text-amber-200">Choose update or delete for every extension-owned reference on entities being merged.</p> : null}
          {plan.hasInvalidRename ? <p className="mt-2 text-sm text-red-300">Enter a non-blank name for every renamed {singular}.</p> : null}
        </div>
        <button type="button" onClick={onResolve} disabled={disabled || plan.hasInvalidRename || plan.hasRestrictedExternalReferences || plan.hasUnresolvedExternalReferences} className="shrink-0 rounded-lg border border-accent px-3 py-2 text-sm font-semibold text-accent hover:bg-accent/10 disabled:opacity-50">Resolve group</button>
      </div>

      <div className="grid gap-5 p-4 sm:p-5 xl:grid-cols-[minmax(0,0.9fr)_minmax(0,1.4fr)]">
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wide text-muted">Candidates and actions</h4>
          <div className="mt-2 space-y-2">
            {group.candidates.map((candidate) => {
              const selected = candidate.entityId === survivorId;
              const choice = selected ? defaultChoice(candidate.entityId, survivorId) : choices[candidate.entityId] ?? defaultChoice(candidate.entityId, survivorId);
              return (
                <div key={candidate.entityId} className={`rounded-xl border p-3 text-sm ${selected ? "border-accent/50 bg-accent/5" : "border-border bg-card"}`}>
                  <label className="flex cursor-pointer items-start gap-2">
                    <input type="radio" name={`entity-survivor-${group.key}`} checked={selected} onChange={() => onSelectSurvivor(candidate.entityId)} className="mt-1 accent-accent" aria-label={`Keep ${candidate.name}`} />
                    <span>
                      <span className="font-medium text-foreground">{displayName(candidate.name, candidate.disambiguation)}</span>
                      <span className="ml-2 text-xs text-muted">#{candidate.entityId}</span>
                      {candidate.entityId === group.recommendedSurvivorEntityId ? <span className="ml-2 rounded bg-emerald-500/15 px-1.5 py-0.5 text-[10px] font-semibold uppercase text-emerald-200">Recommended</span> : null}
                    </span>
                  </label>
                  {!selected ? (
                    <div className="mt-3 space-y-2 pl-6">
                      <select value={choice.action} onChange={(event) => onChangeChoice(candidate.entityId, { ...choice, action: event.target.value as EntityAction })} aria-label={`Action for ${candidate.name}`} className="w-full rounded-lg border border-border bg-surface px-2 py-1.5 text-sm text-foreground">
                        <option value="merge-entity">Merge into survivor</option>
                        <option value="rename">Rename and keep separate</option>
                      </select>
                      {choice.action === "rename" ? (
                        <div className={`grid gap-2 ${group.entityType === "performer" ? "sm:grid-cols-2" : ""}`}>
                          <input value={choice.newName} onChange={(event) => onChangeChoice(candidate.entityId, { ...choice, newName: event.target.value })} placeholder={`New ${singular} name`} aria-label={`New name for ${candidate.name}`} className="rounded-lg border border-border bg-surface px-2 py-1.5 text-sm text-foreground placeholder:text-muted" />
                          {group.entityType === "performer" ? <input value={choice.newDisambiguation} onChange={(event) => onChangeChoice(candidate.entityId, { ...choice, newDisambiguation: event.target.value })} placeholder="Disambiguation (optional)" aria-label={`New disambiguation for ${candidate.name}`} className="rounded-lg border border-border bg-surface px-2 py-1.5 text-sm text-foreground placeholder:text-muted" /> : null}
                        </div>
                      ) : <p className="text-xs text-muted">Relationships and metadata transfer to the survivor.</p>}
                    </div>
                  ) : <p className="mt-2 pl-6 text-xs text-emerald-200">Survives this group.</p>}
                </div>
              );
            })}
          </div>
        </div>

        <div className="min-w-0">
          <h4 className="text-xs font-semibold uppercase tracking-wide text-muted">Impact before changes</h4>
          <div className="mt-2 overflow-x-auto rounded-xl border border-border">
            <table className="min-w-[900px] w-full text-left text-sm">
              <thead className="bg-card text-xs text-secondary"><tr><th className="px-3 py-2">Entity</th><th className="px-3 py-2">Action</th><th className="px-3 py-2">References</th><th className="px-3 py-2">Linked</th><th className="px-3 py-2">Groups</th><th className="px-3 py-2">Hierarchy</th><th className="px-3 py-2">Faces</th><th className="px-3 py-2">Ratings</th><th className="px-3 py-2">Other</th><th className="px-3 py-2">Extensions</th></tr></thead>
              <tbody className="divide-y divide-border">{group.impacts.map((impact) => <ImpactRow key={impact.entityId} impact={impact} selected={impact.entityId === survivorId} willMerge={plan.mergeEntityIds.has(impact.entityId)} recommended={impact.entityId === group.recommendedSurvivorEntityId} />)}</tbody>
            </table>
          </div>
        </div>
      </div>

      {externalImpacts.length > 0 ? (
        <div className="border-t border-border p-4 sm:p-5">
          <h4 className="text-xs font-semibold uppercase tracking-wide text-muted">Extension-owned database references</h4>
          <p className="mt-1 text-xs text-secondary">Updating changes the foreign key to the survivor. Deleting removes matching extension rows. Both run in the same transaction as the merge.</p>
          <div className="mt-3 space-y-3">{externalImpacts.map((impact) => <ExternalReferenceTable key={impact.entityId} impact={impact} willMerge={plan.mergeEntityIds.has(impact.entityId)} choices={externalChoices} onChange={onChangeExternal} />)}</div>
        </div>
      ) : null}
    </section>
  );
}

function ExternalReferenceTable({ impact, willMerge, choices, onChange }: { impact: EntityNameImpact; willMerge: boolean; choices: ExternalChoices; onChange: (reference: EntityExternalReference, action: ExternalAction | "") => void }) {
  return (
    <section className="overflow-hidden rounded-xl border border-border bg-card">
      <div className="flex items-center justify-between gap-2 border-b border-border px-3 py-2"><span className="text-sm font-medium text-foreground">{displayName(impact.name, impact.disambiguation)} <span className="font-normal text-muted">#{impact.entityId}</span></span><span className={`text-xs ${willMerge ? "text-amber-200" : "text-muted"}`}>{willMerge ? "Review required" : "Kept unchanged"}</span></div>
      <div className="overflow-x-auto"><table className="min-w-[760px] w-full text-left text-sm"><thead className="text-xs text-secondary"><tr><th className="px-3 py-2">Table</th><th className="px-3 py-2">Column</th><th className="px-3 py-2">Rows</th><th className="px-3 py-2">Deletion policy</th><th className="px-3 py-2">Database action</th></tr></thead><tbody className="divide-y divide-border">
        {(impact.externalReferences ?? []).map((reference) => <tr key={reference.referenceKey}><td className="px-3 py-2 font-mono text-xs">{reference.schemaName}.{reference.tableName}</td><td className="px-3 py-2 font-mono text-xs">{reference.columnName}</td><td className="px-3 py-2 tabular-nums">{reference.rowCount == null ? "Unknown" : reference.rowCount.toLocaleString()}</td><td className="px-3 py-2 text-secondary">{reference.deleteBehavior}</td><td className="px-3 py-2">
          {willMerge && reference.accessLimitation == null && reference.rowCount != null ? <select value={choices[externalReferenceKey(reference)] ?? ""} onChange={(event) => onChange(reference, event.target.value as ExternalAction | "")} aria-label={`Database action for ${reference.schemaName}.${reference.tableName}.${reference.columnName}`} className="w-full rounded-lg border border-border bg-surface px-2 py-1.5 text-sm"><option value="">Choose action…</option><option value="update-to-survivor">Update rows to survivor</option><option value="delete-rows">Delete rows</option></select> : willMerge ? <span className="text-xs text-amber-200">Owner or DBA repair required</span> : <span className="text-xs text-muted">Keep unchanged</span>}
        </td></tr>)}
      </tbody></table></div>
    </section>
  );
}

function ImpactRow({ impact, selected, willMerge, recommended }: { impact: EntityNameImpact; selected: boolean; willMerge: boolean; recommended: boolean }) {
  return <tr className={selected ? "bg-accent/5" : ""}><td className="px-3 py-2 font-medium text-foreground">{displayName(impact.name, impact.disambiguation)} <span className="text-muted">#{impact.entityId}</span>{recommended ? <span className="ml-2 rounded bg-emerald-500/15 px-1.5 py-0.5 text-[10px] font-semibold uppercase text-emerald-200">Recommended</span> : null}</td><td className={`px-3 py-2 ${willMerge ? "text-amber-200" : "text-secondary"}`}>{selected ? "Survivor" : willMerge ? "Merge" : "Rename"}</td><Count value={impact.referenceCount} /><Count value={impact.linkedEntityCount} /><Count value={impact.groupCount} /><Count value={impact.hierarchyCount} /><Count value={impact.faceCount} /><Count value={impact.ratingCount} /><Count value={impact.otherMetadataCount} />{impact.externalReferences.some((reference) => reference.rowCount == null) ? <td className="px-3 py-2 text-amber-200">Unknown</td> : <Count value={impact.extensionMetadataCount} />}</tr>;
}

function Count({ value }: { value: number }) {
  return <td className={`px-3 py-2 tabular-nums ${value > 0 ? "text-foreground" : "text-muted"}`}>{value.toLocaleString()}</td>;
}

function describePlan(plan: GroupPlan | null, singular: string) {
  if (!plan) return "Cove will apply the selected actions transactionally and refresh the scan.";
  const parts = [
    plan.mergeEntityIds.size ? `merge ${plan.mergeEntityIds.size} ${singular}${plan.mergeEntityIds.size === 1 ? "" : "s"}` : null,
    plan.renameCount ? `rename ${plan.renameCount}` : null,
    plan.externalUpdatedReferenceCount ? `update ${plan.externalUpdatedReferenceCount} extension row reference${plan.externalUpdatedReferenceCount === 1 ? "" : "s"}` : null,
    plan.externalDeletedReferenceCount ? `delete ${plan.externalDeletedReferenceCount} extension row reference${plan.externalDeletedReferenceCount === 1 ? "" : "s"}` : null,
  ].filter(Boolean);
  return `Cove will ${parts.join(", ") || "normalize the selected identities"}. The operation is transactional and the scan refreshes afterward.`;
}

function StatusBox({ tone, children }: { tone: "success" | "error"; children: React.ReactNode }) {
  return <div className={`rounded-2xl border p-4 text-sm ${tone === "success" ? "border-emerald-500/30 bg-emerald-500/10 text-emerald-100" : "border-red-500/30 bg-red-500/10 text-red-100"}`}>{children}</div>;
}

function displayName(name: string, disambiguation?: string | null) {
  return disambiguation?.trim() ? `${displayValue(name)} (${disambiguation.trim()})` : displayValue(name);
}

function displayValue(value: string) {
  return value.trim().length === 0 ? "<blank>" : value;
}
