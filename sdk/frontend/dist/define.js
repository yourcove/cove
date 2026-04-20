/**
 * Helper to define an extension module with proper typing.
 * Use this as the default export of your extension's entry point.
 *
 * @example
 * ```tsx
 * import { defineExtension } from "@cove/extension-sdk";
 * import { AudiosPage } from "./AudiosPage";
 * import { AudioDetailPage } from "./AudioDetailPage";
 *
 * export default defineExtension({
 *   components: {
 *     AudiosPage,
 *     AudioDetailPage,
 *   },
 * });
 * ```
 */
export function defineExtension(module) {
    return module;
}
