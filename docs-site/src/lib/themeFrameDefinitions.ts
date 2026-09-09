export interface ThemeFrameDefinition {
  name: string;
  themeId: string;
  themeLabel?: string;
  alt: string;
  label: string;
}

export const themeFrameDefinitions: ThemeFrameDefinition[] = [
  { name: 'default', themeId: 'default', alt: 'Cove videos page using the default theme.', label: 'Default' },
  { name: 'dark-midnight', themeId: 'dark-midnight', alt: 'Cove videos page using the dark midnight theme.', label: 'Dark Midnight' },
  { name: 'dark-emerald', themeId: 'dark-emerald', alt: 'Cove videos page using the dark emerald theme.', label: 'Dark Emerald' },
  { name: 'dark-rose', themeId: 'dark-rose', themeLabel: 'Dark Rosé', alt: 'Cove videos page using the dark rose theme.', label: 'Dark Rose' },
  { name: 'copper-noir', themeId: 'copper-noir', alt: 'Cove videos page using the copper noir theme.', label: 'Copper Noir' },
  { name: 'golden-hour', themeId: 'golden-hour', alt: 'Cove videos page using the golden hour theme.', label: 'Golden Hour' },
  { name: 'signal-dark', themeId: 'signal-dark', alt: 'Cove videos page using the signal dark theme.', label: 'Signal Dark' },
  { name: 'liquid-glass', themeId: 'liquid-glass', alt: 'Cove videos page using the liquid glass theme.', label: 'Liquid Glass' },
  { name: 'sunset-gradient', themeId: 'sunset-gradient', alt: 'Cove videos page using the sunset gradient theme.', label: 'Sunset Gradient' },
  { name: 'cyberpunk', themeId: 'cyberpunk', alt: 'Cove videos page using the cyberpunk theme.', label: 'Cyberpunk' },
  { name: 'deep-space', themeId: 'deep-space', alt: 'Cove videos page using the deep space theme.', label: 'Deep Space' },
  { name: 'synthwave', themeId: 'synthwave', alt: 'Cove videos page using the synthwave theme.', label: 'Synthwave' },
  { name: 'ember', themeId: 'ember', alt: 'Cove videos page using the ember theme.', label: 'Ember' },
  { name: 'cinema-dark-floating', themeId: 'cinema-dark', alt: 'Cove videos page using the cinema dark theme.', label: 'Cinema Dark' },
  { name: 'dark-ocean-glass', themeId: 'dark-ocean', alt: 'Cove videos page using the dark ocean theme.', label: 'Dark Ocean' },
  { name: 'rainbow-gradient', themeId: 'rainbow', alt: 'Cove videos page using the rainbow theme.', label: 'Rainbow' },
];
