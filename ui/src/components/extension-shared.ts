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

// ─── Entity Cards & Popovers ─────────────────────────────────────────────
export {
  PopoverButton,
  SceneCardPopovers,
  PerformerTile,
  SceneCard,
  SceneTile,
} from "./EntityCards";

// ─── Detail Page Building Blocks ──────────────────────────────────────────
export { DetailListToolbar } from "./DetailListToolbar";
export { ListPage } from "./ListPage";
export type { DisplayMode } from "./ListPage";
export { FilterButton, FilterDialog } from "./FilterDialog";
export type { CriterionDefinition } from "./FilterDialog";
export { BulkEditDialog } from "./BulkEditDialog";
export { getDefaultFilter } from "./SavedFilterMenu";
export { Pager } from "./Pager";

// ─── Hooks ────────────────────────────────────────────────────────────────
export { useMultiSelect } from "../hooks/useMultiSelect";
export { useKeySequence } from "../hooks/useKeySequence";
export { useListUrlState } from "../hooks/useListUrlState";

// ─── App Config (extensions run in the same React tree) ───────────────────
export { useAppConfig } from "../state/AppConfigContext";

// ─── Types ────────────────────────────────────────────────────────────────
export type { FindFilter } from "../api/types";
