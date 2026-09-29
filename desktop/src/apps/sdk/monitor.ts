import type { components } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import type { HostExtensions } from '@pithyx/sdk/host';
import { realtime } from '../../realtime/connection';

export type ResourceSnapshot = components['schemas']['ResourceSnapshot'];
export type ResourceSample = components['schemas']['ResourceSample'];
export type ResourceHistory = ResourceSnapshot['history'];
export type ProcessList = components['schemas']['ProcessList'];
export type ProcessInfo = ProcessList['processes'][number];
export type TemperatureReading = ResourceSample['temperatures'][number];

/** The snapshot of `system/resources` first, then one sample per second. */
export const MONITOR_RESOURCES = 'internal.admin.monitor.resources';
/** A process list per second while the app watches processes; the first one is the snapshot. */
export const MONITOR_PROCESSES = 'internal.admin.monitor.processes';

const topics = {
  resources: { topic: 'system/resources', event: MONITOR_RESOURCES },
  processes: { topic: 'system/processes', event: MONITOR_PROCESSES },
} as const;

type Watched = keyof typeof topics;

/** Which topics the app wants; the daemon samples each one only while someone watches it. */
export interface MonitorWatch {
  resources: boolean;
  processes: boolean;
}

function isWatch(value: unknown): value is MonitorWatch {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as Record<string, unknown>).resources === 'boolean' &&
    typeof (value as Record<string, unknown>).processes === 'boolean'
  );
}

/**
 * The host side of the Resource Monitor. Nothing is subscribed until the app says what it watches (after it
 * listens, so the snapshot is not lost); closing the window releases every topic, and the daemon stops sampling.
 */
export function monitorHost(emit: (event: string, data?: unknown) => void): {
  extensions: HostExtensions;
  close: () => void;
} {
  const releases = new Map<Watched, () => void>();
  const watch = (wanted: MonitorWatch) => {
    for (const name of Object.keys(topics) as Watched[]) {
      const release = releases.get(name);
      if (wanted[name] && release === undefined) {
        const { topic, event } = topics[name];
        releases.set(
          name,
          realtime.listen(topic, (data) => {
            emit(event, data);
          }),
        );
      } else if (!wanted[name] && release !== undefined) {
        release();
        releases.delete(name);
      }
    }
  };
  return {
    extensions: {
      'internal.admin.monitor.watch': {
        validate: isWatch,
        handle: (params) => {
          watch(params as MonitorWatch);
          return null;
        },
      },
    },
    close: () => {
      watch({ resources: false, processes: false });
    },
  };
}

/** Tells the host which topics to follow. */
export async function watchMonitor(app: PithyxApp, wanted: MonitorWatch): Promise<void> {
  // The SDK's call is typed to the public methods; the internal name only exists here.
  await app.call('internal.admin.monitor.watch' as unknown as MethodName, wanted as never);
}

export function onMonitor(
  app: PithyxApp,
  event: typeof MONITOR_RESOURCES | typeof MONITOR_PROCESSES,
  handler: (data: unknown) => void,
): () => void {
  return app.on(event as unknown as EventName, handler);
}
