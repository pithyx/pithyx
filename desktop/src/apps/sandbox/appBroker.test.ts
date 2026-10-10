import type { FileRef } from '@pithyx/sdk';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { answerFileDialog, useHost, type ChosenFile } from '../host/store';
import { useDesktop } from '../../wm/store';
import type { AppApi } from './appApi';

const posts: { url: string; body: unknown }[] = [];

vi.mock('../../api/client', () => ({
  api: {
    POST: (url: string, options: { body?: unknown }) => {
      posts.push({ url, body: options.body });
      return Promise.resolve({ data: undefined, response: new Response(null, { status: 204 }) });
    },
  },
}));

const { appBrokerFor } = await import('./appBroker');

const chosen: ChosenFile = {
  share: 3,
  path: 'photos/a.png',
  name: 'a.png',
  size: 10,
  modified: '2026-10-10T10:00:00Z',
  access: 'readWrite',
  overwrite: false,
};

function broker(permissions: string[]) {
  const daemon = {
    call: async (send: (headers: { Authorization: string }) => Promise<{ data?: unknown }>) =>
      (await send({ Authorization: 'Bearer t' })).data,
  } as unknown as AppApi;
  return appBrokerFor({
    app: 'com.example.demo',
    windowId: 'w1',
    daemon,
    logLevel: () => 'info',
    permissions: () => permissions,
    emit: () => undefined,
  });
}

beforeEach(() => {
  posts.length = 0;
  useDesktop.setState({ focused: 'w1' });
});

afterEach(() => {
  answerFileDialog(null);
});

describe('the file dialogs of a third-party app', () => {
  it('need files.read to pick and files.write to save, before any dialog opens', async () => {
    const { methods } = broker(['files.shares']);
    await expect(methods['files.pick']?.({ multiple: false })).rejects.toMatchObject({
      code: 'app.permission.denied',
      params: { permission: 'files.read' },
    });
    await expect(
      methods['files.saveAs']?.({ suggestedName: 'a.txt', data: new Blob(['x']) }),
    ).rejects.toMatchObject({
      code: 'app.permission.denied',
      params: { permission: 'files.write' },
    });
    expect(useHost.getState().fileDialog).toBeNull();
  });

  it('open only for the window the user works in', async () => {
    useDesktop.setState({ focused: 'other' });
    const { methods } = broker(['files.read', 'files.write']);
    expect(await methods['files.pick']?.({})).toEqual([]);
    expect(await methods['files.saveAs']?.({ suggestedName: 'a', data: new Blob([]) })).toBeNull();
    expect(useHost.getState().fileDialog).toBeNull();
  });

  it('hand the chosen file to the daemon as read-only unless the app may write', async () => {
    const reader = broker(['files.read']);
    const reading = reader.methods['files.pick']?.({ multiple: false, extensions: ['.png'] });
    await vi.waitFor(() => {
      expect(useHost.getState().fileDialog).toMatchObject({ kind: 'pick', extensions: ['png'] });
    });
    answerFileDialog([chosen]);
    const [file] = (await reading) as unknown as FileRef[];
    expect(file).toMatchObject({ name: 'a.png', size: 10, writable: false });
    expect(posts).toEqual([
      { url: '/api/app/files/handles', body: { share: '3', path: 'photos/a.png', write: false } },
    ]);

    const writer = broker(['files.read', 'files.write']);
    const writing = writer.methods['files.pick']?.({});
    await vi.waitFor(() => {
      expect(useHost.getState().fileDialog).not.toBeNull();
    });
    answerFileDialog([chosen]);
    expect(((await writing) as unknown as FileRef[])[0]?.writable).toBe(true);
    expect(posts.at(-1)?.body).toMatchObject({ write: true });
  });

  it('give an empty list when the user cancels', async () => {
    const { methods } = broker(['files.read']);
    const picking = methods['files.pick']?.({});
    await vi.waitFor(() => {
      expect(useHost.getState().fileDialog).not.toBeNull();
    });
    answerFileDialog(null);
    expect(await picking).toEqual([]);
    expect(posts).toEqual([]);
  });

  it('give a launched file only to an app that can read it', async () => {
    const file = {
      share: '3',
      path: 'photos/a.png',
      size: 10,
      modified: '2026-10-10T10:00:00Z',
      writable: true,
    };
    expect(await broker(['files.shares']).adopt(file)).toBeUndefined();
    expect(await broker(['files.read']).adopt(file)).toMatchObject({
      name: 'a.png',
      writable: false,
    });
    expect(posts.at(-1)?.body).toMatchObject({ write: false });
  });
});
