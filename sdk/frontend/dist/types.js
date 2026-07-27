/**
 * @cove/extension-sdk — Types for the Cove extension system.
 *
 * These types mirror the host app's extension manifest contracts.
 * Extension authors should use these for type-safe development.
 */
/** Stable extension slot rendered inside Cove's native entity cover editor. */
export const ENTITY_COVER_EDITOR_SLOT = "entity-cover-editor";
/** Stable host component target for primary entity media overrides. */
export const ENTITY_MEDIA_TARGET = "entity.media";
// ── Media-player contribution contracts ───────────────────────────────────────
/** Stable extension slot rendered with Cove's native media-player actions. */
export const MEDIA_PLAYER_ACTIONS_SLOT = "media-player-actions";
/** Stable extension slot positioned over the displayed media content. */
export const MEDIA_PLAYER_OVERLAY_SLOT = "media-player-overlay";
