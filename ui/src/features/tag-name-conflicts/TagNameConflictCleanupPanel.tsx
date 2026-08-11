import { useEffect, useMemo, useState, type ReactNode } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, CheckCircle2, Loader2, RefreshCw, Tags } from "lucide-react";
import { tagNameConflicts } from "../../api/client";
import type {
  TagExternalReference,
  TagExternalReferenceResolution,
  TagNameClaimResolution,
  TagNameConflictClaim,
  TagNameConflictGroup,
  TagNameConflictImpact,
} from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { getApiValidationFailureDetail } from "../../utils/requestFailure";
import {
  tagNameConflictQueryKey,
  tagNameConflictSummaryQueryKey,
  useTagNameConflictScan,
} from "./useTagNameConflicts";

const KIND_LABELS: Record<string, string> = {
  "canonical-name-collision": "Multiple tag names",
  "name-alias-collision": "Tag name and alias",
  "alias-ownership-collision": "Alias on multiple tags",
  "redundant-self-alias": "Alias repeats its tag name",
  "duplicate-alias": "Duplicate aliases",
  "blank-alias": "Blank alias",
  "whitespace-only-canonical-name": "Whitespace-only tag name",
  "empty-name-collision": "<empty> collision",
};

type ResolutionAction = TagNameClaimResolution["action"];
type ExternalReferenceAction = TagExternalReferenceResolution["action"];
type ClaimChoice = { action: ResolutionAction; newValue: string };
type GroupChoices = Record<string, ClaimChoice>;
type ExternalReferenceChoices = Record<string, ExternalReferenceAction | "">;
type PendingAction = { kind: "group"; group: TagNameConflictGroup } | { kind: "all"; expectedRevision: string } | null;

type GroupPlan = {
  survivingClaimKey: string | null;
  actions: Map<string, ClaimChoice | { action: "keep"; newValue: string }>;
  resolutions: TagNameClaimResolution[];
  externalReferenceResolutions: TagExternalReferenceResolution[];
  mergeTagIds: Set<number>;
  removedAliasCount: number;
  renamedClaimCount: number;
  hasInvalidRename: boolean;
  externalUpdatedRowCount: number;
  externalDeletedRowCount: number;
  hasUnresolvedExternalReferences: boolean;
  hasRestrictedExternalReferences: boolean;
};

function claimKey(claim: Pick<TagNameConflictClaim, "tagId" | "aliasId">) {
  return claim.aliasId == null ? `tag-${claim.tagId}` : `alias-${claim.aliasId}`;
}

function externalReferenceKey(reference: Pick<TagExternalReference, "tagId" | "referenceKey">) {
  return `${reference.tagId}:${reference.referenceKey}`;
}

function buildGroupPlan(
  group: TagNameConflictGroup,
  survivorTagId: number,
  choices: GroupChoices = {},
  externalReferenceChoices: ExternalReferenceChoices = {},
): GroupPlan {
  const isBlankAliasGroup = group.kinds.includes("blank-alias");
  const survivingClaim = isBlankAliasGroup
    ? undefined
    : group.claims.find((claim) => claim.tagId === survivorTagId && claim.claimType === "tag-name")
      ?? group.claims
        .filter((claim) => claim.tagId === survivorTagId)
        .sort((left, right) => (left.aliasId ?? 0) - (right.aliasId ?? 0))[0];
  const survivingClaimKey = survivingClaim ? claimKey(survivingClaim) : null;
  const actions = new Map<string, ClaimChoice | { action: "keep"; newValue: string }>();
  const resolutions: TagNameClaimResolution[] = [];
  const defaultMergeTagIds = new Set(
    group.claims
      .filter((claim) => claim.claimType === "tag-name" && claim.tagId !== survivorTagId)
      .map((claim) => claim.tagId),
  );

  for (const claim of group.claims) {
    const key = claimKey(claim);
    if (key === survivingClaimKey) {
      actions.set(key, { action: "keep", newValue: "" });
      continue;
    }

    const fallbackAction: ResolutionAction = defaultMergeTagIds.has(claim.tagId) ? "merge-tag" : "remove-alias";
    const choice = choices[key] ?? { action: fallbackAction, newValue: "" };
    actions.set(key, choice);
    resolutions.push({
      tagId: claim.tagId,
      aliasId: claim.aliasId,
      action: choice.action,
      ...(choice.action === "rename" ? { newValue: choice.newValue } : {}),
    });
  }

  const mergeTagIds = new Set(
    resolutions
      .filter((resolution) => resolution.action === "merge-tag" && resolution.tagId !== survivorTagId)
      .map((resolution) => resolution.tagId),
  );
  const effectiveResolutions = resolutions.filter((resolution) => !mergeTagIds.has(resolution.tagId));
  const externalReferenceResolutions: TagExternalReferenceResolution[] = [];
  let externalUpdatedRowCount = 0;
  let externalDeletedRowCount = 0;
  let hasUnresolvedExternalReferences = false;
  let hasRestrictedExternalReferences = false;
  for (const impact of group.impacts) {
    if (!mergeTagIds.has(impact.tagId)) continue;
    for (const reference of impact.externalReferences ?? []) {
      if (reference.accessLimitation != null || reference.rowCount == null) {
        hasRestrictedExternalReferences = true;
        continue;
      }
      const action = externalReferenceChoices[externalReferenceKey(reference)];
      if (!action) {
        hasUnresolvedExternalReferences = true;
        continue;
      }
      externalReferenceResolutions.push({
        tagId: reference.tagId,
        referenceKey: reference.referenceKey,
        action,
      });
      if (action === "update-to-survivor") externalUpdatedRowCount += reference.rowCount;
      else externalDeletedRowCount += reference.rowCount;
    }
  }
  return {
    survivingClaimKey,
    actions,
    resolutions,
    externalReferenceResolutions,
    mergeTagIds,
    removedAliasCount: effectiveResolutions.filter((resolution) => resolution.action === "remove-alias").length,
    renamedClaimCount: effectiveResolutions.filter((resolution) => resolution.action === "rename").length,
    hasInvalidRename: effectiveResolutions.some((resolution) => resolution.action === "rename" && !resolution.newValue?.trim()),
    externalUpdatedRowCount,
    externalDeletedRowCount,
    hasUnresolvedExternalReferences,
    hasRestrictedExternalReferences,
  };
}

function updateClaimChoice(
  group: TagNameConflictGroup,
  survivorTagId: number,
  choices: GroupChoices,
  changedClaim: TagNameConflictClaim,
  choice: ClaimChoice,
): GroupChoices {
  const next = { ...choices };
  const currentPlan = buildGroupPlan(group, survivorTagId, choices);
  const claimsOnTag = group.claims.filter((claim) => claim.tagId === changedClaim.tagId);

  if (choice.action === "merge-tag") {
    for (const claim of claimsOnTag)
      if (claimKey(claim) !== currentPlan.survivingClaimKey)
        next[claimKey(claim)] = { action: "merge-tag", newValue: "" };
    return next;
  }

  for (const claim of claimsOnTag) {
    const key = claimKey(claim);
    if (key === claimKey(changedClaim)) {
      next[key] = choice;
    } else if (currentPlan.actions.get(key)?.action === "merge-tag") {
      next[key] = claim.claimType === "alias"
        ? { action: "remove-alias", newValue: "" }
        : { action: "rename", newValue: "" };
    }
  }
  return next;
}

export function TagNameConflictCleanupPanel() {
  const queryClient = useQueryClient();
  const scan = useTagNameConflictScan();
  const [selectedSurvivors, setSelectedSurvivors] = useState<Record<string, number>>({});
  const [choices, setChoices] = useState<Record<string, GroupChoices>>({});
  const [externalReferenceChoices, setExternalReferenceChoices] = useState<Record<string, ExternalReferenceChoices>>({});
  const [pendingAction, setPendingAction] = useState<PendingAction>(null);

  useEffect(() => {
    if (!scan.data) return;
    queryClient.setQueryData(tagNameConflictSummaryQueryKey, {
      unresolvedGroupCount: scan.data.unresolvedGroupCount,
      scannedAtUtc: scan.data.scannedAtUtc,
    });
    setSelectedSurvivors((current) => {
      const retained: Record<string, number> = {};
      for (const group of scan.data.groups) {
        const currentSurvivor = current[group.key];
        const survivorStillOwnsAClaim = currentSurvivor != null
          && group.claims.some((claim) => claim.tagId === currentSurvivor);
        if (survivorStillOwnsAClaim) retained[group.key] = currentSurvivor;
      }
      return retained;
    });
  }, [queryClient, scan.data]);

  const planFor = (group: TagNameConflictGroup) => buildGroupPlan(
    group,
    selectedSurvivors[group.key] ?? group.recommendedSurvivorTagId,
    choices[group.key],
    externalReferenceChoices[group.key],
  );

  const mutation = useMutation({
    mutationFn: (action: Exclude<PendingAction, null>) => {
      if (action.kind === "all") return tagNameConflicts.resolveAll(action.expectedRevision);
      const survivorTagId = selectedSurvivors[action.group.key] ?? action.group.recommendedSurvivorTagId;
      return tagNameConflicts.resolve(
        action.group.key,
        action.group.revision,
        survivorTagId,
        planFor(action.group).resolutions,
        planFor(action.group).externalReferenceResolutions,
      );
    },
    onSuccess: (nextScan) => {
      queryClient.setQueryData(tagNameConflictQueryKey, nextScan);
      queryClient.setQueryData(tagNameConflictSummaryQueryKey, {
        unresolvedGroupCount: nextScan.unresolvedGroupCount,
        scannedAtUtc: nextScan.scannedAtUtc,
      });
      setChoices({});
      setExternalReferenceChoices({});
      setPendingAction(null);
    },
  });

  const totalClaims = useMemo(() => scan.data?.groups.reduce((sum, group) => sum + group.claims.length, 0) ?? 0, [scan.data]);
  const recommendedMergeBlocked = useMemo(() => scan.data?.groups.some((group) => {
    const plan = buildGroupPlan(group, group.recommendedSurvivorTagId);
    return group.impacts.some((impact) => plan.mergeTagIds.has(impact.tagId) && (impact.externalReferences ?? []).length > 0);
  }) ?? false, [scan.data]);
  const pendingPlan = pendingAction?.kind === "group" ? planFor(pendingAction.group) : null;

  if (scan.isLoading)
    return <div className="flex min-h-40 items-center justify-center"><Loader2 className="h-6 w-6 animate-spin text-accent" aria-label="Scanning tag names" /></div>;
  if (scan.error)
    return <StatusBox tone="error">The tag-name conflict scan failed: {(scan.error as Error).message}</StatusBox>;
  if (!scan.data || scan.data.unresolvedGroupCount === 0)
    return (
      <StatusBox tone="success">
        <span className="inline-flex items-center gap-2"><CheckCircle2 className="h-5 w-5" /> No tag name or alias conflicts remain. This database is ready for the Cove 1.3.0 namespace rules.</span>
      </StatusBox>
    );

  return (
    <div className="space-y-5">
      <section className="rounded-2xl border border-amber-500/30 bg-amber-500/10 p-4 sm:p-5">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div className="flex gap-3">
            <AlertTriangle className="mt-0.5 h-6 w-6 shrink-0 text-amber-300" aria-hidden="true" />
            <div>
              <h3 className="font-semibold text-amber-100">Resolve these claims before upgrading to Cove 1.3.0</h3>
              <p className="mt-1 max-w-3xl text-sm text-amber-100/80">
                Cove 1.3.0 will require every trimmed tag name and alias to be unique. Choose one claim to keep, then merge or rename conflicting tags and remove, rename, or merge conflicting aliases.
              </p>
              <p className="mt-2 text-xs text-amber-100/70">{scan.data.unresolvedGroupCount} unresolved groups containing {totalClaims} claims.</p>
            </div>
          </div>
          <div className="flex shrink-0 flex-wrap gap-2">
            <button
              type="button"
              onClick={() => scan.refetch()}
              disabled={scan.isFetching || mutation.isPending}
              className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:border-accent hover:text-foreground disabled:opacity-50"
            >
              <RefreshCw className={`h-4 w-4 ${scan.isFetching ? "animate-spin" : ""}`} /> Refresh scan
            </button>
            <button
              type="button"
              onClick={() => setPendingAction({ kind: "all", expectedRevision: scan.data.revision })}
              disabled={mutation.isPending || recommendedMergeBlocked}
              title={recommendedMergeBlocked ? "At least one recommended merge requires per-table non-core database decisions." : undefined}
              className="inline-flex items-center gap-2 rounded-lg bg-accent px-3 py-2 text-sm font-semibold text-white hover:bg-accent-hover disabled:opacity-50"
            >
              <Tags className="h-4 w-4" /> Apply all recommended fixes
            </button>
          </div>
        </div>
        {recommendedMergeBlocked ? (
          <p className="mt-3 text-sm text-amber-100/80">
            Apply all is unavailable because at least one recommended source tag has non-core references that must be reviewed table by table. Resolve that group individually, rename the tag, or choose another survivor.
          </p>
        ) : null}
      </section>

      {mutation.error ? <StatusBox tone="error">{getApiValidationFailureDetail(mutation.error)}</StatusBox> : null}

      <div className="space-y-4">
        {scan.data.groups.map((group) => (
          <ConflictGroupCard
            key={group.key}
            group={group}
            selectedSurvivor={selectedSurvivors[group.key] ?? group.recommendedSurvivorTagId}
            choices={choices[group.key] ?? {}}
            onSelectSurvivor={(tagId) => {
              setSelectedSurvivors((current) => ({ ...current, [group.key]: tagId }));
              setChoices((current) => ({ ...current, [group.key]: {} }));
              setExternalReferenceChoices((current) => ({ ...current, [group.key]: {} }));
            }}
            onChangeClaim={(claim, choice) => setChoices((current) => ({
              ...current,
              [group.key]: updateClaimChoice(
                group,
                selectedSurvivors[group.key] ?? group.recommendedSurvivorTagId,
                current[group.key] ?? {},
                claim,
                choice,
              ),
            }))}
            externalReferenceChoices={externalReferenceChoices[group.key] ?? {}}
            onChangeExternalReference={(reference, action) => setExternalReferenceChoices((current) => ({
              ...current,
              [group.key]: {
                ...(current[group.key] ?? {}),
                [externalReferenceKey(reference)]: action,
              },
            }))}
            onResolve={() => setPendingAction({ kind: "group", group })}
            disabled={mutation.isPending}
          />
        ))}
      </div>

      <ConfirmDialog
        open={pendingAction != null}
        title={pendingAction?.kind === "all" ? "Apply all recommended tag fixes?" : "Resolve this tag-name conflict?"}
        message={pendingAction?.kind === "all"
          ? "Cove will remove redundant or conflicting aliases and merge canonical-name conflicts into their recommended survivors. All operations run transactionally and the scan refreshes afterward."
          : describePlan(pendingPlan)}
        confirmLabel={pendingAction?.kind === "all" ? "Apply all" : "Resolve group"}
        destructive={pendingAction?.kind === "all" || Boolean(pendingPlan && (pendingPlan.mergeTagIds.size > 0 || pendingPlan.removedAliasCount > 0))}
        isPending={mutation.isPending}
        errorMessage={mutation.error ? getApiValidationFailureDetail(mutation.error) : null}
        onCancel={() => { if (!mutation.isPending) setPendingAction(null); }}
        onConfirm={() => { if (pendingAction) mutation.mutate(pendingAction); }}
      />
    </div>
  );
}

function ConflictGroupCard({
  group,
  selectedSurvivor,
  choices,
  externalReferenceChoices,
  onSelectSurvivor,
  onChangeClaim,
  onChangeExternalReference,
  onResolve,
  disabled,
}: {
  group: TagNameConflictGroup;
  selectedSurvivor: number;
  choices: GroupChoices;
  externalReferenceChoices: ExternalReferenceChoices;
  onSelectSurvivor: (tagId: number) => void;
  onChangeClaim: (claim: TagNameConflictClaim, choice: ClaimChoice) => void;
  onChangeExternalReference: (reference: TagExternalReference, action: ExternalReferenceAction | "") => void;
  onResolve: () => void;
  disabled: boolean;
}) {
  const owners = group.impacts;
  const plan = buildGroupPlan(group, selectedSurvivor, choices, externalReferenceChoices);
  const mergingExternalReferenceCount = owners
    .filter((impact) => plan.mergeTagIds.has(impact.tagId))
    .reduce((sum, impact) => sum + impact.extensionMetadataCount, 0);
  const externalImpacts = owners.filter((impact) => (impact.externalReferences ?? []).length > 0);
  const canSelectSurvivor = group.hasCrossTagClaims && !group.kinds.includes("blank-alias");

  return (
    <section className="overflow-hidden rounded-2xl border border-border bg-surface shadow-lg shadow-black/10">
      <div className="flex flex-col gap-3 border-b border-border p-4 sm:flex-row sm:items-start sm:justify-between sm:p-5">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <h3 className="text-lg font-semibold text-foreground">{displayValue(group.normalizedName)}</h3>
            {group.kinds.map((kind) => <span key={kind} className="rounded-full border border-border bg-card px-2 py-0.5 text-xs text-secondary">{KIND_LABELS[kind] ?? kind}</span>)}
          </div>
          <p className="mt-1 text-sm text-secondary">{group.hasCrossTagClaims ? `${owners.length} tags share this future namespace.` : "This group affects one tag and can be cleaned without merging."}</p>
          {plan.hasRestrictedExternalReferences ? (
            <p className="mt-2 max-w-2xl text-sm text-red-200">
              A non-core table on a tag being merged is protected by row-level security or database permissions. Cove cannot verify or change those rows; use the owning extension or a database administrator, or keep that tag.
            </p>
          ) : plan.hasUnresolvedExternalReferences ? (
            <p className="mt-2 max-w-2xl text-sm text-amber-200">
              Choose whether to update or delete all {mergingExternalReferenceCount.toLocaleString()} non-core reference{mergingExternalReferenceCount === 1 ? "" : "s"} on the tags being merged.
            </p>
          ) : null}
          {plan.hasInvalidRename ? <p className="mt-2 text-sm text-red-300">Enter a non-blank replacement for every claim set to rename.</p> : null}
        </div>
        <button
          type="button"
          onClick={onResolve}
          disabled={disabled || plan.hasRestrictedExternalReferences || plan.hasUnresolvedExternalReferences || plan.hasInvalidRename}
          className="shrink-0 rounded-lg border border-accent px-3 py-2 text-sm font-semibold text-accent hover:bg-accent/10 disabled:opacity-50"
        >
          Resolve group
        </button>
      </div>

      <div className="grid gap-5 p-4 sm:p-5 xl:grid-cols-[minmax(0,0.95fr)_minmax(0,1.35fr)]">
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wide text-muted">Claims and actions</h4>
          <div className="mt-2 space-y-2">
            {group.claims.map((claim, index) => {
              const key = claimKey(claim);
              const choice = plan.actions.get(key)!;
              const isSurviving = key === plan.survivingClaimKey;
              return (
                <div key={`${key}-${index}`} className="rounded-xl border border-border bg-card p-3 text-sm">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className={`rounded px-1.5 py-0.5 text-[11px] font-semibold uppercase ${claim.claimType === "tag-name" ? "bg-blue-500/15 text-blue-200" : "bg-violet-500/15 text-violet-200"}`}>
                      {claim.claimType === "tag-name" ? "Tag name" : "Alias"}
                    </span>
                    <span className="font-medium text-foreground">{displayValue(claim.originalValue)}</span>
                    {isSurviving ? <span className="rounded bg-emerald-500/15 px-1.5 py-0.5 text-[10px] font-semibold uppercase text-emerald-200">Keeps name</span> : null}
                  </div>
                  <div className="mt-1 text-xs text-secondary">Tag #{claim.tagId}: {displayValue(claim.tagName)}{claim.normalizedValue != null && claim.normalizedValue !== claim.originalValue ? ` → ${displayValue(claim.normalizedValue)}` : ""}</div>
                  {!isSurviving ? (
                    <div className="mt-3 grid gap-2 sm:grid-cols-[minmax(0,11rem)_minmax(0,1fr)]">
                      <label className="sr-only" htmlFor={`action-${group.key}-${key}`}>Resolution for {claim.claimType} {claim.originalValue}</label>
                      <select
                        id={`action-${group.key}-${key}`}
                        aria-label={`Resolution for ${claim.claimType} ${displayValue(claim.originalValue)} on tag ${claim.tagId}`}
                        value={choice.action}
                        onChange={(event) => onChangeClaim(claim, { action: event.target.value as ResolutionAction, newValue: choice.newValue })}
                        className="rounded-lg border border-border bg-surface px-2 py-1.5 text-sm text-foreground"
                      >
                        {claim.claimType === "alias" ? <option value="remove-alias">Remove alias</option> : null}
                        <option value="rename">Rename {claim.claimType === "alias" ? "alias" : "tag"}</option>
                        {claim.tagId !== selectedSurvivor ? <option value="merge-tag">Merge tag into survivor</option> : null}
                      </select>
                      {choice.action === "rename" ? (
                        <input
                          type="text"
                          value={choice.newValue}
                          onChange={(event) => onChangeClaim(claim, { action: "rename", newValue: event.target.value })}
                          aria-label={`New value for ${claim.claimType} ${displayValue(claim.originalValue)} on tag ${claim.tagId}`}
                          placeholder={claim.claimType === "alias" ? "New alias" : "New tag name"}
                          className="rounded-lg border border-border bg-surface px-2 py-1.5 text-sm text-foreground placeholder:text-muted"
                        />
                      ) : <span className="self-center text-xs text-muted">{choice.action === "merge-tag" ? "All metadata on this tag will transfer." : "The tag itself will remain unchanged."}</span>}
                    </div>
                  ) : null}
                </div>
              );
            })}
          </div>
        </div>

        <div className="min-w-0">
          <h4 className="text-xs font-semibold uppercase tracking-wide text-muted">Impact before changes</h4>
          {canSelectSurvivor ? (
            <p className="mt-1 text-xs text-secondary">
              Cove recommends the canonical-name owner with the most references when one exists, so an alias alone does not force a whole-tag merge. Alias-only groups use the most-referenced owner. Ties use the lowest tag ID, and you can choose another survivor.
            </p>
          ) : null}
          <div className="mt-2 overflow-x-auto rounded-xl border border-border">
            <table className="min-w-[980px] w-full text-left text-sm">
              <thead className="bg-card text-xs text-secondary">
                <tr>
                  <th className="px-3 py-2 font-medium">Survivor</th>
                  <th className="px-3 py-2 font-medium">Tag</th>
                  <th className="px-3 py-2 font-medium">Action</th>
                  <th className="px-3 py-2 font-medium">References</th>
                  <th className="px-3 py-2 font-medium">Entities</th>
                  <th className="px-3 py-2 font-medium">Segments</th>
                  <th className="px-3 py-2 font-medium">Parents</th>
                  <th className="px-3 py-2 font-medium">Children</th>
                  <th className="px-3 py-2 font-medium">Ratings</th>
                  <th className="px-3 py-2 font-medium">Other metadata</th>
                  <th className="px-3 py-2 font-medium">Extension data</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {owners.map((impact) => (
                  <ImpactRow
                    key={impact.tagId}
                    impact={impact}
                    recommended={plan.survivingClaimKey != null && impact.tagId === group.recommendedSurvivorTagId}
                    selected={plan.survivingClaimKey != null && impact.tagId === selectedSurvivor}
                    selectable={canSelectSurvivor}
                    willMerge={plan.mergeTagIds.has(impact.tagId)}
                    radioGroup={group.key}
                    onSelect={() => onSelectSurvivor(impact.tagId)}
                  />
                ))}
              </tbody>
            </table>
          </div>
        </div>
      </div>
      {externalImpacts.length > 0 ? (
        <div className="border-t border-border p-4 sm:p-5">
          <h4 className="text-xs font-semibold uppercase tracking-wide text-muted">Non-core database references</h4>
          <p className="mt-1 max-w-4xl text-xs text-secondary">
            These foreign keys belong to extension or otherwise non-core tables. Updating preserves the rows and changes their tag ID to the survivor. Deleting removes the matching rows and may activate extension-defined triggers or cascades.
          </p>
          <div className="mt-3 space-y-3">
            {externalImpacts.map((impact) => (
              <ExternalReferenceTable
                key={impact.tagId}
                impact={impact}
                willMerge={plan.mergeTagIds.has(impact.tagId)}
                choices={externalReferenceChoices}
                onChange={onChangeExternalReference}
              />
            ))}
          </div>
        </div>
      ) : null}
    </section>
  );
}

function ExternalReferenceTable({
  impact,
  willMerge,
  choices,
  onChange,
}: {
  impact: TagNameConflictImpact;
  willMerge: boolean;
  choices: ExternalReferenceChoices;
  onChange: (reference: TagExternalReference, action: ExternalReferenceAction | "") => void;
}) {
  const hasRestrictedLocation = (impact.externalReferences ?? []).some(
    (reference) => reference.accessLimitation != null || reference.rowCount == null,
  );
  return (
    <section className="overflow-hidden rounded-xl border border-border bg-card">
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border px-3 py-2">
        <span className="text-sm font-medium text-foreground">{displayValue(impact.tagName)} <span className="font-normal text-muted">#{impact.tagId}</span></span>
        <span className={`text-xs ${willMerge ? "text-amber-200" : "text-secondary"}`}>
          {willMerge
            ? hasRestrictedLocation ? "Owner repair required before merge" : "Review required before merge"
            : "No repair needed while this tag remains"}
        </span>
      </div>
      <div className="overflow-x-auto">
        <table className="min-w-[760px] w-full text-left text-sm">
          <thead className="text-xs text-secondary">
            <tr>
              <th className="px-3 py-2 font-medium">Table</th>
              <th className="px-3 py-2 font-medium">Column</th>
              <th className="px-3 py-2 font-medium">Rows</th>
              <th className="px-3 py-2 font-medium">Tag deletion policy</th>
              <th className="px-3 py-2 font-medium">Database action</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-border">
            {(impact.externalReferences ?? []).map((reference) => (
              <tr key={reference.referenceKey}>
                <td className="px-3 py-2 font-mono text-xs text-foreground">{reference.schemaName}.{reference.tableName}</td>
                <td className="px-3 py-2 font-mono text-xs text-foreground">{reference.columnName}</td>
                <td className={`px-3 py-2 tabular-nums ${reference.rowCount == null ? "text-amber-200" : "text-foreground"}`}>
                  {reference.rowCount == null ? "Unknown" : reference.rowCount.toLocaleString()}
                </td>
                <td className="px-3 py-2 text-secondary">{reference.deleteBehavior}</td>
                <td className="px-3 py-2">
                  {willMerge && reference.accessLimitation == null && reference.rowCount != null ? (
                    <select
                      value={choices[externalReferenceKey(reference)] ?? ""}
                      onChange={(event) => onChange(reference, event.target.value as ExternalReferenceAction | "")}
                      aria-label={`Database action for ${reference.schemaName}.${reference.tableName}.${reference.columnName} on tag ${impact.tagId}`}
                      className="w-full rounded-lg border border-border bg-surface px-2 py-1.5 text-sm text-foreground"
                    >
                      <option value="">Choose action…</option>
                      <option value="update-to-survivor">Update rows to survivor</option>
                      <option value="delete-rows">Delete rows</option>
                    </select>
                  ) : willMerge ? (
                    <span className="text-xs text-amber-200">Use extension or database administrator</span>
                  ) : <span className="text-xs text-muted">Keep unchanged</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function ImpactRow({ impact, recommended, selected, selectable, willMerge, radioGroup, onSelect }: { impact: TagNameConflictImpact; recommended: boolean; selected: boolean; selectable: boolean; willMerge: boolean; radioGroup: string; onSelect: () => void }) {
  return (
    <tr className={selected ? "bg-accent/5" : ""}>
      <td className="px-3 py-2">
        {selectable ? <input type="radio" name={`survivor-${radioGroup}`} checked={selected} onChange={onSelect} aria-label={`Keep tag ${impact.tagName}`} className="accent-accent" /> : <span className="text-muted">—</span>}
      </td>
      <td className="px-3 py-2 font-medium text-foreground">
        <span>{displayValue(impact.tagName)} <span className="font-normal text-muted">#{impact.tagId}</span></span>
        {recommended ? <span className="ml-2 rounded bg-emerald-500/15 px-1.5 py-0.5 text-[10px] font-semibold uppercase text-emerald-200">Recommended</span> : null}
      </td>
      <td className={`px-3 py-2 ${willMerge ? "text-amber-200" : "text-secondary"}`}>{selected ? "Survivor" : willMerge ? "Merge" : "Keep separate"}</td>
      <CountCell value={impact.referenceCount} />
      <CountCell value={impact.taggedEntityCount} />
      <CountCell value={impact.segmentCount} />
      <CountCell value={impact.parentRelationshipCount} />
      <CountCell value={impact.childRelationshipCount} />
      <CountCell value={impact.ratingCount} />
      <CountCell value={impact.otherMetadataCount} />
      {(impact.externalReferences ?? []).some((reference) => reference.rowCount == null)
        ? <td className="px-3 py-2 text-amber-200">Unknown</td>
        : <CountCell value={impact.extensionMetadataCount} />}
    </tr>
  );
}

function describePlan(plan: GroupPlan | null) {
  if (!plan) return "Cove will apply the selected claim actions transactionally and refresh the scan afterward.";
  const pieces = [
    plan.mergeTagIds.size > 0 ? `merge ${plan.mergeTagIds.size} tag${plan.mergeTagIds.size === 1 ? "" : "s"}` : null,
    plan.removedAliasCount > 0 ? `remove ${plan.removedAliasCount} alias${plan.removedAliasCount === 1 ? "" : "es"}` : null,
    plan.renamedClaimCount > 0 ? `rename ${plan.renamedClaimCount} claim${plan.renamedClaimCount === 1 ? "" : "s"}` : null,
    plan.externalUpdatedRowCount > 0 ? `update ${plan.externalUpdatedRowCount} non-core row reference${plan.externalUpdatedRowCount === 1 ? "" : "s"} to the survivor` : null,
    plan.externalDeletedRowCount > 0 ? `delete ${plan.externalDeletedRowCount} non-core row reference${plan.externalDeletedRowCount === 1 ? "" : "s"}` : null,
  ].filter(Boolean);
  const deleteWarning = plan.externalDeletedRowCount > 0 ? " Deleting non-core rows may activate extension-defined triggers or cascades." : "";
  return `Cove will ${pieces.length > 0 ? pieces.join(", ") : "normalize the affected values"}. The operation is transactional and the scan refreshes afterward.${deleteWarning}`;
}

function CountCell({ value }: { value: number }) {
  return <td className={`px-3 py-2 tabular-nums ${value > 0 ? "text-foreground" : "text-muted"}`}>{value.toLocaleString()}</td>;
}

function StatusBox({ tone, children }: { tone: "success" | "error"; children: ReactNode }) {
  return <div className={`rounded-2xl border p-4 text-sm ${tone === "success" ? "border-emerald-500/30 bg-emerald-500/10 text-emerald-100" : "border-red-500/30 bg-red-500/10 text-red-100"}`}>{children}</div>;
}

function displayValue(value: string) {
  return value.trim().length === 0 ? "<blank>" : value;
}
