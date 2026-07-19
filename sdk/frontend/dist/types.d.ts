/**
 * @cove/extension-sdk — Types for the Cove extension system.
 *
 * These types mirror the host app's extension manifest contracts.
 * Extension authors should use these for type-safe development.
 */
export type EntityType = "video" | "performer" | "studio" | "tag" | "gallery" | "image" | "group" | "audio" | "text" | "face" | "segment";
export type CriterionModifier = "EQUALS" | "NOT_EQUALS" | "GREATER_THAN" | "LESS_THAN" | "INCLUDES" | "EXCLUDES" | "INCLUDES_ALL" | "EXCLUDES_ALL" | "IS_NULL" | "NOT_NULL" | "BETWEEN" | "NOT_BETWEEN" | "MATCHES_REGEX" | "NOT_MATCHES_REGEX";
export type ListCriterionType = "string" | "number" | "date" | "timestamp" | "duration" | "rating" | "multiId" | "enum" | "bool";
export type CustomFieldType = "text" | "longText" | "number" | "boolean" | "date" | "timestamp" | "duration" | "percent" | "url" | "enum" | "tag" | "performer" | "studio" | "video" | "gallery" | "image" | "group";
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
export interface ListFilterOption {
    value: string;
    label: string;
}
export interface ListFilterContribution {
    id: string;
    entityType: EntityType;
    label: string;
    criterionType: ListCriterionType | CustomFieldType;
    extensionId: string;
    filterKey?: string;
    customFieldKey?: string;
    customFieldType?: CustomFieldType;
    entityReferenceType?: EntityType;
    modifiers?: CriterionModifier[];
    options?: ListFilterOption[];
    order?: number;
}
export interface ListSortContribution {
    id: string;
    entityType: EntityType;
    label: string;
    extensionId: string;
    sortKey?: string;
    customFieldKey?: string;
    customFieldType?: CustomFieldType;
    order?: number;
}
export interface UIManifestListContributions {
    listFilters?: ListFilterContribution[];
    listSorts?: ListSortContribution[];
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