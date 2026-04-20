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
// Extension definition helper
export { defineExtension } from "./define";
// API utilities
export { request, ApiError, createExtensionStore, runExtensionJob } from "./api";
// Hooks
export { useFetch, useExtensionStore, useEntityList } from "./hooks";
