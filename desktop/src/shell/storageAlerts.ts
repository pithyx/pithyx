import { unwrap, type components } from '@pithyx/api-client';
import { useEffect } from 'react';
import { api } from '../api/client';
import { showError } from '../errors/errorStore';
import { realtime, useRealtime } from '../realtime/connection';
import type { Message, MessageParam } from './messages';

export type StorageAlert = components['schemas']['StorageAlert'];

const NO_ALERTS: StorageAlert[] = [];

/** The storage alerts with the user's mute flags, live. Administrators only: nobody else subscribes. */
export function useStorageAlerts(isAdmin: boolean): StorageAlert[] {
  // Subscribing is synchronization with the WebSocket, not data fetching.
  useEffect(() => (isAdmin ? realtime.subscribe('storage/alerts') : undefined), [isAdmin]);
  return useRealtime((state) =>
    isAdmin ? (state.data['storage/alerts']?.alerts ?? NO_ALERTS) : NO_ALERTS,
  );
}

/** The alert as a translatable message of the notifications namespace. */
export function alertMessage(alert: StorageAlert): Message {
  const params: Record<string, MessageParam> = {};
  for (const [name, value] of Object.entries<unknown>(alert.params)) {
    if (typeof value === 'string' || typeof value === 'number') {
      params[name] = value;
    }
  }
  return { code: alert.code, params };
}

/** The alert the desktop banner shows: the oldest critical one the user has not muted. */
export function bannerAlert(alerts: StorageAlert[]): StorageAlert | undefined {
  return alerts
    .filter((alert) => alert.severity === 'critical' && !alert.muted)
    .sort((a, b) => a.since.localeCompare(b.since))[0];
}

/** Mutes or shows an alert again; the change arrives on the topic. */
export async function setAlertMuted(key: string, muted: boolean): Promise<void> {
  const options = { params: { path: { key } } };
  try {
    unwrap(
      muted
        ? await api.POST('/api/storage/alerts/{key}/mute', options)
        : await api.DELETE('/api/storage/alerts/{key}/mute', options),
    );
  } catch (error) {
    showError(error);
  }
}
