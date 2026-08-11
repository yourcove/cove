import { AlertTriangle, ArrowRight } from "lucide-react";
import { useAuth } from "../../auth/AuthContext";
import {
  ENTITY_NAME_CONFLICTS_PERMISSION,
  TAG_NAME_CONFLICTS_PERMISSION,
  useTagNameConflictSummary,
} from "./useTagNameConflicts";

export const TAG_NAME_CONFLICT_TOOL_PATH = "/settings/operations/name-conflicts";

export function TagNameConflictWarning() {
  const { hasPermission } = useAuth();
  const canManageEntityConflicts = hasPermission(ENTITY_NAME_CONFLICTS_PERMISSION);
  const canManageTagConflicts = canManageEntityConflicts || hasPermission(TAG_NAME_CONFLICTS_PERMISSION);
  const summary = useTagNameConflictSummary(canManageTagConflicts, canManageEntityConflicts);
  if (!canManageTagConflicts)
    return null;
  if (!summary.data)
    return <TagNameConflictReadinessUnknownBanner checking={summary.isLoading} includeEntityConflicts={canManageEntityConflicts} />;
  if (summary.data.unresolvedGroupCount === 0)
    return null;

  return <TagNameConflictWarningBanner unresolvedGroupCount={summary.data.unresolvedGroupCount} includeEntityConflicts={canManageEntityConflicts} />;
}

export function TagNameConflictReadinessUnknownBanner({ checking, includeEntityConflicts = true }: { checking: boolean; includeEntityConflicts?: boolean }) {
  return (
    <div className="border-b border-amber-500/40 bg-amber-500/10 px-3 py-3 text-amber-100 sm:px-4 md:px-6" role="alert">
      <div className="mx-auto flex w-full items-start gap-3">
        <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-300" aria-hidden="true" />
        <div className="min-w-0 flex-1 text-sm">
          <span className="font-semibold">{checking ? `Checking Cove 1.3.0 ${includeEntityConflicts ? "name" : "tag-name"} readiness.` : `Cove 1.3.0 ${includeEntityConflicts ? "name" : "tag-name"} readiness could not be determined.`}</span>{" "}
          {checking ? "Do not upgrade until this check finishes." : "Retry the conflict scan before upgrading."}
        </div>
        <a
          href={TAG_NAME_CONFLICT_TOOL_PATH}
          className="inline-flex shrink-0 items-center gap-1 rounded-lg border border-amber-400/40 px-2.5 py-1.5 text-xs font-semibold text-amber-100 transition hover:border-amber-300 hover:bg-amber-400/10"
        >
          Open checker <ArrowRight className="h-3.5 w-3.5" aria-hidden="true" />
        </a>
      </div>
    </div>
  );
}

export function TagNameConflictWarningBanner({ unresolvedGroupCount, includeEntityConflicts = true }: { unresolvedGroupCount: number; includeEntityConflicts?: boolean }) {
  return (
    <div className="border-b border-amber-500/40 bg-amber-500/10 px-3 py-3 text-amber-100 sm:px-4 md:px-6" role="alert">
      <div className="mx-auto flex w-full items-start gap-3">
        <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-300" aria-hidden="true" />
        <div className="min-w-0 flex-1 text-sm">
          <span className="font-semibold">{includeEntityConflicts ? "Cove 1.3.0 will enforce new tag, performer, and studio name rules." : "Cove 1.3.0 will enforce globally unique tag names and aliases."}</span>{" "}
          {includeEntityConflicts ? "Some current identities conflict after trimming and case folding. Review and resolve them before upgrading." : "Some current tag names or aliases conflict after trimming and case folding. Review and resolve them before upgrading."}
        </div>
        <a
          href={TAG_NAME_CONFLICT_TOOL_PATH}
          className="inline-flex shrink-0 items-center gap-1 rounded-lg border border-amber-400/40 px-2.5 py-1.5 text-xs font-semibold text-amber-100 transition hover:border-amber-300 hover:bg-amber-400/10"
        >
          {unresolvedGroupCount} {unresolvedGroupCount === 1 ? "group" : "groups"}
          <ArrowRight className="h-3.5 w-3.5" aria-hidden="true" />
        </a>
      </div>
    </div>
  );
}

export function TagNameConflictReadinessStatus({ unresolvedGroupCount, includeEntityConflicts = true }: { unresolvedGroupCount: number; includeEntityConflicts?: boolean }) {
  if (unresolvedGroupCount === 0) {
    return <p className="text-sm text-emerald-300">{includeEntityConflicts ? "Ready: no tag, performer, or studio name conflicts would block the Cove 1.3.0 preflight." : "Ready: no tag-name conflicts would block the Cove 1.3.0 preflight."}</p>;
  }

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-amber-500/30 bg-amber-500/10 p-3 sm:flex-row sm:items-center sm:justify-between" role="status">
      <div>
        <p className="text-sm font-semibold text-amber-100">Action required before Cove 1.3.0</p>
        <p className="mt-1 text-sm text-amber-100/80">
          {unresolvedGroupCount} unresolved name conflict {unresolvedGroupCount === 1 ? "group would" : "groups would"} block the upgrade preflight.
        </p>
      </div>
      <a
        href={TAG_NAME_CONFLICT_TOOL_PATH}
        className="inline-flex shrink-0 items-center gap-1 text-sm font-semibold text-amber-200 hover:text-amber-100"
      >
        Review conflicts <ArrowRight className="h-4 w-4" aria-hidden="true" />
      </a>
    </div>
  );
}

export function TagNameConflictReadinessUnknownStatus({ checking, includeEntityConflicts = true }: { checking: boolean; includeEntityConflicts?: boolean }) {
  return (
    <div className="flex flex-col gap-3 rounded-xl border border-amber-500/30 bg-amber-500/10 p-3 sm:flex-row sm:items-center sm:justify-between" role="status">
      <div>
        <p className="text-sm font-semibold text-amber-100">{checking ? `Checking ${includeEntityConflicts ? "name-rule" : "tag-name"} readiness` : `${includeEntityConflicts ? "Name-rule" : "Tag-name"} readiness unknown`}</p>
        <p className="mt-1 text-sm text-amber-100/80">
          {checking ? "Wait for the compatibility scan to finish before upgrading." : "The compatibility scan failed. Retry it and confirm that no conflicts remain before upgrading."}
        </p>
      </div>
      <a
        href={TAG_NAME_CONFLICT_TOOL_PATH}
        className="inline-flex shrink-0 items-center gap-1 text-sm font-semibold text-amber-200 hover:text-amber-100"
      >
        Open checker <ArrowRight className="h-4 w-4" aria-hidden="true" />
      </a>
    </div>
  );
}
