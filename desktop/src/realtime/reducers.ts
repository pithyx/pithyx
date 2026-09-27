import type { TopicEvents, TopicName, TopicSnapshots } from '@vyxis/api-client';

type Reducer<K extends TopicName> = (
  snapshot: TopicSnapshots[K] | undefined,
  event: TopicEvents[K],
) => TopicSnapshots[K];

/** How each topic's events change its snapshot. A new topic in the daemon fails the build here until handled. */
export const reducers: { [K in TopicName]: Reducer<K> } = {
  'storage/disks': (snapshot, event) => {
    const removed = new Set(event.removals);
    const disks = new Map(
      (snapshot?.disks ?? [])
        .filter((disk) => !removed.has(disk.id))
        .map((disk) => [disk.id, disk]),
    );
    for (const disk of event.upserts) {
      disks.set(disk.id, disk);
    }
    return {
      disks: [...disks.values()].sort((a, b) =>
        a.name.localeCompare(b.name, 'en', { numeric: true }),
      ),
    };
  },
  // Folder changes are deltas for the file manager's own listing; the snapshot only says whether the folder is watched.
  'files/+/+': (snapshot) => snapshot ?? { watching: true },
  'jobs/+': (_, event) => event,
  'system/info': (_, event) => event,
};

export function applyEvent<K extends TopicName>(
  topic: K,
  snapshot: TopicSnapshots[K] | undefined,
  event: TopicEvents[K],
): TopicSnapshots[K] {
  return (reducers[topic] as Reducer<K>)(snapshot, event);
}

/** Exponential backoff with jitter: 0.5 s, 1 s, 2 s ... up to 30 s, each randomized to 50 to 100 percent. */
export function reconnectDelay(attempt: number, random: number = Math.random()): number {
  return Math.min(30_000, 500 * 2 ** attempt) * (0.5 + random / 2);
}
