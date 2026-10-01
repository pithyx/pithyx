import type { components } from '@pithyx/api-client';
import { create } from 'zustand';
import type { Message } from './messages';

export type DaemonNotification = components['schemas']['Notification'];

/** A toast: a message of the desktop itself, or a notification from the daemon (which stays in the history). */
export interface Toast {
  id: string;
  message: Message;
  notification?: DaemonNotification;
}

/** Short-lived toasts. Dismissing one hides it only; a daemon notification stays in the panel. */
export const useToasts = create<{ items: Toast[] }>(() => ({ items: [] }));

const LIFETIME = 5000;
let next = 0;
// While the notification panel is open, daemon notifications show there and not as toasts.
let panelOpen = false;

export function dismissToast(id: string): void {
  useToasts.setState((state) => ({ items: state.items.filter((item) => item.id !== id) }));
}

function show(toast: Omit<Toast, 'id'>): void {
  const id = `t${String(++next)}`;
  useToasts.setState((state) => ({ items: [...state.items, { id, ...toast }] }));
  setTimeout(() => {
    dismissToast(id);
  }, LIFETIME);
}

/** Shows a toast from the notifications namespace (title and body under the message code). */
export function notify(message: Message): void {
  show({ message });
}

/** Shows a new notification of the daemon as a toast, unless the panel that lists it is open. */
export function toastNotification(notification: DaemonNotification): void {
  if (!panelOpen) {
    show({ message: { code: notification.code, params: {} }, notification });
  }
}

/** Opening the notification panel hides the toasts of the notifications it lists. */
export function setNotificationPanelOpen(open: boolean): void {
  panelOpen = open;
  if (open) {
    useToasts.setState((state) => ({
      items: state.items.filter((item) => item.notification === undefined),
    }));
  }
}

export function clearToasts(): void {
  useToasts.setState({ items: [] });
}
