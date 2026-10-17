import { CSRF_HEADER, PithyxApiError, unwrap, type components } from '@pithyx/api-client';
import type { BackendToken, FileLocation, FileOperation } from '@pithyx/sdk';
import { PithyxError } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { putFile } from '../sdk/fileMapping';

type AppToken = components['schemas']['AppToken'];
type TokenKind = 'host' | 'backend';

/** A token is renewed this long before it expires, so a call never goes out with one that is about to end. */
const RENEW_BEFORE = 60_000;

/** One kind of token of one window, fetched when needed and renewed before it expires. */
function tokenCache(app: string, instance: string, kind: TokenKind) {
  let current: AppToken | undefined;
  let pending: Promise<AppToken> | undefined;
  return {
    async get(fresh = false): Promise<AppToken> {
      if (
        !fresh &&
        current !== undefined &&
        new Date(current.expiresAt).getTime() - Date.now() > RENEW_BEFORE
      ) {
        return current;
      }
      pending ??= api
        .POST('/api/apps/{appId}/tokens', {
          params: { path: { appId: app } },
          body: { kind, instance },
        })
        .then((result) => {
          current = unwrap(result);
          return current;
        })
        .finally(() => {
          pending = undefined;
        });
      return pending;
    },
    clear() {
      current = undefined;
    },
  };
}

/** The query of a file location: `share=<id>` or `storage=<id>`. */
export function locationQuery(location: FileLocation): { share?: string; storage?: string } {
  return 'share' in location ? { share: location.share } : { storage: location.storage };
}

/** A `files.run` call as the daemon's API for apps takes it: each place a shared folder or a storage request (D362). */
export function appOperationBody(params: FileOperation) {
  const { kind, source, target, onConflict, permanent } = params;
  const place = locationQuery(source.location);
  const join = (name: string) => (source.folder === '' ? name : `${source.folder}/${name}`);
  const selection =
    'names' in source.items
      ? { items: source.items.names.map((name) => ({ ...place, path: join(name) })) }
      : { allIn: { folder: { ...place, path: source.folder }, except: source.items.except } };
  return {
    kind,
    ...selection,
    ...(target === undefined
      ? {}
      : { target: { ...locationQuery(target.location), path: target.path } }),
    ...(onConflict === undefined ? {} : { onConflict }),
    permanent: permanent === true,
  };
}

/**
 * The daemon's API for apps (`/api/app/**`, A11) for one window of a third-party app. The window's app and instance
 * are fixed here when the window opens, from the desktop's own state; nothing the app sends can change them. The host
 * token stays in this object and never reaches the app's frame; only `backendToken` hands a token out, one that works
 * only at the app's own backend.
 */
export function appApiFor(app: string, instance: string) {
  const host = tokenCache(app, instance, 'host');
  const backend = tokenCache(app, instance, 'backend');

  /** Runs a call with the host token; a token the daemon no longer takes is renewed once. */
  async function call<T>(
    send: (headers: { Authorization: string }) => Promise<{
      data?: T;
      error?: unknown;
      response: Response;
    }>,
  ): Promise<T> {
    let result = await send({ Authorization: `Bearer ${(await host.get()).token}` });
    if (result.response.status === 401) {
      result = await send({ Authorization: `Bearer ${(await host.get(true)).token}` });
    }
    return unwrap(result);
  }

  /** A raw request with the host token, for file contents, which the typed client does not stream. */
  async function raw(path: string, init: RequestInit = {}): Promise<Response> {
    const send = async (fresh: boolean) =>
      fetch(path, {
        ...init,
        credentials: 'omit',
        headers: {
          ...(init.headers as Record<string, string> | undefined),
          Authorization: `Bearer ${(await host.get(fresh)).token}`,
          [CSRF_HEADER]: '1',
        },
      });
    let response = await send(false);
    if (response.status === 401) {
      response = await send(true);
    }
    if (!response.ok) {
      unwrap({ error: await response.json().catch(() => undefined), response });
    }
    return response;
  }

  /** Uploads a file with progress; a token the daemon no longer takes is renewed once. */
  async function upload(
    path: string,
    name: string,
    data: Blob,
    onProgress: (loaded: number) => void,
  ): Promise<components['schemas']['AppFileEntry']> {
    const send = async (fresh: boolean) =>
      putFile<components['schemas']['AppFileEntry']>(path, name, data, onProgress, {
        Authorization: `Bearer ${(await host.get(fresh)).token}`,
        [CSRF_HEADER]: '1',
      });
    try {
      return await send(false);
    } catch (error) {
      if (error instanceof PithyxError && error.code === 'app.token.invalid') {
        return send(true);
      }
      throw error;
    }
  }

  return {
    call,
    raw,
    upload,
    /** The backend token; `renew` drops the cached one first, after the proxy refused it. */
    async backendToken(renew = false): Promise<BackendToken> {
      const token = await backend.get(renew);
      return { token: token.token, expiresAt: token.expiresAt };
    },
    /** Forgets the tokens, for example after the app's permissions changed. */
    reset() {
      host.clear();
      backend.clear();
    },
  };
}

export type AppApi = ReturnType<typeof appApiFor>;

/** Whether an error says the window's app is no longer available to the user. */
export function isAccessGone(error: unknown): boolean {
  return error instanceof PithyxApiError && error.code === 'app.app.not_found';
}
