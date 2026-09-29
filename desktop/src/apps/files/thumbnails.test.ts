import type { FileEntry } from '@pithyx/sdk';
import { describe, expect, it, vi } from 'vitest';
import type { ThumbnailState } from '../sdk/filesInternal';
import { createThumbnails, thumbnailKey, type Ask } from './thumbnails';

function file(name: string, size = 10, modified = '2026-09-29T01:00:00Z'): FileEntry {
  return { name, kind: 'file', size, modified };
}

async function settle() {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('createThumbnails', () => {
  it('asks only for images, once per version of a file', async () => {
    const ask = vi.fn<Ask>((_share, _folder, names) =>
      Promise.resolve(
        names.map((name): ThumbnailState => ({ name, status: 'ready', version: `v-${name}` })),
      ),
    );
    const thumbnails = createThumbnails(ask, () => undefined);
    const beach = file('beach.jpg');

    thumbnails.request(
      '1',
      'trip',
      [beach, file('notes.txt'), { ...file('album'), kind: 'folder' }],
      'large',
    );
    await settle();
    thumbnails.request('1', 'trip', [beach], 'large');
    thumbnails.request('1', 'trip', [file('beach.jpg', 11)], 'large');
    await settle();

    expect(ask.mock.calls.map((call) => call[2])).toEqual([['beach.jpg'], ['beach.jpg']]);
    expect(thumbnails.store.getState().byKey[thumbnailKey('1', 'trip', 'large', beach)]).toEqual({
      status: 'ready',
      version: 'v-beach.jpg',
    });
  });

  it('places events by name on the version shown now', async () => {
    const thumbnails = createThumbnails(
      (_share, _folder, names) =>
        Promise.resolve(
          names.map((name): ThumbnailState => ({ name, status: 'pending', version: null })),
        ),
      () => undefined,
    );
    const beach = file('beach.jpg');
    thumbnails.request('1', 'trip', [beach], 'small');
    await settle();

    thumbnails.receive({
      share: '1',
      folder: 'trip',
      size: 'small',
      items: [{ name: 'beach.jpg', status: 'ready', version: 'v1' }],
    });
    thumbnails.receive({
      share: '1',
      folder: 'other',
      size: 'small',
      items: [{ name: 'beach.jpg', status: 'none', version: null }],
    });

    expect(thumbnails.store.getState().byKey[thumbnailKey('1', 'trip', 'small', beach)]).toEqual({
      status: 'ready',
      version: 'v1',
    });
  });

  it('asks again for what is still pending, in case its event came too early', async () => {
    const runs: (() => void)[] = [];
    let answer: ThumbnailState['status'] = 'pending';
    const ask = vi.fn<Ask>((_share, _folder, names) =>
      Promise.resolve(
        names.map((name) => ({ name, status: answer, version: answer === 'ready' ? 'v2' : null })),
      ),
    );
    const thumbnails = createThumbnails(ask, (run) => {
      runs.push(run);
    });
    const beach = file('beach.jpg');
    thumbnails.request('1', 'trip', [beach], 'large');
    await settle();

    answer = 'ready';
    runs.shift()?.();
    await settle();

    expect(ask).toHaveBeenCalledTimes(2);
    expect(
      thumbnails.store.getState().byKey[thumbnailKey('1', 'trip', 'large', beach)]?.status,
    ).toBe('ready');
    expect(runs).toEqual([]);
  });

  it('shows icons when the daemon cannot be asked', async () => {
    const thumbnails = createThumbnails(
      () => Promise.reject(new Error('offline')),
      () => undefined,
    );
    const beach = file('beach.jpg');
    thumbnails.request('1', '', [beach], 'large');
    await settle();

    expect(thumbnails.store.getState().byKey[thumbnailKey('1', '', 'large', beach)]?.status).toBe(
      'none',
    );
  });

  it('splits long screens into batches the daemon accepts', async () => {
    const ask = vi.fn<Ask>(() => Promise.resolve([]));
    const thumbnails = createThumbnails(ask, () => undefined);

    thumbnails.request(
      '1',
      '',
      Array.from({ length: 450 }, (_, index) => file(`${String(index)}.png`)),
      'small',
    );
    await settle();

    expect(ask.mock.calls.map((call) => call[2].length)).toEqual([200, 200, 50]);
  });
});
