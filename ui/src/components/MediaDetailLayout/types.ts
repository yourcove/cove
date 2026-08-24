import type { ReactNode } from "react";

export type MediaDetailAspectRatio = "video" | "square" | "auto" | "compact";
export type MediaDetailTabPlacement = "sidebar" | "header";
export type MediaDetailMediaPosition = "left" | "right";

export interface MediaDetailTab {
  key: string;
  label: string;
  icon?: ReactNode;
  count?: number;
  disabled?: boolean;
  manualContexts?: string[];
}

export interface MediaDetailKeyboardShortcut {
  id?: string;
  key: string;
  description: string;
  handler: () => void;
}

export interface EngagementBarMetric {
  label: string;
  value: ReactNode;
  icon?: ReactNode;
  onClick?: () => void;
  title?: string;
  active?: boolean;
}

export interface EngagementBarProps {
  primaryContent?: ReactNode;
  rating?: number | null;
  favorite?: boolean;
  favoritePending?: boolean;
  ratingReadOnly?: boolean;
  onFavoriteChange?: (favorite: boolean) => void;
  onRatingChange?: (rating: number) => void;
  additionalMetrics?: EngagementBarMetric[];
  className?: string;
}

export interface MetadataPanelItem {
  label: string;
  value: ReactNode;
}

export interface MediaDetailLayoutProps {
  title: ReactNode;
  subtitle?: ReactNode;
  backLabel?: string;
  onGoBack?: () => void;
  media?: ReactNode;
  mediaSticky?: boolean;
  mediaAspectRatio?: MediaDetailAspectRatio;
  mediaPosition?: MediaDetailMediaPosition;
  // When true, the media slot renders without the rounded/bordered frame and
  // without an aspect-ratio container. Use for video players that should fill
  // the available column height naturally.
  mediaFullBleed?: boolean;
  // Optional content rendered above the title (e.g., studio logo).
  headerImage?: ReactNode;
  tabs?: MediaDetailTab[];
  activeTab?: string;
  onTabChange?: (key: string) => void;
  tabPlacement?: MediaDetailTabPlacement;
  engagement?: EngagementBarProps;
  actions?: ReactNode;
  keyboardShortcuts?: MediaDetailKeyboardShortcut[];
  isLoading?: boolean;
  error?: string | null;
  children?: ReactNode;
}

export interface MediaDetailSectionProps {
  children?: ReactNode;
  className?: string;
}
