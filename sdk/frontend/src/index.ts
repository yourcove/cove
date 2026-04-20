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

// Types
export type {
  EntityType,
  EntityTabProps,
  SlotProps,
  PageProps,
  DetailPageProps,
  NavigateTarget,
  FindFilter,
  ExtensionModule,
} from "./types";

// Extension definition helper
export { defineExtension } from "./define";

// API utilities
export { request, ApiError, createExtensionStore, runExtensionJob } from "./api";

// Hooks
export { useFetch, useExtensionStore, useEntityList } from "./hooks";
