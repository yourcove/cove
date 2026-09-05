/** Stable extension slot rendered inside Cove's native entity cover editor. */
export const ENTITY_COVER_EDITOR_SLOT = "entity-cover-editor" as const;

/** Host-owned context supplied to entity cover editor contributions. */
export interface EntityCoverEditorContext {
  entityType:
    "video" | "performer" | "studio" | "tag" | "gallery" | "image" | "group" | "audio" | "text" | "face" | "segment";
  entityId: number;
  coverKey: "primary" | "front" | "back";
  currentImageUrl?: string | null;
  canEdit: boolean;
}
