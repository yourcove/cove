import { tagApplications } from "../api/client";
import type { TagApplication } from "../api/types";
import { EntityReferenceMultiSelector, EntityReferenceValue } from "./EntityReferenceSelector";
import { TagBadge } from "./shared";

export type PerformerContextHostType = "video" | "image" | "audio" | "text";

interface PerformerContextTagEditorProps {
  performerIds: number[];
  contextTagIdsByPerformer: Record<number, number[]>;
  onChange: (performerId: number, tagIds: number[]) => void;
  inputClassName?: string;
}

export function PerformerContextTagEditor({
  performerIds,
  contextTagIdsByPerformer,
  onChange,
  inputClassName,
}: PerformerContextTagEditorProps) {
  if (performerIds.length === 0) {
    return null;
  }

  return (
    <div className="space-y-3 rounded-lg border border-border bg-surface/40 p-3">
      {performerIds.map((performerId) => {
        const tagIds = contextTagIdsByPerformer[performerId] ?? [];

        return (
          <div key={performerId} className="rounded-lg border border-border bg-card/70 p-3">
            <div className="mb-2 flex items-center justify-between gap-3">
              <div className="min-w-0 text-sm font-medium text-foreground">
                <EntityReferenceValue entityType="performer" value={performerId} />
              </div>
              <div className="text-xs text-muted">
                {tagIds.length} tag{tagIds.length === 1 ? "" : "s"}
              </div>
            </div>
            <EntityReferenceMultiSelector
              entityType="tag"
              values={tagIds}
              onChange={(nextTagIds) =>
                onChange(performerId, Array.from(new Set(nextTagIds.filter((tagId) => tagId > 0))))
              }
              placeholder="Search tags for this occurrence..."
              emptyMessage="No tags found"
              inputClassName={inputClassName}
            />
          </div>
        );
      })}
    </div>
  );
}

export function buildPerformerContextTagIds(applications: TagApplication[] | undefined): Record<number, number[]> {
  const result: Record<number, number[]> = {};
  for (const application of applications ?? []) {
    if (application.contextType !== "performer" || application.contextId == null) {
      continue;
    }

    result[application.contextId] = [...(result[application.contextId] ?? []), application.tag.id];
  }

  return result;
}

export async function syncPerformerContextTags(
  hostType: PerformerContextHostType,
  hostId: number,
  existingApplications: TagApplication[],
  desiredByPerformer: Record<number, number[]>,
  selectedPerformerIds: number[],
) {
  const selectedPerformers = new Set(selectedPerformerIds);
  const desiredKeys = new Set<string>();

  for (const [performerIdText, tagIds] of Object.entries(desiredByPerformer)) {
    const performerId = Number(performerIdText);
    if (!selectedPerformers.has(performerId)) {
      continue;
    }

    for (const tagId of tagIds) {
      if (tagId > 0) {
        desiredKeys.add(`${performerId}:${tagId}`);
      }
    }
  }

  const existingContextApplications = existingApplications.filter(
    (application) => application.contextType === "performer" && application.contextId != null,
  );

  for (const application of existingContextApplications) {
    const key = `${application.contextId}:${application.tag.id}`;
    if (!desiredKeys.has(key)) {
      await tagApplications.delete(application.id);
    }
  }

  const existingKeys = new Set(
    existingContextApplications.map((application) => `${application.contextId}:${application.tag.id}`),
  );
  for (const [performerIdText, tagIds] of Object.entries(desiredByPerformer)) {
    const performerId = Number(performerIdText);
    if (!selectedPerformers.has(performerId)) {
      continue;
    }

    for (const tagId of tagIds) {
      if (tagId <= 0) {
        continue;
      }

      const key = `${performerId}:${tagId}`;
      if (existingKeys.has(key)) {
        continue;
      }

      await tagApplications.create({
        hostType,
        hostId,
        contextType: "performer",
        contextId: performerId,
        tagId,
        sourceKey: "user",
      });
    }
  }
}

export function getPerformerContextTags(applications: TagApplication[] | undefined, performerId: number) {
  return (applications ?? []).filter(
    (application) => application.contextType === "performer" && application.contextId === performerId,
  );
}

export function PerformerContextTagList({
  contextTags,
  onNavigate,
}: {
  contextTags: TagApplication[];
  onNavigate?: (route: any) => void;
}) {
  if (contextTags.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-wrap gap-1.5">
      {contextTags.map((application) => (
        <TagBadge
          key={application.id}
          name={application.tag.name}
          tag={application.tag}
          provenance={[toTagProvenance(application)]}
          onClick={onNavigate ? () => onNavigate({ page: "tag", id: application.tag.id }) : undefined}
        />
      ))}
    </div>
  );
}

function toTagProvenance(application: TagApplication) {
  return {
    sourceKey: application.sourceKey,
    sourceRunId: application.sourceRunId ?? undefined,
    modelKey: application.modelKey ?? undefined,
    confidence: application.confidence ?? undefined,
    appliedAt: application.appliedAt,
    contextType: application.contextType ?? undefined,
    contextId: application.contextId ?? undefined,
    totalDurationSec: application.totalDurationSec ?? undefined,
    hostDurationSec: application.hostDurationSec ?? undefined,
  };
}
