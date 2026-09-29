import { createApp, inProcessPair, type AppContext } from '@pithyx/sdk';
import { serveApp } from '@pithyx/sdk/host';
import { describe, expect, it } from 'vitest';
import type { InternalMethods } from './internal';

const context: Omit<AppContext, 'capabilities'> = {
  app: { id: 'demo', version: '0.0.0' },
  instance: 'w1',
  user: { id: 1, name: 'ada', displayName: null },
  locale: 'en',
  timeZone: 'UTC',
  theme: {
    mode: 'light',
    accent: 'petrol',
    density: 'comfortable',
    tokens: {},
  } as unknown as AppContext['theme'],
  sizeClass: 'expanded',
  launch: { args: null },
  session: null,
  permissions: [],
  host: { version: '0.0.0', protocol: 1 },
};

const sources = import.meta.glob<string>('../sandbox/*.tsx', {
  query: '?raw',
  import: 'default',
  eager: true,
});

describe('the internal administrator calls', () => {
  it('are unknown to an app served like a sandboxed app', async () => {
    const [appEnd, hostEnd] = inProcessPair();
    serveApp(hostEnd, { context, methods: { notify: () => undefined } });
    const app = await createApp(appEnd, { themeRoot: null, timeout: 500 });
    const names: (keyof InternalMethods)[] = [
      'internal.admin.storage.disks',
      'internal.admin.storage.pools',
      'internal.admin.storage.plan',
      'internal.admin.storage.run',
      'internal.admin.storage.importScan',
      'internal.admin.storage.snapshots.list',
      'internal.admin.storage.snapshots.create',
      'internal.admin.storage.snapshots.hold',
      'internal.admin.storage.snapshots.release',
      'internal.admin.storage.snapshots.setSchedules',
      'internal.admin.storage.health',
      'internal.admin.storage.mute',
      'internal.admin.storage.scrub',
      'internal.admin.storage.schedules',
      'internal.admin.storage.schedule',
    ];
    for (const name of names) {
      await expect(app.call(name as 'notify', {} as never)).rejects.toMatchObject({
        code: 'sdk.method.unknown',
      });
    }
  });

  it('are never handed to the sandboxed app window', () => {
    const source = Object.values(sources).join('\n');
    expect(source).toContain('serveApp(');
    expect(source).not.toMatch(/extensions|internal|adminStorageHost/);
  });
});
