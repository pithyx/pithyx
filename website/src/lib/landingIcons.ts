// Icon names a landing page feature may use (src/content/landing/<locale>.yaml); components/landing/icons.ts maps
// them to Tabler icons.
export const landingIcons = [
  'storage',
  'files',
  'apps',
  'users',
  'network',
  'updates',
  'desktop',
  'shield',
  'local',
  'code',
  'license',
  'hardware',
] as const;

export type LandingIcon = (typeof landingIcons)[number];
