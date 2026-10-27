import { unwrap } from '@pithyx/api-client';
import type { MethodName, PithyxApp } from '@pithyx/sdk';
import { type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { daemon } from './daemonError';
import type { AppDetails } from './store';

/** The Security page of the Settings app, outside the public SDK; the daemon checks the right on every request. */
interface SecurityMethods {
  /** The installed apps that hold a critical right, root on the box (D388). */
  'internal.admin.security.rootApps': { params: Record<string, never>; result: AppDetails[] };
  /** Revokes every critical right of the app, or answers null when it is gone; revoking needs no step-up. */
  'internal.admin.security.revokeRoot': { params: { appId: string }; result: AppDetails | null };
}

type SecurityName = keyof SecurityMethods;

export const rootAppsKey = ['settings', 'security', 'rootApps'] as const;

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const criticalRights = (app: AppDetails) =>
  app.elevated.filter((entry) => entry.tier === 'critical' && entry.granted);

/** The host side of Settings > Security. */
export function securityHost(): HostExtensions {
  return {
    'internal.admin.security.rootApps': {
      validate: isRecord,
      handle: () =>
        daemon(async () =>
          unwrap(await api.GET('/api/apps')).apps.filter((app) => criticalRights(app).length > 0),
        ),
    },
    'internal.admin.security.revokeRoot': {
      validate: (value) => isRecord(value) && typeof value.appId === 'string',
      handle: (params) =>
        daemon(async () => {
          const { appId } =
            params as SecurityMethods['internal.admin.security.revokeRoot']['params'];
          const app = unwrap(await api.GET('/api/apps')).apps.find((entry) => entry.id === appId);
          if (app === undefined) {
            return null;
          }
          return unwrap(
            await api.PUT('/api/apps/{appId}/elevated', {
              params: { path: { appId } },
              body: {
                rights: criticalRights(app).map((entry) => ({
                  right: entry.right,
                  granted: false,
                })),
              },
            }),
          );
        }),
    },
  };
}

export async function securityCall<M extends SecurityName>(
  app: PithyxApp,
  method: M,
  params: SecurityMethods[M]['params'],
): Promise<SecurityMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as SecurityMethods[M]['result'];
}
