import { useSyncExternalStore } from 'react';

// One timer for every clock on the page. It fires at the start of each minute, and only while someone listens.
const listeners = new Set<() => void>();
let timer: ReturnType<typeof setTimeout> | undefined;
let minute = -1;
let current = new Date();

function snapshot(): Date {
  const now = Math.floor(Date.now() / 60_000);
  if (now !== minute) {
    minute = now;
    current = new Date(now * 60_000);
  }
  return current;
}

function schedule() {
  timer = setTimeout(
    () => {
      for (const listener of listeners) {
        listener();
      }
      schedule();
    },
    60_000 - (Date.now() % 60_000),
  );
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  if (listeners.size === 1) {
    schedule();
  }
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0) {
      clearTimeout(timer);
    }
  };
}

/** The current time, updated once a minute. */
export function useMinuteClock(): Date {
  return useSyncExternalStore(subscribe, snapshot);
}
