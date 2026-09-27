import { describe, expect, it, vi } from 'vitest';
import { createApp } from './app';
import { VyxisError } from './errors';
import { serveApp, type MethodHandlers } from './host';
import type { AppContext } from './protocol';
import { themeTokenNames, type Theme } from './theme';
import { inProcessPair, portEndpoint, type Endpoint } from './transport';
import { PROTOCOL_VERSION } from './version';

function theme(scheme: 'light' | 'dark'): Theme {
  return {
    scheme,
    tokens: Object.fromEntries(
      themeTokenNames.map((name) => [name, scheme === 'dark' ? '#000' : '#fff']),
    ) as Theme['tokens'],
  };
}

const context: Omit<AppContext, 'capabilities'> = {
  app: { id: 'demo', version: '1.0.0' },
  instance: 'w1',
  user: { id: 1, name: 'ada', displayName: 'Ada Admin' },
  locale: 'en-US',
  timeZone: 'Europe/Berlin',
  theme: theme('light'),
  sizeClass: 'expanded',
  launch: { args: null },
  session: { folder: '/tank' },
  permissions: ['notifications'],
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

describe.each(transports)('over the %s transport', (_, pair) => {
  function connectTo(methods: MethodHandlers) {
    const [appEnd, hostEnd] = pair();
    const host = serveApp(hostEnd, { context, methods });
    return { host, app: createApp(appEnd, { themeRoot: null, timeout: 500 }) };
  }

  it('hands the app its context with the capabilities the desktop serves', async () => {
    const { app } = connectTo({ 'window.setTitle': () => undefined, notify: () => undefined });
    const connected = await app;
    expect(connected.context.user.name).toBe('ada');
    expect(connected.context.capabilities).toEqual(['window.setTitle', 'notify']);
    expect(connected.supports('notify')).toBe(true);
    expect(connected.supports('storage.get')).toBe(false);
    expect(connected.session.restore()).toEqual({ folder: '/tank' });
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
        throw new VyxisError({
          code: 'desktop.item.conflict',
          params: { id: 7, version: 3 },
          detail: 'Changed elsewhere',
          requestId: 'abc123',
        });
      },
    });
    const connected = await app;
    const error = await connected.storage.set('k', 1).catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(VyxisError);
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

  it('turns handler failures that are not problems into an unexpected error', async () => {
    const { app } = connectTo({
      'window.close': () => {
        throw new Error('secret internals');
      },
    });
    const connected = await app;
    const error = (await connected.window.close().catch((caught: unknown) => caught)) as VyxisError;
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
    host.emit('localeChange', { locale: 'de-DE', timeZone: 'UTC' });
    await vi.waitFor(() => {
      expect(connected.context.locale).toBe('de-DE');
    });
    expect(themes).toEqual(['dark']);
    expect(focus).toHaveBeenCalledOnce();
    expect(connected.context.theme.scheme).toBe('dark');
    stop();
    host.emit('themeChange', theme('light'));
    host.emit('permissionRevoked', { permission: 'notifications' });
    await vi.waitFor(() => {
      expect(connected.context.theme.scheme).toBe('light');
    });
    expect(themes).toEqual(['dark']);
  });

  it('lets the app keep a window open and gives up on apps that do not answer', async () => {
    const { host, app } = connectTo({});
    const connected = await app;
    expect(await host.ask('beforeClose', {}, 500)).toEqual({ allow: true });
    connected.answer('beforeClose', () => ({ allow: false }));
    expect(await host.ask('beforeClose', {}, 500)).toEqual({ allow: false });
    connected.answer('beforeLogout', () => new Promise(() => undefined));
    expect(await host.ask('beforeLogout', {}, 50)).toBeUndefined();
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
