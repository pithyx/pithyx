import type { TopicEvents, TopicName, TopicSnapshots } from '@pithyx/api-client';

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
  // Pools are few; every event is the whole state.
  'storage/pools': (_, event) => event,
  'system/info': (_, event) => event,
};

/** The pattern a concrete topic belongs to: `files/3/0.2a.1f` belongs to `files/+/+`. */
export function patternOf(topic: string): TopicName | undefined {
  if (Object.hasOwn(reducers, topic)) {
    return topic as TopicName;
  }
  const segments = topic.split('/');
  return (Object.keys(reducers) as TopicName[]).find((pattern) => {
    const parts = pattern.split('/');
    return (
      parts.length === segments.length &&
      parts.every((part, i) => part === '+' || part === segments[i])
    );
  });
}

export function applyEvent<K extends TopicName>(
  topic: K,
  snapshot: TopicSnapshots[K] | undefined,
  event: TopicEvents[K],
): TopicSnapshots[K] {
  const pattern = patternOf(topic);
  return pattern === undefined
    ? (event as unknown as TopicSnapshots[K])
    : (reducers[pattern] as unknown as Reducer<K>)(snapshot, event);
}

/** Exponential backoff with jitter: 0.5 s, 1 s, 2 s ... up to 30 s, each randomized to 50 to 100 percent. */
export function reconnectDelay(attempt: number, random: number = Math.random()): number {
  return Math.min(30_000, 500 * 2 ** attempt) * (0.5 + random / 2);
}
