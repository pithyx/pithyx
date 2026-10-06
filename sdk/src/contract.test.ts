import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createApp } from './app';
import { backendUrl, endpointPath, socketUrl } from './backend';
import { PithyxError } from './errors';
import { serveApp, type MethodHandlers } from './host';
import type { FileEntry, FileVersion, JobInfo } from './files';
import { BEFORE_UNLOAD_TIMEOUT, type AppContext } from './protocol';
import { themeTokenNames, type Theme } from './theme';
import { inProcessPair, portEndpoint, type Endpoint } from './transport';
import { PROTOCOL_VERSION } from './version';

function theme(scheme: 'light' | 'dark'): Theme {
  return {
    scheme,
    density: 'comfortable',
    tokens: Object.fromEntries(
      themeTokenNames.map((name) => [name, scheme === 'dark' ? '#000' : '#fff']),
    ) as Theme['tokens'],
  };
}

const context: Omit<AppContext, 'capabilities'> = {
  app: { id: 'demo', version: '1.0.0' },
  instance: 'w1',
  user: {
    id: 1,
    name: 'ada',
    displayName: 'Ada Admin',
    groups: [{ id: 30000, name: 'family' }],
    isAdmin: true,
    twoFactor: false,
  },
  language: 'en',
  locale: 'en-US',
  timeZone: 'Europe/Berlin',
  theme: theme('light'),
  sizeClass: 'expanded',
  launch: { args: null },
  session: { folder: '/tank' },
  permissions: ['notifications'],
  box: { name: 'nas', version: '0.1.0' },
  visible: true,
  online: true,
  logLevel: 'info',
  backend: null,
  storage: [],
  host: { version: '0.1.0', protocol: PROTOCOL_VERSION },
};

/** The two transports every contract below runs over. */
const transports: [string, () => [Endpoint, Endpoint]][] = [
  ['in-process', () => inProcessPair()],
  [
    'MessageChannel',
    () => {
      const channel = new MessageChannel();
      return [portEndpoint(channel.port1), portEndpoint(channel.port2)];
    },
  ],
];

const ORIGIN = 'https://nas.example:7484';

/** The console the SDK mirrors log entries to, silenced and recorded. */
function spyConsole() {
  return {
    debug: vi.spyOn(console, 'debug').mockImplementation(() => undefined),
    info: vi.spyOn(console, 'info').mockImplementation(() => undefined),
    warn: vi.spyOn(console, 'warn').mockImplementation(() => undefined),
    error: vi.spyOn(console, 'error').mockImplementation(() => undefined),
  };
}

let consoleSpy: ReturnType<typeof spyConsole>;
beforeEach(() => {
  consoleSpy = spyConsole();
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe.each(transports)('over the %s transport', (_, pair) => {
  function connectTo(methods: MethodHandlers, overrides: Partial<typeof context> = {}) {
    const [appEnd, hostEnd] = pair();
    const host = serveApp(hostEnd, { context: { ...context, ...overrides }, methods });
    return { host, app: createApp(appEnd, { themeRoot: null, timeout: 500, origin: ORIGIN }) };
  }

  it('batches log calls into log.write, cut to the limits, and never throws', async () => {
    const received: unknown[][] = [];
    const { app } = connectTo(
      {
        'log.write': ({ entries }) => {
          received.push(entries);
        },
      },
      { logLevel: 'debug' },
    );
    const connected = await app;
    connected.log.info('started', { attempt: 2, ok: true });
    connected.log.error('x'.repeat(5000));
    connected.log.debug(
      'with many fields',
      Object.fromEntries(Array.from({ length: 30 }, (_, i) => [`k${String(i)}`, i])),
    );
    await vi.waitFor(() => {
      expect(received).toHaveLength(1);
    });
    const [entries] = received as [
      { level: string; message: string; data?: Record<string, string> }[],
    ];
    expect(entries.map((entry) => entry.level)).toEqual(['info', 'error', 'debug']);
    expect(entries[0]?.data).toEqual({ attempt: '2', ok: 'true' });
    expect(entries[1]?.message).toHaveLength(4000);
    expect(Object.keys(entries[2]?.data ?? {})).toHaveLength(20);
  });

  it('drops log entries silently when the desktop does not serve log.write', async () => {
    const { app } = connectTo({ notify: () => ({ id: '1' }) });
    const connected = await app;
    expect(() => {
      connected.log.warn('nobody listens');
    }).not.toThrow();
  });

  it('hands the app its context with the capabilities the desktop serves', async () => {
    const { app } = connectTo({ 'window.setTitle': () => undefined, notify: () => ({ id: '1' }) });
    const connected = await app;
    expect(connected.context.user.name).toBe('ada');
    expect(connected.context.capabilities).toEqual(['window.setTitle', 'notify']);
    expect(connected.supports('notify')).toBe(true);
    expect(connected.supports('storage.get')).toBe(false);
    await expect(connected.session.restore()).resolves.toEqual({ folder: '/tank' });
  });

  it('calls a method with its parameters and returns the result', async () => {
    const setTitle = vi.fn();
    const stored = new Map<string, unknown>();
    const { app } = connectTo({
      'window.setTitle': setTitle,
      'window.setSize': ({ width, height }) => ({ width: Math.max(width, 320), height }),
      'storage.set': ({ key, value }) => {
        stored.set(key, value);
      },
      'storage.get': ({ key }) => stored.get(key) ?? null,
    });
    const connected = await app;
    await connected.window.setTitle('Notes');
    expect(setTitle).toHaveBeenCalledWith({ title: 'Notes' });
    expect(await connected.window.setSize({ width: 100, height: 400 })).toEqual({
      width: 320,
      height: 400,
    });
    await connected.storage.set('draft', { text: 'hello', at: [1, 2] });
    expect(await connected.storage.get('draft')).toEqual({ text: 'hello', at: [1, 2] });
    expect(await connected.storage.get('missing')).toBeNull();
  });

  it('passes daemon problems through unchanged', async () => {
    const { app } = connectTo({
      'storage.set': () => {
        throw new PithyxError({
          code: 'desktop.item.conflict',
          params: { id: 7, version: 3 },
          detail: 'Changed elsewhere',
          requestId: 'abc123',
        });
      },
    });
    const connected = await app;
    const error = await connected.storage.set('k', 1).catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(PithyxError);
    expect(error).toMatchObject({
      code: 'desktop.item.conflict',
      params: { id: 7, version: 3 },
      requestId: 'abc123',
    });
  });

  it('refuses unknown methods and invalid parameters before any handler runs', async () => {
    const setTitle = vi.fn();
    const { app } = connectTo({ 'window.setTitle': setTitle });
    const connected = await app;
    await expect(connected.notify({ title: 'x' })).rejects.toMatchObject({
      code: 'sdk.method.unknown',
    });
    await expect(
      connected.call('window.setTitle', { title: 'x'.repeat(201) }),
    ).rejects.toMatchObject({
      code: 'sdk.request.invalid',
    });
    await expect(connected.call('nonsense' as 'window.close', {})).rejects.toMatchObject({
      code: 'sdk.method.unknown',
    });
    expect(setTitle).not.toHaveBeenCalled();
  });

  it('names a whole folder without its names and refuses malformed file operations', async () => {
    const job: JobInfo = {
      id: 1,
      kind: 'files.copy',
      state: 'queued',
      progress: { itemsDone: 0, itemsTotal: 0, bytesDone: 0, bytesTotal: 0, current: null },
      problem: null,
      result: null,
    };
    const run = vi.fn(() => job);
    const { app } = connectTo({ 'files.run': run });
    const connected = await app;
    const source = { location: { share: '1' }, folder: 'Photos' };
    const target = { location: { share: '2' }, path: '' };

    await connected.files.run({
      kind: 'copy',
      source: { ...source, items: { all: true, except: ['a.jpg'] } },
      target,
    });
    expect(run).toHaveBeenCalledWith(
      expect.objectContaining({ source: { ...source, items: { all: true, except: ['a.jpg'] } } }),
    );

    const invalid = [
      { kind: 'copy', source: { ...source, items: { names: [] } }, target },
      { kind: 'copy', source: { ...source, items: { names: ['a/b'] } }, target },
      {
        kind: 'copy',
        source: {
          ...source,
          items: { names: Array.from({ length: 1001 }, (_, i) => `f${String(i)}`) },
        },
        target,
      },
      { kind: 'copy', source: { ...source, items: { names: ['a'] } } },
      { kind: 'delete', source: { ...source, items: { names: ['a'] } }, target },
      { kind: 'delete', source: { ...source, items: { names: ['a'] } }, permanent: 'yes' },
      { kind: 'move', source: { ...source, folder: '../x', items: { names: ['a'] } }, target },
    ];
    for (const operation of invalid) {
      await expect(
        connected.call('files.run', operation as Parameters<typeof connected.files.run>[0]),
      ).rejects.toMatchObject({ code: 'sdk.request.invalid' });
    }
    expect(run).toHaveBeenCalledTimes(1);
  });

  it('lists and restores previous versions and refuses malformed requests', async () => {
    const version: FileVersion = {
      snapshot: 'pithyx-daily-20260927T000000Z',
      createdAt: '2026-09-27T00:00:00Z',
      kind: 'file',
      size: 3,
      modified: '2026-09-26T12:00:00Z',
    };
    const job: JobInfo = {
      id: 7,
      kind: 'files.restore_version',
      state: 'queued',
      progress: { itemsDone: 0, itemsTotal: 0, bytesDone: 0, bytesTotal: 0, current: null },
      problem: null,
      result: null,
    };
    const versions = vi.fn(() => [version]);
    const restore = vi.fn(() => job);
    const { app } = connectTo({ 'files.versions': versions, 'files.restoreVersion': restore });
    const connected = await app;
    const location = { share: '1' };

    expect(await connected.files.versions(location, 'Docs/plan.txt')).toEqual([version]);
    expect(versions).toHaveBeenCalledWith({ location, path: 'Docs/plan.txt' });
    expect(
      await connected.files.restoreVersion(location, 'Docs/plan.txt', version.snapshot),
    ).toEqual(job);
    await connected.files.restoreVersion(location, 'Docs/plan.txt', version.snapshot, {
      overwrite: true,
    });
    expect(restore).toHaveBeenLastCalledWith({
      location,
      path: 'Docs/plan.txt',
      snapshot: version.snapshot,
      overwrite: true,
    });

    await expect(connected.call('files.versions', { location, path: '' })).rejects.toMatchObject({
      code: 'sdk.request.invalid',
    });
    await expect(
      connected.call('files.versions', { location, path: '../secret' }),
    ).rejects.toMatchObject({ code: 'sdk.request.invalid' });
    const invalid = [
      { location, path: 'a', snapshot: 'x/y' },
      { location, path: 'a', snapshot: '' },
      { location, path: 'a', snapshot: 's', overwrite: 'yes' },
      { location: { share: 1 }, path: 'a', snapshot: 's' },
    ];
    for (const params of invalid) {
      await expect(
        connected.call(
          'files.restoreVersion',
          params as Parameters<typeof connected.call<'files.restoreVersion'>>[1],
        ),
      ).rejects.toMatchObject({ code: 'sdk.request.invalid' });
    }
    expect(restore).toHaveBeenCalledTimes(2);
  });

  it('lets an upload take as long as the transfer', async () => {
    const entry: FileEntry = {
      name: 'big.bin',
      kind: 'file',
      size: 3,
      modified: '2026-09-27T12:00:00Z',
    };
    const { app } = connectTo({
      'files.upload': () =>
        new Promise((resolve) => {
          setTimeout(() => {
            resolve(entry);
          }, 700);
        }),
    });
    const connected = await app;
    await expect(
      connected.files.upload({ share: '1' }, '', 'big.bin', new Blob(['abc'])),
    ).resolves.toEqual(entry);
  });

  it('turns handler failures that are not problems into an unexpected error', async () => {
    const { app } = connectTo({
      'window.close': () => {
        throw new Error('secret internals');
      },
    });
    const connected = await app;
    const error = (await connected.window
      .close()
      .catch((caught: unknown) => caught)) as PithyxError;
    expect(error.code).toBe('system.internal.unexpected');
    expect(error.message).not.toContain('secret');
  });

  it('delivers lifecycle events and keeps the context current', async () => {
    const { host, app } = connectTo({});
    const connected = await app;
    const themes: string[] = [];
    const stop = connected.on('themeChange', (next) => themes.push(next.scheme));
    const focus = vi.fn();
    connected.on('focus', focus);
    host.emit('themeChange', theme('dark'));
    host.emit('focus', {});
    host.emit('localeChange', { language: 'de', locale: 'de-DE', timeZone: 'UTC' });
    await vi.waitFor(() => {
      expect(connected.context.locale).toBe('de-DE');
    });
    expect(connected.context.language).toBe('de');
    expect(themes).toEqual(['dark']);
    expect(focus).toHaveBeenCalledOnce();
    expect(connected.context.theme.scheme).toBe('dark');
    stop();
    host.emit('themeChange', theme('light'));
    host.emit('permissionsChange', { permissions: [] });
    await vi.waitFor(() => {
      expect(connected.context.theme.scheme).toBe('light');
    });
    expect(connected.context.permissions).not.toContain('notifications');
    expect(connected.format.date('2026-09-27T23:30:00Z')).toBe('27.09.2026');
    expect(themes).toEqual(['dark']);
  });

  it('sends sharesChanged only to apps that hold files.shares', async () => {
    const [appEnd, hostEnd] = pair();
    const host = serveApp(hostEnd, {
      context: { ...context, permissions: ['files.shares'] },
      methods: {},
    });
    const connected = await createApp(appEnd, { themeRoot: null, timeout: 500 });
    const changed = vi.fn();
    connected.on('sharesChanged', changed);
    host.emit('sharesChanged', {});
    await vi.waitFor(() => {
      expect(changed).toHaveBeenCalledOnce();
    });
    host.emit('permissionsChange', { permissions: [] });
    await vi.waitFor(() => {
      expect(connected.context.permissions).not.toContain('files.shares');
    });
    const focus = vi.fn();
    connected.on('focus', focus);
    host.emit('sharesChanged', {});
    // Events arrive in order, so once focus is there a sharesChanged sent before it would be too.
    host.emit('focus', {});
    await vi.waitFor(() => {
      expect(focus).toHaveBeenCalledOnce();
    });
    expect(changed).toHaveBeenCalledOnce();

    const { host: other, app } = connectTo({});
    const without = await app;
    const never = vi.fn();
    without.on('sharesChanged', never);
    const focused = vi.fn();
    without.on('focus', focused);
    other.emit('sharesChanged', {});
    other.emit('focus', {});
    await vi.waitFor(() => {
      expect(focused).toHaveBeenCalledOnce();
    });
    expect(never).not.toHaveBeenCalled();
  });

  it('lets the app keep a window open and gives up on apps that do not answer', async () => {
    const { host, app } = connectTo({});
    const connected = await app;
    expect(await host.ask('beforeClose', {}, 500)).toEqual({ allow: true });
    connected.answer('beforeClose', () => ({ allow: false }));
    expect(await host.ask('beforeClose', {}, 500)).toEqual({ allow: false });
    connected.answer('beforeUnload', () => new Promise(() => undefined));
    expect(await host.ask('beforeUnload', { reason: 'logout' }, 50)).toBeUndefined();
  });

  it('mirrors every log level to the console and sends debug only while it is switched on', async () => {
    const received: string[] = [];
    const { host, app } = connectTo({
      'log.write': ({ entries }) => {
        received.push(...entries.map((entry) => `${entry.level}:${entry.message}`));
      },
    });
    const connected = await app;
    connected.log.debug('details', { step: 1 });
    connected.log.info('started');
    connected.log.log('also info');
    connected.log.warn('slow');
    connected.log.error('failed');
    expect(consoleSpy.debug).toHaveBeenCalledWith('details', { step: 1 });
    expect(consoleSpy.info).toHaveBeenCalledWith('started');
    expect(consoleSpy.info).toHaveBeenCalledWith('also info');
    expect(consoleSpy.warn).toHaveBeenCalledWith('slow');
    expect(consoleSpy.error).toHaveBeenCalledWith('failed');
    await vi.waitFor(() => {
      expect(received).toEqual(['info:started', 'info:also info', 'warn:slow', 'error:failed']);
    });

    host.emit('logLevelChange', { level: 'debug' });
    await vi.waitFor(() => {
      expect(connected.context.logLevel).toBe('debug');
    });
    connected.log.debug('now sent');
    await vi.waitFor(() => {
      expect(received).toContain('debug:now sent');
    });
  });

  it('keeps the context current with every event that changes it', async () => {
    const { host, app } = connectTo(
      {},
      {
        backend: { status: 'stopped', endpoints: ['api'] },
        storage: [{ id: 'library', state: 'unmapped', access: null }],
      },
    );
    const connected = await app;
    const actions = vi.fn();
    connected.on('notificationAction', actions);
    host.emit('visibilityChange', { visible: false });
    host.emit('connectionChange', { online: false });
    host.emit('backendStatus', { status: 'running' });
    host.emit('storageChange', {
      storage: [{ id: 'library', state: 'ready', access: 'read' }],
    });
    host.emit('permissionsChange', { permissions: ['notifications', 'directory.read'] });
    host.emit('notificationAction', { notification: '7', action: 'reply', data: { chat: 3 } });
    await vi.waitFor(() => {
      expect(actions).toHaveBeenCalledWith({
        notification: '7',
        action: 'reply',
        data: { chat: 3 },
      });
    });
    expect(connected.context).toMatchObject({
      visible: false,
      online: false,
      backend: { status: 'running', endpoints: ['api'] },
      storage: [{ id: 'library', state: 'ready', access: 'read' }],
      permissions: ['notifications', 'directory.read'],
    });

    const { host: other, app: withoutBackend } = connectTo({});
    const plain = await withoutBackend;
    const status = vi.fn();
    plain.on('backendStatus', status);
    other.emit('backendStatus', { status: 'running' });
    await vi.waitFor(() => {
      expect(status).toHaveBeenCalledOnce();
    });
    expect(plain.context.backend).toBeNull();
  });

  it('lists users and groups only while the app holds directory.read', async () => {
    const users = vi.fn(() => [{ id: 1, name: 'ada', displayName: null, groups: [30000] }]);
    const groups = vi.fn(() => [{ id: 30000, name: 'family', description: '' }]);
    const methods = { 'directory.users': users, 'directory.groups': groups };

    const { app } = connectTo(methods);
    const without = await app;
    await expect(without.directory.users()).rejects.toMatchObject({
      code: 'app.permission.denied',
      params: { permission: 'directory.read' },
    });
    // The host refuses too, for an app that skips the SDK's own check.
    await expect(without.call('directory.groups', {})).rejects.toMatchObject({
      code: 'app.permission.denied',
    });
    expect(users).not.toHaveBeenCalled();
    expect(groups).not.toHaveBeenCalled();

    const { host, app: granted } = connectTo(methods, { permissions: ['directory.read'] });
    const allowed = await granted;
    expect(await allowed.directory.users()).toEqual([
      { id: 1, name: 'ada', displayName: null, groups: [30000] },
    ]);
    expect(await allowed.directory.groups()).toHaveLength(1);
    host.emit('permissionsChange', { permissions: [] });
    await vi.waitFor(() => {
      expect(allowed.context.permissions).toEqual([]);
    });
    await expect(allowed.call('directory.users', {})).rejects.toMatchObject({
      code: 'app.permission.denied',
    });
    expect(users).toHaveBeenCalledOnce();
  });

  it('checks the parameters of the window, notification, menu, clipboard and link calls', async () => {
    const handlers = {
      notify: vi.fn(() => ({ id: '42' })),
      setBadge: vi.fn(),
      'window.setDirty': vi.fn(),
      'window.requestAttention': vi.fn(),
      'contextMenu.show': vi.fn(() => 'copy'),
      'clipboard.writeText': vi.fn(),
      openUrl: vi.fn(),
    };
    const { app } = connectTo(handlers);
    const connected = await app;

    expect(
      await connected.notify({
        title: 'New message',
        actions: [
          { id: 'reply', label: 'Reply' },
          { id: 'mute', label: 'Mute' },
        ],
        data: { chat: 3 },
        tag: 'chat-3',
      }),
    ).toEqual({ id: '42' });
    await connected.setBadge(3);
    await connected.setBadge(null);
    await connected.window.setDirty(true);
    await connected.window.requestAttention();
    expect(
      await connected.contextMenu.show({ x: 10, y: 20 }, [
        { id: 'copy', label: 'Copy', shortcut: 'Ctrl+C' },
        { separator: true },
        { id: 'delete', label: 'Delete', danger: true },
      ]),
    ).toBe('copy');
    await connected.clipboard.writeText('hello');
    await connected.openUrl('https://example.org/docs');
    await connected.openUrl('mailto:ada@example.org');

    const action = (n: number) => ({ id: `a${String(n)}`, label: 'Go' });
    const invalid: [string, unknown][] = [
      ['notify', { title: '' }],
      ['notify', { title: 'x', actions: [action(1), action(2), action(3)] }],
      ['notify', { title: 'x', actions: [{ id: '', label: 'Go' }] }],
      ['notify', { title: 'x', tag: 'x'.repeat(65) }],
      ['notify', { title: 'x', data: 'x'.repeat(5000) }],
      ['setBadge', { count: -1 }],
      ['setBadge', { count: 1.5 }],
      ['window.setDirty', { dirty: 'yes' }],
      ['contextMenu.show', { at: { x: 0, y: 0 }, items: [] }],
      ['contextMenu.show', { at: { x: 0 }, items: [{ id: 'a', label: 'A' }] }],
      ['contextMenu.show', { at: { x: 0, y: 0 }, items: [{ separator: true, id: 'x' }] }],
      ['clipboard.writeText', { text: 7 }],
      ['openUrl', { url: 'javascript:alert(1)' }],
      ['openUrl', { url: 'data:text/html,hi' }],
      ['openUrl', { url: '/relative' }],
    ];
    for (const [method, params] of invalid) {
      await expect(
        connected.call(method as 'notify', params as never),
        method,
      ).rejects.toMatchObject({ code: 'sdk.request.invalid' });
    }
    expect(handlers.notify).toHaveBeenCalledOnce();
    expect(handlers.setBadge).toHaveBeenCalledTimes(2);
    expect(handlers.openUrl).toHaveBeenCalledTimes(2);
  });

  it('asks beforeUnload with its reason, ignores a refusal and gives up after two seconds', async () => {
    const { host, app } = connectTo({});
    const connected = await app;
    const reasons: string[] = [];
    connected.answer('beforeUnload', ({ reason }) => {
      reasons.push(reason);
      return { allow: false } as never;
    });
    expect(await host.ask('beforeUnload', { reason: 'restart' }, BEFORE_UNLOAD_TIMEOUT)).toEqual(
      {},
    );
    expect(reasons).toEqual(['restart']);

    connected.answer('beforeUnload', () => new Promise(() => undefined));
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    let settled = false;
    const asked = host.ask('beforeUnload', { reason: 'logout' }, BEFORE_UNLOAD_TIMEOUT);
    void asked.then(() => {
      settled = true;
    });
    await vi.advanceTimersByTimeAsync(BEFORE_UNLOAD_TIMEOUT - 1);
    expect(settled).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(await asked).toBeUndefined();
    expect(BEFORE_UNLOAD_TIMEOUT).toBe(2000);
  });

  it('reaches the backend endpoints with a token, a ticket and without cookies', async () => {
    const expiresAt = new Date(Date.now() + 5 * 60_000).toISOString();
    const token = vi.fn(() => ({ token: 't1', expiresAt }));
    const ticket = vi.fn(({ endpoint }: { endpoint: string }) => ({
      ticket: `k-${endpoint}`,
      expiresAt,
    }));
    const fetchMock = vi.fn<(url: URL, init: RequestInit) => Promise<Response>>(() =>
      Promise.resolve(new Response('ok')),
    );
    vi.stubGlobal('fetch', fetchMock);
    const sockets: { url: string; protocols: unknown }[] = [];
    vi.stubGlobal(
      'WebSocket',
      class {
        readonly url: string;
        constructor(url: URL, protocols?: string | string[]) {
          this.url = url.href;
          sockets.push({ url: url.href, protocols });
        }
      },
    );
    const { app } = connectTo(
      { 'backend.token': token, 'backend.ticket': ticket },
      {
        app: { id: 'com.mueller.chat', version: '1.2.0' },
        backend: { status: 'running', endpoints: ['api', 'metrics'] },
      },
    );
    const connected = await app;

    expect(await connected.backend.url('/avatar/1')).toBe(
      `${ORIGIN}/apps/com.mueller.chat/api/avatar/1?pithyx-token=t1`,
    );
    expect(await connected.backend.url('stats?day=1', { endpoint: 'metrics' })).toBe(
      `${ORIGIN}/apps/com.mueller.chat/api-metrics/stats?day=1&pithyx-token=t1`,
    );

    const response = await connected.backend.fetch('/messages', {
      method: 'POST',
      body: 'hi',
      headers: { 'Content-Type': 'text/plain' },
    });
    expect(await response.text()).toBe('ok');
    const [url, init] = fetchMock.mock.calls[0] ?? [];
    expect(url?.href).toBe(`${ORIGIN}/apps/com.mueller.chat/api/messages`);
    expect(init).toMatchObject({ method: 'POST', body: 'hi', credentials: 'omit' });
    const headers = new Headers(init?.headers);
    expect(headers.get('Authorization')).toBe('Bearer t1');
    expect(headers.get('Content-Type')).toBe('text/plain');
    expect(url?.searchParams.has('pithyx-token')).toBe(false);

    await connected.backend.socket('/live', { protocols: 'chat' });
    await connected.backend.socket('/feed', { endpoint: 'metrics' });
    expect(sockets).toEqual([
      {
        url: 'wss://nas.example:7484/apps/com.mueller.chat/api/live?pithyx-ticket=k-api',
        protocols: 'chat',
      },
      {
        url: 'wss://nas.example:7484/apps/com.mueller.chat/api-metrics/feed?pithyx-ticket=k-metrics',
        protocols: undefined,
      },
    ]);
    // One token serves every call while it is fresh; every socket gets its own ticket.
    expect(token).toHaveBeenCalledOnce();
    expect(ticket).toHaveBeenCalledTimes(2);
    expect(await connected.backend.token()).toEqual({ token: 't1', expiresAt });

    await expect(connected.backend.fetch('/x', { endpoint: 'admin' })).rejects.toMatchObject({
      code: 'sdk.backend.endpoint_unknown',
      params: { endpoint: 'admin' },
    });
    await expect(connected.backend.url('/../../org.pithyx.files/api/x')).rejects.toMatchObject({
      code: 'sdk.request.invalid',
    });
    await expect(connected.backend.url('/%2e%2e/%2e%2e/other/api/x')).rejects.toMatchObject({
      code: 'sdk.request.invalid',
    });

    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    await expect(connected.backend.fetch('/messages')).rejects.toMatchObject({
      code: 'sdk.backend.unreachable',
      params: { endpoint: 'api' },
    });
    const aborted = new DOMException('Aborted', 'AbortError');
    fetchMock.mockRejectedValueOnce(aborted);
    await expect(connected.backend.fetch('/messages')).rejects.toBe(aborted);
  });

  it('fails backend calls of an app without a backend before asking the desktop', async () => {
    const token = vi.fn(() => ({ token: 't', expiresAt: new Date().toISOString() }));
    const { app } = connectTo({ 'backend.token': token });
    const connected = await app;
    for (const attempt of [
      () => connected.backend.url('/x'),
      () => connected.backend.fetch('/x'),
      () => connected.backend.socket('/x'),
    ]) {
      await expect(attempt()).rejects.toMatchObject({
        code: 'sdk.backend.endpoint_unknown',
        params: { endpoint: 'api' },
      });
    }
    expect(token).not.toHaveBeenCalled();
  });

  it('renews the backend token before it expires and after permissionsChange', async () => {
    let issued = 0;
    const token = vi.fn(() => {
      issued += 1;
      // The first token is almost expired, the later ones fresh.
      const lifetime = issued === 1 ? 10_000 : 5 * 60_000;
      return {
        token: `t${String(issued)}`,
        expiresAt: new Date(Date.now() + lifetime).toISOString(),
      };
    });
    const { host, app } = connectTo(
      { 'backend.token': token },
      { backend: { status: 'running', endpoints: ['api'] }, permissions: [] },
    );
    const connected = await app;
    // A token with less than 30 seconds left is never handed out; the next one is kept while fresh.
    expect((await connected.backend.token()).token).toBe('t2');
    expect((await connected.backend.token()).token).toBe('t2');
    expect(token).toHaveBeenCalledTimes(2);
    host.emit('permissionsChange', { permissions: ['notifications'] });
    await vi.waitFor(() => {
      expect(connected.context.permissions).toEqual(['notifications']);
    });
    expect((await connected.backend.token()).token).toBe('t3');
  });

  it('fails a call that the desktop never answers', async () => {
    const [appEnd, hostEnd] = pair();
    const app = createApp(appEnd, { themeRoot: null, timeout: 50 });
    // A desktop that completes the handshake and then goes silent.
    hostEnd.listen((message) => {
      if ((message as { type: string }).type === 'hello') {
        hostEnd.post({
          type: 'init',
          protocol: PROTOCOL_VERSION,
          context: { ...context, capabilities: [] },
        });
      }
    });
    const connected = await app;
    await expect(connected.window.close()).rejects.toMatchObject({ code: 'sdk.request.timeout' });
  });
});

describe('backend addresses', () => {
  it('put the default endpoint at api/ and every other one at api-<name>/', () => {
    expect(endpointPath('com.mueller.chat')).toBe('/apps/com.mueller.chat/api/');
    expect(endpointPath('com.mueller.chat', 'metrics')).toBe('/apps/com.mueller.chat/api-metrics/');
    for (const name of ['', 'Metrics', 'a/b', '../x', 'x'.repeat(64)]) {
      expect(() => endpointPath('com.mueller.chat', name), name).toThrow(
        expect.objectContaining({ code: 'sdk.backend.endpoint_unknown' }),
      );
    }
  });

  it('follow the window: the port of the address, ws on plain HTTP, no smuggled credentials', () => {
    const plain = backendUrl(
      'http://192.168.1.5:7484',
      'io.github.ada.notes',
      'live?x=1&pithyx-token=evil',
    );
    expect(plain.href).toBe('http://192.168.1.5:7484/apps/io.github.ada.notes/api/live?x=1');
    expect(socketUrl(plain).href).toBe(
      'ws://192.168.1.5:7484/apps/io.github.ada.notes/api/live?x=1',
    );
    expect(socketUrl(backendUrl('https://[::1]', 'a.b.c', '/x')).href).toBe(
      'wss://[::1]/apps/a.b.c/api/x',
    );
    expect(backendUrl('https://nas', 'a.b.c', '//evil.example/x').origin).toBe('https://nas');
    expect(() => backendUrl('https://nas', 'a.b.c', 'x/../../../d.e.f/api/y')).toThrow(
      expect.objectContaining({ code: 'sdk.request.invalid' }),
    );
  });
});

describe('the host side', () => {
  it('refuses a protocol it does not speak', async () => {
    const [appEnd, hostEnd] = inProcessPair();
    serveApp(hostEnd, { context, methods: {} });
    const answers: unknown[] = [];
    appEnd.listen((message) => answers.push(message));
    appEnd.post({ type: 'hello', protocol: PROTOCOL_VERSION + 1, sdk: '9.0.0' });
    await vi.waitFor(() => {
      expect(answers).toHaveLength(1);
    });
    expect(answers[0]).toMatchObject({
      type: 'refuse',
      problem: { code: 'sdk.protocol.unsupported' },
    });
  });

  it('ignores requests before the handshake and messages of the wrong shape', async () => {
    const [appEnd, hostEnd] = inProcessPair();
    const close = vi.fn();
    serveApp(hostEnd, { context, methods: { 'window.close': close } });
    const answers: unknown[] = [];
    appEnd.listen((message) => answers.push(message));
    appEnd.post({ type: 'request', id: 1, method: 'window.close', params: {} });
    appEnd.post('window.close');
    appEnd.post({ type: 'request', id: 'x', method: 'window.close', params: {} });
    appEnd.post({ type: 'hello', protocol: PROTOCOL_VERSION, sdk: '0.1.0' });
    appEnd.post({ type: 'hello', protocol: PROTOCOL_VERSION, sdk: '0.1.0' });
    await vi.waitFor(() => {
      expect(answers).toHaveLength(1);
    });
    expect(answers[0]).toMatchObject({ type: 'init' });
    expect(close).not.toHaveBeenCalled();
  });

  it('knows which app called from the connection, whatever the message says', async () => {
    const calls: string[] = [];
    const apps = await Promise.all(
      ['alpha', 'beta'].map((id) => {
        const [appEnd, hostEnd] = inProcessPair();
        serveApp(hostEnd, {
          context: { ...context, app: { id, version: '1.0.0' } },
          methods: {
            'window.setTitle': ({ title }) => {
              calls.push(`${id}:${title}`);
            },
          },
        });
        return createApp(appEnd, { themeRoot: null });
      }),
    );
    // An app that claims to be another one in its parameters changes nothing about who it is.
    await apps[0]?.call('window.setTitle', { title: 'beta', app: 'beta' } as { title: string });
    await apps[1]?.window.setTitle('own');
    expect(calls).toEqual(['alpha:beta', 'beta:own']);
  });

  it('answers nothing after it closed', async () => {
    const [appEnd, hostEnd] = inProcessPair();
    const host = serveApp(hostEnd, { context, methods: { 'window.close': () => undefined } });
    const app = await createApp(appEnd, { themeRoot: null, timeout: 50 });
    host.close();
    await expect(app.window.close()).rejects.toMatchObject({ code: 'sdk.request.timeout' });
    expect(await host.ask('beforeClose', {}, 50)).toBeUndefined();
  });
});

describe('host extensions', () => {
  it('serve their own checked methods, outside the capabilities, only where the host passes them', async () => {
    const [appEnd, hostEnd] = inProcessPair();
    serveApp(hostEnd, {
      context,
      methods: {},
      extensions: {
        'host.echo': {
          validate: (params) => typeof params === 'object' && params !== null && 'text' in params,
          handle: (params) => params,
        },
      },
    });
    const connected = await createApp(appEnd, { themeRoot: null, timeout: 500 });
    const call = (method: string, params: unknown) =>
      connected.call(method as 'window.close', params as never);

    expect(connected.context.capabilities).toEqual([]);
    await expect(call('host.echo', { text: 'hi' })).resolves.toEqual({ text: 'hi' });
    await expect(call('host.echo', {})).rejects.toMatchObject({ code: 'sdk.request.invalid' });

    const [otherApp, otherHost] = inProcessPair();
    serveApp(otherHost, { context, methods: {} });
    const sandboxed = await createApp(otherApp, { themeRoot: null, timeout: 500 });
    const sandboxedCall = (method: string, params: unknown) =>
      sandboxed.call(method as 'window.close', params as never);
    await expect(sandboxedCall('host.echo', { text: 'hi' })).rejects.toMatchObject({
      code: 'sdk.method.unknown',
    });
  });

  it('cannot shadow a public method', async () => {
    const [appEnd, hostEnd] = inProcessPair();
    serveApp(hostEnd, {
      context,
      methods: {},
      extensions: { notify: { validate: () => true, handle: () => 'shadowed' } },
    });
    const connected = await createApp(appEnd, { themeRoot: null, timeout: 500 });

    await expect(connected.notify({ title: 'x' })).rejects.toMatchObject({
      code: 'sdk.method.unknown',
    });
  });
});
