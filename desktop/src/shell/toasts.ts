import { create } from 'zustand';
import type { Message } from './messages';

export interface Toast {
  id: string;
  message: Message;
}

/** Short-lived notifications of the desktop itself. Daemon notifications will arrive in the same store. */
export const useToasts = create<{ items: Toast[] }>(() => ({ items: [] }));

const LIFETIME = 8000;
let next = 0;

export function dismissToast(id: string): void {
  useToasts.setState((state) => ({ items: state.items.filter((item) => item.id !== id) }));
}

/** Shows a toast from the notifications namespace (title and body under the message code). */
export function notify(message: Message): void {
  const id = `t${String(++next)}`;
  useToasts.setState((state) => ({ items: [...state.items, { id, message }] }));
  setTimeout(() => {
    dismissToast(id);
  }, LIFETIME);
}
