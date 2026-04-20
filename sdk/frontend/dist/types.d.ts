/**
 * @cove/extension-sdk — Types for the Cove extension system.
 *
 * These types mirror the host app's extension manifest contracts.
 * Extension authors should use these for type-safe development.
 */
export type EntityType = "scene" | "performer" | "studio" | "tag" | "gallery" | "image" | "group";
/** Props passed to extension components rendered in entity detail tabs. */
export interface EntityTabProps {
    entityId: number;
}
/** Props passed to extension components rendered in slots. */
export interface SlotProps<TContext = Record<string, unknown>> {
    context: TContext;
}
/** Props passed to extension page components. */
export interface PageProps {
    onNavigate: (route: NavigateTarget) => void;
    params?: Record<string, string>;
}
/** Props passed to extension detail page components. */
export interface DetailPageProps {
    id: number;
    onNavigate: (route: NavigateTarget) => void;
}
/** Navigation target for onNavigate callback. */
export interface NavigateTarget {
    page: string;
    id?: number;
    [key: string]: unknown;
}
export interface FindFilter {
    page?: number;
    perPage?: number;
    sort?: string;
    direction?: "asc" | "desc";
    query?: string;
}
/** The default export expected from an extension's JS bundle. */
export interface ExtensionModule {
    /** Map of component name → React component. */
    components: Record<string, React.FC<any>>;
    /** Optional lifecycle hook called after the extension is loaded. */
    onLoad?: () => void | Promise<void>;
    /** Optional cleanup hook called before unload. */
    onUnload?: () => void;
}
//# sourceMappingURL=types.d.ts.map