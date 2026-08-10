import { AlertTriangle, ArrowRight } from "lucide-react";
import { useAuth } from "../../auth/AuthContext";
import { useTagNameConflictSummary, TAG_NAME_CONFLICTS_PERMISSION } from "./useTagNameConflicts";

export const TAG_NAME_CONFLICT_TOOL_PATH = "/settings/operations/tag-name-conflicts";

export function TagNameConflictWarning() {
  const { hasPermission } = useAuth();
  const canManage = hasPermission(TAG_NAME_CONFLICTS_PERMISSION);
  const summary = useTagNameConflictSummary(canManage);
  if (!canManage)
    return null;
  if (!summary.data)
    return <TagNameConflictReadinessUnknownBanner checking={summary.isLoading} />;
  if (summary.data.unresolvedGroupCount === 0)
    return null;

  return <TagNameConflictWarningBanner unresolvedGroupCount={summary.data.unresolvedGroupCount} />;
}

export function TagNameConflictReadinessUnknownBanner({ checking }: { checking: boolean }) {
  return (
    <div className="border-b border-amber-500/40 bg-amber-500/10 px-3 py-3 text-amber-100 sm:px-4 md:px-6" role="alert">
      <div className="mx-auto flex w-full items-start gap-3">
        <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-300" aria-hidden="true" />
        <div className="min-w-0 flex-1 text-sm">
          <span className="font-semibold">{checking ? "Checking Cove 1.3.0 tag readiness." : "Cove 1.3.0 tag readiness could not be determined."}</span>{" "}
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

export function TagNameConflictWarningBanner({ unresolvedGroupCount }: { unresolvedGroupCount: number }) {
  return (
    <div className="border-b border-amber-500/40 bg-amber-500/10 px-3 py-3 text-amber-100 sm:px-4 md:px-6" role="alert">
      <div className="mx-auto flex w-full items-start gap-3">
        <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-300" aria-hidden="true" />
        <div className="min-w-0 flex-1 text-sm">
          <span className="font-semibold">Tag names will become globally unique in Cove 1.3.0.</span>{" "}
          Some current tags or aliases conflict after trimming. Review and resolve them before upgrading.
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

export function TagNameConflictReadinessStatus({ unresolvedGroupCount }: { unresolvedGroupCount: number }) {
  if (unresolvedGroupCount === 0) {
    return <p className="text-sm text-emerald-300">Ready: no tag-name conflicts would block the Cove 1.3.0 namespace preflight.</p>;
  }

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-amber-500/30 bg-amber-500/10 p-3 sm:flex-row sm:items-center sm:justify-between" role="status">
      <div>
        <p className="text-sm font-semibold text-amber-100">Action required before Cove 1.3.0</p>
        <p className="mt-1 text-sm text-amber-100/80">
          {unresolvedGroupCount} unresolved tag-name conflict {unresolvedGroupCount === 1 ? "group would" : "groups would"} block the upgrade preflight.
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

export function TagNameConflictReadinessUnknownStatus({ checking }: { checking: boolean }) {
  return (
    <div className="flex flex-col gap-3 rounded-xl border border-amber-500/30 bg-amber-500/10 p-3 sm:flex-row sm:items-center sm:justify-between" role="status">
      <div>
        <p className="text-sm font-semibold text-amber-100">{checking ? "Checking tag namespace readiness" : "Tag namespace readiness unknown"}</p>
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
