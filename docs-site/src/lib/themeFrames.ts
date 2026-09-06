import { withBase } from './paths';
import { themeFrameDefinitions } from './themeFrameDefinitions';

export const themeFrames = themeFrameDefinitions.map(({ name, themeId: _themeId, themeLabel: _themeLabel, ...frame }) => ({
  ...frame,
  src: withBase(`images/screenshots/theme-reel-thumbnails/${name}.webp`),
}));
