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
  CriterionModifier,
  ListCriterionType,
  CustomFieldType,
  EntityTabProps,
  SlotProps,
  PageProps,
  DetailPageProps,
  NavigateTarget,
  EntityMediaSurface,
  EntityMediaFit,
  EntityMediaRenderProps,
  FindFilter,
  ListFilterOption,
  ListFilterContribution,
  ListSortContribution,
  UIManifestListContributions,
  ExtensionAction,
  ExtensionActionHandler,
  ExtensionModule,
} from "./types";

export { ENTITY_MEDIA_TARGET } from "./types";

// Extension definition helper
export { defineExtension } from "./define";

// API utilities
export { request, ApiError, createExtensionStore, runExtensionJob } from "./api";

// Hooks
export { useFetch, useExtensionStore, useEntityList } from "./hooks";
