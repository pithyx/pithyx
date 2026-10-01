import { useSyncExternalStore } from 'react';

export const ACCENTS = ['petrol', 'ocean', 'indigo', 'plum', 'moss', 'graphite'] as const;
export const WALLPAPERS = ['boxes', 'plain'] as const;

/**
 * The accent color and wallpaper of the signed-in user: saved in the daemon and the same on every device (D281). The
 * theme (light, dark) is a choice of each device (theme/preference.ts).
 */
export interface Appearance {
  accent: (typeof ACCENTS)[number];
  wallpaper: (typeof WALLPAPERS)[number];
}

export const DEFAULT_APPEARANCE: Appearance = { accent: 'petrol', wallpaper: 'boxes' };

// The last appearance seen on this device, so the first paint after a reload already matches it.
const STORAGE_KEY = 'pithyx.appearance';

let current: Appearance = DEFAULT_APPEARANCE;
const listeners = new Set<() => void>();

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** Only known values survive; anything else falls back to the default of that part. */
export function checkedAppearance(value: unknown): Appearance {
  const candidate = (typeof value === 'object' && value !== null ? value : {}) as Record<
    string,
    unknown
  >;
  return {
    accent: ACCENTS.find((entry) => entry === candidate.accent) ?? DEFAULT_APPEARANCE.accent,
    wallpaper:
      WALLPAPERS.find((entry) => entry === candidate.wallpaper) ?? DEFAULT_APPEARANCE.wallpaper,
  };
}

export function currentAppearance(): Appearance {
  return current;
}

/** Sets the accent on the page, tells the wallpaper and open apps, and remembers it on this device. */
export function applyAppearance(appearance: Appearance): void {
  document.documentElement.dataset.accent = appearance.accent;
  if (appearance.accent === current.accent && appearance.wallpaper === current.wallpaper) {
    return;
  }
  current = appearance;
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(appearance));
  } catch {
    // Storage can be unavailable (private mode); the next desktop load applies the saved appearance again.
  }
  listeners.forEach((listener) => {
    listener();
  });
}

/** Applies the appearance last used on this device; runs before the first render. */
export function applyStoredAppearance(): void {
  let stored: unknown;
  try {
    stored = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null');
  } catch {
    stored = null;
  }
  applyAppearance(checkedAppearance(stored));
}

/** Calls back when the accent or the wallpaper changes. */
export function onAppearanceChange(callback: () => void): () => void {
  return subscribe(callback);
}

export function useAppearance(): Appearance {
  return useSyncExternalStore(subscribe, currentAppearance);
}
