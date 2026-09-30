import { PithyxApiError, unwrap, type components, type ErrorCode } from '@pithyx/api-client';
import type { MethodName, PithyxApp } from '@pithyx/sdk';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import type { DescribedError } from '../../errors/describe';

export type UserSummary = components['schemas']['UserSummary'];
export type NewUser = components['schemas']['NewUser'];
export type UserUpdate = components['schemas']['UserUpdate'];

/** User management for administrators, outside the public SDK; the daemon checks the right on every request. */
interface UserMethods {
  'internal.admin.users.list': { params: Record<string, never>; result: UserSummary[] };
  'internal.admin.users.create': { params: NewUser; result: UserSummary };
  'internal.admin.users.update': { params: UserUpdate & { userId: number }; result: UserSummary };
  'internal.admin.users.resetPassword': {
    params: { userId: number; password: string };
    result: null;
  };
  'internal.admin.users.delete': { params: { userId: number }; result: null };
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const hasUserId = (value: unknown): value is Record<string, unknown> & { userId: number } =>
  isRecord(value) && Number.isSafeInteger(value.userId);

async function daemon<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    throw error instanceof PithyxApiError
      ? new PithyxError({
          code: error.code,
          params: error.params,
          detail: error.message,
          requestId: error.requestId,
        })
      : error;
  }
}

/**
 * The host side of the Users page, for the window that shows it (the Settings app, once its new shell is in dev).
 * Passwords only pass through to the daemon; nothing here keeps them.
 */
export function usersHost(): HostExtensions {
  return {
    'internal.admin.users.list': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/users'))),
    },
    'internal.admin.users.create': {
      validate: (value) =>
        isRecord(value) && typeof value.username === 'string' && typeof value.password === 'string',
      handle: (params) =>
        daemon(async () => unwrap(await api.POST('/api/users', { body: params as NewUser }))),
    },
    'internal.admin.users.update': {
      validate: hasUserId,
      handle: (params) => {
        const { userId, ...body } = params as UserUpdate & { userId: number };
        return daemon(async () =>
          unwrap(await api.PATCH('/api/users/{userId}', { params: { path: { userId } }, body })),
        );
      },
    },
    'internal.admin.users.resetPassword': {
      validate: (value) => hasUserId(value) && typeof value.password === 'string',
      handle: async (params) => {
        const { userId, password } = params as { userId: number; password: string };
        await daemon(async () => {
          unwrap(
            await api.PUT('/api/users/{userId}/password', {
              params: { path: { userId } },
              body: { password },
            }),
          );
        });
        return null;
      },
    },
    'internal.admin.users.delete': {
      validate: hasUserId,
      handle: async (params) => {
        const { userId } = params as { userId: number };
        await daemon(async () => {
          unwrap(await api.DELETE('/api/users/{userId}', { params: { path: { userId } } }));
        });
        return null;
      },
    },
  };
}

export async function usersCall<M extends keyof UserMethods>(
  app: PithyxApp,
  method: M,
  params: UserMethods[M]['params'],
): Promise<UserMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as UserMethods[M]['result'];
}

/** A failed call as the error notice shows it: the problem the SDK passed on. */
export function describeProblem(error: unknown): DescribedError {
  const problem = error as { code?: string; params?: Record<string, unknown>; requestId?: string };
  return {
    code: (problem.code ?? 'system.internal.unexpected') as ErrorCode,
    params: problem.params ?? {},
    requestId: problem.requestId ?? '',
  };
}
