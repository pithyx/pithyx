import { unwrap, type components } from '@pithyx/api-client';
import type { MethodName, PithyxApp } from '@pithyx/sdk';
import type { HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import type {
  NotificationChannel,
  NotificationChannelList,
  NotificationChannelRequest,
} from '../../notifications/ChannelEditor';
import { toPithyxError } from './daemonError';

export type SmtpStatus = components['schemas']['SmtpStatus'];
export type SmtpRequest = components['schemas']['SmtpRequest'];

/** The query keys of Settings > Notifications. */
export const smtpKey = ['settings', 'notifications', 'smtp'] as const;
export const systemChannelsKey = ['settings', 'notifications', 'channels'] as const;

/** The notification calls of Settings, outside the public SDK; the daemon checks the administrator right on each. */
interface NotificationMethods {
  'internal.admin.notifications.smtp': { params: Record<string, never>; result: SmtpStatus };
  'internal.admin.notifications.setSmtp': { params: SmtpRequest; result: SmtpStatus };
  'internal.admin.notifications.channels': {
    params: Record<string, never>;
    result: NotificationChannelList;
  };
  'internal.admin.notifications.createChannel': {
    params: { channel: NotificationChannelRequest };
    result: NotificationChannel;
  };
  'internal.admin.notifications.updateChannel': {
    params: { id: number; channel: NotificationChannelRequest };
    result: NotificationChannel;
  };
  'internal.admin.notifications.deleteChannel': { params: { id: number }; result: null };
  'internal.admin.notifications.testChannel': {
    params: { id: number };
    result: NotificationChannel;
  };
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const hasId = (value: unknown): value is { id: number } =>
  isRecord(value) && typeof value.id === 'number';

async function daemon<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    throw toPithyxError(error);
  }
}

/** The host side of Settings > Notifications. */
export function notificationsHost(): HostExtensions {
  const extensions: Record<keyof NotificationMethods, HostExtensions[string]> = {
    'internal.admin.notifications.smtp': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/system/notifications/smtp'))),
    },
    'internal.admin.notifications.setSmtp': {
      validate: (value) => isRecord(value) && typeof value.port === 'number',
      handle: (params) =>
        daemon(async () =>
          unwrap(await api.PUT('/api/system/notifications/smtp', { body: params as SmtpRequest })),
        ),
    },
    'internal.admin.notifications.channels': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/system/notifications/channels'))),
    },
    'internal.admin.notifications.createChannel': {
      validate: (value) => isRecord(value) && isRecord(value.channel),
      handle: (params) => {
        const { channel } = params as { channel: NotificationChannelRequest };
        return daemon(async () =>
          unwrap(await api.POST('/api/system/notifications/channels', { body: channel })),
        );
      },
    },
    'internal.admin.notifications.updateChannel': {
      validate: (value) => hasId(value) && isRecord((value as { channel?: unknown }).channel),
      handle: (params) => {
        const { id, channel } = params as { id: number; channel: NotificationChannelRequest };
        return daemon(async () =>
          unwrap(
            await api.PUT('/api/system/notifications/channels/{id}', {
              params: { path: { id } },
              body: channel,
            }),
          ),
        );
      },
    },
    'internal.admin.notifications.deleteChannel': {
      validate: hasId,
      handle: (params) => {
        const { id } = params as { id: number };
        return daemon(async () => {
          unwrap(
            await api.DELETE('/api/system/notifications/channels/{id}', {
              params: { path: { id } },
            }),
          );
          return null;
        });
      },
    },
    'internal.admin.notifications.testChannel': {
      validate: hasId,
      handle: (params) => {
        const { id } = params as { id: number };
        return daemon(async () =>
          unwrap(
            await api.POST('/api/system/notifications/channels/{id}/test', {
              params: { path: { id } },
            }),
          ),
        );
      },
    },
  };
  return extensions;
}

export async function notificationsCall<M extends keyof NotificationMethods>(
  app: PithyxApp,
  method: M,
  params: NotificationMethods[M]['params'],
): Promise<NotificationMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as NotificationMethods[M]['result'];
}
