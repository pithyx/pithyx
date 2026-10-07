import { CSRF_HEADER, PithyxApiError, unwrap, type components } from '@pithyx/api-client';
import type { BackendToken, FileLocation } from '@pithyx/sdk';
import { api } from '../../api/client';

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

  return {
    call,
    raw,
    async backendToken(): Promise<BackendToken> {
      const token = await backend.get();
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
