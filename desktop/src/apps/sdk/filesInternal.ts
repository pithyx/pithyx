import { unwrap, type components } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import type { HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { realtime } from '../../realtime/connection';
import { openApp } from '../../wm/store';
import { appsFor } from '../files/defaultAction';
import { coreAppIds } from '../ids';
import { installedApp } from '../installed';

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

/** A file to open with an installed app, with what the Files app knows about it. */
export interface AppTarget extends ViewerTarget {
  app: string;
  size: number;
  modified: string;
  writable: boolean;
}

/** A selection in Files handed to an installed app's file action (D375). */
export interface ActionTarget {
  app: string;
  action: string;
  share: string;
  folder: string;
  names: string[];
}

interface InternalFilesMethods {
  'internal.files.thumbnails': {
    params: { share: string; folder: string; names: string[]; size: ThumbnailSize };
    result: ThumbnailState[];
  };
  'internal.files.openViewer': { params: ViewerTarget; result: boolean };
  'internal.files.openWithApp': { params: AppTarget; result: boolean };
  'internal.files.runAction': { params: ActionTarget; result: boolean };
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

function isAppTarget(value: unknown): value is AppTarget {
  return (
    isTarget(value) &&
    typeof (value as Partial<AppTarget>).app === 'string' &&
    Number.isFinite((value as Partial<AppTarget>).size) &&
    typeof (value as Partial<AppTarget>).modified === 'string' &&
    typeof (value as Partial<AppTarget>).writable === 'boolean'
  );
}

function isActionTarget(value: unknown): value is ActionTarget {
  return (
    isRecord(value) &&
    typeof value.app === 'string' &&
    typeof value.action === 'string' &&
    typeof value.share === 'string' &&
    typeof value.folder === 'string' &&
    Array.isArray(value.names) &&
    value.names.length > 0 &&
    value.names.every((name) => typeof name === 'string' && name !== '' && !name.includes('/'))
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
        handle: (params) => openApp(coreAppIds.viewer, undefined, { session: params }),
      },
      'internal.files.runAction': {
        validate: isActionTarget,
        handle: (params) => {
          const target = params as ActionTarget;
          const app = installedApp(target.app);
          const action = app?.fileActions.find((candidate) => candidate.id === target.action);
          // Only an action the app declares, for as many items as it takes, of an app that may use shares.
          if (
            app === undefined ||
            action === undefined ||
            !app.permissions.includes('files.shares') ||
            (!action.multiple && target.names.length > 1)
          ) {
            return false;
          }
          return openApp(target.app, {
            args: null,
            action: {
              id: action.id,
              location: { share: target.share },
              folder: target.folder,
              names: target.names,
            },
          });
        },
      },
      'internal.files.openWithApp': {
        validate: isAppTarget,
        handle: (params) => {
          const target = params as AppTarget;
          const app = installedApp(target.app);
          // Only an installed app, and only for a file its manifest says it opens.
          if (
            app === undefined ||
            appsFor(
              { name: target.name, kind: 'file', size: target.size, modified: target.modified },
              [app],
            ).length === 0
          ) {
            return false;
          }
          return openApp(target.app, {
            args: null,
            pendingFile: {
              share: target.share,
              path: target.folder === '' ? target.name : `${target.folder}/${target.name}`,
              size: target.size,
              modified: target.modified,
              writable: target.writable,
            },
          });
        },
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
