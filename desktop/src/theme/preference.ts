export type ThemeChoice = 'system' | 'light' | 'dark';

export const themeChoices: readonly ThemeChoice[] = ['system', 'light', 'dark'];

const STORAGE_KEY = 'pithyx.theme';
const CHANGE_EVENT = 'pithyx:themechange';

/** The theme picked on this device; `system` follows the browser's color scheme. */
export function storedTheme(): ThemeChoice {
  try {
    const value = localStorage.getItem(STORAGE_KEY);
    return value === 'light' || value === 'dark' ? value : 'system';
  } catch {
    return 'system';
  }
}

function apply(choice: ThemeChoice) {
  if (choice === 'system') {
    delete document.documentElement.dataset.theme;
  } else {
    document.documentElement.dataset.theme = choice;
  }
}

/** Applies the stored theme; runs before the first render so nothing flashes in the other scheme. */
export function applyStoredTheme(): void {
  apply(storedTheme());
}

export function setTheme(choice: ThemeChoice): void {
  try {
    if (choice === 'system') {
      localStorage.removeItem(STORAGE_KEY);
    } else {
      localStorage.setItem(STORAGE_KEY, choice);
    }
  } catch {
    // Storage can be unavailable (private mode); the choice then lasts for this page only.
  }
  apply(choice);
  window.dispatchEvent(new Event(CHANGE_EVENT));
}

/** Calls back when the user picks another theme on this page. */
export function onThemeChoice(callback: () => void): () => void {
  window.addEventListener(CHANGE_EVENT, callback);
  return () => {
    window.removeEventListener(CHANGE_EVENT, callback);
  };
}
