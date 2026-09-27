import { useSyncExternalStore } from 'react';
import type { SizeClass } from './types';

// The same breakpoints as the media queries in the shell CSS (desktop/CLAUDE.md).
const compact = '(max-width: 599px)';
const medium = '(min-width: 600px) and (max-width: 1023px)';

function subscribe(listener: () => void): () => void {
  const queries = [matchMedia(compact), matchMedia(medium)];
  for (const query of queries) {
    query.addEventListener('change', listener);
  }
  return () => {
    for (const query of queries) {
      query.removeEventListener('change', listener);
    }
  };
}

function snapshot(): SizeClass {
  if (matchMedia(compact).matches) {
    return 'compact';
  }
  return matchMedia(medium).matches ? 'medium' : 'expanded';
}

/** Compact, medium or expanded, from the viewport width. Updates on change, never polls. */
export function useSizeClass(): SizeClass {
  return useSyncExternalStore(subscribe, snapshot);
}
