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
export type { EntityType, EntityTabProps, SlotProps, PageProps, DetailPageProps, NavigateTarget, FindFilter, ExtensionModule, } from "./types";
export { defineExtension } from "./define";
export { request, ApiError, createExtensionStore, runExtensionJob } from "./api";
export { useFetch, useExtensionStore, useEntityList } from "./hooks";
//# sourceMappingURL=index.d.ts.map