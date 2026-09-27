import type { Size } from './types';

// The last free size per app, from the daemon (shared across devices). New windows of the app open at this size.
const sizes = new Map<string, Size>();

export function preferredSize(app: string): Size | undefined {
  return sizes.get(app);
}

export function setPreferredSize(app: string, size: Size): void {
  sizes.set(app, size);
}
