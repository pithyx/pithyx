import type { components } from '@pithyx/api-client';
import { api } from '../api/client';
import { realtime, useRealtime } from '../realtime/connection';
import { toastNotification, type DaemonNotification } from './toasts';

type NotificationEvent = components['schemas']['NotificationEvent'];
type NotificationList = components['schemas']['NotificationList'];

/**
 * The notification that a message of the `notifications` topic brings, if it is a new one. Snapshots (on connect and
 * after every reconnect) are the history the user has already seen and bring none, and a notification of a category
 * the user muted arrives already read (D390) and brings none either.
 */
export function createdNotification(
  data: NotificationEvent | NotificationList,
): DaemonNotification | undefined {
  return 'change' in data && data.change === 'created' && data.notification?.readAt == null
    ? (data.notification ?? undefined)
    : undefined;
}

/**
 * Follows the user's notifications and running jobs for the taskbar and the panel, and shows a toast for every new
 * notification. Returns the function that stops it.
 */
export function followNotifications(): () => void {
  const stopNotifications = realtime.listen('notifications', (data) => {
    const created = createdNotification(data as NotificationEvent | NotificationList);
    if (created !== undefined) {
      toastNotification(created);
    }
  });
  const stopJobs = realtime.subscribe('jobs');
  return () => {
    stopNotifications();
    stopJobs();
  };
}

/** How many notifications the user has not read. */
export function useUnreadNotifications(): number {
  return useRealtime(
    (state) => state.data.notifications?.items.filter((item) => item.readAt === null).length ?? 0,
  );
}

/** How many jobs of the user are queued or running. */
export function useRunningJobs(): number {
  return useRealtime((state) => state.data.jobs?.jobs.length ?? 0);
}

/** Marks every notification as read, when the user opens the panel; the change arrives on the topic. */
export async function markAllRead(): Promise<void> {
  // A failure only leaves the badge up until the next open; it is not worth an error dialog.
  await api.POST('/api/notifications/read', { body: {} }).catch(() => undefined);
}
