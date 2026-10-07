import { unwrap, type components, type ErrorCode } from '@pithyx/api-client';
import { useState, type Ref } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { coreAppIds } from '../apps/ids';
import { activateNotification, useHost } from '../apps/host/store';
import { useAppName } from '../apps/names';
import { findApp } from '../apps/registry';
import { showError } from '../errors/errorStore';
import { translateError } from '../errors/ErrorNotice';
import { applyEvent } from '../realtime/reducers';
import { useRealtime } from '../realtime/connection';
import { IconDeviceDesktop } from '../ui/icons';
import { openApp } from '../wm/store';
import { JobList } from './JobList';
import { NotificationPanel } from './NotificationPanel';
import { ToastStack, type JobItem, type NotificationItem } from './Notifications';
import { useMessages, type Message, type MessageParam } from './messages';
import { alertSection } from './StorageAlertViews';
import { alertMessage, setAlertMuted, type StorageAlert } from './storageAlerts';
import { dismissToast, useToasts, type DaemonNotification } from './toasts';
import type { TaskbarPosition } from './types';

// The panel and the toasts load after the desktop's first render, not with it (see Desktop.tsx).

type Job = components['schemas']['Job'];
type NotificationList = components['schemas']['NotificationList'];

const NO_NOTIFICATIONS: DaemonNotification[] = [];
const NO_JOBS: Job[] = [];

const TONES = {
  info: 'default',
  success: 'success',
  warning: 'warning',
  critical: 'danger',
} as const satisfies Record<DaemonNotification['tone'], NotificationItem['tone']>;

/** The parameters a message can use: strings and numbers. */
function messageParams(params: Record<string, unknown>): Record<string, MessageParam> {
  const result: Record<string, MessageParam> = {};
  for (const [name, value] of Object.entries(params)) {
    if (typeof value === 'string' || typeof value === 'number') {
      result[name] = value;
    }
  }
  return result;
}

/** The app a notification shows the icon of: the sender, or the core app its cause belongs to. */
export function sourceApp(
  notification: Pick<DaemonNotification, 'app' | 'code'> & { params: Record<string, unknown> },
): string | undefined {
  if (notification.app !== null) {
    return notification.app;
  }
  if (notification.code.startsWith('storage.')) {
    return coreAppIds.storage;
  }
  const kind = notification.params.kind;
  if (notification.code.startsWith('jobs.') && typeof kind === 'string') {
    return kind.startsWith('files.')
      ? coreAppIds.files
      : kind.startsWith('storage.')
        ? coreAppIds.storage
        : undefined;
  }
  return undefined;
}

/** A job's kind as a key of the jobs namespace: `files.empty_trash` is `kinds.files_empty_trash`. */
export function kindKey(kind: string): string {
  return `kinds.${kind.replaceAll('.', '_')}`;
}

/** Percent done and the message of a running job, by bytes when the job knows its size, otherwise by items. */
export function jobProgress(job: Job, kind: string): { message: Message; progress: number | null } {
  const { bytesDone, bytesTotal, itemsDone, itemsTotal } = job.progress;
  if (job.state === 'queued') {
    return { message: { code: 'running.queued', params: { kind } }, progress: null };
  }
  if (bytesTotal > 0) {
    return {
      message: {
        code: 'running.bytes',
        params: { kind, done: { bytes: bytesDone }, total: { bytes: bytesTotal } },
      },
      progress: Math.min(100, (bytesDone / bytesTotal) * 100),
    };
  }
  if (itemsTotal > 0) {
    return {
      message: { code: 'running.items', params: { kind, done: itemsDone, total: itemsTotal } },
      progress: Math.min(100, (itemsDone / itemsTotal) * 100),
    };
  }
  return { message: { code: 'running.working', params: { kind } }, progress: null };
}

function useKindLabel(): (kind: unknown) => string {
  const { t, i18n } = useTranslation('jobs');
  // Kinds are data from the daemon; unknown ones get a generic label.
  const translate = t as unknown as (key: string) => string;
  return (kind) => {
    const key = typeof kind === 'string' ? kindKey(kind) : '';
    return translate(key !== '' && i18n.exists(key, { ns: 'jobs' }) ? key : 'kinds.unknown');
  };
}

/** The buttons an app's notification carries in its params, or undefined when it has none there. */
function appActions(value: unknown): { id: string; label: string }[] | undefined {
  return Array.isArray(value)
    ? value.filter(
        (action): action is { id: string; label: string } =>
          typeof action === 'object' &&
          action !== null &&
          typeof (action as Record<string, unknown>).id === 'string' &&
          typeof (action as Record<string, unknown>).label === 'string',
      )
    : undefined;
}

/** Turns a daemon notification into a card: its app, its translated message and, for storage, a way there. */
function useNotificationView() {
  const { t, i18n } = useTranslation();
  const text = useMessages('notifications');
  const appName = useAppName();
  const kindLabel = useKindLabel();
  const remembered = useHost((state) => state.notifications);
  return (notification: DaemonNotification, now: Date, unread: boolean): NotificationItem => {
    // Parameters are JSON values of any shape; the generated type cannot say more.
    const raw: Record<string, unknown> = notification.params;
    const params = messageParams(raw);
    if (notification.code.startsWith('jobs.job.')) {
      params.kind = kindLabel(raw.kind);
      const errorCode = raw.errorCode;
      if (typeof errorCode === 'string') {
        const errorParams = raw.errorParams;
        params.error = translateError(t, {
          code: errorCode as ErrorCode,
          params:
            typeof errorParams === 'object' && errorParams !== null
              ? (errorParams as Record<string, unknown>)
              : {},
          requestId: '',
        });
      }
    }
    const alertCode = raw.alert;
    if (notification.code === 'storage.alert.resolved' && typeof alertCode === 'string') {
      params.problem = text({ code: alertCode, params }, 'title') ?? '';
    }
    const appId = sourceApp(notification);
    const app = appId === undefined ? undefined : findApp(appId);
    const alertKey = raw.alertKey;
    const id = String(notification.id);
    // An app's own notification opens the app; its buttons and data come from the daemon (A11), or from this tab for
    // a core app's notification, and go to the app as well.
    const sender = notification.code === 'app.message' ? notification.app : null;
    const buttons =
      sender === null ? undefined : (appActions(raw.actions) ?? remembered[id]?.actions);
    const data = 'data' in raw ? raw.data : undefined;
    return {
      id,
      app: {
        // An app the desktop does not know (any more) shows the default icon; the system its own.
        icon: app !== undefined ? app.icon : appId === undefined ? IconDeviceDesktop : null,
        tone: app?.tone ?? 'petrol',
        name:
          appId === undefined
            ? t('brand')
            : app !== undefined || i18n.exists(`apps.${appId}`)
              ? appName(appId)
              : appId,
      },
      message: { code: notification.code, params },
      minutesAgo: Math.max(
        0,
        Math.round((now.getTime() - new Date(notification.createdAt).getTime()) / 60_000),
      ),
      unread,
      tone: TONES[notification.tone],
      ...(typeof alertKey === 'string'
        ? {
            onAction: () => {
              openApp(coreAppIds.storage, { args: alertSection({ key: alertKey }) });
            },
          }
        : {}),
      ...(sender === null
        ? {}
        : {
            onAction: () => {
              activateNotification(id, sender, null, data);
            },
          }),
      ...(buttons === undefined || buttons.length === 0
        ? {}
        : {
            extraActions: buttons.map((button, index) => ({
              id: `app-${String(index)}`,
              label: button.label,
              onPress: () => {
                if (sender !== null) {
                  activateNotification(id, sender, button.id, data);
                }
              },
            })),
          }),
    };
  };
}

/** Removes notifications right away and on the daemon; puts them back if the daemon refuses. */
async function removeNotifications(
  ids: number[],
  call: () => Promise<{ error?: unknown; response: Response }>,
) {
  const update = (next: NotificationList) => {
    useRealtime.setState((state) => ({ data: { ...state.data, notifications: next } }));
  };
  const before = useRealtime.getState().data.notifications;
  const removed = new Set(ids);
  update(
    applyEvent('notifications', before, {
      change: 'deleted',
      ids,
      notification: null,
      readAt: null,
    }),
  );
  try {
    unwrap(await call());
  } catch (error) {
    const current = useRealtime.getState().data.notifications;
    if (current !== undefined && before !== undefined) {
      const present = new Set(current.items.map((item) => item.id));
      const back = before.items.filter((item) => removed.has(item.id) && !present.has(item.id));
      update({ items: [...back, ...current.items].sort((a, b) => b.id - a.id) });
    }
    showError(error);
  }
}

function dismissNotification(id: string): void {
  const numeric = Number(id);
  void removeNotifications([numeric], () =>
    api.DELETE('/api/notifications/{id}', { params: { path: { id: numeric } } }),
  );
}

function clearNotifications(): void {
  const ids = (useRealtime.getState().data.notifications?.items ?? []).map((item) => item.id);
  void removeNotifications(ids, () => api.DELETE('/api/notifications'));
}

async function cancelJob(id: number): Promise<void> {
  try {
    unwrap(await api.POST('/api/jobs/{id}/cancel', { params: { path: { id } } }));
  } catch (error) {
    showError(error);
  }
}

/** A toast goes away once the user pressed one of its buttons. */
function withDismiss(item: NotificationItem, dismiss: () => void): NotificationItem {
  const { onAction, extraActions } = item;
  return {
    ...item,
    ...(onAction === undefined
      ? {}
      : {
          onAction: () => {
            dismiss();
            onAction();
          },
        }),
    ...(extraActions === undefined
      ? {}
      : {
          extraActions: extraActions.map((extra) => ({
            ...extra,
            onPress: () => {
              dismiss();
              extra.onPress();
            },
          })),
        }),
  };
}

/** The toasts: desktop messages and new notifications of the daemon, which stay in the panel after they go. */
export function NotificationToasts({
  position,
  locale,
}: {
  position: TaskbarPosition;
  locale: string;
}) {
  const { t } = useTranslation();
  const toasts = useToasts((state) => state.items);
  const view = useNotificationView();
  const now = new Date();
  const items = toasts.map((toast): NotificationItem =>
    toast.notification === undefined
      ? {
          id: toast.id,
          app: { icon: IconDeviceDesktop, tone: 'petrol', name: t('brand') },
          message: toast.message,
          minutesAgo: 0,
        }
      : withDismiss(
          { ...view(toast.notification, now, false), id: toast.id, minutesAgo: 0 },
          () => {
            dismissToast(toast.id);
          },
        ),
  );
  return <ToastStack position={position} locale={locale} items={items} onDismiss={dismissToast} />;
}

/**
 * The panel behind the bell: the user's running jobs with progress and cancel, the storage alerts (administrators;
 * they stay until their cause is gone and can only be muted), then the notification history. Opening it marks the
 * history read; what was unread when it opened stays marked while it is open.
 */
export function NotificationCenter({
  alerts,
  now,
  locale,
  position,
  panelRef,
}: {
  alerts: StorageAlert[];
  now: Date;
  locale: string;
  position: TaskbarPosition;
  panelRef: Ref<HTMLElement>;
}) {
  const { t } = useTranslation();
  const history = useRealtime((state) => state.data.notifications?.items ?? NO_NOTIFICATIONS);
  const jobs = useRealtime((state) => state.data.jobs?.jobs ?? NO_JOBS);
  const [unreadAtOpen] = useState(
    () => new Set(history.filter((item) => item.readAt === null).map((item) => item.id)),
  );
  const view = useNotificationView();
  const kindLabel = useKindLabel();
  const storage = findApp(coreAppIds.storage);
  const alertItems = alerts.map((alert): NotificationItem => ({
    id: alert.key,
    app: {
      icon: storage?.icon ?? IconDeviceDesktop,
      tone: storage?.tone ?? 'graphite',
      name: t('notifications.storage'),
    },
    message: alertMessage(alert),
    minutesAgo: Math.max(0, Math.round((now.getTime() - new Date(alert.since).getTime()) / 60_000)),
    unread: !alert.muted,
    tone: alert.severity === 'critical' ? 'danger' : 'warning',
    persistent: true,
    onAction: () => {
      openApp(coreAppIds.storage, { args: alertSection(alert) });
    },
    extraActions: [
      {
        id: 'mute',
        label: alert.muted ? t('notifications.unmute') : t('notifications.mute'),
        onPress: () => {
          void setAlertMuted(alert.key, !alert.muted);
        },
      },
    ],
  }));
  const jobItems = jobs.map((job): JobItem => ({
    id: String(job.id),
    ...jobProgress(job, kindLabel(job.kind)),
    onCancel: () => {
      void cancelJob(job.id);
    },
  }));
  return (
    <NotificationPanel
      panelRef={panelRef}
      jobs={<JobList jobs={jobItems} />}
      items={[...alertItems, ...history.map((item) => view(item, now, unreadAtOpen.has(item.id)))]}
      locale={locale}
      position={position}
      onDismiss={dismissNotification}
      onClearAll={clearNotifications}
    />
  );
}
