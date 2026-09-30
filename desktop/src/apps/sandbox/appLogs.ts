import type { LogRecord } from '@pithyx/sdk';
import { api } from '../../api/client';

// What apps log through the SDK, collected per app and sent to the daemon every few seconds, at most 100 entries per
// request. The daemon applies each app's rate limit; this buffer only keeps a runaway app from growing the tab's memory.
const FLUSH_DELAY = 2000;
const BATCH = 100;
const MAX_QUEUED = 1000;

const queues = new Map<string, LogRecord[]>();
let timer: ReturnType<typeof setTimeout> | undefined;

async function flush() {
  timer = undefined;
  for (const [app, queue] of queues) {
    const entries = queue.splice(0, BATCH);
    if (queue.length === 0) {
      queues.delete(app);
    }
    if (entries.length > 0) {
      // Logging must never disturb the app; a lost batch is not worth an error.
      await api
        .POST('/api/apps/{app}/logs', { params: { path: { app } }, body: { entries } })
        .catch(() => undefined);
    }
  }
  if (queues.size > 0) {
    timer = setTimeout(() => void flush(), FLUSH_DELAY);
  }
}

/** Queues entries of an app; the app's identity comes from its connection, never from the entries. */
export function writeAppLog(app: string, entries: readonly LogRecord[]) {
  const queue = queues.get(app) ?? [];
  queue.push(...entries.slice(0, Math.max(0, MAX_QUEUED - queue.length)));
  queues.set(app, queue);
  timer ??= setTimeout(() => void flush(), FLUSH_DELAY);
}
