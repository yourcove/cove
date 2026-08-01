import { withBase } from './paths';

const frames = [
  { name: 'default', alt: 'Cove videos page using the default theme.', label: 'Default' },
  { name: 'dark-midnight', alt: 'Cove videos page using the dark midnight theme.', label: 'Dark Midnight' },
  { name: 'dark-emerald', alt: 'Cove videos page using the dark emerald theme.', label: 'Dark Emerald' },
  { name: 'dark-rose', alt: 'Cove videos page using the dark rose theme.', label: 'Dark Rose' },
  { name: 'copper-noir', alt: 'Cove videos page using the copper noir theme.', label: 'Copper Noir' },
  { name: 'golden-hour', alt: 'Cove videos page using the golden hour theme.', label: 'Golden Hour' },
  { name: 'signal-dark', alt: 'Cove videos page using the signal dark theme.', label: 'Signal Dark' },
  { name: 'liquid-glass', alt: 'Cove videos page using the liquid glass theme.', label: 'Liquid Glass' },
  { name: 'sunset-gradient', alt: 'Cove videos page using the sunset gradient theme.', label: 'Sunset Gradient' },
  { name: 'cyberpunk', alt: 'Cove videos page using the cyberpunk theme.', label: 'Cyberpunk' },
  { name: 'deep-space', alt: 'Cove videos page using the deep space theme.', label: 'Deep Space' },
  { name: 'synthwave', alt: 'Cove videos page using the synthwave theme.', label: 'Synthwave' },
  { name: 'ember', alt: 'Cove videos page using the ember theme.', label: 'Ember' },
  { name: 'cinema-dark-floating', alt: 'Cove videos page using the cinema dark theme.', label: 'Cinema Dark' },
  { name: 'dark-ocean-glass', alt: 'Cove videos page using the dark ocean theme.', label: 'Dark Ocean' },
  { name: 'rainbow-gradient', alt: 'Cove videos page using the rainbow theme.', label: 'Rainbow' },
];

export const themeFrames = frames.map(({ name, ...frame }) => ({
  ...frame,
  src: withBase(`images/screenshots/theme-reel-thumbnails/${name}.webp`),
}));
