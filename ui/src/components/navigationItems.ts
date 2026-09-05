import {
  Bookmark,
  Building2,
  FileText,
  Film,
  Fingerprint,
  Headphones,
  Image,
  ImageIcon,
  Layers,
  Tags,
  Users,
  type LucideIcon,
} from "lucide-react";
import type { NavPage } from "../auth/visibility";

export interface BuiltInNavigationItem {
  page: NavPage;
  label: string;
  icon: LucideIcon;
}

export const BUILT_IN_NAVIGATION_ITEMS: BuiltInNavigationItem[] = [
  { page: "videos", label: "Videos", icon: Film },
  { page: "audios", label: "Audios", icon: Headphones },
  { page: "texts", label: "Texts", icon: FileText },
  { page: "segments", label: "Segments", icon: Bookmark },
  { page: "images", label: "Images", icon: ImageIcon },
  { page: "faces", label: "Faces", icon: Fingerprint },
  { page: "galleries", label: "Galleries", icon: Image },
  { page: "performers", label: "Performers", icon: Users },
  { page: "studios", label: "Studios", icon: Building2 },
  { page: "tags", label: "Tags", icon: Tags },
  { page: "groups", label: "Groups", icon: Layers },
];

const BUILT_IN_NAVIGATION_ICONS = new Map(BUILT_IN_NAVIGATION_ITEMS.map(({ page, icon }) => [page, icon]));

export function getBuiltInNavigationIcon(page: string): LucideIcon | undefined {
  return BUILT_IN_NAVIGATION_ICONS.get(page as NavPage);
}
