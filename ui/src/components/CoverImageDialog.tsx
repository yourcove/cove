import { useState, type ReactNode } from "react";
import { ImageIcon, X } from "lucide-react";
import { ImageInput } from "./ImageInput";
import { ExtensionSlot } from "../router/RouteRegistry";
import { ENTITY_COVER_EDITOR_SLOT, type EntityCoverEditorContext } from "./EntityCoverEditorExtension";

interface CoverImageDialogProps {
  open: boolean;
  title: string;
  entityType: EntityCoverEditorContext["entityType"];
  entityId: number;
  coverKey?: EntityCoverEditorContext["coverKey"];
  currentImageUrl?: string | null;
  onUpload?: (file: File) => Promise<unknown>;
  onDelete?: () => Promise<unknown>;
  onClose: () => void;
  onSuccess?: () => void;
  aspectRatio?: string;
  objectFit?: "cover" | "contain";
  deleteLabel?: string;
  extraActions?: ReactNode | ((imageOperationPending: boolean) => ReactNode);
  externalPending?: boolean;
}

export function CoverImageDialog({
  open,
  title,
  entityType,
  entityId,
  coverKey = "primary",
  currentImageUrl,
  onUpload,
  onDelete,
  onClose,
  onSuccess,
  aspectRatio = "2/3",
  objectFit = "cover",
  deleteLabel = "Use Default",
  extraActions,
  externalPending = false,
}: CoverImageDialogProps) {
  const [imageOperationPending, setImageOperationPending] = useState(false);

  if (!open) return null;

  const handleSuccess = () => {
    onSuccess?.();
    onClose();
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 px-4" onClick={onClose}>
      <div
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-xl border border-border bg-surface p-4 shadow-2xl"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="mb-4 flex items-center justify-between gap-3">
          <h2 className="text-base font-semibold text-foreground">{title}</h2>
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg border border-border bg-card p-1.5 text-secondary hover:text-foreground"
            title="Close"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        {onUpload ? (
          <ImageInput
            currentImageUrl={currentImageUrl ?? undefined}
            onUpload={onUpload}
            onDelete={onDelete}
            onSuccess={handleSuccess}
            label="Cover"
            aspectRatio={aspectRatio}
            objectFit={objectFit}
            deleteLabel={deleteLabel}
            disabled={externalPending}
            onBusyChange={setImageOperationPending}
          />
        ) : (
          <div className="space-y-2">
            <div className="block text-sm font-medium text-secondary">Cover</div>
            <div className="overflow-hidden rounded-lg border border-border bg-card" style={{ aspectRatio }}>
              {currentImageUrl ? (
                <img
                  src={currentImageUrl}
                  alt="Cover source"
                  className={`h-full w-full ${objectFit === "contain" ? "object-contain p-2" : "object-cover"}`}
                />
              ) : (
                <div className="flex h-full w-full items-center justify-center text-muted">
                  <ImageIcon className="h-8 w-8" />
                </div>
              )}
            </div>
          </div>
        )}

        <ExtensionSlot<EntityCoverEditorContext>
          slot={ENTITY_COVER_EDITOR_SLOT}
          context={{ entityType, entityId, coverKey, currentImageUrl, canEdit: Boolean(onUpload || onDelete) }}
          contextResetKey={`${entityType}:${entityId}:${coverKey}`}
          fallback={null}
        />

        {extraActions ? (
          <div className="mt-3 border-t border-border pt-3">
            {typeof extraActions === "function" ? extraActions(imageOperationPending) : extraActions}
          </div>
        ) : null}
      </div>
    </div>
  );
}
