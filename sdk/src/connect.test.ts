import { afterEach, describe, expect, it, vi } from 'vitest';
import { serveApp } from './host';
import { connect } from './index';
import { themeTokenNames, type Theme } from './theme';
import { portEndpoint } from './transport';
import { PROTOCOL_VERSION } from './version';

const theme: Theme = {
  scheme: 'light',
  density: 'comfortable',
  tokens: Object.fromEntries(themeTokenNames.map((name) => [name, '#fff'])) as Theme['tokens'],
};

/** A stand-in for the iframe's window: its parent is the desktop, anything else is a stranger. */
function fakeWindow() {
  const listeners = new Set<(event: MessageEvent) => void>();
  const parent = {};
  const frame = {
    parent,
    addEventListener: (_: string, listener: (event: MessageEvent) => void) =>
      listeners.add(listener),
    removeEventListener: (_: string, listener: (event: MessageEvent) => void) =>
      listeners.delete(listener),
  };
  vi.stubGlobal('window', frame);
  vi.stubGlobal('document', { addEventListener: () => undefined });
  const post = (source: unknown, data: unknown, ports: MessagePort[]) => {
    for (const listener of [...listeners]) {
      listener({ source, data, ports } as unknown as MessageEvent);
    }
  };
  return { parent, post, listeners };
}

/** A desktop end that answers the handshake with this app id. */
function desktopPort(app: string): MessagePort {
  const channel = new MessageChannel();
  serveApp(portEndpoint(channel.port1), {
    context: {
      app: { id: app, version: '1.0.0' },
      instance: 'w1',
      user: { id: 1, name: 'ada', displayName: null },
      locale: 'en-US',
      timeZone: 'UTC',
      theme,
      sizeClass: 'expanded',
      launch: { args: null },
      session: null,
      permissions: [],
      host: { version: '0.1.0', protocol: PROTOCOL_VERSION },
    },
    methods: {},
  });
  return channel.port2;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('connect', () => {
  it('takes the port only from the parent window, only once and only alone', async () => {
    const { parent, post, listeners } = fakeWindow();
    const connecting = connect({ themeRoot: null, connectTimeout: 1000 });
    const stranger = {};
    post(stranger, { type: 'pithyx:connect' }, [desktopPort('evil')]);
    post(parent, { type: 'something-else' }, [desktopPort('evil')]);
    post(parent, { type: 'pithyx:connect' }, []);
    post(parent, { type: 'pithyx:connect' }, [desktopPort('evil'), desktopPort('evil')]);
    post(parent, { type: 'pithyx:connect' }, [desktopPort('real')]);
    // After the first valid connect the app no longer listens, so a later port cannot take it over.
    expect(listeners.size).toBe(0);
    post(parent, { type: 'pithyx:connect' }, [desktopPort('evil')]);
    const app = await connecting;
    expect(app.context.app.id).toBe('real');
  });

  it('gives up when no desktop connects', async () => {
    fakeWindow();
    await expect(connect({ themeRoot: null, connectTimeout: 20 })).rejects.toMatchObject({
      code: 'sdk.transport.closed',
    });
  });

  it('does not connect a page that is not framed', async () => {
    const { post } = fakeWindow();
    const self = window as unknown as { parent: unknown };
    self.parent = self;
    const connecting = connect({ themeRoot: null, connectTimeout: 20 });
    post(self, { type: 'pithyx:connect' }, [desktopPort('evil')]);
    await expect(connecting).rejects.toMatchObject({ code: 'sdk.transport.closed' });
  });
});
