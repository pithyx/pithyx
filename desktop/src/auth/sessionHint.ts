import type { components } from '@vyxis/api-client';

type User = components['schemas']['UserInfo'];

// The last user whose session the daemon confirmed in this browser. A reload shows the desktop with it right away and
// checks the session in the background (show cached data first, then reconcile); a rejected session removes it.
// It holds no secret: the session itself is an HttpOnly cookie the page cannot read.
const KEY = 'vyxis.session';

export function readSessionHint(): User | undefined {
  try {
    const value = localStorage.getItem(KEY);
    return value === null ? undefined : (JSON.parse(value) as User);
  } catch {
    return undefined;
  }
}

export function writeSessionHint(user: User): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(user));
  } catch {
    // Storage can be unavailable (private mode); reloads then wait for the session check.
  }
}

export function clearSessionHint(): void {
  try {
    localStorage.removeItem(KEY);
  } catch {
    // Nothing stored.
  }
}
