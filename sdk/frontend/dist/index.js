/**
 * @cove/extension-sdk
 *
 * SDK for building Cove UI extensions.
 *
 * @example
 * ```tsx
 * import { defineExtension, request, useEntityList } from "@cove/extension-sdk";
 * ```
 */
export { ENTITY_COVER_EDITOR_SLOT } from "./types";
export { ENTITY_MEDIA_TARGET, MEDIA_PLAYER_ACTIONS_SLOT, MEDIA_PLAYER_OVERLAY_SLOT, } from "./types";
// Extension definition helper
export { defineExtension } from "./define";
// API utilities
export { request, ApiError, createExtensionStore, runExtensionJob } from "./api";
// Hooks
export { useFetch, useExtensionStore, useEntityList } from "./hooks";
