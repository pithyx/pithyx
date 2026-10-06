import { unwrap, type components } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import type { HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { realtime } from '../../realtime/connection';
import { openApp } from '../../wm/store';
import { coreAppIds } from '../ids';

export type ThumbnailSize = components['schemas']['ThumbnailSize'];
export type ThumbnailState = components['schemas']['ThumbnailState'];

/** Thumbnails that were rendered or refused; `folder` and `size` say which request they answer. */
export const THUMBNAILS_READY = 'internal.files.thumbnails';

export interface ThumbnailsReady {
  share: string;
  folder: string;
  size: ThumbnailSize;
  items: ThumbnailState[];
}

/** Which file a viewer window shows; also its saved session, so a restored layout opens the same file. */
export interface ViewerTarget {
  share: string;
  folder: string;
  name: string;
}

interface InternalFilesMethods {
  'internal.files.thumbnails': {
    params: { share: string; folder: string; names: string[]; size: ThumbnailSize };
    result: ThumbnailState[];
  };
  'internal.files.openViewer': { params: ViewerTarget; result: boolean };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isTarget(value: unknown): value is ViewerTarget {
  return (
    isRecord(value) &&
    typeof value.share === 'string' &&
    typeof value.folder === 'string' &&
    typeof value.name === 'string'
  );
}

function isThumbnailRequest(value: unknown): boolean {
  return (
    isRecord(value) &&
    typeof value.share === 'string' &&
    typeof value.folder === 'string' &&
    (value.size === 'small' || value.size === 'large') &&
    Array.isArray(value.names) &&
    value.names.every((name) => typeof name === 'string')
  );
}

/** The URL of a ready thumbnail; `version` makes it change with the file. */
export function thumbnailUrl(
  share: string,
  folder: string,
  name: string,
  size: ThumbnailSize,
  version: string,
): string {
  const path = folder === '' ? name : `${folder}/${name}`;
  return `/api/files/${share}/thumbnail?${new URLSearchParams({ path, size, v: version }).toString()}`;
}

/**
 * The host side of the Files app's own calls: thumbnails of the entries on screen, and opening a file in the
 * viewer. Thumbnail events of the folder last asked about are followed until another folder is, or the window
 * closes.
 */
export function filesHost(emit: (event: string, data?: unknown) => void): {
  extensions: HostExtensions;
  close: () => void;
} {
  let followed: { topic: string; release: () => void } | null = null;
  const follow = (topic: string, share: string, folder: string) => {
    if (followed?.topic === topic) {
      return;
    }
    followed?.release();
    followed = {
      topic,
      release: realtime.listen(topic, (data) => {
        const ready = data as Partial<components['schemas']['ThumbnailsReady']>;
        // The snapshot only says the topic is followed.
        if (ready.items !== undefined && ready.size !== undefined) {
          emit(THUMBNAILS_READY, { share, folder, size: ready.size, items: ready.items });
        }
      }),
    };
  };
  return {
    extensions: {
      'internal.files.thumbnails': {
        validate: isThumbnailRequest,
        handle: async (params) => {
          const { share, folder, names, size } =
            params as InternalFilesMethods['internal.files.thumbnails']['params'];
          const states = unwrap(
            await api.POST('/api/files/{shareId}/thumbnails', {
              params: { path: { shareId: Number(share) } },
              body: { folder, names, size },
            }),
          );
          if (states.items.some((item) => item.status === 'pending')) {
            follow(states.topic, share, folder);
          }
          return states.items;
        },
      },
      'internal.files.openViewer': {
        validate: isTarget,
        handle: (params) => openApp(coreAppIds.viewer, { session: params }),
      },
    },
    close: () => {
      followed?.release();
      followed = null;
    },
  };
}

export async function filesCall<M extends keyof InternalFilesMethods>(
  app: PithyxApp,
  method: M,
  params: InternalFilesMethods[M]['params'],
): Promise<InternalFilesMethods[M]['result']> {
  // The SDK's call is typed to the public methods; internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as InternalFilesMethods[M]['result'];
}

export function onThumbnails(
  app: PithyxApp,
  handler: (ready: ThumbnailsReady) => void,
): () => void {
  return app.on(THUMBNAILS_READY as unknown as EventName, (data) => {
    handler(data as unknown as ThumbnailsReady);
  });
}
