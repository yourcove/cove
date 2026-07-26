/**
 * Shared Component Barrel for Extensions
 *
 * This module is exposed to extensions via @cove/runtime/components.
 * Extensions import shared components from here instead of reimplementing them.
 * All components use the host's CSS variables and theme context.
 *
 * @module @cove/runtime/components
 */

// ─── Utilities ────────────────────────────────────────────────────────────
export {
  TagBadge,
  formatDuration,
  formatFileSize,
  formatDate,
  getResolutionLabel,
  CustomFieldsDisplay,
  CustomFieldsEditor,
} from "./shared";

// ─── Rating ───────────────────────────────────────────────────────────────
export {
  InteractiveRating,
  RatingBadge,
  RatingBanner,
  RatingField,
  getRatingBannerColor,
  convertToRatingFormat,
  convertFromRatingFormat,
  formatDisplayRating,
  getRatingMax,
  getRatingStep,
  getRatingInputLabel,
  normalizeRatingOptions,
  defaultRatingSystemOptions,
} from "./Rating";

// ─── Dialogs / Modals ────────────────────────────────────────────────────
export { ConfirmDialog } from "./ConfirmDialog";
export {
  EditModal,
  Field,
  TextInput,
  TextArea,
  NumberInput,
  SelectInput,
  SaveButton,
} from "./EditModal";
export { ImageInput } from "./ImageInput";
export { openTutorialStoryboard } from "./TutorialStoryboardDialog";
export { registerManualContext, useManualContext } from "./ManualContext";
export type { TutorialOpenRequest } from "./ManualContext";

// ─── Entity Cards & Popovers ─────────────────────────────────────────────
export {
  PopoverButton,
  VideoCardPopovers,
  PerformerTile,
  VideoCard,
  VideoTile,
  ImageTile,
} from "./EntityCards";

// ─── Players / Viewers ────────────────────────────────────────────────────
export { VideoPlayer } from "./VideoPlayer";
export { Lightbox } from "./Lightbox";
export type { LightboxImage } from "./Lightbox";

// ─── Detail Page Building Blocks ──────────────────────────────────────────
export { MediaDetailLayout } from "./MediaDetailLayout/MediaDetailLayout";
export type {
  MediaDetailLayoutProps,
  MediaDetailTab,
  MediaDetailSectionProps,
} from "./MediaDetailLayout/types";
export { DetailListPagination, DetailListToolbar } from "./DetailListToolbar";
export { ListPage } from "./ListPage";
export type { DisplayMode } from "./ListPage";
// Cove's canonical multi-mode results renderer (grid / list / wall / feed / vertical) for a given entity type —
// lets extensions render entity lists exactly like the native pages instead of reimplementing each layout.
export { RelatedEntityListView, getRelatedEntityDisplayModes } from "./RelatedEntityListView";
export { FilterButton, FilterDialog, VIDEO_CRITERIA } from "./FilterDialog";
export type { CriterionDefinition } from "./FilterDialog";
export { BulkEditDialog } from "./BulkEditDialog";
export { getDefaultFilter } from "./SavedFilterMenu";
export { Pager } from "./Pager";
export { VIDEO_SORT_OPTIONS } from "./videoSortOptions";

// ─── Hooks ────────────────────────────────────────────────────────────────
export { useMultiSelect } from "../hooks/useMultiSelect";
export { useKeySequence } from "../hooks/useKeySequence";
export { useListUrlState } from "../hooks/useListUrlState";

// ─── App Config (extensions run in the same React tree) ───────────────────
export { useAppConfig } from "../state/AppConfigContext";

// ─── Types ────────────────────────────────────────────────────────────────
export type { FindFilter } from "../api/types";
