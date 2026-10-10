// A unit test without a browser: the app's code talks to a fake desktop over the SDK's in-process transport, with the
// same protocol, parameter checks and permission checks as a real window. Run with `node --test`.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  createApp,
  inProcessPair,
  themeTokenNames,
  type AppContext,
  type PithyxApp,
  type Theme,
} from '@pithyx/sdk';
import { serveApp, type AppConnection, type MethodHandlers } from '@pithyx/sdk/host';
import { people } from './permissions.ts';

/** A context like the desktop's init message, for a user without directory.read. */
function testContext(permissions: string[]): Omit<AppContext, 'capabilities'> {
  const theme: Theme = {
    scheme: 'light',
    density: 'comfortable',
    tokens: Object.fromEntries(themeTokenNames.map((name) => [name, '0'])) as Theme['tokens'],
  };
  return {
    app: { id: 'com.example.board', version: '1.0.0' },
    instance: 'test-1',
    user: { id: 2, name: 'bob', displayName: 'Bob', groups: [], isAdmin: false, twoFactor: false },
    language: 'en',
    locale: 'en-US',
    timeZone: 'UTC',
    theme,
    sizeClass: 'expanded',
    launch: { args: null },
    session: null,
    permissions,
    box: { name: 'test', version: '0.1.0' },
    visible: true,
    online: true,
    logLevel: 'info',
    backend: null,
    storage: [],
    host: { version: '1.0.0', protocol: 1 },
  };
}

/** Connects app code to a fake desktop that serves only the given methods. */
async function fakeDesktop(
  permissions: string[],
  methods: MethodHandlers,
): Promise<{ app: PithyxApp; desktop: AppConnection }> {
  const [appEnd, desktopEnd] = inProcessPair();
  const desktop = serveApp(desktopEnd, { context: testContext(permissions), methods });
  const app = await createApp(appEnd, { themeRoot: null });
  return { app, desktop };
}

const directory: MethodHandlers = {
  'directory.users': () => [
    { id: 1, name: 'alice', displayName: 'Alice', groups: [] },
    { id: 2, name: 'bob', displayName: 'Bob', groups: [] },
  ],
};

await test('without directory.read only the signed-in user can be picked', async () => {
  const { app } = await fakeDesktop([], directory);
  assert.deepEqual(await people(app), [{ id: 2, name: 'bob', displayName: 'Bob' }]);
});

await test('a granted directory.read lists everyone, a revoked one falls back', async () => {
  const { app, desktop } = await fakeDesktop(['directory.read'], directory);
  assert.equal((await people(app)).length, 2);

  desktop.emit('permissionsChange', { permissions: [] });
  // Events arrive asynchronously, like over a MessagePort.
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(app.context.permissions, []);
  assert.equal((await people(app)).length, 1);
});
