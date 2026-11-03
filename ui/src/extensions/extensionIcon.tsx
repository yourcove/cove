import { forwardRef, type ReactNode } from "react";
import { Puzzle, type LucideIcon, type LucideProps } from "lucide-react";
import { DynamicIcon, iconNames, type IconName } from "lucide-react/dynamic.js";

// ============================================================================
// Icon resolver — maps manifest icon names to Lucide components
// ============================================================================
const validIconNames = new Set<string>(iconNames);
const iconComponents = new Map<IconName, LucideIcon>();

/** Lucide component for a manifest icon name, or Puzzle when the name is missing or unknown. */
export function resolveExtensionIcon(name?: string): LucideIcon {
  const normalizedName = name?.toLowerCase();
  if (!normalizedName || !validIconNames.has(normalizedName)) return Puzzle;

  const iconName = normalizedName as IconName;
  let Icon = iconComponents.get(iconName);
  if (!Icon) {
    // Keep the component type stable when manifest refreshes register routes again.
    Icon = forwardRef<SVGSVGElement, LucideProps>((props, ref) => <DynamicIcon {...props} name={iconName} ref={ref} />);
    Icon.displayName = `ExtensionIcon(${iconName})`;
    iconComponents.set(iconName, Icon);
  }
  return Icon;
}

/**
 * Icon for an extension-contributed detail tab. A tab that declares no icon gets none, so the
 * detail layout's own default applies.
 */
export function extensionTabIcon(name?: string): ReactNode {
  if (!name) return undefined;
  const Icon = resolveExtensionIcon(name);
  return <Icon className="h-4 w-4" />;
}
