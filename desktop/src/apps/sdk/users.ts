import { unwrap, type components, type ErrorCode } from '@pithyx/api-client';
import type { MethodName, PithyxApp } from '@pithyx/sdk';
import { type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import type { DescribedError } from '../../errors/describe';
import { daemon } from './daemonError';

export type UserSummary = components['schemas']['UserSummary'];
export type NewUser = components['schemas']['NewUser'];
export type UserUpdate = components['schemas']['UserUpdate'];
export type GroupSummary = components['schemas']['GroupSummary'];
export type NewGroup = components['schemas']['NewGroup'];
export type GroupUpdate = components['schemas']['GroupUpdate'];
export type GrantEntry = components['schemas']['GrantEntry'];
export type UserGrants = components['schemas']['UserGrants'];
export type GroupGrants = components['schemas']['GroupGrants'];
export type PermissionCatalog = components['schemas']['PermissionCatalog'];
export type HomeOverview = components['schemas']['HomeOverview'];
export type HomeInfo = components['schemas']['HomeInfo'];
export type Job = components['schemas']['Job'];

/** What happens to a deleted user's home: kept, moved into a shared folder or deleted after the name was typed. */
export type HomeChoice =
  { home: 'keep' } | { home: 'move'; shareId: number } | { home: 'delete'; confirm: string };

/** User management for administrators, outside the public SDK; the daemon checks the right on every request. */
interface UserMethods {
  'internal.admin.users.list': { params: Record<string, never>; result: UserSummary[] };
  'internal.admin.users.create': { params: NewUser; result: UserSummary };
  'internal.admin.users.update': { params: UserUpdate & { userId: number }; result: UserSummary };
  'internal.admin.users.resetPassword': {
    params: { userId: number; password: string };
    result: null;
  };
  'internal.admin.users.delete': {
    params: { userId: number } & HomeChoice;
    result: { job?: Job | null };
  };
  'internal.admin.homes.overview': { params: Record<string, never>; result: HomeOverview };
  'internal.admin.homes.setDefaultQuota': {
    params: { defaultQuotaBytes: number | null };
    result: HomeOverview;
  };
  'internal.admin.homes.setQuota': {
    params: { userId: number; quotaBytes: number | null };
    result: HomeOverview;
  };
  'internal.admin.homes.move': { params: { pool: string }; result: Job };
  'internal.admin.homes.delete': { params: { shareId: number; confirm: string }; result: null };
  'internal.admin.homes.release': {
    params: { shareId: number; targetShareId: number };
    result: Job;
  };
  'internal.admin.groups.list': { params: Record<string, never>; result: GroupSummary[] };
  'internal.admin.groups.create': { params: NewGroup; result: GroupSummary };
  'internal.admin.groups.update': {
    params: GroupUpdate & { groupId: number };
    result: GroupSummary;
  };
  'internal.admin.groups.delete': { params: { groupId: number }; result: null };
  'internal.admin.permissions.catalog': {
    params: Record<string, never>;
    result: PermissionCatalog;
  };
  'internal.admin.users.grants': { params: { userId: number }; result: UserGrants };
  'internal.admin.users.setGrants': {
    params: { userId: number; grants: GrantEntry[] };
    result: UserGrants;
  };
  'internal.admin.groups.grants': { params: { groupId: number }; result: GroupGrants };
  'internal.admin.groups.setGrants': {
    params: { groupId: number; grants: GrantEntry[] };
    result: GroupGrants;
  };
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const hasGroupId = (value: unknown): value is Record<string, unknown> & { groupId: number } =>
  isRecord(value) && Number.isSafeInteger(value.groupId);

const LEVELS: readonly string[] = ['read', 'readWrite', 'allow'];

const hasGrants = (value: Record<string, unknown>): boolean =>
  Array.isArray(value.grants) &&
  value.grants.every(
    (grant: unknown) =>
      isRecord(grant) && typeof grant.resource === 'string' && LEVELS.includes(String(grant.level)),
  );

const hasUserId = (value: unknown): value is Record<string, unknown> & { userId: number } =>
  isRecord(value) && Number.isSafeInteger(value.userId);

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
      validate: (value) =>
        hasUserId(value) &&
        (value.home === 'keep' ||
          (value.home === 'move' && Number.isSafeInteger(value.shareId)) ||
          (value.home === 'delete' && typeof value.confirm === 'string')),
      handle: (params) => {
        const { userId, ...choice } = params as { userId: number } & HomeChoice;
        return daemon(async () =>
          unwrap(
            await api.DELETE('/api/users/{userId}', {
              params: { path: { userId }, query: choice },
            }),
          ),
        );
      },
    },
    'internal.admin.homes.overview': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/homes'))),
    },
    'internal.admin.homes.setDefaultQuota': {
      validate: (value) =>
        isRecord(value) &&
        (value.defaultQuotaBytes === null || Number.isSafeInteger(value.defaultQuotaBytes)),
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.PUT('/api/homes/settings', {
              body: {
                defaultQuotaBytes: (params as { defaultQuotaBytes: number | null })
                  .defaultQuotaBytes,
              },
            }),
          ),
        ),
    },
    'internal.admin.homes.setQuota': {
      validate: (value) =>
        hasUserId(value) && (value.quotaBytes === null || Number.isSafeInteger(value.quotaBytes)),
      handle: (params) => {
        const { userId, quotaBytes } = params as { userId: number; quotaBytes: number | null };
        return daemon(async () =>
          unwrap(
            await api.PUT('/api/homes/users/{userId}/quota', {
              params: { path: { userId } },
              body: { quotaBytes },
            }),
          ),
        );
      },
    },
    'internal.admin.homes.move': {
      validate: (value) => isRecord(value) && typeof value.pool === 'string',
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/homes/move', {
              body: { pool: (params as { pool: string }).pool },
            }),
          ),
        ),
    },
    'internal.admin.homes.delete': {
      validate: (value) =>
        isRecord(value) && Number.isSafeInteger(value.shareId) && typeof value.confirm === 'string',
      handle: async (params) => {
        const { shareId, confirm } = params as { shareId: number; confirm: string };
        await daemon(async () => {
          unwrap(
            await api.DELETE('/api/homes/{shareId}', {
              params: { path: { shareId }, query: { confirm } },
            }),
          );
        });
        return null;
      },
    },
    'internal.admin.homes.release': {
      validate: (value) =>
        isRecord(value) &&
        Number.isSafeInteger(value.shareId) &&
        Number.isSafeInteger(value.targetShareId),
      handle: (params) => {
        const { shareId, targetShareId } = params as { shareId: number; targetShareId: number };
        return daemon(async () =>
          unwrap(
            await api.POST('/api/homes/{shareId}/release', {
              params: { path: { shareId } },
              body: { shareId: targetShareId },
            }),
          ),
        );
      },
    },
    'internal.admin.groups.list': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/groups'))),
    },
    'internal.admin.groups.create': {
      validate: (value) => isRecord(value) && typeof value.name === 'string',
      handle: (params) =>
        daemon(async () => unwrap(await api.POST('/api/groups', { body: params as NewGroup }))),
    },
    'internal.admin.groups.update': {
      validate: hasGroupId,
      handle: (params) => {
        const { groupId, ...body } = params as GroupUpdate & { groupId: number };
        return daemon(async () =>
          unwrap(await api.PATCH('/api/groups/{groupId}', { params: { path: { groupId } }, body })),
        );
      },
    },
    'internal.admin.permissions.catalog': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/permissions/catalog'))),
    },
    'internal.admin.users.grants': {
      validate: hasUserId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.GET('/api/users/{userId}/grants', {
              params: { path: { userId: (params as { userId: number }).userId } },
            }),
          ),
        ),
    },
    'internal.admin.users.setGrants': {
      validate: (value) => hasUserId(value) && hasGrants(value),
      handle: (params) => {
        const { userId, grants } = params as { userId: number; grants: GrantEntry[] };
        return daemon(async () =>
          unwrap(
            await api.PUT('/api/users/{userId}/grants', {
              params: { path: { userId } },
              body: { grants },
            }),
          ),
        );
      },
    },
    'internal.admin.groups.grants': {
      validate: hasGroupId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.GET('/api/groups/{groupId}/grants', {
              params: { path: { groupId: (params as { groupId: number }).groupId } },
            }),
          ),
        ),
    },
    'internal.admin.groups.setGrants': {
      validate: (value) => hasGroupId(value) && hasGrants(value),
      handle: (params) => {
        const { groupId, grants } = params as { groupId: number; grants: GrantEntry[] };
        return daemon(async () =>
          unwrap(
            await api.PUT('/api/groups/{groupId}/grants', {
              params: { path: { groupId } },
              body: { grants },
            }),
          ),
        );
      },
    },
    'internal.admin.groups.delete': {
      validate: hasGroupId,
      handle: async (params) => {
        const { groupId } = params as { groupId: number };
        await daemon(async () => {
          unwrap(await api.DELETE('/api/groups/{groupId}', { params: { path: { groupId } } }));
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
